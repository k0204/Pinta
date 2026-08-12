//
// Document.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2010 Jonathan Pobst
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

// The differentiation between Document and DocumentWorkspace is
// somewhat arbitrary.  In general:
// Document - Data about the image itself
// Workspace - Data about Pinta's state for the image
public sealed class Document
{
	private string display_name = string.Empty;
	private Size image_size;
	private Gio.File? file = null;
	private FileSystemWatcher? resource_watcher;
	private int reference_reload_scheduled;
	private int resource_watcher_retry_scheduled;
	private bool is_closed;
	private readonly SemaphoreSlim save_gate = new (1, 1);

	private bool is_dirty;

	private DocumentSelection selection = null!; // NRT - Set by constructor via Selection property
	public DocumentSelection Selection {
		get => selection;
		set {
			selection = value;

			// Listen for any changes to this selection.
			selection.SelectionModified += (_, _) => OnSelectionChanged ();

			// Notify listeners that our selection has been modified.
			OnSelectionChanged ();
		}
	}

	public DocumentSelection PreviousSelection { get; set; }

	private static int name_counter = 1;

	public Document (ActionManager actions, ToolManager tools, WorkspaceManager workspace, Size size)
		: this (actions, tools, workspace, size, null, null)
	{ }

	private readonly ActionManager actions;
	private readonly ToolManager tools;
	private readonly WorkspaceManager workspace;
	public Document (
		ActionManager actions,
		ToolManager tools,
		WorkspaceManager workspace,
		Size size,
		Gio.File? file,
		string? fileType)
	{
		// --- Mandatory fields

		PreviousSelection = new ();
		Selection = new DocumentSelection ();

		Layers = new DocumentLayers (tools, this);
		Workspace = new DocumentWorkspace (actions, this);
                Guides = new DocumentGuides (this);
                Guides.Changed += (_, _) => Workspace.Invalidate ();
		IsDirty = false;
		HasBeenSavedInSession = false;
		ImageSize = size;

		this.actions = actions;
		this.tools = tools;
		this.workspace = workspace;

		// --- Post-initialization

		if (file is not null) {

			if (string.IsNullOrEmpty (fileType))
				throw new ArgumentNullException ($"nameof{fileType} must contain value.");

			File = file;
			FileType = fileType;
		} else
			DisplayName = Translations.GetString ("Unsaved Image {0}", name_counter++);

		Workspace.ViewSize = size;

		ResetSelectionPaths ();
	}

	/// <summary>
	/// Just the file name, like "dog.jpg".
	/// If HasFile is false, this may be something like "Unsaved image 1".
	/// </summary>
	public string DisplayName {
		get => display_name;
		set {
			display_name = value;
			OnRenamed ();
		}
	}

	/// <summary>
	/// File type of the image, if File is not null. This is an extensions, e.g. "png" or "ora".
	/// This cannot necessarily be derived from the file name's extension (e.g. when opening
	/// a file with no extension or an incorrect extension) so it's easiest to record
	/// the file type used when opening the image.
	/// </summary>
	public string? FileType { get; set; }

	/// <summary>
	/// Identifier for the file location on disk.
	/// </summary>
	public Gio.File? File {
		get => file;
		set {
			file = value;
			DisplayName = file?.GetDisplayName () ?? string.Empty;
		}
	}

	/// <summary>
	/// Whether the document is associated with a file on disk.
	/// </summary>
	public bool HasFile => file is not null;

	/// Whether or not the document has been saved to the file that it is currently associated with in the
	/// current session. This should be false if the Document has not yet been saved, if it was just loaded into
	/// Pinta from a file, or if the user just clicked Save As.
	public bool HasBeenSavedInSession { get; set; }

	public DocumentHistory History => Workspace.History;

	public Size ImageSize {
		get => image_size;
		set {
			image_size = value;
			Layers.UpdateAnimationOutputTransforms ();
		}
	}

	/// <summary>
	/// Absolute URI of the directory used to resolve referenced layer paths.
	/// </summary>
	public string? ResourceRootUri { get; private set; }

	public bool IsDirty {
		get => is_dirty;
		set {
			if (is_dirty == value) return;
			is_dirty = value;
			OnIsDirtyChanged ();
		}
	}

	public DocumentLayers Layers { get; }

        public DocumentGuides Guides { get; }

	public DocumentWorkspace Workspace { get; }

	public void SetResourceRoot (Gio.File? root)
	{
		resource_watcher?.Dispose ();
		resource_watcher = null;
		ResourceRootUri = root?.GetUri ();

		ReloadReferencedLayers ();
	}

	private bool TryStartResourceWatcher ()
	{
		if (ResourceRootUri is null)
			return false;

		try {
			string? path = Gio.FileHelper.NewForUri (ResourceRootUri).GetPath ();
			if (path is null || !Directory.Exists (path)) {
				resource_watcher?.Dispose ();
				resource_watcher = null;
				return false;
			}

			if (resource_watcher is not null)
				return true;

			resource_watcher = new FileSystemWatcher (path) {
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			resource_watcher.Changed += HandleResourceChanged;
			resource_watcher.Created += HandleResourceChanged;
			resource_watcher.Deleted += HandleResourceChanged;
			resource_watcher.Renamed += HandleResourceChanged;
			resource_watcher.Error += HandleResourceWatcherError;
			resource_watcher.EnableRaisingEvents = true;
			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or GLib.GException) {
			resource_watcher?.Dispose ();
			resource_watcher = null;
			return false;
		}
	}

	public bool TryGetResourceRelativePath (Gio.File file, out string relativePath)
	{
		relativePath = string.Empty;
		if (ResourceRootUri is null)
			return false;

		Gio.File root = Gio.FileHelper.NewForUri (ResourceRootUri);
		string? result = root.GetRelativePath (file)?.Replace ('\\', '/');
		if (string.IsNullOrEmpty (result) || result == ".." || result.StartsWith ("../", StringComparison.Ordinal))
			return false;

		relativePath = result;
		return true;
	}

	public bool LoadReferencedLayer (UserLayer layer)
	{
		if (ResourceRootUri is null || layer.ReferencePath is null) {
			SetReferenceMissing (layer);
			return false;
		}

		try {
			Gio.File root = Gio.FileHelper.NewForUri (ResourceRootUri);
			string? rootPath = root.GetPath ();
			if (rootPath is null) {
				SetReferenceMissing (layer);
				return false;
			}

			string sourcePath = System.IO.Path.Combine (rootPath, layer.ReferencePath.Replace ('/', System.IO.Path.DirectorySeparatorChar));
			using GdkPixbuf.Pixbuf? pixbuf = GdkPixbuf.Pixbuf.NewFromFile (sourcePath);
			if (pixbuf is null) {
				SetReferenceMissing (layer);
				return false;
			}

			ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, pixbuf.Width, pixbuf.Height);
			using Context context = new (surface);
			context.DrawPixbuf (pixbuf, PointD.Zero);
			ImageSurface oldSurface = layer.Surface;
			layer.Surface = surface;
			oldSurface.Dispose ();
			layer.ReferenceSize = new Size (pixbuf.Width, pixbuf.Height);
			layer.ReferenceMissing = false;
			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or GLib.GException) {
			SetReferenceMissing (layer);
			return false;
		}
	}

	private void SetReferenceMissing (UserLayer layer)
	{
		layer.ReferenceMissing = true;
		layer.Surface.Clear ();
		Size size = layer.ReferenceSize.IsEmpty
			? new Size (Math.Min (128, ImageSize.Width), Math.Min (128, ImageSize.Height))
			: layer.ReferenceSize;
		layer.ReferenceSize = size;

		if (layer.Surface.Width != size.Width || layer.Surface.Height != size.Height) {
			layer.Surface.Dispose ();
			layer.Surface = CairoExtensions.CreateImageSurface (Format.Argb32, size.Width, size.Height);
		} else {
			layer.Surface.Clear ();
		}

		using Context context = new (layer.Surface);
		context.SetSourceColor (new Color (0.85, 0.15, 0.15, 0.8));
		context.LineWidth = 3;
		context.Rectangle (1.5, 1.5, Math.Max (0, size.Width - 3), Math.Max (0, size.Height - 3));
		context.MoveTo (2, 2);
		context.LineTo (size.Width - 2, size.Height - 2);
		context.MoveTo (size.Width - 2, 2);
		context.LineTo (2, size.Height - 2);
		context.Stroke ();
	}

	public void ReloadReferencedLayers ()
	{
		if (!TryStartResourceWatcher ())
			ScheduleResourceWatcherRetry ();

		foreach (UserLayer layer in Layers.AllLayers) {
			if (layer.IsReference)
				LoadReferencedLayer (layer);
		}

		Workspace.Invalidate ();
		Layers.NotifyLayerTreeChanged ();
	}

	private void HandleResourceChanged (object sender, FileSystemEventArgs e)
		=> ScheduleReferenceReload ();

	private void HandleResourceWatcherError (object sender, ErrorEventArgs e)
		=> ScheduleReferenceReload ();

	private void ScheduleReferenceReload ()
	{
		if (is_closed || Interlocked.Exchange (ref reference_reload_scheduled, 1) != 0)
			return;

		GLib.Functions.TimeoutAdd (GLib.Constants.PRIORITY_DEFAULT, 200, () => {
			Interlocked.Exchange (ref reference_reload_scheduled, 0);
			if (!is_closed)
				ReloadReferencedLayers ();
			return false;
		});
	}

	private void ScheduleResourceWatcherRetry ()
	{
		if (is_closed || ResourceRootUri is null || Interlocked.Exchange (ref resource_watcher_retry_scheduled, 1) != 0)
			return;

		GLib.Functions.TimeoutAdd (GLib.Constants.PRIORITY_DEFAULT, 1000, () => {
			if (is_closed || ResourceRootUri is null) {
				Interlocked.Exchange (ref resource_watcher_retry_scheduled, 0);
				return false;
			}

			if (!TryStartResourceWatcher ())
				return true;

			Interlocked.Exchange (ref resource_watcher_retry_scheduled, 0);
			ReloadReferencedLayers ();
			return false;
		});
	}

	public delegate void LayerCloneEvent ();

	public RectangleI ClampToImageSize (RectangleI r)
	{
		int x = Math.Clamp (r.X, 0, ImageSize.Width);
		int y = Math.Clamp (r.Y, 0, ImageSize.Height);
		int width = Math.Min (r.Width, ImageSize.Width - x);
		int height = Math.Min (r.Height, ImageSize.Height - y);
		return new (x, y, width, height);
	}

	public PointD ClampToImageSize (PointD p)
		=> new (
			Math.Clamp (p.X, 0, ImageSize.Width),
			Math.Clamp (p.Y, 0, ImageSize.Height)
		);

	/// <summary>
	/// Sets File to null while keeping the DisplayName the same.
	/// This will force the user to choose a new filename when saving (i.e. "Save As").
	/// </summary>
	public void ClearFileReference ()
	{
		string name = DisplayName;
		File = null;
		FileType = null;
		DisplayName = name;
	}

	// Clean up any native resources we had
	public void Close ()
	{
		is_closed = true;
		resource_watcher?.Dispose ();
		resource_watcher = null;
		Layers.Close ();
		Workspace.History.Clear ();
	}

	public Context CreateClippedContext ()
	{
		Context g = new (Layers.CurrentUserLayer.Surface);
		Selection.Clip (g);
		return g;
	}

	public Context CreateClippedToolContext ()
	{
		Context g = new (Layers.ToolLayer.Surface);
		Selection.Clip (g);
		return g;
	}

	public void FinishSelection ()
	{
		if (!Layers.CurrentUserLayer.IsEditable)
			return;

		// We don't have an uncommitted layer, abort
		if (!Layers.ShowSelectionLayer)
			return;

		FinishPixelsHistoryItem hist = new ();

		hist.TakeSnapshot ();

		Layer layer = Layers.SelectionLayer;

		using Context g = new (Layers.CurrentUserLayer.Surface);
		selection.Clip (g);
		layer.DrawWithOperator (g, Operator.Source, opacity: 1.0, transform: true);

		Layers.DestroySelectionLayer ();
		Workspace.Invalidate ();

		Workspace.History.PushNewItem (hist);
	}

	// Flip image horizontally
	public void FlipImageHorizontal ()
	{
		foreach (var layer in Layers.AllLayers)
			layer.FlipHorizontal ();

		Workspace.Invalidate ();
	}

	// Flip image vertically
	public void FlipImageVertical ()
	{
		foreach (var layer in Layers.AllLayers)
			layer.FlipVertical ();

		Workspace.Invalidate ();
	}

	/// <summary>
	/// Gets the final pixel color for the given point, taking layers, opacity, and blend modes into account.
	/// </summary>
	public ColorBgra GetComputedPixel (PointI position)
	{
		using ImageSurface dst = CairoExtensions.CreateImageSurface (
			Format.Argb32,
			1,
			1);

		using Context g = new (dst);

		foreach (var layer in Layers.GetLayersToPaint ()) {

			Color color = layer.Surface.GetColorBgra (position).ToCairoColor ();

			g.SetBlendMode (layer.BlendMode);
			g.SetSourceColor (color);

			g.Rectangle (dst.GetBounds ().ToDouble ());
			g.PaintWithAlpha (layer.Opacity);
		}

		return dst.GetColorBgra (PointI.Zero);
	}

	public ImageSurface GetFlattenedImage (bool clip_to_selection = false) => Layers.GetFlattenedImage (clip_to_selection);

	/// <param name="canvasOnly">false for the whole selection, true for the part only on our canvas</param>
	public RectangleI GetSelectedBounds (bool canvasOnly)
	{
		RectangleI bounds = Selection.GetBounds ().ToInt ();

		if (canvasOnly)
			bounds = ClampToImageSize (bounds);

		return bounds;
	}

	public void ResetSelectionPaths ()
	{
		RectangleD rect = new (0, 0, ImageSize.Width, ImageSize.Height);
		Selection.CreateRectangleSelection (rect);
		PreviousSelection.CreateRectangleSelection (rect);
		Selection.Visible = false;
		PreviousSelection.Visible = false;
	}

	/// <summary>
	/// Resizes the canvas.
	/// </summary>
	/// <param name="width">The new width of the canvas.</param>
	/// <param name="height">The new height of the canvas.</param>
	/// <param name="anchor">Direction in which to adjust the canvas</param>
	/// <param name='compoundAction'>
	/// Optionally, the history item for resizing the canvas can be added to
	/// a CompoundHistoryItem if it is part of a larger action (e.g. pasting an image).
	/// </param>
	public void ResizeCanvas (
		Size newSize,
		Anchor anchor,
		CompoundHistoryItem? compoundAction)
	{
		if (ImageSize == newSize)
			return;

		tools.Commit ();

		ResizeHistoryItem hist = new (workspace, ImageSize) {
			Icon = Resources.Icons.ImageResizeCanvas,
			Text = Translations.GetString ("Resize Canvas"),
		};

		hist.StartSnapshotOfImage ();

		double scale = Workspace.Scale;

		ImageSize = newSize;
                Guides.ClampAllToImageBounds ();

		foreach (var layer in Layers.AllLayers)
			if (!Layers.IsAnimationOutputLayer (layer))
				layer.ResizeCanvas (newSize, anchor);

		hist.FinishSnapshotOfImage ();

		if (compoundAction is not null)
			compoundAction.Push (hist);
		else
			Workspace.History.PushNewItem (hist);

		ResetSelectionPaths ();

		Workspace.Scale = scale;
	}

	public void ResizeImage (
		Size newSize,
		ResamplingMode resamplingMode)
	{
		if (ImageSize == newSize)
			return;

		tools.Commit ();

		ResizeHistoryItem hist = new (workspace, ImageSize);

		hist.StartSnapshotOfImage ();

		double scale = Workspace.Scale;

		ImageSize = newSize;
                Guides.ClampAllToImageBounds ();

		foreach (var layer in Layers.AllLayers)
			if (!Layers.IsAnimationOutputLayer (layer))
				layer.Resize (newSize, resamplingMode);

		hist.FinishSnapshotOfImage ();

		Workspace.History.PushNewItem (hist);

		ResetSelectionPaths ();

		Workspace.Scale = scale;
		actions.View.UpdateCanvasScale ();
	}

	// Rotate image 180 degrees (flip H+V)
	public void RotateImage180 ()
	{
		RotateImage (new DegreesAngle (180));
	}

	public void RotateImageCW ()
	{
		RotateImage (new DegreesAngle (90));
	}

	public void RotateImageCCW ()
	{
		RotateImage (new DegreesAngle (-90));
	}

	/// <summary>
	/// Rotates the image by the specified angle (in degrees)
	/// </summary>
	private void RotateImage (DegreesAngle angle)
	{
		Size new_size = Layer.RotateDimensions (ImageSize, angle);

		foreach (var layer in Layers.AllLayers)
			layer.Rotate (angle, ImageSize, new_size);

		ImageSize = new_size;
		Workspace.ViewSize = new_size;
                Guides.ClampAllToImageBounds ();

		actions.View.UpdateCanvasScale ();
		ResetSelectionPaths ();
		Workspace.Invalidate ();
	}

	// Returns true if successful, false if canceled
	public async Task<bool> Save (bool saveAs)
	{
		await save_gate.WaitAsync ();
		try {
			return await actions.File.RaiseSaveDocument (this, saveAs);
		} finally {
			save_gate.Release ();
		}
	}

	/// <summary>
	/// Signal to the TextTool that an ImageSurface was cloned.
	/// </summary>
	public void SignalSurfaceCloned ()
	{
		LayerCloned?.Invoke ();
	}

	private void OnIsDirtyChanged ()
	{
		IsDirtyChanged?.Invoke (this, EventArgs.Empty);
	}

	private void OnRenamed ()
	{
		Renamed?.Invoke (this, EventArgs.Empty);
	}

	private void OnSelectionChanged ()
	{
		SelectionChanged?.Invoke (this, EventArgs.Empty);
	}

	public event EventHandler? IsDirtyChanged;
	public event EventHandler? Renamed;
	public event LayerCloneEvent? LayerCloned;
	public event EventHandler? SelectionChanged;
}
