//
// LayerActions.Video.cs
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandleGenerateVideoActivated (object sender, EventArgs e)
	{
		if (video_running || !EnsureAiLoggedIn ()
			|| workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.HasSelectedLayer)
			return;

		video_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		try {
			UserLayer layer = document.Layers.CurrentUserLayer;
			(string Prompt, IReadOnlyList<Gio.File> ReferenceFiles)? request =
				await PromptVideoRequestAsync (layer);
			if (request is not null)
				await GenerateVideoAsync (layer, request.Value.Prompt, request.Value.ReferenceFiles);
		} finally {
			video_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
		}
	}

	private async Task<(string Prompt, IReadOnlyList<Gio.File> ReferenceFiles)?> PromptVideoRequestAsync (UserLayer layer)
	{
		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Generate Video");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = 520;

		Gtk.Label sourceLabel = Gtk.Label.New (
			Translations.GetString ("Source layer: {0}", layer.Name));
		sourceLabel.Halign = Gtk.Align.Start;

		Gtk.TextView promptView = Gtk.TextView.New ();
		promptView.WrapMode = Gtk.WrapMode.WordChar;
		promptView.SetSizeRequest (-1, 130);
		promptView.Buffer!.SetText (string.Empty, -1);

		List<Gio.File> referenceFiles = [];
		Gtk.Label referenceLabel = Gtk.Label.New (Translations.GetString ("No files selected"));
		referenceLabel.Halign = Gtk.Align.Start;
		referenceLabel.Wrap = true;
		Gtk.Button chooseReferencesButton = Gtk.Button.NewWithLabel (
			Translations.GetString ("Choose Reference Images"));
		chooseReferencesButton.Halign = Gtk.Align.Start;
		chooseReferencesButton.OnClicked += async (_, _) => {
			using Gtk.FileFilter imagesFilter = CreateImagesFileFilter ();
			using Gio.ListStore fileFilters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
			fileFilters.Append (imagesFilter);
			using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
			fileDialog.SetTitle (Translations.GetString ("Choose Reference Images"));
			fileDialog.SetFilters (fileFilters);
			if (recent_files.GetDialogDirectory () is Gio.File directory && directory.QueryExists (null))
				fileDialog.SetInitialFolder (directory);

			IReadOnlyList<Gio.File>? choices = await fileDialog.OpenFilesAsync (dialog);
			if (choices is null)
				return;

			referenceFiles.Clear ();
			referenceFiles.AddRange (choices);
			referenceLabel.SetText (referenceFiles.Count == 0
				? Translations.GetString ("No files selected")
				: string.Join (", ", referenceFiles.ConvertAll (file => file.GetDisplayName ())));
			if (referenceFiles.Count > 0 && referenceFiles[0].GetParent () is Gio.File parent)
				recent_files.LastDialogDirectory = parent;
		};

		Gtk.Box content = dialog.GetContentAreaBox ();
		content.Spacing = 8;
		content.SetAllMargins (12);
		content.Append (sourceLabel);
		content.Append (chooseReferencesButton);
		content.Append (referenceLabel);
		content.Append (Gtk.Label.New (Translations.GetString ("Video prompt:")));
		content.Append (promptView);

		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		dialog.AddButton (Translations.GetString ("_Generate"), (int) Gtk.ResponseType.Ok);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.ResponseType response = await dialog.RunAsync ();
		dialog.Hide ();
		if (response != Gtk.ResponseType.Ok)
			return null;

		Gtk.TextBuffer buffer = promptView.Buffer!;
		buffer.GetBounds (out Gtk.TextIter start, out Gtk.TextIter end);
		string prompt = buffer.GetText (start, end, includeHiddenChars: true).Trim ();
		if (!string.IsNullOrWhiteSpace (prompt))
			return (prompt, referenceFiles);

		await chrome.ShowMessageDialog (
			chrome.MainWindow,
			Translations.GetString ("Generate Video"),
			Translations.GetString ("Enter a video prompt before generating."));
		return null;
	}

	private async Task GenerateVideoAsync (
		UserLayer layer,
		string prompt,
		IReadOnlyList<Gio.File> referenceFiles)
	{
		using CancellationTokenSource cts = new ();
		IProgressDialog progress = chrome.ProgressDialog;
		progress.Title = Translations.GetString ("Generate Video");
		progress.Text = Translations.GetString ("Preparing video request...");
		progress.Progress = 0.05;
		progress.Cancellable = true;
		progress.Canceled += HandleProgressCanceled;
		progress.Show ();
		chrome.MainWindowBusy = true;
		bool clearStatus = true;

		try {
			SetProgress (Translations.GetString ("Preparing source layer..."), 0.15);
			List<(byte[] Data, string FileName)> references = [(CreateLayerPng (layer), "pinta.png")];
			foreach (Gio.File referenceFile in referenceFiles)
				references.Add (LoadReferenceImage (referenceFile));
			SetProgress (Translations.GetString ("Generating video..."), 0.25);

			using JsonDocument result = await video_jobs.RunVideoFromImageAsync (
				references,
				prompt,
				cancellationToken: cts.Token);
			string videoUrl = ReadVideoUrl (result.RootElement);

			SetProgress (Translations.GetString ("Downloading generated video..."), 0.75);
			byte[] video = await video_jobs.DownloadAsync (videoUrl, cts.Token);

			progress.Hide ();
			chrome.MainWindowBusy = false;
			Gio.File? file = await ChooseVideoFileAsync (layer);
			if (file is null) {
				await RefreshVideoBalanceAsync (cts.Token);
				clearStatus = false;
				chrome.SetStatusBarText (Translations.GetString ("Video generation canceled."));
				return;
			}

			progress.Show ();
			chrome.MainWindowBusy = true;
			SetProgress (Translations.GetString ("Saving video..."), 0.9);
			await Task.Run (() => SaveVideo (file, video), cts.Token);
			recent_files.LastDialogDirectory = file.GetParent ();
			SetProgress (Translations.GetString ("Refreshing balance..."), 0.95);
			await RefreshVideoBalanceAsync (cts.Token);
			SetProgress (Translations.GetString ("Video generation complete."), 1.0);
		} catch (OperationCanceledException) {
			clearStatus = false;
			chrome.SetStatusBarText (Translations.GetString ("Video generation canceled."));
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				progress.Window,
				Translations.GetString ("Video Generation Failed"),
				Translations.GetString ("Check the selected layer, API server logs, balance, and login status, then try again."),
				ex.ToString ());
		} finally {
			progress.Canceled -= HandleProgressCanceled;
			progress.Hide ();
			chrome.MainWindowBusy = false;
			progress.Cancellable = true;
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

	private static async Task RefreshVideoBalanceAsync (CancellationToken cancellationToken)
	{
		await PintaCore.AiAuth.RefreshAccountSummaryAsync (cancellationToken);
		PintaCore.Settings.DoSaveSettingsBeforeQuit ();
	}

	private async Task<Gio.File?> ChooseVideoFileAsync (UserLayer layer)
	{
		using Gtk.FileChooserNative chooser = Gtk.FileChooserNative.New (
			Translations.GetString ("Save Generated Video"),
			chrome.MainWindow,
			Gtk.FileChooserAction.Save,
			Translations.GetString ("Save"),
			Translations.GetString ("Cancel"));

		Gtk.FileFilter filter = Gtk.FileFilter.New ();
		filter.AddPattern ("*.mp4");
		filter.Name = Translations.GetString ("MP4 video (*.mp4)");
		chooser.AddFilter (filter);
		chooser.Filter = filter;
		if (recent_files.GetDialogDirectory () is Gio.File directory && directory.QueryExists (null))
			chooser.SetCurrentFolder (directory);

		string name = string.IsNullOrWhiteSpace (layer.Name)
			? Translations.GetString ("Generated Video")
			: layer.Name;
		chooser.SetCurrentName ($"{name}.mp4");
		if (await chooser.RunAsync () != Gtk.ResponseType.Accept)
			return null;

		Gio.File file = chooser.GetFile ()!;
		string basename = file.GetParent ()!.GetRelativePath (file)!;
		if (string.IsNullOrEmpty (Path.GetExtension (basename)))
			file = file.GetParent ()!.GetChild ($"{basename}.mp4");
		return file;
	}

	private static string ReadVideoUrl (JsonElement root)
	{
		if (root.TryGetProperty ("video_url", out JsonElement value)
			&& value.GetString () is string url
			&& Uri.TryCreate (url, UriKind.Absolute, out Uri? uri)
			&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
			return uri.AbsoluteUri;

		throw new InvalidOperationException (
			Translations.GetString ("Video response did not include a video URL."));
	}

	private static void SaveVideo (Gio.File file, byte[] video)
	{
		using GioStream destination = new (file.Replace ());
		destination.Write (video, 0, video.Length);
	}
}
