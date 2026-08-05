using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta.Core.AI;

public sealed class BackgroundCutoutService
{
	private readonly AiJobService jobs;
	private const int gpt_size_multiple = 16;
	private const int gpt_min_pixels = 655_360;
	private const int gpt_max_pixels = 8_294_400;
	private const int gpt_max_edge = 3_840;
	private const int gpt_max_aspect_ratio = 3;
	private const string prompt_config_file = "gpt-image-prompts.json";
	private const string default_white_prompt = "Edit only the background. Replace every background area, cast shadow, reflection, halo, watermark, and unrelated scenery with pure white (#FFFFFF). Preserve the foreground subject pixels as much as possible: same canvas, position, scale, shape, colors, lighting, edges, and details. Do not crop, resize, repaint, stylize, recolor, or move the subject.";
	private const string default_black_prompt = "Starting from this white-background image, edit only the pure white background to pure black (#000000). Preserve the foreground subject pixels as much as possible: same canvas, position, scale, shape, colors, lighting, edges, and details. Do not crop, resize, repaint, stylize, recolor, or move the subject.";
	private static readonly JsonSerializerOptions prompt_config_json_options = new () {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};
	private static readonly Size[] agnes_image_sizes = [
		new (1024, 1024), new (2048, 2048), new (3072, 3072), new (4096, 4096),
		new (864, 1152), new (1728, 2304), new (2592, 3456), new (3456, 4608),
		new (1152, 864), new (2304, 1728), new (3456, 2592), new (4608, 3456),
		new (1312, 736), new (2624, 1472), new (3936, 2208), new (5248, 2944),
		new (736, 1312), new (1472, 2624), new (2208, 3936), new (2944, 5248),
		new (832, 1248), new (1664, 2496), new (2496, 3744), new (3328, 4992),
		new (1248, 832), new (2496, 1664), new (3744, 2496), new (4992, 3328),
		new (1568, 672), new (3136, 1344), new (4704, 2016), new (6272, 2688),
	];
	private static readonly Size[] gpt_image_generation_sizes = [
		new (1024, 1024), new (2048, 2048),
		new (1536, 1024), new (1024, 1536),
		new (1536, 864), new (864, 1536),
		new (1280, 960), new (960, 1280),
		new (1280, 1024), new (1024, 1280),
		new (1792, 1024), new (1024, 1792), new (1792, 768),
		new (2560, 1440), new (1440, 2560),
		new (3840, 2160), new (2160, 3840),
	];

	public BackgroundCutoutService (AiAuthService auth)
	{
		jobs = new (auth);
	}

	public Task<byte[]> GenerateWhiteAsync (
		byte[] sourcePng,
		Size targetSize,
		string prompt,
		IReadOnlyList<(byte[] Png, string FileName)> referenceImages,
		Action<string, double>? reportProgress = null,
		Action<string, byte[]>? saveResult = null,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
		=> GenerateAsync (
			sourcePng,
			targetSize,
			prompt,
			Translations.GetString ("white background"),
			"white-background.png",
			whitePadding: false,
			referenceImages,
			reportProgress,
			saveResult,
			log,
			cancellationToken);

	public Task<byte[]> GenerateBlackAsync (
		byte[] whiteBackgroundPng,
		Size targetSize,
		Action<string, double>? reportProgress = null,
		Action<string, byte[]>? saveResult = null,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
		=> GenerateAsync (
			whiteBackgroundPng,
			targetSize,
			ReadPromptConfig ().BlackBackgroundPrompt,
			Translations.GetString ("black background"),
			"black-background.png",
			whitePadding: true,
			referenceImages: [],
			reportProgress,
			saveResult,
			log,
			cancellationToken);

	public Task<byte[]> GenerateImageAsync (
		Size targetSize,
		string prompt,
		IReadOnlyList<(byte[] Png, string FileName)> referenceImages,
		Action<string, double>? reportProgress = null,
		Action<string, byte[]>? saveResult = null,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
		=> GenerateAsync (
			sourcePng: null,
			targetSize,
			prompt,
			Translations.GetString ("image"),
			"generated-image.png",
			whitePadding: false,
			referenceImages,
			reportProgress,
			saveResult,
			log,
			cancellationToken);

	public static string GetDefaultBackgroundCleanupPrompt ()
		=> ReadPromptConfig ().WhiteBackgroundPrompt;

	public static IReadOnlyList<Size> GetImageGenerationSizes (string imageService)
		=> imageService == AiRequestSettings.AgnesService
			? agnes_image_sizes
			: gpt_image_generation_sizes;

	public static string? GetGptImageSizeError (Size size)
	{
		if (size.Width <= 0 || size.Height <= 0)
			return Translations.GetString ("Width and height must be positive whole numbers.");
		if (size.Width % gpt_size_multiple != 0 || size.Height % gpt_size_multiple != 0)
			return string.Format (Translations.GetString ("Width and height must be divisible by {0}."), gpt_size_multiple);
		long pixels = (long) size.Width * size.Height;
		if (pixels < gpt_min_pixels || pixels > gpt_max_pixels)
			return string.Format (
				Translations.GetString ("Total pixels must be between {0:N0} and {1:N0}."),
				gpt_min_pixels,
				gpt_max_pixels);
		if (Math.Max (size.Width, size.Height) > gpt_max_edge)
			return string.Format (Translations.GetString ("The longest edge cannot exceed {0:N0} pixels."), gpt_max_edge);
		if (Math.Max (size.Width, size.Height) > Math.Min (size.Width, size.Height) * gpt_max_aspect_ratio)
			return string.Format (Translations.GetString ("Aspect ratio cannot exceed {0}:1."), gpt_max_aspect_ratio);
		return null;
	}

	public async Task<byte[]> GenerateBaiduCutoutAsync (
		byte[] sourcePng,
		Size targetSize,
		RectangleI? controlBox,
		Action<string, double>? reportProgress = null,
		Action<string, byte[]>? saveResult = null,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
	{
		string name = Translations.GetString ("transparent cutout");
		string size = FormatSize (targetSize);
		string mode = controlBox is null ? "auto" : "control";
		reportProgress?.Invoke (Translations.GetString ("Requesting Baidu intelligent cutout..."), 0.25);
		Log (log, $"AI image start: service=baidu, mode={mode}, name={name}, target_size={size}, request_size={size}, images=1");
		using JsonDocument json = await jobs.RunBaiduCutoutAsync (
			sourcePng,
			controlBox,
			returnForm: "rgba",
			log: log,
			cancellationToken: cancellationToken);
		if (!TryReadImage (json.RootElement, "result_b64_json", out byte[]? rawResult))
			throw new InvalidOperationException ("Baidu response did not include a foreground image.");

		saveResult?.Invoke ("transparent-cutout.raw.png", rawResult!);
		byte[] normalized = NormalizePngSize (rawResult!, targetSize, targetSize, PointI.Zero, name, log);
		saveResult?.Invoke ("transparent-cutout.png", normalized);
		return normalized;
	}

	private async Task<byte[]> GenerateAsync (
		byte[]? sourcePng,
		Size targetSize,
		string prompt,
		string name,
		string fileName,
		bool whitePadding,
		IReadOnlyList<(byte[] Png, string FileName)> referenceImages,
		Action<string, double>? reportProgress,
		Action<string, byte[]>? saveResult,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		string imageService = AiRequestSettings.GetImageService (PintaCore.Settings);
		if (sourcePng is null && imageService == AiRequestSettings.GptImageService && GetGptImageSizeError (targetSize) is string sizeError)
			throw new InvalidOperationException (sizeError);
		Size requestSize = sourcePng is null ? targetSize : GetImageRequestSize (imageService, targetSize);
		string size = FormatSize (requestSize);
		PointI contentOffset = PointI.Zero;
		string provider = imageService == AiRequestSettings.AgnesService
			? AiRequestSettings.AgnesService
			: AiRequestSettings.GetGptProvider (PintaCore.Settings);
		KeyValuePair<string, string>[] fields = [
			new ("size", size),
			new ("provider", provider),
			new ("prompt", prompt),
		];

		int sourceCount = sourcePng is null ? 0 : 1;
		(byte[] Data, string FormName, string FileName)[] files = new (byte[], string, string)[sourceCount + referenceImages.Count];
		if (sourcePng is not null)
			files[0] = (PadPng (sourcePng, requestSize, whitePadding, out contentOffset), "reference_files", "pinta.png");
		for (int i = 0; i < referenceImages.Count; i++)
			files[sourceCount + i] = (referenceImages[i].Png, "reference_files", referenceImages[i].FileName);

		Log (log, $"AI image start: service={imageService}, name={name}, target_size={FormatSize (targetSize)}, request_size={size}, content_offset={contentOffset.X},{contentOffset.Y}, images={files.Length}");
		reportProgress?.Invoke ($"Generating {name} ({size})...", 0.25);
		using JsonDocument json = await jobs.RunImageAsync (
			fields,
			files,
			log,
			cancellationToken);

		JsonElement root = json.RootElement;
		byte[] rawResult;
		if (TryReadImage (root, "result_b64_json", out byte[]? result))
			rawResult = result!;
		else if (root.TryGetProperty ("result_url", out JsonElement urlElement) &&
			urlElement.GetString () is string url &&
			!string.IsNullOrWhiteSpace (url))
			rawResult = await jobs.DownloadAsync (url, cancellationToken);
		else
			throw new InvalidOperationException ("Image response did not include a result image.");

		Log (log, $"AI image response: name={name}, returned_size={FormatSize (GetPngSize (rawResult))}, returned_bytes={rawResult.Length}, form_size={ReadStringProperty (root, "size")}");
		saveResult?.Invoke (GetRawResultFileName (fileName), rawResult);
		reportProgress?.Invoke (Translations.GetString ("Normalizing AI image..."), 0.7);
		byte[] normalized = NormalizePngSize (rawResult, targetSize, requestSize, contentOffset, name, log);
		saveResult?.Invoke (fileName, normalized);
		return normalized;
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

	private static Size GetImageRequestSize (string imageService, Size imageSize)
	{
		if (imageSize.Width <= 0 || imageSize.Height <= 0)
			return new (1024, 1024);

		return imageService == AiRequestSettings.AgnesService
			? GetAgnesImageRequestSize (imageSize)
			: GetGptImageRequestSize (imageSize);
	}

	private static Size GetAgnesImageRequestSize (Size imageSize)
	{
		Size? closest = null;
		double closestDistance = double.MaxValue;
		foreach (Size candidate in agnes_image_sizes) {
			if (candidate.Width < imageSize.Width || candidate.Height < imageSize.Height)
				continue;

			double distance = Math.Pow ((double) candidate.Width / imageSize.Width - 1, 2)
				+ Math.Pow ((double) candidate.Height / imageSize.Height - 1, 2);
			if (distance < closestDistance) {
				closest = candidate;
				closestDistance = distance;
			}
		}

		return closest ?? throw new InvalidOperationException ("Image is larger than every supported Agnes image size.");
	}

	private static Size GetGptImageRequestSize (Size imageSize)
	{
		Size? closest = null;
		double closestDistance = double.MaxValue;
		int firstWidth = RoundUp (imageSize.Width, gpt_size_multiple);
		for (int width = firstWidth; width <= gpt_max_edge; width += gpt_size_multiple) {
			int minimumHeight = Math.Max (imageSize.Height, Math.Max (
				(int) Math.Ceiling ((double) gpt_min_pixels / width),
				(int) Math.Ceiling ((double) width / gpt_max_aspect_ratio)));
			int height = RoundUp (minimumHeight, gpt_size_multiple);
			if (height > gpt_max_edge || width * height > gpt_max_pixels || height > width * gpt_max_aspect_ratio)
				continue;

			double distance = Math.Pow ((double) width / imageSize.Width - 1, 2)
				+ Math.Pow ((double) height / imageSize.Height - 1, 2);
			if (distance < closestDistance) {
				closest = new (width, height);
				closestDistance = distance;
			}
		}

		return closest ?? throw new InvalidOperationException ("Image cannot be padded to a supported GPT Image size.");
	}

	private static int RoundUp (int value, int multiple)
		=> checked((value + multiple - 1) / multiple * multiple);

	private static byte[] PadPng (byte[] png, Size requestSize, bool whitePadding, out PointI contentOffset)
	{
		using GdkPixbuf.Pixbuf pixbuf = LoadPixbuf (png);
		if (pixbuf.Width > requestSize.Width || pixbuf.Height > requestSize.Height)
			throw new InvalidOperationException ("Image is larger than the selected request size.");

		contentOffset = new ((requestSize.Width - pixbuf.Width) / 2, (requestSize.Height - pixbuf.Height) / 2);
		if (pixbuf.Width == requestSize.Width && pixbuf.Height == requestSize.Height)
			return png;

		using Cairo.ImageSurface surface = CairoExtensions.CreateImageSurface (Cairo.Format.Argb32, requestSize.Width, requestSize.Height);
		if (whitePadding) {
			using Cairo.Context context = new (surface);
			context.SetSourceColor (new Cairo.Color (1, 1, 1));
			context.Paint ();
		} else {
			surface.Clear ();
		}
		using (Cairo.Context context = new (surface))
			context.DrawPixbuf (pixbuf, contentOffset.X, contentOffset.Y);
		using GdkPixbuf.Pixbuf padded = surface.ToPixbuf ();
		return padded.SaveToBuffer ("png");
	}

	private static byte[] NormalizePngSize (
		byte[] png,
		Size targetSize,
		Size requestSize,
		PointI contentOffset,
		string name,
		Action<string>? log)
	{
		using GdkPixbuf.Pixbuf pixbuf = LoadPixbuf (png);
		Size returnedSize = new (pixbuf.Width, pixbuf.Height);
		if (returnedSize == targetSize) {
			Log (log, $"AI image normalize: name={name}, action=none, size={FormatSize (returnedSize)}, bytes={png.Length}");
			return png;
		}
		if (returnedSize == requestSize && targetSize.Width <= returnedSize.Width && targetSize.Height <= returnedSize.Height) {
			using Cairo.ImageSurface surface = CairoExtensions.CreateImageSurface (Cairo.Format.Argb32, targetSize.Width, targetSize.Height);
			surface.Clear ();
			using (Cairo.Context context = new (surface))
				context.DrawPixbuf (pixbuf, -contentOffset.X, -contentOffset.Y);
			using GdkPixbuf.Pixbuf cropped = surface.ToPixbuf ();
			byte[] croppedResult = cropped.SaveToBuffer ("png");
			Log (log, $"AI image normalize: name={name}, action=center-crop, from={FormatSize (returnedSize)}, to={FormatSize (targetSize)}, offset={contentOffset.X},{contentOffset.Y}, raw_bytes={png.Length}, normalized_bytes={croppedResult.Length}");
			return croppedResult;
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
