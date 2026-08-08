using System;
using System.IO;
using Pinta.Core;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private Gtk.Entry ffmpeg_directory_entry = null!;
	private Gtk.Label ffmpeg_status_label = null!;
	private Gtk.Button ffmpeg_directory_button = null!;
	private Gtk.Button open_video_button = null!;
	private string? pending_video_filename;
	private bool ffmpeg_ready;
	private bool choosing_ffmpeg;
	private bool prompted_for_ffmpeg;

	private Gtk.Box CreateFfmpegControls ()
	{
		Gtk.Box box = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		ffmpeg_directory_entry = Gtk.Entry.New ();
		ffmpeg_directory_entry.WidthRequest = 260;
		ffmpeg_directory_entry.Editable = false;
		ffmpeg_directory_entry.SetPlaceholderText (Translations.GetString ("FFmpeg directory not configured"));
		row.Append (ffmpeg_directory_entry);

		ffmpeg_directory_button = Gtk.Button.NewWithLabel (Translations.GetString ("Select FFmpeg Directory..."));
		ffmpeg_directory_button.OnClicked += HandleChooseFfmpegDirectoryClicked;
		row.Append (ffmpeg_directory_button);
		box.Append (row);

		ffmpeg_status_label = Gtk.Label.New (string.Empty);
		ffmpeg_status_label.Halign = Gtk.Align.Start;
		ffmpeg_status_label.AddCssClass (AdwaitaStyles.DimLabel);
		box.Append (ffmpeg_status_label);
		return box;
	}

	private void InitializeFfmpeg ()
	{
		string? directory = VideoFrameExportProcess.FindToolsDirectory ();
		ffmpeg_ready = directory is not null;
		UpdateFfmpegState (directory, ffmpeg_ready
			? Translations.GetString ("FFmpeg is ready.")
			: Translations.GetString ("Select the folder containing FFmpeg before editing video."));
	}

	private void PromptForFfmpegIfNeeded ()
	{
		if (ffmpeg_ready || prompted_for_ffmpeg || choosing_ffmpeg)
			return;
		prompted_for_ffmpeg = true;
		HandleChooseFfmpegDirectoryClicked (this, EventArgs.Empty);
	}

	private async void HandleChooseFfmpegDirectoryClicked (object sender, EventArgs args)
	{
		if (choosing_ffmpeg)
			return;
		choosing_ffmpeg = true;
		try {
			using Gtk.FileDialog dialog = Gtk.FileDialog.New ();
			dialog.SetTitle (Translations.GetString ("Select FFmpeg Directory"));
			Gio.File? folder = await dialog.SelectFolderAsync (window);
			string? path = folder?.GetPath ();
			if (path is null)
				return;

			if (!VideoFrameExportProcess.TryResolveToolsDirectory (path, out string? directory, out string error)) {
				UpdateFfmpegState (ffmpeg_ready ? ffmpeg_directory_entry.GetText () : path, error);
				return;
			}

			VideoFrameExportProcess.SaveToolsDirectory (directory!);
			ffmpeg_ready = true;
			UpdateFfmpegState (directory, Translations.GetString ("FFmpeg directory verified and saved."));
			await ContinueAfterFfmpegConfigured ();
		} finally {
			choosing_ffmpeg = false;
		}
	}

	private void UpdateFfmpegState (string? directory, string status)
	{
		ffmpeg_directory_entry.SetText (directory ?? string.Empty);
		ffmpeg_status_label.SetText (status);
		open_video_button.Sensitive = ffmpeg_ready;
		ffmpeg_directory_button.SetLabel (ffmpeg_ready
			? Translations.GetString ("Update FFmpeg Directory...")
			: Translations.GetString ("Select FFmpeg Directory..."));
		UpdateExportState ();
	}

	private async System.Threading.Tasks.Task ContinueAfterFfmpegConfigured ()
	{
		if (pending_video_filename is string filename && File.Exists (filename)) {
			pending_video_filename = null;
			await LoadVideoAsync (filename);
		}
	}
}
