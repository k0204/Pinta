using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private const int ThumbnailWidth = 320;
	private const int ThumbnailHeight = 96;

	private Gtk.GridView CreateThumbnailGrid ()
	{
		thumbnail_model = Gtk.StringList.New (null);
		Gtk.NoSelection selection = Gtk.NoSelection.New (thumbnail_model);
		Gtk.SignalListItemFactory factory = Gtk.SignalListItemFactory.New ();
		factory.OnSetup += HandleThumbnailSetup;
		factory.OnBind += HandleThumbnailBind;
		factory.OnUnbind += HandleThumbnailUnbind;

		Gtk.GridView grid = Gtk.GridView.New (selection, factory);
		grid.MinColumns = 1;
		grid.MaxColumns = 3;
		grid.Hexpand = true;
		grid.Vexpand = true;
		grid.CanFocus = false;
		return grid;
	}

	private void HandleThumbnailSetup (Gtk.SignalListItemFactory factory, Gtk.SignalListItemFactory.SetupSignalArgs args)
	{
		Gtk.ListItem item = (Gtk.ListItem) args.Object;
		ThumbnailBinding binding = CreateThumbnailBinding ();
		thumbnail_bindings[item] = binding;
		item.SetChild (binding.Button);
	}

	private void HandleThumbnailBind (Gtk.SignalListItemFactory factory, Gtk.SignalListItemFactory.BindSignalArgs args)
	{
		Gtk.ListItem item = (Gtk.ListItem) args.Object;
		if (!thumbnail_bindings.TryGetValue (item, out ThumbnailBinding? binding))
			return;

		int position = (int) item.Position;
		if (position < 0 || position >= visible_previews.Count) {
			UnbindThumbnail (binding);
			return;
		}

		BindThumbnail (binding, visible_previews[position]);
	}

	private void HandleThumbnailUnbind (Gtk.SignalListItemFactory factory, Gtk.SignalListItemFactory.UnbindSignalArgs args)
	{
		Gtk.ListItem item = (Gtk.ListItem) args.Object;
		if (thumbnail_bindings.TryGetValue (item, out ThumbnailBinding? binding))
			UnbindThumbnail (binding);
	}

	private ThumbnailBinding CreateThumbnailBinding ()
	{
		Gtk.ToggleButton button = Gtk.ToggleButton.New ();
		button.HeightRequest = 132;
		button.Hexpand = true;
		button.Halign = Gtk.Align.Fill;
		button.Valign = Gtk.Align.Start;

		Gtk.CheckButton selectedIndicator = Gtk.CheckButton.New ();
		selectedIndicator.SetCanTarget (false);
		selectedIndicator.Valign = Gtk.Align.Start;
		selectedIndicator.Halign = Gtk.Align.Start;
		selectedIndicator.SetMarginTop (6);
		selectedIndicator.SetMarginStart (6);

		Gtk.Picture picture = Gtk.Picture.New ();
		picture.ContentFit = Gtk.ContentFit.Contain;
		picture.CanShrink = true;
		picture.Hexpand = true;
		picture.Valign = Gtk.Align.Center;

		Gtk.Overlay imageArea = Gtk.Overlay.New ();
		imageArea.HeightRequest = ThumbnailHeight;
		imageArea.Hexpand = true;
		imageArea.Valign = Gtk.Align.Start;
		imageArea.SetChild (picture);
		imageArea.AddOverlay (selectedIndicator);

		Gtk.Label frameLabel = Gtk.Label.New (string.Empty);
		frameLabel.AddCssClass ("monospace");
		Gtk.Label timeLabel = Gtk.Label.New (string.Empty);
		timeLabel.AddCssClass (AdwaitaStyles.DimLabel);

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		content.Append (imageArea);
		content.Append (frameLabel);
		content.Append (timeLabel);
		button.SetChild (content);

		ThumbnailBinding binding = new (button, picture, selectedIndicator, frameLabel, timeLabel);
		button.OnToggled += (_, _) => HandleThumbnailToggled (binding);
		return binding;
	}

	private void BindThumbnail (ThumbnailBinding binding, VideoFramePreview frame)
	{
		binding.LoadCts?.Cancel ();
		binding.LoadCts?.Dispose ();
		binding.LoadCts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		binding.Frame = frame;
		binding.Updating = true;
		binding.Button.Active = selectedIndices.Contains (frame.SourceIndex);
		binding.Updating = false;
		binding.SelectedIndicator.Active = binding.Button.Active;
		binding.Button.SetTooltipText (Translations.GetString (
			"Frame {0} at {1}", frame.SourceIndex + 1, FormatTime (frame.Time.TotalSeconds)));
		binding.FrameLabel.SetText (Translations.GetString ("F{0}", frame.SourceIndex + 1));
		binding.TimeLabel.SetText (FormatTime (frame.Time.TotalSeconds));
		binding.Picture.SetPaintable (null);
		binding.Button.RemoveCssClass (AdwaitaStyles.SuggestedAction);
		if (frame.SourceIndex == currentFrameIndex)
			binding.Button.AddCssClass (AdwaitaStyles.SuggestedAction);

		_ = LoadThumbnailAsync (binding, frame, binding.LoadCts.Token);
	}

	private void UnbindThumbnail (ThumbnailBinding binding)
	{
		binding.LoadCts?.Cancel ();
		binding.LoadCts?.Dispose ();
		binding.LoadCts = null;
		binding.Frame = null;
		binding.Updating = true;
		binding.Button.Active = false;
		binding.Updating = false;
		binding.SelectedIndicator.Active = false;
		binding.Picture.SetPaintable (null);
		binding.Button.RemoveCssClass (AdwaitaStyles.SuggestedAction);
	}

	private void ClearThumbnailBindings ()
	{
		foreach (ThumbnailBinding binding in thumbnail_bindings.Values)
			UnbindThumbnail (binding);
	}

	private void HandleThumbnailToggled (ThumbnailBinding binding)
	{
		if (binding.Updating || binding.Frame is not VideoFramePreview frame)
			return;

		binding.SelectedIndicator.Active = binding.Button.Active;
		if (binding.Button.Active) {
			selectedIndices.Add (frame.SourceIndex);
			SetFrameIndex (frame.SourceIndex);
		} else {
			selectedIndices.Remove (frame.SourceIndex);
			if (frame.SourceIndex == currentFrameIndex)
				MoveToSelectedNeighbor (frame.SourceIndex);
		}
		SaveSelection ();
		UpdateSelectionSummary ();
	}

	private Task LoadThumbnailAsync (ThumbnailBinding binding, VideoFramePreview frame, CancellationToken cancellationToken)
		=> LoadThumbnailAsyncCore (binding, frame, cancellationToken);

	private async Task LoadThumbnailAsyncCore (
		ThumbnailBinding binding,
		VideoFramePreview frame,
		CancellationToken cancellationToken)
	{
		try {
			Gdk.Texture? texture = await LoadThumbnailTextureAsync (frame.SourcePath, cancellationToken);
			if (texture is not null
				&& !cancellationToken.IsCancellationRequested
				&& ReferenceEquals (binding.Frame, frame))
				binding.Picture.SetPaintable (texture);
		} catch (OperationCanceledException) {
		} catch (Exception ex) {
			Console.Error.WriteLine ($"Thumbnail loading failed: {ex}");
		}
	}

	private void StartCurrentFrameLoad (VideoFramePreview frame)
	{
		current_frame_load_version++;
		int version = current_frame_load_version;
		current_frame_cts?.Cancel ();
		current_frame_cts?.Dispose ();
		current_frame_cts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		_ = LoadCurrentFrameAsync (frame, version, current_frame_cts.Token);
	}

	private async Task LoadCurrentFrameAsync (
		VideoFramePreview frame,
		int version,
		CancellationToken cancellationToken)
	{
		try {
			Gdk.Texture? texture = await LoadOriginalTextureAsync (frame.SourcePath, cancellationToken);
			if (texture is not null && version == current_frame_load_version && !disposed) {
				player.SetPaintable (texture);
				sourceVideo.SetPaintable (texture);
			}
		} catch (OperationCanceledException) {
		} catch (Exception ex) {
			Console.Error.WriteLine ($"Current frame loading failed: {ex}");
		}
	}

	private async Task<Gdk.Texture?> LoadThumbnailTextureAsync (string path, CancellationToken cancellationToken)
	{
		if (thumbnail_cache.TryGet (path, out Gdk.Texture? cached))
			return cached;

		GdkPixbuf.Pixbuf? pixbuf = await Task.Run (
			() => GdkPixbuf.Pixbuf.NewFromFileAtScale (path, ThumbnailWidth, ThumbnailHeight, true),
			cancellationToken);
		if (pixbuf is null)
			return null;

		using (pixbuf) {
			cancellationToken.ThrowIfCancellationRequested ();
			Gdk.Texture texture = Gdk.Texture.NewForPixbuf (pixbuf);
			return thumbnail_cache.Add (path, texture);
		}
	}

	private static async Task<Gdk.Texture?> LoadOriginalTextureAsync (string path, CancellationToken cancellationToken)
	{
		GdkPixbuf.Pixbuf? pixbuf = await Task.Run (
			() => GdkPixbuf.Pixbuf.NewFromFile (path),
			cancellationToken);
		if (pixbuf is null)
			return null;

		using (pixbuf) {
			cancellationToken.ThrowIfCancellationRequested ();
			return Gdk.Texture.NewForPixbuf (pixbuf);
		}
	}

	private void RebuildFilmstrip ()
	{
		visible_previews.Clear ();
		IEnumerable<VideoFramePreview> visible = selectedFramesButton.Active
			? previews.Where (preview => selectedIndices.Contains (preview.SourceIndex))
			: previews;
		visible_previews.AddRange (visible);
		thumbnail_model.Splice (
			0,
			thumbnail_model.NItems,
			Enumerable.Repeat (string.Empty, visible_previews.Count).ToArray ());
		UpdateSelectionSummary (visible_previews.Count);
	}

	private void UpdateSelectionSummary (int? visibleCount = null)
	{
		int shown = visibleCount ?? visible_previews.Count;
		string summary = Translations.GetString ("{0} shown · {1} selected", shown, selectedIndices.Count);
		selectionLabel.SetText (summary);
		filmstripSummary.SetText (summary);
		playButton.Sensitive = previews.Count >= 2 && selectedIndices.Count >= 2;
		UpdateExportState ();
	}

	private void SelectAllFrames ()
	{
		allFramesButton.Active = true;
		selectedIndices.Clear ();
		selectedIndices.UnionWith (previews.Select (preview => preview.SourceIndex));
		SaveSelection ();
		RebuildFilmstrip ();
	}

	private void ClearSelection ()
	{
		selectedFramesButton.Active = true;
		selectedIndices.Clear ();
		SaveSelection ();
		RebuildFilmstrip ();
	}

	private void HandleSelectRangeClicked (object sender, EventArgs args)
	{
		int start = Math.Min ((int) rangeStartSpinner.GetValue (), (int) rangeEndSpinner.GetValue ()) - 1;
		int end = Math.Max ((int) rangeStartSpinner.GetValue (), (int) rangeEndSpinner.GetValue ()) - 1;
		selectedFramesButton.Active = true;
		selectedIndices.Clear ();
		for (int index = start; index <= end; index++)
			selectedIndices.Add (index);
		SaveSelection ();
		RebuildFilmstrip ();
	}

	private void SaveSelection ()
	{
		if (videoLayer is null)
			return;
		videoLayer.SelectedFrames = selectedIndices.Count == previews.Count
			? "*"
			: string.Join (',', selectedIndices.Order ());
		if (PintaCore.Workspace.ActiveDocumentOrDefault is Document document
			&& document.Layers.AllLayers.Contains (videoLayer))
			document.IsDirty = true;
	}

	private void MoveToSelectedNeighbor (int sourceIndex)
	{
		int? next = previews
			.Where (preview => preview.SourceIndex > sourceIndex && selectedIndices.Contains (preview.SourceIndex))
			.Select (preview => (int?) preview.SourceIndex)
			.FirstOrDefault ();
		int? previous = previews
			.Where (preview => preview.SourceIndex < sourceIndex && selectedIndices.Contains (preview.SourceIndex))
			.Select (preview => (int?) preview.SourceIndex)
			.LastOrDefault ();
		int? replacement = next ?? previous;
		if (replacement is int index)
			SetFrameIndex (index);
	}

	private sealed class ThumbnailBinding (
		Gtk.ToggleButton button,
		Gtk.Picture picture,
		Gtk.CheckButton selectedIndicator,
		Gtk.Label frameLabel,
		Gtk.Label timeLabel)
	{
		public Gtk.ToggleButton Button { get; } = button;
		public Gtk.Picture Picture { get; } = picture;
		public Gtk.CheckButton SelectedIndicator { get; } = selectedIndicator;
		public Gtk.Label FrameLabel { get; } = frameLabel;
		public Gtk.Label TimeLabel { get; } = timeLabel;
		public VideoFramePreview? Frame { get; set; }
		public CancellationTokenSource? LoadCts { get; set; }
		public bool Updating { get; set; }
	}

	private sealed class ThumbnailCache
	{
		private const int MaxEntries = 24;
		private readonly Dictionary<string, CacheEntry> entries = new (StringComparer.OrdinalIgnoreCase);
		private readonly LinkedList<string> lru = new ();

		public bool TryGet (string path, out Gdk.Texture? texture)
		{
			if (!entries.TryGetValue (path, out CacheEntry? entry)) {
				texture = null;
				return false;
			}

			Touch (entry);
			texture = entry.Texture;
			return true;
		}

		public Gdk.Texture Add (string path, Gdk.Texture texture)
		{
			if (entries.TryGetValue (path, out CacheEntry? existing)) {
				texture.Dispose ();
				Touch (existing);
				return existing.Texture;
			}

			CacheEntry entry = new (path, texture, lru.AddFirst (path));
			entries.Add (path, entry);
			while (entries.Count > MaxEntries) {
				LinkedListNode<string> last = lru.Last!;
				lru.RemoveLast ();
				if (entries.Remove (last.Value, out CacheEntry? removed))
					removed.Texture.Dispose ();
			}
			return texture;
		}

		public void Clear ()
		{
			foreach (CacheEntry entry in entries.Values)
				entry.Texture.Dispose ();
			entries.Clear ();
			lru.Clear ();
		}

		private void Touch (CacheEntry entry)
		{
			lru.Remove (entry.Node);
			entry.Node = lru.AddFirst (entry.Path);
		}

		private sealed class CacheEntry (string path, Gdk.Texture texture, LinkedListNode<string> node)
		{
			public string Path { get; } = path;
			public Gdk.Texture Texture { get; } = texture;
			public LinkedListNode<string> Node { get; set; } = node;
		}
	}
}
