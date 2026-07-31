//
// PasteAction.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//       Cameron White <cameronwhite91@gmail.com>
//
// Copyright (c) 2012 Jonathan Pobst, Cameron White
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
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class PasteAction : IActionHandler
{
	private readonly ChromeManager chrome;
	private readonly ActionManager actions;
	private readonly WorkspaceManager workspace;
	private readonly ToolManager tools;
	internal PasteAction (
		ChromeManager chrome,
		ActionManager actions,
		WorkspaceManager workspace,
		ToolManager tools)
	{
		this.chrome = chrome;
		this.actions = actions;
		this.workspace = workspace;
		this.tools = tools;
	}

	void IActionHandler.Initialize ()
	{
		actions.Edit.Paste.Activated += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		actions.Edit.Paste.Activated -= Activated;
	}

	private void Activated (object sender, EventArgs e)
	{
		// If no documents are open, activate the
		// PasteIntoNewImage action and abort this Paste action.
		if (!workspace.HasOpenDocuments) {
			actions.Edit.PasteIntoNewImage.Activate ();
			return;
		}

		var doc = workspace.ActiveDocument;
		if (!doc.Layers.CurrentUserLayer.IsEditable)
			return;

		// Get the scroll position in canvas coordinates
		Gtk.Viewport view = doc.Workspace.Viewport;

		PointD viewPoint = new (
			X: view.Hadjustment!.Value,
			Y: view.Vadjustment!.Value);

		PointD canvasPos = doc.Workspace.ViewPointToCanvas (viewPoint);

		// Allow the current tool to consume text; image paste reuses only an empty layer.
		Paste (
			actions: actions,
			chrome: chrome,
			workspace: workspace,
			tools: tools,
			doc: doc,
			toNewLayer: false,
			pastePosition: canvasPos.ToInt ()
		);
	}

	/// <summary>
	/// Pastes an image from the clipboard.
	/// </summary>
	/// <param name="toNewLayer">Whether to always create a new layer. Otherwise, an empty
	/// current layer is reused.</param>
	/// <param name="x">Optional. Location within image to paste to.
	/// Position will be adjusted if pasted image would hang
	/// over right or bottom edges of canvas.</param>
	/// <param name="y">Optional. Location within image to paste to.
	/// Position will be adjusted if pasted image would hang
	/// over right or bottom edges of canvas.</param>
	public static async void Paste (
		ActionManager actions,
		ChromeManager chrome,
		WorkspaceManager workspace,
		ToolManager tools,
		Document doc,
		bool toNewLayer,
		PointI pastePosition = new ())
	{
		if (!toNewLayer && !doc.Layers.CurrentUserLayer.IsEditable)
			return;

		var cb = GdkExtensions.GetDefaultClipboard ();

		// See if the current tool wants to handle the paste
		// operation (e.g., the text tool could paste text)
		if (!toNewLayer) {
			if (await tools.DoHandlePaste (doc, cb))
				return;
		}

		// Commit any unfinished tool actions
		tools.Commit ();

		Gdk.Texture? cb_texture = await cb.ReadTextureAsync ();
		if (cb_texture is null) {
			await ShowClipboardEmptyDialog (chrome);
			return;
		}

		Cairo.ImageSurface cb_image = cb_texture.ToSurface ();
		bool create_new_layer = toNewLayer || LayerHasContent (doc.Layers.CurrentUserLayer);
		CompoundHistoryItem paste_action = new (
			Resources.StandardIcons.EditPaste,
			create_new_layer
				? Translations.GetString ("Paste Into New Layer")
				: Translations.GetString ("Paste"));

		var canvas_size = workspace.ImageSize;

		// If the image being pasted is larger than the canvas size, allow the user to optionally resize the canvas
		if (cb_image.Width > canvas_size.Width || cb_image.Height > canvas_size.Height) {

			var response = await ShowExpandCanvasDialog (chrome);

			if (response == Gtk.ResponseType.Accept) {

				Size newSize = new (
					Width: Math.Max (canvas_size.Width, cb_image.Width),
					Height: Math.Max (canvas_size.Height, cb_image.Height)
				);

				workspace.ResizeCanvas (newSize, Pinta.Core.Anchor.Center, paste_action);
				actions.View.UpdateCanvasScale ();

			} else if (response != Gtk.ResponseType.Reject) // cancelled
				return;
		}

		// If the pasted image would fall off bottom- or right-
		// side of image, adjust paste position
		pastePosition = new PointI (
			X: Math.Clamp (pastePosition.X, 0, Math.Max (0, canvas_size.Width - cb_image.Width)),
			Y: Math.Clamp (pastePosition.Y, 0, Math.Max (0, canvas_size.Height - cb_image.Height))
		);

		if (create_new_layer) {
			var l = doc.Layers.AddNewLayer (string.Empty);
			paste_action.Push (new AddLayerHistoryItem (Resources.Icons.LayerNew, Translations.GetString ("Add New Layer"), l, doc.Layers.GetPosition (l)));
		}

		// Copy the paste to the temp layer, which should be at least the size of this document.
		doc.Layers.CreateSelectionLayer (
			Math.Max (doc.ImageSize.Width, cb_image.Width),
			Math.Max (doc.ImageSize.Height, cb_image.Height));

		doc.Layers.ShowSelectionLayer = true;

		using Cairo.Context g = new (doc.Layers.SelectionLayer.Surface);

		g.SetSourceSurface (cb_image, 0, 0);
		g.Paint ();

		doc.Layers.SelectionLayer.Transform.InitIdentity ();
		doc.Layers.SelectionLayer.Transform.Translate (pastePosition.X, pastePosition.Y);

		tools.SetCurrentTool ("MoveSelectedTool");

		var old_selection = doc.Selection.Clone ();

		doc.Selection.CreateRectangleSelection (new RectangleD ((PointD) pastePosition, cb_image.Width, cb_image.Height));
		doc.Selection.Visible = true;

		doc.Workspace.Invalidate ();

		paste_action.Push (new PasteHistoryItem (cb_image, old_selection));
		doc.History.PushNewItem (paste_action);
	}

	private static bool LayerHasContent (UserLayer layer)
	{
		foreach (ColorBgra pixel in layer.Surface.GetReadOnlyPixelData ()) {
			if (pixel.A != 0)
				return true;
		}

		return false;
	}

	public static Task ShowClipboardEmptyDialog (ChromeManager chrome)
	{
		string primary = Translations.GetString ("Image cannot be pasted");
		string secondary = Translations.GetString ("The clipboard does not contain an image.");
		return chrome.ShowMessageDialog (chrome.MainWindow, primary, secondary);
	}

	public static async Task<Gtk.ResponseType> ShowExpandCanvasDialog (ChromeManager chrome)
	{
		string primary = Translations.GetString ("Image larger than canvas");
		string secondary = Translations.GetString ("The image being pasted is larger than the canvas. What would you like to do to the canvas size?");

		using Adw.MessageDialog dialog = Adw.MessageDialog.New (chrome.MainWindow, primary, secondary);

		const string cancel_response = "cancel";
		const string reject_response = "reject";
		const string expand_response = "expand";

		dialog.AddResponse (cancel_response, Translations.GetString ("_Cancel"));
		// Translators: This refers to preserving the current canvas size when pasting a larger image.
		dialog.AddResponse (reject_response, Translations.GetString ("Preserve"));
		// Translators: This refers to expanding the canvas size when pasting a larger image.
		dialog.AddResponse (expand_response, Translations.GetString ("Expand"));

		dialog.SetResponseAppearance (expand_response, Adw.ResponseAppearance.Suggested);
		dialog.CloseResponse = cancel_response;
		dialog.DefaultResponse = expand_response;

		string response = await dialog.RunAsync ();

		return response switch {
			expand_response => Gtk.ResponseType.Accept,
			reject_response => Gtk.ResponseType.Reject,
			_ => Gtk.ResponseType.Cancel,
		};
	}
}
