using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

public sealed class SpriteSegmentationService
{
	private const string prompt_config_file = "sprite-segmentation-prompt.txt";
	private const int max_upload_bytes = 5 * 1024 * 1024;

	private readonly AiJobService jobs;

	public SpriteSegmentationService (AiAuthService auth)
	{
		jobs = new (auth);
	}

	public async Task<SpriteSegmentationAnalysis> AnalyzeAsync (
		byte[] png,
		int imageWidth,
		int imageHeight,
		string provider,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace (provider))
			throw new ArgumentException ("Sprite analysis provider is required.", nameof (provider));
		(byte[] requestPng, int requestWidth, int requestHeight) = PrepareRequestImage (png, imageWidth, imageHeight);
		string prompt = $"{ReadPrompt ()}\n\n" +
			$"The uploaded input image dimensions are exactly {requestWidth}x{requestHeight} pixels. " +
			$"Return image_width={requestWidth} and image_height={requestHeight}; do not estimate them.";
		string debugDirectory = CreateDebugDirectory ();
		var request = new {
			text = prompt,
			image_base64 = new[] { Convert.ToBase64String (requestPng) },
			provider,
		};
		SaveDebugBytes (debugDirectory, "request.png", requestPng);
		SaveDebugText (debugDirectory, "request.json", JsonSerializer.Serialize (request));

		using JsonDocument result = await jobs.RunChatAsync (
			request,
			(stage, json) => SaveDebugText (debugDirectory, $"{stage}.json", json),
			log: null,
			cancellationToken);
		string json = result.RootElement.GetProperty ("text").GetString ()
			?? throw new InvalidOperationException ("Sprite analysis response did not include JSON text.");
		SpriteSegmentationAnalysis analysis = JsonSerializer.Deserialize<SpriteSegmentationAnalysis> (
			ExtractJsonObject (json),
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? throw new InvalidOperationException ("Sprite analysis JSON was empty.");
		if (analysis.Items is null)
			throw new InvalidOperationException ("Sprite analysis did not include items.");
		Validate (analysis, requestWidth, requestHeight);
		SpriteSegmentationAnalysis restored = RestoreOriginalCoordinates (
			analysis,
			imageWidth,
			imageHeight,
			requestWidth,
			requestHeight);
		Validate (restored, imageWidth, imageHeight);
		return restored with { Items = [.. restored.Items.OrderBy (item => item.Index)] };
	}

	private static (byte[] Png, int Width, int Height) PrepareRequestImage (
		byte[] png,
		int imageWidth,
		int imageHeight)
	{
		if (png.Length <= max_upload_bytes)
			return (png, imageWidth, imageHeight);

		using GdkPixbuf.Pixbuf source = LoadPixbuf (png);
		int width = source.Width;
		int height = source.Height;
		byte[] resized = png;
		while (resized.Length > max_upload_bytes && width > 1 && height > 1) {
			double ratio = Math.Sqrt ((double) max_upload_bytes / resized.Length) * 0.9;
			int nextWidth = Math.Max (1, (int) Math.Floor (width * ratio));
			int nextHeight = Math.Max (1, (int) Math.Floor (height * ratio));
			if (nextWidth == width && nextHeight == height) {
				nextWidth = Math.Max (1, width - 1);
				nextHeight = Math.Max (1, height - 1);
			}

			using GdkPixbuf.Pixbuf scaled = source.ScaleSimple (
				nextWidth,
				nextHeight,
				GdkPixbuf.InterpType.Hyper)
				?? throw new InvalidOperationException ("Unable to resize sprite analysis image.");
			resized = scaled.SaveToBuffer ("png");
			width = nextWidth;
			height = nextHeight;
		}

		if (resized.Length > max_upload_bytes)
			throw new InvalidOperationException ("Sprite analysis image is too large to upload.");
		return (resized, width, height);
	}

	private static SpriteSegmentationAnalysis RestoreOriginalCoordinates (
		SpriteSegmentationAnalysis analysis,
		int originalWidth,
		int originalHeight,
		int requestWidth,
		int requestHeight)
	{
		if (requestWidth == originalWidth && requestHeight == originalHeight)
			return analysis;

		double scaleX = originalWidth / (double) requestWidth;
		double scaleY = originalHeight / (double) requestHeight;
		return analysis with {
			ImageWidth = originalWidth,
			ImageHeight = originalHeight,
			Items = [.. analysis.Items.Select (item => item with {
				Bbox = RestoreBox (item.Bbox, scaleX, scaleY, originalWidth, originalHeight),
				FootAnchor = new SpriteSegmentationPoint (
					item.FootAnchor.X * scaleX,
					item.FootAnchor.Y * scaleY),
			})],
		};
	}

	private static SpriteSegmentationBox RestoreBox (
		SpriteSegmentationBox box,
		double scaleX,
		double scaleY,
		int imageWidth,
		int imageHeight)
	{
		int left = Math.Clamp ((int) Math.Floor (box.X * scaleX), 0, imageWidth - 1);
		int top = Math.Clamp ((int) Math.Floor (box.Y * scaleY), 0, imageHeight - 1);
		int right = Math.Clamp ((int) Math.Ceiling ((box.X + box.Width) * scaleX), left + 1, imageWidth);
		int bottom = Math.Clamp ((int) Math.Ceiling ((box.Y + box.Height) * scaleY), top + 1, imageHeight);
		return new (left, top, right - left, bottom - top);
	}

	private static string ExtractJsonObject (string response)
	{
		string text = response.Trim ();
		int start = text.IndexOf ('{');
		if (start < 0)
			return text;

		int depth = 0;
		bool inString = false;
		bool escaped = false;
		for (int i = start; i < text.Length; i++) {
			char character = text[i];
			if (inString) {
				if (escaped)
					escaped = false;
				else if (character == '\\')
					escaped = true;
				else if (character == '"')
					inString = false;
				continue;
			}

			if (character == '"')
				inString = true;
			else if (character == '{')
				depth++;
			else if (character == '}' && --depth == 0)
				return text[start..(i + 1)];
		}

		return text;
	}

	private static string ReadPrompt ()
	{
		string path = Path.Combine (AppContext.BaseDirectory, "config", prompt_config_file);
		string prompt = File.ReadAllText (path).Trim ();
		if (string.IsNullOrWhiteSpace (prompt))
			throw new InvalidOperationException ($"Sprite segmentation prompt is missing: {path}");
		return prompt;
	}

	private static GdkPixbuf.Pixbuf LoadPixbuf (byte[] png)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		return GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
	}

	private static string CreateDebugDirectory ()
	{
		string path = Path.Combine (
			AppContext.BaseDirectory,
			"ai-sprite-segmentation-logs",
			DateTime.Now.ToString ("yyyyMMdd-HHmmss-fff"));
		Directory.CreateDirectory (path);
		Console.WriteLine ($"Sprite segmentation debug data: {path}");
		return path;
	}

	private static void SaveDebugBytes (string directory, string fileName, byte[] data)
	{
		try {
			File.WriteAllBytes (Path.Combine (directory, fileName), data);
		} catch (Exception ex) {
			Console.WriteLine ($"Warning: failed to save sprite segmentation debug file '{fileName}': {ex.Message}");
		}
	}

	private static void SaveDebugText (string directory, string fileName, string text)
	{
		try {
			File.WriteAllText (Path.Combine (directory, fileName), text);
		} catch (Exception ex) {
			Console.WriteLine ($"Warning: failed to save sprite segmentation debug file '{fileName}': {ex.Message}");
		}
	}

	private static void Validate (SpriteSegmentationAnalysis analysis, int imageWidth, int imageHeight)
	{
		if (analysis.ImageWidth <= 0 || analysis.ImageHeight <= 0)
			throw new InvalidOperationException ("Sprite analysis returned invalid image dimensions.");
		if (analysis.ImageWidth != imageWidth || analysis.ImageHeight != imageHeight)
			throw new InvalidOperationException ("Sprite analysis dimensions do not match the source image.");
		if (analysis.Items.Count is < 1 or > 256)
			throw new InvalidOperationException ("Sprite analysis returned an invalid sprite count.");
		if (analysis.Items.Select (item => item.Index).Distinct ().Count () != analysis.Items.Count)
			throw new InvalidOperationException ("Sprite analysis returned duplicate indices.");

		foreach (SpriteSegmentationItem item in analysis.Items) {
			SpriteSegmentationBox box = item.Bbox;
			if (item.Index < 0 ||
				box.Width <= 0 || box.Height <= 0 || box.X < 0 || box.Y < 0 ||
				(long) box.X + box.Width > imageWidth || (long) box.Y + box.Height > imageHeight ||
				item.FootAnchor.X < box.X || item.FootAnchor.X > box.X + box.Width ||
				item.FootAnchor.Y < box.Y || item.FootAnchor.Y > box.Y + box.Height)
				throw new InvalidOperationException ("Sprite analysis returned an out-of-bounds item.");
		}
	}
}

public sealed record SpriteSegmentationAnalysis (
	[property: JsonPropertyName ("image_width")] int ImageWidth,
	[property: JsonPropertyName ("image_height")] int ImageHeight,
	IReadOnlyList<SpriteSegmentationItem> Items);

public sealed record SpriteSegmentationItem (
	int Index,
	SpriteSegmentationBox Bbox,
	[property: JsonPropertyName ("foot_anchor")] SpriteSegmentationPoint FootAnchor);
public sealed record SpriteSegmentationBox (int X, int Y, int Width, int Height);
public sealed record SpriteSegmentationPoint (double X, double Y);
