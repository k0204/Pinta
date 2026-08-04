//
// CanvasWindow.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2015 Jonathan Pobst
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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Cairo;
using Pinta.Core;
using Pinta.Gui.Widgets;

namespace Pinta;

[GObject.Subclass<Gtk.Grid>]
public sealed partial class CanvasWindow
{
	private Document document = null!; // NRT - set by factory method.
	private ChromeManager chrome = null!;
	private ToolManager tools = null!;

	private PintaCanvas canvas;
	private Gtk.Fixed canvas_container;
        private Gtk.DrawingArea guide_overlay;
	private Ruler horizontal_ruler;
	private Ruler vertical_ruler;
	private Gtk.ScrolledWindow scrolled_window;
	private Gtk.Widget? horizontal_scrollbar;
	private Gtk.Widget? vertical_scrollbar;
	private Gtk.EventControllerMotion motion_controller;
	private Gtk.GestureDrag drag_controller;
	private Gtk.GestureZoom gesture_zoom;

	private PointD current_canvas_pos = PointD.Zero;
	private double cumulative_zoom_amount;
	private double last_scale_delta;
        private GuideDragState? guide_drag_state;

	private const double ZOOM_THRESHOLD_SCROLL = 1.25;
	private const double ZOOM_THRESHOLD_PINCH = 0.15;
        private const double GUIDE_HIT_TOLERANCE_VIEW = 6;

        private readonly record struct GuideDragState (GuideOrientation Orientation, int Index);

	public Gtk.Widget Canvas { get { return canvas; } }

	[MemberNotNull (nameof (canvas), nameof (canvas_container))]
        [MemberNotNull (nameof (guide_overlay))]
	[MemberNotNull (nameof (horizontal_ruler), nameof (vertical_ruler))]
	[MemberNotNull (nameof (scrolled_window), nameof (horizontal_scrollbar), nameof (vertical_scrollbar))]
	[MemberNotNull (nameof (motion_controller), nameof (drag_controller), nameof (gesture_zoom))]
	partial void Initialize ()
	{
		Gtk.GestureZoom gestureZoom = Gtk.GestureZoom.New ();
		gestureZoom.SetPropagationPhase (Gtk.PropagationPhase.Bubble);
		gestureZoom.OnScaleChanged += HandleGestureZoomScaleChanged;
		gestureZoom.OnEnd += (_, _) => cumulative_zoom_amount = last_scale_delta = 0;
		gestureZoom.OnCancel += (_, _) => cumulative_zoom_amount = last_scale_delta = 0;

		Gtk.EventControllerScroll scrollController = Gtk.EventControllerScroll.New (Gtk.EventControllerScrollFlags.BothAxes); // Both axes must be captured so the zoom gesture can cancel them
		scrollController.OnScroll += HandleScrollEvent;
		scrollController.OnDecelerate += (_, _) => gestureZoom.IsActive (); // Cancel scroll deceleration when zooming

		PintaCanvas canvas = PintaCanvas.New ();
		// For CSS: add a drop shadow outline to the canvas to give it a clear border
		// when the image is close to the background color.
		canvas.Name = "canvas";

		Gtk.DropTarget referenceDrop = Gtk.DropTarget.New (Gdk.FileList.GetGType (), Gdk.DragAction.Copy);
		referenceDrop.OnDrop += HandleReferenceDrop;
		canvas.AddController (referenceDrop);

		Gtk.Fixed canvasContainer = Gtk.Fixed.New ();
		canvasContainer.Hexpand = true;
		canvasContainer.Vexpand = true;
		canvasContainer.Put (canvas, 0, 0);

		Gtk.Viewport viewPort = Gtk.Viewport.New (null, null);
		viewPort.AddController (scrollController);
		viewPort.Child = canvasContainer;

		// Use the drag gesture to forward a sequence of mouse press -> move -> release events to the current tool.
		// This is more reliable than using just a click gesture in combination with the move controller (see bug #1456)
		// Note that we attach this to the root canvas widget, not the canvas, so that it can receive drags that start outside the canvas.
		Gtk.GestureDrag dragController = Gtk.GestureDrag.New ();
		dragController.SetButton (0); // Listen for all mouse buttons.
		dragController.OnDragBegin += OnDragBegin;
		dragController.OnDragUpdate += OnDragUpdate;
		dragController.OnDragEnd += OnDragEnd;

		Gtk.ScrolledWindow scrolledWindow = Gtk.ScrolledWindow.New ();
		scrolledWindow.Hexpand = true;
		scrolledWindow.Vexpand = true;
		scrolledWindow.Child = viewPort;

                Gtk.DrawingArea guideOverlay = Gtk.DrawingArea.New ();
                guideOverlay.Hexpand = true;
                guideOverlay.Vexpand = true;
                guideOverlay.CanTarget = false;
                guideOverlay.SetDrawFunc ((_, context, width, height) => DrawGuidesOverlay (context, width, height));

                Gtk.Overlay canvasOverlay = Gtk.Overlay.New ();
                canvasOverlay.Child = scrolledWindow;
                canvasOverlay.AddOverlay (guideOverlay);

		Ruler horizontalRuler = Ruler.New (Gtk.Orientation.Horizontal);
		horizontalRuler.Metric = MetricType.Pixels;
		horizontalRuler.Visible = false;

		Ruler verticalRuler = Ruler.New (Gtk.Orientation.Vertical);
		verticalRuler.Metric = MetricType.Pixels;
		verticalRuler.Visible = false;

		Gtk.EventControllerMotion motionController = Gtk.EventControllerMotion.New ();
		motionController.OnMotion += HandleMotion;

		// --- Initialization (Gtk.Widget)

		// The mouse handler in PintaCanvas grabs focus away from toolbar widgets, along
		// with DocumentWorkpace.GrabFocusToCanvas()
		Focusable = true;

		AddController (gestureZoom);
		AddController (dragController);
		AddController (motionController);

		// --- Initialization (Gtk.Grid)

		ColumnHomogeneous = false;
		RowHomogeneous = false;

		Attach (horizontalRuler, 1, 0, 1, 1);
		Attach (verticalRuler, 0, 1, 1, 1);
                Attach (canvasOverlay, 1, 1, 1, 1);

		// --- References to keep

		this.canvas = canvas;
		canvas_container = canvasContainer;
                guide_overlay = guideOverlay;

		scrolled_window = scrolledWindow;
		gesture_zoom = gestureZoom;
		horizontal_ruler = horizontalRuler;
		vertical_ruler = verticalRuler;
		motion_controller = motionController;
		drag_controller = dragController;
		horizontal_scrollbar = scrolledWindow.GetHscrollbar ();
		vertical_scrollbar = scrolledWindow.GetVscrollbar ();

		// --- Further initialization

		// Update the ruler when the horizontal or vertical size has changed.
		// This can happen either from the canvas size changing (e.g. zooming),
		// or when the window is resized and the scroll area's size changes.
		scrolledWindow.Hadjustment!.OnChanged += UpdateRulerRange;
		scrolledWindow.Vadjustment!.OnChanged += UpdateRulerRange;

		// Update the ruler when scrolling around.
		scrolledWindow.Hadjustment!.OnValueChanged += UpdateRulerRange;
		scrolledWindow.Vadjustment!.OnValueChanged += UpdateRulerRange;
	}

	private void Configure (
		ChromeManager chrome,
		ToolManager tools,
		Document document,
		ICanvasGridService canvasGrid)
	{
		canvas.Configure (tools, document, canvasGrid);

		// Also update if the view size changed without affecting the size of
		// the canvas widget (e.g. when zoomed out and no scrollbars are required)
		document.Workspace.ViewSizeChanged += UpdateRulerRange;
		document.Workspace.CanvasPositionChanged += UpdateRulerRange;
		document.SelectionChanged += UpdateRulerSelection;
                document.Guides.Changed += (_, _) => guide_overlay.QueueDraw ();

		this.chrome = chrome;
		this.tools = tools;
		this.document = document;
		document.Workspace.Viewport = (Gtk.Viewport) scrolled_window.Child!;
		document.Workspace.CanvasContainer = canvas_container;
	}

	private bool HandleReferenceDrop (Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
	{
		if (args.Value.GetBoxed (Gdk.FileList.GetGType ()) is not Gdk.FileList files || document.ResourceRootUri is null)
			return false;

		PointD center = document.Workspace.ViewPointToCanvas (new PointD (args.X, args.Y));
		bool added = false;
		foreach (Gio.File file in files.GetFilesHelper ()) {
			if (!document.TryGetResourceRelativePath (file, out string relativePath) || PintaCore.ImageFormats.GetImporterByFile (file.GetParseName ()) is null)
				continue;

			tools.Commit ();
			string name = System.IO.Path.GetFileName (file.GetPath () ?? relativePath);
			UserLayer layer = document.Layers.AddReferenceLayer (name, relativePath, center);
			document.History.PushNewItem (new AddLayerHistoryItem (Resources.Icons.LayerImport, Translations.GetString ("Import Referenced Image"), layer, document.Layers.GetPosition (layer)));
			added = true;
		}

		if (added)
			document.Workspace.Invalidate ();
		return added;
	}

	public static CanvasWindow New (
		ChromeManager chrome,
		ToolManager tools,
		Document document,
		ICanvasGridService canvasGrid)
	{
		CanvasWindow window = NewWithProperties ([]);
		window.Configure (chrome, tools, document, canvasGrid);
		return window;
	}

	private void UpdateRulerSelection (object? sender, EventArgs e)
	{
		if (document.Selection.Visible) {
			RectangleD bounds = document.Selection.GetBounds ();
			var horizontalBounds = NumberRange.Create (bounds.Left, bounds.Left + bounds.Width);
			var verticalBounds = NumberRange.Create (bounds.Top, bounds.Top + bounds.Height);
			horizontal_ruler.SelectionBounds = horizontalBounds;
			vertical_ruler.SelectionBounds = verticalBounds;
		} else {
			// If there's no selection, clear the highlight
			horizontal_ruler.SelectionBounds = null;
			vertical_ruler.SelectionBounds = null;
		}
	}

	private void HandleMotion (
		Gtk.EventControllerMotion controller,
		Gtk.EventControllerMotion.MotionSignalArgs args)
	{
		PointD rootPoint = new (args.X, args.Y);

		// These coordinates are relative to our grid widget, so transform into the child image
		// view's coordinates, and then to the canvas coordinates.
		this.TranslateCoordinates (Canvas, rootPoint, out PointD viewPos);

		current_canvas_pos = document.Workspace.ViewPointToCanvas (viewPos);
		horizontal_ruler.Position = current_canvas_pos.X;
		vertical_ruler.Position = current_canvas_pos.Y;
		UpdateScrollbarTargeting (viewPos);

		// Forward mouse move events to the current tool when not dragging.
		if (drag_controller.GetStartPoint (out _, out _))
			return;

		if (document.Workspace.PointInCanvas (current_canvas_pos))
			chrome.LastCanvasCursorPoint = current_canvas_pos.ToInt ();

		ToolMouseEventArgs tool_args = new () {
			State = controller.GetCurrentEventState (),
			MouseButton = MouseButton.None,
			PointDouble = current_canvas_pos,
			WindowPoint = viewPos,
			RootPoint = rootPoint,
		};

		tools.DoMouseMove (document, tool_args);
	}

	private void HandleGestureZoomScaleChanged (object? sender, EventArgs e)
	{
		// Allow the user to zoom in/out by pinching the trackpad
		double pinchDelta = gesture_zoom.GetScaleDelta () - 1 - last_scale_delta;
		if (pinchDelta < 0) {
			if (cumulative_zoom_amount > 0)
				cumulative_zoom_amount = 0; // Reset the counter if the user changes direction so that changing direction doesn't take extra movement

			cumulative_zoom_amount += pinchDelta;
			if (cumulative_zoom_amount <= -ZOOM_THRESHOLD_PINCH) {
				document.Workspace.ZoomOutAroundCanvasPoint (current_canvas_pos);
				cumulative_zoom_amount = 0;
			}
		} else {
			if (cumulative_zoom_amount < 0)
				cumulative_zoom_amount = 0;

			cumulative_zoom_amount += pinchDelta;
			if (cumulative_zoom_amount >= ZOOM_THRESHOLD_PINCH) {
				document.Workspace.ZoomInAroundCanvasPoint (current_canvas_pos);
				cumulative_zoom_amount = 0;
			}
		}
		last_scale_delta = gesture_zoom.GetScaleDelta () - 1;
	}

	public bool IsMouseOnCanvas
		=> motion_controller.ContainsPointer;

	public bool RulersVisible {
		get => horizontal_ruler.Visible;
		set {
			if (horizontal_ruler.Visible == value) return;
			horizontal_ruler.Visible = value;
			vertical_ruler.Visible = value;
		}
	}

	public MetricType RulerMetric {
		get => horizontal_ruler.Metric;
		set {
			if (horizontal_ruler.Metric == value) return;
			horizontal_ruler.Metric = value;
			vertical_ruler.Metric = value;
		}
	}

	public void UpdateRulerRange (object? sender, EventArgs e)
	{
		DocumentWorkspace workspace = document.Workspace;
		workspace.PositionCanvas ();
		if (TryGetRulerRange (horizontal_ruler, Gtk.Orientation.Horizontal, workspace.Scale, out NumberRange<double> horizontalRange))
			horizontal_ruler.RulerRange = horizontalRange;
		if (TryGetRulerRange (vertical_ruler, Gtk.Orientation.Vertical, workspace.Scale, out NumberRange<double> verticalRange))
			vertical_ruler.RulerRange = verticalRange;
                guide_overlay.QueueDraw ();
	}

	private bool TryGetRulerRange (
		Ruler ruler,
		Gtk.Orientation orientation,
		double scale,
		out NumberRange<double> range)
	{
		range = default;
		if (scale <= 0 || !canvas.TranslateCoordinates (ruler, PointD.Zero, out PointD canvasOrigin))
			return false;

		double origin = orientation == Gtk.Orientation.Horizontal ? canvasOrigin.X : canvasOrigin.Y;
		double length = orientation == Gtk.Orientation.Horizontal
			? ruler.GetAllocatedWidth ()
			: ruler.GetAllocatedHeight ();
		if (length <= 0)
			return false;

		double lower = -origin / scale;
		range = new (lower, lower + length / scale);
		return true;
	}

	private bool HandleScrollEvent (
		Gtk.EventControllerScroll controller,
		Gtk.EventControllerScroll.ScrollSignalArgs args)
	{
		if (gesture_zoom.IsActive ())
			return true;
		// Allow the user to zoom in/out with Ctrl-Mousewheel or Ctrl-two-finger-scroll
		if (!controller.GetCurrentEventState ().IsControlPressed ())
			return false;

		// "clicky" scroll wheels generate 1 or -1

		if (args.Dy == -1) {
			document.Workspace.ZoomInAroundCanvasPoint (current_canvas_pos);
			return true;
		}

		if (args.Dy == 1) {
			document.Workspace.ZoomOutAroundCanvasPoint (current_canvas_pos);
			return true;
		}

		// analog scroll wheels and scrolling on a touchpad generates a range of values constantly as the user scrolls
		// this might feel "backwards" on a touchpad to some people
		if (args.Dy < 0) {
			if (cumulative_zoom_amount > 0)
				cumulative_zoom_amount = 0;

			cumulative_zoom_amount += args.Dy;
			if (cumulative_zoom_amount <= -ZOOM_THRESHOLD_SCROLL) {
				document.Workspace.ZoomInAroundCanvasPoint (current_canvas_pos);
				cumulative_zoom_amount = 0;
			}

		} else {
			if (cumulative_zoom_amount < 0)
				cumulative_zoom_amount = 0;

			cumulative_zoom_amount += args.Dy;
			if (cumulative_zoom_amount >= ZOOM_THRESHOLD_SCROLL) {
				document.Workspace.ZoomOutAroundCanvasPoint (current_canvas_pos);
				cumulative_zoom_amount = 0;
			}

		}

		return true;
	}

	private void OnDragBegin (Gtk.GestureDrag gesture, Gtk.GestureDrag.DragBeginSignalArgs args)
	{
		// A mouse click on the canvas should grab focus away from any toolbar widgets, etc
		// Using the root canvas widget works best - if the drawing area is given focus, the scroll
		// widget jumps back to the origin.
		GrabFocus ();

                PointD rootPoint = new (args.StartX, args.StartY);
                if (TryStartGuideDrag (rootPoint)) {
                        gesture.SetState (Gtk.EventSequenceState.Claimed);
                        return;
                }

		// Note: if we ever regain support for docking multiple canvas
		// widgets side by side (like Pinta 1.7 could), a mouse click should switch
		// the active document to this document.

		// Send the mouse press event to the current tool.
		// Translate coordinates to the canvas widget.
		this.TranslateCoordinates (Canvas, rootPoint, out PointD viewPoint);
		PointD canvasPoint = document.Workspace.ViewPointToCanvas (viewPoint);

		ToolMouseEventArgs tool_args = new () {
			State = gesture.GetCurrentEventState (),
			MouseButton = gesture.GetCurrentMouseButton (),
			PointDouble = canvasPoint,
			WindowPoint = viewPoint,
			RootPoint = rootPoint,
		};

		tools.DoMouseDown (document, tool_args);
	}

	private void OnDragUpdate (Gtk.GestureDrag gesture, Gtk.GestureDrag.DragUpdateSignalArgs args)
	{
		gesture.GetStartPoint (out double startX, out double startY);
		PointD rootPoint = new (startX + args.OffsetX, startY + args.OffsetY);

                if (guide_drag_state.HasValue) {
                        UpdateGuideDrag (rootPoint);
                        return;
                }

		// Translate coordinates to the canvas widget.
		this.TranslateCoordinates (Canvas, rootPoint, out PointD viewPoint);

		current_canvas_pos = document.Workspace.ViewPointToCanvas (viewPoint);
		if (document.Workspace.PointInCanvas (current_canvas_pos))
			chrome.LastCanvasCursorPoint = current_canvas_pos.ToInt ();

		// Send the mouse move event to the current tool.
		ToolMouseEventArgs tool_args = new () {
			State = gesture.GetCurrentEventState (),
			MouseButton = gesture.GetCurrentMouseButton (),
			PointDouble = current_canvas_pos,
			WindowPoint = viewPoint,
			RootPoint = rootPoint,
		};

		tools.DoMouseMove (document, tool_args);
	}

	private void OnDragEnd (Gtk.GestureDrag gesture, Gtk.GestureDrag.DragEndSignalArgs args)
	{
		gesture.GetStartPoint (out double startX, out double startY);
		PointD rootPoint = new (startX + args.OffsetX, startY + args.OffsetY);

                if (guide_drag_state.HasValue) {
                        EndGuideDrag (rootPoint);
                        return;
                }

		// Translate coordinates to the canvas widget.
		this.TranslateCoordinates (Canvas, rootPoint, out PointD viewPoint);
		PointD canvasPoint = document.Workspace.ViewPointToCanvas (viewPoint);

		// Send the mouse release event to the current tool.
		ToolMouseEventArgs tool_args = new () {
			State = gesture.GetCurrentEventState (),
			MouseButton = gesture.GetCurrentMouseButton (),
			PointDouble = canvasPoint,
			WindowPoint = viewPoint,
			RootPoint = rootPoint,
		};

		tools.DoMouseUp (document, tool_args);
	}

	public bool DoKeyPressEvent (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		// Give the current tool a chance to handle the key press
		ToolKeyEventArgs tool_args = new () {
			Event = controller.GetCurrentEvent (),
			Key = args.GetKey (),
			State = args.State,
		};

		return tools.DoKeyDown (document, tool_args);
	}

	public bool DoKeyReleaseEvent (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyReleasedSignalArgs args)
	{
		ToolKeyEventArgs tool_args = new () {
			Event = controller.GetCurrentEvent (),
			Key = args.GetKey (),
			State = args.State,
		};

		return tools.DoKeyUp (document, tool_args);
	}

	private void UpdateScrollbarTargeting (PointD? viewPos = null)
	{
		bool canTarget = viewPos is null
			|| tools.CurrentTool?.Handles.Any (h => h.Active && h.ContainsPoint (viewPos.Value)) != true;

		if (horizontal_scrollbar is not null)
			horizontal_scrollbar.CanTarget = canTarget;

		if (vertical_scrollbar is not null)
			vertical_scrollbar.CanTarget = canTarget;
	}

        private void DrawGuidesOverlay (Context context, int width, int height)
        {
                if (document.Guides.Count == 0 || width <= 0 || height <= 0)
                        return;

                PointD canvasOrigin = GetCanvasOriginInViewport ();
                double scale = document.Workspace.Scale;

                context.SetSourceColor (new Color (0.1, 0.6, 1.0, 0.95));
                context.LineWidth = 1;

                foreach (DocumentGuide guide in document.Guides.Items) {
                        switch (guide.Orientation) {
                                case GuideOrientation.Horizontal:
                                        double y = canvasOrigin.Y + guide.Position * scale + 0.5;
                                        context.MoveTo (0, y);
                                        context.LineTo (width, y);
                                        break;
                                case GuideOrientation.Vertical:
                                        double x = canvasOrigin.X + guide.Position * scale + 0.5;
                                        context.MoveTo (x, 0);
                                        context.LineTo (x, height);
                                        break;
                        }
                }

                context.Stroke ();
        }

        private PointD GetCanvasOriginInViewport ()
        {
		canvas.TranslateCoordinates (guide_overlay, PointD.Zero, out PointD overlayOrigin);
		return overlayOrigin;
        }

        private void EndGuideDrag (PointD rootPoint)
        {
                GuideDragState state = guide_drag_state!.Value;
                guide_drag_state = null;

                if (IsPointerOnDeleteRuler (state.Orientation, rootPoint))
                        document.Guides.RemoveAt (state.Index);
        }

        private bool IsPointerOnDeleteRuler (GuideOrientation orientation, PointD rootPoint)
        {
                return orientation switch {
                        GuideOrientation.Horizontal => horizontal_ruler.Visible && horizontal_ruler.IsMouseInDrawingArea (this, rootPoint, out _),
                        GuideOrientation.Vertical => vertical_ruler.Visible && vertical_ruler.IsMouseInDrawingArea (this, rootPoint, out _),
                        _ => false,
                };
        }

        private bool TryFindGuideAtPoint (PointD rootPoint, out GuideDragState state)
        {
                state = default;

                if (!this.TranslateCoordinates (guide_overlay, rootPoint, out PointD overlayPoint))
                        return false;

                PointD canvasOrigin = GetCanvasOriginInViewport ();
                double scale = document.Workspace.Scale;
                double bestDistance = double.MaxValue;
                bool found = false;

                for (int i = 0; i < document.Guides.Items.Count; i++) {
                        DocumentGuide guide = document.Guides.Items[i];
                        double distance = guide.Orientation == GuideOrientation.Horizontal
                                ? Math.Abs (canvasOrigin.Y + guide.Position * scale - overlayPoint.Y)
                                : Math.Abs (canvasOrigin.X + guide.Position * scale - overlayPoint.X);

                        if (distance > GUIDE_HIT_TOLERANCE_VIEW || distance >= bestDistance)
                                continue;

                        bestDistance = distance;
                        state = new GuideDragState (guide.Orientation, i);
                        found = true;
                }

                return found;
        }

        private bool TryStartGuideDrag (PointD rootPoint)
        {
                if (horizontal_ruler.Visible && horizontal_ruler.IsMouseInDrawingArea (this, rootPoint, out _)) {
                        int index = document.Guides.AddHorizontal (0);
                        guide_drag_state = new GuideDragState (GuideOrientation.Horizontal, index);
                        UpdateGuideDrag (rootPoint);
                        return true;
                }

                if (vertical_ruler.Visible && vertical_ruler.IsMouseInDrawingArea (this, rootPoint, out _)) {
                        int index = document.Guides.AddVertical (0);
                        guide_drag_state = new GuideDragState (GuideOrientation.Vertical, index);
                        UpdateGuideDrag (rootPoint);
                        return true;
                }

                if (!TryFindGuideAtPoint (rootPoint, out GuideDragState state))
                        return false;

                guide_drag_state = state;
                UpdateGuideDrag (rootPoint);
                return true;
        }

        private void UpdateGuideDrag (PointD rootPoint)
        {
                GuideDragState state = guide_drag_state!.Value;

                if (!this.TranslateCoordinates (guide_overlay, rootPoint, out PointD overlayPoint))
                        return;

                PointD canvasOrigin = GetCanvasOriginInViewport ();
                double scale = document.Workspace.Scale;
                PointD canvasPoint = new (
                        (overlayPoint.X - canvasOrigin.X) / scale,
                        (overlayPoint.Y - canvasOrigin.Y) / scale);
                current_canvas_pos = canvasPoint;
                horizontal_ruler.Position = canvasPoint.X;
                vertical_ruler.Position = canvasPoint.Y;

                double position = state.Orientation == GuideOrientation.Horizontal
                        ? canvasPoint.Y
                        : canvasPoint.X;

                document.Guides.UpdateAt (state.Index, position);
        }
}
