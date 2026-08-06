using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

public sealed record AiProviderInfo (
	string Id,
	string Name,
	[property: JsonPropertyName ("supports_chat")] bool SupportsChat,
	[property: JsonPropertyName ("supports_image")] bool SupportsImage,
	[property: JsonPropertyName ("image_type")] string? ImageType = null,
	[property: JsonPropertyName ("channel")] string? Channel = null,
	[property: JsonPropertyName ("image_sizes")] IReadOnlyList<string>? ImageSizes = null,
	[property: JsonPropertyName ("image_resolutions")] IReadOnlyList<string>? ImageResolutions = null,
	[property: JsonPropertyName ("image_cost")] int ImageCost = 0);

public sealed class AiProviderCatalog
{
	private const string cache_key = "ai-provider-catalog";
	private static readonly AiProviderInfo[] defaults = [
		new (AiRequestSettings.AgnesService, "Agnes", true, true, AiRequestSettings.AgnesService, AiRequestSettings.AgnesService),
		new (AiRequestSettings.ZzswitchProvider, AiRequestSettings.ZzswitchProvider, true, true, AiRequestSettings.GptImageService, AiRequestSettings.ZzswitchProvider),
		new (AiRequestSettings.LukyfaceProvider, AiRequestSettings.LukyfaceProvider, true, true, AiRequestSettings.GptImageService, AiRequestSettings.LukyfaceProvider),
		new (AiRequestSettings.TokenX24Provider, "TokenX24", false, true, AiRequestSettings.NanoBananaService, AiRequestSettings.TokenX24Provider),
		new (AiRequestSettings.VisionaryProvider, "Visionary", false, true, AiRequestSettings.NanoBananaService, AiRequestSettings.VisionaryProvider),
	];

	private readonly AiAuthService auth;
	private readonly ISettingsService settings;
	private IReadOnlyList<AiProviderInfo> providers;

	public AiProviderCatalog (AiAuthService auth, ISettingsService settings)
	{
		this.auth = auth;
		this.settings = settings;
		providers = AddMissingNanoBananaDefaults (ReadCache (settings) ?? defaults);
	}

	public IReadOnlyList<AiProviderInfo> ChatProviders
		=> Filter (provider => provider.SupportsChat);

	public IReadOnlyList<AiProviderInfo> ImageProviders
		=> Filter (provider => provider.SupportsImage);

	public AiProviderInfo? FindImageProvider (string providerId)
		=> ImageProviders.FirstOrDefault (provider => provider.Id == providerId);

	public async Task RefreshAsync (CancellationToken cancellationToken = default)
	{
		AiApiClient api = new (auth);
		string json = await api.GetStringAsync ("api/providers", cancellationToken);
		AiProviderInfo[] loaded = JsonSerializer.Deserialize<AiProviderInfo[]> (
			json,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
		if (loaded.Length == 0 || loaded.Any (item => string.IsNullOrWhiteSpace (item.Id)))
			throw new InvalidOperationException ("The API server returned an invalid provider catalog.");

		providers = AddMissingNanoBananaDefaults (loaded);
		settings.PutSetting (cache_key, JsonSerializer.Serialize (loaded));
	}

	private IReadOnlyList<AiProviderInfo> Filter (Func<AiProviderInfo, bool> predicate)
		=> [.. providers.Where (predicate)];

	private static IReadOnlyList<AiProviderInfo> AddMissingNanoBananaDefaults (IReadOnlyList<AiProviderInfo> source)
	{
		List<AiProviderInfo> result = [.. source];
		foreach (AiProviderInfo provider in defaults.Where (item => item.ImageType == AiRequestSettings.NanoBananaService))
			if (result.All (item => item.Id != provider.Id))
				result.Add (provider);
		return result;
	}

	private static AiProviderInfo[]? ReadCache (ISettingsService settings)
	{
		try {
			string json = settings.GetSetting (cache_key, string.Empty);
			return string.IsNullOrWhiteSpace (json) ? null : JsonSerializer.Deserialize<AiProviderInfo[]> (json);
		} catch (JsonException) {
			return null;
		}
	}
}
