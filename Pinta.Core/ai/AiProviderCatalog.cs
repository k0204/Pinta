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
	[property: JsonPropertyName ("supports_image")] bool SupportsImage);

public sealed class AiProviderCatalog
{
	private const string cache_key = "ai-provider-catalog";
	private static readonly AiProviderInfo[] defaults = [
		new (AiRequestSettings.AgnesService, "Agnes", true, true),
		new (AiRequestSettings.ZzswitchProvider, AiRequestSettings.ZzswitchProvider, true, true),
		new (AiRequestSettings.LukyfaceProvider, AiRequestSettings.LukyfaceProvider, true, true),
	];

	private readonly AiAuthService auth;
	private readonly ISettingsService settings;
	private IReadOnlyList<AiProviderInfo> providers;

	public AiProviderCatalog (AiAuthService auth, ISettingsService settings)
	{
		this.auth = auth;
		this.settings = settings;
		providers = ReadCache (settings) ?? defaults;
	}

	public IReadOnlyList<AiProviderInfo> ChatProviders
		=> Filter (provider => provider.SupportsChat);

	public IReadOnlyList<AiProviderInfo> ImageProviders
		=> Filter (provider => provider.SupportsImage);

	public async Task RefreshAsync (CancellationToken cancellationToken = default)
	{
		AiApiClient api = new (auth);
		string json = await api.GetStringAsync ("api/providers", cancellationToken);
		AiProviderInfo[] loaded = JsonSerializer.Deserialize<AiProviderInfo[]> (
			json,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
		if (loaded.Length == 0 || loaded.Any (item => string.IsNullOrWhiteSpace (item.Id)))
			throw new InvalidOperationException ("The API server returned an invalid provider catalog.");

		providers = loaded;
		settings.PutSetting (cache_key, JsonSerializer.Serialize (loaded));
	}

	private IReadOnlyList<AiProviderInfo> Filter (Func<AiProviderInfo, bool> predicate)
		=> [.. providers.Where (predicate)];

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
