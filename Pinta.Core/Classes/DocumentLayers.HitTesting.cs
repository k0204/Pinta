using System;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	/// <summary>
	/// Finds the topmost visible user layer with a non-transparent pixel at the specified canvas point.
	/// </summary>
	public UserLayer? FindTopmostLayerAtPoint (PointD point)
	{
		for (int i = user_layers.Count - 1; i >= 0; i--) {
			UserLayer? match = FindTopmostLayerAtPoint (user_layers[i], point);
			if (match is not null)
				return match;
		}

		return null;
	}

	private UserLayer? FindTopmostLayerAtPoint (UserLayer userLayer, PointD point)
	{
		if (userLayer.Hidden)
			return null;

		for (int i = userLayer.Children.Count - 1; i >= 0; i--) {
			UserLayer? match = FindTopmostLayerAtPoint (userLayer.Children[i], point);
			if (match is not null)
				return match;
		}

		if (userLayer == current_user_layer
			&& ShowSelectionLayer
			&& !SelectionLayer.Hidden
			&& ContainsPixel (SelectionLayer, point))
			return userLayer;

		return userLayer.GetOwnLayersToPaint ().Reverse ().Any (layer => ContainsPixel (layer, point))
			? userLayer
			: null;
	}

	private static bool ContainsPixel (Layer layer, PointD point)
	{
		if (layer.Opacity <= 0)
			return false;

		Matrix inverse = layer.Transform.Clone ();
		if (inverse.Invert () != Status.Success)
			return false;

		PointD local = inverse.TransformPoint (point);
		PointI pixel = new ((int) Math.Floor (local.X), (int) Math.Floor (local.Y));
		return layer.Surface.GetBounds ().Contains (pixel)
			&& layer.Surface.GetColorBgra (pixel).A > 0;
	}
}
