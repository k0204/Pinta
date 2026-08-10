using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GdkPixbuf;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed partial class AtlasPackingWindow
{
	private Gtk.Grid sourceGrid = null!;
	private Gtk.Stack previewStack = null!;
	private Gtk.Label atlasPreviewPageLabel = null!;
	private Gtk.Button previousPageButton = null!;
	private Gtk.Button nextPageButton = null!;
	private readonly List<string> atlasPreviewPaths = [];
	private int atlasPreviewPageIndex;

	private Gtk.Box CreateSourcePanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		panel.SetAllMargins (10);

		Gtk.Box header = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		Gtk.Label title = Gtk.Label.New (Translations.GetString ("Input frames"));
		title.Halign = Gtk.Align.Start;
		title.Hexpand = true;
		title.AddCssClass (AdwaitaStyles.Heading);
		header.Append (title);
		Gtk.Button add = Gtk.Button.NewFromIconName ("list-add-symbolic");
		add.SetTooltipText (Translations.GetString ("Add image frames..."));
		add.OnClicked += HandleAddFilesClicked;
		header.Append (add);
		Gtk.Button clear = Gtk.Button.NewFromIconName ("edit-clear-symbolic");
		clear.SetTooltipText (Translations.GetString ("Clear image frames"));
		clear.OnClicked += (_, _) => SetInputPaths (Array.Empty<string> ());
		header.Append (clear);
		panel.Append (header);

		fileSummary = Gtk.Label.New (string.Empty);
		fileSummary.Halign = Gtk.Align.Start;
		fileSummary.AddCssClass (AdwaitaStyles.DimLabel);
		panel.Append (fileSummary);

		sourceGrid = Gtk.Grid.New ();
		sourceGrid.ColumnSpacing = 8;
		sourceGrid.RowSpacing = 8;
		sourceGrid.SetAllMargins (4);
		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.SetPolicy (Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
		scroll.SetChild (sourceGrid);
		scroll.Hexpand = true;
		scroll.Vexpand = true;
		panel.Append (scroll);
		return panel;
	}

	private Gtk.Box CreatePreviewPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		panel.SetAllMargins (10);

		Gtk.Box header = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		Gtk.Label title = Gtk.Label.New (Translations.GetString ("Atlas preview"));
		title.Halign = Gtk.Align.Start;
		title.Hexpand = true;
		title.AddCssClass (AdwaitaStyles.Heading);
		header.Append (title);
		previousPageButton = Gtk.Button.NewFromIconName (StandardIcons.GoPrevious);
		previousPageButton.SetTooltipText (Translations.GetString ("Previous atlas page"));
		previousPageButton.OnClicked += HandlePreviousPageClicked;
		header.Append (previousPageButton);
		atlasPreviewPageLabel = Gtk.Label.New (Translations.GetString ("No atlas preview"));
		atlasPreviewPageLabel.Halign = Gtk.Align.Center;
		header.Append (atlasPreviewPageLabel);
		nextPageButton = Gtk.Button.NewFromIconName (StandardIcons.GoNext);
		nextPageButton.SetTooltipText (Translations.GetString ("Next atlas page"));
		nextPageButton.OnClicked += HandleNextPageClicked;
		header.Append (nextPageButton);
		panel.Append (header);

		previewStack = Gtk.Stack.New ();
		previewStack.Hexpand = true;
		previewStack.Vexpand = true;
		previewStack.AddNamed (
			Gtk.Label.New (Translations.GetString ("Build an atlas to preview the result.")),
			"empty");
		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.SetPolicy (Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
		scroll.SetChild (previewStack);
		scroll.Hexpand = true;
		scroll.Vexpand = true;
		panel.Append (scroll);
		ShowAtlasPage ();
		return panel;
	}

	internal void SetInputPaths (IReadOnlyList<string> inputPaths)
	{
		paths.Clear ();
		foreach (string path in inputPaths)
			if (!string.IsNullOrWhiteSpace (path)
				&& !paths.Any (existing => string.Equals (existing, path, StringComparison.OrdinalIgnoreCase)))
				paths.Add (path);

		RebuildSourcePreview ();
		UpdateAtlasPreview (Array.Empty<string> ());
		RequestAtlasPreview ();
		UpdateState ();
	}

	private async void RequestAtlasPreview ()
	{
		if (disposed)
			return;

		CancelAtlasPreviewBuild ();
		if (paths.Count == 0) {
			UpdateAtlasPreview (Array.Empty<string> ());
			progress.Hide ();
			return;
		}

		preview_cts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		CancellationToken cancellationToken = preview_cts.Token;
		int previewVersion = preview_version;
		string[] inputPaths = paths.ToArray ();
		int scalePercent = (int) scaleSpinner.GetValue ();
		int minWidth = (int) minWidthSpinner.GetValue ();
		int maxWidth = (int) maxWidthSpinner.GetValue ();
		int minHeight = (int) minHeightSpinner.GetValue ();
		int maxHeight = (int) maxHeightSpinner.GetValue ();
		int spacing = (int) spacingSpinner.GetValue ();
		bool trimTransparent = trimToggle.Active;
		string previewDirectory = Path.Combine (
			Path.GetTempPath (),
			"Pinta",
			$"atlas-preview-{Guid.NewGuid ():N}");
		progress.Fraction = 0;
		progress.Text = Translations.GetString ("Building atlas: {0} frames", inputPaths.Length);
		progress.Show ();

		try {
			AtlasBuildResult result = await Task.Run (() => VideoAtlasBuilder.Build (
				inputPaths,
				previewDirectory,
				"preview",
				scalePercent,
				minWidth,
				maxWidth,
				minHeight,
				maxHeight,
				spacing,
				trimTransparent,
				cancellationToken,
				drawPreviewBorders: true), cancellationToken);
			if (!IsCurrentPreview (previewVersion, cancellationToken))
				return;

			UpdateAtlasPreview (result.ImagePaths);
			progress.Fraction = 1;
			progress.Text = Translations.GetString ("Ready");
			progress.Hide ();
		} catch (OperationCanceledException) {
		} catch (VideoFrameExportException ex) {
			if (IsCurrentPreview (previewVersion, cancellationToken)) {
				progress.Fraction = 0;
				progress.Text = ex.Message;
			}
		} catch (Exception ex) {
			if (IsCurrentPreview (previewVersion, cancellationToken)) {
				progress.Fraction = 0;
				progress.Text = Translations.GetString ("Atlas build failed.");
			}
			Console.Error.WriteLine (ex);
		} finally {
			DeletePreviewDirectory (previewDirectory);
			if (IsCurrentPreview (previewVersion, cancellationToken))
				UpdateState ();
		}
	}

	private void CancelAtlasPreviewBuild ()
	{
		preview_version++;
		preview_cts?.Cancel ();
		preview_cts?.Dispose ();
		preview_cts = null;
	}

	private bool IsCurrentPreview (int version, CancellationToken cancellationToken)
		=> !disposed && version == preview_version && !cancellationToken.IsCancellationRequested;

	private static void DeletePreviewDirectory (string directory)
	{
		try {
			if (Directory.Exists (directory))
				Directory.Delete (directory, recursive: true);
		} catch (Exception ex) {
			Console.Error.WriteLine ($"Atlas preview cleanup failed: {ex}");
		}
	}

	private async void HandleAddFilesClicked (object sender, EventArgs args)
	{
		using Gtk.FileDialog dialog = Gtk.FileDialog.New ();
		dialog.SetTitle (Translations.GetString ("Add image frames"));
		using Gtk.FileFilter filter = Gtk.FileFilter.New ();
		filter.Name = Translations.GetString ("Image files");
		foreach (string pattern in new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" })
			filter.AddPattern (pattern);
		using Gio.ListStore filters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		filters.Append (filter);
		dialog.SetFilters (filters);

		IReadOnlyList<Gio.File>? files = await dialog.OpenFilesAsync (this);
		if (files is null)
			return;
		List<string> updatedPaths = [.. paths];
		foreach (Gio.File file in files) {
			string? path = file.GetPath ();
			if (!string.IsNullOrWhiteSpace (path)
				&& !updatedPaths.Any (existing => string.Equals (existing, path, StringComparison.OrdinalIgnoreCase)))
				updatedPaths.Add (path);
		}
		SetInputPaths (updatedPaths);
	}

	private void RebuildSourcePreview ()
	{
		while (sourceGrid.GetFirstChild () is Gtk.Widget child)
			sourceGrid.Remove (child);

		for (int index = 0; index < paths.Count; index++)
			sourceGrid.Attach (CreateSourceCard (paths[index], index), index % 2, index / 2, 1, 1);
	}

	private Gtk.Box CreateSourceCard (string path, int index)
	{
		Gtk.Box card = Gtk.Box.New (Gtk.Orientation.Vertical, 3);
		card.SetSizeRequest (126, 124);
		card.SetTooltipText (path);

		Gtk.Picture picture = Gtk.Picture.New ();
		picture.ContentFit = Gtk.ContentFit.Contain;
		picture.SetSizeRequest (120, 88);
		try {
			using Pixbuf source = Pixbuf.NewFromFile (path)!;
			using Pixbuf thumbnail = CreateThumbnail (source);
			picture.SetPaintable (Gdk.Texture.NewForPixbuf (thumbnail));
		} catch (Exception ex) {
			Console.Error.WriteLine (ex);
		}
		card.Append (picture);

		Gtk.Label name = Gtk.Label.New (Translations.GetString ("F{0}: {1}", index + 1, Path.GetFileName (path)));
		name.Ellipsize = Pango.EllipsizeMode.End;
		name.Halign = Gtk.Align.Start;
		card.Append (name);
		return card;
	}

	private static Pixbuf CreateThumbnail (Pixbuf source)
	{
		int width = Math.Min (120, source.Width);
		int height = Math.Max (1, (int) Math.Round (source.Height * (double) width / source.Width));
		return source.ScaleSimple (width, height, InterpType.Bilinear)!;
	}

	private void UpdateAtlasPreview (IReadOnlyList<string> imagePaths)
	{
		atlasPreviewPaths.Clear ();
		atlasPreviewPaths.AddRange (imagePaths);
		atlasPreviewPageIndex = 0;
		while (previewStack.GetFirstChild () is Gtk.Widget child)
			previewStack.Remove (child);

		if (atlasPreviewPaths.Count == 0) {
			previewStack.AddNamed (
				Gtk.Label.New (Translations.GetString ("Build an atlas to preview the result.")),
				"empty");
			ShowAtlasPage ();
			return;
		}

		for (int index = 0; index < atlasPreviewPaths.Count; index++) {
			Gtk.Picture picture = Gtk.Picture.New ();
			picture.ContentFit = Gtk.ContentFit.Contain;
			picture.SetSizeRequest (640, 520);
			try {
				using Pixbuf page = Pixbuf.NewFromFile (atlasPreviewPaths[index])!;
				picture.SetPaintable (Gdk.Texture.NewForPixbuf (page));
			} catch (Exception ex) {
				Console.Error.WriteLine (ex);
			}
			previewStack.AddNamed (picture, $"page-{index}");
		}
		ShowAtlasPage ();
	}

	private void ShowAtlasPage (object? sender = null, EventArgs? args = null)
	{
		if (atlasPreviewPaths.Count == 0) {
			atlasPreviewPageLabel.SetText (Translations.GetString ("No atlas preview"));
			previousPageButton.Sensitive = false;
			nextPageButton.Sensitive = false;
			previewStack.SetVisibleChildName ("empty");
			return;
		}

		atlasPreviewPageIndex = Math.Clamp (atlasPreviewPageIndex, 0, atlasPreviewPaths.Count - 1);
		previewStack.SetVisibleChildName ($"page-{atlasPreviewPageIndex}");
		atlasPreviewPageLabel.SetText (Translations.GetString (
			"Page {0} of {1}",
			atlasPreviewPageIndex + 1,
			atlasPreviewPaths.Count));
		previousPageButton.Sensitive = atlasPreviewPageIndex > 0;
		nextPageButton.Sensitive = atlasPreviewPageIndex < atlasPreviewPaths.Count - 1;
	}

	private void HandlePreviousPageClicked (object sender, EventArgs args)
	{
		atlasPreviewPageIndex--;
		ShowAtlasPage ();
	}

	private void HandleNextPageClicked (object sender, EventArgs args)
	{
		atlasPreviewPageIndex++;
		ShowAtlasPage ();
	}
}
