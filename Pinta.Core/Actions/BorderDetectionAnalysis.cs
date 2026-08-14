using System;
using System.Runtime.InteropServices;
using Cairo;
using OpenCvSharp;

namespace Pinta.Core;

internal static class BorderDetectionAnalysis
{
	public static void Render (
		ImageSurface source,
		ImageSurface destination,
		RectangleI bounds,
		double minimumAreaRatio)
	{
		int width = bounds.Width;
		int height = bounds.Height;
		if (width < 3 || height < 3)
			return;

		using Mat luminance = Mat.FromPixelData (height, width, MatType.CV_8UC1, ReadLuminance (source, bounds));
		using Mat blurred = new ();
		using Mat edges = new ();
		Cv2.GaussianBlur (luminance, blurred, new OpenCvSharp.Size (3, 3), 0);
		Cv2.Canny (blurred, edges, 50, 150);
		RemoveSmallRegions (
			edges,
			Math.Max (8, (int) Math.Ceiling (width * (double) height * minimumAreaRatio)));
		DrawOverlay (destination, bounds, edges);
	}

	private static byte[] ReadLuminance (ImageSurface source, RectangleI bounds)
	{
		ReadOnlySpan<ColorBgra> pixels = source.GetReadOnlyPixelData ();
		byte[] result = new byte[bounds.Width * bounds.Height];
		for (int y = 0; y < bounds.Height; y++) {
			int sourceOffset = (bounds.Y + y) * source.Width + bounds.X;
			for (int x = 0; x < bounds.Width; x++)
				result[y * bounds.Width + x] = pixels[sourceOffset + x].GetIntensityByte ();
		}
		return result;
	}

	private static void RemoveSmallRegions (Mat edges, int minimumRegionArea)
	{
		int radius = Math.Clamp (Math.Min (edges.Width, edges.Height) / 100, 2, 8);
		using Mat kernel = Cv2.GetStructuringElement (
			MorphShapes.Rect,
			new OpenCvSharp.Size (radius * 2 + 1, radius * 2 + 1));
		using Mat groupedEdges = new ();
		Cv2.Dilate (edges, groupedEdges, kernel);

		using Mat labels = new ();
		using Mat stats = new ();
		using Mat centroids = new ();
		using Mat mask = new ();
		int count = Cv2.ConnectedComponentsWithStats (
			groupedEdges,
			labels,
			stats,
			centroids,
			PixelConnectivity.Connectivity8);
		for (int label = 1; label < count; label++) {
			int width = stats.Get<int> (label, (int) ConnectedComponentsTypes.Width);
			int height = stats.Get<int> (label, (int) ConnectedComponentsTypes.Height);
			if (width * (long) height >= minimumRegionArea)
				continue;
			Cv2.Compare (labels, label, mask, CmpTypes.EQ);
			edges.SetTo (Scalar.Black, mask);
		}
	}

	private static void DrawOverlay (ImageSurface destination, RectangleI bounds, Mat edges)
	{
		byte[] edgePixels = new byte[bounds.Width * bounds.Height];
		Marshal.Copy (edges.Data, edgePixels, 0, edgePixels.Length);
		Span<ColorBgra> pixels = destination.GetPixelData ();
		ColorBgra color = ColorBgra.FromBgr (255, 75, 128).NewAlpha (220);
		for (int y = 0; y < bounds.Height; y++) {
			for (int x = 0; x < bounds.Width; x++) {
				if (edgePixels[y * bounds.Width + x] == 0)
					continue;
				for (int dy = -1; dy <= 1; dy++)
					for (int dx = -1; dx <= 1; dx++)
						SetPixel (pixels, destination.Width, destination.Height, bounds.X + x + dx, bounds.Y + y + dy, color);
			}
		}
		destination.MarkDirty (bounds);
	}

	private static void SetPixel (Span<ColorBgra> pixels, int width, int height, int x, int y, ColorBgra color)
	{
		if (x >= 0 && y >= 0 && x < width && y < height)
			pixels[y * width + x] = color;
	}
}
