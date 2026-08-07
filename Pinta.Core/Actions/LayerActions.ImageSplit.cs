using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandlePintaCoreActionsLayersImageSplitActivated (object sender, EventArgs e)
	{
		if (cutout_running || !EnsureAiLoggedIn ()
			|| workspace.ActiveDocumentOrDefault is not Document document
			|| !document.Layers.HasSelectedLayer)
			return;

		UserLayer source = document.Layers.CurrentUserLayer;
		if (!source.IsEditable)
			return;

		AiImageRequestOptions? options = await PromptAiImageRequestAsync (
			AiImageRequestMode.ImageSplitGeneration,
			document,
			source);
		if (options is null)
			return;

		await GenerateImageAsync (document, options with {
			ImageSize = source.Surface.GetSize (),
			SourceLayer = source,
			ParentLayer = source,
		});
	}

	private static void AttachSettingsRow (Gtk.Grid grid, string label, Gtk.Widget value, int row)
	{
		Gtk.Label labelWidget = Gtk.Label.New (label);
		labelWidget.Halign = Gtk.Align.End;
		grid.Attach (labelWidget, 0, row, 1, 1);
		grid.Attach (value, 1, row, 1, 1);
	}

	private static Gtk.Overlay CreatePreviewWidget (
		Gdk.Paintable? paintable,
		bool checkerboard,
		out Gtk.Picture picture,
		out Gtk.DrawingArea background,
		out Gtk.DrawingArea border)
	{
		background = Gtk.DrawingArea.New ();
		background.SetSizeRequest (430, 300);
		background.CanTarget = false;
		background.SetDrawFunc ((_, context, width, height)
			=> DrawPreviewBackground (context, width, height, checkerboard));
		picture = Gtk.Picture.New ();
		picture.ContentFit = Gtk.ContentFit.ScaleDown;
		picture.Hexpand = true;
		picture.Vexpand = true;
		picture.Halign = Gtk.Align.Fill;
		picture.Valign = Gtk.Align.Fill;
		picture.CanTarget = false;
		if (paintable is not null)
			picture.Paintable = paintable;
		border = Gtk.DrawingArea.New ();
		border.Hexpand = true;
		border.Vexpand = true;
		border.CanTarget = false;
		Gtk.Overlay overlay = Gtk.Overlay.New ();
		overlay.SetChild (background);
		overlay.AddOverlay (picture);
		overlay.AddOverlay (border);
		return overlay;
	}

	private static void DrawPreviewBackground (
		Context context,
		int width,
		int height,
		bool checkerboard)
	{
		if (width <= 0 || height <= 0)
			return;
		context.SetSourceColor (new Color (1, 1, 1));
		context.Rectangle (0, 0, width, height);
		context.Fill ();
		if (checkerboard) {
			const int cell = 16;
			for (int y = 0; y < height; y += cell)
				for (int x = 0; x < width; x += cell)
					if ((x / cell + y / cell) % 2 == 0) {
						context.SetSourceColor (new Color (0.88, 0.89, 0.90));
						context.Rectangle (x, y, cell, cell);
						context.Fill ();
					}
		}
	}

	private static void DrawPreviewBorder (Context context, int width, int height, Size? canvasSize)
	{
		if (width <= 0 || height <= 0 || canvasSize is not Size canvas
			|| canvas.Width <= 0 || canvas.Height <= 0)
			return;
		double scale = Math.Min (width / (double) canvas.Width, height / (double) canvas.Height);
		double canvasWidth = canvas.Width * scale;
		double canvasHeight = canvas.Height * scale;
		double canvasX = (width - canvasWidth) / 2;
		double canvasY = (height - canvasHeight) / 2;
		context.SetSourceColor (new Color (0.25, 0.28, 0.32));
		context.LineWidth = 2;
		context.Rectangle (canvasX + 1, canvasY + 1, canvasWidth - 2, canvasHeight - 2);
		context.Stroke ();
	}

	private static string GetPaddingText (Size requestSize, AI.ImageFitInfo fit)
	{
		int right = requestSize.Width - fit.ContentSize.Width - fit.Offset.X;
		int bottom = requestSize.Height - fit.ContentSize.Height - fit.Offset.Y;
		if (fit.Offset == PointI.Zero && right == 0 && bottom == 0)
			return Translations.GetString ("None");
		return Translations.GetString (
			"Left {0}, right {1}, top {2}, bottom {3} px",
			fit.Offset.X,
			right,
			fit.Offset.Y,
			bottom);
	}

	private static string GetImageServiceLabel (string imageService)
		=> imageService switch {
			AI.AiRequestSettings.NanoBananaService => Translations.GetString ("Nano Banana"),
			AI.AiRequestSettings.AgnesService => Translations.GetString ("Agnes"),
			_ => Translations.GetString ("GPT Image"),
		};

	private sealed record ImageSplitPreviewSelection (
		Size RequestSize,
		bool WhitePadding,
		byte[]? PreparedSourcePng);

	private static UserLayer AddAiChildResultLayer (
		Document document,
		UserLayer parent,
		string name,
		Size size)
	{
		UserLayer child = document.Layers.CreateLayer (name, size.Width, size.Height);
		child.Transform = parent.Transform.Clone ();
		document.Layers.Insert (child, new LayerPosition (parent, parent.Children.Count));
		document.Layers.SetCurrentUserLayer (child);
		return child;
	}
}
