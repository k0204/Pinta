//
// LayerPreviewWindow.cs
//

using System;
using System.ComponentModel;
using System.Linq;
using Cairo;
using Pinta.Core;

namespace Pinta.Gui.Widgets;

[GObject.Subclass<Gtk.Window>]
public sealed partial class LayerPreviewWindow
{
	private static readonly Pattern transparent_pattern = CairoExtensions.CreateTransparentBackgroundPattern (16);
	private const double MIN_SCALE = 0.05;
	private const double MAX_SCALE = 32.0;
	private static readonly Gdk.Cursor grab_cursor = Gdk.Cursor.NewFromName (Resources.StandardCursors.Grab, null)!;
	private static readonly Gdk.Cursor grabbing_cursor = Gdk.Cursor.NewFromName (Resources.StandardCursors.Grabbing, null)!;

	private Document document = null!;
	private UserLayer layer = null!;
	private Gtk.DrawingArea preview_area = null!;
	private Gtk.ScrolledWindow scrolled_window = null!;
	private Gtk.Label info_label = null!;
	private Cairo.ImageSurface? preview_surface;
	private double scale = 1.0;
	private bool closing;
	private double pan_start_h;
	private double pan_start_v;

	public Document Document => document;

	public event EventHandler? Closed;

	public static LayerPreviewWindow New (
		Gtk.Window parent,
		Document document,
		UserLayer layer)
	{
		LayerPreviewWindow window = NewWithProperties ([]);
		window.Configure (parent, document, layer);
		return window;
	}

	[System.Diagnostics.CodeAnalysis.MemberNotNull (nameof (preview_area))]
	[System.Diagnostics.CodeAnalysis.MemberNotNull (nameof (scrolled_window))]
	[System.Diagnostics.CodeAnalysis.MemberNotNull (nameof (info_label))]
	partial void Initialize ()
	{
		Gtk.DrawingArea previewArea = Gtk.DrawingArea.New ();
		previewArea.Focusable = true;
		previewArea.Cursor = grab_cursor;
		previewArea.SetDrawFunc ((_, context, width, height) => DrawPreview (context, width, height));

		Gtk.EventControllerScroll scrollController = Gtk.EventControllerScroll.New (Gtk.EventControllerScrollFlags.BothAxes);
		scrollController.OnScroll += HandleScroll;
		previewArea.AddController (scrollController);

		Gtk.GestureDrag panGesture = Gtk.GestureDrag.New ();
		panGesture.SetButton (0);
		panGesture.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		panGesture.OnDragBegin += HandlePanBegin;
		panGesture.OnDragUpdate += HandlePanUpdate;
		panGesture.OnDragEnd += HandlePanEnd;
		panGesture.OnCancel += (_, _) => previewArea.Cursor = grab_cursor;
		previewArea.AddController (panGesture);

		Gtk.EventControllerKey keyController = Gtk.EventControllerKey.New ();
		keyController.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		keyController.OnKeyPressed += HandleKeyPressed;
		AddController (keyController);

		Gtk.Label infoLabel = Gtk.Label.New (string.Empty);
		infoLabel.Halign = Gtk.Align.Start;
		infoLabel.MarginStart = 8;
		infoLabel.MarginEnd = 8;
		infoLabel.MarginTop = 6;
		infoLabel.MarginBottom = 6;

		Gtk.Button maximizeButton = Gtk.Button.NewFromIconName (Resources.StandardIcons.ViewFullscreen);
		maximizeButton.TooltipText = Translations.GetString ("Maximize Preview");
		maximizeButton.OnClicked += (_, _) => ToggleMaximized ();
		Gtk.HeaderBar headerBar = Gtk.HeaderBar.New ();
		headerBar.PackEnd (maximizeButton);
		SetTitlebar (headerBar);

		Gtk.ScrolledWindow scrolledWindow = Gtk.ScrolledWindow.New ();
		scrolledWindow.Hexpand = true;
		scrolledWindow.Vexpand = true;
		scrolledWindow.SetPolicy (Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
		scrolledWindow.SetChild (previewArea);

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		content.Append (infoLabel);
		content.Append (scrolledWindow);
		SetChild (content);

		preview_area = previewArea;
		scrolled_window = scrolledWindow;
		info_label = infoLabel;

		OnCloseRequest += HandleCloseRequest;
	}

	private void Configure (Gtk.Window parent, Document document, UserLayer layer)
	{
		this.document = document;
		this.layer = layer;

		TransientFor = parent;
		Title = layer.Name;
		DefaultWidth = 800;
		DefaultHeight = 600;
		Resizable = true;
		RenderPreview ();

		document.Workspace.CanvasInvalidated += HandleCanvasInvalidated;
		document.Layers.LayerPropertyChanged += HandleLayerPropertyChanged;
		document.Layers.LayerTreeChanged += HandleLayerTreeChanged;
		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT, () => {
			preview_area.GrabFocus ();
			return false;
		});
	}

	private void RenderPreview ()
	{
		if (closing)
			return;

		Cairo.ImageSurface surface = LayerActions.RenderLayer (document, layer);

		preview_surface?.Dispose ();
		preview_surface = surface;
		Title = layer.Name;
		UpdateInfoLabel ();
		UpdatePreviewSize ();
		preview_area.QueueDraw ();
	}

	private void UpdateInfoLabel ()
	{
		if (preview_surface is null)
			return;

		info_label.SetText ($"{layer.Name}  {preview_surface.Width} x {preview_surface.Height}  ({scale:P0})");
	}

	private void UpdatePreviewSize ()
	{
		if (preview_surface is null)
			return;

		int width = GetScaledSize (preview_surface.Width);
		int height = GetScaledSize (preview_surface.Height);
		preview_area.SetSizeRequest (width, height);
	}

	private int GetScaledSize (int size)
		=> Math.Clamp ((int) Math.Round (size * scale), 1, 100000);

	private void DrawPreview (Context context, int width, int height)
	{
		if (preview_surface is null || width <= 0 || height <= 0)
			return;

		context.Save ();
		context.Scale (scale, scale);
		context.SetSource (transparent_pattern);
		context.Rectangle (0, 0, preview_surface.Width, preview_surface.Height);
		context.Paint ();
		context.SetSourceSurface (
			preview_surface,
			scale >= 1.0 ? ResamplingMode.NearestNeighbor : ResamplingMode.Bilinear);
		context.Paint ();
		context.Restore ();
	}

	private bool HandleScroll (
		Gtk.EventControllerScroll controller,
		Gtk.EventControllerScroll.ScrollSignalArgs args)
	{
		double delta = Math.Abs (args.Dy) >= Math.Abs (args.Dx) ? args.Dy : args.Dx;
		if (delta == 0)
			return false;

		SetScale (scale * (delta < 0 ? 1.1 : 1.0 / 1.1));
		return true;
	}

	private void HandlePanBegin (
		Gtk.GestureDrag gesture,
		Gtk.GestureDrag.DragBeginSignalArgs args)
	{
		pan_start_h = scrolled_window.Hadjustment!.Value;
		pan_start_v = scrolled_window.Vadjustment!.Value;
		preview_area.Cursor = grabbing_cursor;
		gesture.SetState (Gtk.EventSequenceState.Claimed);
	}

	private void HandlePanUpdate (
		Gtk.GestureDrag gesture,
		Gtk.GestureDrag.DragUpdateSignalArgs args)
	{
		SetAdjustmentValue (scrolled_window.Hadjustment!, pan_start_h - args.OffsetX);
		SetAdjustmentValue (scrolled_window.Vadjustment!, pan_start_v - args.OffsetY);
	}

	private void HandlePanEnd (
		Gtk.GestureDrag gesture,
		Gtk.GestureDrag.DragEndSignalArgs args)
		=> preview_area.Cursor = grab_cursor;

	private static void SetAdjustmentValue (Gtk.Adjustment adjustment, double value)
	{
		double maximum = Math.Max (adjustment.Lower, adjustment.Upper - adjustment.PageSize);
		adjustment.Value = Math.Clamp (value, adjustment.Lower, maximum);
	}

	private bool HandleKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		Gdk.Key key = args.GetKey ();
		string keyName = key.Name ();
		if (key.Value == Gdk.Constants.KEY_Escape && IsMaximized ())
		{
			Unmaximize ();
			return true;
		}

		if (keyName is "plus" or "equal" or "KP_Add")
		{
			SetScale (scale * 1.1);
			return true;
		}

		if (keyName is "minus" or "KP_Subtract")
		{
			SetScale (scale / 1.1);
			return true;
		}

		if (keyName is "0" or "KP_0")
		{
			SetScale (1.0);
			return true;
		}

		return false;
	}

	private void SetScale (double requestedScale)
	{
		if (preview_surface is null)
			return;

		double newScale = Math.Clamp (requestedScale, MIN_SCALE, MAX_SCALE);
		if (Math.Abs (newScale - scale) < 0.0001)
			return;

		Gtk.Adjustment horizontal = scrolled_window.Hadjustment!;
		Gtk.Adjustment vertical = scrolled_window.Vadjustment!;
		double centerX = horizontal.PageSize / 2;
		double centerY = vertical.PageSize / 2;
		double imageX = (horizontal.Value + centerX) / scale;
		double imageY = (vertical.Value + centerY) / scale;

		scale = newScale;
		UpdateInfoLabel ();
		UpdatePreviewSize ();
		preview_area.QueueDraw ();

		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT, () => {
			SetAdjustmentValue (horizontal, imageX * scale - centerX);
			SetAdjustmentValue (vertical, imageY * scale - centerY);
			return false;
		});
	}

	private void HandleCanvasInvalidated (object? sender, CanvasInvalidatedEventArgs args)
		=> RenderPreview ();

	private void HandleLayerPropertyChanged (object? sender, PropertyChangedEventArgs args)
		=> RenderPreview ();

	private void HandleLayerTreeChanged (object? sender, EventArgs args)
	{
		if (!document.Layers.AllLayers.Any (item => item == layer))
		{
			Close ();
			return;
		}

		RenderPreview ();
	}

	private void ToggleMaximized ()
	{
		if (IsMaximized ())
			Unmaximize ();
		else
			Maximize ();
	}

	private bool HandleCloseRequest (Gtk.Window window, EventArgs args)
	{
		if (closing)
			return false;

		closing = true;
		document.Workspace.CanvasInvalidated -= HandleCanvasInvalidated;
		document.Layers.LayerPropertyChanged -= HandleLayerPropertyChanged;
		document.Layers.LayerTreeChanged -= HandleLayerTreeChanged;
		preview_surface?.Dispose ();
		preview_surface = null;
		Closed?.Invoke (this, EventArgs.Empty);
		return false;
	}

	public override void Dispose ()
	{
		if (!closing)
			HandleCloseRequest (this, EventArgs.Empty);

		base.Dispose ();
	}
}
