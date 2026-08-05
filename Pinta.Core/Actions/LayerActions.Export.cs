//
// LayerActions.Export.cs
//

using System;
using System.IO;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandleSaveLayerImageActivated (object sender, EventArgs e)
	{
		if (!workspace.HasOpenDocuments || save_layer_running)
			return;

		save_layer_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		try {
			await SaveLayerImageAsync ();
		} finally {
			save_layer_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
		}
	}

	private async Task SaveLayerImageAsync ()
	{
		try {
			Document document = workspace.ActiveDocument;
			UserLayer layer = document.Layers.CurrentUserLayer;
			tools.Commit ();

			using Gtk.FileChooserNative chooser = Gtk.FileChooserNative.New (
				Translations.GetString ("Save Layer Image"),
				chrome.MainWindow,
				Gtk.FileChooserAction.Save,
				Translations.GetString ("Save"),
				Translations.GetString ("Cancel"));

			Gtk.FileFilter filter = Gtk.FileFilter.New ();
			filter.AddPattern ("*.png");
			filter.Name = Translations.GetString ("PNG image (*.png)");
			chooser.AddFilter (filter);
			chooser.Filter = filter;
			SetInitialSaveLocation (chooser, layer);

			if (await chooser.RunAsync () != Gtk.ResponseType.Accept)
				return;

			Gio.File file = chooser.GetFile ()!;
			IProgressDialog progressDialog = chrome.ProgressDialog;
			progressDialog.Title = Translations.GetString ("Saving Layer Image");
			progressDialog.Text = layer.Name;
			progressDialog.Progress = 0;
			progressDialog.Cancellable = false;
			progressDialog.Show ();
			chrome.MainWindowBusy = true;
			try {
				IProgress<double> progress = new Progress<double> (value => progressDialog.Progress = Math.Clamp (value, 0, 1));
				await Task.Run (() => SaveLayerPng (document, layer, file, progress));
			} finally {
				progressDialog.Cancellable = true;
				progressDialog.Hide ();
				chrome.MainWindowBusy = false;
			}
			recent_files.LastDialogDirectory = file.GetParent ();
		} catch (OperationCanceledException) {
			return;
		} catch (Exception exception) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Failed to save image"),
				exception.Message,
				exception.ToString ());
		}
	}

	private void SetInitialSaveLocation (Gtk.FileChooserNative chooser, UserLayer layer)
	{
		if (recent_files.GetDialogDirectory () is Gio.File directory && directory.QueryExists (null))
			chooser.SetCurrentFolder (directory);

		string name = string.IsNullOrWhiteSpace (layer.Name) ? "Layer" : layer.Name;
		chooser.SetCurrentName ($"{name}.png");
	}

	private static void SaveLayerPng (
		Document document,
		UserLayer layer,
		Gio.File file,
		IProgress<double> progress)
	{
		using ImageSurface image = RenderLayer (document, layer, progress);
		progress.Report (0.85);
		string temporaryFile = System.IO.Path.GetTempFileName ();
		try {
			CairoExtensions.SaveToPng (image, temporaryFile);
			progress.Report (0.95);
			using FileStream source = File.OpenRead (temporaryFile);
			using GioStream destination = new (file.Replace ());
			source.CopyTo (destination);
			progress.Report (1);
		} finally {
			File.Delete (temporaryFile);
		}
	}

}
