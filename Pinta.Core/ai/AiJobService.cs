using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

public sealed class AiJobService
{
	private const string chat_path = "api/chat";
	private const string image_path = "api/images";
	private const string baidu_image_path = "api/baidu-images";
	private const string image_jobs_path = "api/images/jobs";
	private const string video_jobs_path = "api/videos/jobs";

	private readonly AiApiClient api;

	public AiJobService (AiAuthService auth)
	{
		api = new (auth);
	}

	public Task<JsonDocument> RunChatAsync (
		object request,
		Action<string, string>? capture = null,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
		=> RunAsync (
			ct => api.PostJsonAsync (chat_path, request, ct),
			image_jobs_path,
			capture,
			log,
			cancellationToken);

	public Task<JsonDocument> RunImageAsync (
		IEnumerable<KeyValuePair<string, string>> fields,
		IEnumerable<(byte[] Data, string FormName, string FileName)> files,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
		=> RunAsync (
			ct => api.PostMultipartAsync (image_path, fields, files, ct),
			image_jobs_path,
			capture: null,
			log,
			cancellationToken);

	public Task<JsonDocument> RunVideoFromImageAsync (
		IEnumerable<(byte[] Data, string FileName)> references,
		string prompt,
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
	{
		List<KeyValuePair<string, string>> fields = [new ("prompt", prompt)];
		IEnumerable<(byte[] Data, string FormName, string FileName)> files = references.Select (
			reference => (reference.Data, "reference_image", reference.FileName));
		return RunAsync (
			ct => api.PostMultipartAsync (
				"api/videos/image",
				fields,
				files,
				ct),
			video_jobs_path,
			capture: null,
			log,
			cancellationToken);
	}

	public Task<JsonDocument> RunBaiduCutoutAsync (
		byte[] png,
		RectangleI? controlBox = null,
		string returnForm = "rgba",
		Action<string>? log = null,
		CancellationToken cancellationToken = default)
	{
		List<KeyValuePair<string, string>> fields = [
			new ("method", controlBox is null ? "auto" : "control"),
			new ("refine_mask", "true"),
			new ("return_form", returnForm),
		];
		if (controlBox is RectangleI box) {
			int[][][] position = [
				[
					[box.X, box.Y],
					[box.X + box.Width, box.Y + box.Height],
				]
			];
			string positionJson = JsonSerializer.Serialize (position);
			fields.Add (new ("position", positionJson));
			Log (log, $"Baidu cutout request: method=control, position={positionJson}, source_bytes={png.Length}");
		} else {
			Log (log, $"Baidu cutout request: method=auto, source_bytes={png.Length}");
		}

		return RunAsync (
			ct => api.PostMultipartAsync (
				baidu_image_path,
				fields,
				[(png, "file", "pinta.png")],
				ct),
			image_jobs_path,
			capture: null,
			log,
			cancellationToken);
	}

	public Task<byte[]> DownloadAsync (string path, CancellationToken cancellationToken = default)
		=> api.GetBytesAsync (path, cancellationToken);

	private async Task<JsonDocument> RunAsync (
		Func<CancellationToken, Task<string>> submit,
		string jobsPath,
		Action<string, string>? capture,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		string acceptedJson = await submit (cancellationToken);
		capture?.Invoke ("accepted", acceptedJson);
		using JsonDocument accepted = JsonDocument.Parse (acceptedJson);
		string jobId = ReadJobId (accepted.RootElement);
		Log (log, $"AI job accepted: job_id={jobId}");
		return await WaitForResultAsync (jobId, jobsPath, capture, log, cancellationToken);
	}

	private async Task<JsonDocument> WaitForResultAsync (
		string jobId,
		string jobsPath,
		Action<string, string>? capture,
		Action<string>? log,
		CancellationToken cancellationToken)
	{
		string jobPath = $"{jobsPath}/{jobId}";
		while (true) {
			string statusJson = await api.GetStringAsync (jobPath, cancellationToken);
			capture?.Invoke ("status", statusJson);
			using JsonDocument job = JsonDocument.Parse (statusJson);
			string status = job.RootElement.GetProperty ("status").GetString () ?? "";
			Log (log, $"AI job status: job_id={jobId}, status={status}");
			switch (status) {
				case "completed":
					string resultJson = await api.GetStringAsync ($"{jobPath}/result", cancellationToken);
					capture?.Invoke ("result", resultJson);
					return JsonDocument.Parse (resultJson);
				case "failed":
					throw new InvalidOperationException ($"AI job failed: {ReadError (job.RootElement)}");
				case "queued":
				case "processing":
					await Task.Delay (TimeSpan.FromSeconds (1), cancellationToken);
					break;
				default:
					throw new InvalidOperationException ($"AI job returned unknown status: {status}");
			}
		}
	}

	private static string ReadJobId (JsonElement root)
	{
		if (root.TryGetProperty ("id", out JsonElement value) &&
			value.GetString () is string jobId && !string.IsNullOrWhiteSpace (jobId))
			return jobId;
		throw new InvalidOperationException ("AI response did not include a job id.");
	}

	private static string ReadError (JsonElement root)
		=> root.TryGetProperty ("error_message", out JsonElement value)
			? value.GetString () ?? "Unknown server error."
			: "Unknown server error.";

	private static void Log (Action<string>? log, string message)
	{
		Console.WriteLine (message);
		log?.Invoke (message);
	}
}
