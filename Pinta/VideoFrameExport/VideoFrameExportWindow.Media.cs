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
		if (!ffmpeg_ready) {
			pending_video_filename = filename;
			PromptForFfmpegIfNeeded ();
			return;
		}
		load_cts?.Cancel ();
		load_cts?.Dispose ();
		load_cts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		CancellationToken cancellationToken = load_cts.Token;
		ResetLoadedVideo ();
		playerStatus.SetText (Translations.GetString ("Loading video..."));
		playerStatus.Show ();

		try {
			VideoMetadata loadedMetadata = await VideoFrameExportProcess.ProbeAsync (filename, cancellationToken);
			DirectoryInfo directory = GetFrameDirectory (filename);
			ClearFrameFiles (directory);
			IProgress<int> progress = new Progress<int> (count => UpdateAnalysisProgress (count, loadedMetadata.TotalFrames));
			IReadOnlyList<string> paths = await VideoFrameExportProcess.ExtractFrameFilesAsync (
				filename,
				directory.FullName,
				progress,
				cancellationToken);
			int[] sourceIndices = Enumerable.Range (0, paths.Count).ToArray ();
			if (paths.Count == 0 || sourceIndices.Length == 0)
				throw new VideoFrameExportException (Translations.GetString ("No video frames were found."));

			videoFilename = filename;
			if (videoLayer is not null)
				videoLayer.VideoPath = filename;
			NotifyVideoLoaded ();
			metadata = loadedMetadata;
			previewDirectory = directory;
			LoadPreviews (paths, sourceIndices, loadedMetadata.FrameRate);
			RestoreSelection (loadedMetadata.TotalFrames);
			currentFrameIndex = previews[0].SourceIndex;
			playerStatus.Hide ();
			player.Show ();
			playButton.Sensitive = true;
			speedButton.Sensitive = true;
			seekScale.Sensitive = true;
			seekScale.SetRange (0, Math.Max (1, loadedMetadata.TotalFrames - 1));
			UpdateMetadataLabels ();
			UpdateCurrentFrame ();
			RebuildFilmstrip ();
			exportProgress.Hide ();
		} catch (OperationCanceledException) {
		} catch (VideoFrameExportException ex) {
			SetErrorState (ex.Message);
			Console.Error.WriteLine ($"Video loading failed: {ex.Message}{Environment.NewLine}{ex.Details}");
		} catch (Exception ex) {
			SetErrorState (Translations.GetString ("Could not open this video."));
			Console.Error.WriteLine (ex);
		}
	}

	private void RestoreSelection (int totalFrames)
	{
		selectedIndices.Clear ();
		string? saved = videoLayer?.SelectedFrames;
		if (saved is "*") {
			selectedIndices.UnionWith (previews.Select (preview => preview.SourceIndex));
			return;
		}
		if (saved is null) {
			selectedIndices.UnionWith (previews.Select (preview => preview.SourceIndex));
			return;
		}
		foreach (string value in saved.Split (',', StringSplitOptions.RemoveEmptyEntries))
			if (int.TryParse (value, out int index) && index >= 0 && index < totalFrames)
				selectedIndices.Add (index);
	}

	private void UpdateAnalysisProgress (int current, int total)
	{
		exportProgress.Fraction = total > 0 ? Math.Clamp ((double) current / total, 0, 1) : 0;
		exportProgress.Text = Translations.GetString ("Analyzing frames: {0} / {1}", current, total);
		exportProgress.Show ();
	}

	private DirectoryInfo GetFrameDirectory (string filename)
	{
		string? projectPath = (PintaCore.Workspace.ActiveDocumentOrDefault as Document)?.File?.GetPath ();
		string root = projectPath ?? Path.GetDirectoryName (filename)!;
		return Directory.CreateDirectory (Path.Combine (root, "resources", "video-frames"));
	}

	private static void ClearFrameFiles (DirectoryInfo directory)
	{
		foreach (FileInfo file in directory.EnumerateFiles ("*.png"))
			file.Delete ();
	}

	private void ClearGeneratedFrames ()
	{
		StopPlayback ();
		ClearPreviews (deleteDirectory: true);
		selectedIndices.Clear ();
		SaveSelection ();
		playerStatus.SetText (Translations.GetString ("Generated frames cleared."));
		playerStatus.Show ();
		player.Hide ();
		UpdateSelectionSummary (0);
	}

	private async void RegenerateVideoFrames ()
	{
		if (videoFilename is string filename)
			await LoadVideoAsync (filename);
	}

	private void LoadPreviews (IReadOnlyList<string> paths, IReadOnlyList<int> sourceIndices, double frameRate)
	{
		ClearPreviews (deleteDirectory: false);
		for (int i = 0; i < Math.Min (paths.Count, sourceIndices.Count); i++) {
			previews.Add (new VideoFramePreview (
				sourceIndices[i],
				TimeSpan.FromSeconds (sourceIndices[i] / frameRate),
				paths[i]));
		}
	}

	private void ResetLoadedVideo ()
	{
		StopPlayback ();
		player.SetPaintable (null);
		sourceVideo.SetPaintable (null);
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
		current_frame_load_version++;
		current_frame_cts?.Cancel ();
		current_frame_cts?.Dispose ();
		current_frame_cts = null;
		ClearThumbnailBindings ();
		thumbnail_model.Splice (0, thumbnail_model.NItems, Array.Empty<string> ());
		visible_previews.Clear ();
		player.SetPaintable (null);
		sourceVideo.SetPaintable (null);
		previews.Clear ();
		thumbnail_cache.Clear ();
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
		speedButton.Sensitive = false;
		seekScale.Sensitive = false;
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
		UpdateCurrentFrame ();
		RebuildFilmstrip (); 
	}

	private void UpdateCurrentFrame ()
	{
		if (metadata is not VideoMetadata data)
			return;
		VideoFramePreview? preview = previews.MinBy (item => Math.Abs (item.SourceIndex - currentFrameIndex));
		updating_seek = true;
		seekScale.SetValue (currentFrameIndex);
		updating_seek = false;
		timeLabel.SetText (Translations.GetString ("{0} / {1}", FormatTime (currentFrameIndex / data.FrameRate), FormatTime (data.Duration)));
		if (preview is not null)
			StartCurrentFrameLoad (preview);
	}

}
