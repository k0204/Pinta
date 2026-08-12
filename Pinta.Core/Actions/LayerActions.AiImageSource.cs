//
// LayerActions.AiImageSource.cs
//

using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private readonly record struct AiLayerImage (byte[] Png, Size Size, PointD Origin);

	private static ImageSurface CreateLayerSurface (UserLayer source)
	{
		ImageSurface result = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			source.Surface.Width,
			source.Surface.Height);
		Matrix inverse = source.Transform.Clone ();
		if (inverse.Invert () != Cairo.Status.Success) {
			result.Dispose ();
			return source.Surface.Clone ();
		}

		try {
			using Cairo.Context context = new (result);
			context.Transform (inverse);
			foreach (Layer layer in source.GetLayersToPaint ())
				layer.Draw (context);
			result.MarkDirty ();
			return result;
		} catch {
			result.Dispose ();
			throw;
		}
	}

	private static byte[] CreateLayerPng (UserLayer sourceLayer)
	{
		using Cairo.ImageSurface source = CreateLayerSurface (sourceLayer);
		using GdkPixbuf.Pixbuf pixbuf = source.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static AiLayerImage CreateAiLayerImage (UserLayer sourceLayer)
	{
		using Cairo.ImageSurface source = RenderLayerContent (sourceLayer, out PointD origin);
		using GdkPixbuf.Pixbuf pixbuf = source.ToPixbuf ();
		return new (pixbuf.SaveToBuffer ("png"), source.GetSize (), origin);
	}

	private static byte[] CreateAiLayerPng (UserLayer sourceLayer)
	{
		RectangleI bounds = GetAiLayerContentBounds (sourceLayer);
		using ImageSurface cropped = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			bounds.Width,
			bounds.Height);
		using (Cairo.Context context = new (cropped)) {
			context.SetSourceSurface (sourceLayer.Surface, -bounds.X, -bounds.Y);
			context.Paint ();
		}
		using GdkPixbuf.Pixbuf pixbuf = cropped.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static RectangleI GetAiLayerContentBounds (UserLayer layer)
		=> Utility.TryGetAlphaBounds (layer.Surface, out RectangleI bounds)
			? bounds
			: new RectangleI (0, 0, 1, 1);
}
