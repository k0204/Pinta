using System.Linq;

namespace Pinta.Core;

internal abstract partial class AnimationFrameEditorBase
{
	public UserLayer? OutputAttempt
		=> output_attempt.Active > 0 ? output_attempts[output_attempt.Active - 1] : null;

	private Gtk.ComboBoxText CreateOutputAttemptCombo ()
	{
		Gtk.ComboBoxText combo = Gtk.ComboBoxText.New ();
		combo.AppendText (Translations.GetString ("Current attempt"));
		foreach (UserLayer attempt in output_attempts)
			combo.AppendText (Translations.GetString ("Add directions to {0}", attempt.Name));
		combo.Active = 0;
		return combo;
	}

	private bool OutputCanvasMatchesTarget ()
	{
		if (OutputAttempt is not UserLayer target)
			return true;

		SpriteSheetLayer? spriteSheet = target.Children.OfType<SpriteSheetLayer> ().FirstOrDefault ();
		return spriteSheet is null
			|| (spriteSheet.CanvasWidth == (int) canvas_width.Value && spriteSheet.CanvasHeight == (int) canvas_height.Value);
	}
}
