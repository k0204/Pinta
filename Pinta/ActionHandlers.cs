//
// FileActionHandler.cs
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

using System.Collections.Generic;
using System.Linq;
using Pinta.Actions;
using Pinta.Core;

namespace Pinta;

public sealed class ActionHandlers
{
	private readonly List<IActionHandler> action_handlers;

	public ActionHandlers ()
	{
		ChromeManager chrome = PintaCore.Chrome;
		WorkspaceManager workspace = PintaCore.Workspace;
		ActionManager actions = PintaCore.Actions;
		RecentFileManager recentFiles = PintaCore.RecentFiles;
		ImageConverterManager imageFormats = PintaCore.ImageFormats;
		SettingsManager settings = PintaCore.Settings;
		Pinta.Core.AI.AiAuthService aiAuth = PintaCore.AiAuth;
		SystemManager system = PintaCore.System;
		ToolManager tools = PintaCore.Tools;
		PaletteManager palette = PintaCore.Palette;
		CanvasGridManager canvasGrid = PintaCore.CanvasGrid;
		string applicationVersion = PintaCore.ApplicationVersion;

		action_handlers = [
			// File
			new NewDocumentAction (actions, chrome, palette, settings, workspace),
			new NewScreenshotAction (system, chrome, workspace, actions),
			new OpenDocumentAction (actions.File, chrome, workspace, recentFiles),
			new SaveDocumentAction (actions.File, workspace),
			new SaveDocumentAsAction (actions.File, workspace),
			new SaveDocumentImplmentationAction (actions.File, actions.Image, chrome, imageFormats, recentFiles, tools),
			new ModifyCompressionAction (actions.File),
			//new PrintDocumentAction ();
			new CloseDocumentAction (actions, chrome, workspace, tools),
			new ExitProgramAction (actions, chrome, workspace),

			// Edit
			new OffsetSelectionAction (actions.Edit, chrome, workspace, tools),
			new PasteAction (chrome, actions, workspace, tools),
			new PasteIntoNewLayerAction (actions, chrome, workspace, tools),
			new PasteIntoNewImageAction (actions, chrome, workspace),
			new ResizePaletteAction (actions.Edit, chrome, palette),
			new AddinManagerAction (actions.Addins, chrome, system),

			// Image
			new ResizeImageAction (actions.Image, chrome, workspace, settings),
			new ResizeCanvasAction (chrome, workspace, settings, actions),

			// Layers
			new LayerAlignmentWindowAction (chrome, actions.Layers),
			new LayerPropertiesAction (chrome, actions.Layers, workspace),
			new ResizeLayerAction (actions.Layers, chrome, workspace, settings, tools),
			new RotateZoomLayerAction (chrome, actions.Layers, workspace, tools),

			// View
			new MenuBarToggledAction (actions.View, chrome),
			new ToolBarToggledAction (actions.View, chrome),
			new ImageTabsToggledAction (actions.View, chrome),
			new ToolWindowsToggledAction (actions.View, chrome),
			new StatusBarToggledAction (actions.View, chrome),
			new ToolBoxToggledAction (actions.View, chrome),
			new ColorSchemeChangedAction (actions.View),
			new EditCanvasGridAction (actions.View, chrome, canvasGrid),

			// Window
			new CloseAllDocumentsAction (actions, workspace),
			new SaveAllDocumentsAction (actions.Window, workspace),

			// Help
			new AiAccountAction (actions.App, chrome, aiAuth),
			new AboutDialogAction (actions.App, chrome, applicationVersion),
			new KeyboardShortcutsDialogAction (actions.App, actions, chrome, tools),
		];

		// Initialize each action handler
		foreach (var action in action_handlers)
			action.Initialize ();

		// We need to toggle actions active/inactive
		// when there isn't an open document
		PintaCore.Workspace.DocumentActivated += Workspace_DocumentCreated;
		PintaCore.Workspace.DocumentClosed += Workspace_DocumentClosed;

		// Initially, no documents are open.
		ToggleActions (false);
	}

	private void Workspace_DocumentClosed (object? sender, DocumentEventArgs e)
	{
		if (!PintaCore.Workspace.HasOpenDocuments)
			ToggleActions (false);
	}

	private void Workspace_DocumentCreated (object? sender, DocumentEventArgs e)
	{
		ToggleActions (true);
	}

	private static void ToggleActions (bool enable)
	{
		Document? document = enable ? PintaCore.Workspace.ActiveDocumentOrDefault : null;
		bool hasSelectedLayer = document?.Layers.HasSelectedLayer == true;
		bool editableLayer = hasSelectedLayer && document!.Layers.CurrentUserLayer.IsEditable;
		bool resizableLayer = editableLayer || document?.Layers.CurrentUserLayer is AnimationOutputLayer;
		bool editableCanvas = document is not null && !document.Layers.HasLockedReferences;
		bool editableImage = editableCanvas && !document!.Layers.AllLayers.Any (layer => layer is AnimationOutputLayer);
		bool selectionVisible = document?.Selection.Visible == true;

		PintaCore.Actions.File.Close.Sensitive = enable;
		PintaCore.Actions.File.Save.Sensitive = enable;
		PintaCore.Actions.File.SaveAs.Sensitive = enable;
		PintaCore.Actions.File.Print.Sensitive = enable;
		PintaCore.Actions.Edit.Copy.Sensitive = hasSelectedLayer;
		PintaCore.Actions.Edit.CopyMerged.Sensitive = enable;
		PintaCore.Actions.Edit.Cut.Sensitive = editableLayer;
		PintaCore.Actions.Edit.PasteIntoNewLayer.Sensitive = enable;
		PintaCore.Actions.Edit.EraseSelection.Sensitive = editableLayer && selectionVisible;
		PintaCore.Actions.Edit.FillSelection.Sensitive = editableLayer && selectionVisible;
		PintaCore.Actions.Edit.InvertSelection.Sensitive = selectionVisible;
		PintaCore.Actions.Edit.OffsetSelection.Sensitive = selectionVisible;
		PintaCore.Actions.Edit.SelectAll.Sensitive = enable;
		PintaCore.Actions.Edit.Deselect.Sensitive = selectionVisible;

		PintaCore.Actions.View.ActualSize.Sensitive = enable;
		PintaCore.Actions.View.ZoomIn.Sensitive = enable;
		PintaCore.Actions.View.ZoomOut.Sensitive = enable;
		PintaCore.Actions.View.ZoomToSelection.Sensitive = enable;
		PintaCore.Actions.View.ZoomToWindow.Sensitive = enable;
		PintaCore.Actions.View.ZoomComboBox.Sensitive = enable;

		PintaCore.Actions.Image.CropToSelection.Sensitive = editableImage && selectionVisible;
		PintaCore.Actions.Image.AutoCrop.Sensitive = editableImage;
		PintaCore.Actions.Image.CanvasSize.Sensitive = editableCanvas;
		PintaCore.Actions.Image.Resize.Sensitive = editableImage;
		PintaCore.Actions.Image.FlipHorizontal.Sensitive = editableImage;
		PintaCore.Actions.Image.FlipVertical.Sensitive = editableImage;
		PintaCore.Actions.Image.Rotate180.Sensitive = editableImage;
		PintaCore.Actions.Image.RotateCCW.Sensitive = editableImage;
		PintaCore.Actions.Image.RotateCW.Sensitive = editableImage;
		PintaCore.Actions.Image.Flatten.Sensitive = editableImage && document!.Layers.AllLayers.Count > 1;

		PintaCore.Actions.Layers.AddNewLayer.Sensitive = enable;
		PintaCore.Actions.Layers.AddNewGroup.Sensitive = enable;
		PintaCore.Actions.Layers.DeleteLayer.Sensitive = hasSelectedLayer;
		PintaCore.Actions.Layers.DuplicateLayer.Sensitive = hasSelectedLayer;
		bool canMergeDown = editableLayer
			&& document!.Layers.CanMoveCurrentLayerDown ()
			&& document.Layers.GetSiblingBelow (document.Layers.CurrentUserLayer).IsEditable;
		PintaCore.Actions.Layers.MergeLayerDown.Sensitive = canMergeDown;
		PintaCore.Actions.Layers.ImportFromFile.Sensitive = enable;
		PintaCore.Actions.Layers.DetectBorder.Sensitive = editableLayer;
		PintaCore.Actions.Layers.Cutout.Sensitive = editableLayer;
		PintaCore.Actions.Layers.FlipHorizontal.Sensitive = editableLayer;
		PintaCore.Actions.Layers.FlipVertical.Sensitive = editableLayer;
		PintaCore.Actions.Layers.ResizeLayer.Sensitive = resizableLayer;
		PintaCore.Actions.Layers.RotateZoom.Sensitive = editableLayer;
		PintaCore.Actions.Layers.MoveLayerUp.Sensitive = hasSelectedLayer;
		PintaCore.Actions.Layers.MoveLayerDown.Sensitive = hasSelectedLayer;
		PintaCore.Actions.Layers.Properties.Sensitive = hasSelectedLayer;
		PintaCore.Actions.Layers.OpenLayerAlignmentWindow.Sensitive = enable;
		PintaCore.Actions.Layers.UnlockReference.Sensitive = document?.Layers.CurrentUserLayer.IsReference == true && !document.Layers.CurrentUserLayer.ReferenceMissing;

		PintaCore.Actions.Adjustments.ToggleActionsSensitive (editableLayer);
		PintaCore.Actions.Effects.ToggleActionsSensitive (editableLayer);

		PintaCore.Actions.Window.SaveAll.Sensitive = enable;
		PintaCore.Actions.Window.CloseAll.Sensitive = enable;
	}
}

