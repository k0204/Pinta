using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed class AnimationFrameSequenceData
{
	public List<AnimationFrameData> Frames { get; } = [];

	internal AnimationFrameSequenceData Clone ()
	{
		AnimationFrameSequenceData result = new ();
		result.Frames.AddRange (Frames.Select (frame => frame.Clone ()));
		return result;
	}
}
