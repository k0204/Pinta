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
		if (!TryGetLayerSize (layer, out Size layerSize))
			return;

		tools.Commit ();

		Size canvasSize = layer is AnimationOutputLayer animation
			? new Size (animation.CanvasWidth, animation.CanvasHeight)
			: doc.ImageSize;
		ResizeLayerOptions? response = await PromptResize (layerSize, canvasSize);
		if (!response.HasValue) return;

		ResizeLayerOptions resizing = response.Value;
		if (resizing.NewSize == layerSize)
			return;

		BaseHistoryItem history = layer is AnimationOutputLayer animationLayer
			? ResizeAnimationLayer (doc, animationLayer, resizing)
			: ResizeUserLayer (layer, resizing);
		doc.ResetSelectionPaths ();
		doc.Workspace.Invalidate ();
		doc.History.PushNewItem (history);
	}

	private async Task<ResizeLayerOptions?> PromptResize (Size layerSize, Size canvasSize)
	{
		using ResizeLayerDialog dialog = ResizeLayerDialog.New (chrome, settings, layerSize, canvasSize);
		try {
			Gtk.ResponseType response = await dialog.RunAsync ();
			if (response != Gtk.ResponseType.Ok) return null;
			return dialog.GetResizeLayerOptions ();
		} finally {
			dialog.Destroy ();
		}
	}

	private static bool TryGetLayerSize (UserLayer layer, out Size size)
	{
		if (!layer.IsEditable && layer is not AnimationOutputLayer) {
			size = Size.Empty;
			return false;
		}

		if (layer is AnimationOutputLayer animation) {
			foreach (AnimationFrameData frame in animation.GetFrames ()) {
				size = new Size (frame.Surface.Width, frame.Surface.Height);
				return true;
			}

			size = Size.Empty;
			return false;
		}

		size = new Size (layer.Surface.Width, layer.Surface.Height);
		return true;
	}

	private static BaseHistoryItem ResizeUserLayer (UserLayer layer, ResizeLayerOptions resizing)
	{
		ImageSurface oldSurface = layer.Surface.Clone ();
		layer.Resize (resizing.NewSize, resizing.ResamplingMode);
		return new SimpleHistoryItem (
			Resources.Icons.ImageResize,
			Translations.GetString ("Resize Layer"),
			oldSurface,
			layer);
	}

	private static BaseHistoryItem ResizeAnimationLayer (Document document, AnimationOutputLayer layer, ResizeLayerOptions resizing)
	{
		return layer switch {
			SpriteSheetLayer spriteSheet => ResizeSpriteSheetLayer (document, spriteSheet, resizing),
			SingleDirectionAnimationLayer singleDirection => ResizeSingleDirectionLayer (document, singleDirection, resizing),
			_ => throw new InvalidOperationException ($"Unsupported animation layer type '{layer.GetType ().Name}'."),
		};
	}

	private static BaseHistoryItem ResizeSpriteSheetLayer (Document document, SpriteSheetLayer layer, ResizeLayerOptions resizing)
	{
		SpriteSheetLayerSnapshot oldSnapshot = layer.CaptureSnapshot ();
		layer.Resize (resizing.NewSize, resizing.ResamplingMode);
		SpriteSheetLayerSnapshot newSnapshot = layer.CaptureSnapshot ();
		return new AnimationLayerResizeHistoryItem (
			Translations.GetString ("Resize Layer"),
			() => layer.ReplaceSnapshot (oldSnapshot, document.ImageSize),
			() => layer.ReplaceSnapshot (newSnapshot, document.ImageSize));
	}

	private static BaseHistoryItem ResizeSingleDirectionLayer (Document document, SingleDirectionAnimationLayer layer, ResizeLayerOptions resizing)
	{
		SingleDirectionAnimationLayerSnapshot oldSnapshot = layer.CaptureSnapshot ();
		layer.Resize (resizing.NewSize, resizing.ResamplingMode);
		SingleDirectionAnimationLayerSnapshot newSnapshot = layer.CaptureSnapshot ();
		return new AnimationLayerResizeHistoryItem (
			Translations.GetString ("Resize Layer"),
			() => layer.ReplaceSnapshot (oldSnapshot, document.ImageSize),
			() => layer.ReplaceSnapshot (newSnapshot, document.ImageSize));
	}

	private sealed class AnimationLayerResizeHistoryItem : BaseHistoryItem
	{
		private readonly Action undo;
		private readonly Action redo;

		public AnimationLayerResizeHistoryItem (string text, Action undo, Action redo)
			: base (Resources.Icons.ImageResize, text)
		{
			this.undo = undo;
			this.redo = redo;
		}

		public override void Undo () => undo ();
		public override void Redo () => redo ();
	}

}
