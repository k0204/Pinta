using Cairo;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	internal bool IsSpritesheetOutputLayer (UserLayer layer)
	{
		for (UserLayer? current = layer; current is not null; current = current.Parent)
			if (current.Metadata.ContainsKey (SpritesheetLayerMetadata.OutputCanvas))
				return true;

		return false;
	}

	internal void UpdateSpritesheetOutputTransforms ()
	{
		foreach (UserLayer layer in AllLayers) {
			if (layer is not GroupLayer
				|| !layer.Metadata.ContainsKey (SpritesheetLayerMetadata.OutputCanvas)
				|| layer.SpritesheetSplit is not SpritesheetSplitData split)
				continue;

			layer.Transform = SpritesheetLayerMetadata.CreateAnchorTransform (document.ImageSize);
			Matrix outputTransform = SpritesheetLayerMetadata.CreateOutputTransform (
				document.ImageSize,
				new Size (split.CanvasWidth, split.CanvasHeight));

			foreach (UserLayer descendant in layer.GetSelfAndDescendants ())
				if (descendant is not GroupLayer)
					descendant.Transform = outputTransform.Clone ();
		}
	}
}
