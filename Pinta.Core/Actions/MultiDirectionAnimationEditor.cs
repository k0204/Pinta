using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal sealed class MultiDirectionAnimationEditor : AnimationFrameEditorBase
{
	private static readonly string[] clockwise_direction_ids = [
		"down", "down-right", "right", "up-right",
		"up", "up-left", "left", "down-left",
	];

	public MultiDirectionAnimationEditor (
		Gtk.Window hostWindow,
		Action<bool> setSubmitSensitive,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> outputAttempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> saveAnalysis,
		SpritesheetSplitData? savedAnalysis,
		IReadOnlyList<ImageSurface>? frameSurfaces,
		IReadOnlyList<SpritesheetFrameSplit>? existingFrames,
		bool allowAiAnalysis)
		: base (hostWindow, setSubmitSensitive, source, info, outputAttempts, analyze, saveAnalysis, savedAnalysis, frameSurfaces, existingFrames, allowAiAnalysis)
	{
	}

	protected override int ExpectedFrameCount => info.DirectionIds.Count * info.FrameCount;

	protected override int[] CreateFrameNavigationOrder (int frameCount)
		=> [.. Enumerable.Range (0, frameCount)
			.OrderBy (GetDirectionOrder)
			.ThenBy (GetFrameOrder)
			.ThenBy (index => index)];

	protected override string GetFrameLabel (int displayIndex, int index)
	{
		if (index >= ExpectedFrameCount)
			return Translations.GetString ("Cell {0} (extra)", displayIndex + 1);

		int direction = index / info.FrameCount;
		int frame = index % info.FrameCount;
		return $"{displayIndex + 1}: {info.DirectionIds[direction]} / {Translations.GetString ("Frame {0}", frame + 1)}";
	}

	private int GetDirectionOrder (int index)
	{
		if (index >= ExpectedFrameCount)
			return clockwise_direction_ids.Length + info.DirectionIds.Count;

		string directionId = info.DirectionIds[index / info.FrameCount];
		int rank = Array.IndexOf (clockwise_direction_ids, directionId);
		return rank >= 0 ? rank : clockwise_direction_ids.Length + index / info.FrameCount;
	}

	private int GetFrameOrder (int index)
		=> index >= ExpectedFrameCount ? index - ExpectedFrameCount : index % info.FrameCount;
}
