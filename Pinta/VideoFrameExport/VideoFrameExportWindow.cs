using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow : IDisposable
{
	private readonly Adw.ApplicationWindow window;
	private readonly Gtk.Picture player;
	private readonly Gtk.Picture sourceVideo;
	private readonly Gtk.Stack playerStack;
	private readonly Gtk.ToggleButton sequenceTab;
	private readonly Gtk.ToggleButton sourceTab;
	private readonly Gtk.Label playerStatus;
	private readonly Gtk.Button playButton;
	private readonly Gtk.Scale seekScale;
	private readonly Gtk.Label timeLabel;
	private Gtk.Label fileLabel = null!;
	private Gtk.Label metadataLabel = null!;
	private readonly Gtk.Label sourceFileLabel;
	private readonly Gtk.Label sourceResolutionLabel;
	private readonly Gtk.Label sourceRateLabel;
	private readonly Gtk.Label sourceDurationLabel;
	private readonly Gtk.Label sourceFramesLabel;
	private readonly Gtk.Label selectionLabel;
	private readonly Gtk.Grid filmstrip;
	private readonly Gtk.Label filmstripSummary;
	private readonly Gtk.Button exportButton;
	private readonly Gtk.Button cancelExportButton;
	private readonly Gtk.ProgressBar exportProgress;
	private Gtk.Label numberingLabel = null!;
	private readonly Gtk.Entry outputFolderEntry;
	private readonly Gtk.Entry prefixEntry;
	private readonly Gtk.SpinButton digitsSpinner;
	private readonly Gtk.SpinButton rangeStartSpinner;
	private readonly Gtk.SpinButton rangeEndSpinner;
	private readonly Gtk.Button rangeButton;
	private readonly Gtk.ToggleButton allFramesButton;
	private readonly Gtk.ToggleButton selectedFramesButton;
	private readonly Gtk.Button speedButton;
	private readonly Gtk.ToggleButton muteButton;
	private readonly List<VideoFramePreview> previews = [];
	private readonly HashSet<int> selectedIndices = [];
	private readonly CancellationTokenSource lifetime = new ();

	private DirectoryInfo? previewDirectory;
	private string? videoFilename;
	private VideoMetadata? metadata;
	private int currentFrameIndex;
	private bool disposed;
	private readonly VideoEditingLayer? videoLayer;

	public event EventHandler? Closed;
	public event EventHandler? VideoLoaded;

	internal void NotifyVideoLoaded ()
		=> VideoLoaded?.Invoke (this, EventArgs.Empty);

	public VideoFrameExportWindow (Adw.Application application, Gtk.Window parent, VideoEditingLayer? videoLayer = null)
	{
		this.videoLayer = videoLayer;
		window = Adw.ApplicationWindow.New (application);
		window.TransientFor = parent;
		window.DefaultWidth = 1440;
		window.DefaultHeight = 900;
		window.Title = Translations.GetString ("Video Frame Exporter");
		window.OnCloseRequest += HandleCloseRequest;
		InitializeFullscreen ();

		Gtk.Box root = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		Gtk.Box header = CreateHeader ();
		root.Append (header);

		Gtk.Box workspace = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		workspace.Hexpand = true;
		workspace.Vexpand = true;

			(player, sourceVideo, playerStack, sequenceTab, sourceTab, playerStatus, playButton, seekScale, timeLabel, speedButton, muteButton) = CreatePlayerPanel ();
		(sourceFileLabel, sourceResolutionLabel, sourceRateLabel, sourceDurationLabel, sourceFramesLabel,
			selectionLabel, allFramesButton, selectedFramesButton, outputFolderEntry, prefixEntry, digitsSpinner,
			rangeStartSpinner, rangeEndSpinner, rangeButton, exportButton, cancelExportButton, exportProgress) = CreateInspectorPanel ();

		Gtk.Box leftContent = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		leftContent.Append (CreatePlayerLayout ());
		leftContent.Append (CreateInspectorLayout ());
		Gtk.ScrolledWindow leftScroll = Gtk.ScrolledWindow.New ();
		leftScroll.Hexpand = true;
		leftScroll.Vexpand = true;
		leftScroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		leftScroll.SetChild (leftContent);
		workspace.Append (leftScroll);

		(filmstrip, filmstripSummary, Gtk.Box filmstripPanel) = CreateFilmstrip ();
		filmstripPanel.WidthRequest = 680;
		filmstripPanel.Hexpand = true;
		filmstripPanel.Vexpand = true;
		workspace.Append (filmstripPanel);
		root.Append (workspace);
		window.SetContent (root);
		InitializeFfmpeg ();
		ResetView ();
		if (videoLayer?.VideoPath is string path && File.Exists (path)) {
			if (ffmpeg_ready)
				_ = LoadVideoAsync (path);
			else
				pending_video_filename = path;
		}
	}

	public void Present ()
	{
		window.Present ();
		PromptForFfmpegIfNeeded ();
	}

	public bool IsForLayer (VideoEditingLayer? layer)
		=> ReferenceEquals (videoLayer, layer);

	public void Close () => window.Close ();

	public void Dispose ()
	{
		if (disposed)
			return;
		disposed = true;
		StopPlayback ();
		lifetime.Cancel ();
		foreach (VideoFramePreview preview in previews)
			preview.Dispose ();
		previews.Clear ();
		previewDirectory?.Delete (recursive: true);
		lifetime.Dispose ();
	}

	private bool HandleCloseRequest (Gtk.Window sender, EventArgs args)
	{
		Dispose ();
		Closed?.Invoke (this, EventArgs.Empty);
		return false;
	}

	private Gtk.Box CreateHeader ()
	{
		Gtk.Box header = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		header.SetAllMargins (10);
		header.AddCssClass (AdwaitaStyles.Toolbar);

		Gtk.Button closeButton = Gtk.Button.NewFromIconName (StandardIcons.WindowClose);
		closeButton.SetTooltipText (Translations.GetString ("Close Video Frame Exporter"));
		closeButton.OnClicked += (_, _) => window.Close ();
		header.Append (closeButton);

		Gtk.Box title = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		Gtk.Label titleLabel = Gtk.Label.New (Translations.GetString ("Video Frame Exporter"));
		titleLabel.Halign = Gtk.Align.Start;
		titleLabel.AddCssClass (AdwaitaStyles.Heading);
		fileLabel = Gtk.Label.New (Translations.GetString ("No video open"));
		fileLabel.Halign = Gtk.Align.Start;
		fileLabel.AddCssClass (AdwaitaStyles.DimLabel);
		title.Append (titleLabel);
		title.Append (fileLabel);
		header.Append (title);

		metadataLabel = Gtk.Label.New (string.Empty);
		metadataLabel.Hexpand = true;
		metadataLabel.Halign = Gtk.Align.End;
		metadataLabel.AddCssClass (AdwaitaStyles.DimLabel);
		header.Append (metadataLabel);
		header.Append (CreateFfmpegControls ());
		header.Append (CreateFullscreenButton ());

		open_video_button = Gtk.Button.NewWithLabel (Translations.GetString ("Open Video..."));
		open_video_button.AddCssClass (AdwaitaStyles.SuggestedAction);
		open_video_button.OnClicked += HandleOpenVideoClicked;
		header.Append (open_video_button);
		return header;
	}

	private (Gtk.Picture, Gtk.Picture, Gtk.Stack, Gtk.ToggleButton, Gtk.ToggleButton, Gtk.Label, Gtk.Button, Gtk.Scale, Gtk.Label, Gtk.Button, Gtk.ToggleButton) CreatePlayerPanel ()
	{
		Gtk.Picture video = Gtk.Picture.New ();
		video.ContentFit = Gtk.ContentFit.Contain;
		video.Hexpand = true;
		video.Vexpand = true;
		Gtk.Picture source = Gtk.Picture.New ();
		source.ContentFit = Gtk.ContentFit.Contain;
		source.Hexpand = true;
		source.Vexpand = true;
		Gtk.Stack stack = Gtk.Stack.New ();
		stack.Hexpand = true;
		stack.Vexpand = true;
		stack.AddNamed (video, "sequence");
		stack.AddNamed (source, "source");
		stack.SetVisibleChildName ("sequence");

		Gtk.ToggleButton sequence = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Frame sequence"));
		Gtk.ToggleButton sourceTab = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Source video"));
		sequence.Active = true;
		sequence.OnToggled += (_, _) => {
			if (!sequence.Active)
				return;
			sourceTab.Active = false;
			StopPlayback ();
			stack.SetVisibleChildName ("sequence");
		};
		sourceTab.OnToggled += (_, _) => {
			if (!sourceTab.Active)
				return;
			sequence.Active = false;
			StopPlayback ();
			stack.SetVisibleChildName ("source");
		};

		Gtk.Label status = Gtk.Label.New (Translations.GetString ("Open a video to preview frames"));
		status.Halign = Gtk.Align.Center;
		status.Valign = Gtk.Align.Center;
		status.AddCssClass (AdwaitaStyles.DimLabel);

		Gtk.Button play = Gtk.Button.NewFromIconName ("media-playback-start-symbolic");
		play.SetTooltipText (Translations.GetString ("Play"));
		play.OnClicked += HandlePlayClicked;
		play.Sensitive = false;

		Gtk.Scale seek = Gtk.Scale.NewWithRange (Gtk.Orientation.Horizontal, 0, 1, 1);
		seek.DrawValue = false;
		seek.Hexpand = true;
		seek.OnValueChanged += HandleSeekChanged;

		Gtk.Label time = Gtk.Label.New (Translations.GetString ("00:00.00 / 00:00.00"));
		time.AddCssClass ("monospace");

		Gtk.Button speed = Gtk.Button.NewWithLabel (Translations.GetString ("1x"));
		speed.SetTooltipText (Translations.GetString ("Playback speed"));
		speed.OnClicked += HandleSpeedClicked;
		speed.Sensitive = false;

		Gtk.ToggleButton mute = Gtk.ToggleButton.New ();
		mute.SetChild (Gtk.Image.NewFromIconName ("audio-volume-muted-symbolic"));
		mute.SetTooltipText (Translations.GetString ("Mute"));
		mute.Sensitive = false;

		return (video, source, stack, sequence, sourceTab, status, play, seek, time, speed, mute);
	}

	private Gtk.Box CreatePlayerLayout ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		panel.SetAllMargins (12);
		panel.Hexpand = true;
		panel.Vexpand = false;

		Gtk.Box tabs = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		tabs.AddCssClass (AdwaitaStyles.Linked);
		tabs.Append (sequenceTab);
		tabs.Append (sourceTab);
		panel.Append (tabs);

		Gtk.Overlay videoArea = Gtk.Overlay.New ();
		videoArea.SetChild (playerStack);
		videoArea.AddOverlay (playerStatus);
		videoArea.AddCssClass (AdwaitaStyles.Osd);
		videoArea.Hexpand = true;
		videoArea.HeightRequest = 360;
		panel.Append (videoArea);

		Gtk.Box controls = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		controls.Append (CreateIconButton (StandardIcons.GoPrevious, Translations.GetString ("Previous frame"), () => HandlePreviousFrameClicked (this, EventArgs.Empty)));
		controls.Append (playButton);
		controls.Append (CreateIconButton (StandardIcons.GoNext, Translations.GetString ("Next frame"), () => HandleNextFrameClicked (this, EventArgs.Empty)));
		controls.Append (timeLabel);
		controls.Append (seekScale);
		controls.Append (speedButton);
		controls.Append (muteButton);
		panel.Append (controls);

		return panel;
	}

	private (Gtk.Label, Gtk.Label, Gtk.Label, Gtk.Label, Gtk.Label, Gtk.Label, Gtk.ToggleButton, Gtk.ToggleButton,
		Gtk.Entry, Gtk.Entry, Gtk.SpinButton, Gtk.SpinButton, Gtk.SpinButton, Gtk.Button, Gtk.Button, Gtk.Button,
		Gtk.ProgressBar) CreateInspectorPanel ()
	{
		Gtk.Label sourceFile = Gtk.Label.New (Translations.GetString ("No video open"));
		Gtk.Label resolution = Gtk.Label.New (Translations.GetString ("Not available"));
		Gtk.Label rate = Gtk.Label.New (Translations.GetString ("Not available"));
		Gtk.Label duration = Gtk.Label.New (Translations.GetString ("Not available"));
		Gtk.Label frames = Gtk.Label.New (Translations.GetString ("Not available"));
		Gtk.Label selection = Gtk.Label.New (Translations.GetString ("0 selected"));

		Gtk.ToggleButton allFrames = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("All frames"));
		Gtk.ToggleButton selectedFrames = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Selected frames"));
		allFrames.Active = true;
		allFrames.OnToggled += (_, _) => {
			if (allFrames.Active)
				selectedFrames.Active = false;
			if (metadata is not null)
				RebuildFilmstrip ();
		};
		selectedFrames.OnToggled += (_, _) => {
			if (selectedFrames.Active)
				allFrames.Active = false;
			if (metadata is not null)
				RebuildFilmstrip ();
		};

		Gtk.Entry folder = Gtk.Entry.New ();
		folder.Hexpand = true;
		folder.SetText (Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory));
		folder.OnChanged += (_, _) => UpdateExportState ();

		Gtk.Entry prefix = Gtk.Entry.New ();
		prefix.SetText ("frame_");
		prefix.Hexpand = true;
		prefix.OnChanged += (_, _) => UpdateNumberingPreview ();

		Gtk.SpinButton digits = Gtk.SpinButton.NewWithRange (1, 8, 1);
		digits.Value = 4;
		digits.WidthRequest = 72;
		digits.OnValueChanged += (_, _) => UpdateNumberingPreview ();

		Gtk.SpinButton rangeStart = Gtk.SpinButton.NewWithRange (1, 1, 1);
		Gtk.SpinButton rangeEnd = Gtk.SpinButton.NewWithRange (1, 1, 1);
		rangeStart.WidthRequest = rangeEnd.WidthRequest = 64;
		rangeStart.Sensitive = rangeEnd.Sensitive = false;
		Gtk.Button range = Gtk.Button.NewWithLabel (Translations.GetString ("Select range"));
		range.Sensitive = false;
		range.OnClicked += HandleSelectRangeClicked;

		Gtk.Button export = Gtk.Button.NewWithLabel (Translations.GetString ("Export 0 Frames"));
		export.AddCssClass (AdwaitaStyles.SuggestedAction);
		export.OnClicked += HandleExportClicked;

		Gtk.Button cancel = Gtk.Button.NewWithLabel (Translations.GetString ("Cancel Export"));
		cancel.AddCssClass (AdwaitaStyles.DestructiveAction);
		cancel.OnClicked += (_, _) => export_cts?.Cancel ();
		cancel.Hide ();

		Gtk.ProgressBar progress = Gtk.ProgressBar.New ();
		progress.ShowText = true;
		progress.Text = Translations.GetString ("Ready");
		progress.Hide ();

		return (sourceFile, resolution, rate, duration, frames, selection, allFrames, selectedFrames,
			folder, prefix, digits, rangeStart, rangeEnd, range, export, cancel, progress);
	}

	private Gtk.Box CreateInspectorLayout ()
	{
		Gtk.Box inspector = Gtk.Box.New (Gtk.Orientation.Vertical, 10);
		inspector.Hexpand = true;
		inspector.Vexpand = true;
		inspector.SetAllMargins (12);

		inspector.Append (CreateSection (
			Translations.GetString ("Source"),
			CreateInfoRows (
				(Translations.GetString ("File"), sourceFileLabel),
				(Translations.GetString ("Resolution"), sourceResolutionLabel),
				(Translations.GetString ("Frame rate"), sourceRateLabel),
				(Translations.GetString ("Duration"), sourceDurationLabel),
				(Translations.GetString ("Total frames"), sourceFramesLabel))));

		Gtk.Box selectionBox = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		selectionBox.Append (selectionLabel);
		Gtk.Box modeBox = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		modeBox.AddCssClass (AdwaitaStyles.Linked);
		modeBox.Append (allFramesButton);
		modeBox.Append (selectedFramesButton);
		selectionBox.Append (modeBox);
		Gtk.Box rangeBox = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		rangeBox.Append (Gtk.Label.New (Translations.GetString ("Frames")));
		rangeBox.Append (rangeStartSpinner);
		rangeBox.Append (Gtk.Label.New (Translations.GetString ("to")));
		rangeBox.Append (rangeEndSpinner);
		rangeBox.Append (rangeButton);
		selectionBox.Append (rangeBox);
		inspector.Append (CreateSection (Translations.GetString ("Selection"), selectionBox));

		Gtk.Box exportBox = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		exportBox.Append (CreateEntryRow (Translations.GetString ("Output folder"), outputFolderEntry, CreateBrowseButton ()));
		exportBox.Append (CreateEntryRow (Translations.GetString ("Filename prefix"), prefixEntry));
		exportBox.Append (CreateEntryRow (Translations.GetString ("Format"), Gtk.Label.New (Translations.GetString ("PNG"))));
		exportBox.Append (CreateEntryRow (Translations.GetString ("Numbering digits"), digitsSpinner));
		numberingLabel = Gtk.Label.New (string.Empty);
		numberingLabel.Halign = Gtk.Align.Start;
		numberingLabel.AddCssClass (AdwaitaStyles.DimLabel);
		exportBox.Append (numberingLabel);
		exportBox.Append (exportButton);
		exportBox.Append (cancelExportButton);
		exportBox.Append (exportProgress);
		inspector.Append (CreateSection (Translations.GetString ("Export"), exportBox));

		Gtk.Label errorHint = Gtk.Label.New (Translations.GetString ("Preview states: no video, loading, exporting, unsupported format"));
		errorHint.Wrap = true;
		errorHint.AddCssClass (AdwaitaStyles.DimLabel);
		inspector.Append (errorHint);
		inspector.Vexpand = true;
		return inspector;
	}

	private (Gtk.Grid Frames, Gtk.Label Summary, Gtk.Box Panel) CreateFilmstrip ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		panel.SetAllMargins (8);
		panel.AddCssClass (AdwaitaStyles.Toolbar);
		panel.Hexpand = true;
		panel.Vexpand = true;

		Gtk.Box header = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		Gtk.Label title = Gtk.Label.New (Translations.GetString ("Choose frames"));
		title.Halign = Gtk.Align.Start;
		title.AddCssClass (AdwaitaStyles.Heading);
		header.Append (title);
		Gtk.Label summary = Gtk.Label.New (string.Empty);
		summary.Hexpand = true;
		summary.Halign = Gtk.Align.End;
		summary.AddCssClass (AdwaitaStyles.DimLabel);
		header.Append (summary);
		Gtk.Button selectAll = CreateIconButton (StandardIcons.EditSelectAll, Translations.GetString ("Select all"), SelectAllFrames);
		Gtk.Button clear = CreateIconButton (Icons.EditSelectionNone, Translations.GetString ("Clear selection"), ClearSelection);
		header.Append (selectAll);
		header.Append (clear);
		panel.Append (header);

		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
		scroll.VscrollbarPolicy = Gtk.PolicyType.Automatic;
		scroll.Hexpand = true;
		scroll.Vexpand = true;
		Gtk.Grid frames = Gtk.Grid.New ();
		frames.ColumnSpacing = 6;
		frames.RowSpacing = 6;
		frames.SetAllMargins (2);
		scroll.SetChild (frames);
		panel.Append (scroll);
		return (frames, summary, panel);
	}

	private static Gtk.Box CreateSection (string title, Gtk.Widget content)
	{
		Gtk.Box section = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		Gtk.Label heading = Gtk.Label.New (title);
		heading.Halign = Gtk.Align.Start;
		heading.AddCssClass (AdwaitaStyles.Heading);
		section.Append (heading);
		section.Append (content);
		return section;
	}

	private static Gtk.Box CreateInfoRows (params (string Name, Gtk.Label Value)[] rows)
	{
		Gtk.Box result = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
		foreach ((string name, Gtk.Label value) in rows) {
			Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
			Gtk.Label nameLabel = Gtk.Label.New (name);
			nameLabel.AddCssClass (AdwaitaStyles.DimLabel);
			nameLabel.Hexpand = true;
			nameLabel.Halign = Gtk.Align.Start;
			value.Halign = Gtk.Align.End;
			row.Append (nameLabel);
			row.Append (value);
			result.Append (row);
		}
		return result;
	}

	private static Gtk.Box CreateEntryRow (string label, Gtk.Widget entry, Gtk.Widget? trailing = null)
	{
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		Gtk.Label name = Gtk.Label.New (label);
		name.WidthRequest = 112;
		name.Halign = Gtk.Align.Start;
		name.AddCssClass (AdwaitaStyles.DimLabel);
		row.Append (name);
		entry.Hexpand = true;
		row.Append (entry);
		if (trailing is not null)
			row.Append (trailing);
		return row;
	}

	private Gtk.Button CreateBrowseButton ()
	{
		Gtk.Button browse = Gtk.Button.NewFromIconName (StandardIcons.Folder);
		browse.SetTooltipText (Translations.GetString ("Choose output folder"));
		browse.OnClicked += HandleChooseFolderClicked;
		return browse;
	}

	private static Gtk.Button CreateIconButton (string iconName, string tooltip, Action handler)
	{
		Gtk.Button button = Gtk.Button.NewFromIconName (iconName);
		button.SetTooltipText (tooltip);
		button.OnClicked += (_, _) => handler ();
		return button;
	}

	private void ResetView ()
	{
		playerStatus.RemoveCssClass (AdwaitaStyles.Error);
		playerStatus.SetText (Translations.GetString ("Open a video to preview frames"));
		playerStatus.Show ();
		player.Hide ();
		seekScale.Sensitive = false;
		playButton.Sensitive = false;
		selectionLabel.SetText (Translations.GetString ("0 selected"));
		UpdateNumberingPreview ();
		UpdateExportState ();
	}

	private void UpdateExportState ()
	{
		bool ready = ffmpeg_ready && metadata is not null && !string.IsNullOrEmpty (videoFilename);
		exportButton.Sensitive = ready && (allFramesButton.Active || selectedIndices.Count > 0);
		int count = allFramesButton.Active ? metadata?.TotalFrames ?? 0 : selectedIndices.Count;
		exportButton.SetLabel (Translations.GetString ("Export {0} Frames", count));
	}

	private void UpdateNumberingPreview ()
	{
		numberingLabel.SetText ($"{prefixEntry.GetText ()}{1.ToString ($"D{(int) digitsSpinner.GetValue ()}", System.Globalization.CultureInfo.InvariantCulture)}.png");
	}

	private void UpdateMetadataLabels ()
	{
		if (metadata is not VideoMetadata data || videoFilename is null)
			return;
		string filename = Path.GetFileName (videoFilename);
		fileLabel.SetText (filename);
		metadataLabel.SetText (Translations.GetString ("{0} x {1}   {2:0.##} fps   {3}", data.Width, data.Height, data.FrameRate, FormatTime (data.Duration)));
		sourceFileLabel.SetText (filename);
		sourceResolutionLabel.SetText (Translations.GetString ("{0} x {1}", data.Width, data.Height));
		sourceRateLabel.SetText (Translations.GetString ("{0:0.##} fps", data.FrameRate));
		sourceDurationLabel.SetText (FormatTime (data.Duration));
		sourceFramesLabel.SetText (data.TotalFrames.ToString (CultureInfo.InvariantCulture));
		rangeStartSpinner.Adjustment!.Upper = data.TotalFrames;
		rangeEndSpinner.Adjustment!.Upper = data.TotalFrames;
		rangeStartSpinner.SetValue (1);
		rangeEndSpinner.SetValue (Math.Min (3, data.TotalFrames));
		rangeStartSpinner.Sensitive = true;
		rangeEndSpinner.Sensitive = true;
		rangeButton.Sensitive = true;
	}

	private static string FormatTime (double seconds)
	{
		TimeSpan time = TimeSpan.FromSeconds (Math.Max (0, seconds));
		return Translations.GetString ("{0:00}:{1:00}.{2:00}", (int) time.TotalMinutes, time.Seconds, time.Milliseconds / 10);
	}

	private CancellationTokenSource? export_cts;
}
