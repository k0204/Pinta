//
// SaveDocumentImplmentationAction.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2010 Jonathan Pobst
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class SaveDocumentImplmentationAction : IActionHandler
{
	const string RESPONSE_CANCEL = "cancel";
	const string RESPONSE_FLATTEN = "flatten";

	private readonly FileActions file;
	private readonly ImageActions image;
	private readonly ChromeManager chrome;
	private readonly ImageConverterManager image_formats;
	private readonly RecentFileManager recent_files;
	private readonly ToolManager tools;
	internal SaveDocumentImplmentationAction (
		FileActions file,
		ImageActions image,
		ChromeManager chrome,
		ImageConverterManager imageFormats,
		RecentFileManager recentFiles,
		ToolManager tools)
	{
		this.file = file;
		this.image = image;
		this.chrome = chrome;
		image_formats = imageFormats;
		recent_files = recentFiles;
		this.tools = tools;
	}

	void IActionHandler.Initialize ()
	{
		file.SaveDocument += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		file.SaveDocument -= Activated;
	}

	private async Task<bool> Activated (FileActions sender, DocumentSaveEventArgs e)
	{
		if (e.SaveAs)
			return await SaveFileAs (e.Document, documentFormatOnly: false);

		// Ctrl+S is document save. Imported images do not become the document's
		// native save target until they have been saved as a .pinta file.
		if (!IsPintaDocument (e.Document))
			return await SaveFileAs (e.Document, documentFormatOnly: true);

		// Document hasn't changed, don't re-save it
		if (!e.Document.IsDirty)
			return true;

		// If the document already has a filename, just re-save it
		return await SaveFile (e.Document, null, null, chrome.MainWindow);
	}

	// This is actually both for "Save As" and saving a file that never
	// been saved before.  Either way, we need to prompt for a filename.
	private async Task<bool> SaveFileAs (Document document, bool documentFormatOnly)
	{
		var fcd = Gtk.FileChooserNative.New (
			documentFormatOnly
				? Translations.GetString ("Save Pinta Document")
				: Translations.GetString ("Save Image File"),
			chrome.MainWindow,
			Gtk.FileChooserAction.Save,
			Translations.GetString ("Save"),
			Translations.GetString ("Cancel"));

		FormatDescriptor pintaFormat = image_formats.GetFormatByExtension ("pinta")
			?? throw new InvalidOperationException ("The Pinta document format is not registered.");

		if (document.HasFile && !documentFormatOnly)
			fcd.SetFile (document.File!);
		else {
			Gio.File? dir = document.File?.GetParent () ?? recent_files.GetDialogDirectory ();
			if (dir is not null && dir.QueryExists (null))
				fcd.SetCurrentFolder (dir);

			string name = System.IO.Path.GetFileNameWithoutExtension (document.DisplayName);
			string extension = documentFormatOnly
				? pintaFormat.Extensions.First ()
				: image_formats.GetDefaultSaveFormat ().Extensions.First ();
			fcd.SetCurrentName ($"{name}.{extension}");
		}

		// Add all the formats we support to the save dialog
		Dictionary<Gtk.FileFilter, FormatDescriptor> filetypes = [];
		foreach (var format in image_formats.Formats) {

			if (!format.IsExportAvailable () || (documentFormatOnly && format != pintaFormat))
				continue;

			fcd.AddFilter (format.Filter);
			filetypes.Add (format.Filter, format);

			// Set the filter to anything we found
			// We want to ensure that *something* is selected in the filetype
			fcd.Filter = format.Filter;
		}

		// If we already have a format, set it to the default.
		// If not, default to jpeg
		FormatDescriptor? format_desc = documentFormatOnly ? pintaFormat : null;

		if (!documentFormatOnly && document.HasFile) {
			format_desc = image_formats.GetFormatByFile (document.DisplayName);
		}

		if (format_desc is null || !format_desc.IsExportAvailable ())
			format_desc = image_formats.GetDefaultSaveFormat ();

		fcd.Filter = format_desc.Filter;

		while (await fcd.RunAsync () == Gtk.ResponseType.Accept) {

			Gio.File file = fcd.GetFile ()!;

			// Note that we can't use file.GetDisplayName() because the file doesn't exist.
			string displayName = file.GetParent ()!.GetRelativePath (file)!;
			if (documentFormatOnly && !displayName.EndsWith (".pinta", StringComparison.OrdinalIgnoreCase)) {
				displayName = System.IO.Path.ChangeExtension (displayName, "pinta");
				file = file.GetParent ()!.GetChild (displayName);
			}

			// Always follow the extension rather than the file type drop down
			// ie: if the user chooses to save a "jpeg" as "foo.png", we are going
			// to assume they just didn't update the dropdown and really want png
			FormatDescriptor? format = documentFormatOnly
				? pintaFormat
				: image_formats.GetFormatByFile (displayName);
			if (format is null) {
				if (fcd.Filter is not null)
					format = filetypes[fcd.Filter];
				else // Somehow, no file filter was selected...
					format = image_formats.GetDefaultSaveFormat ();
			}

			if (!await ConfirmFlatten (document, format)) {
				continue;
			}

			Gio.File? directory = file.GetParent ();

			if (directory is not null)
				recent_files.LastDialogDirectory = directory;

			// If saving the file failed or was cancelled, let the user select
			// a different file type.
			if (!await SaveFile (document, file, format, chrome.MainWindow)) {
				// Re-set the current name and directory
				fcd.SetCurrentName (displayName);
				fcd.SetCurrentFolder (directory);
				continue;
			}

			// Native Pinta documents are fully saved by SaveFile. Keep the existing
			// image-export behavior, where Save As prompts for JPEG quality again.
			if (!format.Extensions.Contains ("pinta", StringComparer.OrdinalIgnoreCase))
				document.HasBeenSavedInSession = false;

			recent_files.AddFile (file);
			image_formats.SetDefaultFormat (format.Extensions.First ());

			document.File = file;
			document.FileType = format.Extensions.First ();
			return true;
		}

		return false;
	}

	private static bool IsPintaDocument (Document document)
		=> document.HasFile
		&& string.Equals (document.FileType, "pinta", StringComparison.OrdinalIgnoreCase);

	private async Task<bool> SaveFile (Document document, Gio.File? file, FormatDescriptor? format, Gtk.Window parent)
	{
		file ??= document.File;

		if (file is null)
			throw new ArgumentException ("Attempted to save a document with no associated file", nameof (file));

		if (format is null) {

			if (string.IsNullOrEmpty (document.FileType))
				throw new ArgumentException ($"{nameof (document.FileType)} must contain value.", nameof (document));

			format = image_formats.GetFormatByExtension (document.FileType);
		}

		if (format is null || !format.IsExportAvailable ()) {

			await chrome.ShowMessageDialog (
				parent,
				Translations.GetString ("Pinta does not support saving images in this file format."),
				file.GetDisplayName ());

			return false;
		}

		if (!await ConfirmFlatten (document, format)) {
			return false;
		}

		// Commit any pending changes
		tools.Commit ();

		try {
			format.Exporter.Export (document, file, parent);
			document.File = file;
			document.FileType = format.Extensions.First ();

		} catch (GLib.GException e) when (e.Message == "Image too large to be saved as ICO") {

			string primary = Translations.GetString ("Image too large");
			string secondary = Translations.GetString ("ICO files can not be larger than 255 x 255 pixels.");

			await chrome.ShowMessageDialog (parent, primary, secondary);

			return false;

		} catch (GLib.GException e) when (e.Message.Contains ("Permission denied") && e.Message.Contains ("Failed to open")) {

			string primary = Translations.GetString ("Failed to save image");

			// Translators: {0} is the name of a file that the user does not have write permission for.
			string secondary = Translations.GetString ("You do not have access to modify '{0}'. The file or folder may be read-only.", file);

			await chrome.ShowMessageDialog (parent, primary, secondary);

			return false;

		} catch (OperationCanceledException) {

			return false;
		} catch (Exception e) {
			await chrome.ShowErrorDialog (
				parent,
				Translations.GetString ("Failed to save image"),
				e.Message,
				e.ToString ());
			return false;
		}

		tools.DoAfterSave (document);

		// Mark the document as clean following the tool's after-save handler, which might
		// adjust history (e.g. undo changes that were committed before saving).
		document.Workspace.History.SetClean ();

		//Now the Document has been saved to the file it's associated with in this session.
		document.HasBeenSavedInSession = true;

		return true;
	}

	private async Task<bool> ConfirmFlatten (Document document, FormatDescriptor format)
	{
		// If the format doesn't support layers but there is more than one layer, ask to flatten the image
		if (!format.SupportsLayers
			&& document.Layers.Count () > 1) {

			string heading = Translations.GetString ("This format does not support layers. Flatten image?");
			string body = Translations.GetString ("Flattening the image will merge all layers into a single layer.");

			using Adw.MessageDialog dialog = Adw.MessageDialog.New (chrome.MainWindow, heading, body);
			dialog.AddResponse (RESPONSE_CANCEL, Translations.GetString ("_Cancel"));
			dialog.AddResponse (RESPONSE_FLATTEN, Translations.GetString ("Flatten"));
			dialog.SetResponseAppearance (RESPONSE_FLATTEN, Adw.ResponseAppearance.Suggested);

			dialog.CloseResponse = RESPONSE_CANCEL;
			dialog.DefaultResponse = RESPONSE_FLATTEN;

			string response = await dialog.RunAsync ();

			if (response == RESPONSE_CANCEL) {
				return false;
			}

			// Flatten the image
			tools.Commit ();
			image.Flatten.Activate ();
		}
		return true;
	}
}
