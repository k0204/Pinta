//
// LayerActions.Background.cs
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersGenerateImageActivated (object sender, EventArgs e)
	{
		if (cutout_running || !EnsureAiLoggedIn ())
			return;

		await RunImageGenerationAsync ();
	}

	private async void HandlePintaCoreActionsLayersCutoutActivated (object sender, EventArgs e)
	{
		if (cutout_running)
			return;

		AiImageOperation? operation = await PromptAiImageOperationAsync ();
		if (operation is null || !EnsureAiLoggedIn ())
			return;

		if (operation == AiImageOperation.GenerateWhite)
			await RunBackgroundCleanupAsync ();
		else
			await RunCutoutAsync ();
	}

	private async Task RunImageGenerationAsync ()
	{
		Document? referenceDocument = workspace.ActiveDocumentOrDefault;
		AiImageRequestOptions? options = await PromptAiImageRequestAsync (
			AiImageRequestMode.ImageGeneration,
			referenceDocument,
			sourceLayer: null);
		if (options is null)
			return;

		await GenerateImageAsync (referenceDocument, options);
	}

	private async Task GenerateImageAsync (Document? referenceDocument, AiImageRequestOptions options)
	{

		cutout_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		using CancellationTokenSource cts = new ();
		IProgressDialog progress = chrome.ProgressDialog;
		progress.Title = options.ProgressTitle;
		progress.Text = Translations.GetString ("Preparing image request...");
		progress.Progress = 0.05;
		progress.Canceled += HandleProgressCanceled;
		progress.Show ();
		chrome.MainWindowBusy = true;
		bool clearStatus = true;

		try {
			List<(byte[] Png, string FileName)> references = [];
			if (referenceDocument is not null)
				foreach (UserLayer layer in options.Layers.OrderByDescending (IsCharacterAnchor))
					references.Add ((CreateLayerPng (referenceDocument, layer), GetAiReferenceFileName (layer, references.Count + 1)));
			foreach (Gio.File file in options.Files)
				references.Add (LoadReferenceImage (file));

			string debugDir = CreateCutoutDebugDirectory ();
			byte[] generatedPng = await GenerateBackgroundWithRetryAsync (
				options.ProgressTitle,
				() => background_cutout.GenerateImageAsync (
					options.ImageSize,
					options.Prompt,
					references,
					SetProgress,
					(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token),
				message => SaveCutoutDebugLog (debugDir, message),
				cts.Token);

			SetProgress (Translations.GetString ("Opening generated image..."), 0.85);
			using Cairo.ImageSurface generated = LoadPngAsSurface (generatedPng, options.ImageSize);
			if (options.Spritesheet is null) {
				Document generatedDocument = workspace.NewDocumentFromImage (generated);
				generatedDocument.Layers.CurrentUserLayer.Name = options.ResultLayerName;
			} else {
				InsertSpritesheetAttempt (referenceDocument!, generatedPng, options.Spritesheet);
			}
			SetProgress (Translations.GetString ("Refreshing balance..."), 0.95);
			await PintaCore.AiAuth.RefreshAccountSummaryAsync (cts.Token);
			PintaCore.Settings.DoSaveSettingsBeforeQuit ();
			SetProgress (Translations.GetString ("Image generation complete."), 1.0);
		} catch (OperationCanceledException) {
			clearStatus = false;
			chrome.SetStatusBarText (Translations.GetString ("Image generation canceled."));
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Image Generation Failed"),
				Translations.GetString ("Check the selected images, API server logs, balance, and login status, then try again."),
				ex.ToString ());
		} finally {
			progress.Canceled -= HandleProgressCanceled;
			progress.Hide ();
			chrome.MainWindowBusy = false;
			cutout_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
			if (clearStatus)
				chrome.SetStatusBarText (string.Empty);
		}

		void HandleProgressCanceled (object? sender, EventArgs args)
			=> cts.Cancel ();

		void SetProgress (string text, double value)
		{
			progress.Text = text;
			progress.Progress = Math.Clamp (value, 0.0, 1.0);
			chrome.SetStatusBarText (text);
		}
	}

	private async Task RunBackgroundCleanupAsync ()
	{

		Document doc = workspace.ActiveDocument;
		UserLayer sourceLayer = doc.Layers.CurrentUserLayer;
		if (!sourceLayer.IsEditable)
			return;

		AiImageRequestOptions? options = await PromptAiImageRequestAsync (
			AiImageRequestMode.BackgroundCleanup,
			doc,
			sourceLayer);
		if (options is null)
			return;

		tools.Commit ();
		cutout_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		using CancellationTokenSource cts = new ();
		IProgressDialog progress = chrome.ProgressDialog;
		progress.Title = Translations.GetString ("清理背景");
		progress.Text = Translations.GetString ("Preparing image...");
		progress.Progress = 0.05;
		progress.Canceled += HandleProgressCanceled;
		progress.Show ();
		chrome.MainWindowBusy = true;
		SetProgress (Translations.GetString ("Preparing image..."), 0.05);
		bool clearStatus = true;

		try {
			byte[] sourcePng = CreateLayerPng (doc, sourceLayer);
			string debugDir = CreateCutoutDebugDirectory ();
			List<(byte[] Png, string FileName)> references = [];
			foreach (UserLayer layer in options.Layers)
				references.Add ((CreateLayerPng (doc, layer), $"layer-{references.Count + 1}.png"));
			foreach (Gio.File file in options.Files)
				references.Add (LoadReferenceImage (file));

			SaveCutoutDebugLog (
				debugDir,
				$"AI background cleanup client: document_size={doc.ImageSize.Width}x{doc.ImageSize.Height}, "
				+ $"source_layer={sourceLayer.Name}, source_bytes={sourcePng.Length}, references={references.Count}");
			SaveCutoutDebugPng (debugDir, "source.png", sourcePng);
			byte[] whitePng = await GenerateBackgroundWithRetryAsync (
				Translations.GetString ("清理背景"),
				() => background_cutout.GenerateWhiteAsync (
					sourcePng,
					doc.ImageSize,
					options.Prompt,
					references,
					SetProgress,
					(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token),
				message => SaveCutoutDebugLog (debugDir, message),
				cts.Token);

			SetProgress (Translations.GetString ("Creating white background layer..."), 0.85);
			UserLayer whiteLayer = doc.Layers.AddNewLayer (Translations.GetString ("White Background"));
			DrawPngOnLayer (whitePng, whiteLayer);
			doc.History.PushNewItem (new AddLayerHistoryItem (
				Resources.Icons.ColorModeColor,
				Translations.GetString ("清理背景"),
				whiteLayer,
				doc.Layers.GetPosition (whiteLayer)));
			doc.Workspace.Invalidate ();

			SetProgress (Translations.GetString ("Refreshing balance..."), 0.95);
			await PintaCore.AiAuth.RefreshAccountSummaryAsync (cts.Token);
			PintaCore.Settings.DoSaveSettingsBeforeQuit ();
			SetProgress (Translations.GetString ("Background cleanup complete."), 1.0);
		} catch (OperationCanceledException) {
			clearStatus = false;
			chrome.SetStatusBarText (Translations.GetString ("Background cleanup canceled."));
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Background Cleanup Failed"),
				Translations.GetString ("Check the selected images, API server logs, balance, and login status, then try again."),
				ex.ToString ());
		} finally {
			progress.Canceled -= HandleProgressCanceled;
			progress.Hide ();
			chrome.MainWindowBusy = false;
			cutout_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
			if (clearStatus)
				chrome.SetStatusBarText (string.Empty);
		}

		void HandleProgressCanceled (object? sender, EventArgs args)
			=> cts.Cancel ();

		void SetProgress (string text, double value)
		{
			progress.Text = text;
			progress.Progress = Math.Clamp (value, 0.0, 1.0);
			chrome.SetStatusBarText (text);
		}
	}

	private async Task RunCutoutAsync ()
	{
		Document doc = workspace.ActiveDocument;
		UserLayer sourceLayer = doc.Layers.CurrentUserLayer;
		if (!sourceLayer.IsEditable)
			return;

		tools.Commit ();
		cutout_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		using CancellationTokenSource cts = new ();
		IProgressDialog progress = chrome.ProgressDialog;
		progress.Title = Translations.GetString ("Cutout");
		progress.Text = Translations.GetString ("Preparing image...");
		progress.Progress = 0.05;
		progress.Canceled += HandleProgressCanceled;
		progress.Show ();
		chrome.MainWindowBusy = true;
		SetProgress (Translations.GetString ("Preparing image..."), 0.05);
		bool clearStatus = true;

		try {
			Size operationSize = IsSpritesheetFrame (sourceLayer)
				? new Size (sourceLayer.Surface.Width, sourceLayer.Surface.Height)
				: doc.ImageSize;
			byte[] sourcePng = IsSpritesheetFrame (sourceLayer)
				? CreateSurfacePng (sourceLayer.Surface)
				: CreateLayerPng (doc, sourceLayer);
			string cutoutName = GetCutoutResultName (sourceLayer);
			string imageService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
			string debugDir = CreateCutoutDebugDirectory ();
			SaveCutoutDebugLog (
				debugDir,
				$"AI cutout: service={imageService}, document_size={operationSize.Width}x{operationSize.Height}, "
				+ $"source_layer={sourceLayer.Name}, source_bytes={sourcePng.Length}");
			SaveCutoutDebugPng (debugDir, "source.png", sourcePng);
			if (imageService == AI.AiRequestSettings.BaiduService) {
				SetProgress (Translations.GetString ("Requesting Baidu human segmentation..."), 0.25);
				byte[] transparentPng = await GenerateBackgroundWithRetryAsync (
					Translations.GetString ("Cutout"),
					() => background_cutout.GenerateBaiduCutoutAsync (
						sourcePng,
						operationSize,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
						message => SaveCutoutDebugLog (debugDir, message),
						cts.Token),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token);

				SetProgress (Translations.GetString ("Creating transparent layer..."), 0.85);
				UserLayer cutoutLayer = AddAiResultLayer (doc, cutoutName, operationSize);
				DrawPngOnLayer (transparentPng, cutoutLayer);
				doc.History.PushNewItem (new AddLayerHistoryItem (
					Resources.Icons.ColorModeTransparency,
					cutoutName,
					cutoutLayer,
					doc.Layers.GetPosition (cutoutLayer)));
			} else {
				byte[] blackPng = await GenerateBackgroundWithRetryAsync (
					Translations.GetString ("Cutout"),
					() => background_cutout.GenerateBlackAsync (
						sourcePng,
						operationSize,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
						message => SaveCutoutDebugLog (debugDir, message),
						cts.Token),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token);

				SetProgress (Translations.GetString ("Creating black and transparent layers..."), 0.85);
				CompoundHistoryItem history = new (Resources.Icons.ColorModeTransparency, Translations.GetString ("Cutout"));
				UserLayer blackLayer = AddAiResultLayer (doc, IsSpritesheetFrame (sourceLayer) ? $"{cutoutName}-black-source" : Translations.GetString ("Black Background"), operationSize);
				DrawPngOnLayer (blackPng, blackLayer);
				history.Push (new AddLayerHistoryItem (
					Resources.Icons.ColorModeColor,
					Translations.GetString ("Black Background"),
					blackLayer,
					doc.Layers.GetPosition (blackLayer)));

				using Cairo.ImageSurface white = LoadPngAsSurface (sourcePng, operationSize);
				using Cairo.ImageSurface black = LoadPngAsSurface (blackPng, operationSize);
				UserLayer cutoutLayer = AddAiResultLayer (doc, cutoutName, operationSize);
				CreateTransparentCutout (white, black, cutoutLayer.Surface);
				history.Push (new AddLayerHistoryItem (
					Resources.Icons.ColorModeTransparency,
					cutoutName,
					cutoutLayer,
					doc.Layers.GetPosition (cutoutLayer)));
				doc.History.PushNewItem (history);
			}

			doc.Workspace.Invalidate ();
			SetProgress (Translations.GetString ("Cutout complete."), 1.0);
		} catch (OperationCanceledException) {
			clearStatus = false;
			chrome.SetStatusBarText (Translations.GetString ("Cutout canceled."));
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Cutout Failed"),
				Translations.GetString ("Check the selected API, server logs, balance, and image, then try again."),
				ex.ToString ());
		} finally {
			progress.Canceled -= HandleProgressCanceled;
			progress.Hide ();
			chrome.MainWindowBusy = false;
			cutout_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
			if (clearStatus)
				chrome.SetStatusBarText (string.Empty);
		}

		void HandleProgressCanceled (object? sender, EventArgs args)
			=> cts.Cancel ();

		void SetProgress (string text, double value)
		{
			progress.Text = text;
			progress.Progress = Math.Clamp (value, 0.0, 1.0);
			chrome.SetStatusBarText (text);
		}
	}

	private async Task<byte[]> GenerateBackgroundWithRetryAsync (
		string operation,
		Func<Task<byte[]>> generate,
		Action<string> log,
		CancellationToken cancellationToken)
	{
		for (int attempt = 1; ; attempt++) {
			try {
				return await generate ();
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				log ($"AI background request failed: operation={operation}, attempt={attempt}, error={ex}");
				if (!await ConfirmBackgroundRetryAsync (operation, ex))
					throw new OperationCanceledException (cancellationToken);

				log ($"AI background retry confirmed: operation={operation}, next_attempt={attempt + 1}, error={ex.Message}");
			}
		}
	}

	private async Task<bool> ConfirmBackgroundRetryAsync (string operation, Exception ex)
	{
		Console.Error.WriteLine ("Pinta: {0} request failed\n{1}", operation, ex);

		using Adw.MessageDialog confirmation = Adw.MessageDialog.New (
			chrome.MainWindow,
			operation,
			$"{Translations.GetString ("The image request failed. Try the request again?")}\n\n{ex.Message}");
		const string cancel_response = "cancel";
		const string retry_response = "retry";
		confirmation.AddResponse (cancel_response, Translations.GetString ("_Cancel"));
		confirmation.AddResponse (retry_response, Translations.GetString ("_Retry"));
		confirmation.SetResponseAppearance (retry_response, Adw.ResponseAppearance.Suggested);
		confirmation.DefaultResponse = retry_response;
		confirmation.CloseResponse = cancel_response;
		return await confirmation.RunAsync () == retry_response;
	}

	private async Task<AiImageOperation?> PromptAiImageOperationAsync ()
	{
		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("AI Request Settings");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = 420;

		Gtk.ComboBoxText serviceCombobox = Gtk.ComboBoxText.New ();
		serviceCombobox.AppendText (Translations.GetString ("Agnes"));
		serviceCombobox.AppendText (Translations.GetString ("GPT Image"));
		serviceCombobox.AppendText (Translations.GetString ("Baidu"));
		serviceCombobox.Hexpand = true;

		Gtk.ComboBoxText providerCombobox = Gtk.ComboBoxText.New ();
		providerCombobox.AppendText (AI.AiRequestSettings.ZzswitchProvider);
		providerCombobox.AppendText (AI.AiRequestSettings.LukyfaceProvider);
		providerCombobox.Hexpand = true;
		Gtk.Label providerLabel = CreateSettingsLabel (Translations.GetString ("GPT provider:"));

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = 8;
		grid.ColumnSpacing = 8;
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Image service:")), 0, 0, 1, 1);
		grid.Attach (serviceCombobox, 1, 0, 1, 1);
		grid.Attach (providerLabel, 0, 1, 1, 1);
		grid.Attach (providerCombobox, 1, 1, 1, 1);

		Gtk.Widget whiteButton = dialog.AddButton (Translations.GetString ("生成白图"), (int) Gtk.ResponseType.Apply);
		Gtk.Widget cutoutButton = dialog.AddButton (Translations.GetString ("Cutout"), (int) Gtk.ResponseType.Ok);
		cutoutButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		string savedService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
		serviceCombobox.Active = savedService switch {
			AI.AiRequestSettings.AgnesService => 0,
			AI.AiRequestSettings.BaiduService => 2,
			_ => 1,
		};
		providerCombobox.Active = AI.AiRequestSettings.GetGptProvider (PintaCore.Settings) == AI.AiRequestSettings.ZzswitchProvider ? 0 : 1;

		void UpdateVisibility ()
		{
			bool gptSelected = serviceCombobox.Active == 1;
			providerLabel.Visible = gptSelected;
			providerCombobox.Visible = gptSelected;
			whiteButton.Visible = serviceCombobox.Active != 2;
		}
		serviceCombobox.OnChanged += (_, _) => UpdateVisibility ();
		UpdateVisibility ();

		Gtk.Box content = dialog.GetContentAreaBox ();
		content.Spacing = 8;
		content.SetAllMargins (12);
		content.Append (grid);

		Gtk.ResponseType response = await dialog.RunAsync ();
		dialog.Hide ();
		if (response is not Gtk.ResponseType.Apply and not Gtk.ResponseType.Ok)
			return null;

		string imageService = serviceCombobox.Active switch {
			0 => AI.AiRequestSettings.AgnesService,
			2 => AI.AiRequestSettings.BaiduService,
			_ => AI.AiRequestSettings.GptImageService,
		};
		string gptProvider = providerCombobox.Active == 0
			? AI.AiRequestSettings.ZzswitchProvider
			: AI.AiRequestSettings.LukyfaceProvider;
		AI.AiRequestSettings.Save (PintaCore.Settings, imageService, gptProvider);
		PintaCore.Settings.DoSaveSettingsBeforeQuit ();
		return response == Gtk.ResponseType.Apply
			? AiImageOperation.GenerateWhite
			: AiImageOperation.Cutout;
	}

	private static Gtk.Label CreateSettingsLabel (string text)
	{
		Gtk.Label label = Gtk.Label.New (text);
		label.Halign = Gtk.Align.End;
		return label;
	}

	private static bool EnsureAiLoggedIn ()
	{
		if (PintaCore.AiAuth.IsLoggedIn)
			return true;

		PintaCore.Actions.App.AiAccount.Activate ();
		return false;
	}

	private async Task<AiImageRequestOptions?> PromptAiImageRequestAsync (
		AiImageRequestMode mode,
		Document? doc,
		UserLayer? sourceLayer)
	{
		if (mode == AiImageRequestMode.BackgroundCleanup && (doc is null || sourceLayer is null))
			throw new ArgumentException ("Background cleanup requires a document and source layer.");
		bool spritesheetMode = mode == AiImageRequestMode.SpritesheetGeneration;
		AI.SpritesheetPromptCatalog? spritesheetCatalog = spritesheetMode
			? AI.SpritesheetPromptCatalog.Load ()
			: null;

		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = mode switch {
			AiImageRequestMode.BackgroundCleanup => Translations.GetString ("清理背景"),
			AiImageRequestMode.SpritesheetGeneration => Translations.GetString ("Generate Spritesheet"),
			_ => Translations.GetString ("AI 生成"),
		};
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = 520;
		dialog.DefaultHeight = spritesheetMode ? 820 : 620;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget submitButton = dialog.AddButton (
			mode == AiImageRequestMode.BackgroundCleanup
				? Translations.GetString ("清理背景")
				: Translations.GetString ("生成"),
			(int) Gtk.ResponseType.Ok);
		submitButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.TextView promptView = Gtk.TextView.New ();
		promptView.WrapMode = Gtk.WrapMode.WordChar;
		promptView.Vexpand = true;
		Gtk.TextBuffer promptBuffer = promptView.Buffer!;
		promptBuffer.SetText (
			mode == AiImageRequestMode.BackgroundCleanup
				? AI.BackgroundCutoutService.GetDefaultBackgroundCleanupPrompt ()
				: string.Empty,
			-1);
		Gtk.ScrolledWindow promptScroll = Gtk.ScrolledWindow.New ();
		promptScroll.HeightRequest = 110;
		promptScroll.SetChild (promptView);

		Gtk.ComboBoxText serviceCombobox = Gtk.ComboBoxText.New ();
		serviceCombobox.AppendText (Translations.GetString ("Agnes"));
		serviceCombobox.AppendText (Translations.GetString ("GPT Image"));
		serviceCombobox.Active = AI.AiRequestSettings.GetImageService (PintaCore.Settings) == AI.AiRequestSettings.AgnesService ? 0 : 1;
		Gtk.ComboBoxText providerCombobox = Gtk.ComboBoxText.New ();
		providerCombobox.AppendText (AI.AiRequestSettings.ZzswitchProvider);
		providerCombobox.AppendText (AI.AiRequestSettings.LukyfaceProvider);
		providerCombobox.Active = AI.AiRequestSettings.GetGptProvider (PintaCore.Settings) == AI.AiRequestSettings.ZzswitchProvider ? 0 : 1;
		Gtk.Label providerLabel = CreateSettingsLabel (Translations.GetString ("GPT provider:"));
		AiImageSizePicker sizePicker = new ();

		void UpdateGenerationSettings ()
		{
			bool gptSelected = serviceCombobox.Active == 1;
			providerLabel.Visible = gptSelected;
			providerCombobox.Visible = gptSelected;
			sizePicker.SetService (gptSelected
				? AI.AiRequestSettings.GptImageService
				: AI.AiRequestSettings.AgnesService);
		}

		Gtk.Grid generationGrid = Gtk.Grid.New ();
		generationGrid.RowSpacing = 8;
		generationGrid.ColumnSpacing = 8;
		generationGrid.Attach (CreateSettingsLabel (Translations.GetString ("Image service:")), 0, 0, 1, 1);
		generationGrid.Attach (serviceCombobox, 1, 0, 1, 1);
		generationGrid.Attach (providerLabel, 0, 1, 1, 1);
		generationGrid.Attach (providerCombobox, 1, 1, 1, 1);
		generationGrid.Attach (CreateSettingsLabel (Translations.GetString ("Image size:")), 0, 2, 1, 1);
		generationGrid.Attach (sizePicker.Widget, 1, 2, 1, 1);
		generationGrid.Visible = mode != AiImageRequestMode.BackgroundCleanup;
		serviceCombobox.OnChanged += (_, _) => UpdateGenerationSettings ();
		UpdateGenerationSettings ();

		Gtk.Widget? spritesheet_controls = null;
		Func<string>? spritesheet_result_layer_name = null;
		Func<AI.SpritesheetAttemptInfo?> spritesheet_info = () => null;
		Func<bool> spritesheet_valid = () => true;
		if (spritesheetCatalog is not null) {
			Gtk.CheckButton directionModeButton = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Direction Sheet"));
			Gtk.CheckButton actionModeButton = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Action Sequence"));
			actionModeButton.SetGroup (directionModeButton);
			directionModeButton.Active = true;

			Gtk.ComboBoxText actionCombobox = Gtk.ComboBoxText.New ();
			foreach (AI.SpritesheetActionPreset action in spritesheetCatalog.Actions)
				actionCombobox.AppendText (action.Label);
			actionCombobox.Active = 0;

			Gtk.Entry customActionEntry = Gtk.Entry.New ();
			customActionEntry.PlaceholderText = Translations.GetString ("Describe the custom action");
			customActionEntry.Hexpand = true;
			Gtk.Label customActionLabel = CreateSettingsLabel (Translations.GetString ("Custom action:"));

			Gtk.SpinButton frameCountSpinner = Gtk.SpinButton.NewWithRange (1, 16, 1);
			frameCountSpinner.Value = spritesheetCatalog.Actions[0].DefaultFrameCount;
			Gtk.Label frameCountLabel = CreateSettingsLabel (Translations.GetString ("Frames per direction:"));

			Gtk.ComboBoxText backgroundCombobox = Gtk.ComboBoxText.New ();
			foreach (AI.SpritesheetBackground background in spritesheetCatalog.Backgrounds)
				backgroundCombobox.AppendText (background.Label);
			backgroundCombobox.Active = 0;

			List<(Gtk.CheckButton Button, AI.SpritesheetDirection Direction)> directionChoices = [];
			Gtk.Grid directionsGrid = Gtk.Grid.New ();
			directionsGrid.RowSpacing = 4;
			directionsGrid.ColumnSpacing = 12;
			for (int index = 0; index < spritesheetCatalog.Directions.Count; index++) {
				AI.SpritesheetDirection direction = spritesheetCatalog.Directions[index];
				Gtk.CheckButton check = Gtk.CheckButton.NewWithLabel (direction.Label);
				check.Active = true;
				directionsGrid.Attach (check, index % 2, index / 2, 1, 1);
				directionChoices.Add ((check, direction));
			}

			Gtk.Label summaryLabel = Gtk.Label.New (string.Empty);
			summaryLabel.Halign = Gtk.Align.Start;
			summaryLabel.AddCssClass (AdwaitaStyles.DimLabel);

			Gtk.Grid spritesheetGrid = Gtk.Grid.New ();
			spritesheetGrid.RowSpacing = 8;
			spritesheetGrid.ColumnSpacing = 8;
			Gtk.Box modeBox = Gtk.Box.New (Gtk.Orientation.Horizontal, 12);
			modeBox.Append (directionModeButton);
			modeBox.Append (actionModeButton);
			spritesheetGrid.Attach (CreateSettingsLabel (Translations.GetString ("Generation type:")), 0, 0, 1, 1);
			spritesheetGrid.Attach (modeBox, 1, 0, 1, 1);
			Gtk.Label actionLabel = CreateSettingsLabel (Translations.GetString ("Action:"));
			spritesheetGrid.Attach (actionLabel, 0, 1, 1, 1);
			spritesheetGrid.Attach (actionCombobox, 1, 1, 1, 1);
			spritesheetGrid.Attach (customActionLabel, 0, 2, 1, 1);
			spritesheetGrid.Attach (customActionEntry, 1, 2, 1, 1);
			spritesheetGrid.Attach (frameCountLabel, 0, 3, 1, 1);
			spritesheetGrid.Attach (frameCountSpinner, 1, 3, 1, 1);
			spritesheetGrid.Attach (CreateSettingsLabel (Translations.GetString ("Background:")), 0, 4, 1, 1);
			spritesheetGrid.Attach (backgroundCombobox, 1, 4, 1, 1);
			spritesheetGrid.Attach (CreateSettingsLabel (Translations.GetString ("Directions:")), 0, 5, 1, 1);
			spritesheetGrid.Attach (directionsGrid, 1, 5, 1, 1);
			spritesheetGrid.Attach (summaryLabel, 1, 6, 1, 1);
			spritesheet_controls = spritesheetGrid;

			bool IsCustomAction ()
				=> spritesheetCatalog.Actions[actionCombobox.Active].Id == "custom";

			void SelectDefaultDirections (bool directionSheet)
			{
				foreach ((Gtk.CheckButton check, AI.SpritesheetDirection direction) in directionChoices)
					check.Active = directionSheet || direction.Id is "down" or "left" or "up" or "right";
			}

			void RebuildSpritesheetPrompt ()
			{
				bool directionSheet = directionModeButton.Active;
				actionLabel.Visible = !directionSheet;
				actionCombobox.Visible = !directionSheet;
				frameCountLabel.Visible = !directionSheet;
				frameCountSpinner.Visible = !directionSheet;
				bool customVisible = !directionSheet && IsCustomAction ();
				customActionLabel.Visible = customVisible;
				customActionEntry.Visible = customVisible;

				string[] selectedIds = directionChoices
					.Where (choice => choice.Button.Active)
					.Select (choice => choice.Direction.Id)
					.ToArray ();
				int framesPerDirection = directionSheet ? 1 : (int) frameCountSpinner.Value;
				int totalFrames = selectedIds.Length * framesPerDirection;
				if (selectedIds.Length == 0 || sizePicker.SelectedSize is not Size size) {
					promptBuffer.SetText (string.Empty, -1);
					summaryLabel.SetText (selectedIds.Length == 0
						? Translations.GetString ("Select at least one direction.")
						: Translations.GetString ("Select a valid image size."));
					return;
				}

				(int columns, int rows) = AI.SpritesheetPromptCatalog.CalculateGrid (totalFrames, size);
				summaryLabel.SetText (directionSheet
					? $"{selectedIds.Length} directions / {columns} x {rows} grid"
					: $"{selectedIds.Length} directions x {framesPerDirection} frames = {totalFrames} frames / {columns} x {rows} grid");
				string actionId = spritesheetCatalog.Actions[actionCombobox.Active].Id;
				string backgroundId = spritesheetCatalog.Backgrounds[backgroundCombobox.Active].Id;
				promptBuffer.SetText (spritesheetCatalog.BuildPrompt (
					directionSheet,
					actionId,
					customActionEntry.GetText (),
					selectedIds,
					framesPerDirection,
					backgroundId,
					size), -1);
			}

			spritesheet_valid = () => directionChoices.Any (choice => choice.Button.Active)
				&& (directionModeButton.Active || !IsCustomAction () || !string.IsNullOrWhiteSpace (customActionEntry.GetText ()));
			spritesheet_result_layer_name = () => directionModeButton.Active
				? Translations.GetString ("Direction Sheet")
				: $"{spritesheetCatalog.Actions[actionCombobox.Active].Label} {Translations.GetString ("Spritesheet")}";
			spritesheet_info = () => CreateSpritesheetAttemptInfo (
				spritesheetCatalog, directionChoices, directionModeButton.Active, actionCombobox.Active,
				frameCountSpinner.Value, backgroundCombobox.Active, sizePicker.SelectedSize!.Value, promptBuffer);

			directionModeButton.OnToggled += (_, _) => {
				if (!directionModeButton.Active)
					return;
				SelectDefaultDirections (directionSheet: true);
				RebuildSpritesheetPrompt ();
			};
			actionModeButton.OnToggled += (_, _) => {
				if (!actionModeButton.Active)
					return;
				SelectDefaultDirections (directionSheet: false);
				RebuildSpritesheetPrompt ();
			};
			actionCombobox.OnChanged += (_, _) => {
				frameCountSpinner.Value = spritesheetCatalog.Actions[actionCombobox.Active].DefaultFrameCount;
				RebuildSpritesheetPrompt ();
			};
			customActionEntry.OnChanged += (_, _) => RebuildSpritesheetPrompt ();
			frameCountSpinner.OnValueChanged += (_, _) => RebuildSpritesheetPrompt ();
			backgroundCombobox.OnChanged += (_, _) => RebuildSpritesheetPrompt ();
			sizePicker.Changed += (_, _) => RebuildSpritesheetPrompt ();
			foreach ((Gtk.CheckButton check, _) in directionChoices)
				check.OnToggled += (_, _) => RebuildSpritesheetPrompt ();
			RebuildSpritesheetPrompt ();
		}

		Gtk.Picture sourcePreview = Gtk.Picture.New ();
		sourcePreview.ContentFit = Gtk.ContentFit.ScaleDown;
		sourcePreview.SetSizeRequest (160, 100);
		Gtk.Label sourceLabel = Gtk.Label.New (string.Empty);
		sourceLabel.Halign = Gtk.Align.Start;
		if (sourceLayer is not null) {
			sourcePreview.Paintable = sourceLayer.Surface.ToTexture ();
			sourceLabel.SetText ($"{GetLayerPath (sourceLayer)}  {sourceLayer.Surface.Width} × {sourceLayer.Surface.Height} px");
		}

		Gtk.Box layerBox = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
		List<(Gtk.CheckButton Button, UserLayer Layer)> layerChoices = [];
		if (doc is not null)
			foreach (UserLayer layer in doc.Layers.AllLayers) {
				if (layer == sourceLayer || layer is GroupLayer || layer.ReferenceMissing)
					continue;

				Gtk.CheckButton check = Gtk.CheckButton.New ();
				check.Active = false;
				Gtk.Picture preview = Gtk.Picture.New ();
				preview.Paintable = layer.Surface.ToTexture ();
				preview.ContentFit = Gtk.ContentFit.ScaleDown;
				preview.SetSizeRequest (80, 56);

				Gtk.Label nameLabel = Gtk.Label.New (GetLayerPath (layer));
				nameLabel.Halign = Gtk.Align.Start;
				nameLabel.Ellipsize = Pango.EllipsizeMode.End;
				Gtk.Label sizeLabel = Gtk.Label.New ($"{layer.Surface.Width} × {layer.Surface.Height} px");
				sizeLabel.Halign = Gtk.Align.Start;
				sizeLabel.AddCssClass (AdwaitaStyles.DimLabel);

				Gtk.Box details = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
				details.Hexpand = true;
				details.Append (nameLabel);
				details.Append (sizeLabel);
				Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
				row.Append (check);
				row.Append (preview);
				row.Append (details);
				layerBox.Append (row);
				layerChoices.Add ((check, layer));
			}
		if (spritesheetMode)
			SelectDefaultCharacterAnchor (layerChoices);

		Gtk.ScrolledWindow layerScroll = Gtk.ScrolledWindow.New ();
		layerScroll.HeightRequest = 180;
		layerScroll.SetChild (layerBox);

		List<Gio.File> files = [];
		Gtk.Label fileLabel = Gtk.Label.New (Translations.GetString ("No files selected"));
		fileLabel.Halign = Gtk.Align.Start;
		fileLabel.Wrap = true;
		Gtk.Button chooseFilesButton = Gtk.Button.NewWithLabel (Translations.GetString ("选择图片文件..."));
		chooseFilesButton.Halign = Gtk.Align.Start;
		chooseFilesButton.OnClicked += async (_, _) => {
			using Gtk.FileFilter imagesFilter = CreateImagesFileFilter ();
			using Gio.ListStore fileFilters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
			fileFilters.Append (imagesFilter);
			using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
			fileDialog.SetTitle (Translations.GetString ("选择参考图片"));
			fileDialog.SetFilters (fileFilters);
			if (recent_files.GetDialogDirectory () is Gio.File dir && dir.QueryExists (null))
				fileDialog.SetInitialFolder (dir);

			IReadOnlyList<Gio.File>? choices = await fileDialog.OpenFilesAsync (dialog);
			if (choices is null)
				return;

			files.Clear ();
			files.AddRange (choices);
			fileLabel.SetText (files.Count == 0
				? Translations.GetString ("No files selected")
				: string.Join (", ", files.ConvertAll (file => file.GetDisplayName ())));
			Gio.File? directory = files.Count > 0 ? files[0].GetParent () : null;
			if (directory is not null)
				recent_files.LastDialogDirectory = directory;
		};

		Gtk.Box content = dialog.GetContentAreaBox ();
		content.Spacing = 8;
		content.SetAllMargins (12);
		content.Append (generationGrid);
		if (spritesheet_controls is not null)
			content.Append (spritesheet_controls);
		if (sourceLayer is not null) {
			content.Append (CreateDialogLabel (Translations.GetString ("当前图层")));
			content.Append (sourcePreview);
			content.Append (sourceLabel);
		}
		content.Append (CreateDialogLabel (Translations.GetString ("提示词")));
		content.Append (promptScroll);
		if (doc is not null) {
			content.Append (CreateDialogLabel (Translations.GetString ("其他参考图层")));
			content.Append (layerScroll);
		}
		content.Append (CreateDialogLabel (Translations.GetString ("参考图片文件")));
		content.Append (chooseFilesButton);
		content.Append (fileLabel);

		void UpdateSubmitButton ()
		{
			promptBuffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
			submitButton.Sensitive = sizePicker.IsValid
				&& spritesheet_valid ()
				&& !string.IsNullOrWhiteSpace (promptBuffer.GetText (start, end, includeHiddenChars: true));
		}
		promptBuffer.OnChanged += (_, _) => UpdateSubmitButton ();
		sizePicker.Changed += (_, _) => UpdateSubmitButton ();
		UpdateSubmitButton ();

		if (await dialog.RunAsync () != Gtk.ResponseType.Ok)
			return null;

		List<UserLayer> layers = [];
		foreach ((Gtk.CheckButton check, UserLayer layer) in layerChoices)
			if (check.Active)
				layers.Add (layer);

		promptBuffer.GetBounds (out Gtk.TextIter promptStart, out Gtk.TextIter promptEnd);
		string prompt = promptBuffer.GetText (promptStart, promptEnd, includeHiddenChars: true).Trim ();
		Size imageSize = mode == AiImageRequestMode.BackgroundCleanup
			? doc!.ImageSize
			: sizePicker.SelectedSize ?? throw new InvalidOperationException ("A valid image size is required.");
		if (mode != AiImageRequestMode.BackgroundCleanup) {
			string imageService = serviceCombobox.Active == 0
				? AI.AiRequestSettings.AgnesService
				: AI.AiRequestSettings.GptImageService;
			string provider = providerCombobox.Active == 0
				? AI.AiRequestSettings.ZzswitchProvider
				: AI.AiRequestSettings.LukyfaceProvider;
			AI.AiRequestSettings.Save (PintaCore.Settings, imageService, provider);
			PintaCore.Settings.DoSaveSettingsBeforeQuit ();
		}

		string resultLayerName = spritesheetMode
			? spritesheet_result_layer_name?.Invoke () ?? Translations.GetString ("Spritesheet")
			: mode == AiImageRequestMode.BackgroundCleanup
				? Translations.GetString ("White Background")
				: Translations.GetString ("AI Generated Image");
		string progressTitle = spritesheetMode
			? Translations.GetString ("Generate Spritesheet")
			: Translations.GetString ("AI 生成");
		return new (prompt, imageSize, layers, files, resultLayerName, progressTitle, spritesheet_info ());
	}

	private static Gtk.Label CreateDialogLabel (string text)
	{
		Gtk.Label label = Gtk.Label.New (text);
		label.Halign = Gtk.Align.Start;
		label.AddCssClass (AdwaitaStyles.Heading);
		return label;
	}

	private static string GetLayerPath (UserLayer layer)
	{
		List<string> names = [];
		for (UserLayer? current = layer; current is not null; current = current.Parent)
			names.Add (current.Name);
		names.Reverse ();
		return string.Join (" / ", names);
	}

	private static (byte[] Png, string FileName) LoadReferenceImage (Gio.File file)
	{
		using Gio.FileInputStream stream = file.Read (null);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)
			?? throw new InvalidOperationException ($"Unable to read image: {file.GetDisplayName ()}");
		string name = Path.GetFileNameWithoutExtension (file.GetDisplayName ());
		return (pixbuf.SaveToBuffer ("png"), $"{name}.png");
	}

	private static byte[] CreateLayerPng (Document doc, UserLayer sourceLayer)
	{
		using Cairo.ImageSurface source = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			doc.ImageSize.Width,
			doc.ImageSize.Height);
		using (Cairo.Context context = new (source)) {
			foreach (Layer layer in sourceLayer.GetLayersToPaint ())
				layer.Draw (context);
		}

		source.MarkDirty ();
		using GdkPixbuf.Pixbuf pixbuf = source.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static string CreateCutoutDebugDirectory ()
	{
		string root = Path.Combine (
			AppContext.BaseDirectory,
			"ai-cutout-logs",
			DateTime.Now.ToString ("yyyyMMdd-HHmmss-fff"));
		Directory.CreateDirectory (root);
		return root;
	}

	private static void SaveCutoutDebugPng (string directory, string fileName, byte[] png)
	{
		try {
			File.WriteAllBytes (Path.Combine (directory, fileName), png);
		} catch (Exception ex) {
			Console.WriteLine ($"Warning: failed to save AI cutout debug image '{fileName}': {ex.Message}");
		}
	}

	private static void SaveCutoutDebugLog (string directory, string message)
	{
		try {
			File.AppendAllText (
				Path.Combine (directory, "log.txt"),
				$"{DateTime.Now:O} {message}{Environment.NewLine}");
		} catch (Exception ex) {
			Console.WriteLine ($"Warning: failed to save AI cutout debug log: {ex.Message}");
		}
	}

	private static Cairo.ImageSurface LoadPngAsSurface (byte[] png, Size targetSize)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		Cairo.ImageSurface surface = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			targetSize.Width,
			targetSize.Height);
		using Cairo.Context context = new (surface);
		context.Scale (targetSize.Width / (double) pixbuf.Width, targetSize.Height / (double) pixbuf.Height);
		context.DrawPixbuf (pixbuf, PointD.Zero);
		surface.MarkDirty ();
		return surface;
	}

	private static void CreateTransparentCutout (
		Cairo.ImageSurface white,
		Cairo.ImageSurface black,
		Cairo.ImageSurface destination)
	{
		const int alpha_noise_floor = 24;
		ReadOnlySpan<ColorBgra> whitePixels = white.GetReadOnlyPixelData ();
		ReadOnlySpan<ColorBgra> blackPixels = black.GetReadOnlyPixelData ();
		Span<ColorBgra> destinationPixels = destination.GetPixelData ();

		for (int i = 0; i < destinationPixels.Length; i++) {
			ColorBgra w = whitePixels[i];
			ColorBgra b = blackPixels[i];
			int matte = Math.Max (
				Math.Clamp (w.R - b.R, 0, 255),
				Math.Max (Math.Clamp (w.G - b.G, 0, 255), Math.Clamp (w.B - b.B, 0, 255)));
			int alpha = 255 - matte;
			destinationPixels[i] = alpha <= alpha_noise_floor
				? ColorBgra.Transparent
				: ColorBgra.FromBgraClamped (
					Math.Min (b.B, alpha),
					Math.Min (b.G, alpha),
					Math.Min (b.R, alpha),
					alpha);
		}
		destination.MarkDirty ();
	}

	private static void DrawPngOnLayer (byte[] png, UserLayer layer)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		using Cairo.Context context = new (layer.Surface);
		context.DrawPixbuf (pixbuf, PointD.Zero);
	}

	private sealed record AiImageRequestOptions (
		string Prompt,
		Size ImageSize,
		IReadOnlyList<UserLayer> Layers,
		IReadOnlyList<Gio.File> Files,
		string ResultLayerName,
		string ProgressTitle,
		AI.SpritesheetAttemptInfo? Spritesheet);

	private enum AiImageRequestMode
	{
		BackgroundCleanup,
		ImageGeneration,
		SpritesheetGeneration,
	}

	private enum AiImageOperation
	{
		GenerateWhite,
		Cutout,
	}
}
