using System;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed class VideoFrameExportAction
{
	private readonly Adw.Application application;
	private VideoFrameExportWindow? window;

	public event EventHandler? VideoImported;

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

	public void ImportVideoForLayer (VideoEditingLayer layer)
	{
		EnsureWindow (layer);
		window!.Present ();
		window!.ImportVideo ();
	}

	public void Register (Gtk.Application app, Gio.Menu menu)
	{
		app.AddCommand (Command);
		menu.AppendItem (Command.CreateMenuItem ());
	}

	private void HandleActivated (object sender, EventArgs args)
	{
		VideoEditingLayer? videoLayer = null;
		if (PintaCore.Workspace.ActiveDocumentOrDefault is Document document)
			videoLayer = document.Layers.GetOrCreateVideoEditingLayer ();
		EnsureWindow (videoLayer);
		window!.Present ();
	}

	private void EnsureWindow (VideoEditingLayer? videoLayer)
	{
		if (window is not null) {
			window.Present ();
			return;
		}

		window = new VideoFrameExportWindow (application, PintaCore.Chrome.MainWindow, videoLayer);
		window.Closed += HandleWindowClosed;
		window.VideoLoaded += HandleVideoLoaded;
	}

	private void HandleVideoLoaded (object? sender, EventArgs args)
		=> VideoImported?.Invoke (this, EventArgs.Empty);

	private void HandleWindowClosed (object? sender, EventArgs args)
	{
		if (sender is VideoFrameExportWindow closedWindow) {
			closedWindow.VideoLoaded -= HandleVideoLoaded;
			closedWindow.Closed -= HandleWindowClosed;
		}
		window = null;
	}
}
