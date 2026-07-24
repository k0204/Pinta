using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Cairo;
using ClipperLib;
using GdkPixbuf;

namespace Pinta.Core;

public sealed class PintaDocumentFormat : IImageImporter, IImageExporter
{
	public const string FormatName = "pinta-document";
	public const int CurrentVersion = 1;

	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	public Document Import (Gio.File file)
	{
		using GioStream stream = new (file.Read (cancellable: null));
		using ZipArchive archive = new (stream, ZipArchiveMode.Read);
		PintaDocumentManifest manifest = ReadManifest (archive);

		Document document = new (
			PintaCore.Actions,
			PintaCore.Tools,
			PintaCore.Workspace,
			new Size (manifest.Width, manifest.Height),
			file,
			"pinta");

		Dictionary<string, UserLayer> layersById = [];
		ImportLayers (document, archive, manifest.Layers, parent: null, layersById);

		if (manifest.SelectedLayerId is not null)
			document.Layers.SetCurrentUserLayer (layersById[manifest.SelectedLayerId]);

                document.Guides.ReplaceAll (CreateGuides (manifest.Guides));
		document.Selection = CreateSelection (manifest.Selection);
		return document;
	}

	public void Export (Document document, Gio.File file, Gtk.Window parent)
	{
		using GioStream stream = new (file.Replace ());
		using ZipArchive archive = new (stream, ZipArchiveMode.Create);
		WriteArchive (document, archive);
	}

	internal static void WriteArchive (Document document, ZipArchive archive)
	{
		AssignLayerIds (document.Layers.RootLayers);

		foreach (UserLayer layer in document.Layers.AllLayers) {
			using Pixbuf pixbuf = layer.Surface.ToPixbuf ();
			byte[] data = pixbuf.SaveToBuffer ("png");
			ZipArchiveEntry entry = archive.CreateEntry ($"layers/{layer.DocumentId}.png");
			using Stream destination = entry.Open ();
			destination.Write (data);
		}

		PintaDocumentManifest manifest = CreateManifest (document);
		ZipArchiveEntry manifestEntry = archive.CreateEntry ("project.json");
		using Stream manifestStream = manifestEntry.Open ();
		JsonSerializer.Serialize (manifestStream, manifest, json_options);
	}

	internal static PintaDocumentManifest ReadManifest (ZipArchive archive)
	{
		ZipArchiveEntry entry = archive.GetEntry ("project.json")
			?? throw new InvalidDataException ("The Pinta document does not contain project.json.");

		PintaDocumentManifest manifest;
		try {
			using Stream stream = entry.Open ();
			manifest = JsonSerializer.Deserialize<PintaDocumentManifest> (stream, json_options)
				?? throw new InvalidDataException ("The Pinta document manifest is empty.");
		} catch (JsonException e) {
			throw new InvalidDataException ("The Pinta document manifest is not valid JSON.", e);
		}

		ValidateManifest (manifest, archive);
		return manifest;
	}

	private static PintaDocumentManifest CreateManifest (Document document)
	{
		UserLayer selectedLayer = document.Layers.CurrentUserLayer;
		return new () {
			Format = FormatName,
			Version = CurrentVersion,
			Width = document.ImageSize.Width,
			Height = document.ImageSize.Height,
			SelectedLayerId = selectedLayer.DocumentId,
                        Guides = [.. document.Guides.Items.Select (guide => new PintaDocumentGuide {
                                Orientation = guide.Orientation,
                                Position = guide.Position,
                        })],
			Selection = CreateSelectionModel (document.Selection),
			Layers = [.. document.Layers.RootLayers.Select (CreateLayerModel)],
		};
	}

	private static PintaDocumentLayerNode CreateLayerModel (UserLayer layer)
	{
		PointD origin = layer.Transform.TransformPoint (PointD.Zero);
		PointD xAxis = layer.Transform.TransformPoint (new PointD (1, 0));
		PointD yAxis = layer.Transform.TransformPoint (new PointD (0, 1));
		return new () {
			Id = layer.DocumentId!,
			Name = layer.Name,
			Hidden = layer.Hidden,
			Opacity = layer.Opacity,
			BlendMode = layer.BlendMode.ToString (),
			Expanded = layer.Expanded,
			Surface = $"layers/{layer.DocumentId}.png",
			Transform = new () {
				Xx = xAxis.X - origin.X,
				Yx = xAxis.Y - origin.Y,
				Xy = yAxis.X - origin.X,
				Yy = yAxis.Y - origin.Y,
				X0 = origin.X,
				Y0 = origin.Y,
			},
			Children = [.. layer.Children.Select (CreateLayerModel)],
		};
	}

	private static PintaDocumentSelection CreateSelectionModel (DocumentSelection selection)
		=> new () {
			Visible = selection.Visible,
			HandleBounds = new () {
				X = selection.HandleBounds.X,
				Y = selection.HandleBounds.Y,
				Width = selection.HandleBounds.Width,
				Height = selection.HandleBounds.Height,
			},
			Polygons = [.. selection.SelectionPolygons.Select (
				polygon => polygon.Select (point => new PintaDocumentPoint { X = point.X, Y = point.Y }).ToList ())],
		};

        private static IReadOnlyList<DocumentGuide> CreateGuides (IReadOnlyList<PintaDocumentGuide> guides)
                => [.. guides.Select (guide => new DocumentGuide (guide.Orientation, guide.Position))];

	private static DocumentSelection CreateSelection (PintaDocumentSelection selection)
		=> new () {
			Visible = selection.Visible,
			HandleBounds = new RectangleD (
				selection.HandleBounds.X,
				selection.HandleBounds.Y,
				selection.HandleBounds.Width,
				selection.HandleBounds.Height),
			SelectionPolygons = [.. selection.Polygons.Select (
				polygon => polygon.Select (point => new IntPoint (point.X, point.Y)).ToList ())],
		};

	private static void ImportLayers (
		Document document,
		ZipArchive archive,
		IReadOnlyList<PintaDocumentLayerNode> nodes,
		UserLayer? parent,
		Dictionary<string, UserLayer> layersById)
	{
		for (int index = 0; index < nodes.Count; index++) {
			PintaDocumentLayerNode node = nodes[index];
			UserLayer layer = document.Layers.CreateLayer (node.Name);
			layer.DocumentId = node.Id;
			layer.Hidden = node.Hidden;
			layer.Opacity = node.Opacity;
			layer.BlendMode = Enum.Parse<BlendMode> (node.BlendMode);
			layer.Expanded = node.Expanded;
			layer.Transform = CairoExtensions.CreateMatrix (
				node.Transform.Xx,
				node.Transform.Xy,
				node.Transform.Yx,
				node.Transform.Yy,
				node.Transform.X0,
				node.Transform.Y0);

			LoadSurface (archive.GetEntry (node.Surface)!, layer);
			document.Layers.Insert (layer, new LayerPosition (parent, index));
			layersById.Add (node.Id, layer);
			ImportLayers (document, archive, node.Children, layer, layersById);
		}
	}

	private static void LoadSurface (ZipArchiveEntry entry, UserLayer layer)
	{
		string temporaryFile = System.IO.Path.GetTempFileName ();
		try {
			using (Stream source = entry.Open ())
			using (FileStream destination = File.OpenWrite (temporaryFile))
				source.CopyTo (destination);

			using Pixbuf pixbuf = Pixbuf.NewFromFile (temporaryFile)
				?? throw new InvalidDataException ($"Layer surface '{entry.FullName}' could not be decoded as PNG.");
			if (pixbuf.Width != layer.Surface.Width || pixbuf.Height != layer.Surface.Height)
				throw new InvalidDataException ($"Layer surface '{entry.FullName}' does not match the document dimensions.");

			using Context context = new (layer.Surface);
			context.DrawPixbuf (pixbuf, PointD.Zero);
		} catch (GLib.GException e) {
			throw new InvalidDataException ($"Layer surface '{entry.FullName}' is not a valid PNG.", e);
		} finally {
			File.Delete (temporaryFile);
		}
	}

	private static void AssignLayerIds (IReadOnlyList<UserLayer> roots)
	{
		List<UserLayer> layers = [.. roots.SelectMany (layer => layer.GetSelfAndDescendants ())];
		HashSet<string> usedIds = [];
		int nextId = 1;
		foreach (UserLayer layer in layers) {
			if (layer.DocumentId is not null && usedIds.Add (layer.DocumentId))
				continue;
			layer.DocumentId = null;

			string id;
			do id = $"layer-{nextId++:D4}";
			while (!usedIds.Add (id));
			layer.DocumentId = id;
		}
	}

	private static void ValidateManifest (PintaDocumentManifest manifest, ZipArchive archive)
	{
		if (manifest.Format != FormatName)
			throw new InvalidDataException ($"Unsupported Pinta document format '{manifest.Format}'.");
		if (manifest.Version != CurrentVersion)
			throw new InvalidDataException ($"Unsupported Pinta document version {manifest.Version}.");
		if (manifest.Width <= 0 || manifest.Height <= 0)
			throw new InvalidDataException ("The Pinta document dimensions must be positive.");
                if (manifest.Guides is null)
                        throw new InvalidDataException ("The Pinta document guides collection is missing.");
                if (manifest.Guides.Any (guide => !Enum.IsDefined (guide.Orientation) || !double.IsFinite (guide.Position)))
                        throw new InvalidDataException ("The Pinta document guides are invalid.");
		if (manifest.Selection is null || manifest.Selection.HandleBounds is null || manifest.Selection.Polygons is null)
			throw new InvalidDataException ("The Pinta document selection is incomplete.");
		if (!IsFinite (manifest.Selection.HandleBounds))
			throw new InvalidDataException ("The Pinta document selection bounds are invalid.");
		if (manifest.Selection.Polygons.Any (polygon => polygon is null || polygon.Any (point => point is null)))
			throw new InvalidDataException ("The Pinta document selection polygons are invalid.");
		if (manifest.Layers is null || manifest.Layers.Count == 0)
			throw new InvalidDataException ("The Pinta document does not contain any layers.");

		HashSet<string> ids = [];
		ValidateLayers (manifest.Layers, archive, ids);
		if (manifest.SelectedLayerId is not null && !ids.Contains (manifest.SelectedLayerId))
			throw new InvalidDataException ($"Selected layer '{manifest.SelectedLayerId}' does not exist.");
	}

	private static void ValidateLayers (
		IReadOnlyList<PintaDocumentLayerNode> nodes,
		ZipArchive archive,
		HashSet<string> ids)
	{
		foreach (PintaDocumentLayerNode node in nodes) {
			if (node is null)
				throw new InvalidDataException ("The Pinta document contains an invalid layer node.");
			if (string.IsNullOrWhiteSpace (node.Id) || !ids.Add (node.Id))
				throw new InvalidDataException ($"The Pinta document contains an invalid or duplicate layer ID '{node.Id}'.");
			if (node.Name is null)
				throw new InvalidDataException ($"Layer '{node.Id}' has no name.");
			if (!double.IsFinite (node.Opacity) || node.Opacity < 0 || node.Opacity > 1)
				throw new InvalidDataException ($"Layer '{node.Id}' has invalid opacity.");
			if (!Enum.TryParse<BlendMode> (node.BlendMode, ignoreCase: false, out _))
				throw new InvalidDataException ($"Layer '{node.Id}' has unknown blend mode '{node.BlendMode}'.");
			if (node.Transform is null || !IsFinite (node.Transform))
				throw new InvalidDataException ($"Layer '{node.Id}' has an invalid transform.");
			if (node.Surface != $"layers/{node.Id}.png")
				throw new InvalidDataException ($"Layer '{node.Id}' has an invalid surface path.");
			if (archive.GetEntry (node.Surface) is null)
				throw new InvalidDataException ($"Layer surface '{node.Surface}' is missing.");
			if (node.Children is null)
				throw new InvalidDataException ($"Layer '{node.Id}' has no children collection.");

			ValidateLayers (node.Children, archive, ids);
		}
	}

	private static bool IsFinite (PintaDocumentRectangle rectangle)
		=> double.IsFinite (rectangle.X)
		&& double.IsFinite (rectangle.Y)
		&& double.IsFinite (rectangle.Width)
		&& double.IsFinite (rectangle.Height);

	private static bool IsFinite (PintaDocumentMatrix matrix)
		=> double.IsFinite (matrix.Xx)
		&& double.IsFinite (matrix.Yx)
		&& double.IsFinite (matrix.Xy)
		&& double.IsFinite (matrix.Yy)
		&& double.IsFinite (matrix.X0)
		&& double.IsFinite (matrix.Y0);
}
