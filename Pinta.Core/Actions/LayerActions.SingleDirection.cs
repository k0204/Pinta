using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandleCreateSingleDirectionAnimationActivated (object sender, EventArgs e)
	{
		if (workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.HasSelectedLayer)
			return;
		if (document.Layers.CurrentUserLayer is SingleDirectionAnimationLayer existingLayer) {
			await OpenSingleDirectionAnimationEditorAsync (document, existingLayer);
			return;
		}
		if (!CanCreateSpritesheetAnimation (document.Layers.CurrentUserLayer))
			return;

		UserLayer source = document.Layers.CurrentUserLayer;
		AI.SpritesheetAttemptInfo info = CreateDefaultSpritesheetInfo (source);
		using SingleDirectionAnimationDialog dialog = new (
			chrome.MainWindow,
			source,
			info,
			[],
			provider => sprite_segmentation.AnalyzeAsync (
				CreateSurfacePng (source.Surface),
				source.Surface.Width,
				source.Surface.Height,
				provider),
			split => SaveSpritesheetAnalysis (document, source, split),
			source.SpritesheetSplit);

		SpritesheetSplitData? split = await dialog.RunAsync ();
		if (split is null)
			return;

		tools.Commit ();
		ApplySingleDirectionSplit (document, source, info, split);
	}

	private static void ApplySingleDirectionSplit (
		Document document,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split)
	{
		CompoundHistoryItem history = new (Resources.Icons.ImageCrop, Translations.GetString ("Create Single-Direction Animation"));
		SaveFinalSplit (source, split, history, sourceIsNew: false);
		SingleDirectionAnimationLayer? layer = FindSiblingSingleDirectionOutputLayer (document, source);
		SingleDirectionAnimationLayerSnapshot incoming = CreateSingleDirectionSnapshot (source, info, split, PointD.Zero);

		if (layer is null) {
			layer = document.Layers.CreateSingleDirectionAnimationLayer (
				"SingleDirectionAnimationLayer",
				split.CanvasWidth,
				split.CanvasHeight);
			layer.Metadata["pinta.single-direction-animation.source-layer"] = source.Name;
			layer.ReplaceSnapshot (incoming, document.ImageSize);
			LayerPosition position = document.Layers.GetPosition (source) with {
				Index = document.Layers.GetPosition (source).Index + 1,
			};
			document.Layers.Insert (layer, position);
			history.Push (CreateAddHistory (document, layer));
		} else {
			SingleDirectionAnimationLayerSnapshot old = layer.CaptureSnapshot ();
			incoming = new (
				incoming.DirectionId,
				incoming.CanvasWidth,
				incoming.CanvasHeight,
				old.PositionOffset,
				incoming.Animations);
			layer.ReplaceSnapshot (incoming, document.ImageSize);
			history.Push (new SingleDirectionAnimationLayerDataHistoryItem (document, layer, old, layer.CaptureSnapshot ()));
		}

		document.Layers.SetCurrentUserLayer (layer);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

	private static SingleDirectionAnimationLayer? FindSiblingSingleDirectionOutputLayer (
		Document document,
		UserLayer source)
		=> (source.Parent?.Children ?? document.Layers.RootLayers)
			.OfType<SingleDirectionAnimationLayer> ()
			.FirstOrDefault (layer => layer.Metadata.GetValueOrDefault ("pinta.single-direction-animation.source-layer") == source.Name);

	private static SingleDirectionAnimationLayerSnapshot CreateSingleDirectionSnapshot (
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split,
		PointD positionOffset)
	{
		SingleDirectionAnimationData animation = new (info.ActionId, split.CanvasWidth, split.CanvasHeight);
		for (int index = 0; index < split.Frames.Count; index++) {
			SpritesheetFrameSplit placement = split.Frames[index];
			using Cairo.ImageSurface crop = CreateSplitFrameSurface (source, info, split, index);
			animation.Frames.Add (new AnimationFrameData (
				index,
				placement.X,
				placement.Y,
				placement.Visible,
				crop.Clone ()));
		}

		return new SingleDirectionAnimationLayerSnapshot (
			SingleDirectionAnimationLayer.DefaultDirectionId,
			split.CanvasWidth,
			split.CanvasHeight,
			positionOffset,
			[animation]);
	}

	private UserLayer InsertSingleDirectionAnimationAttempt (
		Document document,
		byte[] png,
		AI.SpritesheetAttemptInfo info)
	{
		if (info.DirectionIds.Count != 1 || info.DirectionIds[0] != SingleDirectionAnimationLayer.DefaultDirectionId)
			throw new InvalidOperationException ("A single-direction animation request must contain one default direction.");

		tools.Commit ();
		CompoundHistoryItem history = new (Resources.Icons.LayerDuplicate, Translations.GetString ("Generate Single-Direction Animation"));
		GroupLayer root = FindOrCreateGroup (document, null, "single-direction-animation", history);
		MoveSpritesheetRootToTop (document, root, history);
		GroupLayer actions = FindOrCreateGroup (document, root, "actions", history);
		GroupLayer action = FindOrCreateGroup (document, actions, info.ActionId, history);
		GroupLayer attempt = document.Layers.CreateGroupLayer (GetNextAttemptName (action));
		document.Layers.Insert (attempt, new LayerPosition (action, action.Children.Count));
		history.Push (CreateAddHistory (document, attempt));

		UserLayer source = document.Layers.CreateLayer ("source-sequence", info.ImageSize.Width, info.ImageSize.Height);
		DrawPngOnLayer (png, source);
		string json = System.Text.Json.JsonSerializer.Serialize (info);
		attempt.Metadata["pinta.single-direction-animation.attempt"] = json;
		source.Metadata["pinta.single-direction-animation.attempt"] = json;
		document.Layers.Insert (source, new LayerPosition (attempt, 0));
		history.Push (CreateAddHistory (document, source));
		document.Layers.SetCurrentUserLayer (source);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
		return source;
	}

	private async Task OpenGeneratedSingleDirectionEditorAsync (
		Document document,
		UserLayer source,
		AI.SpritesheetAttemptInfo info)
	{
		using SingleDirectionAnimationDialog dialog = new (
			chrome.MainWindow,
			source,
			info,
			[],
			provider => sprite_segmentation.AnalyzeAsync (
				CreateSurfacePng (source.Surface),
				source.Surface.Width,
				source.Surface.Height,
				provider),
			split => SaveSpritesheetAnalysis (document, source, split),
			source.SpritesheetSplit);

		SpritesheetSplitData? split = await dialog.RunAsync ();
		if (split is null)
			return;

		tools.Commit ();
		ApplySingleDirectionSplit (document, source, info, split);
	}

	private async Task<bool> ConfirmGeneratedSingleDirectionAsync (
		UserLayer source,
		AI.SpritesheetAttemptInfo info)
	{
		using PintaDialog dialog = PintaDialog.NewWithProperties ([]);
		dialog.Title = Translations.GetString ("Confirm Generated Sequence");
		dialog.TransientFor = chrome.MainWindow;
		dialog.DefaultWidth = 900;
		dialog.DefaultHeight = 700;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget continueButton = dialog.AddButton (
			Translations.GetString ("Continue to Frame Extraction"),
			(int) Gtk.ResponseType.Ok);
		continueButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		Gtk.Box content = dialog.GetContentAreaBox ();
		content.SetAllMargins (12);
		content.Spacing = 8;
		Gtk.Label message = Gtk.Label.New (
			Translations.GetString ("Review the generated sequence image. Continue only if it is ready for frame analysis and cropping."));
		message.Wrap = true;
		message.Halign = Gtk.Align.Start;
		content.Append (message);
		Gtk.Expander promptExpander = Gtk.Expander.New (
			Translations.GetString ("Prompt"));
		Gtk.TextView promptView = Gtk.TextView.New ();
		promptView.Editable = false;
		promptView.WrapMode = Gtk.WrapMode.WordChar;
		promptView.SetSizeRequest (-1, 120);
		promptView.Buffer!.SetText (info.Prompt, -1);
		promptExpander.Child = promptView;
		content.Append (promptExpander);

		Gtk.Picture preview = Gtk.Picture.New ();
		preview.ContentFit = Gtk.ContentFit.ScaleDown;
		preview.Hexpand = true;
		preview.Vexpand = true;
		preview.SetSizeRequest (640, 520);
		preview.Paintable = source.Surface.ToTexture ();
		content.Append (preview);

		Gtk.ResponseType response = await dialog.RunAsync ();
		dialog.Close ();
		return response == Gtk.ResponseType.Ok;
	}

	private async Task OpenSingleDirectionAnimationEditorAsync (
		Document document,
		SingleDirectionAnimationLayer layer)
	{
		SingleDirectionEditorSource editorSource = CreateEditorSource (layer);
		using SingleDirectionAnimationDialog dialog = new (
			chrome.MainWindow,
			editorSource.Source,
			editorSource.Info,
			[],
			provider => sprite_segmentation.AnalyzeAsync (
				CreateSurfacePng (editorSource.Source.Surface),
				editorSource.Source.Surface.Width,
				editorSource.Source.Surface.Height,
				provider),
			_ => { },
			editorSource.SavedAnalysis,
			editorSource.FrameSurfaces,
			editorSource.ExistingFrames,
			editing: true);

		SpritesheetSplitData? split = await dialog.RunAsync ();
		if (split is null)
			return;

		SingleDirectionAnimationLayerSnapshot incoming = CreateSnapshot (layer, editorSource, split);
		SingleDirectionAnimationLayerSnapshot old = layer.CaptureSnapshot ();
		layer.ReplaceSnapshot (incoming, document.ImageSize);
		document.History.PushNewItem (new SingleDirectionAnimationLayerDataHistoryItem (document, layer, old, layer.CaptureSnapshot ())); 
		document.Workspace.Invalidate ();
	}

	private static SingleDirectionEditorSource CreateEditorSource (SingleDirectionAnimationLayer layer)
	{
		SingleDirectionAnimationData animation = layer.Animations.FirstOrDefault ()
			?? new SingleDirectionAnimationData ("sequence", layer.CanvasWidth, layer.CanvasHeight);
		List<AnimationFrameData> frames = [.. animation.Frames.OrderBy (frame => frame.FrameIndex)];
		int frameCount = Math.Max (1, frames.Count);
		int cellWidth = Math.Max (1, frames.Select (frame => frame.Surface.Width).DefaultIfEmpty (1).Max ());
		int cellHeight = Math.Max (1, frames.Select (frame => frame.Surface.Height).DefaultIfEmpty (1).Max ());
		UserLayer source = new (CairoExtensions.CreateImageSurface (Format.Argb32, cellWidth * frameCount, cellHeight)) {
			Name = layer.Name,
		};
		List<ImageSurface> surfaces = [];
		List<SpritesheetFrameSplit> placements = [];
		List<RectangleI> rectangles = [];
		using (Context context = new (source.Surface)) {
			for (int index = 0; index < frameCount; index++) {
				AnimationFrameData? frame = index < frames.Count ? frames[index] : null;
				ImageSurface surface = frame?.Surface.Clone ()
					?? CairoExtensions.CreateImageSurface (Format.Argb32, cellWidth, cellHeight);
				surfaces.Add (surface);
				if (frame is not null) {
					context.SetSourceSurface (frame.Surface, index * cellWidth, 0);
					context.Paint ();
				}
				placements.Add (new SpritesheetFrameSplit (frame?.X ?? 0, frame?.Y ?? 0, frame?.Visible ?? false));
				rectangles.Add (new RectangleI (index * cellWidth, 0, cellWidth, cellHeight));
			}
		}

		AI.SpritesheetAttemptInfo info = new (
			false,
			animation.ActionId,
			[layer.DirectionId],
			frameCount,
			frameCount,
			1,
			string.Empty,
			new Size (source.Surface.Width, source.Surface.Height),
			string.Empty,
			string.Empty,
			string.Empty,
			1);
		SpritesheetSplitData savedAnalysis = new (
			frameCount,
			1,
			cellWidth,
			cellHeight,
			0,
			0,
			0,
			0,
			layer.CanvasWidth,
			layer.CanvasHeight,
			false,
			placements,
			rectangles);
		return new (source, info, surfaces, placements, savedAnalysis);
	}

	private static SingleDirectionAnimationLayerSnapshot CreateSnapshot (
		SingleDirectionAnimationLayer layer,
		SingleDirectionEditorSource editorSource,
		SpritesheetSplitData split)
	{
		SingleDirectionAnimationData animation = new (
			editorSource.Info.ActionId,
			split.CanvasWidth,
			split.CanvasHeight);
		for (int index = 0; index < split.Frames.Count; index++) {
			SpritesheetFrameSplit placement = split.Frames[index];
			ImageSurface surface = index < editorSource.FrameSurfaces.Count
				? editorSource.FrameSurfaces[index].Clone ()
				: CreateSplitFrameSurface (editorSource.Source, editorSource.Info, split, index);
			animation.Frames.Add (new AnimationFrameData (
				index,
				placement.X,
				placement.Y,
				placement.Visible,
				surface));
		}

		return new SingleDirectionAnimationLayerSnapshot (
			layer.DirectionId,
			split.CanvasWidth,
			split.CanvasHeight,
			layer.PositionOffset,
			[animation]);
	}

	private sealed record SingleDirectionEditorSource (
		UserLayer Source,
		AI.SpritesheetAttemptInfo Info,
		IReadOnlyList<ImageSurface> FrameSurfaces,
		IReadOnlyList<SpritesheetFrameSplit> ExistingFrames,
		SpritesheetSplitData SavedAnalysis);

	private sealed class SingleDirectionAnimationLayerDataHistoryItem : BaseHistoryItem
	{
		private readonly Document document;
		private readonly SingleDirectionAnimationLayer layer;
		private readonly SingleDirectionAnimationLayerSnapshot oldSnapshot;
		private readonly SingleDirectionAnimationLayerSnapshot newSnapshot;

		public SingleDirectionAnimationLayerDataHistoryItem (
			Document document,
			SingleDirectionAnimationLayer layer,
			SingleDirectionAnimationLayerSnapshot oldSnapshot,
			SingleDirectionAnimationLayerSnapshot newSnapshot)
			: base (Resources.Icons.ImageCrop, Translations.GetString ("Edit Single-Direction Animation"))
		{
			this.document = document;
			this.layer = layer;
			this.oldSnapshot = oldSnapshot;
			this.newSnapshot = newSnapshot;
		}

		public override void Undo () => layer.ReplaceSnapshot (oldSnapshot, document.ImageSize);
		public override void Redo () => layer.ReplaceSnapshot (newSnapshot, document.ImageSize);
	}
}
