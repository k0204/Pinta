using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private static IReadOnlyList<AI.AiProviderInfo> GetGptImageProviders ()
		=> [.. PintaCore.AiProviders.ImageProviders.Where (
			provider => provider.Id != AI.AiRequestSettings.AgnesService)];

	private static void PopulateProviderCombo (
		Gtk.ComboBoxText combo,
		IReadOnlyList<AI.AiProviderInfo> providers)
	{
		foreach (AI.AiProviderInfo provider in providers)
			combo.AppendText (provider.Name);
		string selected = AI.AiRequestSettings.GetGptProvider (PintaCore.Settings);
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
}
