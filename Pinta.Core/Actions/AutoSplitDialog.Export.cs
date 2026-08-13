using System;
using System.IO;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal sealed partial class AutoSplitDialog
{
	private async Task ExportAllRegionsAsync ()
	{
		using Gtk.FileDialog picker = Gtk.FileDialog.New ();
		picker.SetTitle (Translations.GetString ("Choose export folder"));
		Gio.File? folder = await picker.SelectFolderAsync (dialog);
		string? directory = folder?.GetPath ();
		if (directory is null)
			return;

		try {
			Directory.CreateDirectory (directory);
			for (int index = 0; index < regions.Count; index++)
				ExportRegion (directory, regions[index].Bounds, index);

			status_label.RemoveCssClass (AdwaitaStyles.Error);
			status_label.SetText (Translations.GetString ("Exported {0} regions.", regions.Count));
		} catch (Exception ex) {
			status_label.AddCssClass (AdwaitaStyles.Error);
			status_label.SetText (Translations.GetString ("Failed to export regions: {0}", ex.Message));
		}
	}

	private void ExportRegion (string directory, RectangleI bounds, int index)
	{
		using ImageSurface output = CairoExtensions.CreateImageSurface (Format.Argb32, bounds.Width, bounds.Height);
		using (Context context = new (output)) {
			context.SetSourceSurface (source.Surface, -bounds.X, -bounds.Y);
			context.Paint ();
		}

		output.SaveToPng (System.IO.Path.Combine (directory, $"region-{index + 1:D3}.png"));
	}
}
