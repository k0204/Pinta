using System;
using Pinta.Resources;

namespace Pinta.Core;

[GObject.Subclass<Gtk.Dialog>]
public partial class PintaDialog
{
	private Gtk.HeaderBar header_bar = null!;
	private Gtk.Button fullscreen_button = null!;
	private bool is_fullscreen;

	protected Gtk.HeaderBar HeaderBar => header_bar;

	partial void Initialize ()
	{
		Modal = true;

		Gtk.Button fullscreenButton = Gtk.Button.NewFromIconName (Resources.StandardIcons.WindowMaximize);
		fullscreenButton.FocusOnClick = false;
		fullscreenButton.TooltipText = Translations.GetString ("Fullscreen");
		fullscreenButton.AddCssClass (AdwaitaStyles.Flat);
		fullscreenButton.OnClicked += HandleFullscreenClicked;

		Gtk.Button closeButton = Gtk.Button.NewFromIconName (Resources.StandardIcons.WindowClose);
		closeButton.FocusOnClick = false;
		closeButton.TooltipText = Translations.GetString ("Close");
		closeButton.AddCssClass (AdwaitaStyles.Flat);
		closeButton.OnClicked += HandleCloseClicked;

		Gtk.HeaderBar headerBar = Gtk.HeaderBar.New ();
		headerBar.PackEnd (fullscreenButton);
		headerBar.PackEnd (closeButton);
		headerBar.SetShowTitleButtons (false);
		SetTitlebar (headerBar);

		Gtk.EventControllerKey keys = Gtk.EventControllerKey.New ();
		keys.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		keys.OnKeyPressed += HandleKeyPressed;
		AddController (keys);

		header_bar = headerBar;
		fullscreen_button = fullscreenButton;
	}

	private void HandleFullscreenClicked (Gtk.Button sender, EventArgs args)
	{
		is_fullscreen = !is_fullscreen;
		if (is_fullscreen)
			Fullscreen ();
		else
			Unfullscreen ();

		fullscreen_button.SetIconName (
			is_fullscreen
				? Resources.StandardIcons.WindowMinimize
				: Resources.StandardIcons.WindowMaximize);
	}

	private void HandleCloseClicked (Gtk.Button sender, EventArgs args)
	{
		Response ((int) Gtk.ResponseType.Cancel);
		Close ();
	}

	private bool HandleKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (!is_fullscreen || args.GetKey ().Value != Gdk.Constants.KEY_Escape)
			return false;

		is_fullscreen = false;
		Unfullscreen ();
		fullscreen_button.SetIconName (Resources.StandardIcons.WindowMaximize);
		return true;
	}
}
