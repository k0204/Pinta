using System;
using System.Linq;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private static readonly double[] playback_speeds = [0.5, 1, 1.5, 2];
	private GLibTimer playback_timer;
	private bool is_playing;
	private int playback_speed_index = 1;

	private void HandlePlayClicked (object sender, EventArgs args)
	{
		if (is_playing)
			StopPlayback ();
		else
			StartPlayback ();
	}

	private void HandleSpeedClicked (object sender, EventArgs args)
	{
		playback_speed_index = (playback_speed_index + 1) % playback_speeds.Length;
		speedButton.SetLabel (Translations.GetString ("{0:0.#}x", playback_speeds[playback_speed_index]));
		if (is_playing)
			StartPlayback ();
	}

	private void StartPlayback ()
	{
		VideoFramePreview[] sequence = GetSelectedSequence ();
		if (sequence.Length < 2 || metadata is null)
			return;
		if (!selectedIndices.Contains (currentFrameIndex))
			SetFrameIndex (sequence[0].SourceIndex);

		StopPlayback (updateIcon: false);
		is_playing = true;
		SetPlayIcon (true);
		uint interval = (uint) Math.Max (
			16,
			Math.Round (metadata.Duration * 1000 / sequence.Length / playback_speeds[playback_speed_index]));
		playback_timer = GLib.Functions.TimeoutAdd (0, interval, () => {
			if (!is_playing || disposed)
				return false;

			int position = Array.FindIndex (sequence, preview => preview.SourceIndex == currentFrameIndex);
			if (position >= sequence.Length - 1) {
				StopPlayback ();
				return false;
			}

			SetFrameIndex (sequence[position + 1].SourceIndex);
			return true;
		});
	}

	private VideoFramePreview[] GetSelectedSequence ()
		=> previews.Where (preview => selectedIndices.Contains (preview.SourceIndex))
			.OrderBy (preview => preview.SourceIndex)
			.ToArray ();

	private void StopPlayback (bool updateIcon = true)
	{
		is_playing = false;
		playback_timer.Dispose ();
		playback_timer = default;
		if (updateIcon)
			SetPlayIcon (false);
	}


	private void SetPlayIcon (bool playing)
	{
		playButton.SetChild (Gtk.Image.NewFromIconName (
			playing ? "media-playback-pause-symbolic" : "media-playback-start-symbolic"));
		playButton.SetTooltipText (Translations.GetString (playing ? "Pause" : "Play"));
	}
}
