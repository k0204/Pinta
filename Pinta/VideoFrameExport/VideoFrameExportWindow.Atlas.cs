using System;
using System.Linq;

namespace Pinta;

internal sealed partial class VideoFrameExportWindow
{
	private void HandleOpenAtlasClicked (object sender, EventArgs args)
	{
		if (videoFilename is null || previewPaths.Count == 0)
			return;

		string[] paths = allFramesButton.Active
			? previewPaths.ToArray ()
			: selectedIndices
				.Where (index => index >= 0 && index < previewPaths.Count)
				.Order ()
				.Select (index => previewPaths[index])
				.ToArray ();
		if (paths.Length == 0)
			return;

		if (atlasWindow is null) {
			atlasWindow = new AtlasPackingWindow (application, window, paths);
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
