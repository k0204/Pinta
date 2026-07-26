namespace Pinta.Core.AI;

public static class AiRequestSettings
{
	public const string AgnesService = "agnes";
	public const string GptImageService = "gpt-image";
	public const string ZzswitchProvider = "zzswitch";
	public const string LukyfaceProvider = "lukyface";

	private const string image_service_key = "ai-image-service";
	private const string gpt_provider_key = "ai-gpt-image-provider";

	public static string GetImageService (ISettingsService settings)
	{
		string value = settings.GetSetting (image_service_key, GptImageService);
		return value is AgnesService or GptImageService ? value : GptImageService;
	}

	public static string GetGptProvider (ISettingsService settings)
	{
		string value = settings.GetSetting (gpt_provider_key, LukyfaceProvider);
		return value is ZzswitchProvider or LukyfaceProvider ? value : LukyfaceProvider;
	}

	public static void Save (ISettingsService settings, string imageService, string gptProvider)
	{
		settings.PutSetting (image_service_key, imageService);
		settings.PutSetting (gpt_provider_key, gptProvider);
	}
}
