using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using OpenCvSharp;

namespace Pinta.Core;

internal sealed class AutoSplitRegion
{
	public AutoSplitRegion (RectangleI bounds)
	{
		Bounds = bounds;
	}

	public RectangleI Bounds { get; set; }
}

internal static class AutoSplitDetection
{
	public static IReadOnlyList<RectangleI> DetectLocal (
		ImageSurface surface,
		byte alphaThreshold = 8,
		int minimumWidth = 4,
		int minimumHeight = 4)
	{
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();
		byte[] alpha = new byte[surface.Width * surface.Height];
		for (int index = 0; index < alpha.Length; index++)
			alpha[index] = pixels[index].A >= alphaThreshold ? (byte) 255 : (byte) 0;

		using Mat mask = Mat.FromPixelData (surface.Height, surface.Width, MatType.CV_8UC1, alpha);
		using Mat labels = new ();
		using Mat stats = new ();
		using Mat centroids = new ();
		int componentCount = Cv2.ConnectedComponentsWithStats (
			mask,
			labels,
			stats,
			centroids,
			PixelConnectivity.Connectivity8);
		int minimumComponentArea = minimumWidth * minimumHeight;
		List<RectangleI> regions = [];

		for (int component = 1; component < componentCount; component++) {
			int x = stats.Get<int> (component, (int) ConnectedComponentsTypes.Left);
			int y = stats.Get<int> (component, (int) ConnectedComponentsTypes.Top);
			int width = stats.Get<int> (component, (int) ConnectedComponentsTypes.Width);
			int height = stats.Get<int> (component, (int) ConnectedComponentsTypes.Height);
			int area = stats.Get<int> (component, (int) ConnectedComponentsTypes.Area);
			if (width >= minimumWidth && height >= minimumHeight && area >= minimumComponentArea)
				regions.Add (new RectangleI (x, y, width, height));
		}

		return [.. regions.OrderBy (region => region.Y).ThenBy (region => region.X)];
	}
}
