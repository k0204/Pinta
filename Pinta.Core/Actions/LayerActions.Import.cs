//
// LayerActions.Import.cs
//

using System;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private void HandleUnlockReferenceActivated (object sender, EventArgs e)
	{
		Document document = workspace.ActiveDocument;
		UserLayer layer = document.Layers.CurrentUserLayer;
		if (!layer.IsReference || layer.ReferenceMissing)
			return;

		string previousPath = layer.ReferencePath!;
		layer.ReferencePath = null;
		layer.ReferenceMissing = false;
		document.History.PushNewItem (new UpdateLayerReferenceHistoryItem (document, layer, previousPath, null));
		document.Layers.NotifyLayerTreeChanged ();
		document.Workspace.Invalidate ();
	}

	private Gtk.FileFilter CreateImagesFileFilter ()
	{
		Gtk.FileFilter imagesFilter = Gtk.FileFilter.New ();
		foreach (var format in image_formats.Formats) {
			if (!format.IsImportAvailable ()) continue;
			foreach (string ext in format.Extensions)
				imagesFilter.AddPattern ($"*.{ext}");
		}

		// On Unix-like systems, file extensions are often considered optional.
		// Files can often also be identified by their MIME types.
		// Windows does not understand MIME types natively.
		// Adding a MIME filter on Windows would break the native file picker and force a GTK file picker instead.
		if (SystemManager.GetOperatingSystem () != OS.Windows)
			foreach (var format in image_formats.Formats)
				foreach (var mime in format.Mimes)
					imagesFilter.AddMimeType (mime);

		imagesFilter.Name = Translations.GetString ("Image files");

		return imagesFilter;
	}

	private async void HandlePintaCoreActionsLayersImportFromFileActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;

		tools.Commit ();

		// Add image files filter
		using Gtk.FileFilter imagesFilter = CreateImagesFileFilter ();

		using Gio.ListStore fileFilters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		fileFilters.Append (imagesFilter);

		using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
		fileDialog.SetTitle (Translations.GetString ("Open Image File"));
		fileDialog.SetFilters (fileFilters);
		if (recent_files.GetDialogDirectory () is Gio.File dir && dir.QueryExists (null))
			fileDialog.SetInitialFolder (dir);

		Gio.File? choice = await fileDialog.OpenFileAsync (chrome.MainWindow);

		if (choice is null) return;

		Gio.File? directory = choice.GetParent ();

		if (directory is not null)
			recent_files.LastDialogDirectory = directory;

		// Open the image and add it to the layers
		UserLayer layer = doc.Layers.AddNewLayer (choice.GetDisplayName ());

		using (Gio.FileInputStream fs = choice.Read (null)) {
			try {
				using GdkPixbuf.Pixbuf bg = GdkPixbuf.Pixbuf.NewFromStream (fs, cancellable: null)!; // NRT: only nullable when an error is thrown
				using Cairo.Context context = new (layer.Surface);
				context.DrawPixbuf (bg, PointD.Zero);
			} finally {
				fs.Close (null);
			}
		}

		AddLayerHistoryItem hist = new (
			Resources.Icons.LayerImport,
			Translations.GetString ("Import From File"),
			layer,
			doc.Layers.GetPosition (layer));

		// --- Changes to document go after everything else is completed successfully

		doc.Layers.SetCurrentUserLayer (layer);
		doc.History.PushNewItem (hist);
		doc.Workspace.Invalidate ();
	}

}

