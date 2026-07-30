using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog
{
	private readonly Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze;
	private IReadOnlyList<RectangleI>? source_rectangles;

	private Gtk.Widget BuildSmartAnalyzeControls ()
	{
		IReadOnlyList<AI.AiProviderInfo> providers = PintaCore.AiProviders.ChatProviders;
		Gtk.Box controls = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		Gtk.Label label = Gtk.Label.New (Translations.GetString ("Analysis provider:"));
		label.Halign = Gtk.Align.Start;
		Gtk.ComboBoxText provider = Gtk.ComboBoxText.New ();
		foreach (AI.AiProviderInfo item in providers)
			provider.AppendText (item.Name);
		provider.Active = GetProviderIndex (
			providers,
			AI.AiRequestSettings.GetSpriteSegmentationProvider (PintaCore.Settings));
		provider.OnChanged += (_, _) => SaveProvider (provider, providers);
		Gtk.Button button = Gtk.Button.NewWithLabel ("自动分析");
		button.TooltipText = "使用 AI 分析精灵边界和脚底锚点";
		button.Sensitive = providers.Count > 0;
		button.OnClicked += async (_, _) => await AnalyzeAsync (button, provider, providers);
		controls.Append (label);
		controls.Append (provider);
		controls.Append (button);
		return controls;
	}

	private async Task AnalyzeAsync (
		Gtk.Button button,
		Gtk.ComboBoxText provider,
		IReadOnlyList<AI.AiProviderInfo> providers)
	{
		button.Sensitive = false;
		provider.Sensitive = false;
		validation_label.RemoveCssClass (AdwaitaStyles.Error);
		validation_label.SetText (Translations.GetString ("Analyzing sprite bounds..."));
		try {
			ApplyAnalysis (await analyze (providers[provider.Active].Id));
			validation_label.SetText (Translations.GetString ("Smart analysis found {0} sprites.", frames.Count));
		} catch (Exception ex) {
			validation_label.AddCssClass (AdwaitaStyles.Error);
			validation_label.SetText (Translations.GetString ("Smart analysis failed: {0}", ex.Message));
		} finally {
			provider.Sensitive = true;
			button.Sensitive = true;
		}
	}

	private static int GetProviderIndex (IReadOnlyList<AI.AiProviderInfo> providers, string selected)
	{
		for (int index = 0; index < providers.Count; index++)
			if (providers[index].Id == selected)
				return index;
		return 0;
	}

	private static void SaveProvider (
		Gtk.ComboBoxText provider,
		IReadOnlyList<AI.AiProviderInfo> providers)
	{
		if (provider.Active < 0)
			return;
		AI.AiRequestSettings.SaveSpriteSegmentationProvider (
			PintaCore.Settings,
			providers[provider.Active].Id);
		PintaCore.Settings.DoSaveSettingsBeforeQuit ();
	}

	private void ApplyAnalysis (AI.SpriteSegmentationAnalysis analysis)
	{
		syncing = true;
		columns.Value = analysis.Grid.Columns;
		rows.Value = analysis.Grid.Rows;
		cell_width.Value = analysis.Items.Max (item => item.Bbox.Width);
		cell_height.Value = analysis.Items.Max (item => item.Bbox.Height);
		offset_x.Value = offset_y.Value = gap_x.Value = gap_y.Value = 0;
		align_character.Active = false;
		syncing = false;

		source_rectangles = [.. analysis.Items.Select (item => new RectangleI (
			item.Bbox.X, item.Bbox.Y, item.Bbox.Width, item.Bbox.Height))];
		RebuildFrames ();
		ApplyAnchorAlignment (analysis.Items);
		Refresh ();
	}

	private void ApplyAnchorAlignment (IReadOnlyList<AI.SpriteSegmentationItem> items)
	{
		double left = items.Max (item => item.FootAnchor.X - item.Bbox.X);
		double right = items.Max (item => item.Bbox.X + item.Bbox.Width - item.FootAnchor.X);
		double top = items.Max (item => item.FootAnchor.Y - item.Bbox.Y);
		double bottom = items.Max (item => item.Bbox.Y + item.Bbox.Height - item.FootAnchor.Y);
		int anchorX = (int) Math.Ceiling (left);
		int anchorY = (int) Math.Ceiling (top);
		canvas_width.Value = Math.Ceiling (left + right);
		canvas_height.Value = Math.Ceiling (top + bottom);
		for (int index = 0; index < items.Count; index++) {
			frames[index].X = (int) Math.Round (anchorX - (items[index].FootAnchor.X - items[index].Bbox.X));
			frames[index].Y = (int) Math.Round (anchorY - (items[index].FootAnchor.Y - items[index].Bbox.Y));
		}
		SelectFrame (0);
	}

	private void ResetAnalysisAndRefresh ()
	{
		if (!syncing)
			source_rectangles = null;
		Refresh ();
	}

	private void ResetAnalysisAndRebuildFrames ()
	{
		if (syncing)
			return;
		source_rectangles = null;
		RebuildFrames ();
	}
}
