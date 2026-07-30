namespace Pinta.Core.AI;

public static class AiRequestSettings
{
	public const string AgnesService = "agnes";
	public const string GptImageService = "gpt-image";
	public const string BaiduService = "baidu";
	public const string ZzswitchProvider = "zzswitch";
	public const string LukyfaceProvider = "lukyface";

	private const string image_service_key = "ai-image-service";
	private const string gpt_provider_key = "ai-gpt-image-provider";
	private const string sprite_segmentation_provider_key = "ai-sprite-segmentation-provider";

	public static string GetImageService (ISettingsService settings)
	{
		string value = settings.GetSetting (image_service_key, GptImageService);
		return value is AgnesService or GptImageService or BaiduService ? value : GptImageService;
	}

	public static string GetGptProvider (ISettingsService settings)
	{
		string value = settings.GetSetting (gpt_provider_key, LukyfaceProvider);
		return string.IsNullOrWhiteSpace (value) ? LukyfaceProvider : value;
	}

	public static string GetSpriteSegmentationProvider (ISettingsService settings)
	{
		string value = settings.GetSetting (sprite_segmentation_provider_key, AgnesService);
		return string.IsNullOrWhiteSpace (value) ? AgnesService : value;
	}

	public static void SaveSpriteSegmentationProvider (ISettingsService settings, string provider)
	{
		settings.PutSetting (sprite_segmentation_provider_key, provider);
	}

	public static void Save (
		ISettingsService settings,
		string imageService,
		string gptProvider)
	{
		settings.PutSetting (image_service_key, imageService);
		settings.PutSetting (gpt_provider_key, gptProvider);
	}
}
