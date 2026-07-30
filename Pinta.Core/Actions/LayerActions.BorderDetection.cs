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

		using Adw.MessageDialog confirmation = Adw.MessageDialog.New (
			chrome.MainWindow,
			Translations.GetString ("Detect Border"),
			Translations.GetString ("Detect the border in the selected area?"));
		const string cancel_response = "cancel";
		const string confirm_response = "detect";
		confirmation.AddResponse (cancel_response, Translations.GetString ("_Cancel"));
		confirmation.AddResponse (confirm_response, Translations.GetString ("_Detect"));
		confirmation.SetResponseAppearance (confirm_response, Adw.ResponseAppearance.Suggested);
		confirmation.Modal = true;
		confirmation.DefaultResponse = confirm_response;
		confirmation.CloseResponse = cancel_response;
		if (await confirmation.RunAsync () != confirm_response)
			return;

		detect_border_running = true;
		EnableOrDisableLayerActions (null, EventArgs.Empty);
		chrome.SetStatusBarText (Translations.GetString ("Detecting border..."));

		try {
			byte[] sourcePng = CreateDocumentPng (doc);
			AI.CharacterBorderRecognitionResult result = await border_recognition.RecognizeAsync (sourcePng, box);

			CompoundHistoryItem hist = new (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Detect Border"));

			UserLayer detectedLayer = doc.Layers.AddNewLayer (Translations.GetString ("Detected Border"));
			DrawPngOnLayer (result.PartPng, detectedLayer);
			hist.Push (new AddLayerHistoryItem (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Detected Border"),
				detectedLayer,
				doc.Layers.GetPosition (detectedLayer)));

			UserLayer controlLayer = doc.Layers.AddNewLayer (Translations.GetString ("Border Control"));
			DrawRecognitionControl (result.MaskPng, controlLayer, box);
			controlLayer.Opacity = 0.65;
			hist.Push (new AddLayerHistoryItem (
				Resources.Icons.EffectsStylizeOutline,
				Translations.GetString ("Border Control"),
				controlLayer,
				doc.Layers.GetPosition (controlLayer)));

			doc.Layers.SetCurrentUserLayer (controlLayer);
			doc.History.PushNewItem (hist);
			doc.Workspace.Invalidate ();
		} catch (Exception ex) {
			await chrome.ShowErrorDialog (
				chrome.MainWindow,
				Translations.GetString ("Detect Border Failed"),
				Translations.GetString ("Start the local character recognition service on port 8001, then try again."),
				ex.ToString ());
		} finally {
			detect_border_running = false;
			EnableOrDisableLayerActions (null, EventArgs.Empty);
			chrome.SetStatusBarText (string.Empty);
		}
	}

	private static byte[] CreateDocumentPng (Document doc)
	{
		using Cairo.ImageSurface source = doc.GetFlattenedImage ();
		using GdkPixbuf.Pixbuf pixbuf = source.ToPixbuf ();
		return pixbuf.SaveToBuffer ("png");
	}

	private static void DrawRecognitionControl (byte[] maskPng, UserLayer layer, RectangleI box)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (maskPng);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		using Cairo.ImageSurface mask = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			layer.Surface.Width,
			layer.Surface.Height);
		using (Cairo.Context context = new (mask))
			context.DrawPixbuf (pixbuf, PointD.Zero);

		ReadOnlySpan<ColorBgra> maskPixels = mask.GetReadOnlyPixelData ();
		Span<ColorBgra> controlPixels = layer.Surface.GetPixelData ();
		int width = layer.Surface.Width;
		for (int y = box.Top; y < box.Bottom; y++) {
			for (int x = box.Left; x < box.Right; x++) {
				int index = y * width + x;
				controlPixels[index] = maskPixels[index].R >= 128
					? ColorBgra.Red
					: ColorBgra.Yellow;
			}
		}
		layer.Surface.MarkDirty (box);
	}

}
