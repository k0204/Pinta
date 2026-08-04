using System.Linq;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	internal bool IsAnimationOutputLayer (UserLayer layer)
		=> layer is AnimationOutputLayer;

	internal void UpdateAnimationOutputTransforms ()
	{
		foreach (AnimationOutputLayer layer in AllLayers.OfType<AnimationOutputLayer> ())
			layer.UpdateTransforms (document.ImageSize);
	}
}
