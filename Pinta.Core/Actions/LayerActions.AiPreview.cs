using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async Task<byte[]?> ConfirmGeneratedImageAsync (
		UserLayer? sourceLayer,
		IReadOnlyList<byte[]> candidates,
		string title)
	{
		if (candidates.Count == 0)
			return null;

		using Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Review Generated Image");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = 1100;
		dialog.DefaultHeight = 720;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		Gtk.Widget confirm = dialog.AddButton (Translations.GetString ("Confirm"), (int) Gtk.ResponseType.Ok);
		confirm.AddCssClass (AdwaitaStyles.SuggestedAction);

		Gtk.Picture generatedPicture = Gtk.Picture.New ();
		generatedPicture.ContentFit = Gtk.ContentFit.ScaleDown;
		generatedPicture.Hexpand = true;
		generatedPicture.Vexpand = true;
		generatedPicture.SetSizeRequest (440, 520);
		Gtk.Label pageLabel = Gtk.Label.New (string.Empty);
		pageLabel.Halign = Gtk.Align.Center;
		pageLabel.AddCssClass (AdwaitaStyles.DimLabel);
		Gtk.Button previous = Gtk.Button.NewFromIconName (Resources.StandardIcons.GoPrevious);
		Gtk.Button next = Gtk.Button.NewFromIconName (Resources.StandardIcons.GoNext);
		previous.SetTooltipText (Translations.GetString ("Previous generated image"));
		next.SetTooltipText (Translations.GetString ("Next generated image"));
		previous.Sensitive = candidates.Count > 1;
		next.Sensitive = candidates.Count > 1;

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		content.SetAllMargins (12);
		Gtk.Label titleLabel = Gtk.Label.New (title);
		titleLabel.Halign = Gtk.Align.Start;
		titleLabel.AddCssClass (AdwaitaStyles.Heading);
		content.Append (titleLabel);

		Gtk.Grid comparison = Gtk.Grid.New ();
		comparison.ColumnSpacing = 12;
		comparison.RowSpacing = 6;
		comparison.Hexpand = true;
		comparison.Vexpand = true;
		comparison.Attach (CreatePreviewHeading ("Original"), 0, 0, 1, 1);
		comparison.Attach (CreatePreviewHeading ("Generated Preview"), 1, 0, 1, 1);
		Gtk.Widget originalWidget = CreateOriginalPreview (sourceLayer);
		comparison.Attach (originalWidget, 0, 1, 1, 1);
		comparison.Attach (generatedPicture, 1, 1, 1, 1);
		content.Append (comparison);

		Gtk.Box navigation = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		navigation.Halign = Gtk.Align.Center;
		navigation.Append (previous);
		navigation.Append (pageLabel);
		navigation.Append (next);
		content.Append (navigation);
		dialog.GetContentAreaBox ().Append (content);

		int selected = 0;
		Cairo.ImageSurface? generatedSurface = null;
		void UpdatePreview ()
		{
			generatedSurface?.Dispose ();
			generatedSurface = CreatePreviewSurface (candidates[selected]);
			generatedPicture.Paintable = generatedSurface.ToTexture ();
			pageLabel.SetText (Translations.GetString ("Generated image {0} of {1}", selected + 1, candidates.Count));
			previous.Sensitive = candidates.Count > 1;
			next.Sensitive = candidates.Count > 1;
		}
		previous.OnClicked += (_, _) => {
			selected = (selected + candidates.Count - 1) % candidates.Count;
			UpdatePreview ();
		};
		next.OnClicked += (_, _) => {
			selected = (selected + 1) % candidates.Count;
			UpdatePreview ();
		};
		UpdatePreview ();

		Gtk.ResponseType response = await dialog.RunAsync ();
		generatedSurface?.Dispose ();
		dialog.Close ();
		return response == Gtk.ResponseType.Ok ? candidates[selected] : null;
	}

	private static Gtk.Label CreatePreviewHeading (string text)
	{
		Gtk.Label label = Gtk.Label.New (Translations.GetString (text));
		label.Halign = Gtk.Align.Center;
		label.AddCssClass (AdwaitaStyles.Heading);
		return label;
	}

	private static Gtk.Widget CreateOriginalPreview (UserLayer? sourceLayer)
	{
		if (sourceLayer is null)
			return Gtk.Label.New (Translations.GetString ("No original image"));

		Gtk.Picture picture = Gtk.Picture.New ();
		picture.ContentFit = Gtk.ContentFit.ScaleDown;
		picture.Hexpand = true;
		picture.Vexpand = true;
		picture.SetSizeRequest (440, 520);
		picture.Paintable = sourceLayer.Surface.ToTexture ();
		return picture;
	}

	private static Cairo.ImageSurface CreatePreviewSurface (byte[] png)
	{
		using GLib.Bytes bytes = GLib.Bytes.New (png);
		using Gio.MemoryInputStream stream = Gio.MemoryInputStream.NewFromBytes (bytes);
		using GdkPixbuf.Pixbuf pixbuf = GdkPixbuf.Pixbuf.NewFromStream (stream, cancellable: null)!;
		Cairo.ImageSurface surface = CairoExtensions.CreateImageSurface (
			Cairo.Format.Argb32,
			pixbuf.Width,
			pixbuf.Height);
		using Cairo.Context context = new (surface);
		context.DrawPixbuf (pixbuf, PointD.Zero);
		return surface;
	}
}
