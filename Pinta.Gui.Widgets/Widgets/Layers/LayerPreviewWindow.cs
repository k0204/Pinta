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

	private bool initial_center_pending;
	private bool initial_center_complete;
	private Document document = null!;
	private UserLayer layer = null!;
	private Gtk.Overlay preview_area = null!;
	private Gtk.Picture image_picture = null!;
	private Gtk.ScrolledWindow scrolled_window = null!;
	private Gtk.Label info_label = null!;
	private Gdk.Texture? preview_texture;
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
	[System.Diagnostics.CodeAnalysis.MemberNotNull (nameof (image_picture))]
	[System.Diagnostics.CodeAnalysis.MemberNotNull (nameof (scrolled_window))]
	[System.Diagnostics.CodeAnalysis.MemberNotNull (nameof (info_label))]
	partial void Initialize ()
	{
		Gtk.DrawingArea checkerboardArea = Gtk.DrawingArea.New ();
		checkerboardArea.CanTarget = false;
		checkerboardArea.SetDrawFunc ((_, context, width, height) => DrawCheckerboard (context, width, height));

		Gtk.Picture imagePicture = Gtk.Picture.New ();
		imagePicture.CanTarget = false;
		imagePicture.Hexpand = true;
		imagePicture.Vexpand = true;
		imagePicture.Halign = Gtk.Align.Fill;
		imagePicture.Valign = Gtk.Align.Fill;
		imagePicture.ContentFit = Gtk.ContentFit.Fill;

		Gtk.Overlay previewArea = Gtk.Overlay.New ();
		previewArea.Focusable = true;
		previewArea.Cursor = grab_cursor;
		previewArea.Halign = Gtk.Align.Center;
		previewArea.Valign = Gtk.Align.Center;
		previewArea.SetChild (checkerboardArea);
		previewArea.AddOverlay (imagePicture);

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

		// The scrolled window stretches its child to at least the viewport size, which would
		// distort the picture (ContentFit.Fill). Wrap the preview in a box so the overlay keeps
		// its requested, aspect-correct size and stays centered when smaller than the viewport.
		Gtk.Box viewRoot = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		viewRoot.Append (previewArea);
		scrolledWindow.SetChild (viewRoot);

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		content.Append (infoLabel);
		content.Append (scrolledWindow);
		SetChild (content);

		preview_area = previewArea;
		image_picture = imagePicture;
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
		CenterViewWhenReady ();
		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT, () => {
			preview_area.GrabFocus ();
			return false;
		});
	}

	private void RenderPreview ()
	{
		if (closing)
			return;

		using Cairo.ImageSurface surface = LayerActions.RenderLayer (document, layer);
		Gdk.Texture texture = surface.ToTexture ();

		preview_texture?.Dispose ();
		preview_texture = texture;
		image_picture.Paintable = texture;
		Title = layer.Name;
		UpdateInfoLabel ();
		UpdatePreviewSize ();
	}

	private void UpdateInfoLabel ()
	{
		if (preview_texture is null)
			return;

		info_label.SetText ($"{layer.Name}  {preview_texture.Width} x {preview_texture.Height}  ({scale:P0})");
	}

	private void UpdatePreviewSize ()
	{
		if (preview_texture is null)
			return;

		int width = GetScaledSize (preview_texture.Width);
		int height = GetScaledSize (preview_texture.Height);
		preview_area.SetSizeRequest (width, height);
	}

	private int GetScaledSize (int size)
		=> Math.Clamp ((int) Math.Round (size * scale), 1, 100000);

	private static void DrawCheckerboard (Context context, int width, int height)
	{
		if (width <= 0 || height <= 0)
			return;

		context.SetSource (transparent_pattern);
		context.Rectangle (0, 0, width, height);
		context.Paint ();
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
		pan_start_h = RealHorizontal.Value;
		pan_start_v = RealVertical.Value;
		preview_area.Cursor = grabbing_cursor;
		gesture.SetState (Gtk.EventSequenceState.Claimed);
	}

	private void HandlePanUpdate (
		Gtk.GestureDrag gesture,
		Gtk.GestureDrag.DragUpdateSignalArgs args)
	{
		SetAdjustmentValue (RealHorizontal, pan_start_h - args.OffsetX);
		SetAdjustmentValue (RealVertical, pan_start_v - args.OffsetY);
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

	// Center the view on the content when the window first opens. The content size is the
	// scaled texture size (known immediately); we only wait for the scrolled window to
	// have a viewport size. Multiple triggers race to do it once, whichever observes the
	// laid-out viewport first wins.
	// Gtk.ScrolledWindow auto-wraps non-scrollable children in a Gtk.Viewport that uses
	// its own Gtk.Adjustment objects. scrolled_window.Hadjustment may return a separate,
	// unconnected adjustment (lazy-created by the getter), so always resolve the live
	// adjustments from the auto-viewport. Matches the pattern in CanvasWindow.cs and
	// DocumentWorkspace.cs.
	private Gtk.Adjustment RealHorizontal
		=> ((Gtk.Viewport) scrolled_window.Child!).GetHadjustment ()!;
	private Gtk.Adjustment RealVertical
		=> ((Gtk.Viewport) scrolled_window.Child!).GetVadjustment ()!;

	private void CenterViewWhenReady ()
	{
		RealHorizontal.OnChanged += (_, _) => QueueInitialCenter ();
		RealVertical.OnChanged += (_, _) => QueueInitialCenter ();
		OnMap += (_, _) => QueueInitialCenter ();
		QueueInitialCenter ();
	}

	private void QueueInitialCenter ()
	{
		if (initial_center_complete || initial_center_pending)
			return;

		initial_center_pending = true;
		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT_IDLE, () => {
			initial_center_pending = false;
			initial_center_complete = TryCenterView ();
			return false;
		});
	}

	private bool TryCenterView ()
	{
		if (preview_texture is null)
			return false;

		Gtk.Adjustment horizontal = RealHorizontal;
		Gtk.Adjustment vertical = RealVertical;
		int contentWidth = GetScaledSize (preview_texture.Width);
		int contentHeight = GetScaledSize (preview_texture.Height);
		if (horizontal.PageSize <= 0 || vertical.PageSize <= 0
			|| horizontal.Upper < contentWidth || vertical.Upper < contentHeight)
			return false;

		SetAdjustmentValue (horizontal, (horizontal.Lower + horizontal.Upper - horizontal.PageSize) / 2);
		SetAdjustmentValue (vertical, (vertical.Lower + vertical.Upper - vertical.PageSize) / 2);
		return true;
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
		if (preview_texture is null)
			return;

		double newScale = Math.Clamp (requestedScale, MIN_SCALE, MAX_SCALE);
		if (Math.Abs (newScale - scale) < 0.0001)
			return;

		Gtk.Adjustment horizontal = RealHorizontal;
		Gtk.Adjustment vertical = RealVertical;
		double centerX = horizontal.PageSize / 2;
		double centerY = vertical.PageSize / 2;
		double imageX = (horizontal.Value + centerX) / scale;
		double imageY = (vertical.Value + centerY) / scale;

		scale = newScale;
		UpdateInfoLabel ();
		UpdatePreviewSize ();

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
		preview_texture?.Dispose ();
		preview_texture = null;
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
