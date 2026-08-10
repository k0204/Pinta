using System.Collections.Generic;
using Pinta.Core;

namespace Pinta.Tools;

internal readonly record struct SmartGuideLine (bool IsVertical, double Position);

internal sealed class SmartGuideHandle : IToolHandle
{
	private static readonly Gdk.RGBA outline_color = new () {
		Red = 1,
		Green = 1,
		Blue = 1,
		Alpha = 0.85f,
	};

	private static readonly Gdk.RGBA guide_color = new () {
		Red = 1,
		Green = 0.1f,
		Blue = 0.7f,
		Alpha = 0.95f,
	};

	private readonly IWorkspaceService workspace;
	private IReadOnlyList<SmartGuideLine> lines = [];

	public SmartGuideHandle (IWorkspaceService workspace)
	{
		this.workspace = workspace;
	}

	public bool Active => lines.Count > 0;

	public bool ContainsPoint (PointD windowPoint) => false;

	public bool SetLines (IReadOnlyList<SmartGuideLine> updated)
	{
		if (lines.Count == updated.Count) {
			bool same = true;
			for (int i = 0; i < lines.Count; i++) {
				if (lines[i] == updated[i])
					continue;

				same = false;
				break;
			}

			if (same)
				return false;
		}

		lines = [.. updated];
		return true;
	}

	public bool Clear ()
	{
		if (!Active)
			return false;

		lines = [];
		return true;
	}

	public void Draw (Gtk.Snapshot snapshot)
	{
		if (!Active)
			return;

		Gsk.PathBuilder pathBuilder = Gsk.PathBuilder.New ();
		foreach (SmartGuideLine line in lines) {
			PointD start = line.IsVertical
				? workspace.CanvasPointToView (new PointD (line.Position, 0))
				: workspace.CanvasPointToView (new PointD (0, line.Position));
			PointD end = line.IsVertical
				? workspace.CanvasPointToView (new PointD (line.Position, workspace.ImageSize.Height))
				: workspace.CanvasPointToView (new PointD (workspace.ImageSize.Width, line.Position));

			pathBuilder.MoveTo ((float) start.X, (float) start.Y);
			pathBuilder.LineTo ((float) end.X, (float) end.Y);
		}

		Gsk.Path path = pathBuilder.ToPath ();
		snapshot.AppendStroke (path, Gsk.Stroke.New (lineWidth: 3.0f), outline_color);
		snapshot.AppendStroke (path, Gsk.Stroke.New (lineWidth: 1.0f), guide_color);
	}
}
