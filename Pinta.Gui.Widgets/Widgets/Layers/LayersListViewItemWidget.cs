//
// HistoryTreeView.cs
//
// Copyright (c) 2010 Jonathan Pobst
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Cairo;
using GObject;
using Pinta.Core;

namespace Pinta.Gui.Widgets;

// GObject subclass for use with Gio.ListStore
[GObject.Subclass<GObject.Object>]
public sealed partial class LayersListViewItem
{
	// NRT - GObject requires a parameterless constructor, and these don't have simple defaults
	private Document? document;
	public UserLayer? UserLayer { get; private set; }
	public int Depth { get; private set; }

	public static LayersListViewItem New (Document doc, UserLayer userLayer, int depth)
	{
		LayersListViewItem item = NewWithProperties ([]);
		item.document = doc;
		item.UserLayer = userLayer;
		item.Depth = depth;
		return item;
	}

	public string Label => UserLayer?.Name ?? string.Empty;
	public bool Visible => !UserLayer?.Hidden ?? false;
	public bool CanExpand => UserLayer?.HasChildren ?? false;
	public bool Expanded => UserLayer?.Expanded ?? false;

	public ImageSurface BuildThumbnail (
		int widthRequest,
		int heightRequest)
	{
		if (document is null || UserLayer is null)
			throw new InvalidOperationException ($"{nameof (LayersListViewItem)} is not initialized");

		if (UserLayer is AnimationOutputLayer animationOutput)
			return animationOutput.CreateThumbnailSurface ()
				?? CairoExtensions.CreateImageSurface (Format.Argb32, widthRequest, heightRequest);
		if (UserLayer is GroupLayer)
			return CairoExtensions.CreateImageSurface (Format.Argb32, widthRequest, heightRequest);

		List<Layer> layers = UserLayer.GetLayersToPaint ().ToList ();
		// For the current layer, show the selection layer too (e.g. when moving the selection's contents).
		if (UserLayer == document.Layers.CurrentUserLayer && document.Layers.ShowSelectionLayer)
			layers.Add (document.Layers.SelectionLayer);

		return LayerActions.RenderThumbnail (layers, widthRequest, heightRequest);
	}

	public void HandleVisibilityToggled (bool visible)
	{
		if (document is null || UserLayer is null)
			throw new InvalidOperationException ($"{nameof (LayersListViewItem)} is not initialized");

		if (Visible == visible)
			return;

		Document doc = PintaCore.Workspace.ActiveDocument;

		LayerProperties initial = new (UserLayer.Name, visible, UserLayer.Opacity, UserLayer.BlendMode);
		LayerProperties updated = new (UserLayer.Name, !visible, UserLayer.Opacity, UserLayer.BlendMode);

		UpdateLayerPropertiesHistoryItem historyItem = new (
			visible ? Resources.StandardIcons.ViewReveal : Resources.StandardIcons.ViewConceal,
			visible ? Translations.GetString ("Show Layer") : Translations.GetString ("Hide Layer"),
			UserLayer,
			initial,
			updated);

		historyItem.Redo ();

		doc.History.PushNewItem (historyItem);
	}

	public void ToggleExpanded ()
	{
		if (document is null || UserLayer is null || !UserLayer.HasChildren)
			return;

		UserLayer.Expanded = !UserLayer.Expanded;
		document.Layers.NotifyLayerTreeChanged ();
	}

	public event EventHandler? LayerModified;

	/// <summary>
	/// Signal that the layer has been modified.
	/// In the future this should be replaced by GObject properties and bindings.
	/// </summary>
	public void NotifyLayerModified ()
	{
		LayerModified?.Invoke (this, EventArgs.Empty);
	}
}

[GObject.Subclass<Gtk.Box>]
public sealed partial class LayersListViewItemWidget
{
	private static readonly Pattern transparent_pattern = CairoExtensions.CreateTransparentBackgroundPattern (8);

	private LayersListViewItem? item;
	private ImageSurface? thumbnail_surface;

        private Gtk.Image disclosure_button;
	private Gtk.DrawingArea drop_preview;
        private Gtk.Box hierarchy_content;
        private Gtk.Image layer_icon;
	private Gtk.DrawingArea item_thumbnail;
	private Gtk.Label item_label;
	private Gtk.Button cutout_button;
	private Gtk.Button generate_video_button;
	private Gtk.Button import_video_button;
	private Gtk.Image visible_button;
	private LayerDropHint drop_hint = LayerDropHint.None;
	private int drop_preview_depth;

	public event EventHandler<LayerDragEventArgs>? LayerDragEnded;
	public event EventHandler<LayerDragEventArgs>? LayerDragUpdated;
	public event EventHandler? LayerDragCanceled;
	public event EventHandler<LayerSelectionEventArgs>? LayerSelectionRequested;
	public event EventHandler<LayerPreviewRequestedEventArgs>? LayerPreviewRequested;
	public event EventHandler? VideoEditorRequested;
	public int Depth => item?.Depth ?? 0;
	public UserLayer? UserLayer => item?.UserLayer;

	public static LayersListViewItemWidget New ()
		=> NewWithProperties ([]);

	[MemberNotNull (nameof (item_thumbnail))]
	[MemberNotNull (nameof (item_label))]
	[MemberNotNull (nameof (cutout_button))]
	[MemberNotNull (nameof (generate_video_button))]
	[MemberNotNull (nameof (import_video_button))]
	[MemberNotNull (nameof (layer_icon))]
	[MemberNotNull (nameof (visible_button))]
	[MemberNotNull (nameof (lock_button))]
	[MemberNotNull (nameof (disclosure_button))]
        [MemberNotNull (nameof (hierarchy_content))]
	[MemberNotNull (nameof (drop_preview))]
	partial void Initialize ()
	{
		Gtk.DrawingArea dropPreview = Gtk.DrawingArea.New ();
		dropPreview.Hexpand = true;
		dropPreview.Vexpand = true;
		dropPreview.CanTarget = false;
		dropPreview.SetDrawFunc ((area, context, width, height) => DrawDropPreview (context, width, height));

                Gtk.Image disclosureButton = Gtk.Image.New ();
                disclosureButton.WidthRequest = 16;
                disclosureButton.Valign = Gtk.Align.Center;
                AddActionButtonGesture (disclosureButton, () => item?.ToggleExpanded ());

		Gtk.DrawingArea itemThumbnail = Gtk.DrawingArea.New ();
		itemThumbnail.SetDrawFunc ((area, context, width, height) => DrawThumbnail (context, width, height));
                itemThumbnail.WidthRequest = 48;
                itemThumbnail.HeightRequest = 32;

                Gtk.Image layerIcon = Gtk.Image.New ();
                layerIcon.IconSize = Gtk.IconSize.Normal;
                layerIcon.Valign = Gtk.Align.Center;
                layerIcon.Visible = false;
                AddActionButtonGesture (layerIcon, () => item?.ToggleExpanded ());

		Gtk.Label itemLabel = Gtk.Label.New (string.Empty);
		itemLabel.Halign = Gtk.Align.Start;
		itemLabel.Hexpand = true;
		itemLabel.Ellipsize = Pango.EllipsizeMode.End;
		Gtk.Widget nameEditor = CreateNameEditor (itemLabel);

		Gtk.Button cutoutButton = Gtk.Button.NewWithLabel (Translations.GetString ("Cutout"));
		cutoutButton.Valign = Gtk.Align.Center;
		cutoutButton.TooltipText = Translations.GetString ("Choose an image API and operation");
		cutoutButton.OnClicked += (_, _) => {
			SelectCurrentLayer ();
			PintaCore.Actions.Layers.Cutout.Activate ();
		};

		Gtk.Button generateVideoButton = Gtk.Button.NewFromIconName (Resources.Icons.EffectsRenderClouds);
		generateVideoButton.TooltipText = Translations.GetString ("Generate Video");
		generateVideoButton.OnClicked += (_, _) => {
			SelectCurrentLayer ();
			PintaCore.Actions.Layers.GenerateVideo.Activate ();
		};

		Gtk.Button importVideoButton = Gtk.Button.NewFromIconName (Resources.Icons.LayerImport);
		importVideoButton.TooltipText = Translations.GetString ("Import Video");
		importVideoButton.OnClicked += (_, _) => {
			SelectCurrentLayer ();
			VideoEditorRequested?.Invoke (this, EventArgs.Empty);
		};

                Gtk.Image visibleButton = Gtk.Image.New ();
                visibleButton.WidthRequest = 16;
                visibleButton.Halign = Gtk.Align.Start;
                visibleButton.Valign = Gtk.Align.Center;
                AddActionButtonGesture (visibleButton, () => {
			if (item is not null)
				item.HandleVisibilityToggled (!item.Visible);
                });

		Gtk.Button lockButton = CreateLockButton ();

		Gtk.GestureClick menuGesture = Gtk.GestureClick.New ();
		menuGesture.SetButton (Gdk.Constants.BUTTON_SECONDARY);
		menuGesture.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		menuGesture.OnPressed += MenuGesture_OnPressed;

		// --- Initialization (Gtk.Widget)

                this.SetAllMargins (2);

		// --- Initialization (Gtk.Box)

		Gtk.Box itemRow = Gtk.Box.New (Gtk.Orientation.Horizontal, 3);
		itemRow.Hexpand = true;
		itemRow.Append (visibleButton);
		itemRow.Append (lockButton);
		AddSelectLayerGesture (itemRow);
		itemRow.AddController (menuGesture);

                Gtk.Box dragContent = Gtk.Box.New (Gtk.Orientation.Horizontal, 4);
		dragContent.Hexpand = true;
		dragContent.Append (itemThumbnail);
                dragContent.Append (layerIcon);
		dragContent.Append (nameEditor);
		AddLayerDragGesture (dragContent);

                Gtk.Box hierarchyContent = Gtk.Box.New (Gtk.Orientation.Horizontal, 3);
                hierarchyContent.Hexpand = true;
			hierarchyContent.Append (disclosureButton);
			hierarchyContent.Append (dragContent);
			hierarchyContent.Append (generateVideoButton);
			hierarchyContent.Append (importVideoButton);
			hierarchyContent.Append (cutoutButton);
                itemRow.Append (hierarchyContent);

		Gtk.Overlay rowOverlay = Gtk.Overlay.New ();
		rowOverlay.Child = itemRow;
		rowOverlay.AddOverlay (dropPreview);

		Spacing = 0;
		SetOrientation (Gtk.Orientation.Vertical);
		Append (rowOverlay);

		// --- References to keep

		disclosure_button = disclosureButton;
		drop_preview = dropPreview;
                hierarchy_content = hierarchyContent;
		layer_icon = layerIcon;
		item_thumbnail = itemThumbnail;
		item_label = itemLabel;
		cutout_button = cutoutButton;
		generate_video_button = generateVideoButton;
		import_video_button = importVideoButton;
		visible_button = visibleButton;
	}

	private void DrawDropPreview (
		Context context,
		int width,
		int height)
	{
		if (width <= 0 || height <= 0)
			return;

		context.Rectangle (0.5, 0.5, width - 1, height - 1);
		context.SetSourceColor (new Color (0.5, 0.5, 0.5, 0.35));
		context.LineWidth = 1;
		context.Stroke ();

		if (drop_hint == LayerDropHint.None)
			return;

		const double blue = 0.89;
		const double green = 0.52;
		const double red = 0.21;
		double indent = Math.Min (width - 10, 8 + drop_preview_depth * 16);
		context.Save ();

		if (drop_hint is LayerDropHint.Before or LayerDropHint.After) {
			double y = drop_hint == LayerDropHint.Before ? 2 : height - 2;
			context.SetSourceColor (new Color (red, green, blue));
			context.LineWidth = 3;
			context.MoveTo (indent, y);
			context.LineTo (width - 5, y);
			context.Stroke ();
			context.Arc (indent, y, 4, 0, Math.PI * 2);
			context.Fill ();
		} else {
			context.Rectangle (indent, 2, Math.Max (1, width - indent - 5), height - 4);
			context.SetSourceColor (new Color (red, green, blue, 0.16));
			context.FillPreserve ();
			context.SetSourceColor (new Color (red, green, blue, 0.8));
			context.LineWidth = 2;
			context.Stroke ();
		}

		context.Restore ();
	}

	private void AddLayerDragGesture (Gtk.Widget widget)
	{
		bool dragging = false;
		Gtk.GestureDrag dragGesture = Gtk.GestureDrag.New ();
		dragGesture.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		dragGesture.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		dragGesture.OnDragBegin += (_, _) => dragging = false;
		dragGesture.OnDragUpdate += (controller, args) => {
			if (IsRenaming || item?.Locked == true)
				return;

			if (!dragging) {
				dragging = true;
				SetOpacity (0.55);
				dragGesture.SetState (Gtk.EventSequenceState.Claimed);
			}

			HandleDragUpdate (widget, controller, args);
		};
		dragGesture.OnDragEnd += (controller, args) => {
			if (dragging && item?.Locked != true)
				HandleDragEnd (widget, controller, args);
			else if (dragging)
				SetOpacity (1.0f);
		};
		dragGesture.OnCancel += (_, _) => {
			if (dragging) {
				SetOpacity (1.0f);
				LayerDragCanceled?.Invoke (this, EventArgs.Empty);
			}
		};
		widget.AddController (dragGesture);
	}

	private void AddActionButtonGesture (
                Gtk.Widget widget,
                Action action,
		bool selectLayer = true)
        {
                Gtk.GestureClick click = Gtk.GestureClick.New ();
                click.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
                click.SetPropagationPhase (Gtk.PropagationPhase.Capture);
                click.OnPressed += (_, _) => {
                        if (selectLayer && item?.UserLayer is UserLayer layer && !layer.Locked && PintaCore.Workspace.HasOpenDocuments) {
                                Document doc = PintaCore.Workspace.ActiveDocument;
                                if (doc.Layers.CurrentUserLayer != layer)
                                        doc.Layers.SetCurrentUserLayer (layer);
                        }

                        action ();
                        click.SetState (Gtk.EventSequenceState.Claimed);
                };
                widget.AddController (click);
        }

	private void AddSelectLayerGesture (Gtk.Widget widget)
	{
		Gtk.GestureClick click = Gtk.GestureClick.New ();
		click.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		click.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		click.OnPressed += (_, _) => {
			if (item?.UserLayer is not UserLayer layer)
				return;
			if (layer.Locked)
				return;

			Gdk.ModifierType modifiers = click.GetCurrentEventState ();
			LayerSelectionRequested?.Invoke (this, new LayerSelectionEventArgs (layer, modifiers));
			if (modifiers.IsControlPressed () || modifiers.IsShiftPressed ())
				click.SetState (Gtk.EventSequenceState.Claimed);
		};
		widget.AddController (click);
	}

        private void SelectCurrentLayer ()
        {
                if (item?.UserLayer is not UserLayer layer || layer.Locked || !PintaCore.Workspace.HasOpenDocuments)
                        return;

                Document doc = PintaCore.Workspace.ActiveDocument;
                if (doc.Layers.CurrentUserLayer != layer)
                        doc.Layers.SetCurrentUserLayer (layer);
        }

	private void HandleDragUpdate (
		Gtk.Widget sourceWidget,
		Gtk.GestureDrag controller,
		Gtk.GestureDrag.DragUpdateSignalArgs args)
	{
		controller.GetStartPoint (out double startX, out double startY);
		PointD endPoint = new (startX + args.OffsetX, startY + args.OffsetY);
		if (!sourceWidget.TranslateCoordinates (this, endPoint, out PointD rowPoint))
			return;

		LayerDragUpdated?.Invoke (this, new LayerDragEventArgs (rowPoint));
	}

	private void HandleDragEnd (
		Gtk.Widget sourceWidget,
		Gtk.GestureDrag controller,
		Gtk.GestureDrag.DragEndSignalArgs args)
	{
		SetOpacity (1.0f);
		controller.GetStartPoint (out double startX, out double startY);
		PointD endPoint = new (startX + args.OffsetX, startY + args.OffsetY);
		if (!sourceWidget.TranslateCoordinates (this, endPoint, out PointD rowPoint))
			return;

		LayerDragEnded?.Invoke (this, new LayerDragEventArgs (rowPoint));
	}

	public void SetDropHint (LayerDropHint hint)
		=> SetDropHint (hint, 0);

	public void SetDropHint (LayerDropHint hint, int depth)
	{
		if (drop_hint == hint && drop_preview_depth == depth)
			return;

		drop_hint = hint;
		drop_preview_depth = depth;
		drop_preview.QueueDraw ();
	}

	private void MenuGesture_OnPressed (
		Gtk.GestureClick gesture,
		Gtk.GestureClick.PressedSignalArgs args)
	{
		gesture.SetState (Gtk.EventSequenceState.Claimed);
		if (item is null || item.UserLayer is null || !PintaCore.Workspace.HasOpenDocuments)
			return;
		if (item.UserLayer.Locked) {
			ShowLockedLayerMenu ();
			return;
		}

		if (gesture.GetCurrentEventState ().IsControlPressed ()) {
			LayerPreviewRequested?.Invoke (this, new LayerPreviewRequestedEventArgs (item.UserLayer));
			return;
		}

		Document doc = PintaCore.Workspace.ActiveDocument;
		// Ensure this is the current layer before opening the menu, since the menu actions
		// apply to the current layer.
		if (doc.Layers.CurrentUserLayer != item.UserLayer)
			doc.Layers.SetCurrentUserLayer (item.UserLayer);

		LayerActions actions = PintaCore.Actions.Layers;

		Gio.Menu operationsSection = Gio.Menu.New ();
                operationsSection.AppendItem (actions.AddNewGroup.CreateMenuItem ());
		if (item.UserLayer.GetType () == typeof (UserLayer))
			operationsSection.AppendItem (actions.ImportFromFile.CreateMenuItem ());
		operationsSection.AppendItem (actions.GenerateImage.CreateMenuItem ());
		operationsSection.AppendItem (actions.GenerateVideo.CreateMenuItem ());
		operationsSection.AppendItem (actions.Cutout.CreateMenuItem ());
		if (item.UserLayer is SingleDirectionAnimationLayer)
			operationsSection.AppendItem (actions.CreateSingleDirectionAnimation.CreateMenuItem ());
		else if (item.UserLayer is SpriteSheetLayer)
			operationsSection.AppendItem (actions.EditSpritesheet.CreateMenuItem ());
		else {
			operationsSection.AppendItem (actions.GenerateSpritesheet.CreateMenuItem ());
			operationsSection.AppendItem (actions.GenerateSingleDirectionAnimation.CreateMenuItem ());
			operationsSection.AppendItem (actions.CreateMultiDirectionAnimation.CreateMenuItem ());
			operationsSection.AppendItem (actions.ImageSplit.CreateMenuItem ());
			operationsSection.AppendItem (actions.AutoSplit.CreateMenuItem ());
			operationsSection.AppendItem (actions.CreateSingleDirectionAnimation.CreateMenuItem ());
		}
		operationsSection.AppendItem (actions.SaveLayerImage.CreateMenuItem ());
		operationsSection.AppendItem (actions.DeleteLayer.CreateMenuItem ());
		operationsSection.AppendItem (actions.DuplicateLayer.CreateMenuItem ());
		operationsSection.AppendItem (actions.MergeLayerDown.CreateMenuItem ());
		operationsSection.AppendItem (actions.MergeSelectedLayers.CreateMenuItem ());
		operationsSection.AppendItem (actions.MoveLayerUp.CreateMenuItem ());
		operationsSection.AppendItem (actions.MoveLayerDown.CreateMenuItem ());
		if (item.UserLayer is AnimationOutputLayer)
			operationsSection.AppendItem (actions.ResetLayerPosition.CreateMenuItem ());

		Gio.Menu flipSection = Gio.Menu.New ();
		flipSection.AppendItem (actions.FlipHorizontal.CreateMenuItem ());
		flipSection.AppendItem (actions.FlipVertical.CreateMenuItem ());
		flipSection.AppendItem (actions.ResizeLayer.CreateMenuItem ());
		flipSection.AppendItem (actions.RotateZoom.CreateMenuItem ());

		Gio.Menu propertiesSection = Gio.Menu.New ();
		propertiesSection.AppendItem (actions.UnlockReference.CreateMenuItem ());
		propertiesSection.AppendItem (actions.Properties.CreateMenuItem ());

		Gio.Menu menu = Gio.Menu.New ();
		menu.AppendSection (null, operationsSection);
		menu.AppendSection (null, flipSection);
		menu.AppendSection (null, propertiesSection);

		Gtk.PopoverMenu popover = Gtk.PopoverMenu.NewFromModel (menu);
		popover.SetParent (this);
		popover.Popup ();
	}

	/// <summary>
	/// Bind the widget to a different LayersListViewItem.
	/// </summary>
	public void SetItem (LayersListViewItem newItem)
	{
		if (item != null)
			item.LayerModified -= OnLayerModified;

		item = newItem;
		item.LayerModified += OnLayerModified;
		UpdateFromLayer ();
	}

	/// <summary>
	/// Event handler for modifications to the item's layer.
	/// </summary>
	private void OnLayerModified (object? sender, EventArgs e)
	{
		UpdateFromLayer ();
	}

	/// <summary>
	/// Update the widget to reflect the current state of the item's layer.
	/// </summary>
	private void UpdateFromLayer ()
	{
		if (item is null)
			throw new InvalidOperationException ($"{nameof (item)} is null");

		item_label.SetText (item.Label);
                item_label.MarginStart = 0;
                hierarchy_content.MarginStart = item.Depth * 16;
                disclosure_button.MarginStart = 0;
                disclosure_button.Visible = item.Depth > 0 || item.CanExpand;
                disclosure_button.Opacity = item.CanExpand ? 1 : 0;
                disclosure_button.Sensitive = item.CanExpand;
                disclosure_button.IconName = item.CanExpand ? (item.Expanded ? "pan-down-symbolic" : "pan-end-symbolic") : string.Empty;
			bool isGroup = item.UserLayer is GroupLayer && item.UserLayer is not AnimationOutputLayer;
			bool isReference = item.UserLayer?.IsReference == true;
			item_thumbnail.Visible = !isGroup;
			layer_icon.IconName = isGroup ? Resources.StandardIcons.Folder : isReference ? Resources.Icons.LayerLocked : string.Empty;
			layer_icon.TooltipText = isReference
				? item.UserLayer!.ReferenceMissing ? Translations.GetString ("Referenced image is missing") : Translations.GetString ("Referenced layer is locked")
				: null;
			layer_icon.Visible = isGroup || isReference;
			bool isVideoLayer = item.UserLayer is VideoEditingLayer;
			bool hasVideo = item.UserLayer is VideoEditingLayer videoLayer
				&& !string.IsNullOrWhiteSpace (videoLayer.VideoPath);
			generate_video_button.Visible = isVideoLayer && !hasVideo;
			generate_video_button.Sensitive = !item.Locked;
			import_video_button.Visible = isVideoLayer;
			import_video_button.Sensitive = !item.Locked;
			import_video_button.SetIconName (hasVideo ? "document-edit-symbolic" : Resources.Icons.LayerImport);
			import_video_button.TooltipText = hasVideo
				? Translations.GetString ("Edit Video")
				: Translations.GetString ("Import Video");
			cutout_button.Sensitive = item.UserLayer?.IsEditable == true;
                visible_button.IconName = item.Visible ? Resources.StandardIcons.ViewReveal : Resources.StandardIcons.ViewConceal;
		visible_button.TooltipText = item.Visible
			? Translations.GetString ("Hide Layer")
			: Translations.GetString ("Show Layer");
		UpdateLockButton ();

		thumbnail_surface?.Dispose ();
		thumbnail_surface = null;
		item_thumbnail.QueueDraw ();
	}

	internal void DrawThumbnail (
		Context g,
		int width,
		int height)
	{
		if (item is null)
			throw new InvalidOperationException ($"{nameof (item)} is null");

		thumbnail_surface ??= item.BuildThumbnail (width, height);

		double scale;
		int draw_width;
		int draw_height;

		// The image is more constrained by height than width
		if (width / (double) thumbnail_surface.Width >= height / (double) thumbnail_surface.Height) {
			scale = height / (double) (thumbnail_surface.Height);
			draw_width = thumbnail_surface.Width * height / thumbnail_surface.Height;
			draw_height = height;
		} else {
			scale = width / (double) (thumbnail_surface.Width);
			draw_width = width;
			draw_height = thumbnail_surface.Height * width / thumbnail_surface.Width;
		}

		PointI offset = new (
			X: (int) ((width - draw_width) / 2f),
			Y: (int) ((height - draw_height) / 2f)
		);

		g.Save ();

		g.Rectangle (offset.X, offset.Y, draw_width, draw_height);
		g.Clip ();

		g.SetSource (transparent_pattern);
		g.Paint ();

		g.Scale (scale, scale);
		g.SetSourceSurface (thumbnail_surface, (int) (offset.X / scale), (int) (offset.Y / scale));
		g.Paint ();

		g.Restore ();

		// TODO: scale this box correctly to match layer aspect ratio
		g.SetSourceColor (new Color (0.5, 0.5, 0.5));
		g.Rectangle (offset.X + 0.5, offset.Y + 0.5, draw_width, draw_height);
		g.LineWidth = 1;

		g.Stroke ();

		g.Dispose ();
	}

	public override void Dispose ()
	{
		thumbnail_surface?.Dispose ();
		thumbnail_surface = null;
		base.Dispose ();
	}
}

public enum LayerDropHint
{
	None,
	Before,
	Into,
	After
}

public sealed class LayerDragEventArgs : EventArgs
{
	public LayerDragEventArgs (PointD endPoint) => EndPoint = endPoint;

	public PointD EndPoint { get; }
}

public sealed class LayerSelectionEventArgs (UserLayer layer, Gdk.ModifierType modifiers) : EventArgs
{
	public UserLayer Layer { get; } = layer;
	public Gdk.ModifierType Modifiers { get; } = modifiers;
}

public sealed class LayerPreviewRequestedEventArgs (UserLayer layer) : EventArgs
{
	public UserLayer Layer { get; } = layer;
}
