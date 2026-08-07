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
	internal OpenDocumentAction (
		FileActions file,
		ChromeManager chrome,
		WorkspaceManager workspace,
		RecentFileManager recentFiles)
	{
		this.file = file;
		this.chrome = chrome;
		this.workspace = workspace;
		recent_files = recentFiles;
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
		using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
		fileDialog.SetTitle (Translations.GetString ("Open Pinta Project"));
		fileDialog.Modal = true;

		if (recent_files.GetDialogDirectory () is Gio.File dir && dir.QueryExists (null))
			fileDialog.SetInitialFolder (dir);

		Gio.File? project = await fileDialog.SelectFolderAsync (chrome.MainWindow);
		if (project is null)
			return;

		if (!project.GetDisplayName ().EndsWith ($".{PintaDocumentFormat.Extension}", StringComparison.OrdinalIgnoreCase)) {
			await chrome.ShowMessageDialog (
				chrome.MainWindow,
				Translations.GetString ("This folder is not a Pinta project."),
				Translations.GetString ("Select a folder ending in .pintaproject."));
			return;
		}

		if (workspace.OpenFile (project)) {
			recent_files.AddFile (project);
			recent_files.LastDialogDirectory = project.GetParent ();
		}
	}
}
