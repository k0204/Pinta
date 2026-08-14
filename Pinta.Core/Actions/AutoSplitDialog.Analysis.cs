using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

internal sealed partial class AutoSplitDialog
{
	private async Task RunDetectionAsync ()
	{
		if (detection_mode.Active == 2)
			return;

		analysis_running = true;
		UpdateActionState ();
		detect_button.Sensitive = false;
		try {
			if (detection_mode.Active == 0) {
				int minimumSize = minimum_tile_size_spinner.GetValueAsInt ();
				ApplyRegions (
					AutoSplitDetection.DetectLocal (
						source.Surface,
						minimumWidth: minimumSize,
						minimumHeight: minimumSize),
					count => Translations.GetString ("Local pixel scan found {0} regions.", count));
			} else if (providers.Count == 0) {
				status_label.SetText (Translations.GetString ("No API provider is available."));
			} else if (ensure_ai_logged_in is not null && !ensure_ai_logged_in ()) {
				status_label.SetText (Translations.GetString ("Log in to the AI service before API analysis."));
			} else {
				string provider = providers[api_provider.Active].Id;
				AI.AiRequestSettings.SaveSpriteSegmentationProvider (PintaCore.Settings, provider);
				PintaCore.Settings.DoSaveSettingsBeforeQuit ();
				AI.SpriteSegmentationAnalysis analysis = await analyze (provider);
				ApplyRegions (
					[.. analysis.Items.Select (item => new RectangleI (
						item.Bbox.X,
						item.Bbox.Y,
						item.Bbox.Width,
						item.Bbox.Height))],
					count => Translations.GetString ("API pixel analysis found {0} regions.", count));
			}
		} catch (Exception ex) {
			status_label.AddCssClass (AdwaitaStyles.Error);
			status_label.SetText (Translations.GetString ("Pixel analysis failed: {0}", ex.Message));
		} finally {
			analysis_running = false;
			detect_button.Sensitive = detection_mode.Active != 2 && providers.Count > 0 || detection_mode.Active == 0;
			UpdateActionState ();
		}
	}

	private void ApplyLocalDetection ()
	{
		int minimumSize = minimum_tile_size_spinner.GetValueAsInt ();
		ApplyRegions (
			AutoSplitDetection.DetectLocal (
				source.Surface,
				minimumWidth: minimumSize,
				minimumHeight: minimumSize),
			count => Translations.GetString ("Local pixel scan found {0} regions.", count));
	}

	private void ApplyRegions (IReadOnlyList<RectangleI> bounds, Func<int, string> status_factory)
	{
		regions.Clear ();
		foreach (RectangleI bound in bounds) {
			if (TryNormalizeBounds (bound, out RectangleI normalized))
				regions.Add (new AutoSplitRegion (normalized));
		}

		selected_regions.Clear ();
		selected_region = regions.Count > 0 ? 0 : -1;
		if (selected_region >= 0)
			selected_regions.Add (selected_region);
		RefreshRegionList ();
		status_label.RemoveCssClass (AdwaitaStyles.Error);
		status_label.SetText (status_factory (regions.Count));
		UpdateActionState ();
	}

	private void AddDefaultRegion ()
	{
		int width = Math.Max (1, Math.Min (source.Surface.Width, source.Surface.Width / 4));
		int height = Math.Max (1, Math.Min (source.Surface.Height, source.Surface.Height / 4));
		int x = Math.Max (0, (source.Surface.Width - width) / 2);
		int y = Math.Max (0, (source.Surface.Height - height) / 2);
		regions.Add (new AutoSplitRegion (new RectangleI (x, y, width, height)));
		SelectRegion (regions.Count - 1);
		RefreshRegionList ();
		status_label.RemoveCssClass (AdwaitaStyles.Error);
		status_label.SetText (Translations.GetString ("Added a manual region."));
		UpdateActionState ();
	}

	private void DeleteSelectedRegion ()
	{
		if (selected_regions.Count == 0)
			return;

		int nextRegion = selected_region;
		foreach (int index in selected_regions.OrderByDescending (index => index))
			regions.RemoveAt (index);

		selected_regions.Clear ();
		selected_region = regions.Count == 0 ? -1 : Math.Clamp (nextRegion, 0, regions.Count - 1);
		if (selected_region >= 0)
			selected_regions.Add (selected_region);
		RefreshRegionList ();
		status_label.SetText (Translations.GetString ("Region deleted."));
		UpdateActionState ();
	}

	private bool TryNormalizeBounds (RectangleI sourceBounds, out RectangleI normalized)
	{
		int left = Math.Clamp (sourceBounds.X, 0, source.Surface.Width - 1);
		int top = Math.Clamp (sourceBounds.Y, 0, source.Surface.Height - 1);
		int right = Math.Clamp (sourceBounds.X + sourceBounds.Width, left + 1, source.Surface.Width);
		int bottom = Math.Clamp (sourceBounds.Y + sourceBounds.Height, top + 1, source.Surface.Height);
		normalized = new RectangleI (left, top, right - left, bottom - top);
		return sourceBounds.Width > 0 && sourceBounds.Height > 0;
	}
}
