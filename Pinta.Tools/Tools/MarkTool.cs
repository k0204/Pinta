using System;
using System.Collections.Generic;
using Cairo;
using Gtk;
using Pinta.Core;

namespace Pinta.Tools;

public sealed class MarkTool : BaseBrushTool, IMarkTool
{
	private readonly Gtk.ComboBoxText shape_combo = Gtk.ComboBoxText.New ();
	private readonly List<PointD> polygon_points = [];
	private PointD start_point;
	private bool drawing;
	private bool preview_has_content;

	public MarkTool (IServiceProvider services) : base (services)
	{
		DefaultCursor = Gdk.Cursor.NewFromTexture (Resources.GetIcon ("Cursor.Rectangle.png"), 9, 18, null);
	}

	public override string Name => Translations.GetString ("Mark");
	public override string Icon => Pinta.Resources.Icons.ToolRectangle;
	public override string StatusBarText => Translations.GetString (
		"Drag to draw a rectangle or circle. Drag around the outline to draw a polygon.");
	public override Gdk.Cursor DefaultCursor { get; }
	public override int Priority => 46;
	public int CurrentShape => shape_combo.Active;

	public event EventHandler? ShapeChanged;

	protected override void OnBuildToolBar (Box toolbar)
	{
		shape_combo.AppendText (Translations.GetString ("Rectangle"));
		shape_combo.AppendText (Translations.GetString ("Circle"));
		shape_combo.AppendText (Translations.GetString ("Polygon"));
		shape_combo.Active = Settings.GetSetting (SettingNames.MARK_SHAPE, 0);
		shape_combo.OnChanged += (_, _) => {
			SetCursor (DefaultCursor);
			ShapeChanged?.Invoke (this, EventArgs.Empty);
		};
		toolbar.Append (shape_combo);
		base.OnBuildToolBar (toolbar);
	}

	public void SetShape (int shape)
	{
		shape_combo.Active = Math.Clamp (shape, 0, 2);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		if (e.MouseButton != MouseButton.Left || drawing)
			return;

		drawing = true;
		preview_has_content = false;
		start_point = document.ClampToImageSize (e.PointDouble);
		polygon_points.Clear ();
		polygon_points.Add (start_point);
		document.Layers.ToolLayer.Clear ();
		document.Layers.ToolLayer.Hidden = false;
		base.OnMouseDown (document, e);
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		if (!drawing || !e.IsLeftMousePressed)
			return;

		PointD point = document.ClampToImageSize (e.PointDouble);
		if (IsPolygon && (polygon_points.Count == 0 || point != polygon_points[^1]))
			polygon_points.Add (point);

		DrawPreview (document, point);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (!drawing)
			return;

		PointD point = document.ClampToImageSize (e.PointDouble);
		if (IsPolygon && polygon_points.Count > 1)
			polygon_points[^1] = point;
		DrawPreview (document, point, closePolygon: IsPolygon);

		if (preview_has_content) {
			using Context context = document.CreateClippedContext ();
			document.Layers.ToolLayer.Draw (context);
			surface_modified = true;
		}

		FinishPreview (document);
		base.OnMouseUp (document, e);
		drawing = false;
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		if (document is not null && drawing) {
			CancelPreview (document);
			base.OnMouseUp (document, new ToolMouseEventArgs { MouseButton = MouseButton.Left });
		}
		drawing = false;
		base.OnDeactivated (document, newTool);
	}

	protected override void OnCommit (Document? document)
	{
		if (document is not null && drawing) {
			CancelPreview (document);
			base.OnMouseUp (document, new ToolMouseEventArgs { MouseButton = MouseButton.Left });
		}
		drawing = false;
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);
		settings.PutSetting (SettingNames.MARK_SHAPE, shape_combo.Active);
	}

	private bool IsPolygon => shape_combo.Active == 2;

	private void DrawPreview (Document document, PointD end, bool closePolygon = false)
	{
		document.Layers.ToolLayer.Clear ();
		using Context context = document.CreateClippedToolContext ();
		context.Antialias = UseAntialiasing ? Antialias.Subpixel : Antialias.None;
		context.SetSourceColor (Palette.PrimaryColor);
		context.LineWidth = BrushWidth;
		context.LineJoin = LineJoin.Round;
		context.LineCap = LineCap.Round;

		if (IsPolygon)
			DrawPolygon (context, closePolygon);
		else if (shape_combo.Active == 1)
			DrawEllipse (context, GetCircleBounds (end));
		else
			DrawRectangle (context, RectangleD.FromPoints (start_point, end));

		preview_has_content = true;
		document.Workspace.Invalidate ();
	}

	private RectangleD GetCircleBounds (PointD end)
	{
		double size = Math.Min (Math.Abs (end.X - start_point.X), Math.Abs (end.Y - start_point.Y));
		double x = end.X >= start_point.X ? start_point.X : start_point.X - size;
		double y = end.Y >= start_point.Y ? start_point.Y : start_point.Y - size;
		return new RectangleD (x, y, size, size);
	}

	private void DrawRectangle (Context context, RectangleD bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0)
			return;

		context.Rectangle (bounds.X, bounds.Y, bounds.Width, bounds.Height);
		context.Stroke ();
	}

	private static void DrawEllipse (Context context, RectangleD bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0)
			return;

		context.Save ();
		context.Translate (bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
		context.Scale (bounds.Width / 2, bounds.Height / 2);
		context.Arc (0, 0, 1, 0, Math.PI * 2);
		context.Restore ();
		context.Stroke ();
	}

	private void DrawPolygon (Context context, bool closePolygon)
	{
		if (polygon_points.Count < 2)
			return;

		context.MoveTo (polygon_points[0].X, polygon_points[0].Y);
		for (int index = 1; index < polygon_points.Count; index++)
			context.LineTo (polygon_points[index].X, polygon_points[index].Y);
		if (closePolygon)
			context.ClosePath ();
		context.Stroke ();
	}

	private void FinishPreview (Document document)
	{
		document.Layers.ToolLayer.Clear ();
		document.Layers.ToolLayer.Hidden = true;
		polygon_points.Clear ();
		preview_has_content = false;
		document.Workspace.Invalidate ();
	}

	private void CancelPreview (Document document)
	{
		FinishPreview (document);
		surface_modified = false;
		undo_surface = null;
		undo_transform = null;
	}
}
