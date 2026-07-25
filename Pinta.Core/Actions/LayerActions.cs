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
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed class LayerActions
{
	public Command AddNewLayer { get; }
        public Command AddNewGroup { get; }
	public Command AddChildLayer { get; }
	public Command DeleteLayer { get; }
	public Command DuplicateLayer { get; }
	public Command MergeLayerDown { get; }
	public Command ImportFromFile { get; }
	public Command DetectBorder { get; }
	public Command FlipHorizontal { get; }
	public Command FlipVertical { get; }
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
	private bool detect_border_running;

	public LayerActions (
		ChromeManager chrome,
		ImageConverterManager imageFormats,
		RecentFileManager recentFiles,
		ToolManager tools,
		WorkspaceManager workspace,
		ImageActions image,
		AdjustmentsActions adjustments,
		EffectsActions effects)
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

		AddChildLayer = new Command (
			"addchildlayer",
			Translations.GetString ("Add Child Layer"),
			null,
			Resources.Icons.LayerNew);

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
		border_recognition = new ();
	}

	public void RegisterActions (Gtk.Application app)
	{
		app.AddCommands ([
			AddNewLayer,
                        AddNewGroup,
			AddChildLayer,
			DeleteLayer,
			DuplicateLayer,
			MergeLayerDown,
			ImportFromFile,
			DetectBorder,

			FlipHorizontal,
			FlipVertical,
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
		AddChildLayer.Activated += HandlePintaCoreActionsLayersAddChildLayerActivated;
		DeleteLayer.Activated += HandlePintaCoreActionsLayersDeleteLayerActivated;
		DuplicateLayer.Activated += HandlePintaCoreActionsLayersDuplicateLayerActivated;
		MergeLayerDown.Activated += HandlePintaCoreActionsLayersMergeLayerDownActivated;
		MoveLayerDown.Activated += HandlePintaCoreActionsLayersMoveLayerDownActivated;
		MoveLayerUp.Activated += HandlePintaCoreActionsLayersMoveLayerUpActivated;
		FlipHorizontal.Activated += HandlePintaCoreActionsLayersFlipHorizontalActivated;
		FlipVertical.Activated += HandlePintaCoreActionsLayersFlipVerticalActivated;
		ImportFromFile.Activated += HandlePintaCoreActionsLayersImportFromFileActivated;
		DetectBorder.Activated += HandlePintaCoreActionsLayersDetectBorderActivated;
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
		AddChildLayer.Sensitive = activeDoc != null;

		bool currentEditable = activeDoc?.Layers.CurrentUserLayer.IsEditable ?? false;
		bool canMergeDown = activeDoc?.Layers.CanMoveCurrentLayerDown () ?? false;
		MergeLayerDown.Sensitive = canMergeDown && currentEditable && activeDoc!.Layers.GetSiblingBelow (activeDoc.Layers.CurrentUserLayer).IsEditable;
		MoveLayerDown.Sensitive = canMergeDown;

		MoveLayerUp.Sensitive = activeDoc?.Layers.CanMoveCurrentLayerUp () ?? false;
		FlipHorizontal.Sensitive = currentEditable;
		FlipVertical.Sensitive = currentEditable;
		RotateZoom.Sensitive = currentEditable;
		adjustments.ToggleActionsSensitive (currentEditable);
		effects.ToggleActionsSensitive (currentEditable);
		UnlockReference.Sensitive = activeDoc?.Layers.CurrentUserLayer.IsReference == true && !activeDoc.Layers.CurrentUserLayer.ReferenceMissing;
		DetectBorder.Sensitive = activeDoc is not null && !detect_border_running;
	}

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

	private async void HandlePintaCoreActionsLayersDetectBorderActivated (object sender, EventArgs e)
	{
		if (detect_border_running)
			return;

		Document doc = workspace.ActiveDocument;
		tools.Commit ();

		if (!doc.Selection.Visible) {
			await chrome.ShowMessageDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border"),
				Translations.GetString ("Select an area before detecting the border."));
			return;
		}

		RectangleI box = doc.GetSelectedBounds (canvasOnly: true);

		if (box.IsEmpty) {
			await chrome.ShowMessageDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border"),
				Translations.GetString ("The selected area is empty."));
			return;
		}

		using Adw.MessageDialog confirmation = Adw.MessageDialog.New (
			chrome.MainWindow,
			Translations.GetString ("Detect Border"),
			Translations.GetString ("Detect the border in the selected area?"));
		const string cancel_response = "cancel";
		const string confirm_response = "detect";
		confirmation.AddResponse (cancel_response, Translations.GetString ("_Cancel"));
		confirmation.AddResponse (confirm_response, Translations.GetString ("_Detect"));
		confirmation.SetResponseAppearance (confirm_response, Adw.ResponseAppearance.Suggested);
		confirmation.DefaultResponse = confirm_response;
		confirmation.CloseResponse = cancel_response;
		if (await confirmation.RunAsync () != confirm_response)
			return;

		detect_border_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		chrome.SetStatusBarText (Translations.GetString ("Detecting border..."));

		try {
			byte[] sourcePng = CreateDocumentPng (doc);
			AI.CharacterBorderRecognitionResult result = await border_recognition.RecognizeAsync (sourcePng, box);

			CompoundHistoryItem hist = new (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Detect Border"));

			UserLayer detectedLayer = doc.Layers.AddNewLayer (Translations.GetString ("Detected Border"));
			DrawPngOnLayer (result.PartPng, detectedLayer);
			hist.Push (new AddLayerHistoryItem (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Detected Border"),
				detectedLayer,
				doc.Layers.GetPosition (detectedLayer)));

			UserLayer controlLayer = doc.Layers.AddNewLayer (Translations.GetString ("Border Control"));
			DrawRecognitionControl (result.MaskPng, controlLayer, box);
			controlLayer.Opacity = 0.65;
			hist.Push (new AddLayerHistoryItem (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Border Control"),
				controlLayer,
				doc.Layers.GetPosition (controlLayer)));

			doc.Layers.SetCurrentUserLayer (controlLayer);
			doc.History.PushNewItem (hist);
			doc.Workspace.Invalidate ();
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border Failed"),
				Translations.GetString ("Start the local character recognition service on port 8001, then try again."),
				ex.ToString ());
		} finally {
			detect_border_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
			chrome.SetStatusBarText (string.Empty);
		}
	}

	private static byte[] CreateDocumentPng (Document doc)
	{
		using Cairo.ImageSurface source = doc.GetFlattenedImage ();
		using GdkPixbuf.Pixbuf pixbuf = source.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static void DrawPngOnLayer (byte[] png, UserLayer layer)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		using Cairo.Context context = new (layer.Surface);
		context.DrawPixbuf (pixbuf, PointD.Zero);
	}

	private static void DrawRecognitionControl (byte[] maskPng, UserLayer layer, RectangleI box)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (maskPng);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		using Cairo.ImageSurface mask = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			layer.Surface.Width,
			layer.Surface.Height);
		using (Cairo.Context context = new (mask))
			context.DrawPixbuf (pixbuf, PointD.Zero);

		ReadOnlySpan<ColorBgra> maskPixels = mask.GetReadOnlyPixelData ();
		Span<ColorBgra> controlPixels = layer.Surface.GetPixelData ();
		int width = layer.Surface.Width;
		for (int y = box.Top; y < box.Bottom; y++) {
			for (int x = box.Left; x < box.Right; x++) {
				int index = y * width + x;
				controlPixels[index] = maskPixels[index].R >= 128
					? ColorBgra.Red
					: ColorBgra.Yellow;
			}
		}
		layer.Surface.MarkDirty (box);
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

		tools.Commit ();

		UserLayer bottomLayer = doc.Layers.GetSiblingBelow (doc.Layers.CurrentUserLayer);
		Cairo.ImageSurface oldBottomSurface = bottomLayer.Surface.Clone ();

		CompoundHistoryItem hist = new (
			Resources.Icons.LayerMergeDown,
			Translations.GetString ("Merge Layer Down"));

		DeleteLayerHistoryItem h1 = new (
			string.Empty,
			string.Empty,
			doc.Layers.CurrentUserLayer,
			doc.Layers.GetPosition (doc.Layers.CurrentUserLayer));

		doc.Layers.MergeCurrentLayerDown ();

		SimpleHistoryItem h2 = new (
			string.Empty,
			string.Empty,
			oldBottomSurface,
			bottomLayer);
		hist.Push (h1);
		hist.Push (h2);

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

	private void HandlePintaCoreActionsLayersAddChildLayerActivated (object sender, EventArgs e)
	{
		Document doc = workspace.ActiveDocument;
		tools.Commit ();

		UserLayer l = doc.Layers.AddNewChildLayer (doc.Layers.CurrentUserLayer, string.Empty);

		AddLayerHistoryItem hist = new (
			Resources.Icons.LayerNew,
			Translations.GetString ("Add Child Layer"),
			l,
			doc.Layers.GetPosition (l));
		doc.History.PushNewItem (hist);
	}
}
