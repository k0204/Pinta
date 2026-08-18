//
// LayerBlendTool.cs
//

using System;
using Cairo;
using Gtk;
using Pinta.Core;

namespace Pinta.Tools;

public sealed class LayerBlendTool : BaseBrushTool
{
	private readonly IWorkspaceService workspace;
	private readonly Gtk.Scale blend_slider;
	private ImageSurface? source_surface;
	private ImageSurface? base_surface;
	private PointI? last_point;
	private double blend_percent = 100;

	public LayerBlendTool (IServiceProvider services) : base (services)
	{
		workspace = services.GetService<IWorkspaceService> ();
		blend_slider = GtkExtensions.CreateToolBarSlider (0, 100, 1, 100);
		blend_slider.TooltipText = Translations.GetString ("Lower layer blend percentage.");
		blend_slider.OnValueChanged += (_, _) => {
			blend_percent = blend_slider.GetValue ();
		};
	}

	public override string Name => Translations.GetString ("Blend Lower into Upper");
	public override string Icon => Pinta.Resources.Icons.ToolPaintBrush;
	public override string StatusBarText => Translations.GetString ("Paint over the canvas to blend the lower layer into the selected layer.");
	public override Gdk.Key ShortcutKey => Gdk.Key.Invalid;
	public override int Priority => 22;

	public override Gdk.Cursor DefaultCursor
	{
		get
		{
			double scale = workspace.GetScale ();
			var icon = GdkExtensions.CreateIconWithShape (
				"Cursor.Paintbrush.png",
				CursorShape.Ellipse,
				scale,
				BrushWidth,
				8,
				24,
				out int iconOffsetX,
				out int iconOffsetY);
			return Gdk.Cursor.NewFromTexture (icon, iconOffsetX, iconOffsetY, null);
		}
	}

	protected override void OnBuildToolBar (Box toolbar)
	{
		base.OnBuildToolBar (toolbar);
		toolbar.Append (Label.New (string.Format (" {0}: ", Translations.GetString ("Blend (%)"))));
		toolbar.Append (blend_slider);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		if (e.MouseButton != MouseButton.Left)
			return;

		UserLayer upper = document.Layers.CurrentUserLayer;
		if (!TryBuildSourceSurface (document, upper, out ImageSurface? source))
			return;

		source_surface?.Dispose ();
		source_surface = source;
		base_surface?.Dispose ();
		base_surface = null;
		last_point = null;
		document.Layers.ToolLayer.Clear ();
		document.Layers.ToolLayer.Hidden = true;

		base.OnMouseDown (document, e);
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		if (mouse_button != MouseButton.Left || source_surface is null)
			return;

		if (base_surface is null)
			base_surface = document.Layers.CurrentUserLayer.Surface.Clone ();

		PointI previousPoint = last_point ?? e.Point;
		PointD current = e.PointDouble;
		PointD previous = (PointD) previousPoint;
		using (Context mask = new (document.Layers.ToolLayer.Surface)) {
			document.Selection.Clip (mask);
			mask.Antialias = UseAntialiasing ? Antialias.Subpixel : Antialias.None;
			mask.LineWidth = BrushWidth;
			mask.LineCap = LineCap.Round;
			mask.LineJoin = LineJoin.Round;
			mask.Operator = Operator.Over;
			mask.SetSourceColor (new Color (1, 1, 1, blend_percent / 100.0));
			mask.MoveTo (previous.X + 0.5, previous.Y + 0.5);
			mask.LineTo (current.X + 0.5, current.Y + 0.5);
			mask.Stroke ();
		}

		ApplyPreview (document);
		last_point = e.Point;
		surface_modified |= document.Workspace.PointInCanvas (e.PointDouble);
		RectangleI dirty = RectangleI.FromPoints (previousPoint, e.Point).Inflated (BrushWidth + 2, BrushWidth + 2);
		workspace.Invalidate (document.ClampToImageSize (dirty));
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (mouse_button != MouseButton.Left)
			return;

		document.Layers.ToolLayer.Hidden = true;
		base.OnMouseUp (document, e);
		base_surface?.Dispose ();
		base_surface = null;
		source_surface?.Dispose ();
		source_surface = null;
		last_point = null;
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		if (document is not null)
			document.Layers.ToolLayer.Hidden = true;
		source_surface?.Dispose ();
		source_surface = null;
		base_surface?.Dispose ();
		base_surface = null;
		base.OnDeactivated (document, newTool);
	}

	private static bool TryBuildSourceSurface (Document document, UserLayer upper, out ImageSurface? source)
	{
		source = null;
		if (!document.Layers.CanMoveCurrentLayerDown ())
			return false;

		UserLayer lower = document.Layers.GetSiblingBelow (upper);
		if (lower.Hidden)
			return false;

		ImageSurface result = CairoExtensions.CreateImageSurface (
			Format.Argb32,
			document.ImageSize.Width,
			document.ImageSize.Height);
		using (Context context = new (result))
			foreach (Layer layer in lower.GetLayersToPaintTree ())
				layer.Draw (context);

		result.MarkDirty ();
		source = result;
		return true;
	}

	private void ApplyPreview (Document document)
	{
		if (source_surface is null || base_surface is null)
			return;

		UserLayer upper = document.Layers.CurrentUserLayer;
		using Context context = new (upper.Surface);
		context.Operator = Operator.Source;
		context.SetSourceSurface (base_surface, 0, 0);
		context.Paint ();

		context.Operator = Operator.Over;
		UserLayer lower = document.Layers.GetSiblingBelow (upper);
		context.SetBlendMode (lower.BlendMode);
		context.SetSourceSurface (source_surface, 0, 0);
		context.MaskSurface (document.Layers.ToolLayer.Surface, 0, 0);
		upper.Surface.MarkDirty ();
	}
}
