using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public readonly record struct LayerPosition (UserLayer? Parent, int Index);

public sealed partial class DocumentLayers
{
	private readonly ToolManager tools;
	private readonly Document document;
	private readonly List<UserLayer> user_layers = [];

	private int layer_name_int = 2;

	// The layer for tools to use until their output is committed
	private Layer? tool_layer;

	// The layer used for selections
	private Layer? selection_layer;

	public DocumentLayers (
		ToolManager tools,
		Document document)
	{
		this.tools = tools;
		this.document = document;
	}

	public event EventHandler? LayerTreeChanged;
	public event EventHandler? SelectedLayerChanged;
	public event PropertyChangedEventHandler? LayerPropertyChanged;

	/// <summary>
	/// Gets the layer used for drawing and managing selections.
	/// </summary>
	public Layer SelectionLayer {
		get {
			if (selection_layer is null)
				CreateSelectionLayer ();

			return selection_layer;
		}
	}

	/// <summary>
	/// Gets or sets whether the Selection layer should be shown.
	/// </summary>
	public bool ShowSelectionLayer { get; set; }

	/// <summary>
	/// Gets a scratch layer for tools to temporarily use until their content
	/// is committed to the actual layer.
	/// </summary>
	public Layer ToolLayer {
		get {
			if (tool_layer is null || tool_layer.Surface.Width != document.ImageSize.Width || tool_layer.Surface.Height != document.ImageSize.Height) {
				tool_layer = CreateLayer ("Tool Layer");
				tool_layer.Hidden = true;
			}

			return tool_layer;
		}
	}

	/// <summary>
	/// Collection of root user layers.
	/// </summary>
	public IReadOnlyList<UserLayer> RootLayers => user_layers;

	/// <summary>
	/// All user layers, flattened bottom-to-top with descendants after their parent.
	/// </summary>
	public IReadOnlyList<UserLayer> AllLayers => GetAllUserLayers ().ToList ();
	public bool HasLockedReferences => GetAllUserLayers ().Any (layer => layer.IsReference);

	/// <summary>
	/// Creates a new layer and adds it to the Layer collection after the
	/// currently selected layer, making it the new selected layer.
	/// </summary>
	public UserLayer AddNewLayer (string name)
	{
		UserLayer layer =
			string.IsNullOrEmpty (name)
			? CreateLayer ()
			: CreateLayer (name);

		LayerPosition position =
			current_user_layer is null
			? new LayerPosition (null, user_layers.Count)
			: GetNextSiblingPosition (CurrentUserLayer);

		Insert (layer, position);
		current_user_layer = layer;

		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);

		return layer;
	}

	/// <summary>
	/// Disposes all user created and internal layers.
	/// </summary>
	internal void Close ()
	{
		foreach (UserLayer layer in GetAllUserLayers ())
			layer.PropertyChanged -= RaiseLayerPropertyChangedEvent;

		user_layers.Clear ();
		current_user_layer = null;

		tool_layer = null;
		selection_layer = null;
	}

	/// <summary>
	/// Returns the number of user layers.
	/// </summary>
	public int Count () => GetAllUserLayers ().Count ();

	/// <summary>
	/// Creates a new layer, but does not add it to the layer collection.
	/// </summary>
	public UserLayer CreateLayer (
		string? name = null,
		int? width = null,
		int? height = null)
	{
		// Translators: {0} is a unique id for new layers, e.g. "Layer 2".
		name ??= Translations.GetString ("Layer {0}", layer_name_int++);
		width ??= document.ImageSize.Width;
		height ??= document.ImageSize.Height;

		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, width.Value, height.Value);
		UserLayer layer = new (surface) { Name = name };

		return layer;
	}

	public UserLayer AddReferenceLayer (
		string name,
		string referencePath,
		PointD center)
	{
		UserLayer layer = AddNewLayer (name);
		layer.ReferencePath = referencePath;
		document.LoadReferencedLayer (layer);

		Matrix transform = CairoExtensions.CreateIdentityMatrix ();
		transform.Translate (center.X - layer.ReferenceSize.Width / 2.0, center.Y - layer.ReferenceSize.Height / 2.0);
		layer.Transform = transform;
		return layer;
	}

	public GroupLayer CreateGroupLayer (
                string? name = null,
                int? width = null,
                int? height = null)
        {
                // Translators: {0} is a unique id for new layers, e.g. "Layer 2".
                name ??= Translations.GetString ("Layer {0}", layer_name_int++);
                width ??= document.ImageSize.Width;
                height ??= document.ImageSize.Height;

                ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, width.Value, height.Value);
                GroupLayer layer = new (surface) { Name = name };

                return layer;
        }

	public SpriteSheetLayer CreateSpriteSheetLayer (string name, int canvasWidth, int canvasHeight)
		=> new (name, canvasWidth, canvasHeight);

	public SingleDirectionAnimationLayer CreateSingleDirectionAnimationLayer (
		string name,
		int canvasWidth,
		int canvasHeight,
		string directionId = SingleDirectionAnimationLayer.DefaultDirectionId)
		=> new (name, canvasWidth, canvasHeight, directionId);

        /// <summary>
        /// Creates a new group and adds it to the Layer collection after the
        /// currently selected layer, making it the new selected layer.
        /// </summary>
        public GroupLayer AddNewGroup (string name)
        {
                GroupLayer layer =
                        string.IsNullOrEmpty (name)
                        ? CreateGroupLayer ()
                        : CreateGroupLayer (name);

                LayerPosition position =
                        current_user_layer is null
                        ? new LayerPosition (null, user_layers.Count)
                        : GetNextSiblingPosition (CurrentUserLayer);

                Insert (layer, position);
                current_user_layer = layer;

                SelectedLayerChanged?.Invoke (this, EventArgs.Empty);

                return layer;
        }

	/// <summary>
	/// Creates a new SelectionLayer.
	/// </summary>
	[MemberNotNull (nameof (selection_layer))]
	public void CreateSelectionLayer ()
	{
		selection_layer = CreateLayer ();
	}

	/// <summary>
	/// Creates a new SelectionLayer with the specified dimensions.
	/// </summary>
	[MemberNotNull (nameof (selection_layer))]
	public void CreateSelectionLayer (int width, int height)
	{
		selection_layer = CreateLayer (null, width, height);
	}

	/// <summary>
	/// Deletes the current layer and removes it from the layer collection.
	/// </summary>
	public void DeleteCurrentLayer () => DeleteLayer (CurrentUserLayer);

	/// <summary>
	/// Deletes a user layer and its subtree.
	/// </summary>
	public void DeleteLayer (UserLayer layer)
	{
		if (!ContainsLayer (layer))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (layer));

		int index = AllLayers.ToList ().IndexOf (layer);
		bool removedCurrent = current_user_layer is not null && ContainsLayer (layer, current_user_layer);

		RemoveLayer (layer);

		if (removedCurrent)
			current_user_layer = Count () == 0 ? null : AllLayers[Math.Min (index, Count () - 1)];

		NotifyLayerTreeChanged ();
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);
		document.Workspace.Invalidate ();
	}

	/// <summary>
	/// Hide and reset the SelectionLayer.
	/// </summary>
	public void DestroySelectionLayer ()
	{
		ShowSelectionLayer = false;
		SelectionLayer.Clear ();
		SelectionLayer.Transform.InitIdentity ();
	}

	/// <summary>
	/// Duplicate the currently selected user layer, adding the new
	/// layer to the layer collection after the current layer.
	/// </summary>
	public UserLayer DuplicateCurrentLayer ()
	{
		UserLayer layer = DuplicateLayerTree (CurrentUserLayer);

		Insert (layer, GetNextSiblingPosition (CurrentUserLayer));
		current_user_layer = layer;

		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);

		return layer;
	}

	/// <summary>
	/// Flatten all user layers to a single layer.
	/// </summary>
	public void FlattenLayers ()
	{
		if (Count () < 2)
			throw new InvalidOperationException ("Cannot flatten image because there is only one layer.");
		if (AllLayers.Any (layer => layer is AnimationOutputLayer))
			throw new InvalidOperationException ("Cannot flatten an image containing an animation output layer.");

		// Find the "bottom" layer
		UserLayer bottom_layer = AllLayers[0];

		// Replace the bottom surface with the flattened image,
		// and dispose the old surface
		bottom_layer.Surface = GetFlattenedImage ();

		// Reset our layer pointer to the only remaining layer
		current_user_layer = bottom_layer;

		foreach (UserLayer layer in AllLayers.Skip (1).Reverse ())
			DeleteLayer (layer);

		document.Workspace.Invalidate ();
	}

	/// <summary>
	/// Returns all layers flattened to a new surface, optionally clipped by the selection.
	/// </summary>
	internal ImageSurface GetFlattenedImage (bool clip_to_selection = false)
	{
		// Create a new image surface
		ImageSurface surf = CairoExtensions.CreateImageSurface (Format.Argb32, document.ImageSize.Width, document.ImageSize.Height);

		using Context g = new (surf);

		if (clip_to_selection)
			document.Selection.Clip (g);

		// Blend each visible layer onto our surface
		foreach (var layer in GetLayersToPaint (includeToolLayer: false))
			layer.Draw (g);

		surf.MarkDirty ();
		return surf;
	}

	/// <summary>
	/// Returns all layers that are visible and need to be painted, optionally
	/// including tool and selection layers.
	/// </summary>
	public IEnumerable<Layer> GetLayersToPaint (bool includeToolLayer = true)
	{
		foreach (UserLayer userLayer in user_layers) {
			foreach (Layer layer in GetLayersToPaint (userLayer, includeToolLayer))
				yield return layer;
		}
	}

	/// <summary>
	public LayerPosition GetPosition (UserLayer layer)
	{
		if (!ContainsLayer (layer))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (layer));

		if (layer.Parent is not null)
			return new LayerPosition (layer.Parent, layer.Parent.ChildIndexOf (layer));

		return new LayerPosition (null, user_layers.IndexOf (layer));
	}

	public void NotifyLayerTreeChanged ()
	{
		LayerTreeChanged?.Invoke (this, EventArgs.Empty);
	}

	/// <summary>
	/// Adds the provided layer at the requested root index of the layer collection.
	/// </summary>
	public void Insert (UserLayer layer, int index)
	{
		Insert (layer, new LayerPosition (null, index));
	}

	public void Insert (
		UserLayer layer,
		LayerPosition position)
	{
		if (ContainsLayer (layer)) {
			MoveLayer (layer, position);
			return;
		}

		ValidatePosition (layer, position);
		layer.DetachFromParent ();
		user_layers.Remove (layer);

		if (position.Parent is null) {
			user_layers.Insert (position.Index, layer);
		} else {
			position.Parent.InsertChild (position.Index, layer);
		}

		RegisterLayerTree (layer);

		if (current_user_layer is null)
			current_user_layer = layer;

		NotifyLayerTreeChanged ();
		document.Workspace.Invalidate ();
	}

	/// <summary>
	/// Moves a layer and its descendants to the requested tree position.
	/// </summary>
	public void MoveLayer (
		UserLayer layer,
		LayerPosition position)
	{
		if (!ContainsLayer (layer))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (layer));

		ValidatePosition (layer, position);

		LayerPosition oldPosition = GetPosition (layer);
		if (oldPosition.Parent is null)
			user_layers.RemoveAt (oldPosition.Index);
		else
			layer.DetachFromParent ();

		List<UserLayer> destination = position.Parent?.MutableChildren ?? user_layers;
		int index = position.Index;
		if (oldPosition.Parent == position.Parent && oldPosition.Index < index)
			index--;

		if (index < 0 || index > destination.Count)
			throw new ArgumentOutOfRangeException (nameof (position));

		if (position.Parent is null) {
			user_layers.Insert (index, layer);
		} else {
			position.Parent.InsertChild (index, layer);
		}

		NotifyLayerTreeChanged ();
		document.Workspace.Invalidate ();
	}

	/// <summary>
	/// Merges the current layer with the sibling below it.
	/// </summary>
	public void MergeCurrentLayerDown ()
	{
		UserLayer source = CurrentUserLayer;
		List<UserLayer> siblings = GetSiblingList (source);
		int siblingIndex = siblings.IndexOf (source);

		if (siblingIndex == 0)
			throw new InvalidOperationException ("Cannot flatten layer because current layer is the bottom layer.");

		MergeLayers ([siblings[siblingIndex - 1], source]);
	}

	/// <summary>
	/// Merges sibling layers into the lowest selected layer.
	/// </summary>
	public UserLayer MergeLayers (IReadOnlyCollection<UserLayer> layers)
	{
		if (layers.Count < 2)
			throw new ArgumentException ("At least two layers are required.", nameof (layers));

		UserLayer first = layers.First ();
		if (layers.Any (layer => !ContainsLayer (layer) || layer.Parent != first.Parent))
			throw new ArgumentException ("All layers must belong to the same parent.", nameof (layers));

		HashSet<UserLayer> selected = [.. layers];
		List<UserLayer> ordered = [.. GetSiblingList (first).Where (selected.Contains)];
		if (ordered.Count != layers.Count)
			throw new ArgumentException ("Layers must be unique.", nameof (layers));

		UserLayer dest = ordered[0];

		using Context g = new (dest.Surface);
		foreach (UserLayer child in dest.Children)
			foreach (Layer layer in child.GetLayersToPaint ())
				layer.Draw (g);

		foreach (UserLayer source in ordered.Skip (1))
			foreach (Layer layer in source.GetLayersToPaint ())
				layer.Draw (g);

		foreach (UserLayer child in dest.Children.Reverse ().ToList ())
			DeleteLayer (child);

		foreach (UserLayer source in ordered.Skip (1).Reverse ())
			DeleteLayer (source);

		SetCurrentUserLayer (dest);
		return dest;
	}

	/// <summary>
	/// Moves the current layer down 1 position among its siblings.
	/// </summary>
	public void MoveCurrentLayerDown ()
	{
		if (!CanMoveCurrentLayerDown ())
			throw new InvalidOperationException ("Cannot move layer down because current layer is the bottom layer.");

		UserLayer layer = CurrentUserLayer;
		List<UserLayer> siblings = GetSiblingList (layer);
		int siblingIndex = siblings.IndexOf (layer);
		SwapLayers (layer, siblings[siblingIndex - 1]);
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);

		document.Workspace.Invalidate ();
	}

	/// <summary>
	/// Moves the current layer up 1 position among its siblings.
	/// </summary>
	public void MoveCurrentLayerUp ()
	{
		if (!CanMoveCurrentLayerUp ())
			throw new InvalidOperationException ("Cannot move layer up because current layer is the top layer.");

		UserLayer layer = CurrentUserLayer;
		List<UserLayer> siblings = GetSiblingList (layer);
		int siblingIndex = siblings.IndexOf (layer);
		SwapLayers (layer, siblings[siblingIndex + 1]);
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);

		document.Workspace.Invalidate ();
	}

	public bool CanMoveCurrentLayerDown ()
	{
		if (current_user_layer is null)
			return false;

		UserLayer layer = CurrentUserLayer;
		return GetSiblingList (layer).IndexOf (layer) > 0;
	}

	public bool CanMoveCurrentLayerUp ()
	{
		if (current_user_layer is null)
			return false;

		UserLayer layer = CurrentUserLayer;
		List<UserLayer> siblings = GetSiblingList (layer);
		return GetSiblingList (layer).IndexOf (layer) < siblings.Count - 1;
	}

	public UserLayer GetSiblingBelow (UserLayer layer)
	{
		List<UserLayer> siblings = GetSiblingList (layer);
		int index = siblings.IndexOf (layer);

		if (index <= 0)
			throw new InvalidOperationException ("Layer has no sibling below it.");

		return siblings[index - 1];
	}

	public UserLayer GetSiblingAbove (UserLayer layer)
	{
		List<UserLayer> siblings = GetSiblingList (layer);
		int index = siblings.IndexOf (layer);

		if (index < 0 || index >= siblings.Count - 1)
			throw new InvalidOperationException ("Layer has no sibling above it.");

		return siblings[index + 1];
	}

	public void SwapLayers (
		UserLayer layer1,
		UserLayer layer2)
	{
		if (layer1.Parent != layer2.Parent)
			throw new InvalidOperationException ("Cannot swap layers with different parents.");

		List<UserLayer> siblings = GetSiblingList (layer1);
		int siblingIndex1 = siblings.IndexOf (layer1);
		int siblingIndex2 = siblings.IndexOf (layer2);

		(siblings[siblingIndex1], siblings[siblingIndex2]) = (siblings[siblingIndex2], siblings[siblingIndex1]);

		NotifyLayerTreeChanged ();
		document.Workspace.Invalidate ();
	}

	private bool ContainsLayer (UserLayer layer) => GetAllUserLayers ().Contains (layer);

	private static bool ContainsLayer (
		UserLayer parent,
		UserLayer layer)
	{
		return parent.GetSelfAndDescendants ().Contains (layer);
	}

	private UserLayer DuplicateLayerTree (UserLayer source)
	{
		// Translators: this is the auto-generated name for a duplicated layer.
		// {0} is the name of the source layer. Example: "Layer 3 copy".
		UserLayer layer = source switch {
			SpriteSheetLayer sprite => DuplicateSpriteSheetLayer (sprite),
			SingleDirectionAnimationLayer single => DuplicateSingleDirectionAnimationLayer (single),
			VideoEditingLayer => CreateVideoEditingLayer (Translations.GetString ("{0} copy", source.Name)),
			GroupLayer => CreateGroupLayer (Translations.GetString ("{0} copy", source.Name)),
			_ => CreateLayer (Translations.GetString ("{0} copy", source.Name)),
		};
		if (source is AnimationOutputLayer)
			return layer;

		if (!source.IsReference) {
			using Context g = new (layer.Surface);
			g.SetSourceSurface (source.Surface, 0, 0);
			g.Paint ();
		} else {
			layer.ReferencePath = source.ReferencePath;
			layer.ReferenceMissing = source.ReferenceMissing;
			layer.ReferenceSize = source.ReferenceSize;
			using Context g = new (layer.Surface);
			g.SetSourceSurface (source.Surface, 0, 0);
			g.Paint ();
		}

		layer.Hidden = source.Hidden;
		layer.Opacity = source.Opacity;
		layer.BlendMode = source.BlendMode;
		layer.Transform = source.Transform.Clone ();
		foreach ((string key, string value) in source.Metadata)
			layer.Metadata.Add (key, value);
		layer.SpritesheetSplit = source.SpritesheetSplit;

		foreach (UserLayer child in source.Children)
			layer.InsertChild (layer.Children.Count, DuplicateLayerTree (child));

		return layer;
	}

	private SpriteSheetLayer DuplicateSpriteSheetLayer (SpriteSheetLayer source)
	{
		SpriteSheetLayer copy = CreateSpriteSheetLayer (Translations.GetString ("{0} copy", source.Name), source.CanvasWidth, source.CanvasHeight);
		copy.Hidden = source.Hidden;
		copy.Opacity = source.Opacity;
		copy.BlendMode = source.BlendMode;
		copy.Expanded = source.Expanded;
		foreach ((string key, string value) in source.Metadata)
			copy.Metadata[key] = value;
		copy.SpritesheetSplit = source.SpritesheetSplit;
		copy.ReplaceSnapshot (source.CaptureSnapshot (), document.ImageSize);
		return copy;
	}

	public VideoEditingLayer CreateVideoEditingLayer (string? name = null)
	{
		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, document.ImageSize.Width, document.ImageSize.Height);
		return new VideoEditingLayer (surface, name ?? Translations.GetString ("Video Editing"));
	}

	public VideoEditingLayer? FindVideoEditingLayer ()
		=> RootLayers.OfType<VideoEditingLayer> ().FirstOrDefault ();

	public VideoEditingLayer GetOrCreateVideoEditingLayer ()
		=> FindVideoEditingLayer () ?? AddVideoEditingLayer ();

	public VideoEditingLayer AddVideoEditingLayer ()
	{
		VideoEditingLayer layer = CreateVideoEditingLayer ();
		Insert (layer, new LayerPosition (null, user_layers.Count));
		SetCurrentUserLayer (layer);
		return layer;
	}

	private SingleDirectionAnimationLayer DuplicateSingleDirectionAnimationLayer (SingleDirectionAnimationLayer source)
	{
		SingleDirectionAnimationLayer copy = CreateSingleDirectionAnimationLayer (
			Translations.GetString ("{0} copy", source.Name),
			source.CanvasWidth,
			source.CanvasHeight,
			source.DirectionId);
		copy.Hidden = source.Hidden;
		copy.Opacity = source.Opacity;
		copy.BlendMode = source.BlendMode;
		copy.Expanded = source.Expanded;
		foreach ((string key, string value) in source.Metadata)
			copy.Metadata[key] = value;
		copy.SpritesheetSplit = source.SpritesheetSplit;
		copy.ReplaceSnapshot (source.CaptureSnapshot (), document.ImageSize);
		return copy;
	}

	private IEnumerable<UserLayer> GetAllUserLayers ()
	{
		foreach (UserLayer layer in user_layers)
			foreach (UserLayer descendant in layer.GetSelfAndDescendants ())
				yield return descendant;
	}

	private IEnumerable<Layer> GetLayersToPaint (
		UserLayer userLayer,
		bool includeToolLayer)
	{
		if (userLayer.Hidden)
			yield break;

		foreach (Layer layer in userLayer.GetOwnLayersToPaint ())
			yield return layer;

		if (userLayer == CurrentUserLayer) {
			if (includeToolLayer && tool_layer is not null && !ToolLayer.Hidden)
				yield return ToolLayer;

			if (ShowSelectionLayer && (!SelectionLayer.Hidden))
				yield return SelectionLayer;
		}

		foreach (UserLayer child in userLayer.Children)
			foreach (Layer layer in GetLayersToPaint (child, includeToolLayer))
				yield return layer;
	}

	private LayerPosition GetNextSiblingPosition (UserLayer layer)
	{
		LayerPosition position = GetPosition (layer);
		return position with { Index = position.Index + 1 };
	}

	private List<UserLayer> GetSiblingList (UserLayer layer)
		=> layer.Parent?.MutableChildren ?? user_layers;

	private void ValidatePosition (UserLayer layer, LayerPosition position)
	{
		if (position.Parent is not null && !ContainsLayer (position.Parent))
			throw new ArgumentException ("Parent layer does not belong to this document.", nameof (position));

		if (position.Parent is not null && layer.GetSelfAndDescendants ().Contains (position.Parent))
			throw new InvalidOperationException ("Cannot move a layer into itself or one of its descendants.");

		int count = position.Parent?.Children.Count ?? user_layers.Count;
		if (position.Index < 0 || position.Index > count)
			throw new ArgumentOutOfRangeException (nameof (position));
	}

	private void RegisterLayerTree (UserLayer layer)
	{
		foreach (UserLayer descendant in layer.GetSelfAndDescendants ())
			descendant.PropertyChanged += RaiseLayerPropertyChangedEvent;
	}

	private void RemoveLayer (UserLayer layer)
	{
		if (layer.Parent is null)
			user_layers.Remove (layer);
		else
			layer.DetachFromParent ();

		foreach (UserLayer descendant in layer.GetSelfAndDescendants ())
			descendant.PropertyChanged -= RaiseLayerPropertyChangedEvent;
	}

}
