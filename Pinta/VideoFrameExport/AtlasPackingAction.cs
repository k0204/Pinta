using System;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed class AtlasPackingAction
{
	private AtlasPackingWindow? window;

	public AtlasPackingAction ()
	{
		Command = new Command (
			"atlaspacker",
			Translations.GetString ("Texture Atlas Packer..."),
			Translations.GetString ("Pack image frames into texture atlas pages"),
			StandardIcons.ImageGeneric);
		Command.Activated += HandleActivated;
	}

	public Command Command { get; }

	public void Register (Gtk.Application app, Gio.Menu menu)
	{
		app.AddCommand (Command);
		menu.AppendItem (Command.CreateMenuItem ());
	}

	private void HandleActivated (object sender, EventArgs args)
	{
		if (window is null) {
			window = AtlasPackingWindow.New (PintaCore.Chrome.MainWindow);
			window.Closed += HandleWindowClosed;
		}
		window.Present ();
	}

	private void HandleWindowClosed (object? sender, EventArgs args)
	{
		if (sender is AtlasPackingWindow closedWindow) {
			closedWindow.Closed -= HandleWindowClosed;
			if (ReferenceEquals (window, closedWindow))
				window = null;
		}
	}
}
