using System;
using System.Collections.Generic;
using System.ComponentModel;
using Pinta.Core;

namespace Pinta.Tools;

public sealed partial class MoveLayerTool
{
	private const double smart_guide_tolerance_view = 6;
	private const double smart_guide_release_tolerance_view = 12;

	private readonly record struct GuideCandidate (double Position, bool IsCanvas, int Order);
	private readonly record struct GuideSnap (bool IsActive, int AnchorIndex, double Position);

	private SmartGuideHandle? smart_guides;
	private IReadOnlyList<GuideCandidate> smart_guide_vertical_candidates = [];
	private IReadOnlyList<GuideCandidate> smart_guide_horizontal_candidates = [];
	private GuideSnap smart_guide_vertical_snap;
	private GuideSnap smart_guide_horizontal_snap;
	private bool smart_guide_candidates_dirty = true;
	private Document? smart_guide_candidates_document;

	private SmartGuideHandle SmartGuides
		=> smart_guides ??= new (workspace);

	public override IEnumerable<IToolHandle> Handles => [transform_handle, SmartGuides];

	private void BeginSmartGuideDrag (Document document)
	{
		smart_guide_vertical_snap = default;
		smart_guide_horizontal_snap = default;
		if (has_drag_start_bounds)
			EnsureSmartGuideCandidates (document);
	}

	private void EndSmartGuideDrag (Document? document)
	{
		ClearSmartGuides (document);
		smart_guide_vertical_snap = default;
		smart_guide_horizontal_snap = default;
	}

	private void HandleSmartGuideLayerPropertyChanged (object? sender, PropertyChangedEventArgs e)
		=> InvalidateSmartGuideCandidates ();

	private void InvalidateSmartGuideCandidates ()
	{
		smart_guide_candidates_dirty = true;
		smart_guide_vertical_snap = default;
		smart_guide_horizontal_snap = default;
	}

	private PointD ApplySmartGuideSnap (Document document, PointD requestedDelta)
	{
		EnsureSmartGuideCandidates (document);
		RectangleD proposedBounds = OffsetBounds (drag_start_bounds, requestedDelta);
		List<SmartGuideLine> lines = [];

		bool hasVerticalSnap = TryFindSnapDelta (
			document,
			proposedBounds,
			requestedDelta,
			smart_guide_vertical_candidates,
			isVertical: true,
			ref smart_guide_vertical_snap,
			out double xDelta,
			out double xPosition);
		if (hasVerticalSnap)
			lines.Add (new SmartGuideLine (true, xPosition));

		bool hasHorizontalSnap = TryFindSnapDelta (
			document,
			proposedBounds,
			requestedDelta,
			smart_guide_horizontal_candidates,
			isVertical: false,
			ref smart_guide_horizontal_snap,
			out double yDelta,
			out double yPosition);
		if (hasHorizontalSnap)
			lines.Add (new SmartGuideLine (false, yPosition));

		PointD snappedDelta = new (requestedDelta.X + xDelta, requestedDelta.Y + yDelta);
		if (SmartGuides.SetLines (lines))
			document.Workspace.Invalidate ();

		return snappedDelta;
	}

	private void EnsureSmartGuideCandidates (Document document)
	{
		if (!smart_guide_candidates_dirty && smart_guide_candidates_document == document)
			return;

		List<GuideCandidate> vertical = CreateCanvasCandidates (document.ImageSize.Width);
		List<GuideCandidate> horizontal = CreateCanvasCandidates (document.ImageSize.Height);

		int order = 0;
		// ponytail: scan the layer tree once per drag; rebuild only after a layer change.
		foreach (UserLayer layer in EnumerateVisibleLayers (document.Layers.RootLayers)) {
			if (IsInSelectedTree (layer)
				|| !document.Layers.TryGetResizableLayerTreeBounds (layer, out RectangleD bounds))
				continue;

			vertical.Add (new GuideCandidate (bounds.X, IsCanvas: false, order++));
			vertical.Add (new GuideCandidate (bounds.X + bounds.Width / 2, IsCanvas: false, order++));
			vertical.Add (new GuideCandidate (bounds.X + bounds.Width, IsCanvas: false, order++));
			horizontal.Add (new GuideCandidate (bounds.Y, IsCanvas: false, order++));
			horizontal.Add (new GuideCandidate (bounds.Y + bounds.Height / 2, IsCanvas: false, order++));
			horizontal.Add (new GuideCandidate (bounds.Y + bounds.Height, IsCanvas: false, order++));
		}

		smart_guide_vertical_candidates = PrepareCandidates (vertical);
		smart_guide_horizontal_candidates = PrepareCandidates (horizontal);
		smart_guide_candidates_document = document;
		smart_guide_candidates_dirty = false;
		smart_guide_vertical_snap = default;
		smart_guide_horizontal_snap = default;
	}

	private static List<GuideCandidate> CreateCanvasCandidates (double canvasExtent)
		=> [
			new GuideCandidate (0, IsCanvas: true, Order: 0),
			new GuideCandidate (canvasExtent / 2, IsCanvas: true, Order: 1),
			new GuideCandidate (canvasExtent, IsCanvas: true, Order: 2)];

	private static GuideCandidate[] PrepareCandidates (List<GuideCandidate> candidates)
	{
		Dictionary<double, GuideCandidate> unique = [];
		foreach (GuideCandidate candidate in candidates) {
			if (!unique.TryGetValue (candidate.Position, out GuideCandidate existing)
				|| IsHigherPriority (candidate, existing))
				unique[candidate.Position] = candidate;
		}

		GuideCandidate[] result = [.. unique.Values];
		Array.Sort (result, static (left, right) => left.Position.CompareTo (right.Position));
		return result;
	}

	private static bool IsHigherPriority (GuideCandidate candidate, GuideCandidate existing)
		=> candidate.IsCanvas != existing.IsCanvas
			? candidate.IsCanvas
			: candidate.Order < existing.Order;

	private bool TryFindSnapDelta (
		Document document,
		RectangleD proposedBounds,
		PointD requestedDelta,
		IReadOnlyList<GuideCandidate> candidates,
		bool isVertical,
		ref GuideSnap snap,
		out double delta,
		out double guidePosition)
	{
		delta = 0;
		guidePosition = 0;
		double tolerance = smart_guide_tolerance_view / document.Workspace.Scale;
		double releaseTolerance = smart_guide_release_tolerance_view / document.Workspace.Scale;
		if (snap.IsActive) {
			double movingAnchor = GetMovingAnchor (proposedBounds, isVertical, snap.AnchorIndex);
			double lockedDelta = snap.Position - movingAnchor;
			if (Math.Abs (lockedDelta) <= releaseTolerance
				&& CanApplySnapDelta (document, requestedDelta, lockedDelta, isVertical)) {
				delta = lockedDelta;
				guidePosition = snap.Position;
				return true;
			}

			snap = default;
		}

		double bestDistance = double.PositiveInfinity;
		int bestPriority = int.MaxValue;
		int bestOrder = int.MaxValue;
		int bestAnchorIndex = -1;
		double selectedDelta = 0;
		double selectedGuidePosition = 0;

		for (int anchorIndex = 0; anchorIndex < 3; anchorIndex++) {
			double movingAnchor = GetMovingAnchor (proposedBounds, isVertical, anchorIndex);
			int insertionIndex = FindCandidateInsertionIndex (candidates, movingAnchor);
			ConsiderCandidate (insertionIndex - 1);
			ConsiderCandidate (insertionIndex);

			void ConsiderCandidate (int candidateIndex)
			{
				if (candidateIndex < 0 || candidateIndex >= candidates.Count)
					return;

				GuideCandidate candidate = candidates[candidateIndex];
				double candidateDelta = candidate.Position - movingAnchor;
				double distance = Math.Abs (candidateDelta);
				if (distance > tolerance)
					return;

				if (!CanApplySnapDelta (document, requestedDelta, candidateDelta, isVertical))
					return;

				int priority = candidate.IsCanvas ? 0 : 1;
				if (distance > bestDistance
					|| distance == bestDistance && (priority > bestPriority
						|| priority == bestPriority && (candidate.Order > bestOrder
							|| candidate.Order == bestOrder && anchorIndex > 0)))
					return;

				bestDistance = distance;
				bestPriority = priority;
				bestOrder = candidate.Order;
				selectedDelta = candidateDelta;
				selectedGuidePosition = candidate.Position;
				bestAnchorIndex = anchorIndex;
			}
		}

		if (!double.IsFinite (bestDistance))
			return false;

		delta = selectedDelta;
		guidePosition = selectedGuidePosition;
		snap = new GuideSnap (true, bestAnchorIndex, selectedGuidePosition);
		return true;
	}

	private static int FindCandidateInsertionIndex (IReadOnlyList<GuideCandidate> candidates, double position)
	{
		int low = 0;
		int high = candidates.Count;
		while (low < high) {
			int middle = low + (high - low) / 2;
			if (candidates[middle].Position < position)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	private static double GetMovingAnchor (RectangleD bounds, bool isVertical, int anchorIndex)
	{
		double start = isVertical ? bounds.X : bounds.Y;
		double size = isVertical ? bounds.Width : bounds.Height;
		return anchorIndex switch {
			0 => start,
			1 => start + size / 2,
			2 => start + size,
			_ => throw new ArgumentOutOfRangeException (nameof (anchorIndex)),
		};
	}

	private bool CanApplySnapDelta (Document document, PointD requestedDelta, double snapDelta, bool isVertical)
	{
		PointD targetDelta = isVertical
			? new PointD (requestedDelta.X + snapDelta, requestedDelta.Y)
			: new PointD (requestedDelta.X, requestedDelta.Y + snapDelta);
		PointD clampedDelta = ClampMoveDelta (document, drag_start_bounds, targetDelta);
		return NearlyEqual (isVertical ? clampedDelta.X : clampedDelta.Y, isVertical
			? targetDelta.X
			: targetDelta.Y);
	}

	private void ClearSmartGuides (Document? document)
	{
		if (smart_guides?.Clear () == true)
			document?.Workspace.Invalidate ();
	}

	private bool IsInSelectedTree (UserLayer layer)
	{
		foreach (UserLayer draggedLayer in dragged_layers) {
			if (layer == draggedLayer || IsDescendantOf (layer, draggedLayer) || IsDescendantOf (draggedLayer, layer))
				return true;
		}

		return false;
	}

	private static bool IsDescendantOf (UserLayer layer, UserLayer possibleAncestor)
	{
		for (UserLayer? parent = layer.Parent; parent is not null; parent = parent.Parent) {
			if (parent == possibleAncestor)
				return true;
		}

		return false;
	}

	private static IEnumerable<UserLayer> EnumerateVisibleLayers (IReadOnlyList<UserLayer> layers)
	{
		foreach (UserLayer layer in layers) {
			if (layer.Hidden)
				continue;

			yield return layer;
			foreach (UserLayer child in EnumerateVisibleLayers (layer.Children))
				yield return child;
		}
	}

	private static RectangleD OffsetBounds (RectangleD bounds, PointD delta)
		=> bounds with { X = bounds.X + delta.X, Y = bounds.Y + delta.Y };

	private static bool NearlyEqual (double left, double right)
		=> Math.Abs (left - right) <= 0.0001;
}
