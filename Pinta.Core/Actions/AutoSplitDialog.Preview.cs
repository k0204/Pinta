using System;
using System.Linq;
using Cairo;

namespace Pinta.Core;

internal sealed partial class AutoSplitDialog
{
	private const double preview_zoom_min = 0.25;
	private const double preview_zoom_max = 16;
	private double preview_zoom = 1;
	private double preview_pan_x;
	private double preview_pan_y;
	private double preview_pan_start_x;
	private double preview_pan_start_y;
	private bool preview_panning;
	private bool manual_dragging;
	private PointD manual_start_screen;
	private RectangleI? manual_preview;

	private bool HandlePreviewScroll (
		Gtk.EventControllerScroll _,
		Gtk.EventControllerScroll.ScrollSignalArgs args)
	{
		double delta = Math.Abs (args.Dy) >= Math.Abs (args.Dx) ? args.Dy : args.Dx;
		if (delta == 0)
			return false;

		ChangePreviewZoom (delta < 0 ? 1.1 : 1 / 1.1, preview_width / 2.0, preview_height / 2.0);
		return true;
	}

	private void ChangePreviewZoom (double factor)
		=> ChangePreviewZoom (factor, preview_width / 2.0, preview_height / 2.0);

	private void ChangePreviewZoom (double factor, double centerX, double centerY)
	{
		(double oldScale, double oldX, double oldY) = GetPreviewPlacement (preview_width, preview_height);
		if (oldScale <= 0)
			return;

		PointD imagePoint = new ((centerX - oldX) / oldScale, (centerY - oldY) / oldScale);
		preview_zoom = Math.Clamp (preview_zoom * factor, preview_zoom_min, preview_zoom_max);
		(double newScale, double centeredX, double centeredY) = GetPreviewPlacement (
			preview_width,
			preview_height,
			ignorePan: true);
		if (newScale > 0) {
			preview_pan_x = centerX - imagePoint.X * newScale - centeredX;
			preview_pan_y = centerY - imagePoint.Y * newScale - centeredY;
		}

		ClampPreviewPan ();
		preview.QueueDraw ();
	}

	private void ResetPreviewView ()
	{
		preview_zoom = 1;
		preview_pan_x = 0;
		preview_pan_y = 0;
		preview.QueueDraw ();
	}

	private void BeginPreviewPan ()
	{
		preview_pan_start_x = preview_pan_x;
		preview_pan_start_y = preview_pan_y;
		preview_panning = true;
	}

	private void UpdatePreviewPan (double offsetX, double offsetY)
	{
		if (!preview_panning)
			return;

		preview_pan_x = preview_pan_start_x + offsetX;
		preview_pan_y = preview_pan_start_y + offsetY;
		ClampPreviewPan ();
		preview.QueueDraw ();
	}

	private void EndPreviewPan ()
	{
		preview_panning = false;
	}

	private void BeginManualDrag (double x, double y)
	{
		if (detection_mode.Active != 2 || analysis_running)
			return;

		manual_dragging = true;
		manual_start_screen = new PointD (x, y);
		manual_preview = null;
		preview.QueueDraw ();
	}

	private void UpdateManualDrag (double offsetX, double offsetY)
	{
		if (!manual_dragging)
			return;

		PointD end = new (manual_start_screen.X + offsetX, manual_start_screen.Y + offsetY);
		manual_preview = CreatePreviewBounds (end);
		preview.QueueDraw ();
	}

	private void EndManualDrag (double offsetX, double offsetY)
	{
		if (!manual_dragging)
			return;

		manual_dragging = false;
		PointD end = new (manual_start_screen.X + offsetX, manual_start_screen.Y + offsetY);
		RectangleI bounds = CreatePreviewBounds (end);
		manual_preview = null;
		if (TryNormalizeBounds (bounds, out RectangleI normalized)) {
			regions.Add (new AutoSplitRegion (normalized));
			SelectRegion (regions.Count - 1);
			RefreshRegionList ();
			status_label.RemoveCssClass (AdwaitaStyles.Error);
			status_label.SetText (Translations.GetString ("Added a manual region."));
			UpdateActionState ();
		}
		preview.QueueDraw ();
	}

	private void DrawPreview (Gtk.DrawingArea _, Context context, int width, int height)
	{
		if (width <= 0 || height <= 0)
			return;

		context.SetSourceColor (new Color (0.96, 0.96, 0.96));
		context.Paint ();
		DrawCheckerboard (context, width, height);
		(double scale, double imageX, double imageY) = GetPreviewPlacement (width, height);
		if (scale <= 0)
			return;

		context.Save ();
		context.Translate (imageX, imageY);
		context.Scale (scale, scale);
		context.SetSourceSurface (source.Surface, 0, 0);
		context.Paint ();
		context.Restore ();

		DrawPreviewOutline (context, imageX, imageY, scale);
		for (int index = 0; index < regions.Count; index++)
			DrawRegion (
				context,
				regions[index].Bounds,
				selected_regions.Contains (index),
				index == selected_region,
				imageX,
				imageY,
				scale);
		if (manual_preview is RectangleI bounds)
			DrawRegion (context, bounds, true, true, imageX, imageY, scale);
	}

	private static void DrawCheckerboard (Context context, int width, int height)
	{
		const int cell = 16;
		for (int y = 0; y < height; y += cell)
			for (int x = 0; x < width; x += cell)
				if ((x / cell + y / cell) % 2 == 0) {
					context.SetSourceColor (new Color (0.86, 0.87, 0.88));
					context.Rectangle (x, y, cell, cell);
					context.Fill ();
				}
	}

	private void DrawPreviewOutline (Context context, double imageX, double imageY, double scale)
	{
		context.SetSourceColor (new Color (0.25, 0.28, 0.32));
		context.LineWidth = 1;
		context.Rectangle (imageX, imageY, source.Surface.Width * scale, source.Surface.Height * scale);
		context.Stroke ();
	}

	private static void DrawRegion (
		Context context,
		RectangleI bounds,
		bool selected,
		bool current,
		double imageX,
		double imageY,
		double scale)
	{
		double x = imageX + bounds.X * scale;
		double y = imageY + bounds.Y * scale;
		double width = bounds.Width * scale;
		double height = bounds.Height * scale;
		context.Save ();
		context.SetSourceColor (new Color (selected ? 1.0 : 0.15, selected ? 0.67 : 0.72, 0.08, 0.16));
		context.Rectangle (x, y, width, height);
		context.FillPreserve ();
		context.SetSourceColor (new Color (selected ? 1.0 : 0.12, selected ? 0.67 : 0.63, 0.08, 0.95));
		context.LineWidth = current ? 3 : selected ? 2.5 : 1.5;
		context.Stroke ();
		context.Restore ();
	}

	private (double Scale, double X, double Y) GetPreviewPlacement (
		int width,
		int height,
		bool ignorePan = false)
	{
		if (source.Surface.Width <= 0 || source.Surface.Height <= 0)
			return (0, 0, 0);

		double fitScale = Math.Min (
			Math.Max (1, width - 24) / (double) source.Surface.Width,
			Math.Max (1, height - 24) / (double) source.Surface.Height);
		double scale = fitScale * preview_zoom;
		double x = (width - source.Surface.Width * scale) / 2;
		double y = (height - source.Surface.Height * scale) / 2;
		if (!ignorePan) {
			x += preview_pan_x;
			y += preview_pan_y;
		}
		return (
			scale,
			x,
			y);
	}

	private void ClampPreviewPan ()
	{
		(double scale, _, _) = GetPreviewPlacement (preview_width, preview_height, ignorePan: true);
		if (scale <= 0)
			return;

		preview_pan_x = ClampPan (preview_pan_x, source.Surface.Width * scale, preview_width);
		preview_pan_y = ClampPan (preview_pan_y, source.Surface.Height * scale, preview_height);
	}

	private static double ClampPan (double pan, double contentSize, int viewportSize)
	{
		if (contentSize <= viewportSize)
			return 0;

		double centeredOffset = (viewportSize - contentSize) / 2;
		return Math.Clamp (pan, centeredOffset, -centeredOffset);
	}

	private void HandlePreviewClick (Gtk.GestureClick controller, double x, double y)
	{
		Gdk.ModifierType state = controller.GetCurrentEventState ();
		bool additive = state.IsControlPressed () || state.IsShiftPressed ();
		bool toggle = state.IsControlPressed ();
		if (!TryGetImagePoint (x, y, out PointD imagePoint)) {
			if (!additive)
				SelectRegion (-1);
			return;
		}

		int hit = FindRegionAtPoint (imagePoint, additive);
		if (hit < 0) {
			if (!additive)
				SelectRegion (-1);
			return;
		}

		if (!additive) {
			SelectRegion (hit);
			return;
		}

		if (toggle && selected_regions.Contains (hit))
			selected_regions.Remove (hit);
		else
			selected_regions.Add (hit);
		selected_region = selected_regions.Contains (hit)
			? hit
			: selected_regions.FirstOrDefault (-1);
		UpdateEditorValues ();
		UpdateActionState ();
		SyncRegionListSelection ();
		preview.QueueDraw ();
	}

	private int FindRegionAtPoint (PointD imagePoint, bool preferUnselected)
	{
		for (int index = regions.Count - 1; index >= 0; index--)
			if (Contains (regions[index].Bounds, imagePoint)
				&& (!preferUnselected || !selected_regions.Contains (index)))
				return index;

		if (!preferUnselected)
			return -1;

		for (int index = regions.Count - 1; index >= 0; index--)
			if (Contains (regions[index].Bounds, imagePoint))
				return index;

		return -1;
	}

	private static bool Contains (RectangleI bounds, PointD point)
		=> point.X >= bounds.X && point.Y >= bounds.Y
			&& point.X < bounds.X + bounds.Width
			&& point.Y < bounds.Y + bounds.Height;

	private bool TryGetImagePoint (double x, double y, out PointD imagePoint)
	{
		(double scale, double imageX, double imageY) = GetPreviewPlacement (preview_width, preview_height);
		if (scale <= 0 || x < imageX || y < imageY
			|| x > imageX + source.Surface.Width * scale
			|| y > imageY + source.Surface.Height * scale) {
			imagePoint = PointD.Zero;
			return false;
		}

		imagePoint = new PointD ((x - imageX) / scale, (y - imageY) / scale);
		return true;
	}

	private PointD ToImagePoint (double x, double y)
	{
		(double scale, double imageX, double imageY) = GetPreviewPlacement (preview_width, preview_height);
		if (scale <= 0)
			return PointD.Zero;

		return new PointD (
			Math.Clamp ((int) Math.Round ((x - imageX) / scale), 0, source.Surface.Width),
			Math.Clamp ((int) Math.Round ((y - imageY) / scale), 0, source.Surface.Height));
	}

	private RectangleI CreatePreviewBounds (PointD end)
	{
		PointD start = ToImagePoint (manual_start_screen.X, manual_start_screen.Y);
		PointD finish = ToImagePoint (end.X, end.Y);
		return RectangleI.FromPoints (
			new PointI ((int) start.X, (int) start.Y),
			new PointI ((int) finish.X, (int) finish.Y));
	}
}
