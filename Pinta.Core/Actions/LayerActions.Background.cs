//
// LayerActions.Background.cs
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
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

	private async Task RunBackgroundCleanupAsync ()
	{

		Document doc = workspace.ActiveDocument;
		UserLayer sourceLayer = doc.Layers.CurrentUserLayer;
		if (!sourceLayer.IsEditable)
			return;

		BackgroundCleanupOptions? options = await PromptBackgroundCleanupAsync (doc, sourceLayer);
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

			SaveCutoutDebugLog (debugDir, $"AI background cleanup client: document_size={doc.ImageSize.Width}x{doc.ImageSize.Height}, source_layer={sourceLayer.Name}, source_bytes={sourcePng.Length}, references={references.Count}");
			SaveCutoutDebugPng (debugDir, "source.png", sourcePng);
			byte[] whitePng = await GenerateBackgroundWithRetryAsync (
				Translations.GetString ("清理背景"),
				() => background_cutout.GenerateWhiteAsync (
					sourcePng,
					doc.ImageSize,
					options.AdditionalPrompt,
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
			byte[] sourcePng = CreateLayerPng (doc, sourceLayer);
			string imageService = AI.AiRequestSettings.GetImageService (PintaCore.Settings);
			string debugDir = CreateCutoutDebugDirectory ();
			SaveCutoutDebugLog (debugDir, $"AI cutout: service={imageService}, document_size={doc.ImageSize.Width}x{doc.ImageSize.Height}, source_layer={sourceLayer.Name}, source_bytes={sourcePng.Length}");
			SaveCutoutDebugPng (debugDir, "source.png", sourcePng);
			if (imageService == AI.AiRequestSettings.BaiduService) {
				SetProgress (Translations.GetString ("Requesting Baidu human segmentation..."), 0.25);
				byte[] transparentPng = await GenerateBackgroundWithRetryAsync (
					Translations.GetString ("Cutout"),
					() => background_cutout.GenerateBaiduCutoutAsync (
						sourcePng,
						doc.ImageSize,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
						message => SaveCutoutDebugLog (debugDir, message),
						cts.Token),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token);

				SetProgress (Translations.GetString ("Creating transparent layer..."), 0.85);
				UserLayer cutoutLayer = doc.Layers.AddNewLayer (Translations.GetString ("Transparent Cutout"));
				DrawPngOnLayer (transparentPng, cutoutLayer);
				doc.History.PushNewItem (new AddLayerHistoryItem (
					Resources.Icons.ColorModeTransparency,
					Translations.GetString ("Transparent Cutout"),
					cutoutLayer,
					doc.Layers.GetPosition (cutoutLayer)));
			} else {
				byte[] blackPng = await GenerateBackgroundWithRetryAsync (
					Translations.GetString ("Cutout"),
					() => background_cutout.GenerateBlackAsync (
						sourcePng,
						doc.ImageSize,
						SetProgress,
						(fileName, png) => SaveCutoutDebugPng (debugDir, fileName, png),
						message => SaveCutoutDebugLog (debugDir, message),
						cts.Token),
					message => SaveCutoutDebugLog (debugDir, message),
					cts.Token);

				SetProgress (Translations.GetString ("Creating black and transparent layers..."), 0.85);
				CompoundHistoryItem history = new (Resources.Icons.ColorModeTransparency, Translations.GetString ("Cutout"));
				UserLayer blackLayer = doc.Layers.AddNewLayer (Translations.GetString ("Black Background"));
				DrawPngOnLayer (blackPng, blackLayer);
				history.Push (new AddLayerHistoryItem (
					Resources.Icons.ColorModeColor,
					Translations.GetString ("Black Background"),
					blackLayer,
					doc.Layers.GetPosition (blackLayer)));

				using Cairo.ImageSurface white = LoadPngAsSurface (sourcePng, doc.ImageSize);
				using Cairo.ImageSurface black = LoadPngAsSurface (blackPng, doc.ImageSize);
				UserLayer cutoutLayer = doc.Layers.AddNewLayer (Translations.GetString ("Transparent Cutout"));
				CreateTransparentCutout (white, black, cutoutLayer.Surface);
				history.Push (new AddLayerHistoryItem (
					Resources.Icons.ColorModeTransparency,
					Translations.GetString ("Transparent Cutout"),
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

	private async Task<BackgroundCleanupOptions?> PromptBackgroundCleanupAsync (Document doc, UserLayer sourceLayer)
	{
		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("清理背景");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = false;
		dialog.DefaultWidth = 520;
		dialog.DefaultHeight = 480;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget cleanupButton = dialog.AddButton (Translations.GetString ("清理背景"), (int) Gtk.ResponseType.Ok);
		cleanupButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.Entry promptEntry = Gtk.Entry.New ();
		promptEntry.Hexpand = true;
		promptEntry.PlaceholderText = Translations.GetString ("添加需要补充的背景清理要求");

		Gtk.Picture sourcePreview = Gtk.Picture.New ();
		sourcePreview.Paintable = sourceLayer.Surface.ToTexture ();
		sourcePreview.ContentFit = Gtk.ContentFit.ScaleDown;
		sourcePreview.SetSizeRequest (160, 100);
		Gtk.Label sourceLabel = Gtk.Label.New (
			$"{GetLayerPath (sourceLayer)}  {sourceLayer.Surface.Width} × {sourceLayer.Surface.Height} px");
		sourceLabel.Halign = Gtk.Align.Start;

		Gtk.Box layerBox = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
		List<(Gtk.CheckButton Button, UserLayer Layer)> layerChoices = [];
		foreach (UserLayer layer in doc.Layers.AllLayers) {
			if (layer == sourceLayer || layer is GroupLayer || layer.ReferenceMissing)
				continue;

			Gtk.CheckButton check = Gtk.CheckButton.New ();
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
		content.Append (CreateDialogLabel (Translations.GetString ("当前图层")));
		content.Append (sourcePreview);
		content.Append (sourceLabel);
		content.Append (CreateDialogLabel (Translations.GetString ("附加提示词")));
		content.Append (promptEntry);
		content.Append (CreateDialogLabel (Translations.GetString ("其他参考图层")));
		content.Append (layerScroll);
		content.Append (CreateDialogLabel (Translations.GetString ("参考图片文件")));
		content.Append (chooseFilesButton);
		content.Append (fileLabel);

		if (await dialog.RunAsync () != Gtk.ResponseType.Ok)
			return null;

		List<UserLayer> layers = [];
		foreach ((Gtk.CheckButton check, UserLayer layer) in layerChoices)
			if (check.Active)
				layers.Add (layer);

		return new (promptEntry.GetText ().Trim (), layers, files);
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

	private sealed record BackgroundCleanupOptions (
		string AdditionalPrompt,
		IReadOnlyList<UserLayer> Layers,
		IReadOnlyList<Gio.File> Files);

	private enum AiImageOperation
	{
		GenerateWhite,
		Cutout,
	}
}
