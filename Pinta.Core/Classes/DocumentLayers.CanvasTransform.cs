using System;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public sealed partial class DocumentLayers
{
	public bool TryGetResizableLayerTreeBounds (UserLayer root, out RectangleD bounds)
	{
		if (!ContainsLayer (root) || root.GetSelfAndDescendants ().Any (layer => layer is AnimationOutputLayer)) {
			bounds = RectangleD.Zero;
			return false;
		}

		double left = double.PositiveInfinity;
		double top = double.PositiveInfinity;
		double right = double.NegativeInfinity;
		double bottom = double.NegativeInfinity;
		bool hasContent = false;
		foreach (Layer layer in root.GetLayersToPaintTree ()) {
			if (!Utility.TryGetAlphaBounds (layer.Surface, out RectangleI contentBounds))
				continue;

			PointD[] corners = [
				layer.Transform.TransformPoint (new PointD (contentBounds.X, contentBounds.Y)),
				layer.Transform.TransformPoint (new PointD (contentBounds.X + contentBounds.Width, contentBounds.Y)),
				layer.Transform.TransformPoint (new PointD (contentBounds.X, contentBounds.Y + contentBounds.Height)),
				layer.Transform.TransformPoint (new PointD (contentBounds.X + contentBounds.Width, contentBounds.Y + contentBounds.Height))];
			foreach (PointD corner in corners) {
				if (!double.IsFinite (corner.X) || !double.IsFinite (corner.Y)) {
					bounds = RectangleD.Zero;
					return false;
				}
				left = Math.Min (left, corner.X);
				top = Math.Min (top, corner.Y);
				right = Math.Max (right, corner.X);
				bottom = Math.Max (bottom, corner.Y);
			}
			hasContent = true;
		}

		bounds = hasContent
			? new RectangleD (left, top, right - left, bottom - top)
			: RectangleD.Zero;
		return hasContent;
	}

	public bool TryGetSelectedLayerTreeBounds (out RectangleD bounds)
	{
		bounds = RectangleD.Zero;
		bool hasContent = false;
		foreach (UserLayer root in GetSelectedLayerRoots ()) {
			if (root.GetSelfAndDescendants ().Any (layer => layer is AnimationOutputLayer))
				return false;

			if (!TryGetResizableLayerTreeBounds (root, out RectangleD rootBounds))
				continue;

			bounds = hasContent ? bounds.Union (rootBounds) : rootBounds;
			hasContent = true;
		}

		return hasContent;
	}

	public void ResizeLayerTree (UserLayer root, RectangleD source, RectangleD target)
	{
		if (!ContainsLayer (root))
			throw new ArgumentException ("Layer does not belong to this document.", nameof (root));
		if (root.GetSelfAndDescendants ().Any (layer => layer is AnimationOutputLayer))
			throw new InvalidOperationException ("Animation layers cannot be resized with transform controls.");
		if (!IsValidBounds (source))
			throw new ArgumentOutOfRangeException (nameof (source));
		if (!IsValidBounds (target))
			throw new ArgumentOutOfRangeException (nameof (target));

		Matrix transform = CairoExtensions.CreateIdentityMatrix ();
		transform.Translate (target.X, target.Y);
		transform.Scale (target.Width / source.Width, target.Height / source.Height);
		transform.Translate (-source.X, -source.Y);
		foreach (UserLayer node in root.GetSelfAndDescendants ()) {
			foreach (Layer layer in node.GetOwnLayersToPaint ()) {
				Matrix result = transform.Clone ();
				result.Multiply (layer.Transform);
				layer.Transform = result;
			}
		}

		document.Workspace.Invalidate ();
	}

	private static bool IsValidBounds (RectangleD bounds)
		=> double.IsFinite (bounds.X)
			&& double.IsFinite (bounds.Y)
			&& double.IsFinite (bounds.Width)
			&& double.IsFinite (bounds.Height)
			&& bounds.Width > 0
			&& bounds.Height > 0;

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
