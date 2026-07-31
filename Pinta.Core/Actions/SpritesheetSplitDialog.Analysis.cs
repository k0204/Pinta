using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog
{
	private readonly Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze;
	private IReadOnlyList<RectangleI>? source_rectangles;

	private Gtk.Widget BuildSourceModeTabs ()
	{
		Gtk.Box gridControls = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		gridControls.Append (CreateGrid ([
			(Translations.GetString ("Columns:"), columns),
			(Translations.GetString ("Rows:"), rows),
			(Translations.GetString ("Cell width:"), cell_width),
			(Translations.GetString ("Cell height:"), cell_height),
			(Translations.GetString ("Left offset:"), offset_x),
			(Translations.GetString ("Top offset:"), offset_y),
			(Translations.GetString ("Horizontal gap:"), gap_x),
			(Translations.GetString ("Vertical gap:"), gap_y),
		]));
		gridControls.Append (align_character);

		source_mode_stack.AddTitled (BuildSmartAnalyzeControls (), ai_source_mode, Translations.GetString ("AI analysis"));
		source_mode_stack.AddTitled (gridControls, grid_source_mode, Translations.GetString ("Grid"));
		source_mode_stack.VisibleChildName = grid_source_mode;

		Adw.ViewSwitcher switcher = Adw.ViewSwitcher.New ();
		switcher.Stack = source_mode_stack;
		Gtk.Box result = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		result.Append (switcher);
		result.Append (source_mode_stack);
		return result;
	}

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
			save_analysis (ReadOptions ());
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
		source_rectangles = [.. analysis.Items.Select (item => new RectangleI (
			item.Bbox.X, item.Bbox.Y, item.Bbox.Width, item.Bbox.Height))];
		align_character.Active = false;
		RebuildFrames (analysis.Items.Count);
		ApplyAnalysisPlacement (analysis.Items);
		Refresh ();
	}

	private bool TryRestoreAnalysis (SpritesheetSplitData? split)
	{
		if (split?.SourceRectangles is not { Count: > 0 } rectangles
			|| rectangles.Count > max_frames
			|| split.Frames is not { } savedFrames
			|| savedFrames.Count != rectangles.Count
			|| split.CanvasWidth is < 1 or > 16384
			|| split.CanvasHeight is < 1 or > 16384
			|| rectangles.Count * (long) split.CanvasWidth * split.CanvasHeight > max_output_pixels
			|| rectangles.Any (IsInvalidSourceRectangle))
			return false;

		source_mode_stack.VisibleChildName = ai_source_mode;
		source_rectangles = [.. rectangles];
		canvas_width.Value = split.CanvasWidth;
		canvas_height.Value = split.CanvasHeight;
		align_character.Active = split.AlignCharacter;
		frames.AddRange (savedFrames.Select (frame => new EditableFrame {
			X = frame.X,
			Y = frame.Y,
			AnchorX = split.CanvasWidth / 2.0 - frame.X,
			AnchorY = split.CanvasHeight - frame.Y,
			Visible = frame.Visible,
		}));
		RebuildFrames (rectangles.Count);
		return true;
	}

	private bool IsInvalidSourceRectangle (RectangleI rectangle)
		=> rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0
			|| (long) rectangle.X + rectangle.Width > source.Surface.Width
			|| (long) rectangle.Y + rectangle.Height > source.Surface.Height;

	private void ChangeSourceMode ()
	{
		source_rectangles = null;
		ClearFrameAnchors ();
		align_character.Active = !IsAiSourceMode;
		if (IsAiSourceMode)
			RebuildFrames (0);
		else
			RebuildFrames ();
	}

	private void ApplyAnalysisPlacement (IReadOnlyList<AI.SpriteSegmentationItem> items)
	{
		for (int index = 0; index < items.Count; index++) {
			AI.SpriteSegmentationItem item = items[index];
			frames[index].AnchorX = item.FootAnchor.X - item.Bbox.X;
			frames[index].AnchorY = item.FootAnchor.Y - item.Bbox.Y;
		}
		RepositionFramesAroundAnchor ();
		SelectFrame (0);
	}

	private void ClearFrameAnchors ()
	{
		foreach (EditableFrame frame in frames) {
			frame.AnchorX = null;
			frame.AnchorY = null;
		}
	}

	private void ResetAnalysisAndRefresh ()
	{
		if (!syncing) {
			source_rectangles = null;
			ClearFrameAnchors ();
			if (!IsAiSourceMode)
				ApplyGridPlacement ();
		}
		Refresh ();
	}

	private void ResetAnalysisAndRebuildFrames ()
	{
		if (syncing)
			return;
		source_rectangles = null;
		ClearFrameAnchors ();
		RebuildFrames ();
	}
}
