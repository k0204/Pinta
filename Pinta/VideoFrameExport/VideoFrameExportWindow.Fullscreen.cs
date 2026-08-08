using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private bool fullscreened;

	private void InitializeFullscreen ()
	{
		Gtk.EventControllerKey keys = Gtk.EventControllerKey.New ();
		keys.PropagationPhase = Gtk.PropagationPhase.Capture;
		keys.OnKeyPressed += (_, args) => {
			if (!fullscreened || args.GetKey ().Value != Gdk.Constants.KEY_Escape)
				return false;

			fullscreened = false;
			window.Unfullscreen ();
			return true;
		};
		window.AddController (keys);
	}

	private Gtk.Button CreateFullscreenButton ()
	{
		Gtk.Button button = Gtk.Button.NewFromIconName (StandardIcons.WindowMaximize);
		button.SetTooltipText (Translations.GetString ("Maximize dialog"));
		button.OnClicked += (_, _) => {
			fullscreened = !fullscreened;
			if (fullscreened)
				window.Fullscreen ();
			else
				window.Unfullscreen ();
		};
		return button;
	}
}
