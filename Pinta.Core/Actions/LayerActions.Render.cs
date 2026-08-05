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
	{
		List<Layer> paintLayers = [.. layer.GetLayersToPaint ()];
		if (paintLayers.Count == 0) {
			progress?.Report (1);
			return CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);
		}

		double left = double.PositiveInfinity;
		double top = double.PositiveInfinity;
		double right = double.NegativeInfinity;
		double bottom = double.NegativeInfinity;
		foreach (Layer paintLayer in paintLayers)
			ExpandBounds (paintLayer, ref left, ref top, ref right, ref bottom);

		double originX = Math.Floor (left);
		double originY = Math.Floor (top);
		int width = GetRenderDimension (Math.Ceiling (right) - originX, "width");
		int height = GetRenderDimension (Math.Ceiling (bottom) - originY, "height");
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

	private static void ExpandBounds (
		Layer layer,
		ref double left,
		ref double top,
		ref double right,
		ref double bottom)
	{
		if (layer.Surface.Status != Status.Success)
			throw new InvalidOperationException ($"Layer surface is invalid: {layer.Surface.Status}");

		PointD[] corners = [
			layer.Transform.TransformPoint (new PointD (0, 0)),
			layer.Transform.TransformPoint (new PointD (layer.Surface.Width, 0)),
			layer.Transform.TransformPoint (new PointD (0, layer.Surface.Height)),
			layer.Transform.TransformPoint (new PointD (layer.Surface.Width, layer.Surface.Height))];

		foreach (PointD corner in corners) {
			if (!double.IsFinite (corner.X) || !double.IsFinite (corner.Y))
				throw new InvalidOperationException ("Selected layer has an invalid transform.");

			left = Math.Min (left, corner.X);
			top = Math.Min (top, corner.Y);
			right = Math.Max (right, corner.X);
			bottom = Math.Max (bottom, corner.Y);
		}
	}
}
