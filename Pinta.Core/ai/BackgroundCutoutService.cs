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
	private const string gpt_image_path = "api/gpt-images";
	private const string prompt_config_file = "gpt-image-prompts.json";
	private const string default_white_prompt = "\u5c06\u56fe\u7247\u80cc\u666f\u4fee\u6539\u4e3a\u7eaf\u767d\u8272\uff0c\u4e0d\u8981\u4fee\u6539\u4efb\u4f55\u7ed8\u5236\u4fe1\u606f\u3002";
	private const string default_black_prompt = "\u5c06\u80cc\u666f\u4fee\u6539\u4e3a\u7eaf\u9ed1\u8272\uff0c\u4e0d\u8981\u4fee\u6539\u4efb\u4f55\u7ed8\u5236\u4fe1\u606f\u3002\u53ea\u9700\u8981\u5c06\u767d\u8272\u80cc\u666f\u4fee\u6539\u4e3a\u9ed1\u8272\u80cc\u666f\u3002";
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
		string size,
		Action<string, double>? reportProgress = null,
		Action<string, byte[]>? saveResult = null,
		CancellationToken cancellationToken = default)
	{
		byte[] white = await GenerateBackgroundAsync (
			gpt_image_path,
			sourcePng,
			size,
			ReadPromptConfig ().WhiteBackgroundPrompt,
			Translations.GetString ("white background"),
			"white-background.png",
			0.2,
			reportProgress,
			saveResult,
			cancellationToken);

		byte[] black = await GenerateBackgroundAsync (
			gpt_image_path,
			white,
			size,
			ReadPromptConfig ().BlackBackgroundPrompt,
			Translations.GetString ("black background"),
			"black-background.png",
			0.45,
			reportProgress,
			saveResult,
			cancellationToken);

		return new (white, black);
	}

	private async Task<byte[]> GenerateBackgroundAsync (
		string path,
		byte[] png,
		string size,
		string prompt,
		string name,
		string fileName,
		double progressStart,
		Action<string, double>? reportProgress,
		Action<string, byte[]>? saveResult,
		CancellationToken cancellationToken)
	{
		reportProgress?.Invoke ($"Generating {name} ({size})...", progressStart);
		byte[] result = await GenerateBackgroundOnceAsync (path, png, size, prompt, cancellationToken);
		reportProgress?.Invoke ($"Saving {name}...", progressStart + 0.1);
		saveResult?.Invoke (fileName, result);
		return result;
	}

	private async Task<byte[]> GenerateBackgroundOnceAsync (
		string path,
		byte[] png,
		string size,
		string prompt,
		CancellationToken cancellationToken)
	{
		KeyValuePair<string, string>[] fields = [
			new ("size", size),
			new ("prompt", prompt),
		];

		Console.WriteLine ($"AI image request: path={path}, file=pinta.png, image_bytes={png.Length}, size={size}, prompt_bytes={System.Text.Encoding.UTF8.GetByteCount (prompt)}");

		using JsonDocument job = JsonDocument.Parse (await api.PostPngAsync (
			path,
			png,
			"file",
			"pinta.png",
			cancellationToken,
			fields));
		string jobId = job.RootElement.GetProperty ("id").GetString ()
			?? throw new InvalidOperationException ("Image response did not include a job id.");

		using JsonDocument json = await WaitForResultAsync (jobId, cancellationToken);

		JsonElement root = json.RootElement;

		if (TryReadImage (root, "result_b64_json", out byte[]? result))
			return result!;

		if (root.TryGetProperty ("result_url", out JsonElement urlElement) &&
			urlElement.GetString () is string url &&
			!string.IsNullOrWhiteSpace (url))
			return await api.GetBytesAsync (url, cancellationToken);

		throw new InvalidOperationException ("Image response did not include a result image.");
	}

	private async Task<JsonDocument> WaitForResultAsync (
		string jobId,
		CancellationToken cancellationToken)
	{
		string jobPath = $"api/images/jobs/{jobId}";
		while (true) {
			using JsonDocument job = JsonDocument.Parse (await api.GetStringAsync (jobPath, cancellationToken));
			string status = job.RootElement.GetProperty ("status").GetString () ?? "";
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
