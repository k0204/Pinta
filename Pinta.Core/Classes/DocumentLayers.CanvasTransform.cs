using System;
using Cairo;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	public void TranslateLayerTree (UserLayer root, PointD delta)
	{
		if (!ContainsLayer (root))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (root));
		if (!double.IsFinite (delta.X) || !double.IsFinite (delta.Y))
			throw new ArgumentOutOfRangeException (nameof (delta));
		if (delta == PointD.Zero)
			return;

		TranslateNode (root, delta);
		document.Workspace.Invalidate ();
	}

	private void TranslateNode (UserLayer node, PointD delta)
	{
		if (node is AnimationOutputLayer animationLayer) {
			animationLayer.SetPositionOffset (animationLayer.PositionOffset + delta, document.ImageSize);
		} else {
			foreach (Layer layer in node.GetOwnLayersToPaint ())
				Translate (layer, delta);
		}

		foreach (UserLayer child in node.Children)
			TranslateNode (child, delta);
	}

	private static void Translate (Layer layer, PointD delta)
	{
		Matrix transform = CairoExtensions.CreateIdentityMatrix ();
		transform.Translate (delta.X, delta.Y);
		transform.Multiply (layer.Transform);
		layer.Transform = transform;
	}
}
