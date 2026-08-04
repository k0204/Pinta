using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog
{
	private const double guide_hit_tolerance = 6;
	private const double guide_snap_tolerance = 8;
	private const double anchor_hit_tolerance = 10;
	private readonly List<DocumentGuide> guides = [];
	private readonly Gtk.DrawingArea horizontal_ruler = Gtk.DrawingArea.New ();
	private readonly Gtk.DrawingArea vertical_ruler = Gtk.DrawingArea.New ();
	private GuideDragState? guide_drag_state;
	private Gdk.Cursor? horizontal_guide_cursor;
	private Gdk.Cursor? vertical_guide_cursor;
	private PointD? ruler_position;
	private double preview_drag_start_x;
	private double preview_drag_start_y;
	private const double preview_zoom_min = 0.25;
	private const double preview_zoom_max = 8.0;
	private const double preview_zoom_step = 1.25;
	private double preview_zoom = 1.0;
	private double preview_pan_x;
	private double preview_pan_y;
	private double preview_pan_start_x;
	private double preview_pan_start_y;
	private bool preview_panning;
	private bool preview_space_pressed;
	private bool root_dragging;
	private int drag_start_root_dx;
	private int drag_start_root_dy;

	private readonly record struct GuideDragState (int Index);
	private readonly record struct RulerGuideDragState (Gtk.Orientation Orientation, int Index);
	private RulerGuideDragState? ruler_guide_drag_state;

	private void ConnectRulerAndGuidePointerEvents ()
	{
		Gtk.EventControllerMotion motion = Gtk.EventControllerMotion.New ();
		motion.OnMotion += (_, args) => HandlePreviewMotion (args.X, args.Y);
		motion.OnLeave += (_, _) => HandlePreviewLeave ();
		frame_preview.AddController (motion);
		AddRulerGuideDrag (horizontal_ruler, Gtk.Orientation.Horizontal);
		AddRulerGuideDrag (vertical_ruler, Gtk.Orientation.Vertical);
	}

	private void AddRulerGuideDrag (Gtk.DrawingArea ruler, Gtk.Orientation orientation)
	{
		Gtk.GestureDrag drag = Gtk.GestureDrag.New ();
		drag.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		drag.OnDragBegin += (_, _) => BeginRulerGuideDrag (orientation);
		drag.OnDragUpdate += (controller, args) => UpdateRulerGuideDrag (ruler, controller, args.OffsetX, args.OffsetY);
		drag.OnDragEnd += (controller, args) => EndRulerGuideDrag (ruler, controller, args.OffsetX, args.OffsetY);
		ruler.AddController (drag);
	}

	private void BeginRulerGuideDrag (Gtk.Orientation orientation)
	{
		int index = guides.Count;
		GuideOrientation guideOrientation = orientation == Gtk.Orientation.Horizontal
			? GuideOrientation.Horizontal
			: GuideOrientation.Vertical;
		guides.Add (new DocumentGuide (guideOrientation, 0));
		ruler_guide_drag_state = new RulerGuideDragState (orientation, index);
	}

	private void UpdateRulerGuideDrag (
		Gtk.DrawingArea ruler,
		Gtk.GestureDrag drag,
		double offsetX,
		double offsetY)
	{
		if (ruler_guide_drag_state is not RulerGuideDragState state)
			return;
		drag.GetStartPoint (out double startX, out double startY);
		PointD point = new (startX + offsetX, startY + offsetY);
		if (ruler.TranslateCoordinates (frame_preview, point, out PointD previewPoint))
				UpdateGuide (
					state.Index,
					previewPoint.X,
					previewPoint.Y,
					drag.GetCurrentEventState ().IsShiftPressed ());
	}

	private void EndRulerGuideDrag (
		Gtk.DrawingArea ruler,
		Gtk.GestureDrag drag,
		double offsetX,
		double offsetY)
	{
		if (ruler_guide_drag_state is not RulerGuideDragState state)
			return;
		UpdateRulerGuideDrag (ruler, drag, offsetX, offsetY);

		drag.GetStartPoint (out double startX, out double startY);
		PointD point = new (startX + offsetX, startY + offsetY);
		RectangleD bounds = GetPreviewBounds ();
		bool insidePreview = ruler.TranslateCoordinates (frame_preview, point, out PointD previewPoint)
			&& previewPoint.X >= bounds.Left && previewPoint.X <= bounds.Right
			&& previewPoint.Y >= bounds.Top && previewPoint.Y <= bounds.Bottom;
		if (!insidePreview)
			guides.RemoveAt (state.Index);
		ruler_guide_drag_state = null;
		frame_preview.QueueDraw ();
		horizontal_ruler.QueueDraw ();
		vertical_ruler.QueueDraw ();
	}

	private void HandlePreviewMotion (double x, double y)
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		if (scale > 0) {
			ruler_position = new PointD (
				Math.Clamp ((x - left) / scale, 0, canvas_width.Value),
				Math.Clamp ((y - top) / scale, 0, canvas_height.Value));
			horizontal_ruler.QueueDraw ();
			vertical_ruler.QueueDraw ();
		}

		GuideDragState? state = guide_drag_state ?? FindGuideAtPoint (x, y);
		frame_preview.Cursor = preview_space_pressed || preview_panning
			? GetPanCursor ()
			: state is GuideDragState guide
			? GetGuideCursor (guides[guide.Index].Orientation)
			: null;
	}

	private bool HandlePreviewScroll (
		Gtk.EventControllerScroll controller,
		Gtk.EventControllerScroll.ScrollSignalArgs args)
	{
		double delta = Math.Abs (args.Dy) >= Math.Abs (args.Dx) ? args.Dy : args.Dx;
		if (delta == 0)
			return false;

		ChangePreviewZoom (delta < 0 ? 1.1 : 1 / 1.1);
		return true;
	}

	private bool HandlePreviewSpaceKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (args.GetKey ().Value != Gdk.Constants.KEY_space)
			return false;

		preview_space_pressed = true;
		frame_preview.Cursor = GetPanCursor ();
		return true;
	}

	private void HandlePreviewSpaceKeyReleased (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyReleasedSignalArgs args)
	{
		if (args.GetKey ().Value != Gdk.Constants.KEY_space)
			return;

		preview_space_pressed = false;
		if (!preview_panning)
			frame_preview.Cursor = null;
	}

	private Gdk.Cursor? pan_cursor;

	private Gdk.Cursor? GetPanCursor ()
		=> pan_cursor ??= Gdk.Cursor.NewFromName ("move", null);

	private void BeginPreviewPan ()
	{
		preview_pan_start_x = preview_pan_x;
		preview_pan_start_y = preview_pan_y;
		preview_panning = true;
		frame_preview.Cursor = GetPanCursor ();
	}

	private void UpdatePreviewPan (double offsetX, double offsetY)
	{
		if (!preview_panning)
			return;
		preview_pan_x = preview_pan_start_x + offsetX;
		preview_pan_y = preview_pan_start_y + offsetY;
		ClampPreviewPan ();
		QueuePreviewTransformDraw ();
	}

	private void EndPreviewPan ()
	{
		preview_panning = false;
		frame_preview.Cursor = preview_space_pressed ? GetPanCursor () : null;
	}

	private void CancelPreviewDrag ()
	{
		guide_drag_state = null;
		root_dragging = false;
		frame_position_dragging = false;
		preview_panning = false;
		frame_preview.Cursor = preview_space_pressed ? GetPanCursor () : null;
	}

	private void ChangePreviewZoom (double factor)
	{
		preview_zoom = Math.Clamp (preview_zoom * factor, preview_zoom_min, preview_zoom_max);
		ClampPreviewPan ();
		QueuePreviewTransformDraw ();
		Refresh ();
	}

	private void QueuePreviewTransformDraw ()
	{
		frame_preview.QueueDraw ();
		horizontal_ruler.QueueDraw ();
		vertical_ruler.QueueDraw ();
	}

	private void ClampPreviewPan ()
	{
		int width = (int) canvas_width.Value;
		int height = (int) canvas_height.Value;
		(double fitScale, _, _) = GetFramePreviewTransform (ignorePan: true);
		if (fitScale <= 0)
			return;

		preview_pan_x = ClampPan (preview_pan_x, fitScale * width, frame_preview.GetWidth ());
		preview_pan_y = ClampPan (preview_pan_y, fitScale * height, frame_preview.GetHeight ());
	}

	private static double ClampPan (double pan, double contentSize, int viewportSize)
	{
		if (contentSize <= viewportSize)
			return 0;
		double centeredOffset = (viewportSize - contentSize) / 2;
		return Math.Clamp (pan, -centeredOffset - contentSize + viewportSize, -centeredOffset);
	}

	private Gdk.Cursor? GetGuideCursor (GuideOrientation orientation)
		=> orientation == GuideOrientation.Horizontal
			? horizontal_guide_cursor ??= Gdk.Cursor.NewFromName ("row-resize", null)
			: vertical_guide_cursor ??= Gdk.Cursor.NewFromName ("col-resize", null);

	private void HandlePreviewLeave ()
	{
		ruler_position = null;
		if (guide_drag_state is null && !preview_panning && !preview_space_pressed)
			frame_preview.Cursor = null;
		horizontal_ruler.QueueDraw ();
		vertical_ruler.QueueDraw ();
	}

	private void ClampGuidesAndRefresh ()
	{
		for (int i = 0; i < guides.Count; i++) {
			DocumentGuide guide = guides[i];
			double maximum = guide.Orientation == GuideOrientation.Horizontal ? canvas_height.Value : canvas_width.Value;
			guides[i] = guide with { Position = Math.Clamp (guide.Position, 0, maximum) };
		}
		Refresh ();
	}

	private void BeginPreviewDrag (double x, double y)
	{
		if (preview_space_pressed) {
			BeginPreviewPan ();
			return;
		}

		preview_drag_start_x = x;
		preview_drag_start_y = y;
		guide_drag_state = FindGuideAtPoint (x, y);
		frame_position_dragging = false;
		if (guide_drag_state is null && move_root.Active && IsRootAnchorHit (x, y)) {
			root_dragging = true;
			drag_start_root_dx = root_dx;
			drag_start_root_dy = root_dy;
			return;
		}
		if (guide_drag_state is null && frames.Count > 0) {
			drag_start_x = frames[selected_frame].X;
			drag_start_y = frames[selected_frame].Y;
			BeginFramePositionDrag ();
		}
	}

	private bool IsRootAnchorHit (double x, double y)
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		if (scale <= 0)
			return false;
		double anchorX = left + (canvas_width.Value / 2.0 + root_dx) * scale;
		double anchorY = top + (canvas_height.Value + root_dy) * scale;
		return Math.Abs (x - anchorX) <= anchor_hit_tolerance
			&& Math.Abs (y - anchorY) <= anchor_hit_tolerance;
	}

	private void DragRoot (double offsetX, double offsetY)
	{
		double scale = GetFramePreviewTransform ().Scale;
		if (scale <= 0)
			return;
		root_dx = Math.Clamp (drag_start_root_dx + (int) Math.Round (offsetX / scale), (int) (-canvas_width.Value / 2), (int) (canvas_width.Value / 2));
		root_dy = Math.Clamp (drag_start_root_dy + (int) Math.Round (offsetY / scale), (int) -canvas_height.Value, 0);
		RepositionFramesAroundAnchor ();
		Refresh ();
	}

	private void UpdatePreviewDrag (double offsetX, double offsetY, bool snapToRuler)
	{
		if (preview_panning)
			UpdatePreviewPan (offsetX, offsetY);
		else if (root_dragging)
			DragRoot (offsetX, offsetY);
		else if (guide_drag_state is GuideDragState state)
			UpdateGuide (
				state.Index,
				preview_drag_start_x + offsetX,
				preview_drag_start_y + offsetY,
				snapToRuler);
		else
			DragSelectedFrame (offsetX, offsetY);
	}

	private void EndPreviewDrag (double offsetX, double offsetY, bool snapToRuler)
	{
		if (preview_panning) {
			EndPreviewPan ();
			return;
		}
		if (root_dragging) {
			root_dragging = false;
			return;
		}
		if (guide_drag_state is not GuideDragState state) {
			EndFramePositionDrag (selected_frame);
			return;
		}

		PointD point = new (preview_drag_start_x + offsetX, preview_drag_start_y + offsetY);
		if (guide_drag_state is GuideDragState guide)
			UpdateGuide (guide.Index, point.X, point.Y, snapToRuler);
		RectangleD bounds = GetPreviewBounds ();
		if (point.X < bounds.Left || point.X > bounds.Right || point.Y < bounds.Top || point.Y > bounds.Bottom)
			guides.RemoveAt (state.Index);
		guide_drag_state = null;
		frame_preview.Cursor = null;
		frame_preview.QueueDraw ();
	}

	private GuideDragState? FindGuideAtPoint (double x, double y)
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		RectangleD bounds = GetPreviewBounds ();
		double bestDistance = double.MaxValue;
		GuideDragState? result = null;
		for (int i = 0; i < guides.Count; i++) {
			DocumentGuide guide = guides[i];
			if (guide.Orientation == GuideOrientation.Horizontal && (x < bounds.Left || x > bounds.Right)
				|| guide.Orientation == GuideOrientation.Vertical && (y < bounds.Top || y > bounds.Bottom))
				continue;
			double distance = guide.Orientation == GuideOrientation.Horizontal
				? Math.Abs (top + guide.Position * scale - y)
				: Math.Abs (left + guide.Position * scale - x);
			if (distance <= guide_hit_tolerance && distance < bestDistance) {
				bestDistance = distance;
				result = new GuideDragState (i);
			}
		}
		return result;
	}

	private void UpdateGuide (int index, double x, double y, bool snapToRuler = false)
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		DocumentGuide guide = guides[index];
		double position = guide.Orientation == GuideOrientation.Horizontal
			? (y - top) / scale
			: (x - left) / scale;
		double maximum = guide.Orientation == GuideOrientation.Horizontal ? canvas_height.Value : canvas_width.Value;
		if (snapToRuler)
			position = SnapGuidePosition (position, scale);
		guides[index] = guide with { Position = Math.Clamp (position, 0, maximum) };
		frame_preview.QueueDraw ();
	}

	private async Task EditGuidePositionAsync (int index)
	{
		if (index < 0 || index >= guides.Count)
			return;

		DocumentGuide guide = guides[index];
		int maximum = guide.Orientation == GuideOrientation.Horizontal
			? (int) canvas_height.Value
			: (int) canvas_width.Value;
		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Guide position");
		dialog.TransientFor = this.dialog;
		dialog.Modal = true;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget okButton = dialog.AddButton (Translations.GetString ("_OK"), (int) Gtk.ResponseType.Ok);
		okButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.Label label = Gtk.Label.New (Translations.GetString ("Position:"));
		label.Halign = Gtk.Align.Start;
		Gtk.SpinButton input = Gtk.SpinButton.NewWithRange (0, maximum, 1);
		input.Value = Math.Clamp (Math.Round (guide.Position), 0, maximum);
		input.SetActivatesDefaultImmediate (true);
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		row.Append (label);
		row.Append (input);
		Gtk.Box content = dialog.GetContentAreaBox ();
		content.SetAllMargins (12);
		content.Append (row);

		Gtk.ResponseType response = await dialog.RunAsync ();
		dialog.Close ();
		if (response == Gtk.ResponseType.Ok && index < guides.Count) {
			guides[index] = guide with { Position = input.Value };
			Refresh ();
		}
	}

	private static double SnapGuidePosition (double position, double scale)
	{
		double step = GetRulerStep (scale) / 5;
		if (step <= 0)
			return position;

		double snapped = Math.Round (position / step) * step;
		return Math.Abs (snapped - position) * scale <= guide_snap_tolerance
			? snapped
			: position;
	}

	private void DrawPreviewGuides (Context context, double scale, int width, int height)
	{
		context.SetSourceColor (new Color (0.1, 0.6, 1.0, 0.95));
		context.LineWidth = 1 / scale;
		foreach (DocumentGuide guide in guides) {
			if (guide.Orientation == GuideOrientation.Horizontal) {
				context.MoveTo (0, guide.Position);
				context.LineTo (width, guide.Position);
			} else {
				context.MoveTo (guide.Position, 0);
				context.LineTo (guide.Position, height);
			}
		}
		context.Stroke ();
	}

	private void DrawRuler (
		Gtk.DrawingArea area,
		Context context,
		int width,
		int height,
		Gtk.Orientation orientation)
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		if (scale <= 0)
			return;

		// ── Background ──
		context.SetSourceRgb (0.95, 0.94, 0.93); // adwaita light gray
		context.Rectangle (0, 0, width, height);
		context.Fill ();

		// ── Bottom/right border ──
		context.SetSourceRgb (0.85, 0.84, 0.83);
		context.LineWidth = 1;
		if (orientation == Gtk.Orientation.Horizontal) {
			context.MoveTo (0, height - 0.5);
			context.LineTo (width, height - 0.5);
		} else {
			context.MoveTo (width - 0.5, 0);
			context.LineTo (width - 0.5, height);
		}
		context.Stroke ();

		// ── Ticks & labels ──
		area.GetStyleContext ().GetColor (out Gdk.RGBA fg);
		Color labelColor = new Color (fg.Red * 0.55, fg.Green * 0.55, fg.Blue * 0.55, 1.0);
		Color tickColor = new Color (fg.Red * 0.7, fg.Green * 0.7, fg.Blue * 0.7, 1.0);
		double length = orientation == Gtk.Orientation.Horizontal ? canvas_width.Value : canvas_height.Value;
		double origin = orientation == Gtk.Orientation.Horizontal ? left : top;
		double majorStep = GetRulerStep (scale);
		double minorStep = majorStep / 5;
		int tick = 0;
		for (double value = 0; value <= length + minorStep * 0.5; value += minorStep, tick++) {
			double position = origin + value * scale;
			if (position < -1 || position > (orientation == Gtk.Orientation.Horizontal ? width : height) + 1)
				continue;
			int tier = tick % 5;
			bool major = tier == 0;
			DrawRulerTick (context, orientation, position, width, height, major ? 2 : (tier == 3 ? 1 : 0));
			if (major)
				DrawRulerLabel (area, context, orientation, position, value, width, labelColor);
		}

		// ── Pointer indicator (subtle accent) ──
		if (ruler_position is PointD pointer) {
			double value = orientation == Gtk.Orientation.Horizontal ? pointer.X : pointer.Y;
			double pos = origin + value * scale;
			context.SetSourceRgba (0.15, 0.45, 0.8, 0.6); // subtle blue
			context.LineWidth = orientation == Gtk.Orientation.Horizontal ? height * 0.15 : width * 0.12;
			if (orientation == Gtk.Orientation.Horizontal) {
				context.MoveTo (pos, 2);
				context.LineTo (pos, height - 2);
			} else {
				context.MoveTo (2, pos);
				context.LineTo (width - 2, pos);
			}
			context.Stroke ();
		}
	}

	private static void DrawRulerTick (
		Context context,
		Gtk.Orientation orientation,
		double position,
		int width,
		int height,
		int tier) // 0 = minor (1/3), 1 = medium (2/3), 2 = major (full)
	{
		double endRatio = tier switch {
			0 => 0.33,
			1 => 0.67,
			2 => 1.0,
			_ => 1.0,
		};
		context.LineWidth = tier == 2 ? 1.0 : 0.6;
		if (orientation == Gtk.Orientation.Horizontal) {
			double start = height * (1 - endRatio);
			context.MoveTo (position, start);
			context.LineTo (position, height);
		} else {
			double start = width * (1 - endRatio);
			context.MoveTo (start, position);
			context.LineTo (width, position);
		}
		context.Stroke ();
	}

	private static void DrawRulerLabel (
		Gtk.DrawingArea area,
		Context context,
		Gtk.Orientation orientation,
		double position,
		double value,
		int width,
		Color labelColor)
	{
		using Pango.Layout layout = area.CreatePangoLayout (((int) value).ToString ());
		Pango.FontDescription font = Pango.FontDescription.FromString ("8");
		layout.SetFontDescription (font);
		context.SetSourceColor (labelColor);
		if (orientation == Gtk.Orientation.Horizontal) {
			context.MoveTo (position + 3, 2);
			PangoCairo.Functions.ShowLayout (context, layout);
			return;
		}

		context.Save ();
		context.MoveTo (width - 3, position + 3);
		context.Rotate (Math.PI / 2);
		PangoCairo.Functions.ShowLayout (context, layout);
		context.Restore ();
	}

	private static double GetRulerStep (double scale)
	{
		double rawStep = 60 / scale;
		double magnitude = Math.Pow (10, Math.Floor (Math.Log10 (rawStep)));
		double normalized = rawStep / magnitude;
		double multiplier = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
		return multiplier * magnitude;
	}

	private (double Scale, double Left, double Top) GetFramePreviewTransform (int? previewWidth = null, int? previewHeight = null, bool ignorePan = false)
	{
		int width = (int) canvas_width.Value;
		int height = (int) canvas_height.Value;
		int viewportWidth = previewWidth ?? frame_preview.GetWidth ();
		int viewportHeight = previewHeight ?? frame_preview.GetHeight ();
		double fitScale = GetPreviewScale (viewportWidth, viewportHeight, width, height);
		double scale = fitScale * preview_zoom;
		double left = (viewportWidth - width * scale) / 2;
		double top = (viewportHeight - height * scale) / 2;
		if (!ignorePan) {
			left += preview_pan_x;
			top += preview_pan_y;
		}
		return (scale, left, top);
	}

	private RectangleD GetPreviewBounds ()
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		return new RectangleD (left, top, canvas_width.Value * scale, canvas_height.Value * scale);
	}
}
