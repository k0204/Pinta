using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

/// <summary>
/// C# client for the local character border recognition service.
/// </summary>
public sealed class CharacterBorderRecognitionService
{
	private readonly AiApiClient api;

	public CharacterBorderRecognitionService (AiAuthService auth)
	{
		api = new (auth);
	}

	public async Task<CharacterBorderRecognitionResult> RecognizeAsync (
		byte[] sourcePng,
		RectangleI box,
		CancellationToken cancellationToken = default)
	{
		string jobId = await CreateJobAsync (sourcePng, cancellationToken);
		(string imageUrl, string maskUrl) = await CreatePartAsync (jobId, box, cancellationToken);
		Task<byte[]> partTask = api.GetBytesAsync (imageUrl, cancellationToken);
		Task<byte[]> maskTask = api.GetBytesAsync (maskUrl, cancellationToken);
		await Task.WhenAll (partTask, maskTask);
		return new (await partTask, await maskTask);
	}

	private async Task<string> CreateJobAsync (byte[] sourcePng, CancellationToken cancellationToken)
	{
		using JsonDocument json = JsonDocument.Parse (await api.PostPngAsync (
			"api/jobs",
			sourcePng,
			"file",
			"pinta.png",
			cancellationToken));
		return json.RootElement.GetProperty ("job_id").GetString ()
			?? throw new InvalidOperationException ("Missing job_id");
	}

	private async Task<(string ImageUrl, string MaskUrl)> CreatePartAsync (
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

		using JsonDocument json = JsonDocument.Parse (await api.PostJsonAsync ($"api/jobs/{jobId}/parts", payload, cancellationToken));
		JsonElement part = json.RootElement.GetProperty ("part");
		string imageUrl = part.GetProperty ("image_url").GetString ()
			?? throw new InvalidOperationException ("Missing image_url");
		string maskUrl = part.GetProperty ("mask_url").GetString ()
			?? throw new InvalidOperationException ("Missing mask_url");
		return (imageUrl, maskUrl);
	}
}
