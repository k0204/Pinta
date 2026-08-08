using System;
using System.Collections.Generic;
using System.Linq;
using Pinta.Core;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private void RebuildFilmstrip ()
	{
		while (filmstrip.GetFirstChild () is Gtk.Widget child)
			filmstrip.Remove (child);

		IEnumerable<VideoFramePreview> visible = selectedFramesButton.Active
			? previews.Where (preview => selectedIndices.Contains (preview.SourceIndex))
			: previews;
		VideoFramePreview[] visiblePreviews = visible.ToArray ();
		for (int index = 0; index < visiblePreviews.Length; index++)
			filmstrip.Attach (CreateThumbnailButton (visiblePreviews[index]), index % 3, index / 3, 1, 1);
		UpdateSelectionSummary (visiblePreviews.Length);
	}

	private Gtk.ToggleButton CreateThumbnailButton (VideoFramePreview preview)
	{
		Gtk.ToggleButton button = Gtk.ToggleButton.New ();
		button.WidthRequest = 0;
		button.HeightRequest = 132;
		button.Hexpand = true;
		Gtk.CheckButton selectedIndicator = Gtk.CheckButton.New ();
		selectedIndicator.SetCanTarget (false);
		selectedIndicator.Valign = Gtk.Align.Start;
		selectedIndicator.Halign = Gtk.Align.Start;
		selectedIndicator.SetMarginTop (6);
		selectedIndicator.SetMarginStart (6);
		selectedIndicator.Active = selectedIndices.Contains (preview.SourceIndex);
		button.Active = selectedIndicator.Active;
		button.SetTooltipText (Translations.GetString ("Frame {0} at {1}", preview.SourceIndex + 1, FormatTime (preview.Time.TotalSeconds)));
		button.OnToggled += (_, _) => {
			selectedIndicator.Active = button.Active;
			if (button.Active) {
				selectedIndices.Add (preview.SourceIndex);
				SetFrameIndex (preview.SourceIndex);
			} else {
				selectedIndices.Remove (preview.SourceIndex);
				if (preview.SourceIndex == currentFrameIndex)
					MoveToSelectedNeighbor (preview.SourceIndex);
			}
			UpdateSelectionSummary ();
		};

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		Gtk.Picture picture = Gtk.Picture.New ();
		picture.SetPaintable (preview.Texture);
		picture.ContentFit = Gtk.ContentFit.Contain;
		picture.SetSizeRequest (-1, 96);
		picture.Hexpand = true;
		picture.Vexpand = true;
		Gtk.Overlay imageArea = Gtk.Overlay.New ();
		imageArea.SetChild (picture);
		imageArea.AddOverlay (selectedIndicator);
		content.Append (imageArea);

		Gtk.Label frameLabel = Gtk.Label.New (Translations.GetString ("F{0}", preview.SourceIndex + 1));
		frameLabel.AddCssClass ("monospace");
		content.Append (frameLabel);
		Gtk.Label timeLabel = Gtk.Label.New (FormatTime (preview.Time.TotalSeconds));
		timeLabel.AddCssClass (AdwaitaStyles.DimLabel);
		content.Append (timeLabel);
		button.SetChild (content);
		if (preview.SourceIndex == currentFrameIndex)
			button.AddCssClass (AdwaitaStyles.SuggestedAction);
		return button;
	}

	private void UpdateSelectionSummary (int? visibleCount = null)
	{
		int shown = visibleCount ?? previews.Count;
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
		RebuildFilmstrip ();
	}

	private void ClearSelection ()
	{
		selectedFramesButton.Active = true;
		selectedIndices.Clear ();
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
		RebuildFilmstrip ();
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
}
