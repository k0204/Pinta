using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private PromptOptimizationControls? CreatePromptOptimizationControls (
		AiImageRequestMode mode,
		Gtk.TextBuffer promptBuffer,
		Gtk.ScrolledWindow promptScroll,
		Gtk.ComboBoxText serviceCombobox,
		Gtk.ComboBoxText providerCombobox,
		IReadOnlyList<AI.AiProviderInfo> imageProviders,
		UserLayer? sourceLayer,
		IReadOnlyList<(Gtk.CheckButton Button, UserLayer Layer)> layerChoices,
		IReadOnlyList<Gio.File> files)
	{
		if (mode == AiImageRequestMode.BackgroundCleanup)
			return null;

		return new PromptOptimizationControls (
			promptBuffer,
			promptScroll,
			() => GetPromptOptimizationProvider (serviceCombobox, providerCombobox, imageProviders),
			() => CreatePromptOptimizationReferences (sourceLayer, layerChoices, files),
			prompt_optimization);
	}

	private static IReadOnlyList<byte[]> CreatePromptOptimizationReferences (
		UserLayer? sourceLayer,
		IReadOnlyList<(Gtk.CheckButton Button, UserLayer Layer)> layerChoices,
		IReadOnlyList<Gio.File> files)
	{
		List<UserLayer> layers = [];
		if (sourceLayer is not null)
			layers.Add (sourceLayer);
		layers.AddRange (layerChoices
			.Where (choice => choice.Button.Active && choice.Layer != sourceLayer)
			.Select (choice => choice.Layer));

		List<byte[]> references = [];
		foreach (UserLayer layer in layers.OrderByDescending (IsCharacterAnchor))
			references.Add (CreateLayerPng (layer));
		foreach (Gio.File file in files)
			references.Add (LoadReferenceImage (file).Png);
		return references;
	}

	private static Gtk.Box CreatePromptSection (Gtk.ScrolledWindow promptScroll)
	{
		Gtk.Box section = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		section.Append (CreateDialogLabel (Translations.GetString ("提示词")));
		section.Append (promptScroll);
		return section;
	}

	private static string GetPromptOptimizationProvider (
		Gtk.ComboBoxText serviceCombobox,
		Gtk.ComboBoxText providerCombobox,
		IReadOnlyList<AI.AiProviderInfo> imageProviders)
	{
		string requested = serviceCombobox.Active == 0
			? AI.AiRequestSettings.AgnesService
			: providerCombobox.Active >= 0 && providerCombobox.Active < imageProviders.Count
				? imageProviders[providerCombobox.Active].Id
				: AI.AiRequestSettings.GetGptProvider (PintaCore.Settings);

		foreach (AI.AiProviderInfo provider in PintaCore.AiProviders.ChatProviders)
			if (provider.Id == requested)
				return provider.Id;

		foreach (AI.AiProviderInfo provider in PintaCore.AiProviders.ChatProviders)
			return provider.Id;

		throw new InvalidOperationException ("No chat provider is available for prompt optimization.");
	}

	private static string FormatPromptHistoryLabel (AI.AiPromptHistoryItem item)
	{
		string prompt = item.ChinesePrompt.ReplaceLineEndings (" ");
		return prompt.Length > 72 ? $"{prompt[..72]}..." : prompt;
	}

	private static void SavePromptHistory (AI.AiPromptHistoryItem? history)
	{
		if (history is not AI.AiPromptHistoryItem item)
			return;

		AI.AiPromptHistory.Add (PintaCore.Settings, item.ChinesePrompt, item.EnglishPrompt);
		PintaCore.Settings.DoSaveSettingsBeforeQuit ();
	}

	private sealed class PromptOptimizationControls
	{
		private readonly Gtk.TextBuffer originalBuffer;
		private readonly Gtk.TextBuffer englishBuffer;
		private readonly Gtk.Button optimizeButton;
		private readonly Gtk.Label statusLabel;
		private readonly Func<string> getProvider;
		private readonly Func<IReadOnlyList<byte[]>> getReferenceImages;
		private readonly AI.AiPromptOptimizationService service;
		private bool updatingPrompt;
		private bool englishPromptIsCurrent;

		public PromptOptimizationControls (
			Gtk.TextBuffer originalBuffer,
			Gtk.ScrolledWindow promptScroll,
			Func<string> getProvider,
			Func<IReadOnlyList<byte[]>> getReferenceImages,
			AI.AiPromptOptimizationService service)
		{
			this.originalBuffer = originalBuffer;
			this.getProvider = getProvider;
			this.getReferenceImages = getReferenceImages;
			this.service = service;

			Gtk.TextView englishView = Gtk.TextView.New ();
			englishView.Editable = false;
			englishView.WrapMode = Gtk.WrapMode.WordChar;
			englishView.SetSizeRequest (-1, 90);
			englishBuffer = englishView.Buffer!;

			Gtk.ScrolledWindow englishScroll = Gtk.ScrolledWindow.New ();
			englishScroll.HeightRequest = 90;
			englishScroll.SetChild (englishView);

			optimizeButton = Gtk.Button.NewWithLabel (Translations.GetString ("AI 优化并翻译"));
			optimizeButton.TooltipText = Translations.GetString ("将提示词优化并翻译为英文");
			optimizeButton.OnClicked += async (_, _) => await OptimizeAsync ();

			Gtk.Label promptLabel = CreateDialogLabel (Translations.GetString ("提示词"));
			promptLabel.Hexpand = true;
			Gtk.Box promptHeader = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
			promptHeader.Append (promptLabel);
			promptHeader.Append (optimizeButton);

			statusLabel = Gtk.Label.New (string.Empty);
			statusLabel.Halign = Gtk.Align.Start;
			statusLabel.Wrap = true;
			statusLabel.AddCssClass (AdwaitaStyles.DimLabel);

			Gtk.Box section = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
			IReadOnlyList<AI.AiPromptHistoryItem> history = AI.AiPromptHistory.Load (PintaCore.Settings);
			Gtk.Box? historyRow = CreatePromptHistoryRow (history);
			if (historyRow is not null)
				section.Append (historyRow);
			section.Append (promptHeader);
			section.Append (promptScroll);
			section.Append (CreateDialogLabel (Translations.GetString ("英文提示词（发送给绘图 AI）")));
			section.Append (englishScroll);
			section.Append (statusLabel);
			Section = section;

			originalBuffer.OnChanged += (_, _) => MarkPromptChanged ();
			optimizeButton.Sensitive = PintaCore.AiProviders.ChatProviders.Count > 0;
		}

		public Gtk.Box Section { get; }

		private Gtk.Box? CreatePromptHistoryRow (IReadOnlyList<AI.AiPromptHistoryItem> history)
		{
			if (history.Count == 0)
				return null;

			Gtk.ComboBoxText historyCombo = Gtk.ComboBoxText.New ();
			historyCombo.AppendText (Translations.GetString ("选择提示词历史记录"));
			foreach (AI.AiPromptHistoryItem item in history)
				historyCombo.AppendText (FormatPromptHistoryLabel (item));
			historyCombo.Active = 0;
			historyCombo.Hexpand = true;
			historyCombo.OnChanged += (_, _) => RestoreHistory (historyCombo, history);

			Gtk.Label historyLabel = CreateDialogLabel (Translations.GetString ("提示词历史记录"));
			historyLabel.Hexpand = true;
			Gtk.Box historyRow = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
			historyRow.Append (historyLabel);
			historyRow.Append (historyCombo);
			return historyRow;
		}

		private void RestoreHistory (
			Gtk.ComboBoxText historyCombo,
			IReadOnlyList<AI.AiPromptHistoryItem> history)
		{
			if (historyCombo.Active <= 0 || historyCombo.Active > history.Count)
				return;

			AI.AiPromptHistoryItem item = history[historyCombo.Active - 1];
			updatingPrompt = true;
			try {
				originalBuffer.SetText (item.ChinesePrompt, -1);
				englishBuffer.SetText (item.EnglishPrompt, -1);
				englishPromptIsCurrent = !string.IsNullOrWhiteSpace (item.EnglishPrompt);
			} finally {
				updatingPrompt = false;
			}
			historyCombo.Active = 0;
			SetStatus (Translations.GetString ("已恢复提示词历史记录。"), error: false);
		}

		public string GetPrompt (string originalPrompt)
		{
			string englishPrompt = ReadText (englishBuffer);
			return englishPromptIsCurrent && !string.IsNullOrWhiteSpace (englishPrompt)
				? englishPrompt
				: originalPrompt;
		}

		public AI.AiPromptHistoryItem? GetPromptHistory ()
		{
			string chinesePrompt = ReadText (originalBuffer);
			if (string.IsNullOrWhiteSpace (chinesePrompt))
				return null;

			return new (chinesePrompt, englishPromptIsCurrent ? ReadText (englishBuffer) : string.Empty);
		}

		private async System.Threading.Tasks.Task OptimizeAsync ()
		{
			string originalPrompt = ReadText (originalBuffer);
			if (string.IsNullOrWhiteSpace (originalPrompt)) {
				SetStatus (Translations.GetString ("请先输入提示词。"), error: true);
				return;
			}

			optimizeButton.Sensitive = false;
			SetStatus (Translations.GetString ("正在优化并翻译提示词..."), error: false);
			try {
				AI.AiPromptOptimizationResult result = await service.OptimizeAndTranslateAsync (
					originalPrompt,
					getProvider (),
					getReferenceImages ());
				if (!string.Equals (originalPrompt, ReadText (originalBuffer), StringComparison.Ordinal)) {
					SetStatus (Translations.GetString ("提示词已修改，未应用旧的优化结果。"), error: true);
					return;
				}

				if (string.IsNullOrWhiteSpace (result.ChinesePrompt)
					|| string.IsNullOrWhiteSpace (result.EnglishPrompt)) {
					SetStatus (Translations.GetString ("未返回完整的中英文优化提示词，将使用原提示词。"), error: false);
					return;
				}

				updatingPrompt = true;
				try {
					originalBuffer.SetText (result.ChinesePrompt, -1);
					englishBuffer.SetText (result.EnglishPrompt, -1);
					englishPromptIsCurrent = true;
				} finally {
					updatingPrompt = false;
				}
				SetStatus (
					Translations.GetString ("中英文优化提示词已生成，英文将优先发送给绘图 AI。"),
					error: false);
			} catch (Exception ex) {
				Console.Error.WriteLine ($"Pinta: prompt optimization failed: {ex}");
				SetStatus (Translations.GetString ("提示词优化失败，将使用原提示词。"), error: true);
			} finally {
				optimizeButton.Sensitive = true;
			}
		}

		private void MarkPromptChanged ()
		{
			if (updatingPrompt)
				return;

			englishPromptIsCurrent = false;
			statusLabel.RemoveCssClass (AdwaitaStyles.Error);
			statusLabel.SetText (Translations.GetString ("原文已修改，请点击优化并翻译更新中英文提示词。"));
		}

		private void SetStatus (string text, bool error)
		{
			statusLabel.SetText (text);
			statusLabel.RemoveCssClass (AdwaitaStyles.Error);
			if (error)
				statusLabel.AddCssClass (AdwaitaStyles.Error);
		}

		private static string ReadText (Gtk.TextBuffer buffer)
		{
			buffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
			return buffer.GetText (start, end, includeHiddenChars: true).Trim ();
		}
	}
}
