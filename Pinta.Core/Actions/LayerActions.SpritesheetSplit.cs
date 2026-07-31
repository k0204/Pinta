using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersSplitSpritesheetActivated (object sender, EventArgs e)
	{
		Document document = workspace.ActiveDocument;
		UserLayer source = document.Layers.CurrentUserLayer;
		if (!TryGetSpritesheetAttempt (source, out UserLayer? attempt, out AI.SpritesheetAttemptInfo? info)
			|| attempt is null
			|| info is null)
			return;

		using SpritesheetSplitDialog dialog = new (
			chrome.MainWindow,
			source,
			info,
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
		ApplySpritesheetSplit (document, attempt, source, info, split);
	}

	private static void ApplySpritesheetSplit (
		Document document,
		UserLayer attempt,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split)
	{
		CompoundHistoryItem history = new (Resources.Icons.ImageCrop, Translations.GetString ("Split Spritesheet"));
		bool newAttempt = attempt.Children.Any (child => child is GroupLayer);
		if (newAttempt)
			(attempt, source) = CreateResplitAttempt (document, attempt, source, history);

		UserLayer last = source;
		foreach (IGrouping<string, int> group in Enumerable.Range (0, split.Frames.Count).GroupBy (cell => GetFrameGroupName (info, cell))) {
			GroupLayer direction = document.Layers.CreateGroupLayer (group.Key);
			foreach (int cell in group) {
				string name = GetFrameName (info, cell);
				UserLayer frame = CreateFrameLayer (document, source, info, split, cell, name);
				direction.InsertChild (direction.Children.Count, frame);
				last = frame;
			}
			document.Layers.Insert (direction, new LayerPosition (attempt, attempt.Children.Count));
			if (!newAttempt)
				history.Push (CreateAddHistory (document, direction));
		}

		history.Push (new SpritesheetSplitHistoryItem (
			attempt,
			split,
			Translations.GetString ("Split Spritesheet")));
		attempt.SpritesheetSplit = split;
		document.Layers.SetCurrentUserLayer (last);
		document.History.PushNewItem (history);
		document.Workspace.Invalidate ();
	}

	private static void SaveSpritesheetAnalysis (
		Document document,
		UserLayer source,
		SpritesheetSplitData split)
	{
		if (source.SpritesheetSplit == split)
			return;

		SpritesheetSplitHistoryItem history = new (
			source,
			split,
			Translations.GetString ("Analyze Spritesheet"));
		source.SpritesheetSplit = split;
		document.History.PushNewItem (history);
	}

	private static (UserLayer Attempt, UserLayer Source) CreateResplitAttempt (
		Document document,
		UserLayer currentAttempt,
		UserLayer currentSource,
		CompoundHistoryItem history)
	{
		UserLayer action = currentAttempt.Parent
			?? throw new InvalidOperationException ("A spritesheet attempt must belong to an action group.");
		GroupLayer attempt = document.Layers.CreateGroupLayer (GetNextAttemptName (action));
		foreach ((string key, string value) in currentAttempt.Metadata)
			attempt.Metadata.Add (key, value);
		attempt.SpritesheetSplit = currentAttempt.SpritesheetSplit;

		UserLayer source = document.Layers.CreateLayer ("source-sheet", currentSource.Surface.Width, currentSource.Surface.Height);
		using (Cairo.Context context = new (source.Surface)) {
			context.SetSourceSurface (currentSource.Surface, 0, 0);
			context.Paint ();
		}
		foreach ((string key, string value) in currentSource.Metadata)
			source.Metadata.Add (key, value);
		source.SpritesheetSplit = currentSource.SpritesheetSplit;
		attempt.InsertChild (0, source);
		document.Layers.Insert (attempt, new LayerPosition (action, action.Children.Count));
		history.Push (CreateAddHistory (document, attempt));
		return (attempt, source);
	}

	private static UserLayer CreateFrameLayer (
		Document document,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		SpritesheetSplitData split,
		int cell,
		string name)
	{
		using Cairo.ImageSurface crop = CreateSplitFrameSurface (source, info, split, cell);
		UserLayer frame = document.Layers.CreateLayer (name, split.CanvasWidth, split.CanvasHeight);
		SpritesheetFrameSplit placement = split.Frames[cell];
		using (Cairo.Context context = new (frame.Surface)) {
			context.SetSourceSurface (crop, placement.X, placement.Y);
			context.Paint ();
		}
		frame.Hidden = !placement.Visible;
		frame.Surface.MarkDirty ();
		return frame;
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

	private static string GetFrameGroupName (AI.SpritesheetAttemptInfo info, int cell)
	{
		int expected = info.DirectionIds.Count * info.FrameCount;
		return cell < expected ? info.DirectionIds[cell / info.FrameCount] : "extra";
	}

	private static string GetFrameName (AI.SpritesheetAttemptInfo info, int cell)
	{
		int expected = info.DirectionIds.Count * info.FrameCount;
		int frame = cell < expected ? cell % info.FrameCount : cell - expected;
		return $"frame-{frame + 1:D2}";
	}

	private sealed class SpritesheetSplitHistoryItem : BaseHistoryItem
	{
		private readonly UserLayer layer;
		private readonly SpritesheetSplitData? old_value;
		private readonly SpritesheetSplitData new_value;

		public SpritesheetSplitHistoryItem (
			UserLayer layer,
			SpritesheetSplitData newValue,
			string text)
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
