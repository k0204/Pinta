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
			VideoGenerationRequestOptions? request = await PromptVideoRequestAsync (layer);
			if (request is not null)
				await GenerateVideoAsync (document, layer, request);
		} finally {
			video_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
		}
	}

	private async Task GenerateVideoAsync (
		Document document,
		UserLayer layer,
		VideoGenerationRequestOptions request)
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
			SetProgress (Translations.GetString ("Generating video..."), 0.25);
			using JsonDocument result = await SubmitVideoRequestAsync (layer, request, cts.Token);
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
			if (layer is VideoEditingLayer videoLayer && file.GetPath () is string videoPath) {
				videoLayer.VideoPath = videoPath;
				document.IsDirty = true;
				document.Layers.NotifyLayerTreeChanged ();
			}
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

	private Task<JsonDocument> SubmitVideoRequestAsync (
		UserLayer layer,
		VideoGenerationRequestOptions request,
		CancellationToken cancellationToken)
	{
		Dictionary<string, object> parameters = CreateVideoParameters (request);
		List<(byte[] Data, string FileName)> references = [(CreateLayerPng (layer), "pinta.png")];
		foreach (Gio.File file in request.ReferenceFiles)
			references.Add (LoadReferenceImage (file));
		return video_jobs.RunVideoFromImageAsync (
			references,
			request.Prompt,
			request.Provider.Id,
			request.Model,
			GetVideoModeValue (request.Mode),
			parameters,
			cancellationToken: cancellationToken);
	}

	private static Dictionary<string, object> CreateVideoParameters (VideoGenerationRequestOptions request)
	{
		Dictionary<string, object> parameters = new () {
			["resolution"] = request.Resolution,
			["duration"] = request.Duration,
			["watermark"] = request.Watermark,
			["ratio"] = request.Ratio,
			["audio"] = request.Audio,
		};
		return parameters;
	}

	private static string GetVideoModeValue (VideoGenerationMode mode)
		=> mode switch {
			VideoGenerationMode.FirstFrame => "first_frame",
			VideoGenerationMode.FirstLastFrame => "first_last_frame",
			VideoGenerationMode.MultiImage => "multi_image",
			_ => throw new ArgumentOutOfRangeException (nameof (mode)),
		};

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
