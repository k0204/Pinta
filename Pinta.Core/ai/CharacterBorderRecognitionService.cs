using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

/// <summary>
/// C# client for the local character border recognition service.
/// </summary>
public sealed class CharacterBorderRecognitionService
{
	private static readonly Uri base_uri = new ("http://127.0.0.1:8001/");
	private static readonly HttpClient client = new () { Timeout = TimeSpan.FromMinutes (6) };

	public async Task<CharacterBorderRecognitionResult> RecognizeAsync (
		byte[] sourcePng,
		RectangleI box,
		CancellationToken cancellationToken = default)
	{
		string jobId = await CreateJobAsync (sourcePng, cancellationToken);
		(string imageUrl, string maskUrl) = await CreatePartAsync (jobId, box, cancellationToken);
		Task<byte[]> partTask = DownloadAsync (imageUrl, cancellationToken);
		Task<byte[]> maskTask = DownloadAsync (maskUrl, cancellationToken);
		await Task.WhenAll (partTask, maskTask);
		return new (await partTask, await maskTask);
	}

	private static async Task<string> CreateJobAsync (byte[] sourcePng, CancellationToken cancellationToken)
	{
		using MultipartFormDataContent content = new ();
		using ByteArrayContent image = new (sourcePng);
		image.Headers.ContentType = new ("image/png");
		content.Add (image, "file", "pinta.png");

		using HttpResponseMessage response = await client.PostAsync (
			new Uri (base_uri, "api/jobs"),
			content,
			cancellationToken);
		using JsonDocument json = JsonDocument.Parse (await ReadApiResponseAsync (response, cancellationToken));
		return json.RootElement.GetProperty ("job_id").GetString ()
			?? throw new InvalidOperationException ("Missing job_id");
	}

	private static async Task<(string ImageUrl, string MaskUrl)> CreatePartAsync (
		string jobId,
		RectangleI box,
		CancellationToken cancellationToken)
	{
		var payload = new Dictionary<string, object> {
			["name"] = "Detected Border",
			["segment_prompt"] = "object",
			["box"] = new[] { box.X, box.Y, box.X + box.Width, box.Y + box.Height },
			["part_type"] = "other",
		};

		using StringContent content = new (
			JsonSerializer.Serialize (payload),
			Encoding.UTF8,
			"application/json");
		using HttpResponseMessage response = await client.PostAsync (
			new Uri (base_uri, $"api/jobs/{jobId}/parts"),
			content,
			cancellationToken);
		using JsonDocument json = JsonDocument.Parse (await ReadApiResponseAsync (response, cancellationToken));
		JsonElement part = json.RootElement.GetProperty ("part");
		string imageUrl = part.GetProperty ("image_url").GetString ()
			?? throw new InvalidOperationException ("Missing image_url");
		string maskUrl = part.GetProperty ("mask_url").GetString ()
			?? throw new InvalidOperationException ("Missing mask_url");
		return (imageUrl, maskUrl);
	}

	private static Task<byte[]> DownloadAsync (string path, CancellationToken cancellationToken)
		=> client.GetByteArrayAsync (new Uri (base_uri, path.TrimStart ('/')), cancellationToken);

	private static async Task<string> ReadApiResponseAsync (
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		string body = await response.Content.ReadAsStringAsync (cancellationToken);
		if (response.IsSuccessStatusCode)
			return body;

		string detail = body;
		try {
			using JsonDocument json = JsonDocument.Parse (body);
			if (json.RootElement.TryGetProperty ("detail", out JsonElement detailElement))
				detail = detailElement.ToString ();
		} catch (JsonException) {
		}

		throw new InvalidOperationException ($"{(int) response.StatusCode} {response.ReasonPhrase}: {detail}");
	}
}
