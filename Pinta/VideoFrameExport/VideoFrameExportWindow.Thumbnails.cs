using System;
using System.Linq;
using Pinta.Core;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private void RebuildFilmstrip ()
	{
		while (filmstrip.GetFirstChild () is Gtk.Widget child)
			filmstrip.Remove (child);

		foreach (VideoFramePreview preview in previews)
			filmstrip.Append (CreateThumbnailButton (preview));
		UpdateSelectionSummary ();
	}

	private Gtk.ToggleButton CreateThumbnailButton (VideoFramePreview preview)
	{
		Gtk.ToggleButton button = Gtk.ToggleButton.New ();
		button.WidthRequest = 116;
		button.HeightRequest = 122;
		button.Active = selectedIndices.Contains (preview.SourceIndex);
		button.SetTooltipText (Translations.GetString ("Frame {0} at {1}", preview.SourceIndex + 1, FormatTime (preview.Time.TotalSeconds)));
		button.OnToggled += (_, _) => {
			if (button.Active)
				selectedIndices.Add (preview.SourceIndex);
			else
				selectedIndices.Remove (preview.SourceIndex);
			UpdateSelectionSummary ();
		};

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		Gtk.Picture picture = Gtk.Picture.New ();
		picture.SetPaintable (preview.Texture);
		picture.ContentFit = Gtk.ContentFit.Contain;
		picture.SetSizeRequest (104, 82);
		picture.Hexpand = true;
		picture.Vexpand = true;
		content.Append (picture);

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

	private void UpdateSelectionSummary ()
	{
		int total = metadata?.TotalFrames ?? 0;
		string summary = Translations.GetString ("{0} selected / {1}", selectedIndices.Count, total);
		selectionLabel.SetText (summary);
		filmstripSummary.SetText (summary);
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
}
