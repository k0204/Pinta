using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed class SingleDirectionAnimationLayer : AnimationOutputLayer
{
	public const string DefaultDirectionId = "default";

	private readonly List<SingleDirectionAnimationData> animations = [];

	public SingleDirectionAnimationLayer (
		string name,
		int canvasWidth,
		int canvasHeight,
		string directionId = DefaultDirectionId)
		: base (name, canvasWidth, canvasHeight)
	{
		if (string.IsNullOrWhiteSpace (directionId))
			throw new ArgumentException ("A direction ID is required.", nameof (directionId));

		DirectionId = directionId;
	}

	public string DirectionId { get; }
	public IReadOnlyList<SingleDirectionAnimationData> Animations => animations;

	public SingleDirectionAnimationLayerSnapshot CaptureSnapshot ()
		=> new (
			DirectionId,
			CanvasWidth,
			CanvasHeight,
			PositionOffset,
			animations.Select (animation => animation.Clone ()).ToList ());

	public void ReplaceSnapshot (SingleDirectionAnimationLayerSnapshot snapshot, Size documentSize)
	{
		ValidateSnapshotDirection (snapshot);
		animations.Clear ();
		animations.AddRange (snapshot.Animations.Select (animation => animation.Clone ()));
		SetOutputGeometry (snapshot.CanvasWidth, snapshot.CanvasHeight, snapshot.PositionOffset);
		UpdateTransforms (documentSize);
	}

	public void MergeSnapshot (SingleDirectionAnimationLayerSnapshot snapshot, Size documentSize)
	{
		ValidateSnapshotDirection (snapshot);
		SetOutputGeometry (snapshot.CanvasWidth, snapshot.CanvasHeight, snapshot.PositionOffset);
		foreach (SingleDirectionAnimationData animation in snapshot.Animations) {
			SingleDirectionAnimationData target = animations.FirstOrDefault (item => item.ActionId == animation.ActionId)
				?? AddAnimation (animation.ActionId, animation.CanvasWidth, animation.CanvasHeight);
			target.CanvasWidth = animation.CanvasWidth;
			target.CanvasHeight = animation.CanvasHeight;
			foreach (AnimationFrameData frame in animation.Frames) {
				int framePosition = target.Frames.FindIndex (item => item.FrameIndex == frame.FrameIndex);
				if (framePosition < 0)
					target.Frames.Add (frame.Clone ());
				else
					target.Frames[framePosition] = frame.Clone ();
			}
		}

		UpdateTransforms (documentSize);
	}

	public SingleDirectionAnimationData AddAnimation (string actionId, int canvasWidth, int canvasHeight)
	{
		SingleDirectionAnimationData result = new (actionId, canvasWidth, canvasHeight);
		animations.Add (result);
		return result;
	}

	public override IEnumerable<AnimationFrameData> GetFrames ()
		=> animations.SelectMany (animation => animation.Frames);

	protected override IEnumerable<AnimationFrameData> GetDisplayFrames ()
		=> animations.FirstOrDefault ()?.Frames ?? Enumerable.Empty<AnimationFrameData> ();

	private void ValidateSnapshotDirection (SingleDirectionAnimationLayerSnapshot snapshot)
	{
		if (snapshot.DirectionId != DirectionId)
			throw new InvalidOperationException ($"The snapshot direction '{snapshot.DirectionId}' does not match '{DirectionId}'.");
	}
}

public sealed class SingleDirectionAnimationLayerSnapshot
{
	public SingleDirectionAnimationLayerSnapshot (
		string directionId,
		int canvasWidth,
		int canvasHeight,
		PointD positionOffset,
		List<SingleDirectionAnimationData> animations)
	{
		DirectionId = directionId;
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
		PositionOffset = positionOffset;
		Animations = animations;
	}

	public string DirectionId { get; }
	public int CanvasWidth { get; }
	public int CanvasHeight { get; }
	public PointD PositionOffset { get; }
	public List<SingleDirectionAnimationData> Animations { get; }
}

public sealed class SingleDirectionAnimationData
{
	public SingleDirectionAnimationData (string actionId, int canvasWidth, int canvasHeight)
	{
		ActionId = actionId;
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
	}

	public string ActionId { get; }
	public int CanvasWidth { get; set; }
	public int CanvasHeight { get; set; }
	public AnimationFrameSequenceData Sequence { get; } = new ();
	public List<AnimationFrameData> Frames => Sequence.Frames;

	internal SingleDirectionAnimationData Clone ()
	{
		SingleDirectionAnimationData result = new (ActionId, CanvasWidth, CanvasHeight);
		result.Sequence.Frames.AddRange (Sequence.Clone ().Frames);
		return result;
	}
}
