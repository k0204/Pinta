using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal sealed class SingleDirectionAnimationEditor : AnimationFrameEditorBase
{
	public SingleDirectionAnimationEditor (
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

	protected override int ExpectedFrameCount => info.FrameCount;

	protected override int[] CreateFrameNavigationOrder (int frameCount)
		=> [.. Enumerable.Range (0, frameCount)];

	protected override string GetFrameLabel (int displayIndex, int index)
	{
		if (index >= ExpectedFrameCount)
			return Translations.GetString ("Cell {0} (extra)", displayIndex + 1);

		return $"{displayIndex + 1}: {Translations.GetString ("Frame {0}", index + 1)}";
	}
}
