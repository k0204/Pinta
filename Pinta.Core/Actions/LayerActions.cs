//
// LayerActions.cs
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

namespace Pinta.Core;

public sealed partial class LayerActions
{
	public Command AddNewLayer { get; }
        public Command AddNewGroup { get; }
	public Command GenerateImage { get; }
	public Command GenerateSpritesheet { get; }
	public Command SplitSpritesheet { get; }
	public Command SetSpritesheetAnchor { get; }
	public Command DeleteLayer { get; }
	public Command DuplicateLayer { get; }
	public Command MergeLayerDown { get; }
	public Command MergeSelectedLayers { get; }
	public Command ImportFromFile { get; }
	public Command DetectBorder { get; }
	public Command Cutout { get; }
	public Command FlipHorizontal { get; }
	public Command FlipVertical { get; }
	public Command ResizeLayer { get; }
	public Command RotateZoom { get; }
	public Command MoveLayerUp { get; }
	public Command MoveLayerDown { get; }
	public Command Properties { get; }
	public Command UnlockReference { get; }

	private readonly ChromeManager chrome;
	private readonly ImageConverterManager image_formats;
	private readonly RecentFileManager recent_files;
	private readonly ToolManager tools;
	private readonly WorkspaceManager workspace;
	private readonly ImageActions image;
	private readonly AdjustmentsActions adjustments;
	private readonly EffectsActions effects;
	private readonly AI.CharacterBorderRecognitionService border_recognition;
	private readonly AI.BackgroundCutoutService background_cutout;
	private bool detect_border_running;
	private bool cutout_running;

	public LayerActions (
		ChromeManager chrome,
		ImageConverterManager imageFormats,
		RecentFileManager recentFiles,
		ToolManager tools,
		WorkspaceManager workspace,
		ImageActions image,
		AdjustmentsActions adjustments,
		EffectsActions effects,
		AI.AiAuthService aiAuth)
	{
		AddNewLayer = new Command (
			"addnewlayer",
			Translations.GetString ("Add New Layer"),
			null,
			Resources.Icons.LayerNew,
			shortcuts: ["<Primary><Shift>N"]);

                AddNewGroup = new Command (
                        "addnewgroup",
                        Translations.GetString ("Add New Group"),
                        null,
                        Resources.Icons.LayerGroup);

		GenerateImage = new Command (
			"generateimage",
			Translations.GetString ("AI 生成"),
			Translations.GetString ("Generate an image with AI"),
			Resources.Icons.EffectsRenderClouds);

		GenerateSpritesheet = new Command (
			"generatespritesheet",
			Translations.GetString ("Generate Spritesheet"),
			Translations.GetString ("Generate a direction sheet or action spritesheet with AI"),
			Resources.Icons.LayerDuplicate);

		SplitSpritesheet = new Command (
			"splitspritesheet",
			Translations.GetString ("Split Spritesheet"),
			Translations.GetString ("Split the selected source sheet into direction frame layers"),
			Resources.Icons.ImageCrop);

		SetSpritesheetAnchor = new Command (
			"setspritesheetanchor",
			Translations.GetString ("Set as Character Anchor"),
			Translations.GetString ("Use this approved direction sheet as the default character reference"),
			Resources.Icons.LayerDuplicate);

		DeleteLayer = new Command (
			"deletelayer",
			Translations.GetString ("Delete Layer"),
			null,
			Resources.Icons.LayerDelete,
			shortcuts: ["<Primary><Shift>Delete"]);

		DuplicateLayer = new Command (
			"duplicatelayer",
			Translations.GetString ("Duplicate Layer"),
			null,
			Resources.Icons.LayerDuplicate,
			shortcuts: ["<Primary><Shift>D"]);

		MergeLayerDown = new Command (
			"mergelayerdown",
			Translations.GetString ("Merge Layer Down"),
			null,
			Resources.Icons.LayerMergeDown,
			shortcuts: ["<Primary>M"]);

		MergeSelectedLayers = new Command (
			"mergeselectedlayers",
			Translations.GetString ("Merge Selected Layers"),
			null,
			Resources.Icons.LayerMergeDown) { Sensitive = false };

		ImportFromFile = new Command (
			"importfromfile",
			Translations.GetString ("Import from File..."),
			null,
			Resources.Icons.LayerImport);

		DetectBorder = new Command (
			"detectborder",
			Translations.GetString ("Detect Border"),
			Translations.GetString ("Detect border and create a new layer"),
			Resources.Icons.EffectsStylizeOutline);

		Cutout = new Command (
			"backgroundcutout",
			Translations.GetString ("抠图"),
			Translations.GetString ("Choose an image API and operation"),
			Resources.Icons.ColorModeTransparency);

		FlipHorizontal = new Command (
			"fliplayerhorizontal",
			Translations.GetString ("Flip Horizontal"),
			null,
			Resources.Icons.LayerFlipHorizontal);

		FlipVertical = new Command (
			"fliplayervertical",
			Translations.GetString ("Flip Vertical"),
			null,
			Resources.Icons.LayerFlipVertical);

		ResizeLayer = new Command (
			"resizelayer",
			Translations.GetString ("Resize Layer..."),
			null,
			Resources.Icons.ImageResize);

		RotateZoom = new Command (
			"RotateZoom",
			Translations.GetString ("Rotate / Zoom Layer..."),
			null,
			Resources.Icons.LayerRotateZoom);

		MoveLayerUp = new Command (
			"movelayerup",
			Translations.GetString ("Move Layer Up"),
			null,
			Resources.StandardIcons.LayerMoveUp);

		MoveLayerDown = new Command (
			"movelayerdown",
			Translations.GetString ("Move Layer Down"),
			null,
			Resources.StandardIcons.LayerMoveDown);

		Properties = new Command (
			"properties",
			Translations.GetString ("Layer Properties..."),
			null,
			Resources.Icons.LayerProperties,
			shortcuts: ["F4"]);

		UnlockReference = new Command (
			"unlockreference",
			Translations.GetString ("Unlock Referenced Layer"),
			null,
			Resources.Icons.LayerImport);

		this.chrome = chrome;
		image_formats = imageFormats;
		recent_files = recentFiles;
		this.tools = tools;
		this.workspace = workspace;
		this.image = image;
		this.adjustments = adjustments;
		this.effects = effects;
		border_recognition = new (aiAuth);
		background_cutout = new (aiAuth);
	}

	public void RegisterActions (Gtk.Application app)
	{
		app.AddCommands ([
			AddNewLayer,
                        AddNewGroup,
			GenerateImage,
			GenerateSpritesheet,
			SplitSpritesheet,
			SetSpritesheetAnchor,
			DeleteLayer,
			DuplicateLayer,
			MergeLayerDown,
			MergeSelectedLayers,
			ImportFromFile,
			DetectBorder,
			Cutout,

			FlipHorizontal,
			FlipVertical,
			ResizeLayer,
			RotateZoom,

			Properties,
			UnlockReference,

			MoveLayerDown,
			MoveLayerUp]);
	}

	public void RegisterHandlers ()
	{
		AddNewLayer.Activated += HandlePintaCoreActionsLayersAddNewLayerActivated;
                AddNewGroup.Activated += HandlePintaCoreActionsLayersAddNewGroupActivated;
		GenerateImage.Activated += HandlePintaCoreActionsLayersGenerateImageActivated;
		GenerateSpritesheet.Activated += HandlePintaCoreActionsLayersGenerateSpritesheetActivated;
		SplitSpritesheet.Activated += HandlePintaCoreActionsLayersSplitSpritesheetActivated;
		SetSpritesheetAnchor.Activated += HandleSetSpritesheetAnchorActivated;
		DeleteLayer.Activated += HandlePintaCoreActionsLayersDeleteLayerActivated;
		DuplicateLayer.Activated += HandlePintaCoreActionsLayersDuplicateLayerActivated;
		MergeLayerDown.Activated += HandlePintaCoreActionsLayersMergeLayerDownActivated;
		MoveLayerDown.Activated += HandlePintaCoreActionsLayersMoveLayerDownActivated;
		MoveLayerUp.Activated += HandlePintaCoreActionsLayersMoveLayerUpActivated;
		FlipHorizontal.Activated += HandlePintaCoreActionsLayersFlipHorizontalActivated;
		FlipVertical.Activated += HandlePintaCoreActionsLayersFlipVerticalActivated;
		ImportFromFile.Activated += HandlePintaCoreActionsLayersImportFromFileActivated;
		DetectBorder.Activated += HandlePintaCoreActionsLayersDetectBorderActivated;
		Cutout.Activated += HandlePintaCoreActionsLayersCutoutActivated;
		UnlockReference.Activated += HandleUnlockReferenceActivated;

		workspace.LayerTreeChanged += EnableOrDisableLayerActions;
		workspace.SelectedLayerChanged += EnableOrDisableLayerActions;
		workspace.ActiveDocumentChanged += EnableOrDisableLayerActions;

		EnableOrDisableLayerActions (null, EventArgs.Empty);
	}

	private void EnableOrDisableLayerActions (object? sender, EventArgs e)
	{
		Document? activeDoc = workspace.ActiveDocumentOrDefault;

		bool hasMultipleLayers = activeDoc is not null && activeDoc.Layers.AllLayers.Count > 1;
		DeleteLayer.Sensitive = hasMultipleLayers;
		image.Flatten.Sensitive = hasMultipleLayers && activeDoc?.Layers.HasLockedReferences != true;
                AddNewGroup.Sensitive = activeDoc != null;
		GenerateImage.Sensitive = !cutout_running;
		GenerateSpritesheet.Sensitive = activeDoc is not null && !cutout_running;
		SplitSpritesheet.Sensitive = activeDoc is not null && IsSpritesheetSource (activeDoc.Layers.CurrentUserLayer) && !cutout_running;
		SetSpritesheetAnchor.Sensitive = activeDoc is not null && IsDirectionSheetSource (activeDoc.Layers.CurrentUserLayer);

		bool currentEditable = activeDoc?.Layers.CurrentUserLayer.IsEditable ?? false;
		bool canMergeDown = activeDoc?.Layers.CanMoveCurrentLayerDown () ?? false;
		MergeLayerDown.Sensitive = canMergeDown && currentEditable && activeDoc!.Layers.GetSiblingBelow (activeDoc.Layers.CurrentUserLayer).IsEditable;
		MoveLayerDown.Sensitive = canMergeDown;

		MoveLayerUp.Sensitive = activeDoc?.Layers.CanMoveCurrentLayerUp () ?? false;
		FlipHorizontal.Sensitive = currentEditable;
		FlipVertical.Sensitive = currentEditable;
		ResizeLayer.Sensitive = currentEditable;
		RotateZoom.Sensitive = currentEditable;
		adjustments.ToggleActionsSensitive (currentEditable);
		effects.ToggleActionsSensitive (currentEditable);
		UnlockReference.Sensitive = activeDoc?.Layers.CurrentUserLayer.IsReference == true && !activeDoc.Layers.CurrentUserLayer.ReferenceMissing;
		DetectBorder.Sensitive = activeDoc is not null && !detect_border_running;
		Cutout.Sensitive = currentEditable && !cutout_running;
	}

	private void HandlePintaCoreActionsLayersFlipVerticalActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;
		if (!doc.Layers.CurrentUserLayer.IsEditable)
			return;

		tools.Commit ();

		doc.Layers.CurrentUserLayer.FlipVertical ();
		doc.Workspace.Invalidate ();
		doc.History.PushNewItem (new InvertHistoryItem (InvertType.FlipLayerVertical, doc.Layers.CurrentUserLayer));
	}

	private void HandlePintaCoreActionsLayersFlipHorizontalActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;
		if (!doc.Layers.CurrentUserLayer.IsEditable)
			return;

		tools.Commit ();

		doc.Layers.CurrentUserLayer.FlipHorizontal ();
		doc.Workspace.Invalidate ();
		doc.History.PushNewItem (new InvertHistoryItem (InvertType.FlipLayerHorizontal, doc.Layers.CurrentUserLayer));
	}

	private void HandlePintaCoreActionsLayersMoveLayerUpActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;

		tools.Commit ();

		UserLayer layer = doc.Layers.CurrentUserLayer;
		UserLayer sibling = doc.Layers.GetSiblingAbove (layer);
		SwapLayersHistoryItem hist = new (
			Resources.StandardIcons.LayerMoveUp,
			Translations.GetString ("Move Layer Up"),
			layer,
			sibling);

		doc.Layers.MoveCurrentLayerUp ();
		doc.History.PushNewItem (hist);
	}

	private void HandlePintaCoreActionsLayersMoveLayerDownActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;

		tools.Commit ();

		UserLayer layer = doc.Layers.CurrentUserLayer;
		UserLayer sibling = doc.Layers.GetSiblingBelow (layer);
		SwapLayersHistoryItem hist = new (
			Resources.StandardIcons.LayerMoveDown,
			Translations.GetString ("Move Layer Down"),
			layer,
			sibling);

		doc.Layers.MoveCurrentLayerDown ();
		doc.History.PushNewItem (hist);
	}

	private void HandlePintaCoreActionsLayersMergeLayerDownActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;
		if (!doc.Layers.CurrentUserLayer.IsEditable || !doc.Layers.GetSiblingBelow (doc.Layers.CurrentUserLayer).IsEditable)
			return;

		MergeLayers ([doc.Layers.GetSiblingBelow (doc.Layers.CurrentUserLayer), doc.Layers.CurrentUserLayer]);
	}

	public bool CanMergeLayers (IReadOnlyCollection<UserLayer> layers)
	{
		if (layers.Count < 2 || layers.Any (layer => !layer.IsEditable))
			return false;

		UserLayer first = layers.First ();
		return layers.All (layer => layer.Parent == first.Parent);
	}

	public void MergeLayers (IReadOnlyCollection<UserLayer> layers)
	{
		if (!CanMergeLayers (layers))
			return;

		Document doc = workspace.ActiveDocument;
		List<UserLayer> ordered = [.. layers.Distinct ().OrderBy (layer => doc.Layers.GetPosition (layer).Index)];
		if (ordered.Count < 2)
			return;

		tools.Commit ();

		UserLayer bottomLayer = ordered[0];
		Cairo.ImageSurface oldBottomSurface = bottomLayer.Surface.Clone ();

		CompoundHistoryItem hist = new (
			Resources.Icons.LayerMergeDown,
			ordered.Count == 2
				? Translations.GetString ("Merge Layer Down")
				: Translations.GetString ("Merge Selected Layers"));

		foreach (UserLayer child in bottomLayer.Children.Reverse ())
			hist.Push (new DeleteLayerHistoryItem (
				string.Empty,
				string.Empty,
				child,
				doc.Layers.GetPosition (child)));

		foreach (UserLayer layer in ordered.Skip (1).Reverse ())
			hist.Push (new DeleteLayerHistoryItem (
				string.Empty,
				string.Empty,
				layer,
				doc.Layers.GetPosition (layer)));

		doc.Layers.MergeLayers (ordered);
		doc.ResetSelectionPaths ();

		hist.Push (new SimpleHistoryItem (
			string.Empty,
			string.Empty,
			oldBottomSurface,
			bottomLayer));

		doc.History.PushNewItem (hist);
	}

	private void HandlePintaCoreActionsLayersDuplicateLayerActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;

		tools.Commit ();

		UserLayer l = doc.Layers.DuplicateCurrentLayer ();

		// Make new layer the current layer
		doc.Layers.SetCurrentUserLayer (l);

		AddLayerHistoryItem hist = new (
			Resources.Icons.LayerDuplicate,
			Translations.GetString ("Duplicate Layer"),
			l,
			doc.Layers.GetPosition (l));
		doc.History.PushNewItem (hist);
	}

	private void HandlePintaCoreActionsLayersDeleteLayerActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;

		tools.Commit ();

		DeleteLayerHistoryItem hist = new (
			Resources.Icons.LayerDelete,
			Translations.GetString ("Delete Layer"),
			doc.Layers.CurrentUserLayer,
			doc.Layers.GetPosition (doc.Layers.CurrentUserLayer));

		doc.Layers.DeleteCurrentLayer ();

		doc.History.PushNewItem (hist);
	}

	private void HandlePintaCoreActionsLayersAddNewLayerActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;
		tools.Commit ();

		UserLayer l = doc.Layers.AddNewLayer (string.Empty);

		AddLayerHistoryItem hist = new (
			Resources.Icons.LayerNew,
			Translations.GetString ("Add New Layer"),
			l,
			doc.Layers.GetPosition (l));
		doc.History.PushNewItem (hist);
	}

        private void HandlePintaCoreActionsLayersAddNewGroupActivated (object sender, EventArgs e)
        {
                Document doc = workspace.ActiveDocument;
                tools.Commit ();

                GroupLayer layer = doc.Layers.AddNewGroup (Translations.GetString ("Group"));

                AddLayerHistoryItem hist = new (
                        Resources.Icons.LayerGroup,
                        Translations.GetString ("Add New Group"),
                        layer,
                        doc.Layers.GetPosition (layer));
                doc.History.PushNewItem (hist);
        }

}
