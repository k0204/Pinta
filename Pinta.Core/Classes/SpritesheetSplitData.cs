using System.Collections.Generic;

namespace Pinta.Core;

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
