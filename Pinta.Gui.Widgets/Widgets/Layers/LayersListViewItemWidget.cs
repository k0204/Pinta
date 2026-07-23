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
	private CanvasRenderer? canvas_renderer;

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

		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, widthRequest, heightRequest);

		List<Layer> layers = UserLayer.GetLayersToPaint ().ToList ();
		// For the current layer, show the selection layer too (e.g. when moving the selection's contents).
		if (UserLayer == document.Layers.CurrentUserLayer && document.Layers.ShowSelectionLayer)
			layers.Add (document.Layers.SelectionLayer);

		// Directly use the layer's surface if there isn't any blending required.
		if (layers.Count == 1)
			return layers[0].Surface;

		canvas_renderer ??= new CanvasRenderer (
			PintaCore.LivePreview,
			PintaCore.Workspace,
			enableLivePreview: false,
			enableBackgroundPattern: true);
		canvas_renderer.Initialize (document.ImageSize, new Size (widthRequest, heightRequest));
		canvas_renderer.Render (layers, surface, PointI.Zero);

		return surface;
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

	private Gtk.Button disclosure_button;
	private Gtk.DrawingArea drop_after_indicator;
	private Gtk.DrawingArea drop_before_indicator;
	private Gtk.DrawingArea drop_into_indicator;
	private Gtk.DrawingArea item_thumbnail;
	private Gtk.Label item_label;
	private Gtk.CheckButton visible_button;
	private LayerDropHint drop_hint = LayerDropHint.None;

	public event EventHandler<LayerDragEventArgs>? LayerDragEnded;
	public event EventHandler<LayerDragEventArgs>? LayerDragUpdated;
	public event EventHandler? LayerDragCanceled;
	public int Depth => item?.Depth ?? 0;
	public UserLayer? UserLayer => item?.UserLayer;

	public static LayersListViewItemWidget New ()
		=> NewWithProperties ([]);

	[MemberNotNull (nameof (item_thumbnail))]
	[MemberNotNull (nameof (item_label))]
	[MemberNotNull (nameof (visible_button))]
	[MemberNotNull (nameof (disclosure_button))]
	[MemberNotNull (nameof (drop_after_indicator))]
	[MemberNotNull (nameof (drop_before_indicator))]
	[MemberNotNull (nameof (drop_into_indicator))]
	partial void Initialize ()
	{
		Gtk.DrawingArea dropBeforeIndicator = CreateDropIndicator ();
		Gtk.DrawingArea dropAfterIndicator = CreateDropIndicator ();
		Gtk.DrawingArea dropIntoIndicator = Gtk.DrawingArea.New ();
		dropIntoIndicator.WidthRequest = 4;
		dropIntoIndicator.SetDrawFunc ((area, context, width, height) => DrawDropIndicator (context, width, height));
		dropIntoIndicator.Visible = false;

		Gtk.Button disclosureButton = Gtk.Button.New ();
		disclosureButton.HasFrame = false;
		disclosureButton.CanFocus = false;
		disclosureButton.WidthRequest = 20;
		disclosureButton.OnClicked += (_, _) => item?.ToggleExpanded ();

		Gtk.DrawingArea itemThumbnail = Gtk.DrawingArea.New ();
		itemThumbnail.SetDrawFunc ((area, context, width, height) => DrawThumbnail (context, width, height));
		itemThumbnail.WidthRequest = 60;
		itemThumbnail.HeightRequest = 40;

		Gtk.Label itemLabel = Gtk.Label.New (string.Empty);
		itemLabel.Halign = Gtk.Align.Start;
		itemLabel.Hexpand = true;
		itemLabel.Ellipsize = Pango.EllipsizeMode.End;

		Gtk.CheckButton visibleButton = Gtk.CheckButton.New ();
		visibleButton.Halign = Gtk.Align.End;
		visibleButton.Hexpand = false;
		visibleButton.OnToggled += (_, _) => item?.HandleVisibilityToggled (visibleButton.Active);

		Gtk.GestureClick menuGesture = Gtk.GestureClick.New ();
		menuGesture.SetButton (Gdk.Constants.BUTTON_SECONDARY);
		menuGesture.OnPressed += MenuGesture_OnPressed;

		// --- Initialization (Gtk.Widget)

		this.SetAllMargins (2);
		this.AddController (menuGesture);
		AddLayerDragGesture (itemLabel);
		AddLayerDragGesture (itemThumbnail);

		// --- Initialization (Gtk.Box)

		Gtk.Box itemRow = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		itemRow.Append (dropIntoIndicator);
		itemRow.Append (disclosureButton);
		itemRow.Append (visibleButton);
		itemRow.Append (itemLabel);
		itemRow.Append (itemThumbnail);

		Spacing = 0;
		SetOrientation (Gtk.Orientation.Vertical);
		Append (dropBeforeIndicator);
		Append (itemRow);
		Append (dropAfterIndicator);

		// --- References to keep

		disclosure_button = disclosureButton;
		drop_after_indicator = dropAfterIndicator;
		drop_before_indicator = dropBeforeIndicator;
		drop_into_indicator = dropIntoIndicator;
		item_thumbnail = itemThumbnail;
		item_label = itemLabel;
		visible_button = visibleButton;
	}

	private static Gtk.DrawingArea CreateDropIndicator ()
	{
		Gtk.DrawingArea indicator = Gtk.DrawingArea.New ();
		indicator.HeightRequest = 2;
		indicator.SetDrawFunc ((area, context, width, height) => DrawDropIndicator (context, width, height));
		indicator.Visible = false;
		return indicator;
	}

	private static void DrawDropIndicator (
		Context context,
		int width,
		int height)
	{
		context.Rectangle (0, 0, width, height);
		context.SetSourceColor (new Color (0.21, 0.52, 0.89));
		context.Fill ();
	}

	private void AddLayerDragGesture (Gtk.Widget widget)
	{
		Gtk.GestureDrag dragGesture = Gtk.GestureDrag.New ();
		dragGesture.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		dragGesture.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		dragGesture.OnDragBegin += (_, _) => dragGesture.SetState (Gtk.EventSequenceState.Claimed);
		dragGesture.OnDragUpdate += (controller, args) => HandleDragUpdate (widget, controller, args);
		dragGesture.OnDragEnd += (controller, args) => HandleDragEnd (widget, controller, args);
		dragGesture.OnCancel += (_, _) => LayerDragCanceled?.Invoke (this, EventArgs.Empty);
		widget.AddController (dragGesture);
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
		controller.GetStartPoint (out double startX, out double startY);
		PointD endPoint = new (startX + args.OffsetX, startY + args.OffsetY);
		if (!sourceWidget.TranslateCoordinates (this, endPoint, out PointD rowPoint))
			return;

		LayerDragEnded?.Invoke (this, new LayerDragEventArgs (rowPoint));
	}

	public void SetDropHint (LayerDropHint hint)
	{
		if (drop_hint == hint)
			return;

		drop_hint = hint;
		drop_before_indicator.Visible = hint == LayerDropHint.Before;
		drop_after_indicator.Visible = hint == LayerDropHint.After;
		drop_into_indicator.Visible = hint == LayerDropHint.Into;
	}

	private void MenuGesture_OnPressed (
		Gtk.GestureClick _,
		Gtk.GestureClick.PressedSignalArgs args)
	{
		if (item is null || item.UserLayer is null || !PintaCore.Workspace.HasOpenDocuments)
			return;

		Document doc = PintaCore.Workspace.ActiveDocument;
		// Ensure this is the current layer before opening the menu, since the menu actions
		// apply to the current layer.
		if (doc.Layers.CurrentUserLayer != item.UserLayer)
			doc.Layers.SetCurrentUserLayer (item.UserLayer);

		LayerActions actions = PintaCore.Actions.Layers;

		Gio.Menu operationsSection = Gio.Menu.New ();
		operationsSection.AppendItem (actions.AddChildLayer.CreateMenuItem ());
		operationsSection.AppendItem (actions.DeleteLayer.CreateMenuItem ());
		operationsSection.AppendItem (actions.DuplicateLayer.CreateMenuItem ());
		operationsSection.AppendItem (actions.MergeLayerDown.CreateMenuItem ());
		operationsSection.AppendItem (actions.MoveLayerUp.CreateMenuItem ());
		operationsSection.AppendItem (actions.MoveLayerDown.CreateMenuItem ());

		Gio.Menu flipSection = Gio.Menu.New ();
		flipSection.AppendItem (actions.FlipHorizontal.CreateMenuItem ());
		flipSection.AppendItem (actions.FlipVertical.CreateMenuItem ());
		flipSection.AppendItem (actions.RotateZoom.CreateMenuItem ());

		Gio.Menu propertiesSection = Gio.Menu.New ();
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
		disclosure_button.MarginStart = item.Depth * 16;
		disclosure_button.Visible = true;
		disclosure_button.Sensitive = item.CanExpand;
		disclosure_button.SetIconName (item.CanExpand ? (item.Expanded ? "pan-down-symbolic" : "pan-end-symbolic") : string.Empty);
		visible_button.SetActive (item.Visible);

		thumbnail_surface = null;
		item_thumbnail.QueueDraw ();
	}

	private void DrawThumbnail (
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
