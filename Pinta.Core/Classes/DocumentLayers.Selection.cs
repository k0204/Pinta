using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	private UserLayer? current_user_layer;
	private GroupLayer? no_selection_layer;
	private readonly List<UserLayer> selected_user_layers = [];

	/// <summary>
	/// Gets the selected user layer, or an internal non-editable layer when no layer is selected.
	/// </summary>
	public UserLayer CurrentUserLayer
		=> current_user_layer ?? (no_selection_layer ??= CreateNoSelectionLayer ());

	public bool HasSelectedLayer => current_user_layer is not null;
	public IReadOnlyList<UserLayer> SelectedUserLayers => selected_user_layers;

	public IReadOnlyList<UserLayer> GetSelectedLayerRoots ()
	{
		HashSet<UserLayer> selected = [.. selected_user_layers];
		return [.. selected_user_layers.Where (layer => !HasSelectedAncestor (layer, selected))];
	}

	public void SetCurrentUserLayer (UserLayer layer)
	{
		if (!ContainsLayer (layer))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (layer));
		if (layer.Locked)
			return;

		SetSelectedUserLayers ([layer]);
	}

	public void SetSelectedUserLayers (IEnumerable<UserLayer> layers, bool commitCurrentTool = true)
	{
		ArgumentNullException.ThrowIfNull (layers);

		List<UserLayer> selected = [.. layers];
		if (selected.Any (layer => !ContainsLayer (layer)))
			throw new ArgumentException ("One or more layers do not belong to this document.", nameof (layers));

		selected = [.. selected.Where (layer => !layer.Locked).Distinct ()];
		if (selected.Count == 0) {
			ClearCurrentUserLayer (commitCurrentTool);
			return;
		}

		UserLayer current = current_user_layer is not null && selected.Contains (current_user_layer)
			? current_user_layer
			: selected[0];
		bool selectionChanged = !selected_user_layers.SequenceEqual (selected);
		bool currentChanged = current_user_layer != current;
		if (!selectionChanged && !currentChanged)
			return;

		if (currentChanged) {
			if (commitCurrentTool)
				tools.CurrentTool?.DoCommit (document);
			document.ResetSelectionPaths ();
		}

		selected_user_layers.Clear ();
		selected_user_layers.AddRange (selected);
		current_user_layer = current;
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);
		document.Workspace.Invalidate ();
	}

	public void ClearCurrentUserLayer (bool commitCurrentTool = true)
	{
		if (current_user_layer is null && selected_user_layers.Count == 0)
			return;

		if (commitCurrentTool)
			tools.CurrentTool?.DoCommit (document);
		if (ShowSelectionLayer)
			DestroySelectionLayer ();
		document.ResetSelectionPaths ();
		selected_user_layers.Clear ();
		current_user_layer = null;
		SelectedLayerChanged?.Invoke (this, EventArgs.Empty);
		document.Workspace.Invalidate ();
	}

	private void RaiseLayerPropertyChangedEvent (object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == Layer.HiddenProperty
			&& sender is UserLayer { Hidden: true } hiddenLayer
			&& current_user_layer is not null
			&& ContainsLayer (hiddenLayer, current_user_layer))
			ClearCurrentUserLayer ();
		else if (e.PropertyName == UserLayer.LockedProperty
			&& sender is UserLayer { Locked: true } lockedLayer
			&& selected_user_layers.Contains (lockedLayer))
			SetSelectedUserLayers (selected_user_layers.Where (layer => layer != lockedLayer));

		LayerPropertyChanged?.Invoke (sender, e);
	}

	private GroupLayer CreateNoSelectionLayer ()
		=> new (CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1)) { Hidden = true };

	private static bool HasSelectedAncestor (UserLayer layer, HashSet<UserLayer> selected)
	{
		for (UserLayer? parent = layer.Parent; parent is not null; parent = parent.Parent)
			if (selected.Contains (parent))
				return true;

		return false;
	}
}
