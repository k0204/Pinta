using System.Linq;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	internal bool IsSpritesheetOutputLayer (UserLayer layer)
		=> layer is SpriteSheetLayer;

	internal void UpdateSpritesheetOutputTransforms ()
	{
		foreach (SpriteSheetLayer layer in AllLayers.OfType<SpriteSheetLayer> ())
			layer.UpdateTransforms (document.ImageSize);
	}
}
