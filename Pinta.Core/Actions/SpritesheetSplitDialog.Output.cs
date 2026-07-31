using System;
using System.Linq;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog
{
	public UserLayer? OutputAttempt
		=> output_attempt.Active > 0 ? output_attempts[output_attempt.Active - 1] : null;

	private Gtk.ComboBoxText CreateOutputAttemptCombo ()
	{
		Gtk.ComboBoxText combo = Gtk.ComboBoxText.New ();
		combo.AppendText (source.Parent?.Children.Any (child => child is GroupLayer) == true
			? Translations.GetString ("Create a new attempt")
			: Translations.GetString ("Current attempt"));
		foreach (UserLayer attempt in output_attempts)
			combo.AppendText (Translations.GetString ("Add directions to {0}", attempt.Name));
		combo.Active = 0;
		return combo;
	}

	private bool OutputCanvasMatchesTarget ()
	{
		if (OutputAttempt is not UserLayer target)
			return true;

		UserLayer? frame = target.GetSelfAndDescendants ()
			.FirstOrDefault (child => child is not GroupLayer && child.Name.StartsWith ("frame-", StringComparison.Ordinal));
		return frame is null
			|| (frame.Surface.Width == (int) canvas_width.Value && frame.Surface.Height == (int) canvas_height.Value);
	}
}
