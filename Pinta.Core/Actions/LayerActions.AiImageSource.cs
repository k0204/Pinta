//
// LayerActions.AiImageSource.cs
//

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private readonly record struct AiLayerImage (byte[] Png, Size Size, PointD Origin);

	private static byte[] CreateLayerPng (UserLayer sourceLayer)
	{
		using Cairo.ImageSurface source = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			sourceLayer.Surface.Width,
			sourceLayer.Surface.Height);
		using (Cairo.Context context = new (source))
			foreach (Layer layer in sourceLayer.GetLayersToPaint ())
				layer.Draw (context);

		source.MarkDirty ();
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
		=> CreateAiLayerImage (sourceLayer).Png;
}
