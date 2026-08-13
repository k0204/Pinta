//
// LayerActions.Render.cs
//

using System;
using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	public static ImageSurface RenderLayer (
		Document document,
		UserLayer layer,
		IProgress<double>? progress = null)
		=> RenderLayers (layer.GetLayersToPaint (), progress, useSurfaceBounds: true, out _);

	internal static ImageSurface RenderLayerContent (
		UserLayer layer,
		out PointD origin)
		=> RenderLayers (layer.GetLayersToPaint (), progress: null, useSurfaceBounds: false, out origin);

	public static ImageSurface RenderLayers (
		IEnumerable<Layer> layers,
		IProgress<double>? progress = null)
		=> RenderLayers (layers, progress, useSurfaceBounds: false, out _);

	public static ImageSurface RenderThumbnail (
		IEnumerable<Layer> layers,
		int width,
		int height)
	{
		List<Layer> paintLayers = [.. layers];
		ImageSurface image = CairoExtensions.CreateImageSurface (Format.Argb32, width, height);
		if (!TryGetRenderBounds (paintLayers, useSurfaceBounds: false, out RectangleD bounds))
			return image;

		double scale = Math.Min (width / bounds.Width, height / bounds.Height);
		double offsetX = (width - bounds.Width * scale) / 2;
		double offsetY = (height - bounds.Height * scale) / 2;
		using Context context = new (image);
		context.Translate (offsetX, offsetY);
		context.Scale (scale, scale);
		context.Translate (-bounds.X, -bounds.Y);
		foreach (Layer paintLayer in paintLayers)
			paintLayer.Draw (context);
		image.MarkDirty ();
		return image;
	}

	private static ImageSurface RenderLayers (
		IEnumerable<Layer> layers,
		IProgress<double>? progress,
		bool useSurfaceBounds,
		out PointD origin)
	{
		origin = PointD.Zero;
		List<Layer> paintLayers = [.. layers];
		if (paintLayers.Count == 0) {
			progress?.Report (1);
			return CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);
		}

		if (!TryGetRenderBounds (paintLayers, useSurfaceBounds, out RectangleD bounds)) {
			progress?.Report (1);
			return CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);
		}

		double originX = Math.Floor (bounds.Left);
		double originY = Math.Floor (bounds.Top);
		origin = new PointD (originX, originY);
		int width = GetRenderDimension (Math.Ceiling (bounds.Right) - originX, "width");
		int height = GetRenderDimension (Math.Ceiling (bounds.Bottom) - originY, "height");
		ImageSurface image = CairoExtensions.CreateImageSurface (Format.Argb32, width, height);
		progress?.Report (0.1);
		try {
			using Context context = new (image);
			context.Translate (-originX, -originY);
			for (int i = 0; i < paintLayers.Count; i++) {
				Layer paintLayer = paintLayers[i];
				paintLayer.Draw (context);
				progress?.Report (0.1 + 0.7 * (i + 1) / paintLayers.Count);
			}
			if (context.Status != Status.Success)
				throw new InvalidOperationException ($"Unable to render layer: {context.Status}");

			image.MarkDirty ();
			return image;
		} catch {
			image.Dispose ();
			throw;
		}
	}

	private static int GetRenderDimension (double value, string axis)
	{
		if (!double.IsFinite (value) || value <= 0 || value > int.MaxValue)
			throw new InvalidOperationException ($"Selected layer has an invalid {axis}.");

		return Math.Max (1, checked ((int) Math.Ceiling (value)));
	}

	private static bool ExpandBounds (
		Layer layer,
		bool useSurfaceBounds,
		ref double left,
		ref double top,
		ref double right,
		ref double bottom)
	{
		if (layer.Surface.Status != Status.Success)
			throw new InvalidOperationException ($"Layer surface is invalid: {layer.Surface.Status}");
		RectangleI contentBounds = useSurfaceBounds
			? layer.Surface.GetBounds ()
			: Utility.TryGetAlphaBounds (layer.Surface, out RectangleI alphaBounds)
				? alphaBounds
				: RectangleI.Zero;
		if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
			return false;

		PointD[] corners = [
			layer.Transform.TransformPoint (new PointD (contentBounds.X, contentBounds.Y)),
			layer.Transform.TransformPoint (new PointD (contentBounds.X + contentBounds.Width, contentBounds.Y)),
			layer.Transform.TransformPoint (new PointD (contentBounds.X, contentBounds.Y + contentBounds.Height)),
			layer.Transform.TransformPoint (new PointD (contentBounds.X + contentBounds.Width, contentBounds.Y + contentBounds.Height))];

		foreach (PointD corner in corners) {
			if (!double.IsFinite (corner.X) || !double.IsFinite (corner.Y))
				throw new InvalidOperationException ("Selected layer has an invalid transform.");

			left = Math.Min (left, corner.X);
			top = Math.Min (top, corner.Y);
			right = Math.Max (right, corner.X);
			bottom = Math.Max (bottom, corner.Y);
		}

		return true;
	}

	private static bool TryGetRenderBounds (
		IEnumerable<Layer> layers,
		bool useSurfaceBounds,
		out RectangleD bounds)
	{
		double left = double.PositiveInfinity;
		double top = double.PositiveInfinity;
		double right = double.NegativeInfinity;
		double bottom = double.NegativeInfinity;
		bool hasContent = false;
		foreach (Layer layer in layers)
			hasContent |= ExpandBounds (layer, useSurfaceBounds, ref left, ref top, ref right, ref bottom);

		bounds = hasContent
			? new RectangleD (left, top, right - left, bottom - top)
			: RectangleD.Zero;
		return hasContent;
	}
}
