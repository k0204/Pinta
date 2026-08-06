//
// LayerActions.GenerationType.cs
//

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private static string GetImageGenerationTypeLabel (
		AiImageRequestMode mode,
		bool directionSheet)
		=> mode switch {
			AiImageRequestMode.BackgroundCleanup => Translations.GetString ("White Background Image"),
			AiImageRequestMode.SpritesheetGeneration when directionSheet
				=> Translations.GetString ("Direction Sheet"),
			AiImageRequestMode.SpritesheetGeneration
				=> Translations.GetString ("Action Spritesheet"),
			AiImageRequestMode.SingleDirectionAnimationGeneration
				=> Translations.GetString ("Sequence Frames"),
			AiImageRequestMode.ImageSplitGeneration
				=> Translations.GetString ("Split Image"),
			_ => Translations.GetString ("AI Generated Image"),
		};

}
