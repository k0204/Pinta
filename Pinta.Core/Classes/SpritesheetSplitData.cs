using System;
using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

internal static class SpritesheetLayerMetadata
{
	internal const string OutputCanvas = "pinta.spritesheet.output-canvas";

	internal static Matrix CreateAnchorTransform (Size documentSize)
	{
		Matrix transform = CairoExtensions.CreateIdentityMatrix ();
		transform.Translate (documentSize.Width / 2.0, documentSize.Height);
		return transform;
	}

	internal static Matrix CreateOutputTransform (Size documentSize, Size outputSize)
	{
		Matrix transform = CairoExtensions.CreateIdentityMatrix ();
		transform.Translate (
			Math.Floor ((documentSize.Width - outputSize.Width) / 2.0),
			documentSize.Height - outputSize.Height);
		return transform;
	}
}

public sealed record SpritesheetFrameSplit (int X, int Y, bool Visible);

public sealed record SpritesheetSplitData (
	int Columns,
	int Rows,
	int CellWidth,
	int CellHeight,
	int OffsetX,
	int OffsetY,
	int GapX,
	int GapY,
	int CanvasWidth,
	int CanvasHeight,
	bool AlignCharacter,
	IReadOnlyList<SpritesheetFrameSplit> Frames,
	IReadOnlyList<RectangleI>? SourceRectangles);
