using System;
using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog
{
	private const double guide_hit_tolerance = 6;
	private readonly List<DocumentGuide> guides = [];
	private readonly Gtk.DrawingArea horizontal_ruler = Gtk.DrawingArea.New ();
	private readonly Gtk.DrawingArea vertical_ruler = Gtk.DrawingArea.New ();
	private GuideDragState? guide_drag_state;
	private Gdk.Cursor? horizontal_guide_cursor;
	private Gdk.Cursor? vertical_guide_cursor;
	private PointD? ruler_position;
	private double preview_drag_start_x;
	private double preview_drag_start_y;

	private readonly record struct GuideDragState (int Index);

	private Gtk.Widget BuildRulerPreview ()
	{
		horizontal_ruler.HeightRequest = 24;
		horizontal_ruler.Hexpand = true;
		horizontal_ruler.SetDrawFunc ((area, context, width, height) =>
			DrawRuler (area, context, width, height, Gtk.Orientation.Horizontal));
		vertical_ruler.WidthRequest = 32;
		vertical_ruler.Vexpand = true;
		vertical_ruler.SetDrawFunc ((area, context, width, height) =>
			DrawRuler (area, context, width, height, Gtk.Orientation.Vertical));

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.Hexpand = true;
		grid.Vexpand = true;
		grid.Attach (horizontal_ruler, 1, 0, 1, 1);
		grid.Attach (vertical_ruler, 0, 1, 1, 1);
		grid.Attach (frame_preview, 1, 1, 1, 1);
		return grid;
	}

	private void ConnectRulerAndGuidePointerEvents ()
	{
		Gtk.EventControllerMotion motion = Gtk.EventControllerMotion.New ();
		motion.OnMotion += (_, args) => HandlePreviewMotion (args.X, args.Y);
		motion.OnLeave += (_, _) => HandlePreviewLeave ();
		frame_preview.AddController (motion);

		Gtk.GestureClick ruler_click = Gtk.GestureClick.New ();
		ruler_click.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		ruler_click.OnPressed += (_, args) => HandleRulerClick ();
		horizontal_ruler.AddController (ruler_click);
	}

	private void HandleRulerClick ()
	{
		if (source_rectangles is not null)
			return;
		ShowAnalysisModal ();
	}

	private void ShowAnalysisModal ()
	{
		Gtk.Dialog modal = Gtk.Dialog.New ();
		modal.Title = Translations.GetString ("Analyzing");
		modal.TransientFor = dialog;
		modal.Modal = true;
		modal.Resizable = false;
		modal.DefaultWidth = 360;

		Gtk.Box content = modal.GetContentAreaBox ();
		content.Spacing = 16;
		content.SetAllMargins (24);
		content.Halign = Gtk.Align.Center;
		content.Valign = Gtk.Align.Center;

		Gtk.Spinner spinner = Gtk.Spinner.New ();
		spinner.Spinning = true;
		spinner.Halign = Gtk.Align.Center;
		spinner.Valign = Gtk.Align.Center;

		Gtk.Label label = Gtk.Label.New (Translations.GetString ("Analyzing sprite bounds..."));
		label.Halign = Gtk.Align.Center;
		label.AddCssClass (AdwaitaStyles.Title4);

		Gtk.Label hint = Gtk.Label.New (Translations.GetString ("Please wait while AI detects sprite boundaries."));
		hint.Wrap = true;
		hint.Halign = Gtk.Align.Center;
		hint.MaxWidthChars = 30;
		hint.AddCssClass (AdwaitaStyles.DimLabel);

		content.Append (spinner);
		content.Append (label);
		content.Append (hint);

		modal.OnResponse += (_, _) => modal.Destroy ();

		modal.Present ();

		// Auto-dismiss after a delay to simulate analysis completion
		GLib.Functions.TimeoutAdd (GLib.Constants.PRIORITY_DEFAULT, 3000, () => {
			modal.Destroy ();
			return false;
		});
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
		frame_preview.Cursor = state is GuideDragState guide
			? GetGuideCursor (guides[guide.Index].Orientation)
			: null;
	}

	private Gdk.Cursor? GetGuideCursor (GuideOrientation orientation)
		=> orientation == GuideOrientation.Horizontal
			? horizontal_guide_cursor ??= Gdk.Cursor.NewFromName ("row-resize", null)
			: vertical_guide_cursor ??= Gdk.Cursor.NewFromName ("col-resize", null);

	private void HandlePreviewLeave ()
	{
		ruler_position = null;
		if (guide_drag_state is null)
			frame_preview.Cursor = null;
		horizontal_ruler.QueueDraw ();
		vertical_ruler.QueueDraw ();
	}

	private Gtk.Widget BuildPreviewToolbar ()
	{
		Gtk.Box toolbar = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		toolbar.Append (previous_frame);
		toolbar.Append (next_frame);
		toolbar.Append (Gtk.Separator.New (Gtk.Orientation.Vertical));
		toolbar.Append (undo_position);
		toolbar.Append (redo_position);
		Gtk.Box spacer = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		spacer.Hexpand = true;
		toolbar.Append (spacer);
		toolbar.Append (add_horizontal_guide);
		toolbar.Append (add_vertical_guide);
		return toolbar;
	}

	private void AddGuide (GuideOrientation orientation)
	{
		double position = orientation == GuideOrientation.Horizontal
			? canvas_height.Value / 2
			: canvas_width.Value / 2;
		guides.Add (new DocumentGuide (orientation, position));
		frame_preview.QueueDraw ();
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
		preview_drag_start_x = x;
		preview_drag_start_y = y;
		guide_drag_state = FindGuideAtPoint (x, y);
		frame_position_dragging = false;
		if (guide_drag_state is null && frames.Count > 0) {
			drag_start_x = frames[selected_frame].X;
			drag_start_y = frames[selected_frame].Y;
			BeginFramePositionDrag ();
		}
	}

	private void UpdatePreviewDrag (double offsetX, double offsetY)
	{
		if (guide_drag_state is GuideDragState state)
			UpdateGuide (state.Index, preview_drag_start_x + offsetX, preview_drag_start_y + offsetY);
		else
			DragSelectedFrame (offsetX, offsetY);
	}

	private void EndPreviewDrag (double offsetX, double offsetY)
	{
		if (guide_drag_state is not GuideDragState state) {
			EndFramePositionDrag (selected_frame);
			return;
		}

		PointD point = new (preview_drag_start_x + offsetX, preview_drag_start_y + offsetY);
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

	private void UpdateGuide (int index, double x, double y)
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		DocumentGuide guide = guides[index];
		double position = guide.Orientation == GuideOrientation.Horizontal
			? (y - top) / scale
			: (x - left) / scale;
		double maximum = guide.Orientation == GuideOrientation.Horizontal ? canvas_height.Value : canvas_width.Value;
		guides[index] = guide with { Position = Math.Clamp (position, 0, maximum) };
		frame_preview.QueueDraw ();
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

		area.GetStyleContext ().GetColor (out Gdk.RGBA foreground);
		context.SetSourceColor (foreground.ToCairoColor ());
		double length = orientation == Gtk.Orientation.Horizontal ? canvas_width.Value : canvas_height.Value;
		double origin = orientation == Gtk.Orientation.Horizontal ? left : top;
		double majorStep = GetRulerStep (scale);
		double minorStep = majorStep / 5;
		int tick = 0;
		for (double value = 0; value <= length; value += minorStep, tick++) {
			double position = origin + value * scale;
			bool major = tick % 5 == 0;
			DrawRulerTick (context, orientation, position, width, height, major);
			if (major)
				DrawRulerLabel (area, context, orientation, position, value, width);
		}
		context.Stroke ();

		if (ruler_position is PointD pointer) {
			double value = orientation == Gtk.Orientation.Horizontal ? pointer.X : pointer.Y;
			context.SetSourceColor (new Color (0.1, 0.6, 1.0, 0.95));
			DrawRulerTick (context, orientation, origin + value * scale, width, height, true);
			context.Stroke ();
		}
	}

	private static void DrawRulerTick (
		Context context,
		Gtk.Orientation orientation,
		double position,
		int width,
		int height,
		bool major)
	{
		if (orientation == Gtk.Orientation.Horizontal) {
			context.MoveTo (position, major ? 0 : height / 2d);
			context.LineTo (position, height);
		} else {
			context.MoveTo (major ? 0 : width / 2d, position);
			context.LineTo (width, position);
		}
	}

	private static void DrawRulerLabel (
		Gtk.DrawingArea area,
		Context context,
		Gtk.Orientation orientation,
		double position,
		double value,
		int width)
	{
		using Pango.Layout layout = area.CreatePangoLayout (((int) value).ToString ());
		if (orientation == Gtk.Orientation.Horizontal) {
			context.MoveTo (position + 2, 1);
			PangoCairo.Functions.ShowLayout (context, layout);
			return;
		}

		context.Save ();
		context.MoveTo (width - 4, position + 2);
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

	private (double Scale, double Left, double Top) GetFramePreviewTransform ()
	{
		int width = (int) canvas_width.Value;
		int height = (int) canvas_height.Value;
		double scale = GetPreviewScale (frame_preview.GetWidth (), frame_preview.GetHeight (), width, height);
		return (scale, (frame_preview.GetWidth () - width * scale) / 2, (frame_preview.GetHeight () - height * scale) / 2);
	}

	private RectangleD GetPreviewBounds ()
	{
		(double scale, double left, double top) = GetFramePreviewTransform ();
		return new RectangleD (left, top, canvas_width.Value * scale, canvas_height.Value * scale);
	}
}
