using System;
using System.Collections.Generic;
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

	public IReadOnlyList<UserLayer> FindLayersInSelection (RectangleD selection, bool requireFullyContained)
	{
		if (selection.Width <= 0 || selection.Height <= 0)
			return [];

		List<UserLayer> result = [];
		foreach (UserLayer layer in user_layers)
			CollectLayersInSelection (layer, selection, requireFullyContained, result);

		return result;
	}

	private static void CollectLayersInSelection (
		UserLayer userLayer,
		RectangleD selection,
		bool requireFullyContained,
		List<UserLayer> result)
	{
		if (userLayer.Hidden)
			return;

		if (!userLayer.Locked && userLayer.GetOwnLayersToPaint ().Any (layer =>
			requireFullyContained ? IsFullyContained (layer, selection) : Intersects (layer, selection)))
			result.Add (userLayer);

		foreach (UserLayer child in userLayer.Children)
			CollectLayersInSelection (child, selection, requireFullyContained, result);
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

		if (!userLayer.Locked
			&& userLayer == current_user_layer
			&& ShowSelectionLayer
			&& !SelectionLayer.Hidden
			&& ContainsPixel (SelectionLayer, point))
			return userLayer;

		return !userLayer.Locked
			&& userLayer.GetOwnLayersToPaint ().Reverse ().Any (layer => ContainsPixel (layer, point))
			? userLayer
			: null;
	}

	private static bool Intersects (Layer layer, RectangleD selection)
	{
		if (!TryGetLayerBounds (layer, out RectangleD bounds))
			return false;

		return bounds.X < selection.X + selection.Width
			&& bounds.X + bounds.Width > selection.X
			&& bounds.Y < selection.Y + selection.Height
			&& bounds.Y + bounds.Height > selection.Y;
	}

	private static bool IsFullyContained (Layer layer, RectangleD selection)
	{
		if (!TryGetLayerBounds (layer, out RectangleD bounds))
			return false;

		return bounds.X >= selection.X
			&& bounds.Y >= selection.Y
			&& bounds.X + bounds.Width <= selection.X + selection.Width
			&& bounds.Y + bounds.Height <= selection.Y + selection.Height;
	}

	private static bool TryGetLayerBounds (Layer layer, out RectangleD bounds)
	{
		bounds = RectangleD.Zero;
		if (layer.Hidden || layer.Opacity <= 0 || !Utility.TryGetAlphaBounds (layer.Surface, out RectangleI contentBounds))
			return false;

		PointD[] corners = [
			layer.Transform.TransformPoint (new PointD (contentBounds.X, contentBounds.Y)),
			layer.Transform.TransformPoint (new PointD (contentBounds.X + contentBounds.Width, contentBounds.Y)),
			layer.Transform.TransformPoint (new PointD (contentBounds.X, contentBounds.Y + contentBounds.Height)),
			layer.Transform.TransformPoint (new PointD (contentBounds.X + contentBounds.Width, contentBounds.Y + contentBounds.Height))];
		double left = corners.Min (point => point.X);
		double top = corners.Min (point => point.Y);
		double right = corners.Max (point => point.X);
		double bottom = corners.Max (point => point.Y);
		bounds = new RectangleD (left, top, right - left, bottom - top);
		return true;
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
