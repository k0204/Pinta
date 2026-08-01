using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public sealed class SpriteSheetLayer : GroupLayer
{
	private readonly List<SpriteSheetAnimationData> animations = [];

	public SpriteSheetLayer (string name, int canvasWidth, int canvasHeight)
		: base (CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1), false, 1, name)
	{
		if (canvasWidth <= 0 || canvasHeight <= 0)
			throw new ArgumentOutOfRangeException (nameof (canvasWidth));

		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
	}

	public int CanvasWidth { get; private set; }
	public int CanvasHeight { get; private set; }
	public PointD PositionOffset { get; private set; }
	public IReadOnlyList<SpriteSheetAnimationData> Animations => animations;
	public override bool CanMoveOnCanvas => true;

	public void SetPositionOffset (PointD offset, Size documentSize)
	{
		if (!double.IsFinite (offset.X) || !double.IsFinite (offset.Y))
			throw new ArgumentOutOfRangeException (nameof (offset));

		PositionOffset = offset;
		UpdateTransforms (documentSize);
	}

	public SpriteSheetLayerSnapshot CaptureSnapshot ()
		=> new (CanvasWidth, CanvasHeight, PositionOffset, animations.Select (animation => animation.Clone ()).ToList ());

	public void ReplaceSnapshot (SpriteSheetLayerSnapshot snapshot, Size documentSize)
	{
		if (snapshot.CanvasWidth <= 0 || snapshot.CanvasHeight <= 0)
			throw new InvalidOperationException ("A spritesheet canvas must be positive.");

		animations.Clear ();
		CanvasWidth = snapshot.CanvasWidth;
		CanvasHeight = snapshot.CanvasHeight;
		PositionOffset = snapshot.PositionOffset;
		animations.AddRange (snapshot.Animations.Select (animation => animation.Clone ()));
		UpdateTransforms (documentSize);
	}

	public void MergeSnapshot (SpriteSheetLayerSnapshot snapshot, Size documentSize)
	{
		CanvasWidth = snapshot.CanvasWidth;
		CanvasHeight = snapshot.CanvasHeight;
		foreach (SpriteSheetAnimationData animation in snapshot.Animations) {
			SpriteSheetAnimationData targetAnimation = animations.FirstOrDefault (item => item.ActionId == animation.ActionId)
				?? AddAnimation (animation.ActionId, animation.CanvasWidth, animation.CanvasHeight);
			targetAnimation.CanvasWidth = animation.CanvasWidth;
			targetAnimation.CanvasHeight = animation.CanvasHeight;
			foreach (SpriteSheetDirectionData direction in animation.Directions) {
				SpriteSheetDirectionData targetDirection = targetAnimation.Directions.FirstOrDefault (item => item.DirectionId == direction.DirectionId)
					?? targetAnimation.AddDirection (direction.DirectionId);
				foreach (SpriteSheetFrameData frame in direction.Frames) {
					int framePosition = targetDirection.Frames.FindIndex (item => item.FrameIndex == frame.FrameIndex);
					if (framePosition < 0)
						targetDirection.Frames.Add (frame.Clone ());
					else
						targetDirection.Frames[framePosition] = frame.Clone ();
				}
			}
		}

		PositionOffset = snapshot.PositionOffset;
		UpdateTransforms (documentSize);
	}

	public SpriteSheetAnimationData AddAnimation (string actionId, int canvasWidth, int canvasHeight)
	{
		SpriteSheetAnimationData result = new (actionId, canvasWidth, canvasHeight);
		animations.Add (result);
		return result;
	}

	public ImageSurface? CreateThumbnailSurface ()
	{
		SpriteSheetFrameData? frame = animations.FirstOrDefault ()?.Directions.FirstOrDefault ()?.Frames
			.OrderBy (frame => frame.FrameIndex)
			.FirstOrDefault ();
		if (frame is null)
			return null;

		ImageSurface result = CairoExtensions.CreateImageSurface (Format.Argb32, CanvasWidth, CanvasHeight);
		using Context context = new (result);
		context.SetSourceSurface (frame.Surface, frame.X, frame.Y);
		context.Paint ();
		return result;
	}

	public IEnumerable<SpriteSheetFrameData> GetFrames ()
		=> animations.SelectMany (animation => animation.Directions.SelectMany (direction => direction.Frames));

	public void UpdateTransforms (Size documentSize)
	{
		Transform = CreateAnchorTransform (documentSize, PositionOffset);
		foreach (SpriteSheetFrameData frame in GetFrames ()) {
			frame.RenderLayer ??= new Layer (frame.Surface);
			frame.RenderLayer.Surface = frame.Surface;
			frame.RenderLayer.Hidden = !frame.Visible;
			frame.RenderLayer.Opacity = Opacity;
			frame.RenderLayer.BlendMode = BlendMode;
			frame.RenderLayer.Transform = CreateFrameTransform (documentSize, CanvasWidth, CanvasHeight, frame, PositionOffset);
		}
	}

	internal override IEnumerable<Layer> GetOwnLayersToPaint ()
	{
		foreach (SpriteSheetFrameData frame in GetDisplayFrames ()) {
			if (!frame.Visible)
				continue;
			frame.RenderLayer ??= new Layer (frame.Surface);
			frame.RenderLayer.Surface = frame.Surface;
			frame.RenderLayer.Hidden = !frame.Visible;
			frame.RenderLayer.Opacity = Opacity;
			frame.RenderLayer.BlendMode = BlendMode;
			yield return frame.RenderLayer;
		}
	}

	private IEnumerable<SpriteSheetFrameData> GetDisplayFrames ()
		=> animations.FirstOrDefault ()?.Directions.FirstOrDefault ()?.Frames
			?? Enumerable.Empty<SpriteSheetFrameData> ();

	public override void Resize (Size newSize, ResamplingMode resamplingMode) { }
	public override void ResizeCanvas (Size newSize, Anchor anchor) { }
	public override void Crop (RectangleI rect, Path? selection) { }

	private static Matrix CreateAnchorTransform (Size documentSize, PointD offset)
	{
		Matrix result = CairoExtensions.CreateIdentityMatrix ();
		result.Translate (documentSize.Width / 2.0 + offset.X, documentSize.Height + offset.Y);
		return result;
	}

	private static Matrix CreateFrameTransform (Size documentSize, int canvasWidth, int canvasHeight, SpriteSheetFrameData frame, PointD offset)
	{
		Matrix result = CairoExtensions.CreateIdentityMatrix ();
		result.Translate (
			Math.Floor ((documentSize.Width - canvasWidth) / 2.0) + offset.X + frame.X,
			documentSize.Height - canvasHeight + offset.Y + frame.Y);
		return result;
	}

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
	public List<SpriteSheetFrameData> Frames { get; } = [];

	internal SpriteSheetDirectionData Clone ()
	{
		SpriteSheetDirectionData result = new (DirectionId);
		result.Frames.AddRange (Frames.Select (frame => frame.Clone ()));
		return result;
	}
}

public sealed class SpriteSheetFrameData
{
	public SpriteSheetFrameData (int frameIndex, int x, int y, bool visible, ImageSurface surface)
	{
		FrameIndex = frameIndex;
		X = x;
		Y = y;
		Visible = visible;
		Surface = surface;
	}

	public int FrameIndex { get; }
	public int X { get; set; }
	public int Y { get; set; }
	public bool Visible { get; set; }
	public ImageSurface Surface { get; }
	internal Layer? RenderLayer { get; set; }

	internal SpriteSheetFrameData Clone ()
		=> new (FrameIndex, X, Y, Visible, Surface.Clone ());
}
