using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using OpenCvSharp;

namespace Pinta.Core;

internal sealed class AutoSplitRegion
{
	public AutoSplitRegion (
		RectangleI bounds,
		byte[]? pixelMask = null,
		IReadOnlyList<PointI>? outline = null)
	{
		Bounds = bounds;
		PixelMask = pixelMask;
		Outline = outline ?? [];
	}

	public RectangleI Bounds { get; private set; }
	public byte[]? PixelMask { get; private set; }
	public IReadOnlyList<PointI> Outline { get; private set; }

	public void SetBounds (RectangleI bounds)
	{
		Bounds = bounds;
		PixelMask = null;
		Outline = [];
	}

	public void CopyTo (ImageSurface source, ImageSurface destination)
	{
		if (PixelMask is null) {
			using Context context = new (destination);
			context.SetSourceSurface (source, -Bounds.X, -Bounds.Y);
			context.Paint ();
			return;
		}

		ReadOnlySpan<ColorBgra> sourcePixels = source.GetReadOnlyPixelData ();
		Span<ColorBgra> destinationPixels = destination.GetPixelData ();
		destinationPixels.Clear ();
		for (int y = 0; y < Bounds.Height; y++)
			for (int x = 0; x < Bounds.Width; x++) {
				int destinationIndex = y * Bounds.Width + x;
				if (PixelMask[destinationIndex] != 0)
					destinationPixels[destinationIndex] = sourcePixels[(Bounds.Y + y) * source.Width + Bounds.X + x];
			}
		destination.MarkDirty ();
	}

}

internal static class AutoSplitDetection
{
	public static IReadOnlyList<AutoSplitRegion> DetectLocal (
		ImageSurface surface,
		byte alphaThreshold = 1,
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
		List<AutoSplitRegion> regions = [];

		for (int component = 1; component < componentCount; component++) {
			int x = stats.Get<int> (component, (int) ConnectedComponentsTypes.Left);
			int y = stats.Get<int> (component, (int) ConnectedComponentsTypes.Top);
			int width = stats.Get<int> (component, (int) ConnectedComponentsTypes.Width);
			int height = stats.Get<int> (component, (int) ConnectedComponentsTypes.Height);
			int area = stats.Get<int> (component, (int) ConnectedComponentsTypes.Area);
			if (width >= minimumWidth && height >= minimumHeight && area >= minimumComponentArea) {
				RectangleI bounds = new (x, y, width, height);
				byte[] componentMask = GetComponentMask (labels, component, bounds);
				regions.Add (new AutoSplitRegion (bounds, componentMask, GetComponentOutline (componentMask, bounds)));
			}
		}

		return [.. regions.OrderBy (region => region.Bounds.Y).ThenBy (region => region.Bounds.X)];
	}

	private static byte[] GetComponentMask (Mat labels, int component, RectangleI bounds)
	{
		byte[] componentMask = new byte[bounds.Width * bounds.Height];
		for (int y = 0; y < bounds.Height; y++)
			for (int x = 0; x < bounds.Width; x++)
				if (labels.Get<int> (bounds.Y + y, bounds.X + x) == component)
					componentMask[y * bounds.Width + x] = 255;
		return componentMask;
	}

	private static IReadOnlyList<PointI> GetComponentOutline (byte[] componentMask, RectangleI bounds)
	{
		using Mat mask = Mat.FromPixelData (bounds.Height, bounds.Width, MatType.CV_8UC1, componentMask);
		Cv2.FindContours (mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
		Point[]? contour = contours.Length == 0
			? null
			: contours.OrderByDescending (candidate => Cv2.ContourArea (candidate, false)).First ();
		if (contour is null)
			return [];

		Point[] outline = Cv2.ApproxPolyDP (contour, 1, true);
		return [.. outline.Select (point => new PointI (bounds.X + point.X, bounds.Y + point.Y))];
	}
}
