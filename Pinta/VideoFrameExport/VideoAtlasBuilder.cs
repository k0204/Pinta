using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Cairo;
using GdkPixbuf;
using Pinta.Core;
using Path = System.IO.Path;

namespace Pinta;

internal sealed record AtlasBuildResult (
	IReadOnlyList<string> ImagePaths,
	string MetadataPath,
	int FrameCount);

internal static class VideoAtlasBuilder
{
	private const int MaxDimension = 16384;
	private const int MaxScalePercent = 100;
	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	public static AtlasBuildResult Build (
		IReadOnlyList<string> paths,
		string outputDirectory,
		string filename,
		int scalePercent,
		int minWidth,
		int maxWidth,
		int minHeight,
		int maxHeight,
		int spacing,
		bool trimTransparent,
		CancellationToken cancellationToken = default)
	{
		ValidateInput (paths, outputDirectory, filename, scalePercent, minWidth, maxWidth, minHeight, maxHeight, spacing);
		string baseName = Path.GetFileNameWithoutExtension (filename);
		Directory.CreateDirectory (outputDirectory);

		List<AtlasItem> items = [];
		try {
			for (int index = 0; index < paths.Count; index++) {
				cancellationToken.ThrowIfCancellationRequested ();
				items.Add (ReadItem (paths[index], index, trimTransparent, scalePercent));
			}

			List<AtlasPage> pages = PackItems (items, minWidth, maxWidth, minHeight, maxHeight, spacing, cancellationToken);
			List<string> imagePaths = SavePages (pages, outputDirectory, baseName, cancellationToken);
			string metadataPath = Path.Combine (outputDirectory, baseName + ".json");
			AtlasManifest manifest = CreateManifest (baseName, scalePercent, imagePaths, pages, items);
			File.WriteAllText (metadataPath, JsonSerializer.Serialize (manifest, json_options));
			return new AtlasBuildResult (imagePaths, metadataPath, items.Count);
		} finally {
			foreach (AtlasItem item in items)
				item.Pixbuf.Dispose ();
		}
	}

	private static void ValidateInput (
		IReadOnlyList<string> paths,
		string outputDirectory,
		string filename,
		int scalePercent,
		int minWidth,
		int maxWidth,
		int minHeight,
		int maxHeight,
		int spacing)
	{
		if (paths.Count == 0)
			throw new VideoFrameExportException (Translations.GetString ("Select at least one frame for the atlas."));
		if (string.IsNullOrWhiteSpace (outputDirectory))
			throw new VideoFrameExportException (Translations.GetString ("Choose an atlas output folder."));
		if (scalePercent is < 1 or > MaxScalePercent)
			throw new VideoFrameExportException (Translations.GetString ("Atlas scale must be between 1 and 100 percent."));
		string baseName = Path.GetFileNameWithoutExtension (filename);
		if (string.IsNullOrWhiteSpace (baseName)
			|| filename != Path.GetFileName (filename)
			|| baseName is "." or ".."
			|| baseName.IndexOfAny (Path.GetInvalidFileNameChars ()) >= 0)
			throw new VideoFrameExportException (Translations.GetString ("The atlas filename is not valid."));
		if (minWidth is < 0 or > MaxDimension || maxWidth is < 1 or > MaxDimension || minWidth > maxWidth)
			throw new VideoFrameExportException (Translations.GetString ("Atlas width limits are invalid."));
		if (minHeight is < 0 or > MaxDimension || maxHeight is < 1 or > MaxDimension || minHeight > maxHeight)
			throw new VideoFrameExportException (Translations.GetString ("Atlas height limits are invalid."));
		if (spacing is < 0 or > 256)
			throw new VideoFrameExportException (Translations.GetString ("Atlas spacing must be between 0 and 256 pixels."));
	}

	private static List<AtlasPage> PackItems (
		IReadOnlyList<AtlasItem> items,
		int minWidth,
		int maxWidth,
		int minHeight,
		int maxHeight,
		int spacing,
		CancellationToken cancellationToken)
	{
		int width = CalculatePageWidth (items, minWidth, maxWidth, spacing);
		List<AtlasPage> pages = [];
		AtlasPage page = new (width, spacing);
		int pageIndex = 0;
		int x = spacing;
		int y = spacing;
		int rowHeight = 0;

		foreach (AtlasItem item in items) {
			cancellationToken.ThrowIfCancellationRequested ();
			if (item.PackedWidth + spacing * 2 > width || item.PackedHeight + spacing * 2 > maxHeight)
				throw new VideoFrameExportException (Translations.GetString ("A frame is too large for the atlas page."));

			bool newRow = x > spacing && x + item.PackedWidth + spacing > width;
			int nextY = newRow ? y + rowHeight + spacing : y;
			int nextRowHeight = newRow ? item.PackedHeight : Math.Max (rowHeight, item.PackedHeight);
			if (page.Items.Count > 0 && nextY + nextRowHeight + spacing > maxHeight) {
				FinalizePage (page, y, rowHeight, minHeight, pages);
				page = new AtlasPage (width, spacing);
				pageIndex++;
				x = spacing;
				y = spacing;
				rowHeight = 0;
				newRow = false;
				nextY = y;
			}

			if (newRow) {
				x = spacing;
				y = nextY;
				rowHeight = 0;
			}

			item.Page = pageIndex;
			item.X = x;
			item.Y = y;
			page.Items.Add (item);
			x += item.PackedWidth + spacing;
			rowHeight = Math.Max (rowHeight, item.PackedHeight);
		}

		FinalizePage (page, y, rowHeight, minHeight, pages);
		return pages;
	}

	private static int CalculatePageWidth (IReadOnlyList<AtlasItem> items, int minWidth, int maxWidth, int spacing)
	{
		long totalWidth = spacing;
		long widest = 0;
		foreach (AtlasItem item in items) {
			totalWidth += item.PackedWidth + spacing;
			widest = Math.Max (widest, item.PackedWidth + spacing * 2L);
		}

		return (int) Math.Min (maxWidth, Math.Max (minWidth, Math.Max (widest, totalWidth)));
	}

	private static void FinalizePage (AtlasPage page, int y, int rowHeight, int minHeight, ICollection<AtlasPage> pages)
	{
		if (page.Items.Count == 0)
			return;
		page.Height = Math.Max (minHeight, y + rowHeight + page.Spacing);
		pages.Add (page);
	}

	private static List<string> SavePages (
		IReadOnlyList<AtlasPage> pages,
		string outputDirectory,
		string baseName,
		CancellationToken cancellationToken)
	{
		List<string> imagePaths = [];
		for (int index = 0; index < pages.Count; index++) {
			cancellationToken.ThrowIfCancellationRequested ();
			AtlasPage page = pages[index];
			string suffix = pages.Count == 1 ? string.Empty : $"-{index + 1:D3}";
			string path = Path.Combine (outputDirectory, baseName + suffix + ".png");
			using ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, page.Width, page.Height);
			using (Context context = new (surface)) {
				context.Operator = Operator.Clear;
				context.Paint ();
				context.Operator = Operator.Over;
				foreach (AtlasItem item in page.Items)
					context.DrawPixbuf (item.Pixbuf, item.X - item.TrimLeft, item.Y - item.TrimTop);
			}
			surface.SaveToPng (path);
			imagePaths.Add (path);
		}

		return imagePaths;
	}

	private static AtlasManifest CreateManifest (
		string baseName,
		int scalePercent,
		IReadOnlyList<string> imagePaths,
		IReadOnlyList<AtlasPage> pages,
		IReadOnlyList<AtlasItem> items)
	{
		List<AtlasPageManifest> pageManifest = [];
		for (int index = 0; index < pages.Count; index++)
			pageManifest.Add (new AtlasPageManifest (Path.GetFileName (imagePaths[index]), pages[index].Width, pages[index].Height));

		List<AtlasFrameManifest> frameManifest = items.Select (item => new AtlasFrameManifest (
			item.Index,
			Path.GetFileName (item.SourcePath),
			item.Page,
			item.X,
			item.Y,
			item.TrimWidth,
			item.TrimHeight,
			item.SourceWidth,
			item.SourceHeight,
			item.TrimLeft,
			item.TrimTop)).ToList ();
		return new AtlasManifest (baseName, frameManifest.Count, scalePercent, pageManifest, frameManifest);
	}

	private static AtlasItem ReadItem (string path, int index, bool trimTransparent, int scalePercent)
	{
		if (!File.Exists (path))
			throw new VideoFrameExportException (Translations.GetString ("The selected image file could not be found."));

		Pixbuf pixbuf = Pixbuf.NewFromFile (path)!;
		int sourceWidth = pixbuf.Width;
		int sourceHeight = pixbuf.Height;
		try {
			(int width, int height) = CalculateScaledSize (sourceWidth, sourceHeight, scalePercent);
			if (scalePercent != 100) {
				Pixbuf scaled = pixbuf.ScaleSimple (width, height, InterpType.Bilinear)!;
				pixbuf.Dispose ();
				pixbuf = scaled;
			}

			if (!trimTransparent || !pixbuf.HasAlpha)
				return new AtlasItem (pixbuf, index, path, 0, 0, pixbuf.Width, pixbuf.Height, sourceWidth, sourceHeight);

			using ImageSurface surface = CreateSurface (pixbuf);
			ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();
			int left = pixbuf.Width;
			int top = pixbuf.Height;
			int right = -1;
			int bottom = -1;
			for (int row = 0; row < pixbuf.Height; row++)
				for (int column = 0; column < pixbuf.Width; column++)
					if (pixels[row * pixbuf.Width + column].A != 0) {
						left = Math.Min (left, column);
						top = Math.Min (top, row);
						right = Math.Max (right, column);
						bottom = Math.Max (bottom, row);
					}

			if (right < left || bottom < top)
				return new AtlasItem (pixbuf, index, path, 0, 0, 0, 0, sourceWidth, sourceHeight);
			return new AtlasItem (pixbuf, index, path, left, top, right - left + 1, bottom - top + 1, sourceWidth, sourceHeight);
		} catch {
			pixbuf.Dispose ();
			throw;
		}
	}

	private static (int Width, int Height) CalculateScaledSize (int sourceWidth, int sourceHeight, int scalePercent)
	{
		long width = Math.Max (1, (long) Math.Round (sourceWidth * scalePercent / 100d));
		long height = Math.Max (1, (long) Math.Round (sourceHeight * scalePercent / 100d));
		if (width > MaxDimension || height > MaxDimension)
			throw new VideoFrameExportException (Translations.GetString ("The scaled frame exceeds the maximum atlas dimension."));
		return ((int) width, (int) height);
	}

	private static ImageSurface CreateSurface (Pixbuf pixbuf)
	{
		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, pixbuf.Width, pixbuf.Height);
		using Context context = new (surface);
		context.DrawPixbuf (pixbuf, 0, 0);
		return surface;
	}

	private sealed class AtlasItem (
		Pixbuf pixbuf,
		int index,
		string sourcePath,
		int trimLeft,
		int trimTop,
		int trimWidth,
		int trimHeight,
		int sourceWidth,
		int sourceHeight)
	{
		public Pixbuf Pixbuf { get; } = pixbuf;
		public int Index { get; } = index;
		public string SourcePath { get; } = sourcePath;
		public int TrimLeft { get; } = trimLeft;
		public int TrimTop { get; } = trimTop;
		public int TrimWidth { get; } = trimWidth;
		public int TrimHeight { get; } = trimHeight;
		public int SourceWidth { get; } = sourceWidth;
		public int SourceHeight { get; } = sourceHeight;
		public int PackedWidth => Math.Max (1, TrimWidth);
		public int PackedHeight => Math.Max (1, TrimHeight);
		public int Page { get; set; }
		public int X { get; set; }
		public int Y { get; set; }
	}

	private sealed class AtlasPage (int width, int spacing)
	{
		public int Width { get; } = width;
		public int Height { get; set; }
		public int Spacing { get; } = spacing;
		public List<AtlasItem> Items { get; } = [];
	}

	private sealed record AtlasManifest (
		string Name,
		int FrameCount,
		int ScalePercent,
		IReadOnlyList<AtlasPageManifest> Pages,
		IReadOnlyList<AtlasFrameManifest> Frames);

	private sealed record AtlasPageManifest (string FileName, int Width, int Height);

	private sealed record AtlasFrameManifest (
		int Index,
		string Source,
		int Page,
		int X,
		int Y,
		int Width,
		int Height,
		int SourceWidth,
		int SourceHeight,
		int OffsetX,
		int OffsetY);
}
