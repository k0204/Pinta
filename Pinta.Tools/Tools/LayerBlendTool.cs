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
	private UserLayer? target_layer;
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
	public override string Icon => Pinta.Resources.Icons.LayerMergeDown;
	public override string StatusBarText => Translations.GetString ("Paint over the canvas to blend the lower layer into the selected layer.");
	public override bool RequiresEditableLayer => false;
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

		UserLayer lower = document.Layers.CurrentUserLayer;
		if (!document.Layers.TryGetIntersectingLayerPair (lower, out UserLayer sourceLayer, out UserLayer targetLayer)
			|| !TryBuildSourceSurface (document, sourceLayer, targetLayer, out ImageSurface? source))
			return;

		source_surface?.Dispose ();
		source_surface = source;
		target_layer = targetLayer;
		base_surface?.Dispose ();
		base_surface = null;
		last_point = null;
		document.Layers.ToolLayer.Clear ();
		document.Layers.ToolLayer.Hidden = true;

		undo_surface = targetLayer.Surface.Clone ();
		undo_transform = targetLayer.Transform.Clone ();
		ExpandLayerToCanvas (document, targetLayer);
		base_surface = targetLayer.Surface.Clone ();
		surface_modified = false;
		mouse_button = e.MouseButton;
		OnMouseMove (document, e);
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		if (mouse_button != MouseButton.Left || source_surface is null)
			return;

		PointI previousPoint = last_point ?? e.Point;
		PointD current = e.PointDouble;
		PointD previous = (PointD) previousPoint;
		double fadeTop = Math.Min (previous.Y, current.Y) - BrushWidth / 2.0;
		double fadeBottom = Math.Max (previous.Y, current.Y) + BrushWidth / 2.0;
		using (Context mask = new (document.Layers.ToolLayer.Surface)) {
			document.Selection.Clip (mask);
			mask.Antialias = UseAntialiasing ? Antialias.Subpixel : Antialias.None;
			mask.LineWidth = BrushWidth;
			mask.LineCap = LineCap.Round;
			mask.LineJoin = LineJoin.Round;
			mask.Operator = Operator.Over;
			using LinearGradient gradient = new (0, fadeTop, 0, fadeBottom);
			gradient.AddColorStop (0, new Color (1, 1, 1, 0));
			gradient.AddColorStop (1, new Color (1, 1, 1, blend_percent / 100.0));
			mask.SetSource (gradient);
			mask.MoveTo (previous.X + 0.5, previous.Y + 0.5);
			mask.LineTo (current.X + 0.5, current.Y + 0.5);
			mask.Stroke ();
		}

		// The lower layer may only replace pixels that already exist in the
		// upper layer. Keep the brush mask inside the original upper alpha.
		ImageSurface upperSurface = base_surface!;
		using (Context mask = new (document.Layers.ToolLayer.Surface)) {
			mask.Operator = Operator.DestIn;
			mask.SetSourceSurface (upperSurface, 0, 0);
			mask.Paint ();
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
		if (undo_surface is not null && target_layer is not null) {
			if (surface_modified)
				document.History.PushNewItem (new SimpleHistoryItem (Icon, Name, undo_surface, target_layer, undo_transform!));
			else {
				target_layer.Surface = undo_surface;
				target_layer.Transform = undo_transform!;
			}
		}

		surface_modified = false;
		undo_surface = null;
		undo_transform = null;
		mouse_button = MouseButton.None;
		base_surface?.Dispose ();
		base_surface = null;
		source_surface?.Dispose ();
		source_surface = null;
		target_layer = null;
		last_point = null;
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		if (document is not null)
			document.Layers.ToolLayer.Hidden = true;
		source_surface?.Dispose ();
		source_surface = null;
		target_layer = null;
		base_surface?.Dispose ();
		base_surface = null;
		base.OnDeactivated (document, newTool);
	}

	private static bool TryBuildSourceSurface (
		Document document,
		UserLayer lower,
		UserLayer upper,
		out ImageSurface? source)
	{
		source = null;

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

		UserLayer? upper = target_layer;
		if (upper is null)
			return;
		using Context context = new (upper.Surface);
		context.Operator = Operator.Source;
		context.SetSourceSurface (base_surface, 0, 0);
		context.Paint ();

		// source_surface already contains the lower layer tree rendered with its
		// own blend modes, so its pixels must be composited over the upper layer.
		context.Operator = Operator.Over;
		context.SetSourceSurface (source_surface, 0, 0);
		context.MaskSurface (document.Layers.ToolLayer.Surface, 0, 0);
		upper.Surface.MarkDirty ();
	}
}
