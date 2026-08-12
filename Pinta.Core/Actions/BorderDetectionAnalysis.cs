using System;
using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

internal static class BorderDetectionAnalysis
{
	private const int histogram_size = 256;

	public static void Render (ImageSurface source, ImageSurface destination, RectangleI bounds)
	{
		int width = bounds.Width;
		int height = bounds.Height;
		if (width < 3 || height < 3)
			return;

		byte[] luminance = ReadLuminance (source, bounds);
		byte[] blurred = BoxBlur (luminance, width, height);
		byte[] strength = ComputeSobel (blurred, width, height);
		bool[] edges = ThresholdEdges (strength, width, height);
		RemoveSmallRegions (edges, width, height, Math.Max (8, width * height / 100_000));
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

	private static byte[] BoxBlur (byte[] source, int width, int height)
	{
		byte[] result = new byte[source.Length];
		for (int y = 1; y < height - 1; y++) {
			for (int x = 1; x < width - 1; x++) {
				int index = y * width + x;
				int sum = source[index - width - 1] + source[index - width] + source[index - width + 1]
					+ source[index - 1] + source[index] + source[index + 1]
					+ source[index + width - 1] + source[index + width] + source[index + width + 1];
				result[index] = (byte) (sum / 9);
			}
		}
		return result;
	}

	private static byte[] ComputeSobel (byte[] source, int width, int height)
	{
		byte[] result = new byte[source.Length];
		for (int y = 1; y < height - 1; y++) {
			for (int x = 1; x < width - 1; x++) {
				int index = y * width + x;
				int gx = -source[index - width - 1] + source[index - width + 1]
					- 2 * source[index - 1] + 2 * source[index + 1]
					- source[index + width - 1] + source[index + width + 1];
				int gy = -source[index - width - 1] - 2 * source[index - width] - source[index - width + 1]
					+ source[index + width - 1] + 2 * source[index + width] + source[index + width + 1];
				result[index] = (byte) Math.Min (255, (Math.Abs (gx) + Math.Abs (gy)) / 4);
			}
		}
		return result;
	}

	private static bool[] ThresholdEdges (byte[] strength, int width, int height)
	{
		Span<int> histogram = stackalloc int[histogram_size];
		foreach (byte value in strength)
			histogram[value]++;

		int target = strength.Length * 85 / 100;
		int seen = 0;
		int threshold = 24;
		for (int value = 0; value < histogram.Length; value++) {
			seen += histogram[value];
			if (seen < target)
				continue;
			threshold = Math.Max (threshold, value);
			break;
		}

		bool[] edges = new bool[strength.Length];
		for (int y = 1; y < height - 1; y++)
			for (int x = 1; x < width - 1; x++)
				edges[y * width + x] = strength[y * width + x] >= threshold;
		return edges;
	}

	private static void RemoveSmallRegions (bool[] edges, int width, int height, int minimumPixels)
	{
		bool[] visited = new bool[edges.Length];
		Queue<int> pending = new ();
		List<int> region = [];
		for (int start = 0; start < edges.Length; start++) {
			if (!edges[start] || visited[start])
				continue;
			pending.Enqueue (start);
			visited[start] = true;
			region.Clear ();
			while (pending.TryDequeue (out int index)) {
				region.Add (index);
				EnqueueNeighbors (index, edges, visited, pending, width, height);
			}
			if (region.Count < minimumPixels)
				foreach (int index in region)
					edges[index] = false;
		}
	}

	private static void EnqueueNeighbors (
		int index,
		bool[] edges,
		bool[] visited,
		Queue<int> pending,
		int width,
		int height)
	{
		int x = index % width;
		int y = index / width;
		for (int dy = -1; dy <= 1; dy++) {
			for (int dx = -1; dx <= 1; dx++) {
				int nextX = x + dx;
				int nextY = y + dy;
				if ((dx == 0 && dy == 0) || nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
					continue;
				int next = nextY * width + nextX;
				if (!edges[next] || visited[next])
					continue;
				visited[next] = true;
				pending.Enqueue (next);
			}
		}
	}

	private static void DrawOverlay (ImageSurface destination, RectangleI bounds, bool[] edges)
	{
		Span<ColorBgra> pixels = destination.GetPixelData ();
		ColorBgra color = ColorBgra.FromBgr (255, 75, 128).NewAlpha (220);
		for (int y = 0; y < bounds.Height; y++) {
			for (int x = 0; x < bounds.Width; x++) {
				if (!edges[y * bounds.Width + x])
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
