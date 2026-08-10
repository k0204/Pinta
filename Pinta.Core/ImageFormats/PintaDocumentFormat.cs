using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cairo;
using ClipperLib;
using GdkPixbuf;
using Path = System.IO.Path;

namespace Pinta.Core;

public sealed partial class PintaDocumentFormat : IImageImporter, IImageExporter
{
	public const string FormatName = "pinta-document";
	public const string Extension = "pintaproject";
	public const int CurrentVersion = 1;

	private const string manifest_name = "project.json";
	private const string resources_directory = "resources";
	private const string staging_directory = ".staging";

	private static readonly JsonSerializerOptions json_options = new () {
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	public Document Import (Gio.File file)
	{
		string root = GetProjectPath (file);
		PintaDocumentManifest manifest = ReadManifest (root);

		Document document = new (
			PintaCore.Actions,
			PintaCore.Tools,
			PintaCore.Workspace,
			new Size (manifest.Width, manifest.Height),
			file,
			Extension);

		Dictionary<string, UserLayer> layersById = [];
		document.SetResourceRoot (manifest.ResourceRoot is null ? null : Gio.FileHelper.NewForUri (manifest.ResourceRoot));
		ImportLayers (document, root, manifest.Layers, parent: null, layersById);

		if (manifest.SelectedLayerId is not null)
			document.Layers.SetCurrentUserLayer (layersById[manifest.SelectedLayerId]);

		document.Guides.ReplaceAll (CreateGuides (manifest.Guides));
		document.Selection = CreateSelection (manifest.Selection);
		return document;
	}

	public void Export (Document document, Gio.File file, Gtk.Window parent)
		=> ExportWithProgress (document, file, progress: null);

	public void ExportWithProgress (
		Document document,
		Gio.File file,
		IProgress<double>? progress)
	{
		string root = GetProjectPath (file);
		if (File.Exists (root))
			throw new IOException (Translations.GetString ("The Pinta project path is a file, not a folder."));

		Directory.CreateDirectory (root);
		PintaDocumentManifest? previous = ReadPreviousManifest (root);
		string saveId = Guid.NewGuid ().ToString ("N");
		string stagingRoot = Path.Combine (root, staging_directory, saveId);
		List<PendingResource> pending = [];
		List<PendingFile> pendingFiles = [];
		List<string> createdResources = [];
		bool manifestCommitted = false;

		try {
			AssignLayerGuids (document.Layers.RootLayers);
			PintaDocumentManifest manifest = CreateManifest (document, root, previous, saveId, pending, pendingFiles);
			progress?.Report (0);

			WritePendingResources (root, stagingRoot, pending, createdResources, progress);
			WritePendingFiles (root, stagingRoot, pendingFiles, createdResources);
			WriteManifest (root, stagingRoot, manifest);
			manifestCommitted = true;
			ApplyVideoPaths (document.Layers.RootLayers, manifest.Layers, root);
			DeleteUnreferencedResources (root, previous, manifest);
		} finally {
			if (!manifestCommitted)
				DeleteCreatedResources (root, createdResources);

			TryDeleteDirectory (stagingRoot);
		}

		progress?.Report (1);
	}

	private static string GetProjectPath (Gio.File file)
	{
		string? path = file.GetPath ();
		if (path is null)
			throw new IOException (Translations.GetString ("Pinta projects must be stored on a local file system."));

		return Path.GetFullPath (path);
	}

	private static PintaDocumentManifest? ReadPreviousManifest (string root)
	{
		string path = Path.Combine (root, manifest_name);
		return File.Exists (path) ? ReadManifest (root) : null;
	}

	private static PintaDocumentManifest ReadManifest (string root)
	{
		string path = Path.Combine (root, manifest_name);
		if (!File.Exists (path))
			throw new InvalidDataException (Translations.GetString ("The Pinta project does not contain project.json."));

		PintaDocumentManifest manifest;
		try {
			using FileStream stream = File.OpenRead (path);
			manifest = JsonSerializer.Deserialize<PintaDocumentManifest> (stream, json_options)
				?? throw new InvalidDataException (Translations.GetString ("The Pinta project manifest is empty."));
		} catch (JsonException e) {
			throw new InvalidDataException (Translations.GetString ("The Pinta project manifest is not valid JSON."), e);
		}

		ValidateManifest (manifest, root);
		return manifest;
	}

	private static void WriteManifest (
		string root,
		string stagingRoot,
		PintaDocumentManifest manifest)
	{
		Directory.CreateDirectory (stagingRoot);
		string temporaryPath = Path.Combine (stagingRoot, manifest_name);
		using (FileStream stream = File.Create (temporaryPath))
			JsonSerializer.Serialize (stream, manifest, json_options);

		ReplaceFile (temporaryPath, Path.Combine (root, manifest_name));
	}

	private static void ReplaceFile (string source, string destination)
		=> File.Move (source, destination, overwrite: true);

	private static PintaDocumentManifest CreateManifest (
		Document document,
		string root,
		PintaDocumentManifest? previous,
		string saveId,
		ICollection<PendingResource> pending,
		ICollection<PendingFile> pendingFiles)
	{
		UserLayer selectedLayer = document.Layers.CurrentUserLayer;
		return new () {
			Format = FormatName,
			Version = CurrentVersion,
			Width = document.ImageSize.Width,
			Height = document.ImageSize.Height,
			ResourceRoot = document.ResourceRootUri,
			SelectedLayerId = selectedLayer.DocumentId,
			Guides = [.. document.Guides.Items.Select (guide => new PintaDocumentGuide {
				Orientation = guide.Orientation,
				Position = guide.Position,
			})],
			Selection = CreateSelectionModel (document.Selection),
			Layers = [.. document.Layers.RootLayers.Select (
				layer => CreateLayerModel (layer, root, previous?.Layers, saveId, pending, pendingFiles))],
		};
	}

	private static PintaDocumentLayerNode CreateLayerModel (
		UserLayer layer,
		string root,
		IReadOnlyList<PintaDocumentLayerNode>? previousLayers,
		string saveId,
		ICollection<PendingResource> pending,
		ICollection<PendingFile> pendingFiles)
	{
		PintaDocumentLayerNode? previous = previousLayers?.FirstOrDefault (node => node.Id == layer.DocumentId);
		PointD origin = layer.Transform.TransformPoint (PointD.Zero);
		PointD xAxis = layer.Transform.TransformPoint (new PointD (1, 0));
		PointD yAxis = layer.Transform.TransformPoint (new PointD (0, 1));
		PintaDocumentLayerNode result = new () {
			Id = layer.DocumentId!,
			Name = layer.Name,
			Hidden = layer.Hidden,
			Locked = layer.Locked,
			Opacity = layer.Opacity,
			BlendMode = layer.BlendMode.ToString (),
			Expanded = layer.Expanded,
			Kind = layer switch {
				SpriteSheetLayer => "spritesheet",
				SingleDirectionAnimationLayer => "single-direction-animation",
				VideoEditingLayer => "video-editing",
				GroupLayer => "group",
				_ => "layer",
			},
			Storage = layer.IsReference ? "reference" : "embedded",
			SurfaceWidth = layer is GroupLayer || layer is AnimationOutputLayer ? null : layer.Surface.Width,
			SurfaceHeight = layer is GroupLayer || layer is AnimationOutputLayer ? null : layer.Surface.Height,
			ReferencePath = layer.ReferencePath,
			Metadata = layer is VideoEditingLayer
				? new (layer.Metadata.Where (item => item.Key != VideoEditingLayer.VideoPathMetadataKey))
				: new (layer.Metadata),
			SpritesheetSplit = layer.SpritesheetSplit,
			PositionOffsetX = layer is AnimationOutputLayer animation ? animation.PositionOffset.X : 0,
			PositionOffsetY = layer is AnimationOutputLayer animationLayer ? animationLayer.PositionOffset.Y : 0,
			Transform = new () {
				Xx = xAxis.X - origin.X,
				Yx = xAxis.Y - origin.Y,
				Xy = yAxis.X - origin.X,
				Yy = yAxis.Y - origin.Y,
				X0 = origin.X,
				Y0 = origin.Y,
			},
			Children = [.. layer.Children.Select (child => CreateLayerModel (
				child, root, previous?.Children, saveId, pending, pendingFiles))],
		};

		if (layer is VideoEditingLayer videoLayer)
			result.Video = GetVideoResource (videoLayer, root, previous?.Video, saveId, pendingFiles);

		if (layer is SpriteSheetLayer spriteSheet)
			result.SpriteSheetAnimations = CreateSpriteSheetAnimations (spriteSheet, root, previous, saveId, pending);
		else if (layer is SingleDirectionAnimationLayer singleDirection) {
			result.SingleDirectionId = singleDirection.DirectionId;
			result.SingleDirectionAnimations = CreateSingleDirectionAnimations (
				singleDirection, root, previous, saveId, pending);
		}
		else if (layer is not GroupLayer && !layer.IsReference)
			(result.Surface, result.SurfaceHash) = GetResource (
				layer.Surface,
				root,
				previous?.Surface,
				previous?.SurfaceHash,
				$"{resources_directory}/layers/{layer.DocumentId}/{saveId}.png",
				pending);

		return result;
	}

	private static List<PintaDocumentSpriteSheetAnimation> CreateSpriteSheetAnimations (
		SpriteSheetLayer layer,
		string root,
		PintaDocumentLayerNode? previous,
		string saveId,
		ICollection<PendingResource> pending)
	{
		int resourceIndex = 0;
		return [.. layer.Animations.Select (animation => new PintaDocumentSpriteSheetAnimation {
			ActionId = animation.ActionId,
			CanvasWidth = animation.CanvasWidth,
			CanvasHeight = animation.CanvasHeight,
			Directions = [.. animation.Directions.Select (direction => new PintaDocumentSpriteSheetDirection {
				DirectionId = direction.DirectionId,
				Frames = [.. direction.Frames.Select (frame => {
					PintaDocumentSpriteSheetFrame? oldFrame = previous?.SpriteSheetAnimations
						.Where (item => item.ActionId == animation.ActionId)
						.SelectMany (item => item.Directions)
						.Where (item => item.DirectionId == direction.DirectionId)
						.SelectMany (item => item.Frames)
						.FirstOrDefault (item => item.FrameIndex == frame.FrameIndex);
					(string? surface, string hash) = GetResource (
						frame.Surface,
						root,
						oldFrame?.Surface,
						oldFrame?.SurfaceHash,
						$"{resources_directory}/spritesheets/{layer.DocumentId}/{saveId}/frame-{resourceIndex++:D6}.png",
						pending);
					return new PintaDocumentSpriteSheetFrame {
						FrameIndex = frame.FrameIndex,
						X = frame.X,
						Y = frame.Y,
						Visible = frame.Visible,
						Surface = surface!,
						SurfaceHash = hash,
						Width = frame.Surface.Width,
						Height = frame.Surface.Height,
					};
				})],
			})],
		})];
	}

	private static List<PintaDocumentSingleDirectionAnimation> CreateSingleDirectionAnimations (
		SingleDirectionAnimationLayer layer,
		string root,
		PintaDocumentLayerNode? previous,
		string saveId,
		ICollection<PendingResource> pending)
	{
		int resourceIndex = 0;
		return [.. layer.Animations.Select (animation => new PintaDocumentSingleDirectionAnimation {
			ActionId = animation.ActionId,
			CanvasWidth = animation.CanvasWidth,
			CanvasHeight = animation.CanvasHeight,
			Frames = [.. animation.Frames.Select (frame => {
				PintaDocumentSingleDirectionFrame? oldFrame = previous?.SingleDirectionAnimations
					.Where (item => item.ActionId == animation.ActionId)
					.SelectMany (item => item.Frames)
					.FirstOrDefault (item => item.FrameIndex == frame.FrameIndex);
				(string? surface, string hash) = GetResource (
					frame.Surface,
					root,
					oldFrame?.Surface,
					oldFrame?.SurfaceHash,
					$"{resources_directory}/single-direction-animations/{layer.DocumentId}/{saveId}/frame-{resourceIndex++:D6}.png",
					pending);
				return new PintaDocumentSingleDirectionFrame {
					FrameIndex = frame.FrameIndex,
					X = frame.X,
					Y = frame.Y,
					Visible = frame.Visible,
					Surface = surface!,
					SurfaceHash = hash,
					Width = frame.Surface.Width,
					Height = frame.Surface.Height,
				};
			})],
		})];
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
		string root,
		IReadOnlyList<PintaDocumentLayerNode> nodes,
		UserLayer? parent,
		Dictionary<string, UserLayer> layersById)
	{
		for (int index = 0; index < nodes.Count; index++) {
			PintaDocumentLayerNode node = nodes[index];
			UserLayer layer = CreateLayer (document, node);
			layer.DocumentId = node.Id;
			layer.Hidden = node.Hidden;
			layer.Locked = node.Locked;
			layer.Opacity = node.Opacity;
			layer.BlendMode = Enum.Parse<BlendMode> (node.BlendMode);
			layer.Expanded = node.Expanded;
			foreach ((string key, string value) in node.Metadata)
				layer.Metadata.Add (key, value);
			if (layer is VideoEditingLayer videoLayer && node.Video is string video)
				videoLayer.VideoPath = ResolveManagedResourcePath (root, video);
			layer.SpritesheetSplit = node.SpritesheetSplit;
			layer.Transform = CairoExtensions.CreateMatrix (
				node.Transform.Xx,
				node.Transform.Xy,
				node.Transform.Yx,
				node.Transform.Yy,
				node.Transform.X0,
				node.Transform.Y0);

			if (node.Kind == "single-direction-animation")
				LoadSingleDirectionAnimationSurfaces (root, (SingleDirectionAnimationLayer) layer, node);
			else if (node.Kind == "spritesheet")
				LoadSpriteSheetSurfaces (root, (SpriteSheetLayer) layer, node);
			else if (node.Kind == "layer" && node.Storage == "embedded")
				LoadSurface (root, node.Surface!, layer.Surface);
			else if (node.Kind == "layer") {
				layer.ReferencePath = node.ReferencePath;
				document.LoadReferencedLayer (layer);
			}

			document.Layers.Insert (layer, new LayerPosition (parent, index));
			layersById.Add (node.Id, layer);
			ImportLayers (document, root, node.Children, layer, layersById);
		}
	}

	private static UserLayer CreateLayer (Document document, PintaDocumentLayerNode node)
		=> node.Kind switch {
			"single-direction-animation" => CreateSingleDirectionAnimationLayer (document, node),
			"spritesheet" => CreateSpriteSheetLayer (document, node),
			"group" => document.Layers.CreateGroupLayer (node.Name, node.SurfaceWidth, node.SurfaceHeight),
			"video-editing" => document.Layers.CreateVideoEditingLayer (node.Name),
			_ => document.Layers.CreateLayer (node.Name, node.SurfaceWidth, node.SurfaceHeight),
		};

	private static SpriteSheetLayer CreateSpriteSheetLayer (Document document, PintaDocumentLayerNode node)
	{
		PintaDocumentSpriteSheetAnimation animation = node.SpriteSheetAnimations[0];
		List<SpriteSheetAnimationData> animations = [];
		foreach (PintaDocumentSpriteSheetAnimation sourceAnimation in node.SpriteSheetAnimations) {
			SpriteSheetAnimationData animationData = new (sourceAnimation.ActionId, sourceAnimation.CanvasWidth, sourceAnimation.CanvasHeight);
			foreach (PintaDocumentSpriteSheetDirection sourceDirection in sourceAnimation.Directions) {
				SpriteSheetDirectionData directionData = animationData.AddDirection (sourceDirection.DirectionId);
				foreach (PintaDocumentSpriteSheetFrame sourceFrame in sourceDirection.Frames) {
					ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, sourceFrame.Width, sourceFrame.Height);
					directionData.Frames.Add (new AnimationFrameData (sourceFrame.FrameIndex, sourceFrame.X, sourceFrame.Y, sourceFrame.Visible, surface));
				}
			}
			animations.Add (animationData);
		}

		SpriteSheetLayer layer = document.Layers.CreateSpriteSheetLayer (node.Name, animation.CanvasWidth, animation.CanvasHeight);
		layer.ReplaceSnapshot (new SpriteSheetLayerSnapshot (
			animation.CanvasWidth,
			animation.CanvasHeight,
			new PointD (node.PositionOffsetX, node.PositionOffsetY),
			animations), document.ImageSize);
		return layer;
	}

	private static SingleDirectionAnimationLayer CreateSingleDirectionAnimationLayer (Document document, PintaDocumentLayerNode node)
	{
		PintaDocumentSingleDirectionAnimation animation = node.SingleDirectionAnimations[0];
		List<SingleDirectionAnimationData> animations = [];
		foreach (PintaDocumentSingleDirectionAnimation sourceAnimation in node.SingleDirectionAnimations) {
			SingleDirectionAnimationData animationData = new (sourceAnimation.ActionId, sourceAnimation.CanvasWidth, sourceAnimation.CanvasHeight);
			foreach (PintaDocumentSingleDirectionFrame sourceFrame in sourceAnimation.Frames) {
				ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, sourceFrame.Width, sourceFrame.Height);
				animationData.Frames.Add (new AnimationFrameData (sourceFrame.FrameIndex, sourceFrame.X, sourceFrame.Y, sourceFrame.Visible, surface));
			}
			animations.Add (animationData);
		}

		SingleDirectionAnimationLayer layer = document.Layers.CreateSingleDirectionAnimationLayer (
			node.Name,
			animation.CanvasWidth,
			animation.CanvasHeight,
			node.SingleDirectionId!);
		layer.ReplaceSnapshot (new SingleDirectionAnimationLayerSnapshot (
			node.SingleDirectionId!,
			animation.CanvasWidth,
			animation.CanvasHeight,
			new PointD (node.PositionOffsetX, node.PositionOffsetY),
			animations), document.ImageSize);
		return layer;
	}

	private static void LoadSpriteSheetSurfaces (string root, SpriteSheetLayer layer, PintaDocumentLayerNode node)
	{
		IEnumerable<PintaDocumentSpriteSheetFrame> frames = node.SpriteSheetAnimations
			.SelectMany (animation => animation.Directions.SelectMany (direction => direction.Frames));
		foreach ((AnimationFrameData frame, PintaDocumentSpriteSheetFrame manifestFrame) in layer.GetFrames ().Zip (frames))
			LoadSurface (root, manifestFrame.Surface, frame.Surface);
	}

	private static void LoadSingleDirectionAnimationSurfaces (
		string root,
		SingleDirectionAnimationLayer layer,
		PintaDocumentLayerNode node)
	{
		IEnumerable<PintaDocumentSingleDirectionFrame> frames = node.SingleDirectionAnimations
			.SelectMany (animation => animation.Frames);
		foreach ((AnimationFrameData frame, PintaDocumentSingleDirectionFrame manifestFrame) in layer.GetFrames ().Zip (frames))
			LoadSurface (root, manifestFrame.Surface, frame.Surface);
	}

	private static void LoadSurface (string root, string relativePath, ImageSurface surface)
	{
		string path = ResolveResourcePath (root, relativePath);
		try {
			using Pixbuf pixbuf = Pixbuf.NewFromFile (path)
				?? throw new InvalidDataException (Translations.GetString ("The Pinta project resource could not be decoded."));
			if (pixbuf.Width != surface.Width || pixbuf.Height != surface.Height)
				throw new InvalidDataException (Translations.GetString ("The Pinta project resource dimensions are invalid."));

			using Context context = new (surface);
			context.DrawPixbuf (pixbuf, PointD.Zero);
		} catch (GLib.GException e) {
			throw new InvalidDataException (Translations.GetString ("The Pinta project resource is not a valid PNG."), e);
		}
	}

	private static string ResolveResourcePath (string root, string relativePath)
	{
		if (!IsValidResourcePath (relativePath))
			throw new InvalidDataException (Translations.GetString ("The Pinta project contains an invalid resource path."));

		return Path.Combine (root, ToSystemPath (relativePath));
	}

	private static string ResolveManagedResourcePath (string root, string relativePath)
	{
		if (!IsValidManagedPath (relativePath))
			throw new InvalidDataException (Translations.GetString ("The Pinta project contains an invalid resource path."));

		return Path.Combine (root, ToSystemPath (relativePath));
	}

	private static void AssignLayerGuids (IReadOnlyList<UserLayer> roots)
	{
		List<UserLayer> layers = [.. roots.SelectMany (layer => layer.GetSelfAndDescendants ())];
		HashSet<Guid> usedIds = [];
		foreach (UserLayer layer in layers) {
			if (Guid.TryParseExact (layer.DocumentId, "N", out Guid id)
				&& id != Guid.Empty
				&& usedIds.Add (id))
				continue;

			do id = Guid.NewGuid ();
			while (!usedIds.Add (id));
			layer.DocumentId = id.ToString ("N");
		}
	}

	private static void ValidateManifest (PintaDocumentManifest manifest, string root)
	{
		if (manifest.Format != FormatName || manifest.Version != CurrentVersion)
			throw new InvalidDataException (Translations.GetString ("Unsupported Pinta project format or version."));
		if (manifest.ResourceRoot is not null
			&& (!Uri.TryCreate (manifest.ResourceRoot, UriKind.Absolute, out Uri? rootUri) || rootUri.Scheme != Uri.UriSchemeFile))
			throw new InvalidDataException (Translations.GetString ("The Pinta project resource root is invalid."));
		if (manifest.Width <= 0 || manifest.Height <= 0 || manifest.Guides is null)
			throw new InvalidDataException (Translations.GetString ("The Pinta project dimensions or guides are invalid."));
		if (manifest.Guides.Any (guide => !Enum.IsDefined (guide.Orientation) || !double.IsFinite (guide.Position)))
			throw new InvalidDataException (Translations.GetString ("The Pinta project guides are invalid."));
		if (manifest.Selection is null || manifest.Selection.HandleBounds is null || manifest.Selection.Polygons is null)
			throw new InvalidDataException (Translations.GetString ("The Pinta project selection is incomplete."));
		if (!IsFinite (manifest.Selection.HandleBounds) || manifest.Layers is null || manifest.Layers.Count == 0)
			throw new InvalidDataException (Translations.GetString ("The Pinta project content is invalid."));

		HashSet<string> ids = [];
		ValidateLayers (manifest.Layers, root, ids);
		if (manifest.ResourceRoot is null && ContainsReferencedLayer (manifest.Layers))
			throw new InvalidDataException (Translations.GetString ("The Pinta project contains referenced layers but no resource root."));
		if (manifest.SelectedLayerId is not null && !ids.Contains (manifest.SelectedLayerId))
			throw new InvalidDataException (Translations.GetString ("The selected Pinta project layer does not exist."));
	}

	private static void ValidateLayers (
		IReadOnlyList<PintaDocumentLayerNode> nodes,
		string root,
		HashSet<string> ids)
	{
		foreach (PintaDocumentLayerNode node in nodes) {
			if (node is null || string.IsNullOrWhiteSpace (node.Id) || !ids.Add (node.Id)
				|| node.Name is null || !double.IsFinite (node.Opacity) || node.Opacity < 0 || node.Opacity > 1
				|| !Enum.TryParse<BlendMode> (node.BlendMode, ignoreCase: false, out _)
				|| node.Transform is null || !IsFinite (node.Transform)
				|| node.Metadata is null || node.Metadata.Any (item => string.IsNullOrWhiteSpace (item.Key) || item.Value is null)
				|| node.Children is null)
				throw new InvalidDataException (Translations.GetString ("The Pinta project contains an invalid layer."));

			if (node.Kind is not ("layer" or "group" or "video-editing" or "spritesheet" or "single-direction-animation")
				|| node.Storage is not ("embedded" or "reference"))
				throw new InvalidDataException (Translations.GetString ("The Pinta project contains an invalid layer type."));

			if (node.Kind is "group" or "video-editing") {
				ValidateGroup (node);
				if (node.Kind == "video-editing" && node.Video is string video
					&& (!IsValidManagedPath (video)
						|| !video.StartsWith ($"{resources_directory}/videos/{node.Id}/", StringComparison.Ordinal)
						|| !File.Exists (ResolveManagedResourcePath (root, video))))
					throw new InvalidDataException (Translations.GetString ("The Pinta project video resource is invalid."));
			} else if (node.Kind == "layer")
				ValidateRegularLayer (node, root);
			else if (node.Kind == "spritesheet")
				ValidateSpriteSheetNode (node, root);
			else
				ValidateSingleDirectionAnimationNode (node, root);

			ValidateLayers (node.Children, root, ids);
		}
	}

	private static void ValidateGroup (PintaDocumentLayerNode node)
	{
		if (node.Storage != "embedded" || node.Surface is not null || node.SurfaceHash is not null || node.ReferencePath is not null)
			throw new InvalidDataException (Translations.GetString ("The Pinta project group layer is invalid."));
	}

	private static void ValidateRegularLayer (PintaDocumentLayerNode node, string root)
	{
		if (node.Storage == "embedded") {
			if (node.Surface is null || node.SurfaceHash is null || !IsValidResourcePath (node.Surface)
				|| !node.Surface.StartsWith ($"{resources_directory}/layers/", StringComparison.Ordinal)
				|| !File.Exists (ResolveResourcePath (root, node.Surface))
				|| node.SurfaceWidth is not > 0 || node.SurfaceHeight is not > 0)
				throw new InvalidDataException (Translations.GetString ("The Pinta project layer resource is invalid."));
		} else if (node.Surface is not null || node.SurfaceHash is not null || !IsValidReferencePath (node.ReferencePath))
			throw new InvalidDataException (Translations.GetString ("The Pinta project reference layer is invalid."));
	}

	private static void ValidateSpriteSheetNode (PintaDocumentLayerNode node, string root)
	{
		if (node.Storage != "embedded" || node.Children.Count > 0 || node.Surface is not null
			|| node.SpriteSheetAnimations is null || node.SpriteSheetAnimations.Count == 0)
			throw new InvalidDataException (Translations.GetString ("The Pinta project spritesheet layer is invalid."));

		HashSet<(string Action, string Direction, int Frame)> keys = [];
		foreach (PintaDocumentSpriteSheetAnimation animation in node.SpriteSheetAnimations) {
			if (animation is null || string.IsNullOrWhiteSpace (animation.ActionId)
				|| animation.CanvasWidth <= 0 || animation.CanvasHeight <= 0 || animation.Directions is null || animation.Directions.Count == 0)
				throw new InvalidDataException (Translations.GetString ("The Pinta project spritesheet animation is invalid."));

			foreach (PintaDocumentSpriteSheetDirection direction in animation.Directions) {
				if (direction is null || string.IsNullOrWhiteSpace (direction.DirectionId) || direction.Frames is null || direction.Frames.Count == 0)
					throw new InvalidDataException (Translations.GetString ("The Pinta project spritesheet direction is invalid."));
				foreach (PintaDocumentSpriteSheetFrame frame in direction.Frames) {
					ValidateFrame (frame, node.Id, root, "spritesheets");
					if (!keys.Add ((animation.ActionId, direction.DirectionId, frame.FrameIndex)))
						throw new InvalidDataException (Translations.GetString ("The Pinta project contains duplicate animation frames."));
				}
			}
		}
	}

	private static void ValidateSingleDirectionAnimationNode (PintaDocumentLayerNode node, string root)
	{
		if (node.Storage != "embedded" || node.Children.Count > 0 || node.Surface is not null
			|| string.IsNullOrWhiteSpace (node.SingleDirectionId)
			|| node.SingleDirectionAnimations is null || node.SingleDirectionAnimations.Count == 0)
			throw new InvalidDataException (Translations.GetString ("The Pinta project single-direction animation layer is invalid."));

		HashSet<(string Action, int Frame)> keys = [];
		foreach (PintaDocumentSingleDirectionAnimation animation in node.SingleDirectionAnimations) {
			if (animation is null || string.IsNullOrWhiteSpace (animation.ActionId)
				|| animation.CanvasWidth <= 0 || animation.CanvasHeight <= 0 || animation.Frames is null || animation.Frames.Count == 0)
				throw new InvalidDataException (Translations.GetString ("The Pinta project single-direction animation is invalid."));
			foreach (PintaDocumentSingleDirectionFrame frame in animation.Frames) {
				ValidateFrame (frame, node.Id, root, "single-direction-animations");
				if (!keys.Add ((animation.ActionId, frame.FrameIndex)))
					throw new InvalidDataException (Translations.GetString ("The Pinta project contains duplicate animation frames."));
			}
		}
	}

	private static void ValidateFrame<T> (T frame, string layerId, string root, string directory)
		where T : class
	{
		(string? path, string? hash, int width, int height) = frame switch {
			PintaDocumentSpriteSheetFrame sprite => (sprite.Surface, sprite.SurfaceHash, sprite.Width, sprite.Height),
			PintaDocumentSingleDirectionFrame single => (single.Surface, single.SurfaceHash, single.Width, single.Height),
			_ => (null, null, 0, 0),
		};
		if (path is null || hash is null || width <= 0 || height <= 0
			|| !IsValidResourcePath (path)
			|| !path.StartsWith ($"{resources_directory}/{directory}/{layerId}/", StringComparison.Ordinal)
			|| !File.Exists (ResolveResourcePath (root, path)))
			throw new InvalidDataException (Translations.GetString ("The Pinta project animation resource is invalid."));
	}

	private static bool ContainsReferencedLayer (IReadOnlyList<PintaDocumentLayerNode> nodes)
		=> nodes.Any (node => node.Storage == "reference" || ContainsReferencedLayer (node.Children));

	private static bool IsValidResourcePath (string path)
		=> IsValidManagedPath (path)
		&& path.EndsWith (".png", StringComparison.OrdinalIgnoreCase);

	private static bool IsValidManagedPath (string path)
		=> !string.IsNullOrWhiteSpace (path)
		&& !Path.IsPathRooted (path)
		&& !path.Contains ('\\')
		&& path.Split ('/').All (part => !string.IsNullOrEmpty (part) && part is not ("." or ".."))
		&& path.StartsWith ($"{resources_directory}/", StringComparison.Ordinal);

	private static bool IsValidReferencePath (string? path)
		=> path is not null
		&& !string.IsNullOrWhiteSpace (path)
		&& !Path.IsPathRooted (path)
		&& !path.Contains ('\\')
		&& path.Split ('/').All (part => !string.IsNullOrEmpty (part) && part is not ("." or ".."));

	private static void DeleteUnreferencedResources (
		string root,
		PintaDocumentManifest? previous,
		PintaDocumentManifest current)
	{
		if (previous is null)
			return;

		HashSet<string> currentPaths = GetResourcePaths (current).ToHashSet (StringComparer.Ordinal);
		foreach (string oldPath in GetResourcePaths (previous)) {
			if (currentPaths.Contains (oldPath))
				continue;

			string path = ResolveManagedResourcePath (root, oldPath);
			TryDeleteFile (path);
		}
	}

	private static void DeleteCreatedResources (string root, IEnumerable<string> paths)
	{
		foreach (string relativePath in paths) {
			string path = ResolveManagedResourcePath (root, relativePath);
			TryDeleteFile (path);
		}
	}

	private static void TryDeleteFile (string path)
	{
		try {
			if (File.Exists (path))
				File.Delete (path);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private static void TryDeleteDirectory (string path)
	{
		try {
			if (Directory.Exists (path))
				Directory.Delete (path, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private static IEnumerable<string> GetResourcePaths (PintaDocumentManifest manifest)
		=> manifest.Layers.SelectMany (GetResourcePaths);

	private static IEnumerable<string> GetResourcePaths (PintaDocumentLayerNode node)
	{
		if (node.Surface is not null)
			yield return node.Surface;
		if (node.Video is not null)
			yield return node.Video;
		foreach (PintaDocumentSpriteSheetFrame frame in node.SpriteSheetAnimations.SelectMany (
			animation => animation.Directions.SelectMany (direction => direction.Frames)))
			yield return frame.Surface;
		foreach (PintaDocumentSingleDirectionFrame frame in node.SingleDirectionAnimations.SelectMany (animation => animation.Frames))
			yield return frame.Surface;
		foreach (PintaDocumentLayerNode child in node.Children)
			foreach (string path in GetResourcePaths (child))
				yield return path;
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
