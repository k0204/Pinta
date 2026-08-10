using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private async void HandleChooseFolderClicked (object sender, EventArgs args)
	{
		using Gtk.FileDialog dialog = Gtk.FileDialog.New ();
		dialog.SetTitle (Translations.GetString ("Choose output folder"));
		Gio.File? folder = await dialog.SelectFolderAsync (this);
		string? path = folder?.GetPath ();
		if (!string.IsNullOrWhiteSpace (path))
			outputFolderEntry.SetText (path);
	}

	private async void HandleExportClicked (object sender, EventArgs args)
	{
		if (videoFilename is null || metadata is null)
			return;

		export_cts?.Cancel ();
		export_cts?.Dispose ();
		export_cts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		CancellationToken cancellationToken = export_cts.Token;
		bool exportAll = allFramesButton.Active;
		int count = exportAll ? metadata.TotalFrames : selectedIndices.Count;
		string outputDirectory = outputFolderEntry.GetText ().Trim ();
		int digits = (int) digitsSpinner.GetValue ();

		exportProgress.Fraction = 0.05;
		exportProgress.Text = Translations.GetString ("Preparing export...");
		exportProgress.Show ();
		cancelExportButton.Show ();
		exportButton.Hide ();

		try {
			exportProgress.Fraction = 0.15;
			exportProgress.Text = Translations.GetString ("Exporting {0} frames...", count);
			await VideoFrameExportProcess.ExportAsync (
				videoFilename,
				metadata,
				selectedIndices.ToArray (),
				exportAll,
				outputDirectory,
				prefixEntry.GetText (),
				digits,
				cancellationToken);
			exportProgress.Fraction = 1;
			exportProgress.Text = Translations.GetString ("Export complete: {0} frames", count);
		} catch (OperationCanceledException) {
			exportProgress.Fraction = 0;
			exportProgress.Text = Translations.GetString ("Export canceled.");
		} catch (VideoFrameExportException ex) {
			exportProgress.Fraction = 0;
			exportProgress.Text = ex.Message;
		} catch (Exception ex) {
			exportProgress.Fraction = 0;
			exportProgress.Text = Translations.GetString ("Export failed.");
			Console.Error.WriteLine (ex);
		} finally {
			cancelExportButton.Hide ();
			exportButton.Show ();
			UpdateExportState ();
		}
	}
}
