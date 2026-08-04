using System;
using System.Collections.Generic;
using System.Linq;

namespace Pinta.Core;

public sealed class SpriteSheetLayer : AnimationOutputLayer
{
	private readonly List<SpriteSheetAnimationData> animations = [];

	public SpriteSheetLayer (string name, int canvasWidth, int canvasHeight)
		: base (name, canvasWidth, canvasHeight)
	{
	}

	public IReadOnlyList<SpriteSheetAnimationData> Animations => animations;

	public SpriteSheetLayerSnapshot CaptureSnapshot ()
		=> new (CanvasWidth, CanvasHeight, PositionOffset, animations.Select (animation => animation.Clone ()).ToList ());

	public void ReplaceSnapshot (SpriteSheetLayerSnapshot snapshot, Size documentSize)
	{
		if (snapshot.CanvasWidth <= 0 || snapshot.CanvasHeight <= 0)
			throw new InvalidOperationException ("A spritesheet canvas must be positive.");

		animations.Clear ();
		animations.AddRange (snapshot.Animations.Select (animation => animation.Clone ()));
		SetOutputGeometry (snapshot.CanvasWidth, snapshot.CanvasHeight, snapshot.PositionOffset);
		UpdateTransforms (documentSize);
	}

	public void MergeSnapshot (SpriteSheetLayerSnapshot snapshot, Size documentSize)
	{
		SetOutputGeometry (snapshot.CanvasWidth, snapshot.CanvasHeight, snapshot.PositionOffset);
		foreach (SpriteSheetAnimationData animation in snapshot.Animations) {
			SpriteSheetAnimationData targetAnimation = animations.FirstOrDefault (item => item.ActionId == animation.ActionId)
				?? AddAnimation (animation.ActionId, animation.CanvasWidth, animation.CanvasHeight);
			targetAnimation.CanvasWidth = animation.CanvasWidth;
			targetAnimation.CanvasHeight = animation.CanvasHeight;
			foreach (SpriteSheetDirectionData direction in animation.Directions) {
				SpriteSheetDirectionData targetDirection = targetAnimation.Directions.FirstOrDefault (item => item.DirectionId == direction.DirectionId)
					?? targetAnimation.AddDirection (direction.DirectionId);
				foreach (AnimationFrameData frame in direction.Frames) {
					int framePosition = targetDirection.Frames.FindIndex (item => item.FrameIndex == frame.FrameIndex);
					if (framePosition < 0)
						targetDirection.Frames.Add (frame.Clone ());
					else
						targetDirection.Frames[framePosition] = frame.Clone ();
				}
			}
		}

		UpdateTransforms (documentSize);
	}

	public SpriteSheetAnimationData AddAnimation (string actionId, int canvasWidth, int canvasHeight)
	{
		SpriteSheetAnimationData result = new (actionId, canvasWidth, canvasHeight);
		animations.Add (result);
		return result;
	}

	public override IEnumerable<AnimationFrameData> GetFrames ()
		=> animations.SelectMany (animation => animation.Directions.SelectMany (direction => direction.Frames));

	protected override IEnumerable<AnimationFrameData> GetDisplayFrames ()
		=> animations.FirstOrDefault ()?.Directions.FirstOrDefault ()?.Frames
			?? Enumerable.Empty<AnimationFrameData> ();
}

public sealed class SpriteSheetLayerSnapshot
{
	public SpriteSheetLayerSnapshot (int canvasWidth, int canvasHeight, PointD positionOffset, List<SpriteSheetAnimationData> animations)
	{
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
		PositionOffset = positionOffset;
		Animations = animations;
	}

	public int CanvasWidth { get; }
	public int CanvasHeight { get; }
	public PointD PositionOffset { get; }
	public List<SpriteSheetAnimationData> Animations { get; }
}

public sealed class SpriteSheetAnimationData
{
	public SpriteSheetAnimationData (string actionId, int canvasWidth, int canvasHeight)
	{
		ActionId = actionId;
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
	}

	public string ActionId { get; }
	public int CanvasWidth { get; set; }
	public int CanvasHeight { get; set; }
	public List<SpriteSheetDirectionData> Directions { get; } = [];

	public SpriteSheetDirectionData AddDirection (string directionId)
	{
		SpriteSheetDirectionData result = new (directionId);
		Directions.Add (result);
		return result;
	}

	internal SpriteSheetAnimationData Clone ()
	{
		SpriteSheetAnimationData result = new (ActionId, CanvasWidth, CanvasHeight);
		result.Directions.AddRange (Directions.Select (direction => direction.Clone ()));
		return result;
	}
}

public sealed class SpriteSheetDirectionData
{
	public SpriteSheetDirectionData (string directionId) => DirectionId = directionId;

	public string DirectionId { get; }
	public AnimationFrameSequenceData Sequence { get; } = new ();
	public List<AnimationFrameData> Frames => Sequence.Frames;

	internal SpriteSheetDirectionData Clone ()
	{
		SpriteSheetDirectionData result = new (DirectionId);
		result.Sequence.Frames.AddRange (Sequence.Clone ().Frames);
		return result;
	}
}
