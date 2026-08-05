using System;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private static ImageSurface? CloneLocalMask (Document document)
	{
		ImageSurface mask = document.Layers.ToolLayer.Surface;
		ReadOnlySpan<ColorBgra> pixels = mask.GetReadOnlyPixelData ();
		foreach (ColorBgra pixel in pixels) {
			if (pixel.A > 0)
				return mask.Clone ();
		}

		return null;
	}

	private static byte[] ApplyLocalMask (
		byte[] sourcePng,
		byte[] resultPng,
		ImageSurface localMask)
	{
		using ImageSurface source = LoadPng (sourcePng);
		using ImageSurface result = LoadPng (resultPng);

		if (source.Width != result.Width || source.Height != result.Height ||
			localMask.Width != result.Width || localMask.Height != result.Height)
			throw new InvalidOperationException ("Baidu result and local mask have different dimensions.");

		ReadOnlySpan<ColorBgra> sourcePixels = source.GetReadOnlyPixelData ();
		ReadOnlySpan<ColorBgra> localPixels = localMask.GetReadOnlyPixelData ();
		Span<ColorBgra> resultPixels = result.GetPixelData ();
		for (int i = 0; i < resultPixels.Length; i++) {
			ColorBgra local = localPixels[i];
			if (local.A == 0)
				continue;

			resultPixels[i] = local.G >= local.R
				? WithAlpha (sourcePixels[i], 255)
				: ColorBgra.Transparent;
		}

		result.MarkDirty ();
		using GdkPixbuf.Pixbuf pixbuf = result.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static byte[] CreateAlphaMaskPng (byte[] resultPng)
	{
		using ImageSurface result = LoadPng (resultPng);
		using ImageSurface mask = CairoExtensions.CreateImageSurface (Format.Argb32, result.Width, result.Height);
		ReadOnlySpan<ColorBgra> resultPixels = result.GetReadOnlyPixelData ();
		Span<ColorBgra> maskPixels = mask.GetPixelData ();
		for (int i = 0; i < maskPixels.Length; i++) {
			byte alpha = resultPixels[i].A;
			maskPixels[i] = ColorBgra.FromBgra (alpha, alpha, alpha, alpha);
		}

		mask.MarkDirty ();
		using GdkPixbuf.Pixbuf pixbuf = mask.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static ImageSurface LoadPng (byte[] png)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, pixbuf.Width, pixbuf.Height);
		using Context context = new (surface);
		context.DrawPixbuf (pixbuf, PointD.Zero);
		return surface;
	}

	private static ColorBgra WithAlpha (ColorBgra pixel, byte alpha)
	{
		if (alpha == 0 || pixel.A == 0)
			return ColorBgra.Transparent;

		return ColorBgra.FromBgra (
			(byte) (pixel.B * alpha / pixel.A),
			(byte) (pixel.G * alpha / pixel.A),
			(byte) (pixel.R * alpha / pixel.A),
			alpha);
	}
}
