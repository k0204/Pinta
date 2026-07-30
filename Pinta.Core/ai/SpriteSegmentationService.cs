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
	private const string prompt_config_file = "sprite-segmentation-prompt.json";

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
		string prompt = ReadPrompt ();
		string debugDirectory = CreateDebugDirectory ();
		var request = new {
			text = prompt,
			image_base64 = new[] { Convert.ToBase64String (png) },
			provider,
		};
		SaveDebugBytes (debugDirectory, "request.png", png);
		SaveDebugText (debugDirectory, "request.json", JsonSerializer.Serialize (request));

		using JsonDocument result = await jobs.RunChatAsync (
			request,
			(stage, json) => SaveDebugText (debugDirectory, $"{stage}.json", json),
			log: null,
			cancellationToken);
		string json = result.RootElement.GetProperty ("text").GetString ()
			?? throw new InvalidOperationException ("Sprite analysis response did not include JSON text.");
		SpriteSegmentationAnalysis analysis = JsonSerializer.Deserialize<SpriteSegmentationAnalysis> (
			json,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? throw new InvalidOperationException ("Sprite analysis JSON was empty.");
		Validate (analysis, analysis.ImageWidth, analysis.ImageHeight);
		analysis = ScaleToSourceImage (analysis, imageWidth, imageHeight);
		Validate (analysis, imageWidth, imageHeight);
		return analysis with { Items = [.. analysis.Items.OrderBy (item => item.Row).ThenBy (item => item.Column)] };
	}

	private static string ReadPrompt ()
	{
		string path = Path.Combine (AppContext.BaseDirectory, "config", prompt_config_file);
		using JsonDocument config = JsonDocument.Parse (File.ReadAllText (path));
		if (!config.RootElement.TryGetProperty ("prompt", out JsonElement value) ||
			value.GetString () is not string prompt || string.IsNullOrWhiteSpace (prompt))
			throw new InvalidOperationException ($"Sprite segmentation prompt is missing: {path}");
		return prompt.Trim ();
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

	private static SpriteSegmentationAnalysis ScaleToSourceImage (
		SpriteSegmentationAnalysis analysis,
		int imageWidth,
		int imageHeight)
	{
		if (analysis.ImageWidth == imageWidth && analysis.ImageHeight == imageHeight)
			return analysis;

		double scaleX = imageWidth / (double) analysis.ImageWidth;
		double scaleY = imageHeight / (double) analysis.ImageHeight;
		return analysis with {
			ImageWidth = imageWidth,
			ImageHeight = imageHeight,
			Items = [.. analysis.Items.Select (item => item with {
				Bbox = ScaleBox (item.Bbox, scaleX, scaleY, imageWidth, imageHeight),
				FootAnchor = new (item.FootAnchor.X * scaleX, item.FootAnchor.Y * scaleY),
			})],
		};
	}

	private static SpriteSegmentationBox ScaleBox (
		SpriteSegmentationBox box,
		double scaleX,
		double scaleY,
		int imageWidth,
		int imageHeight)
	{
		int left = (int) Math.Floor (box.X * scaleX);
		int top = (int) Math.Floor (box.Y * scaleY);
		int right = Math.Min (imageWidth, (int) Math.Ceiling ((box.X + box.Width) * scaleX));
		int bottom = Math.Min (imageHeight, (int) Math.Ceiling ((box.Y + box.Height) * scaleY));
		return new (left, top, right - left, bottom - top);
	}

	private static void Validate (SpriteSegmentationAnalysis analysis, int imageWidth, int imageHeight)
	{
		if (analysis.ImageWidth <= 0 || analysis.ImageHeight <= 0)
			throw new InvalidOperationException ("Sprite analysis returned invalid image dimensions.");
		if (analysis.ImageWidth != imageWidth || analysis.ImageHeight != imageHeight)
			throw new InvalidOperationException ("Sprite analysis dimensions do not match the source image.");
		if (analysis.Grid.Rows is < 1 or > 32 || analysis.Grid.Columns is < 1 or > 32 ||
			analysis.Items.Count != analysis.Grid.Rows * analysis.Grid.Columns || analysis.Items.Count > 256)
			throw new InvalidOperationException ("Sprite analysis returned an invalid or incomplete grid.");
		if (analysis.Items.Select (item => (item.Row, item.Column)).Distinct ().Count () != analysis.Items.Count)
			throw new InvalidOperationException ("Sprite analysis returned duplicate grid cells.");

		foreach (SpriteSegmentationItem item in analysis.Items) {
			SpriteSegmentationBox box = item.Bbox;
			if (item.Row < 0 || item.Row >= analysis.Grid.Rows || item.Column < 0 || item.Column >= analysis.Grid.Columns ||
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
	SpriteSegmentationGrid Grid,
	IReadOnlyList<SpriteSegmentationItem> Items);

public sealed record SpriteSegmentationGrid (int Rows, int Columns);
public sealed record SpriteSegmentationItem (
	int Index,
	int Row,
	int Column,
	SpriteSegmentationBox Bbox,
	[property: JsonPropertyName ("foot_anchor")] SpriteSegmentationPoint FootAnchor);
public sealed record SpriteSegmentationBox (int X, int Y, int Width, int Height);
public sealed record SpriteSegmentationPoint (double X, double Y);
