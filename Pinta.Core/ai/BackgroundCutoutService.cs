using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta.Core.AI;

public sealed record BackgroundCutoutResult (
	byte[] WhiteBackgroundPng,
	byte[] BlackBackgroundPng);

public sealed class BackgroundCutoutService
{
	private readonly AiApiClient api;
	private const string agnes_cutout_path = "api/agnes-images/cutout-backgrounds";
	private const string agnes_image_size = "2K";
	private const string gpt_image_path = "api/gpt-images";
	private const string prompt_config_file = "gpt-image-prompts.json";
	private const string default_white_prompt = "Edit only the background. Replace every background area, cast shadow, reflection, halo, watermark, and unrelated scenery with pure white (#FFFFFF). Preserve the foreground subject pixels as much as possible: same canvas, position, scale, shape, colors, lighting, edges, and details. Do not crop, resize, repaint, stylize, recolor, or move the subject.";
	private const string default_black_prompt = "Starting from this white-background image, edit only the pure white background to pure black (#000000). Preserve the foreground subject pixels as much as possible: same canvas, position, scale, shape, colors, lighting, edges, and details. Do not crop, resize, repaint, stylize, recolor, or move the subject.";
	private static readonly JsonSerializerOptions prompt_config_json_options = new () {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	public BackgroundCutoutService (AiAuthService auth)
	{
		api = new (auth);
	}

	public async Task<BackgroundCutoutResult> GenerateAsync (
		byte[] sourcePng,
		Size targetSize,
		Action<string, double>? reportProgress = null,
		Action<string, byte[]>? saveResult = null,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
	{
		string imageService = AiRequestSettings.GetImageService (PintaCore.Settings);
		Log (log, $"AI cutout start: service={imageService}, source_size={FormatSize (GetPngSize (sourcePng))}, source_bytes={sourcePng.Length}, target_size={FormatSize (targetSize)}");

		BackgroundCutoutResult rawResult;
		if (imageService == AiRequestSettings.AgnesService) {
			rawResult = await GenerateAgnesAsync (sourcePng, targetSize, reportProgress, saveResult, log, cancellationToken);
		} else {
			string provider = AiRequestSettings.GetGptProvider (PintaCore.Settings);
			rawResult = await GenerateGptAsync (sourcePng, targetSize, provider, reportProgress, saveResult, log, cancellationToken);
		}

		reportProgress?.Invoke (Translations.GetString ("Normalizing AI images..."), 0.65);
		byte[] white = NormalizePngSize (rawResult.WhiteBackgroundPng, targetSize, Translations.GetString ("white background"), log);
		byte[] black = NormalizePngSize (rawResult.BlackBackgroundPng, targetSize, Translations.GetString ("black background"), log);
		saveResult?.Invoke ("white-background.png", white);
		saveResult?.Invoke ("black-background.png", black);

		return new (white, black);
	}

	private async Task<BackgroundCutoutResult> GenerateGptAsync (
		byte[] sourcePng,
		Size targetSize,
		string provider,
		Action<string, double>? reportProgress,
		Action<string, byte[]>? saveResult,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		string size = GetGptImageRequestSize (targetSize);
		BackgroundCutoutPromptConfig promptConfig = ReadPromptConfig ();
		byte[] white = await GenerateBackgroundAsync (
			gpt_image_path,
			sourcePng,
			size,
			provider,
			promptConfig.WhiteBackgroundPrompt,
			Translations.GetString ("white background"),
			"white-background.png",
			0.2,
			reportProgress,
			saveResult,
			log,
			cancellationToken);

		byte[] black = await GenerateBackgroundAsync (
			gpt_image_path,
			white,
			size,
			provider,
			promptConfig.BlackBackgroundPrompt,
			Translations.GetString ("black background"),
			"black-background.png",
			0.45,
			reportProgress,
			saveResult,
			log,
			cancellationToken);

		return new (white, black);
	}

	private async Task<BackgroundCutoutResult> GenerateAgnesAsync (
		byte[] sourcePng,
		Size targetSize,
		Action<string, double>? reportProgress,
		Action<string, byte[]>? saveResult,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		string ratio = GetAgnesImageRatio (targetSize);
		KeyValuePair<string, string>[] fields = [
			new ("size", agnes_image_size),
			new ("ratio", ratio),
		];

		reportProgress?.Invoke ($"Generating cutout backgrounds ({agnes_image_size}, {ratio})...", 0.2);
		Log (log, $"AI image request: name=cutout backgrounds, path={agnes_cutout_path}, file=pinta.png, input_size={FormatSize (GetPngSize (sourcePng))}, input_bytes={sourcePng.Length}, form_size={agnes_image_size}, ratio={ratio}");
		using JsonDocument json = await RunImageJobAsync (
			agnes_cutout_path,
			sourcePng,
			fields,
			"cutout backgrounds",
			agnes_image_size,
			log,
			cancellationToken);

		JsonElement root = json.RootElement;
		if (!TryReadImage (root, "white_result_b64_json", out byte[]? white) ||
			!TryReadImage (root, "black_result_b64_json", out byte[]? black))
			throw new InvalidOperationException ("Agnes cutout response did not include both background images.");

		Log (log, $"AI image response: name=cutout backgrounds, white_size={FormatSize (GetPngSize (white!))}, black_size={FormatSize (GetPngSize (black!))}, form_size={ReadStringProperty (root, "size")}, ratio={ReadStringProperty (root, "ratio")}");
		reportProgress?.Invoke (Translations.GetString ("Saving AI images..."), 0.55);
		saveResult?.Invoke ("white-background.raw.png", white!);
		saveResult?.Invoke ("black-background.raw.png", black!);
		return new (white!, black!);
	}

	private async Task<byte[]> GenerateBackgroundAsync (
		string path,
		byte[] png,
		string size,
		string provider,
		string prompt,
		string name,
		string fileName,
		double progressStart,
		Action<string, double>? reportProgress,
		Action<string, byte[]>? saveResult,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		reportProgress?.Invoke ($"Generating {name} ({size})...", progressStart);
		byte[] rawResult = await GenerateBackgroundOnceAsync (path, png, size, provider, prompt, name, log, cancellationToken);
		reportProgress?.Invoke ($"Saving {name}...", progressStart + 0.1);
		saveResult?.Invoke (GetRawResultFileName (fileName), rawResult);
		return rawResult;
	}

	private async Task<byte[]> GenerateBackgroundOnceAsync (
		string path,
		byte[] png,
		string size,
		string provider,
		string prompt,
		string name,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		KeyValuePair<string, string>[] fields = [
			new ("size", size),
			new ("provider", provider),
			new ("prompt", prompt),
		];

		Log (log, $"AI image request: name={name}, path={path}, provider={provider}, file=pinta.png, input_size={FormatSize (GetPngSize (png))}, input_bytes={png.Length}, form_size={size}, prompt_bytes={System.Text.Encoding.UTF8.GetByteCount (prompt)}");
		using JsonDocument json = await RunImageJobAsync (
			path,
			png,
			fields,
			name,
			size,
			log,
			cancellationToken);

		JsonElement root = json.RootElement;

		if (TryReadImage (root, "result_b64_json", out byte[]? result)) {
			Log (log, $"AI image response: name={name}, source=result_b64_json, returned_size={FormatSize (GetPngSize (result!))}, returned_bytes={result!.Length}, form_size={ReadStringProperty (root, "size")}");
			return result!;
		}

		if (root.TryGetProperty ("result_url", out JsonElement urlElement) &&
			urlElement.GetString () is string url &&
			!string.IsNullOrWhiteSpace (url)) {
			byte[] downloaded = await api.GetBytesAsync (url, cancellationToken);
			Log (log, $"AI image response: name={name}, source=result_url, returned_size={FormatSize (GetPngSize (downloaded))}, returned_bytes={downloaded.Length}, form_size={ReadStringProperty (root, "size")}, url={url}");
			return downloaded;
		}

		throw new InvalidOperationException ("Image response did not include a result image.");
	}

	private async Task<JsonDocument> RunImageJobAsync (
		string path,
		byte[] png,
		IEnumerable<KeyValuePair<string, string>> fields,
		string name,
		string size,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		using JsonDocument job = JsonDocument.Parse (await api.PostPngAsync (
			path,
			png,
			"file",
			"pinta.png",
			cancellationToken,
			fields));
		string jobId = job.RootElement.GetProperty ("id").GetString ()
			?? throw new InvalidOperationException ("Image response did not include a job id.");
		Log (log, $"AI image job accepted: name={name}, job_id={jobId}, form_size={size}");
		return await WaitForResultAsync (jobId, name, log, cancellationToken);
	}

	private async Task<JsonDocument> WaitForResultAsync (
		string jobId,
		string name,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		string jobPath = $"api/images/jobs/{jobId}";
		while (true) {
			using JsonDocument job = JsonDocument.Parse (await api.GetStringAsync (jobPath, cancellationToken));
			string status = job.RootElement.GetProperty ("status").GetString () ?? "";
			Log (log, $"AI image job status: name={name}, job_id={jobId}, status={status}");
			switch (status) {
				case "completed":
					return JsonDocument.Parse (await api.GetStringAsync ($"{jobPath}/result", cancellationToken));
				case "failed":
					string error = job.RootElement.TryGetProperty ("error_message", out JsonElement errorElement)
						? errorElement.GetString () ?? "Unknown server error."
						: "Unknown server error.";
					throw new InvalidOperationException ($"Image job failed: {error}");
				case "queued":
				case "processing":
					await Task.Delay (TimeSpan.FromSeconds (1), cancellationToken);
					break;
				default:
					throw new InvalidOperationException ($"Image job returned unknown status: {status}");
			}
		}
	}

	private static BackgroundCutoutPromptConfig ReadPromptConfig ()
	{
		string path = GetPromptConfigPath ();
		Directory.CreateDirectory (Path.GetDirectoryName (path)!);

		if (!File.Exists (path)) {
			BackgroundCutoutPromptConfig defaults = new ();
			File.WriteAllText (path, JsonSerializer.Serialize (defaults, prompt_config_json_options));
			return defaults;
		}

		string json = File.ReadAllText (path);
		BackgroundCutoutPromptConfig config =
			JsonSerializer.Deserialize<BackgroundCutoutPromptConfig> (json, prompt_config_json_options)
			?? throw new InvalidOperationException ($"Prompt config is empty: {path}");

		if (string.IsNullOrWhiteSpace (config.WhiteBackgroundPrompt) ||
			string.IsNullOrWhiteSpace (config.BlackBackgroundPrompt))
			throw new InvalidOperationException ($"Prompt config must include whiteBackgroundPrompt and blackBackgroundPrompt: {path}");

		return config;
	}

	private static string GetPromptConfigPath ()
		=> Path.Combine (AppContext.BaseDirectory, "config", prompt_config_file);

	private static string GetGptImageRequestSize (Size imageSize)
	{
		if (imageSize.Width <= 0 || imageSize.Height <= 0)
			return "1024x1024";

		return $"{imageSize.Width}x{imageSize.Height}";
	}

	private static string GetAgnesImageRatio (Size imageSize)
	{
		if (imageSize.Width <= 0 || imageSize.Height <= 0)
			return "1:1";

		double aspect = (double) imageSize.Width / imageSize.Height;
		(string Name, double Value)[] ratios = [
			("1:1", 1.0),
			("3:4", 3.0 / 4.0),
			("4:3", 4.0 / 3.0),
			("16:9", 16.0 / 9.0),
			("9:16", 9.0 / 16.0),
			("2:3", 2.0 / 3.0),
			("3:2", 3.0 / 2.0),
			("21:9", 21.0 / 9.0),
		];
		(string Name, double Value) closest = ratios[0];
		foreach ((string Name, double Value) ratio in ratios) {
			if (Math.Abs (ratio.Value - aspect) < Math.Abs (closest.Value - aspect))
				closest = ratio;
		}

		return closest.Name;
	}

	private static byte[] NormalizePngSize (
		byte[] png,
		Size targetSize,
		string name,
		Action<string>? log)
	{
		using GdkPixbuf.Pixbuf pixbuf = LoadPixbuf (png);
		Size returnedSize = new (pixbuf.Width, pixbuf.Height);
		if (returnedSize == targetSize) {
			Log (log, $"AI image normalize: name={name}, action=none, size={FormatSize (returnedSize)}, bytes={png.Length}");
			return png;
		}

		using GdkPixbuf.Pixbuf scaled = pixbuf.ScaleSimple (
			targetSize.Width,
			targetSize.Height,
			GdkPixbuf.InterpType.Hyper)
			?? throw new InvalidOperationException ("Unable to resize AI image response.");
		byte[] result = scaled.SaveToBuffer ("png");
		Log (log, $"AI image normalize: name={name}, action=resize, from={FormatSize (returnedSize)}, to={FormatSize (targetSize)}, filter=hyper, raw_bytes={png.Length}, normalized_bytes={result.Length}");
		return result;
	}

	private static Size GetPngSize (byte[] png)
	{
		using GdkPixbuf.Pixbuf pixbuf = LoadPixbuf (png);
		return new (pixbuf.Width, pixbuf.Height);
	}

	private static GdkPixbuf.Pixbuf LoadPixbuf (byte[] png)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		return GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
	}

	private static string GetRawResultFileName (string fileName)
		=> $"{Path.GetFileNameWithoutExtension (fileName)}.raw{Path.GetExtension (fileName)}";

	private static string FormatSize (Size size)
		=> $"{size.Width}x{size.Height}";

	private static string ReadStringProperty (JsonElement root, string propertyName)
		=> root.TryGetProperty (propertyName, out JsonElement value) ? value.ToString () : "";

	private static void Log (Action<string>? log, string message)
	{
		Console.WriteLine (message);
		log?.Invoke (message);
	}

	private static bool TryReadImage (JsonElement root, string propertyName, out byte[]? image)
	{
		image = null;
		if (!root.TryGetProperty (propertyName, out JsonElement b64) ||
			b64.GetString () is not string b64Value ||
			string.IsNullOrWhiteSpace (b64Value))
			return false;

		image = Convert.FromBase64String (b64Value);
		return true;
	}

	private sealed class BackgroundCutoutPromptConfig
	{
		public string WhiteBackgroundPrompt { get; set; } = default_white_prompt;
		public string BlackBackgroundPrompt { get; set; } = default_black_prompt;
	}
}
