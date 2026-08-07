using System;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed class VideoFrameExportAction
{
	private readonly Adw.Application application;
	private VideoFrameExportWindow? window;

	public VideoFrameExportAction (Adw.Application application)
	{
		this.application = application;
		Command = new Command (
			"videoframeexport",
			Translations.GetString ("Video Frame Exporter..."),
			Translations.GetString ("Preview a video and export selected frames as PNG"),
			StandardIcons.DocumentSaveAs);
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
		if (window is not null) {
			window.Present ();
			return;
		}

		window = new VideoFrameExportWindow (application, PintaCore.Chrome.MainWindow);
		window.Closed += HandleWindowClosed;
		window.Present ();
	}

	private void HandleWindowClosed (object? sender, EventArgs args)
	{
		if (sender is VideoFrameExportWindow closedWindow)
			closedWindow.Closed -= HandleWindowClosed;
		window = null;
	}
}
