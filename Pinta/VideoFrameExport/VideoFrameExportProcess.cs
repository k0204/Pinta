using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta;

internal static class VideoFrameExportProcess
{
	private const int MaxPreviewFrames = 240;

	public static async Task<VideoMetadata> ProbeAsync (string filename, CancellationToken cancellationToken)
	{
		ProcessResult result = await RunToolAsync (
			"ffprobe",
			[
				"-v", "error",
				"-select_streams", "v:0",
				"-show_entries", "stream=width,height,avg_frame_rate,nb_frames,duration:format=duration",
				"-of", "json",
				filename
			],
			cancellationToken);

		try {
			using JsonDocument document = JsonDocument.Parse (result.Output);
			JsonElement root = document.RootElement;
			JsonElement stream = root.GetProperty ("streams")[0];
			int width = stream.GetProperty ("width").GetInt32 ();
			int height = stream.GetProperty ("height").GetInt32 ();
			double frameRate = ParseRate (stream.GetProperty ("avg_frame_rate"));
			double duration = ParseDuration (stream, root);
			int totalFrames = ParseFrameCount (stream, duration, frameRate);

			if (width <= 0 || height <= 0 || frameRate <= 0 || duration <= 0 || totalFrames <= 0)
				throw new InvalidDataException (Translations.GetString ("Video metadata is incomplete."));

			return new VideoMetadata (width, height, frameRate, duration, totalFrames);
		} catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or IndexOutOfRangeException) {
			throw new VideoFrameExportException (
				Translations.GetString ("Could not read video metadata."),
				ex.ToString ());
		}
	}

	public static async Task<IReadOnlyList<string>> ExtractPreviewFilesAsync (
		string filename,
		VideoMetadata metadata,
		string outputDirectory,
		CancellationToken cancellationToken)
	{
		int[] indices = GetPreviewIndices (metadata.TotalFrames, Math.Min (metadata.TotalFrames, MaxPreviewFrames));
		string selection = string.Join ('+', indices.Select (index => $"eq(n\\,{index})"));
		string filter = $"select='{selection}',scale=320:-2:force_original_aspect_ratio=decrease";
		string outputPattern = Path.Combine (outputDirectory, "%06d.png");

		await RunToolAsync (
			"ffmpeg",
			[
				"-hide_banner", "-loglevel", "error", "-y",
				"-i", filename,
				"-map", "0:v:0",
				"-vf", filter,
				"-fps_mode", "vfr",
				"-start_number", "0",
				"-q:v", "6",
				outputPattern
			],
			cancellationToken);

		return Directory.GetFiles (outputDirectory, "*.png")
			.OrderBy (path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray ();
	}

	public static async Task ExportAsync (
		string filename,
		VideoMetadata metadata,
		IReadOnlyCollection<int> selectedIndices,
		bool exportAll,
		string outputDirectory,
		string prefix,
		int digits,
		CancellationToken cancellationToken)
	{
		ValidateOutput (outputDirectory, prefix, digits);
		Directory.CreateDirectory (outputDirectory);

		string outputPattern = Path.Combine (outputDirectory, $"{prefix}%0{digits}d.png");
		List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y", "-i", filename, "-map", "0:v:0"];
		if (!exportAll) {
			string selection = string.Join ('+', selectedIndices.Order ().Select (index => $"eq(n\\,{index})"));
			arguments.AddRange (["-vf", $"select='{selection}'", "-fps_mode", "vfr"]);
		} else {
			arguments.AddRange (["-fps_mode", "vfr"]);
		}

		arguments.AddRange (["-an", "-sn", "-dn", "-c:v", "png", "-compression_level", "3", "-start_number", "1", outputPattern]);
		await RunToolAsync ("ffmpeg", arguments, cancellationToken);

		if (!Directory.EnumerateFiles (outputDirectory, $"{prefix}*.png").Any ())
			throw new VideoFrameExportException (Translations.GetString ("FFmpeg did not export any frames."));
	}

	private static async Task<ProcessResult> RunToolAsync (
		string tool,
		IEnumerable<string> arguments,
		CancellationToken cancellationToken)
	{
		ProcessStartInfo startInfo = new () {
			FileName = tool,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (string argument in arguments)
			startInfo.ArgumentList.Add (argument);

		using Process process = new () { StartInfo = startInfo };
		try {
			if (!process.Start ())
				throw new InvalidOperationException (Translations.GetString ("Could not start the video tool."));
		} catch (System.ComponentModel.Win32Exception ex) {
			throw new VideoFrameExportException (
				Translations.GetString ("FFmpeg and FFprobe are required for frame preview and export."),
				ex.ToString ());
		}

		Task<string> outputTask = process.StandardOutput.ReadToEndAsync (cancellationToken);
		Task<string> errorTask = process.StandardError.ReadToEndAsync (cancellationToken);
		try {
			await process.WaitForExitAsync (cancellationToken);
		} catch (OperationCanceledException) {
			TryKill (process);
			throw;
		}

		string output = await outputTask;
		string error = await errorTask;
		if (process.ExitCode != 0)
			throw new VideoFrameExportException (
				Translations.GetString ("The video tool could not process this file."),
				error);

		return new ProcessResult (output, error);
	}

	private static void TryKill (Process process)
	{
		try {
			if (!process.HasExited)
				process.Kill (entireProcessTree: true);
		} catch (InvalidOperationException) {
		}
	}

	internal static int[] GetPreviewIndices (int totalFrames, int previewCount)
	{
		previewCount = Math.Clamp (previewCount, 0, Math.Min (totalFrames, MaxPreviewFrames));
		if (previewCount == 0)
			return [];
		if (previewCount == totalFrames)
			return Enumerable.Range (0, totalFrames).ToArray ();

		return Enumerable.Range (0, previewCount)
			.Select (index => (int) Math.Round (index * (totalFrames - 1d) / (previewCount - 1d)))
			.Distinct ()
			.ToArray ();
	}

	private static double ParseDuration (JsonElement stream, JsonElement root)
	{
		if (TryParseDouble (stream, "duration", out double streamDuration))
			return streamDuration;
		if (root.TryGetProperty ("format", out JsonElement format)
			&& TryParseDouble (format, "duration", out double formatDuration))
			return formatDuration;
		return 0;
	}

	private static int ParseFrameCount (JsonElement stream, double duration, double frameRate)
	{
		if (TryParseDouble (stream, "nb_frames", out double frames) && frames > 0)
			return (int) Math.Round (frames);
		return Math.Max (1, (int) Math.Round (duration * frameRate));
	}

	private static double ParseRate (JsonElement stream)
	{
		string? value = stream.ValueKind == JsonValueKind.String ? stream.GetString () : null;
		if (string.IsNullOrWhiteSpace (value))
			return 0;
		string[] parts = value.Split ('/');
		if (parts.Length == 2
			&& double.TryParse (parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator)
			&& double.TryParse (parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
			&& denominator != 0)
			return numerator / denominator;
		return double.TryParse (value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) ? rate : 0;
	}

	private static bool TryParseDouble (JsonElement parent, string name, out double value)
	{
		value = 0;
		if (!parent.TryGetProperty (name, out JsonElement element))
			return false;
		if (element.ValueKind == JsonValueKind.Number)
			return element.TryGetDouble (out value);
		return element.ValueKind == JsonValueKind.String
			&& double.TryParse (element.GetString (), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}

	private static void ValidateOutput (string outputDirectory, string prefix, int digits)
	{
		if (string.IsNullOrWhiteSpace (outputDirectory))
			throw new VideoFrameExportException (Translations.GetString ("Choose an output folder before exporting."));
		if (string.IsNullOrWhiteSpace (prefix)
			|| prefix != Path.GetFileName (prefix)
			|| prefix.IndexOfAny (Path.GetInvalidFileNameChars ()) >= 0)
			throw new VideoFrameExportException (Translations.GetString ("The filename prefix is not valid."));
		if (digits is < 1 or > 8)
			throw new VideoFrameExportException (Translations.GetString ("Numbering must use between 1 and 8 digits."));
	}

	private sealed record ProcessResult (string Output, string Error);
}
