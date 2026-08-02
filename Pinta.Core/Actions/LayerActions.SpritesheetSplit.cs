using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersSplitSpritesheetActivated (object sender, EventArgs e)
	{
		Document document = workspace.ActiveDocument;
		UserLayer selected = document.Layers.CurrentUserLayer;
		SpriteSheetLayer? editingLayer = selected as SpriteSheetLayer;
		UserLayer source = selected;
		UserLayer? attempt = null;
		AI.SpritesheetAttemptInfo info;
		IReadOnlyList<Cairo.ImageSurface>? frameSurfaces = null;
		IReadOnlyList<SpritesheetFrameSplit>? existingFrames = null;
		SpritesheetSplitData? savedAnalysis = null;

		if (editingLayer is not null) {
			(source, info, frameSurfaces, existingFrames, savedAnalysis) = CreateSpriteSheetEditorSource (editingLayer);
		} else if (!TryGetSpritesheetAttempt (selected, out attempt, out AI.SpritesheetAttemptInfo? existingInfo)) {
			if (!CanCreateSpritesheetAnimation (selected))
				return;
			info = CreateDefaultSpritesheetInfo (selected);
		} else if (attempt is null || existingInfo is null) {
			return;
		} else {
			info = existingInfo;
		}

		using SpritesheetSplitDialog dialog = new (
			chrome.MainWindow,
			source,
			info,
			attempt is null ? [] : GetCompatibleSplitTargets (attempt, info),
			provider => sprite_segmentation.AnalyzeAsync (
				CreateSurfacePng (source.Surface),
				source.Surface.Width,
				source.Surface.Height,
				provider),
			split => SaveSpritesheetAnalysis (document, source, split),
			savedAnalysis ?? source.SpritesheetSplit,
			frameSurfaces,
			existingFrames,
			editingLayer is not null);
		SpritesheetSplitData? split = await dialog.RunAsync ();
		if (split is null)
			return;

		tools.Commit ();
		ApplySpritesheetSplit (document, attempt, source, info, split, dialog.OutputAttempt, editingLayer, frameSurfaces);
	}

	private static AI.SpritesheetAttemptInfo CreateDefaultSpritesheetInfo (UserLayer source)
		=> new (
			false,
			"sequence",
			["default"],
			1,
			1,
			1,
			string.Empty,
			new Size (source.Surface.Width, source.Surface.Height),
			string.Empty,
			string.Empty,
			string.Empty,
			1);

	private static (
		UserLayer Source,
		AI.SpritesheetAttemptInfo Info,
		IReadOnlyList<Cairo.ImageSurface> Surfaces,
		IReadOnlyList<SpritesheetFrameSplit> Frames,
		SpritesheetSplitData SavedAnalysis) CreateSpriteSheetEditorSource (SpriteSheetLayer layer)
	{
		SpriteSheetAnimationData animation = layer.Animations.FirstOrDefault ()
			?? new SpriteSheetAnimationData ("sequence", layer.CanvasWidth, layer.CanvasHeight);
		List<SpriteSheetDirectionData> directions = animation.Directions.Count > 0
			? animation.Directions
			: [new SpriteSheetDirectionData ("default")];
		List<List<SpriteSheetFrameData>> orderedFrames = [.. directions.Select (
			direction => direction.Frames.OrderBy (frame => frame.FrameIndex).ToList ())];
		int frameCount = Math.Max (1, orderedFrames.Select (frames => frames.Count).DefaultIfEmpty (0).Max ());
		int cellWidth = Math.Max (1, orderedFrames.SelectMany (frames => frames).Select (frame => frame.Surface.Width).DefaultIfEmpty (1).Max ());
		int cellHeight = Math.Max (1, orderedFrames.SelectMany (frames => frames).Select (frame => frame.Surface.Height).DefaultIfEmpty (1).Max ());
		int count = Math.Max (1, directions.Count * frameCount);
		UserLayer source = new (CairoExtensions.CreateImageSurface (Cairo.Format.Argb32, cellWidth * frameCount, cellHeight * directions.Count)) {
			Name = layer.Name,
		};
		List<Cairo.ImageSurface> surfaces = [];
		List<SpritesheetFrameSplit> placements = [];
		List<RectangleI> rectangles = [];
		using (Cairo.Context context = new (source.Surface)) {
			for (int index = 0; index < count; index++) {
				int directionIndex = index / frameCount;
				int frameIndex = index % frameCount;
				SpriteSheetFrameData? frame = frameIndex < orderedFrames[directionIndex].Count
					? orderedFrames[directionIndex][frameIndex]
					: null;
				Cairo.ImageSurface surface = frame?.Surface.Clone ()
					?? CairoExtensions.CreateImageSurface (Cairo.Format.Argb32, cellWidth, cellHeight);
				surfaces.Add (surface);
				if (frame is not null) {
					context.SetSourceSurface (frame.Surface, frameIndex * cellWidth, directionIndex * cellHeight);
					context.Paint ();
				}
				placements.Add (new SpritesheetFrameSplit (frame?.X ?? 0, frame?.Y ?? 0, frame?.Visible ?? false));
				rectangles.Add (new RectangleI (frameIndex * cellWidth, directionIndex * cellHeight, cellWidth, cellHeight));
			}
		}

		AI.SpritesheetAttemptInfo info = new (
			false,
			animation.ActionId,
			[.. directions.Select (direction => direction.DirectionId)],
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
			directions.Count,
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
		return (source, info, surfaces, placements, savedAnalysis);
	}

	private static IReadOnlyList<UserLayer> GetCompatibleSplitTargets (
		UserLayer sourceAttempt,
		AI.SpritesheetAttemptInfo sourceInfo)
	{
		if (sourceAttempt.Parent is not UserLayer action)
			return [];

		return [.. action.Children.Where (candidate =>
			candidate is GroupLayer and not SpriteSheetLayer
			&& candidate != sourceAttempt
			&& candidate.Children.Any (child => child is SpriteSheetLayer)
			&& TryGetSpritesheetAttemptInfo (candidate, out AI.SpritesheetAttemptInfo? targetInfo)
			&& targetInfo?.ActionId == sourceInfo.ActionId
			&& targetInfo.FrameCount == sourceInfo.FrameCount)];
	}

	private static void ApplySpritesheetSplit (
		Document document,
		UserLayer? attempt,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split,
		UserLayer? outputAttempt,
		SpriteSheetLayer? editingLayer,
		IReadOnlyList<Cairo.ImageSurface>? frameSurfaces)
	{
		CompoundHistoryItem history = new (Resources.Icons.ImageCrop, Translations.GetString ("Create Animation Frames"));
		if (outputAttempt is not null) {
			attempt = outputAttempt;
			MergeAttemptDirections (attempt, info, history);
		}

		if (editingLayer is null)
			SaveFinalSplit (source, split, history, outputAttempt is not null);
		SpriteSheetLayerSnapshot incoming = CreateSnapshot (source, info, split, frameSurfaces);
		SpriteSheetLayer? layer = editingLayer
			?? attempt?.Children.OfType<SpriteSheetLayer> ().FirstOrDefault ()
			?? FindSiblingOutputLayer (document, source);

		if (layer is null) {
			layer = document.Layers.CreateSpriteSheetLayer ("SpriteSheetLayer", split.CanvasWidth, split.CanvasHeight);
			if (attempt is null)
				layer.Metadata["pinta.spritesheet.source-layer"] = source.Name;
			layer.ReplaceSnapshot (incoming, document.ImageSize);
			LayerPosition position = attempt is not null
				? new LayerPosition (attempt, attempt.Children.Count)
				: document.Layers.GetPosition (source) with { Index = document.Layers.GetPosition (source).Index + 1 };
			document.Layers.Insert (layer, position);
			history.Push (CreateAddHistory (document, layer));
		} else {
			SpriteSheetLayerSnapshot old = layer.CaptureSnapshot ();
			incoming = new SpriteSheetLayerSnapshot (
				incoming.CanvasWidth,
				incoming.CanvasHeight,
				old.PositionOffset,
				incoming.Animations);
			if (outputAttempt is null && editingLayer is null)
				layer.ReplaceSnapshot (incoming, document.ImageSize);
			else
				layer.MergeSnapshot (incoming, document.ImageSize);
			history.Push (new SpriteSheetLayerDataHistoryItem (document, layer, old, layer.CaptureSnapshot ()));
		}

		document.Layers.SetCurrentUserLayer (layer);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

	private static SpriteSheetLayer? FindSiblingOutputLayer (Document document, UserLayer source)
		=> (source.Parent?.Children ?? document.Layers.RootLayers)
			.OfType<SpriteSheetLayer> ()
			.FirstOrDefault (layer => layer.Metadata.GetValueOrDefault ("pinta.spritesheet.source-layer") == source.Name);

	private static SpriteSheetLayerSnapshot CreateSnapshot (
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split,
		IReadOnlyList<Cairo.ImageSurface>? frameSurfaces)
	{
		SpriteSheetAnimationData animation = new (info.ActionId, split.CanvasWidth, split.CanvasHeight);
		SpriteSheetLayerSnapshot result = new (split.CanvasWidth, split.CanvasHeight, PointD.Zero, [animation]);
		int expected = info.DirectionIds.Count * info.FrameCount;

		for (int cell = 0; cell < split.Frames.Count; cell++) {
			string directionId = cell < expected ? info.DirectionIds[cell / info.FrameCount] : "extra";
			int frameIndex = cell < expected ? cell % info.FrameCount : cell - expected;
			SpriteSheetDirectionData direction = animation.Directions.FirstOrDefault (item => item.DirectionId == directionId)
				?? animation.AddDirection (directionId);
			using Cairo.ImageSurface crop = frameSurfaces is not null && cell < frameSurfaces.Count
				? frameSurfaces[cell].Clone ()
				: CreateSplitFrameSurface (source, info, split, cell);
			SpritesheetFrameSplit placement = split.Frames[cell];
			direction.Frames.Add (new SpriteSheetFrameData (frameIndex, placement.X, placement.Y, placement.Visible, crop.Clone ()));
		}

		return result;
	}

	private static void MergeAttemptDirections (
		UserLayer attempt,
		AI.SpritesheetAttemptInfo sourceInfo,
		CompoundHistoryItem history)
	{
		if (!TryGetSpritesheetAttemptInfo (attempt, out AI.SpritesheetAttemptInfo? targetInfo) || targetInfo is null)
			return;

		string[] directions = [.. targetInfo.DirectionIds.Concat (sourceInfo.DirectionIds).Distinct ()];
		string json = System.Text.Json.JsonSerializer.Serialize (targetInfo with { DirectionIds = directions });
		history.Push (new SpritesheetMetadataHistoryItem (attempt, spritesheet_attempt_metadata, json));
		SetMetadata (attempt, spritesheet_attempt_metadata, json);
	}

	private static void SaveFinalSplit (
		UserLayer source,
		SpritesheetSplitData split,
		CompoundHistoryItem history,
		bool sourceIsNew)
	{
		if (sourceIsNew || source.SpritesheetSplit == split) {
			source.SpritesheetSplit = split;
			return;
		}

		history.Push (new SpritesheetSplitHistoryItem (source, split, Translations.GetString ("Create Animation Frames")));
		source.SpritesheetSplit = split;
	}

	private static void SaveSpritesheetAnalysis (Document document, UserLayer source, SpritesheetSplitData split)
	{
		if (source.SpritesheetSplit == split)
			return;

		SpritesheetSplitHistoryItem history = new (source, split, Translations.GetString ("Analyze Spritesheet"));
		source.SpritesheetSplit = split;
		document.History.PushNewItem (history);
	}

	internal static Cairo.ImageSurface CreateSplitFrameSurface (
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split,
		int cell)
	{
		RectangleI bounds = split.SourceRectangles is not null
			? split.SourceRectangles[cell]
			: GetGridCellBounds (split, cell);
		Cairo.ImageSurface crop = CairoExtensions.CreateImageSurface (Cairo.Format.Argb32, bounds.Width, bounds.Height);
		using (Cairo.Context context = new (crop)) {
			context.SetSourceSurface (source.Surface, -bounds.X, -bounds.Y);
			context.Paint ();
		}
		if (split.AlignCharacter)
			AlignSpriteFrame (crop, info.BackgroundId, alignBaseline: info.ActionId != "jump");
		return crop;
	}

	private static RectangleI GetGridCellBounds (SpritesheetSplitData split, int cell)
	{
		int column = cell % split.Columns;
		int row = cell / split.Columns;
		return new (
			split.OffsetX + column * (split.CellWidth + split.GapX),
			split.OffsetY + row * (split.CellHeight + split.GapY),
			split.CellWidth,
			split.CellHeight);
	}

	private sealed class SpriteSheetLayerDataHistoryItem : BaseHistoryItem
	{
		private readonly Document document;
		private readonly SpriteSheetLayer layer;
		private readonly SpriteSheetLayerSnapshot oldSnapshot;
		private readonly SpriteSheetLayerSnapshot newSnapshot;

		public SpriteSheetLayerDataHistoryItem (
			Document document,
			SpriteSheetLayer layer,
			SpriteSheetLayerSnapshot oldSnapshot,
			SpriteSheetLayerSnapshot newSnapshot)
			: base (Resources.Icons.ImageCrop, Translations.GetString ("Create Animation Frames"))
		{
			this.document = document;
			this.layer = layer;
			this.oldSnapshot = oldSnapshot;
			this.newSnapshot = newSnapshot;
		}

		public override void Undo () => layer.ReplaceSnapshot (oldSnapshot, document.ImageSize);
		public override void Redo () => layer.ReplaceSnapshot (newSnapshot, document.ImageSize);
	}

	private sealed class SpritesheetSplitHistoryItem : BaseHistoryItem
	{
		private readonly UserLayer layer;
		private readonly SpritesheetSplitData? old_value;
		private readonly SpritesheetSplitData new_value;

		public SpritesheetSplitHistoryItem (UserLayer layer, SpritesheetSplitData newValue, string text)
			: base (Resources.Icons.ImageCrop, text)
		{
			this.layer = layer;
			old_value = layer.SpritesheetSplit;
			new_value = newValue;
		}

		public override void Undo () => layer.SpritesheetSplit = old_value;
		public override void Redo () => layer.SpritesheetSplit = new_value;
	}

}
