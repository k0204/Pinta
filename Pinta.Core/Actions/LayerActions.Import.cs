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

		try {
			ImportFile (workspace.ActiveDocument, choice);
		} catch (Exception exception) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Failed to open image"),
				exception.Message,
				exception.ToString ());
		}
	}

	public void ImportFile (Document document, Gio.File file, PointD? center = null)
	{
		tools.Commit ();
		using Cairo.ImageSurface image = file.LoadImageSurface ();
		UserLayer layer = document.Layers.AddNewLayer (file.GetDisplayName ());
		layer.Surface.Dispose ();
		layer.Surface = image.Clone ();

		PointD position = center is PointD dropCenter
			? dropCenter - new PointD (image.Width / 2.0, image.Height / 2.0)
			: PointD.Zero;
		Cairo.Matrix transform = CairoExtensions.CreateIdentityMatrix ();
		transform.Translate (position.X, position.Y);
		layer.Transform = transform;

		AddLayerHistoryItem history = new (
			Resources.Icons.LayerImport,
			Translations.GetString ("Import From File"),
			layer,
			document.Layers.GetPosition (layer));

		document.Layers.SetCurrentUserLayer (layer);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

}

