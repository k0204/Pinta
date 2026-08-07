using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private CancellationTokenSource? load_cts;
	private bool is_playing;
	private bool updating_seek;

	private async void HandleOpenVideoClicked (object sender, EventArgs args)
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

		Gio.File? file = await dialog.OpenFileAsync (window);
		string? filename = file?.GetPath ();
		if (!string.IsNullOrWhiteSpace (filename))
			await LoadVideoAsync (filename);
	}

	private async Task LoadVideoAsync (string filename)
	{
		load_cts?.Cancel ();
		load_cts?.Dispose ();
		load_cts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		CancellationToken cancellationToken = load_cts.Token;
		ResetLoadedVideo ();
		playerStatus.SetText (Translations.GetString ("Loading video..."));
		playerStatus.Show ();

		try {
			VideoMetadata loadedMetadata = await VideoFrameExportProcess.ProbeAsync (filename, cancellationToken);
			DirectoryInfo directory = Directory.CreateTempSubdirectory ("pinta-video-frames-");
			IReadOnlyList<string> paths = await VideoFrameExportProcess.ExtractPreviewFilesAsync (
				filename,
				loadedMetadata,
				directory.FullName,
				cancellationToken);
			int[] sourceIndices = VideoFrameExportProcess.GetPreviewIndices (loadedMetadata.TotalFrames, paths.Count);
			if (paths.Count == 0 || sourceIndices.Length == 0)
				throw new VideoFrameExportException (Translations.GetString ("No video frames were found."));

			videoFilename = filename;
			metadata = loadedMetadata;
			previewDirectory = directory;
			LoadMedia (filename);
			LoadPreviews (paths, sourceIndices, loadedMetadata.FrameRate);
			selectedIndices.UnionWith (previews.Take (3).Select (preview => preview.SourceIndex));
			currentFrameIndex = previews[0].SourceIndex;
			playerStatus.Hide ();
			player.Show ();
			playButton.Sensitive = true;
			seekScale.Sensitive = true;
			seekScale.SetRange (0, Math.Max (1, loadedMetadata.TotalFrames - 1));
			UpdateMetadataLabels ();
			UpdateCurrentFrame (seekMedia: true);
			RebuildFilmstrip ();
		} catch (OperationCanceledException) {
		} catch (VideoFrameExportException ex) {
			SetErrorState (ex.Message);
		} catch (Exception ex) {
			SetErrorState (Translations.GetString ("Could not open this video."));
			Console.Error.WriteLine (ex);
		}
	}

	private void LoadMedia (string filename)
	{
		mediaFile?.Dispose ();
		mediaFile = Gtk.MediaFile.NewForFilename (filename);
		player.SetMediaStream (mediaFile);
		is_playing = false;
		SetPlayIcon (playing: false);
	}

	private void LoadPreviews (IReadOnlyList<string> paths, IReadOnlyList<int> sourceIndices, double frameRate)
	{
		ClearPreviews (deleteDirectory: false);
		for (int i = 0; i < Math.Min (paths.Count, sourceIndices.Count); i++) {
			using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromFile (paths[i])!;
			Gdk.Texture texture = Gdk.Texture.NewForPixbuf (pixbuf);
			previews.Add (new VideoFramePreview (sourceIndices[i], TimeSpan.FromSeconds (sourceIndices[i] / frameRate), texture));
		}
	}

	private void ResetLoadedVideo ()
	{
		is_playing = false;
		SetPlayIcon (playing: false);
		mediaFile?.Dispose ();
		mediaFile = null;
		videoFilename = null;
		metadata = null;
		selectedIndices.Clear ();
		currentFrameIndex = 0;
		ClearPreviews (deleteDirectory: true);
		fileLabel.SetText (Translations.GetString ("No video open"));
		metadataLabel.SetText (string.Empty);
		sourceFileLabel.SetText (Translations.GetString ("No video open"));
		sourceResolutionLabel.SetText (Translations.GetString ("Not available"));
		sourceRateLabel.SetText (Translations.GetString ("Not available"));
		sourceDurationLabel.SetText (Translations.GetString ("Not available"));
		sourceFramesLabel.SetText (Translations.GetString ("Not available"));
		rangeStartSpinner.SetValue (1);
		rangeEndSpinner.SetValue (1);
		rangeStartSpinner.Sensitive = false;
		rangeEndSpinner.Sensitive = false;
		rangeButton.Sensitive = false;
		ResetView ();
	}

	private void ClearPreviews (bool deleteDirectory)
	{
		while (filmstrip.GetFirstChild () is Gtk.Widget child)
			filmstrip.Remove (child);
		foreach (VideoFramePreview preview in previews)
			preview.Dispose ();
		previews.Clear ();
		if (deleteDirectory) {
			previewDirectory?.Delete (recursive: true);
			previewDirectory = null;
		}
	}

	private void SetErrorState (string message)
	{
		playerStatus.RemoveCssClass (AdwaitaStyles.Error);
		playerStatus.SetText (message);
		playerStatus.AddCssClass (AdwaitaStyles.Error);
		playerStatus.Show ();
		player.Hide ();
		playButton.Sensitive = false;
		seekScale.Sensitive = false;
	}

	private void HandlePlayClicked (object sender, EventArgs args)
	{
		if (mediaFile is null)
			return;
		if (is_playing) {
			mediaFile.Pause ();
			is_playing = false;
		} else {
			mediaFile.Play ();
			is_playing = true;
		}
		SetPlayIcon (is_playing);
	}

	private void HandlePreviousFrameClicked (object sender, EventArgs args)
	{
		SetFrameIndex (FindAdjacentFrame (-1));
	}

	private void HandleNextFrameClicked (object sender, EventArgs args)
	{
		SetFrameIndex (FindAdjacentFrame (1));
	}

	private int FindAdjacentFrame (int direction)
	{
		if (previews.Count == 0)
			return 0;
		int position = previews.FindIndex (preview => preview.SourceIndex == currentFrameIndex);
		return previews[Math.Clamp (position + direction, 0, previews.Count - 1)].SourceIndex;
	}

	private void HandleSeekChanged (object sender, EventArgs args)
	{
		if (metadata is null || updating_seek)
			return;
		SetFrameIndex ((int) seekScale.GetValue ());
	}

	private void SetFrameIndex (int sourceIndex)
	{
		if (metadata is null)
			return;
		currentFrameIndex = Math.Clamp (sourceIndex, 0, Math.Max (0, metadata.TotalFrames - 1));
		UpdateCurrentFrame (seekMedia: true);
		RebuildFilmstrip (); 
	}

	private void UpdateCurrentFrame (bool seekMedia)
	{
		if (metadata is not VideoMetadata data)
			return;
		if (seekMedia && mediaFile is not null)
			mediaFile.Seek ((long) (currentFrameIndex / data.FrameRate * 1_000_000));
		updating_seek = true;
		seekScale.SetValue (currentFrameIndex);
		updating_seek = false;
		timeLabel.SetText (Translations.GetString ("{0} / {1}", FormatTime (currentFrameIndex / data.FrameRate), FormatTime (data.Duration)));
	}

	private void SetPlayIcon (bool playing)
	{
		playButton.SetChild (Gtk.Image.NewFromIconName (playing ? "media-playback-pause-symbolic" : "media-playback-start-symbolic"));
		playButton.SetTooltipText (Translations.GetString (playing ? "Pause" : "Play"));
	}

	private void HandleSpeedClicked (object sender, EventArgs args)
	{
		double[] speeds = [0.5, 1, 1.5, 2];
		speedIndex = (speedIndex + 1) % speeds.Length;
		speedButton.SetLabel (Translations.GetString ("{0:0.#}x", speeds[speedIndex]));
	}

	private void HandleMuteToggled (object sender, EventArgs args)
	{
		if (mediaFile is not null)
			mediaFile.Muted = muteButton.Active;
	}
}
