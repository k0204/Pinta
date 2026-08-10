using System;
using System.Linq;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private void HandleOpenAtlasClicked (object sender, EventArgs args)
	{
		if (videoFilename is null || previews.Count == 0)
			return;

		string[] paths = allFramesButton.Active
			? previews.Select (preview => preview.SourcePath).ToArray ()
			: previews
				.Where (preview => selectedIndices.Contains (preview.SourceIndex))
				.Select (preview => preview.SourcePath)
				.ToArray ();
		if (paths.Length == 0)
			return;

		if (atlasWindow is null) {
			atlasWindow = AtlasPackingWindow.New (this, paths);
			atlasWindow.Closed += HandleAtlasWindowClosed;
		} else {
			atlasWindow.SetInputPaths (paths);
		}
		atlasWindow.Present ();
	}

	private void HandleAtlasWindowClosed (object? sender, EventArgs args)
	{
		if (sender is AtlasPackingWindow closedWindow) {
			closedWindow.Closed -= HandleAtlasWindowClosed;
			if (ReferenceEquals (atlasWindow, closedWindow))
				atlasWindow = null;
		}
	}
}
