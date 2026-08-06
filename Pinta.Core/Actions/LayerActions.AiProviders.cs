using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private static IReadOnlyList<AI.AiProviderInfo> GetGptImageProviders ()
		=> [.. PintaCore.AiProviders.ImageProviders.Where (
			provider => provider.ImageType == AI.AiRequestSettings.GptImageService
				|| (provider.ImageType is null
					&& provider.Id != AI.AiRequestSettings.AgnesService
					&& provider.Id != AI.AiRequestSettings.BaiduService
					&& provider.Id != AI.AiRequestSettings.NanoBananaService
					&& !IsNanoBananaProvider (provider)))];

	private static IReadOnlyList<AI.AiProviderInfo> GetNanoBananaProviders ()
		=> [.. PintaCore.AiProviders.ImageProviders.Where (IsNanoBananaProvider)];

	private static bool IsNanoBananaProvider (AI.AiProviderInfo provider)
		=> (provider.ImageType == AI.AiRequestSettings.NanoBananaService
				&& !string.IsNullOrWhiteSpace (provider.Channel))
			|| provider.Id is AI.AiRequestSettings.TokenX24Provider or AI.AiRequestSettings.VisionaryProvider;

	private static void PopulateProviderCombo (
		Gtk.ComboBoxText combo,
		IReadOnlyList<AI.AiProviderInfo> providers,
		string selected)
	{
		combo.RemoveAll ();
		foreach (AI.AiProviderInfo provider in providers)
			combo.AppendText (IsNanoBananaProvider (provider) ? provider.Channel ?? provider.Name : provider.Name);
		combo.Active = 0;
		for (int index = 0; index < providers.Count; index++)
			if (providers[index].Id == selected)
				combo.Active = index;
	}

	private static string GetSelectedProvider (
		Gtk.ComboBoxText combo,
		IReadOnlyList<AI.AiProviderInfo> providers)
		=> combo.Active >= 0 && combo.Active < providers.Count
			? providers[combo.Active].Id
			: throw new System.InvalidOperationException ("No image provider is available.");

	private static void SaveImageServiceSelection (
		string imageService,
		Gtk.ComboBoxText providerCombobox,
		IReadOnlyList<AI.AiProviderInfo> gptProviders,
		IReadOnlyList<AI.AiProviderInfo> nanoBananaProviders)
	{
		string gptProvider = imageService == AI.AiRequestSettings.GptImageService
			? GetSelectedProvider (providerCombobox, gptProviders)
			: AI.AiRequestSettings.GetGptProvider (PintaCore.Settings);
		AI.AiRequestSettings.Save (PintaCore.Settings, imageService, gptProvider);
		if (imageService == AI.AiRequestSettings.NanoBananaService)
			AI.AiRequestSettings.SaveNanoBananaProvider (
				PintaCore.Settings,
				GetSelectedProvider (providerCombobox, nanoBananaProviders));
	}
}
