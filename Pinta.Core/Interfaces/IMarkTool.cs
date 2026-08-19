using System;

namespace Pinta.Core;

/// <summary>
/// Provides the shape selection used by the canvas-side marking controls.
/// </summary>
public interface IMarkTool
{
	int CurrentShape { get; }

	event EventHandler? ShapeChanged;

	void SetShape (int shape);
}
