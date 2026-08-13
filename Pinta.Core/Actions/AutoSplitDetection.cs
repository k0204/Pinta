using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;

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
		bool[] visited = new bool[surface.Width * surface.Height];
		List<RectangleI> regions = [];

		for (int y = 0; y < surface.Height; y++) {
			for (int x = 0; x < surface.Width; x++) {
				int index = y * surface.Width + x;
				if (visited[index] || pixels[index].A < alphaThreshold)
					continue;

				(RectangleI bounds, int pixelCount) = FloodFill (pixels, surface.Width, surface.Height, x, y, alphaThreshold, visited);
				if (bounds.Width >= minimumWidth
					&& bounds.Height >= minimumHeight
					&& pixelCount >= minimumWidth * (long) minimumHeight)
					regions.Add (bounds);
			}
		}

		return [.. regions.OrderBy (region => region.Y).ThenBy (region => region.X)];
	}

	private static (RectangleI Bounds, int PixelCount) FloodFill (
		ReadOnlySpan<ColorBgra> pixels,
		int width,
		int height,
		int startX,
		int startY,
		byte alphaThreshold,
		bool[] visited)
	{
		Queue<int> pending = new ();
		pending.Enqueue (startY * width + startX);
		visited[startY * width + startX] = true;
		int left = startX;
		int right = startX;
		int top = startY;
		int bottom = startY;
		int pixelCount = 0;

		while (pending.Count > 0) {
			pixelCount++;
			int index = pending.Dequeue ();
			int x = index % width;
			int y = index / width;
			left = Math.Min (left, x);
			right = Math.Max (right, x);
			top = Math.Min (top, y);
			bottom = Math.Max (bottom, y);

			for (int offsetY = -1; offsetY <= 1; offsetY++) {
				for (int offsetX = -1; offsetX <= 1; offsetX++) {
					if (offsetX == 0 && offsetY == 0)
						continue;
					EnqueueNeighbor (x + offsetX, y + offsetY, width, height, pixels, alphaThreshold, visited, pending);
				}
			}
		}

		return (RectangleI.FromLTRB (left, top, right, bottom), pixelCount);
	}

	private static void EnqueueNeighbor (
		int x,
		int y,
		int width,
		int height,
		ReadOnlySpan<ColorBgra> pixels,
		byte alphaThreshold,
		bool[] visited,
		Queue<int> pending)
	{
		if (x < 0 || y < 0 || x >= width || y >= height)
			return;

		int index = y * width + x;
		if (visited[index] || pixels[index].A < alphaThreshold)
			return;

		visited[index] = true;
		pending.Enqueue (index);
	}
}
