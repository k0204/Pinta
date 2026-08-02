//
// LayerActions.Export.cs
//

using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private async void HandleSaveLayerImageActivated (object sender, EventArgs e)
	{
		if (!workspace.HasOpenDocuments)
			return;

		Document document = workspace.ActiveDocument;
		UserLayer layer = document.Layers.CurrentUserLayer;
		tools.Commit ();

		using Gtk.FileChooserNative chooser = Gtk.FileChooserNative.New (
			Translations.GetString ("Save Layer Image"),
			chrome.MainWindow,
			Gtk.FileChooserAction.Save,
			Translations.GetString ("Save"),
			Translations.GetString ("Cancel"));

		Dictionary<Gtk.FileFilter, FormatDescriptor> formats = AddImageFilters (chooser);
		SetInitialSaveLocation (chooser, layer);

		while (await chooser.RunAsync () == Gtk.ResponseType.Accept) {
			Gio.File file = chooser.GetFile ()!;
			FormatDescriptor? format = GetSelectedFormat (chooser, file, formats);
			if (format?.Exporter is null)
				continue;

			try {
				ExportLayerImage (document, layer, file, format.Exporter);
				recent_files.LastDialogDirectory = file.GetParent ();
				image_formats.SetDefaultFormat (format.Extensions.First ());
				return;
			} catch (OperationCanceledException) {
				return;
			} catch (Exception exception) {
				await chrome.ShowErrorDialog (
					chrome.MainWindow,
					Translations.GetString ("Failed to save image"),
					exception.Message,
					exception.ToString ());
				return;
			}
		}
	}

	private Dictionary<Gtk.FileFilter, FormatDescriptor> AddImageFilters (Gtk.FileChooserNative chooser)
	{
		Dictionary<Gtk.FileFilter, FormatDescriptor> formats = [];
		foreach (FormatDescriptor format in image_formats.Formats) {
			if (!format.IsExportAvailable () || format.Extensions.Contains ("pinta", StringComparer.OrdinalIgnoreCase))
				continue;

			chooser.AddFilter (format.Filter);
			formats.Add (format.Filter, format);
		}

		FormatDescriptor defaultFormat = GetDefaultLayerFormat ();
		FormatDescriptor? layerFormat = formats.Values.FirstOrDefault (format =>
			format.Extensions.Contains (defaultFormat.Extensions.First (), StringComparer.OrdinalIgnoreCase));
		chooser.Filter = layerFormat?.Filter ?? formats.Values.First ().Filter;
		return formats;
	}

	private void SetInitialSaveLocation (Gtk.FileChooserNative chooser, UserLayer layer)
	{
		if (recent_files.GetDialogDirectory () is Gio.File directory && directory.QueryExists (null))
			chooser.SetCurrentFolder (directory);

		string name = string.IsNullOrWhiteSpace (layer.Name) ? "Layer" : layer.Name;
		string extension = GetDefaultLayerFormat ().Extensions.First ();
		chooser.SetCurrentName ($"{name}.{extension}");
	}

	private FormatDescriptor GetDefaultLayerFormat ()
	{
		FormatDescriptor defaultFormat = image_formats.GetDefaultSaveFormat ();
		if (defaultFormat.IsExportAvailable () && !defaultFormat.Extensions.Contains ("pinta", StringComparer.OrdinalIgnoreCase))
			return defaultFormat;

		return image_formats.Formats.First (format =>
			format.IsExportAvailable () && !format.Extensions.Contains ("pinta", StringComparer.OrdinalIgnoreCase));
	}

	private static FormatDescriptor? GetSelectedFormat (
		Gtk.FileChooserNative chooser,
		Gio.File file,
		IReadOnlyDictionary<Gtk.FileFilter, FormatDescriptor> formats)
	{
		string name = file.GetParent ()?.GetRelativePath (file) ?? file.GetDisplayName ();
		FormatDescriptor? format = formats.Values.FirstOrDefault (candidate =>
			candidate.Extensions.Contains (System.IO.Path.GetExtension (name).TrimStart ('.'), StringComparer.OrdinalIgnoreCase));
		if (format is not null)
			return format;

		return chooser.Filter is not null && formats.TryGetValue (chooser.Filter, out FormatDescriptor? selected)
			? selected
			: null;
	}

	private static void ExportLayerImage (
		Document document,
		UserLayer layer,
		Gio.File file,
		IImageExporter exporter)
	{
		using ImageSurface image = RenderLayer (document, layer);
		Document exportDocument = new (
			PintaCore.Actions,
			PintaCore.Tools,
			PintaCore.Workspace,
			new Size (image.Width, image.Height));
		UserLayer exportLayer = exportDocument.Layers.AddNewLayer (layer.Name);
		exportLayer.Surface = image.Clone ();
		try {
			exporter.Export (exportDocument, file, PintaCore.Chrome.MainWindow);
		} finally {
			exportLayer.Surface.Dispose ();
			exportDocument.Close ();
		}
	}

	public static ImageSurface RenderLayer (Document document, UserLayer layer)
	{
		List<Layer> paintLayers = [.. layer.GetLayersToPaint ()];
		if (paintLayers.Count == 0)
			return CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);

		double left = double.PositiveInfinity;
		double top = double.PositiveInfinity;
		double right = double.NegativeInfinity;
		double bottom = double.NegativeInfinity;
		foreach (Layer paintLayer in paintLayers)
			ExpandBounds (paintLayer, ref left, ref top, ref right, ref bottom);

		int originX = (int) Math.Floor (left);
		int originY = (int) Math.Floor (top);
		int width = Math.Max (1, (int) Math.Ceiling (right) - originX);
		int height = Math.Max (1, (int) Math.Ceiling (bottom) - originY);
		ImageSurface image = CairoExtensions.CreateImageSurface (
			Format.Argb32,
			width,
			height);
		using Context context = new (image);
		context.Translate (-originX, -originY);
		foreach (Layer paintLayer in paintLayers)
			paintLayer.Draw (context);
		image.MarkDirty ();
		return image;
	}

	private static void ExpandBounds (
		Layer layer,
		ref double left,
		ref double top,
		ref double right,
		ref double bottom)
	{
		PointD[] corners = [
			layer.Transform.TransformPoint (new PointD (0, 0)),
			layer.Transform.TransformPoint (new PointD (layer.Surface.Width, 0)),
			layer.Transform.TransformPoint (new PointD (0, layer.Surface.Height)),
			layer.Transform.TransformPoint (new PointD (layer.Surface.Width, layer.Surface.Height))];

		foreach (PointD corner in corners) {
			left = Math.Min (left, corner.X);
			top = Math.Min (top, corner.Y);
			right = Math.Max (right, corner.X);
			bottom = Math.Max (bottom, corner.Y);
		}
	}
}
