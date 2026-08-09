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

	private async void HandleGenerateSingleDirectionAnimationActivated (object sender, EventArgs e)
	{
		if (cutout_running || !EnsureAiLoggedIn () || workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.HasSelectedLayer || !CanCreateSpritesheetAnimation (document.Layers.CurrentUserLayer))
			return;

		AiImageRequestOptions? options = await PromptAiImageRequestAsync (
			AiImageRequestMode.SingleDirectionAnimationGeneration,
			document,
			document.Layers.CurrentUserLayer);
		if (options is not null) {
			UserLayer? source = await GenerateImageAsync (document, options with {
				Layers = [document.Layers.CurrentUserLayer, .. options.Layers],
			});
			if (source is not null
				&& options.SingleDirection is AI.SpritesheetAttemptInfo info)
				await OpenGeneratedSingleDirectionEditorAsync (document, source, info);
		}
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
		if (referenceDocument is null)
			return;
		AiImageRequestOptions? options = await PromptAiImageRequestAsync (
			AiImageRequestMode.ImageGeneration,
			referenceDocument,
			sourceLayer: null);
		if (options is null)
			return;

		await GenerateImageAsync (referenceDocument, options);
	}

	private async Task<UserLayer?> GenerateImageAsync (Document referenceDocument, AiImageRequestOptions options)
	{
		UserLayer? generatedSingleDirectionSource = null;

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
			byte[]? sourcePng = options.SourceLayer is UserLayer sourceLayer
				? options.PreparedSourcePng ?? CreateLayerPng (sourceLayer)
				: null;
			IEnumerable<UserLayer> layers = options.ParentLayer is not null
				? options.Layers
				: options.Layers.OrderByDescending (IsCharacterAnchor);
			foreach (UserLayer layer in layers)
				references.Add ((CreateLayerPng (layer), GetAiReferenceFileName (layer, references.Count + 1)));
			foreach (Gio.File file in options.Files)
				references.Add (LoadReferenceImage (file));

			string debugDir = CreateCutoutDebugDirectory ();
			byte[] generatedPng = await GenerateBackgroundWithRetryAsync (
				options.ProgressTitle,
				() => sourcePng is null
					? background_cutout.GenerateImageAsync (
						options.ImageSize,
						options.Prompt,
						references,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
						message => SaveCutoutDebugLog (debugDir, message),
						cts.Token)
					: background_cutout.GenerateImageFromSourceAsync (
						sourcePng,
						options.ImageSize,
						options.RequestImageSize ?? options.ImageSize,
						options.WhitePadding,
						options.Prompt,
						references,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
						message => SaveCutoutDebugLog (debugDir, message),
						cts.Token),
				message => SaveCutoutDebugLog (debugDir, message),
				cts.Token);
			byte[]? confirmedPng = await ConfirmGeneratedImageAsync (
				options.Layers.FirstOrDefault (),
				[generatedPng],
				options.ResultLayerName);
			if (confirmedPng is null) {
				clearStatus = false;
				chrome.SetStatusBarText (Translations.GetString ("Image generation canceled."));
				return null;
			}
			generatedPng = confirmedPng;

			SetProgress (Translations.GetString ("Creating generated image layer..."), 0.85);
			if (options.ParentLayer is UserLayer parent) {
				UserLayer result = AddAiChildResultLayer (
					referenceDocument,
					parent,
					options.ResultLayerName,
					options.ImageSize);
				DrawPngOnLayer (generatedPng, result);
				referenceDocument.History.PushNewItem (new AddLayerHistoryItem (
					Resources.Icons.LayerNew,
					options.ResultLayerName,
					result,
					referenceDocument.Layers.GetPosition (result)));
				referenceDocument.Workspace.Invalidate ();
			} else if (options.Spritesheet is null && options.SingleDirection is null) {
				UserLayer result = AddAiResultLayer (referenceDocument, options.ResultLayerName, options.ImageSize);
				DrawPngOnLayer (generatedPng, result);
				referenceDocument.History.PushNewItem (new AddLayerHistoryItem (
					Resources.Icons.LayerNew,
					options.ResultLayerName,
					result,
					referenceDocument.Layers.GetPosition (result)));
				referenceDocument.Workspace.Invalidate ();
			} else if (options.SingleDirection is not null) {
				generatedSingleDirectionSource = InsertSingleDirectionAnimationAttempt (
					referenceDocument,
					generatedPng,
					options.SingleDirection);
			} else if (options.Spritesheet is AI.SpritesheetAttemptInfo spritesheet) {
				InsertSpritesheetAttempt (referenceDocument, generatedPng, spritesheet);
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
				progress.Window,
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

		return generatedSingleDirectionSource;

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
		AiLayerImage sourceImage = CreateAiLayerImage (sourceLayer);
		Size operationSize = sourceImage.Size;
		string imageService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
		string provider = AI.AiRequestSettings.GetImageProvider (PintaCore.Settings);
		Size? requestSize = await ConfirmImageResolutionAsync (
			Translations.GetString ("background cleanup"),
			imageService,
			provider,
			operationSize);
		if (requestSize is null)
			return;

		cutout_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		using CancellationTokenSource cts = new ();
		IProgressDialog progress = chrome.ProgressDialog;
		progress.Title = Translations.GetString ("Background Cleanup");
		progress.Text = Translations.GetString ("Preparing image...");
		progress.Progress = 0.05;
		progress.Canceled += HandleProgressCanceled;
		progress.Show ();
		chrome.MainWindowBusy = true;
		SetProgress (Translations.GetString ("Preparing image..."), 0.05);
		bool clearStatus = true;

		try {
			byte[] sourcePng = sourceImage.Png;
			string debugDir = CreateCutoutDebugDirectory ();
			List<(byte[] Png, string FileName)> references = [];
			foreach (UserLayer layer in options.Layers)
				references.Add ((CreateAiLayerPng (layer), $"layer-{references.Count + 1}.png"));
			foreach (Gio.File file in options.Files)
				references.Add (LoadReferenceImage (file));

			SaveCutoutDebugLog (
				debugDir,
				$"AI background cleanup client: document_size={doc.ImageSize.Width}x{doc.ImageSize.Height}, "
				+ $"layer_size={operationSize.Width}x{operationSize.Height}, "
				+ $"source_layer={sourceLayer.Name}, source_bytes={sourcePng.Length}, references={references.Count}");
			SaveCutoutDebugPng (debugDir, "source.png", sourcePng);
			byte[] whitePng = await GenerateBackgroundWithRetryAsync (
				Translations.GetString ("Background Cleanup"),
				() => background_cutout.GenerateWhiteAsync (
					sourcePng,
					operationSize,
					options.Prompt,
					references,
					SetProgress,
					(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token,
					requestSize),
				message => SaveCutoutDebugLog (debugDir, message),
				cts.Token);
			byte[]? confirmedWhitePng = await ConfirmGeneratedImageAsync (
				sourceLayer,
				[whitePng],
				Translations.GetString ("White Background"));
			if (confirmedWhitePng is null) {
				clearStatus = false;
				chrome.SetStatusBarText (Translations.GetString ("Image generation canceled."));
				return;
			}
			whitePng = confirmedWhitePng;

			SetProgress (Translations.GetString ("Creating white background layer..."), 0.85);
			UserLayer whiteLayer = AddAiResultLayer (doc, Translations.GetString ("White Background"), operationSize, sourceImage.Origin);
			DrawPngOnLayer (whitePng, whiteLayer);
			doc.History.PushNewItem (new AddLayerHistoryItem (
				Resources.Icons.ColorModeColor,
				Translations.GetString ("Background Cleanup"),
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
				progress.Window,
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
		AiLayerImage sourceImage = CreateAiLayerImage (sourceLayer);
		Size operationSize = sourceImage.Size;
		string selectedService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
		Size? requestSizeOverride = selectedService == AI.AiRequestSettings.BaiduService
			? operationSize
			: await ConfirmImageResolutionAsync (
				Translations.GetString ("cutout"),
				selectedService,
				AI.AiRequestSettings.GetImageProvider (PintaCore.Settings),
				operationSize);
		if (requestSizeOverride is null)
			return;

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
			byte[] sourcePng = sourceImage.Png;
			string cutoutName = Translations.GetString ("Transparent Cutout");
			string imageService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
			RectangleI? baiduControlBox = imageService == AI.AiRequestSettings.BaiduService
				? GetBaiduControlBox (doc, operationSize, sourceImage.Origin)
				: null;
			string debugDir = CreateCutoutDebugDirectory ();
			SaveCutoutDebugLog (
				debugDir,
				$"AI cutout: service={imageService}, document_size={doc.ImageSize.Width}x{doc.ImageSize.Height}, "
				+ $"layer_size={operationSize.Width}x{operationSize.Height}, "
				+ $"source_layer={sourceLayer.Name}, source_bytes={sourcePng.Length}, "
				+ $"selection={doc.GetSelectedBounds (canvasOnly: true)}, "
				+ $"baidu_mode={(baiduControlBox is null ? "auto" : "control")}, "
				+ $"baidu_control_box={baiduControlBox?.ToString () ?? "none"}");
			SaveCutoutDebugPng (debugDir, "source.png", sourcePng);
			if (imageService == AI.AiRequestSettings.BaiduService) {
				SetProgress (Translations.GetString ("Requesting Baidu intelligent cutout..."), 0.25);
				byte[] transparentPng = await GenerateBackgroundWithRetryAsync (
					Translations.GetString ("Cutout"),
					() => background_cutout.GenerateBaiduCutoutAsync (
						sourcePng,
						operationSize,
						baiduControlBox,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token);
				byte[]? confirmedTransparentPng = await ConfirmGeneratedImageAsync (
					sourceLayer,
					[transparentPng],
					Translations.GetString ("Transparent Cutout"));
				if (confirmedTransparentPng is null) {
					clearStatus = false;
					chrome.SetStatusBarText (Translations.GetString ("Image generation canceled."));
					return;
				}
				transparentPng = confirmedTransparentPng;

				SetProgress (Translations.GetString ("Creating transparent layer..."), 0.85);
				UserLayer cutoutLayer = AddAiResultLayer (doc, cutoutName, operationSize, sourceImage.Origin);
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
						cts.Token,
						requestSizeOverride),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token);

				CompoundHistoryItem history = new (Resources.Icons.ColorModeTransparency, Translations.GetString ("Cutout"));
				using Cairo.ImageSurface white = LoadPngAsSurface (sourcePng, operationSize);
				using Cairo.ImageSurface black = LoadPngAsSurface (blackPng, operationSize);
				using Cairo.ImageSurface cutoutSurface = CairoExtensions.CreateImageSurface (
					Cairo.Format.Argb32,
					operationSize.Width,
					operationSize.Height);
				CreateTransparentCutout (white, black, cutoutSurface);
				byte[] transparentPng = CreateSurfacePng (cutoutSurface);
				byte[]? confirmedTransparentPng = await ConfirmGeneratedImageAsync (
					sourceLayer,
					[transparentPng],
					Translations.GetString ("Transparent Cutout"));
				if (confirmedTransparentPng is null) {
					clearStatus = false;
					chrome.SetStatusBarText (Translations.GetString ("Image generation canceled."));
					return;
				}

				SetProgress (Translations.GetString ("Creating black and transparent layers..."), 0.85);
				UserLayer blackLayer = AddAiResultLayer (doc, Translations.GetString ("Black Background"), operationSize, sourceImage.Origin);
				DrawPngOnLayer (blackPng, blackLayer);
				history.Push (new AddLayerHistoryItem (
					Resources.Icons.ColorModeColor,
					Translations.GetString ("Black Background"),
					blackLayer,
					doc.Layers.GetPosition (blackLayer)));
				UserLayer cutoutLayer = AddAiResultLayer (doc, cutoutName, operationSize, sourceImage.Origin);
				DrawPngOnLayer (confirmedTransparentPng, cutoutLayer);
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
				progress.Window,
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

	private static RectangleI? GetBaiduControlBox (
		Document doc,
		Size sourceSize,
		PointD sourceOrigin = default)
	{
		if (!doc.Selection.Visible)
			return null;

		RectangleI sourceBounds = new (0, 0, sourceSize.Width, sourceSize.Height);
		PointI origin = new ((int) Math.Floor (sourceOrigin.X), (int) Math.Floor (sourceOrigin.Y));
		RectangleI selectedBounds = doc.GetSelectedBounds (canvasOnly: true);
		RectangleI selection = new (
			selectedBounds.X - origin.X,
			selectedBounds.Y - origin.Y,
			selectedBounds.Width,
			selectedBounds.Height);
		selection = selection.Intersect (sourceBounds);
		if (selection.IsEmpty)
			return null;

		// Baidu requires both rectangle corners to be strictly inside the image.
		// Pinta selections use the same top-left origin, but a selection can touch
		// an edge; move only that edge inward instead of discarding the control box.
		int left = Math.Max (selection.X, sourceBounds.X + 1);
		int top = Math.Max (selection.Y, sourceBounds.Y + 1);
		int right = Math.Min (selection.X + selection.Width, sourceBounds.Right);
		int bottom = Math.Min (selection.Y + selection.Height, sourceBounds.Bottom);
		if (right - left < 10 || bottom - top < 10)
			return null;

		return new RectangleI (left, top, right - left, bottom - top);
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
			chrome.ProgressDialog.Window,
			operation,
			$"{Translations.GetString ("The image request failed. Try the request again?")}\n\n{ex.Message}");
		const string cancel_response = "cancel";
		const string retry_response = "retry";
		confirmation.AddResponse (cancel_response, Translations.GetString ("_Cancel"));
		confirmation.AddResponse (retry_response, Translations.GetString ("_Retry"));
		confirmation.SetResponseAppearance (retry_response, Adw.ResponseAppearance.Suggested);
		confirmation.Modal = true;
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
		serviceCombobox.AppendText (Translations.GetString ("Nano Banana"));
		serviceCombobox.AppendText (Translations.GetString ("Baidu"));
		serviceCombobox.Hexpand = true;

		Gtk.ComboBoxText providerCombobox = Gtk.ComboBoxText.New ();
		IReadOnlyList<AI.AiProviderInfo> gptProviders = GetGptImageProviders ();
		IReadOnlyList<AI.AiProviderInfo> nanoBananaProviders = GetNanoBananaProviders ();
		PopulateProviderCombo (
			providerCombobox,
			gptProviders,
			AI.AiRequestSettings.GetGptProvider (PintaCore.Settings));
		providerCombobox.Hexpand = true;
		Gtk.Label providerLabel = CreateSettingsLabel (Translations.GetString ("GPT provider:"));
		Gtk.Label operationCostLabel = Gtk.Label.New (string.Empty);
		operationCostLabel.Halign = Gtk.Align.Start;
		operationCostLabel.AddCssClass (AdwaitaStyles.DimLabel);

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = 8;
		grid.ColumnSpacing = 8;
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Image service:")), 0, 0, 1, 1);
		grid.Attach (serviceCombobox, 1, 0, 1, 1);
		grid.Attach (providerLabel, 0, 1, 1, 1);
		grid.Attach (providerCombobox, 1, 1, 1, 1);
		grid.Attach (CreateSettingsLabel (Translations.GetString ("Generation cost:")), 0, 2, 1, 1);
		grid.Attach (operationCostLabel, 1, 2, 1, 1);

		Gtk.Widget whiteButton = dialog.AddButton (Translations.GetString ("Generate White Background"), (int) Gtk.ResponseType.Apply);
		Gtk.Widget cutoutButton = dialog.AddButton (Translations.GetString ("Cutout"), (int) Gtk.ResponseType.Ok);
		cutoutButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		string savedService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
		serviceCombobox.Active = savedService switch {
			AI.AiRequestSettings.AgnesService => 0,
			AI.AiRequestSettings.NanoBananaService => 2,
			AI.AiRequestSettings.BaiduService => 3,
			_ => 1,
		};

		void UpdateVisibility ()
		{
			bool gptSelected = serviceCombobox.Active == 1;
			bool nanoBananaSelected = serviceCombobox.Active == 2;
			providerLabel.Visible = gptSelected || nanoBananaSelected;
			providerCombobox.Visible = gptSelected || nanoBananaSelected;
			providerLabel.SetText (nanoBananaSelected
				? Translations.GetString ("Nano Banana channel:")
				: Translations.GetString ("GPT provider:"));
			IReadOnlyList<AI.AiProviderInfo> providers = nanoBananaSelected ? nanoBananaProviders : gptProviders;
			string selected = nanoBananaSelected
				? AI.AiRequestSettings.GetNanoBananaProvider (PintaCore.Settings)
				: AI.AiRequestSettings.GetGptProvider (PintaCore.Settings);
			PopulateProviderCombo (providerCombobox, providers, selected);
			whiteButton.Visible = serviceCombobox.Active != 3;
			string provider = providerCombobox.Active >= 0 && providerCombobox.Active < providers.Count
				? providers[providerCombobox.Active].Id
				: imageServiceForCost ();
			int cost = AI.BackgroundCutoutService.GetImageGenerationCost (provider);
			operationCostLabel.SetText (serviceCombobox.Active == 3
				? Translations.GetString ("{0} credits per cutout", 1)
				: cost > 0
					? Translations.GetString ("{0} credits per image", cost)
					: Translations.GetString ("Cost unavailable"));
		}

		string imageServiceForCost ()
			=> serviceCombobox.Active switch {
				0 => AI.AiRequestSettings.AgnesService,
				2 => AI.AiRequestSettings.NanoBananaService,
				3 => AI.AiRequestSettings.BaiduService,
				_ => AI.AiRequestSettings.GptImageService,
			};
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
			2 => AI.AiRequestSettings.NanoBananaService,
			3 => AI.AiRequestSettings.BaiduService,
			_ => AI.AiRequestSettings.GptImageService,
		};
		SaveImageServiceSelection (imageService, providerCombobox, gptProviders, nanoBananaProviders);
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
		if ((mode == AiImageRequestMode.BackgroundCleanup || mode == AiImageRequestMode.ImageSplitGeneration)
			&& (doc is null || sourceLayer is null))
			throw new ArgumentException ("Background cleanup requires a document and source layer.");
		bool singleDirectionMode = mode == AiImageRequestMode.SingleDirectionAnimationGeneration;
		bool imageSplitMode = mode == AiImageRequestMode.ImageSplitGeneration;
		bool spritesheetMode = mode == AiImageRequestMode.SpritesheetGeneration || singleDirectionMode;
		AI.SpritesheetPromptCatalog? spritesheetCatalog = spritesheetMode
			? AI.SpritesheetPromptCatalog.Load ()
			: null;

		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = mode switch {
			AiImageRequestMode.BackgroundCleanup => Translations.GetString ("Background Cleanup"),
			AiImageRequestMode.SpritesheetGeneration => Translations.GetString ("Generate Spritesheet"),
			AiImageRequestMode.SingleDirectionAnimationGeneration => Translations.GetString ("Single-Direction Animation"),
			_ => Translations.GetString ("AI Image Generation"),
		};
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = imageSplitMode ? 980 : 520;
		dialog.DefaultHeight = imageSplitMode ? 900 : spritesheetMode ? 820 : 620;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget submitButton = dialog.AddButton (
			mode == AiImageRequestMode.BackgroundCleanup
				? Translations.GetString ("Clean Up Background")
				: Translations.GetString ("Generate"),
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
		promptScroll.HeightRequest = spritesheetMode ? 240 : 110;
		promptScroll.SetChild (promptView);

		Gtk.ComboBoxText serviceCombobox = Gtk.ComboBoxText.New ();
		serviceCombobox.AppendText (Translations.GetString ("Agnes"));
		serviceCombobox.AppendText (Translations.GetString ("GPT Image"));
		serviceCombobox.AppendText (Translations.GetString ("Nano Banana"));
		serviceCombobox.Active = AI.AiRequestSettings.GetImageService (PintaCore.Settings) switch {
			AI.AiRequestSettings.AgnesService => 0,
			AI.AiRequestSettings.NanoBananaService => 2,
			_ => 1,
		};
		Gtk.ComboBoxText providerCombobox = Gtk.ComboBoxText.New ();
		IReadOnlyList<AI.AiProviderInfo> gptProviders = GetGptImageProviders ();
		IReadOnlyList<AI.AiProviderInfo> nanoBananaProviders = GetNanoBananaProviders ();
		PopulateProviderCombo (
			providerCombobox,
			gptProviders,
			AI.AiRequestSettings.GetGptProvider (PintaCore.Settings));
		Gtk.Label providerLabel = CreateSettingsLabel (Translations.GetString ("GPT provider:"));
		Gtk.Label generationCostLabel = Gtk.Label.New (string.Empty);
		generationCostLabel.Halign = Gtk.Align.Start;
		generationCostLabel.AddCssClass (AdwaitaStyles.DimLabel);
		AiImageSizePicker sizePicker = new ();
		using ImageSplitPreviewControls? imageSplitPreview = imageSplitMode
			? CreateImageSplitPreviewControls (sourceLayer!)
			: null;

		void UpdateGenerationSettings ()
		{
			bool gptSelected = serviceCombobox.Active == 1;
			bool nanoBananaSelected = serviceCombobox.Active == 2;
			providerLabel.Visible = gptSelected || nanoBananaSelected;
			providerCombobox.Visible = gptSelected || nanoBananaSelected;
			providerLabel.SetText (nanoBananaSelected
				? Translations.GetString ("Nano Banana channel:")
				: Translations.GetString ("GPT provider:"));
			IReadOnlyList<AI.AiProviderInfo> providers = nanoBananaSelected ? nanoBananaProviders : gptProviders;
			string selected = nanoBananaSelected
				? AI.AiRequestSettings.GetNanoBananaProvider (PintaCore.Settings)
				: AI.AiRequestSettings.GetGptProvider (PintaCore.Settings);
			PopulateProviderCombo (providerCombobox, providers, selected);
			string imageService = serviceCombobox.Active switch {
				1 => AI.AiRequestSettings.GptImageService,
				2 => AI.AiRequestSettings.NanoBananaService,
				_ => AI.AiRequestSettings.AgnesService,
			};
			UpdateProviderSettings (imageService, nanoBananaSelected ? nanoBananaProviders : gptProviders);
		}

		void UpdateProviderSettings (string imageService, IReadOnlyList<AI.AiProviderInfo> providers)
		{
			string provider = imageService == AI.AiRequestSettings.AgnesService
				? AI.AiRequestSettings.AgnesService
				: providerCombobox.Active >= 0 && providerCombobox.Active < providers.Count
				? providers[providerCombobox.Active].Id
				: imageService;
			sizePicker.SetService (imageService, provider);
			imageSplitPreview?.SetService (imageService, provider);
			int cost = AI.BackgroundCutoutService.GetImageGenerationCost (provider);
			generationCostLabel.SetText (cost > 0
				? Translations.GetString ("{0} credits per image", cost)
				: Translations.GetString ("Cost unavailable"));
		}

		Gtk.Grid generationGrid = Gtk.Grid.New ();
		generationGrid.RowSpacing = 8;
		generationGrid.ColumnSpacing = 8;
		generationGrid.Attach (CreateSettingsLabel (Translations.GetString ("Image service:")), 0, 0, 1, 1);
		generationGrid.Attach (serviceCombobox, 1, 0, 1, 1);
		generationGrid.Attach (providerLabel, 0, 1, 1, 1);
		generationGrid.Attach (providerCombobox, 1, 1, 1, 1);
		Gtk.Label imageSizeLabel = CreateSettingsLabel (Translations.GetString ("Image size:"));
		generationGrid.Attach (imageSizeLabel, 0, 2, 1, 1);
		generationGrid.Attach (sizePicker.Widget, 1, 2, 1, 1);
		generationGrid.Attach (CreateSettingsLabel (Translations.GetString ("Generation cost:")), 0, 3, 1, 1);
		generationGrid.Attach (generationCostLabel, 1, 3, 1, 1);
		generationGrid.Visible = mode != AiImageRequestMode.BackgroundCleanup;
		if (imageSplitMode) {
			imageSizeLabel.Visible = false;
			sizePicker.Widget.Visible = false;
		}
		serviceCombobox.OnChanged += (_, _) => UpdateGenerationSettings ();
		providerCombobox.OnChanged += (_, _) => {
			string imageService = serviceCombobox.Active switch {
				1 => AI.AiRequestSettings.GptImageService,
				2 => AI.AiRequestSettings.NanoBananaService,
				_ => AI.AiRequestSettings.AgnesService,
			};
			UpdateProviderSettings (imageService, serviceCombobox.Active == 2 ? nanoBananaProviders : gptProviders);
		};
		UpdateGenerationSettings ();

		Gtk.Widget? spritesheet_controls = null;
		Gtk.CheckButton? directionModeSelection = null;
		Gtk.Label generationTypeValue = Gtk.Label.New (
			GetImageGenerationTypeLabel (mode, directionSheet: false));
		generationTypeValue.Halign = Gtk.Align.Start;
		generationTypeValue.AddCssClass (AdwaitaStyles.DimLabel);
		Func<string>? spritesheet_result_layer_name = null;
		Func<AI.SpritesheetAttemptInfo?> spritesheet_info = () => null;
		Func<AI.SpritesheetAttemptInfo?> single_direction_info = () => null;
		Func<bool> spritesheet_valid = () => true;
		if (spritesheetCatalog is not null) {
			Gtk.CheckButton directionModeButton = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Direction Sheet"));
			directionModeSelection = directionModeButton;
			Gtk.CheckButton actionModeButton = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Action Sequence"));
			actionModeButton.SetGroup (directionModeButton);
			directionModeButton.Active = !singleDirectionMode;
			actionModeButton.Active = singleDirectionMode;

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

			Gtk.Label backgroundSummaryLabel = Gtk.Label.New (Translations.GetString ("White (#FFFFFF)"));
			backgroundSummaryLabel.Halign = Gtk.Align.Start;
			Gtk.Label directionsSummaryLabel = Gtk.Label.New (Translations.GetString ("8 fixed directions"));
			directionsSummaryLabel.Halign = Gtk.Align.Start;
			Gtk.Label directionsLabel = CreateSettingsLabel (Translations.GetString ("Directions:"));

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
			spritesheetGrid.Attach (backgroundSummaryLabel, 1, 4, 1, 1);
			spritesheetGrid.Attach (directionsLabel, 0, 5, 1, 1);
			spritesheetGrid.Attach (directionsSummaryLabel, 1, 5, 1, 1);
			spritesheetGrid.Attach (summaryLabel, 1, 6, 1, 1);
			Gtk.Box promptSection = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
			promptSection.Append (CreateDialogLabel (Translations.GetString (
				singleDirectionMode ? "Prompt" : "Action generation prompt")));
			promptSection.Append (spritesheetGrid);
			spritesheet_controls = promptSection;
			modeBox.Visible = !singleDirectionMode;
			actionLabel.Visible = !singleDirectionMode;
			actionCombobox.Visible = !singleDirectionMode;
			frameCountLabel.Visible = !singleDirectionMode;
			frameCountSpinner.Visible = !singleDirectionMode;
			directionsLabel.Visible = !singleDirectionMode;
			directionsSummaryLabel.Visible = !singleDirectionMode;
			if (singleDirectionMode)
				spritesheetGrid.GetChildAt (0, 0)!.Visible = false;

			bool IsCustomAction ()
				=> spritesheetCatalog.Actions[actionCombobox.Active].Id == "custom";

			void RebuildSpritesheetPrompt ()
			{
				if (singleDirectionMode)
					return;

				bool directionSheet = !singleDirectionMode && directionModeButton.Active;
				actionLabel.Visible = !directionSheet;
				actionCombobox.Visible = !directionSheet;
				frameCountLabel.Visible = !directionSheet;
				frameCountSpinner.Visible = !directionSheet;
				bool customVisible = !directionSheet && IsCustomAction ();
				customActionLabel.Visible = customVisible;
				customActionEntry.Visible = customVisible;

				int framesPerDirection = directionSheet ? 1 : (int) frameCountSpinner.Value;
				int directionCount = singleDirectionMode ? 1 : spritesheetCatalog.DirectionIds.Count;
				int totalFrames = directionCount * framesPerDirection;
				if (sizePicker.SelectedSize is not Size size) {
					promptBuffer.SetText (string.Empty, -1);
					summaryLabel.SetText (Translations.GetString ("Select a valid image size."));
					return;
				}

				(int columns, int rows) = AI.SpritesheetPromptCatalog.CalculateGrid (totalFrames, size);
				summaryLabel.SetText (directionSheet
					? Translations.GetString ("{0} directions / {1} x {2} grid", spritesheetCatalog.DirectionIds.Count, columns, rows)
					: singleDirectionMode
						? Translations.GetString ("{0} frames / {1} x {2} grid", framesPerDirection, columns, rows)
						: Translations.GetString (
							"{0} direction(s) x {1} frames = {2} frames / {3} x {4} grid",
							directionCount,
							framesPerDirection,
							totalFrames,
							columns,
							rows));
				string actionId = spritesheetCatalog.Actions[actionCombobox.Active].Id;
				promptBuffer.SetText (
					spritesheetCatalog.BuildPrompt (directionSheet, actionId, customActionEntry.GetText (), framesPerDirection, size), -1);
			}

			spritesheet_valid = () => singleDirectionMode
				|| directionModeButton.Active
				|| !IsCustomAction ()
				|| !string.IsNullOrWhiteSpace (customActionEntry.GetText ());
			spritesheet_result_layer_name = () => singleDirectionMode
				? Translations.GetString ("Single-Direction Animation")
				: directionModeButton.Active
					? Translations.GetString ("Direction Sheet")
					: $"{spritesheetCatalog.Actions[actionCombobox.Active].Label} {Translations.GetString ("Spritesheet")}";
			spritesheet_info = () => singleDirectionMode
				? null
				: CreateSpritesheetAttemptInfo (
					spritesheetCatalog, directionModeButton.Active, actionCombobox.Active,
					frameCountSpinner.Value, sizePicker.SelectedSize!.Value, promptBuffer);
			single_direction_info = () => singleDirectionMode
			? CreateSingleDirectionAttemptInfo (sizePicker.SelectedSize!.Value, promptBuffer)
			: null;

			directionModeButton.OnToggled += (_, _) => {
				if (!directionModeButton.Active)
					return;
				RebuildSpritesheetPrompt ();
				generationTypeValue.SetText (GetImageGenerationTypeLabel (mode, directionModeButton.Active));
			};
			actionModeButton.OnToggled += (_, _) => {
				if (!actionModeButton.Active)
					return;
				RebuildSpritesheetPrompt ();
				generationTypeValue.SetText (GetImageGenerationTypeLabel (mode, directionModeButton.Active));
			};
			actionCombobox.OnChanged += (_, _) => {
				frameCountSpinner.Value = spritesheetCatalog.Actions[actionCombobox.Active].DefaultFrameCount;
				RebuildSpritesheetPrompt ();
			};
			customActionEntry.OnChanged += (_, _) => RebuildSpritesheetPrompt ();
			frameCountSpinner.OnValueChanged += (_, _) => RebuildSpritesheetPrompt ();
			sizePicker.Changed += (_, _) => {
				if (!singleDirectionMode)
					RebuildSpritesheetPrompt ();
			};
			if (!singleDirectionMode)
				RebuildSpritesheetPrompt ();
		}
		generationTypeValue.SetText (
			GetImageGenerationTypeLabel (mode, directionModeSelection?.Active == true));

		Gtk.Grid generationTypeGrid = Gtk.Grid.New ();
		generationTypeGrid.RowSpacing = 8;
		generationTypeGrid.ColumnSpacing = 8;
		generationTypeGrid.Attach (
			CreateSettingsLabel (Translations.GetString ("Current generation type:")),
			0,
			0,
			1,
			1);
		generationTypeGrid.Attach (generationTypeValue, 1, 0, 1, 1);

		Gtk.Picture sourcePreview = Gtk.Picture.New ();
		sourcePreview.ContentFit = Gtk.ContentFit.ScaleDown;
		sourcePreview.SetSizeRequest (160, 100);
		Gtk.Label sourceLabel = Gtk.Label.New (string.Empty);
		sourceLabel.Halign = Gtk.Align.Start;
		if (sourceLayer is not null) {
			sourcePreview.Paintable = sourceLayer.Surface.ToTexture ();
			sourceLabel.SetText (Translations.GetString (
				"{0}  {1} x {2} px",
				GetLayerPath (sourceLayer),
				sourceLayer.Surface.Width,
				sourceLayer.Surface.Height));
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
				Gtk.Label sizeLabel = Gtk.Label.New (Translations.GetString (
					"{0} x {1} px",
					layer.Surface.Width,
					layer.Surface.Height));
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
		Gtk.Button chooseFilesButton = Gtk.Button.NewWithLabel (Translations.GetString ("Choose Image Files..."));
		chooseFilesButton.Halign = Gtk.Align.Start;
		chooseFilesButton.OnClicked += async (_, _) => {
			using Gtk.FileFilter imagesFilter = CreateImagesFileFilter ();
			using Gio.ListStore fileFilters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
			fileFilters.Append (imagesFilter);
			using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
			fileDialog.SetTitle (Translations.GetString ("Choose Reference Images"));
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
		PromptOptimizationControls? promptOptimization = CreatePromptOptimizationControls (
			mode,
			promptBuffer,
			promptScroll,
			sourceLayer,
			layerChoices,
			files);

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		content.SetAllMargins (12);
		content.Hexpand = true;
		content.Vexpand = true;
		Gtk.ScrolledWindow contentScroll = Gtk.ScrolledWindow.New ();
		contentScroll.Hexpand = true;
		contentScroll.Vexpand = true;
		contentScroll.MaxContentHeight = spritesheetMode ? 620 : -1;
		contentScroll.PropagateNaturalHeight = false;
		contentScroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		contentScroll.SetChild (content);
		dialog.GetContentAreaBox ().Append (contentScroll);
		content.Append (generationTypeGrid);
		content.Append (generationGrid);
		if (spritesheet_controls is not null)
			content.Append (spritesheet_controls);
		if (sourceLayer is not null && !imageSplitMode) {
			content.Append (CreateDialogLabel (Translations.GetString ("Current Layer")));
			content.Append (sourcePreview);
			content.Append (sourceLabel);
		}
		if (imageSplitPreview is not null)
			content.Append (imageSplitPreview.Widget);
		content.Append (promptOptimization?.Section ?? CreatePromptSection (promptScroll));
		if (doc is not null) {
			content.Append (CreateDialogLabel (Translations.GetString ("Other Reference Layers")));
			content.Append (layerScroll);
		}
		content.Append (CreateDialogLabel (Translations.GetString ("Reference Image Files")));
		content.Append (chooseFilesButton);
		content.Append (fileLabel);

		void UpdateSubmitButton ()
		{
			promptBuffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
			submitButton.Sensitive = (imageSplitPreview?.IsValid ?? !imageSplitMode)
				&& (imageSplitMode || sizePicker.IsValid)
				&& spritesheet_valid ()
				&& !string.IsNullOrWhiteSpace (promptBuffer.GetText (start, end, includeHiddenChars: true));
		}
		promptBuffer.OnChanged += (_, _) => UpdateSubmitButton ();
		sizePicker.Changed += (_, _) => UpdateSubmitButton ();
		UpdateSubmitButton ();

		if (await dialog.RunAsync () != Gtk.ResponseType.Ok)
			return null;
		dialog.Hide ();
		ImageSplitPreviewSelection? splitSelection = imageSplitPreview?.Selection;
		if (imageSplitMode && splitSelection is null)
			return null;

		List<UserLayer> layers = [];
		foreach ((Gtk.CheckButton check, UserLayer layer) in layerChoices)
			if (check.Active)
				layers.Add (layer);

		promptBuffer.GetBounds (out Gtk.TextIter promptStart, out Gtk.TextIter promptEnd);
		string prompt = promptBuffer.GetText (promptStart, promptEnd, includeHiddenChars: true).Trim ();
		prompt = promptOptimization?.GetPrompt (prompt) ?? prompt;
		AI.AiPromptHistoryItem? promptHistory = promptOptimization?.GetPromptHistory ();
		SavePromptHistory (promptHistory);
		Size imageSize = mode switch {
			AiImageRequestMode.BackgroundCleanup => doc!.ImageSize,
			AiImageRequestMode.ImageSplitGeneration => sourceLayer!.Surface.GetSize (),
			_ => sizePicker.SelectedSize ?? throw new InvalidOperationException ("A valid image size is required."),
		};
		if (mode != AiImageRequestMode.BackgroundCleanup) {
			string imageService = serviceCombobox.Active switch {
				0 => AI.AiRequestSettings.AgnesService,
				2 => AI.AiRequestSettings.NanoBananaService,
				_ => AI.AiRequestSettings.GptImageService,
			};
			SaveImageServiceSelection (imageService, providerCombobox, gptProviders, nanoBananaProviders);
			PintaCore.Settings.DoSaveSettingsBeforeQuit ();
		}

		string resultLayerName = imageSplitMode
			? Translations.GetString ("Split Image")
			: spritesheetMode
			? spritesheet_result_layer_name?.Invoke () ?? Translations.GetString ("Spritesheet")
			: mode == AiImageRequestMode.BackgroundCleanup
				? Translations.GetString ("White Background")
				: Translations.GetString ("AI Generated Image");
		string progressTitle = spritesheetMode
			? singleDirectionMode
				? Translations.GetString ("Single-Direction Animation")
				: Translations.GetString ("Generate Spritesheet")
			: Translations.GetString ("AI Image Generation");
		return new (prompt, imageSize, layers, files, resultLayerName, progressTitle, spritesheet_info (), single_direction_info ()) {
			RequestImageSize = splitSelection?.RequestSize,
			WhitePadding = splitSelection?.WhitePadding ?? false,
			PreparedSourcePng = splitSelection?.PreparedSourcePng,
		};
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
		AI.SpritesheetAttemptInfo? Spritesheet,
		AI.SpritesheetAttemptInfo? SingleDirection)
	{
		public UserLayer? ParentLayer { get; init; }
		public UserLayer? SourceLayer { get; init; }
		public Size? RequestImageSize { get; init; }
		public bool WhitePadding { get; init; }
		public byte[]? PreparedSourcePng { get; init; }
	}

	private enum AiImageRequestMode
	{
		BackgroundCleanup,
		ImageGeneration,
		SpritesheetGeneration,
		SingleDirectionAnimationGeneration,
		ImageSplitGeneration,
	}

	private enum AiImageOperation
	{
		GenerateWhite,
		Cutout,
	}
}
