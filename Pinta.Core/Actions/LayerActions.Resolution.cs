using System;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async Task<Size?> ConfirmImageResolutionAsync (
		string operation,
		string imageService,
		string provider,
		Size targetSize)
	{
		AI.AiImageResolutionPlan plan = AI.AiImageResolutionPlanner.Create (imageService, provider, targetSize);
		if (!plan.RequiresChoice)
			return plan.UpperSize ?? plan.LowerSize;

		int cost = AI.BackgroundCutoutService.GetImageGenerationCost (provider);
		string costText = cost > 0
			? Translations.GetString ("{0} credits per image", cost)
			: Translations.GetString ("Cost unavailable");
		string targetText = FormatSize (targetSize);
		string lowerText = FormatResolutionChoice (plan.LowerSize!.Value, costText);
		string upperText = FormatResolutionChoice (plan.UpperSize!.Value, costText);

		using Adw.MessageDialog dialog = Adw.MessageDialog.New (
			chrome.MainWindow,
			Translations.GetString ("Choose Image Resolution"),
			$"{Translations.GetString ("The source image is {0}. Choose how to adapt it for {1}.", targetText, operation)}\n\n"
			+ $"{Translations.GetString ("Shrink and preserve the full image:")} {lowerText}\n"
			+ $"{Translations.GetString ("Enlarge and preserve the full image:")} {upperText}");
		const string cancelResponse = "cancel";
		const string lowerResponse = "lower";
		const string upperResponse = "upper";
		dialog.AddResponse (cancelResponse, Translations.GetString ("_Cancel"));
		dialog.AddResponse (lowerResponse, Translations.GetString ("Use Smaller Resolution"));
		dialog.AddResponse (upperResponse, Translations.GetString ("Use Larger Resolution"));
		dialog.SetResponseAppearance (lowerResponse, Adw.ResponseAppearance.Suggested);
		dialog.Modal = true;
		dialog.DefaultResponse = lowerResponse;
		dialog.CloseResponse = cancelResponse;

		string response = await dialog.RunAsync ();
		return response switch {
			lowerResponse => plan.LowerSize,
			upperResponse => plan.UpperSize,
			_ => null,
		};

		string FormatResolutionChoice (Size size, string price)
			=> $"{FormatSize (size)} ({price})";
	}

	private static string FormatSize (Size size)
		=> $"{size.Width} x {size.Height}";
}
