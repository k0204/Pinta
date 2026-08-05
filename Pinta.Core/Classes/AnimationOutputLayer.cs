using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public abstract class AnimationOutputLayer : GroupLayer
{
	protected AnimationOutputLayer (string name, int canvasWidth, int canvasHeight)
		: base (CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1), false, 1, name)
	{
		ValidateCanvasSize (canvasWidth, canvasHeight);
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
	}

	public int CanvasWidth { get; private set; }
	public int CanvasHeight { get; private set; }
	public PointD PositionOffset { get; private set; }
	public override bool CanMoveOnCanvas => true;

	public void SetPositionOffset (PointD offset, Size documentSize)
	{
		ValidateOffset (offset);
		PositionOffset = offset;
		UpdateTransforms (documentSize);
	}

	public abstract IEnumerable<AnimationFrameData> GetFrames ();

	protected abstract IEnumerable<AnimationFrameData> GetDisplayFrames ();

	protected void SetOutputGeometry (int canvasWidth, int canvasHeight, PointD positionOffset)
	{
		ValidateCanvasSize (canvasWidth, canvasHeight);
		ValidateOffset (positionOffset);
		CanvasWidth = canvasWidth;
		CanvasHeight = canvasHeight;
		PositionOffset = positionOffset;
	}

	public ImageSurface? CreateThumbnailSurface ()
	{
		AnimationFrameData? frame = GetDisplayFrames ()
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

	public void UpdateTransforms (Size documentSize)
	{
		Transform = CreateAnchorTransform (documentSize, PositionOffset);
		foreach (AnimationFrameData frame in GetFrames ()) {
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
		foreach (AnimationFrameData frame in GetDisplayFrames ()) {
			if (!frame.Visible)
				continue;
			frame.RenderLayer ??= new Layer (frame.Surface);
			frame.RenderLayer.Surface = frame.Surface;
			frame.RenderLayer.Hidden = false;
			frame.RenderLayer.Opacity = Opacity;
			frame.RenderLayer.BlendMode = BlendMode;
			yield return frame.RenderLayer;
		}
	}

	public override void Resize (Size newSize, ResamplingMode resamplingMode)
	{
		foreach (AnimationFrameData frame in GetFrames ())
			frame.Resize (newSize, resamplingMode);
	}
	public override void ResizeCanvas (Size newSize, Anchor anchor) { }
	public override void Crop (RectangleI rect, Path? selection) { }

	private static Matrix CreateAnchorTransform (Size documentSize, PointD offset)
	{
		Matrix result = CairoExtensions.CreateIdentityMatrix ();
		result.Translate (documentSize.Width / 2.0 + offset.X, documentSize.Height + offset.Y);
		return result;
	}

	private static Matrix CreateFrameTransform (Size documentSize, int canvasWidth, int canvasHeight, AnimationFrameData frame, PointD offset)
	{
		Matrix result = CairoExtensions.CreateIdentityMatrix ();
		result.Translate (
			Math.Floor ((documentSize.Width - canvasWidth) / 2.0) + offset.X + frame.X,
			documentSize.Height - canvasHeight + offset.Y + frame.Y);
		return result;
	}

	private static void ValidateCanvasSize (int canvasWidth, int canvasHeight)
	{
		if (canvasWidth <= 0 || canvasHeight <= 0)
			throw new ArgumentOutOfRangeException (nameof (canvasWidth));
	}

	private static void ValidateOffset (PointD offset)
	{
		if (!double.IsFinite (offset.X) || !double.IsFinite (offset.Y))
			throw new ArgumentOutOfRangeException (nameof (offset));
	}
}

public sealed class AnimationFrameData
{
	public AnimationFrameData (int frameIndex, int x, int y, bool visible, ImageSurface surface)
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
	public ImageSurface Surface { get; private set; }
	internal Layer? RenderLayer { get; set; }

	public void Resize (Size newSize, ResamplingMode resamplingMode)
	{
		ImageSurface dest = CairoExtensions.CreateImageSurface (Format.Argb32, newSize.Width, newSize.Height);
		using (Context context = new (dest)) {
			context.Scale (newSize.Width / (double) Surface.Width, newSize.Height / (double) Surface.Height);
			context.SetSourceSurface (Surface, resamplingMode);
			context.Paint ();
		}
		Surface = dest;
	}

	internal AnimationFrameData Clone ()
		=> new (FrameIndex, X, Y, Visible, Surface.Clone ());
}
