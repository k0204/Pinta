// 
// OpenDocumentAction.cs
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
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class OpenDocumentAction : IActionHandler
{
	private readonly FileActions file;
	private readonly ChromeManager chrome;
	private readonly WorkspaceManager workspace;
	private readonly RecentFileManager recent_files;
	private readonly ImageConverterManager image_formats;
	internal OpenDocumentAction (
		FileActions file,
		ChromeManager chrome,
		WorkspaceManager workspace,
		RecentFileManager recentFiles,
		ImageConverterManager imageFormats)
	{
		this.file = file;
		this.chrome = chrome;
		this.workspace = workspace;
		recent_files = recentFiles;
		image_formats = imageFormats;
	}

	void IActionHandler.Initialize ()
	{
		file.Open.Activated += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		file.Open.Activated -= Activated;
	}

	private async void Activated (object sender, EventArgs e)
	{
		using Gtk.FileFilter pintaFilter = CreatePintaFilter ();
		using Gtk.FileFilter supportedFilesFilter = CreateSupportedFilesFilter ();
		using Gtk.FileFilter catchAllFilter = CreateCatchAllFilter ();

		using Gio.ListStore filters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		filters.Append (pintaFilter);
		filters.Append (supportedFilesFilter);
		filters.Append (catchAllFilter);

		using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
		fileDialog.SetTitle (Translations.GetString ("Open Pinta Document or Image"));
		fileDialog.SetFilters (filters);
		fileDialog.SetDefaultFilter (pintaFilter);
		fileDialog.Modal = true;

		if (recent_files.GetDialogDirectory () is Gio.File dir && dir.QueryExists (null))
			fileDialog.SetInitialFolder (dir);

		var selection = await fileDialog.OpenFilesAsync (chrome.MainWindow);

		if (selection is null)
			return;

		foreach (var file in selection) {

			if (!workspace.OpenFile (file))
				continue;

			recent_files.AddFile (file);

			Gio.File? directory = file.GetParent ();

			if (directory is not null)
				recent_files.LastDialogDirectory = directory;
		}
	}

	private Gtk.FileFilter CreatePintaFilter ()
	{
		FormatDescriptor format = image_formats.GetFormatByExtension ("pinta")
			?? throw new InvalidOperationException ("The Pinta document format is not registered.");
		Gtk.FileFilter result = Gtk.FileFilter.New ();
		result.Name = Translations.GetString ("Pinta documents");
		foreach (string extension in format.Extensions)
			result.AddPattern ($"*.{extension}");
		return result;
	}

	private static Gtk.FileFilter CreateCatchAllFilter ()
	{
		Gtk.FileFilter result = Gtk.FileFilter.New ();
		result.Name = Translations.GetString ("All files");
		result.AddPattern ("*");
		return result;
	}

	private Gtk.FileFilter CreateSupportedFilesFilter ()
	{
		Gtk.FileFilter result = Gtk.FileFilter.New ();

		result.Name = Translations.GetString ("Supported files");

		foreach (var format in image_formats.Formats) {

			if (!format.IsImportAvailable ())
				continue;

			foreach (var ext in format.Extensions)
				result.AddPattern ($"*.{ext}");

			// On Unix-like systems, file extensions are often considered optional.
			// Files can often also be identified by their MIME types.
			// Windows does not understand MIME types natively.
			// Adding a MIME filter on Windows would break the native file picker and force a GTK file picker instead.
			if (SystemManager.GetOperatingSystem () != OS.Windows) {
				foreach (var mime in format.Mimes)
					result.AddMimeType (mime);
			}
		}

		return result;
	}
}
