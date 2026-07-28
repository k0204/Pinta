//
// ResizeLayerAction.cs
//
// Author:
//       Pinta contributors
//
// Copyright (c) 2026 Pinta contributors
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
using Cairo;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class ResizeLayerAction : IActionHandler
{
	private readonly LayerActions layers;
	private readonly IChromeService chrome;
	private readonly IWorkspaceService workspace;
	private readonly ISettingsService settings;
	private readonly ToolManager tools;

	internal ResizeLayerAction (
		LayerActions layers,
		IChromeService chrome,
		IWorkspaceService workspace,
		ISettingsService settings,
		ToolManager tools)
	{
		this.layers = layers;
		this.chrome = chrome;
		this.workspace = workspace;
		this.settings = settings;
		this.tools = tools;
	}

	void IActionHandler.Initialize ()
	{
		layers.ResizeLayer.Activated += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		layers.ResizeLayer.Activated -= Activated;
	}

	private async void Activated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;
		UserLayer layer = doc.Layers.CurrentUserLayer;
		if (!layer.IsEditable)
			return;

		tools.Commit ();

		RectangleI bounds = Utility.GetAlphaBounds (layer.Surface);
		ResizeLayerOptions? response = await PromptResize (bounds.Size);
		if (!response.HasValue) return;

		ResizeLayerOptions resizing = response.Value;
		if (resizing.NewSize == bounds.Size)
			return;

		ImageSurface oldSurface = layer.Surface.Clone ();

		Matrix xform = CairoExtensions.CreateIdentityMatrix ();
		xform.Translate (bounds.Left, bounds.Top);
		xform.Scale (
			resizing.NewSize.Width / (double) bounds.Width,
			resizing.NewSize.Height / (double) bounds.Height);
		xform.Translate (-bounds.Left, -bounds.Top);

		layer.ApplyTransform (xform, doc.ImageSize, doc.ImageSize, resizing.ResamplingMode);
		doc.ResetSelectionPaths ();
		doc.Workspace.Invalidate ();

		doc.History.PushNewItem (new SimpleHistoryItem (
			Resources.Icons.ImageResize,
			Translations.GetString ("Resize Layer"),
			oldSurface,
			layer));
	}

	private async Task<ResizeLayerOptions?> PromptResize (Size layerSize)
	{
		using ResizeLayerDialog dialog = ResizeLayerDialog.New (chrome, settings, layerSize);
		try {
			Gtk.ResponseType response = await dialog.RunAsync ();
			if (response != Gtk.ResponseType.Ok) return null;
			return dialog.GetResizeLayerOptions ();
		} finally {
			dialog.Destroy ();
		}
	}

}
