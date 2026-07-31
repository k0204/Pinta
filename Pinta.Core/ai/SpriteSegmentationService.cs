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
		string prompt = $"{ReadPrompt ()}\n\n" +
			$"The input image dimensions are exactly {imageWidth}x{imageHeight} pixels. " +
			$"Return image_width={imageWidth} and image_height={imageHeight}; do not estimate them.";
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
			UnwrapJsonCodeFence (json),
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
			?? throw new InvalidOperationException ("Sprite analysis JSON was empty.");
		if (analysis.Items is null)
			throw new InvalidOperationException ("Sprite analysis did not include items.");
		Validate (analysis, imageWidth, imageHeight);
		return analysis with { Items = [.. analysis.Items.OrderBy (item => item.Index)] };
	}

	private static string UnwrapJsonCodeFence (string response)
	{
		string json = response.Trim ();
		if (!json.StartsWith ("```", StringComparison.Ordinal) ||
			!json.EndsWith ("```", StringComparison.Ordinal))
			return json;

		int contentStart = json.IndexOf ('\n');
		if (contentStart < 0)
			return json;

		string fence = json[..contentStart].TrimEnd ('\r');
		if (fence != "```" && !fence.Equals ("```json", StringComparison.OrdinalIgnoreCase))
			return json;

		return json[(contentStart + 1)..^3].Trim ();
	}

	private static string ReadPrompt ()
	{
		string path = Path.Combine (AppContext.BaseDirectory, "config", prompt_config_file);
		string prompt = File.ReadAllText (path).Trim ();
		if (string.IsNullOrWhiteSpace (prompt))
			throw new InvalidOperationException ($"Sprite segmentation prompt is missing: {path}");
		return prompt;
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
