//
// LayerActions.BorderDetection.cs
//

using System;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersDetectBorderActivated (object sender, EventArgs e)
	{
		if (detect_border_running)
			return;

		Document doc = workspace.ActiveDocument;
		tools.Commit ();

		if (!doc.Selection.Visible) {
			await chrome.ShowMessageDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border"),
				Translations.GetString ("Select an area before detecting the border."));
			return;
		}

		RectangleI box = doc.GetSelectedBounds (canvasOnly: true);
		if (box.IsEmpty) {
			await chrome.ShowMessageDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border"),
				Translations.GetString ("The selected area is empty."));
			return;
		}

		detect_border_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		chrome.SetStatusBarText (Translations.GetString ("Detecting border..."));

		try {
			using Cairo.ImageSurface source = doc.GetFlattenedImage ();
			using Cairo.ImageSurface overlay = CairoExtensions.CreateImageSurface (
				Cairo.Format.Argb32,
				doc.ImageSize.Width,
				doc.ImageSize.Height);
			BorderDetectionAnalysis.Render (source, overlay, box);

			UserLayer layer = doc.Layers.AddNewLayer (Translations.GetString ("Detected Border"));
			using (Cairo.Context context = new (layer.Surface)) {
				context.SetSourceSurface (overlay, 0, 0);
				context.Paint ();
			}

			doc.Layers.SetCurrentUserLayer (layer);
			doc.History.PushNewItem (new AddLayerHistoryItem (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Detect Border"),
				layer,
				doc.Layers.GetPosition (layer)));
			doc.Workspace.Invalidate ();
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border Failed"),
				Translations.GetString ("Error"),
				ex.ToString ());
		} finally {
			detect_border_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
			chrome.SetStatusBarText (string.Empty);
		}
	}
}
