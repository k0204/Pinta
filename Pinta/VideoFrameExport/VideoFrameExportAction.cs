using System;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed class VideoFrameExportAction
{
	private VideoFrameExportWindow? window;

	public event EventHandler? VideoImported;

	public VideoFrameExportAction ()
	{
		Command = new Command (
			"videoframeexport",
			Translations.GetString ("Video Frame Exporter..."),
			Translations.GetString ("Preview a video and export selected frames as PNG"),
			StandardIcons.DocumentSaveAs);
		Command.Activated += HandleActivated;
	}

	public Command Command { get; }

	public async void ImportVideoForLayer (VideoEditingLayer layer)
	{
		using Gtk.FileDialog dialog = Gtk.FileDialog.New ();
		dialog.SetTitle (Translations.GetString ("Open Video"));
		using Gtk.FileFilter filter = Gtk.FileFilter.New ();
		filter.Name = Translations.GetString ("Video files");
		foreach (string pattern in new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v" })
			filter.AddPattern (pattern);
		using Gio.ListStore filters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		filters.Append (filter);
		dialog.SetFilters (filters);

		Gio.File? file = await dialog.OpenFileAsync (PintaCore.Chrome.MainWindow);
		string? filename = file?.GetPath ();
		if (string.IsNullOrWhiteSpace (filename))
			return;

		layer.VideoPath = filename;
		VideoImported?.Invoke (this, EventArgs.Empty);
	}

	public void EditVideoLayer (VideoEditingLayer layer)
	{
		EnsureWindow (layer);
		window!.Present ();
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
		if (window is not null && window.IsForLayer (videoLayer)) {
			window.Present ();
			return;
		}
		window?.Close ();

		window = VideoFrameExportWindow.New (PintaCore.Chrome.MainWindow, videoLayer);
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
			if (ReferenceEquals (window, closedWindow))
				window = null;
		}
	}
}
