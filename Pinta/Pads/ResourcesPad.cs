using System;
using System.IO;
using System.Linq;
using Pinta.Core;
using Pinta.Docking;

namespace Pinta;

internal sealed class ResourcesPad : IDockPad
{
	private Gtk.Box entries = null!;
	private Gtk.Label path_label = null!;
	private string? current_directory;

	public void Initialize (Dock workspace)
	{
		Gtk.Button chooseRoot = Gtk.Button.NewFromIconName (Resources.StandardIcons.Folder);
		chooseRoot.TooltipText = Translations.GetString ("Choose Resource Root");
		chooseRoot.OnClicked += async (_, _) => await ChooseRootAsync ();

		Gtk.Button goUp = Gtk.Button.NewFromIconName (Resources.StandardIcons.GoPrevious);
		goUp.TooltipText = Translations.GetString ("Parent Folder");
		goUp.OnClicked += (_, _) => NavigateUp ();

		path_label = Gtk.Label.New (Translations.GetString ("No resource root selected"));
		path_label.Ellipsize = Pango.EllipsizeMode.Middle;
		path_label.Hexpand = true;
		path_label.Halign = Gtk.Align.Start;

		entries = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.Child = entries;
		scroll.HscrollbarPolicy = Gtk.PolicyType.Never;

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
		content.Append (path_label);
		content.Append (scroll);

		DockItem item = DockItem.New (content, "Resources", Resources.StandardIcons.Folder);
		item.Label = Translations.GetString ("Resources");
		Gtk.Box toolbar = item.AddToolBar ();
		toolbar.Append (chooseRoot);
		toolbar.Append (goUp);
		workspace.AddItem (item, DockPlacement.Right);

		PintaCore.Workspace.ActiveDocumentChanged += (_, _) => RefreshFromDocument ();
		PintaCore.Workspace.LayerTreeChanged += (_, _) => Refresh ();
		RefreshFromDocument ();
	}

	private async System.Threading.Tasks.Task ChooseRootAsync ()
	{
		if (!PintaCore.Workspace.HasOpenDocuments)
			return;

		using Gtk.FileDialog dialog = Gtk.FileDialog.New ();
		dialog.SetTitle (Translations.GetString ("Choose Resource Root"));
		Gio.File? root = await dialog.SelectFolderAsync (PintaCore.Chrome.MainWindow);
		if (root is null || root.GetPath () is not string rootPath)
			return;

		PintaCore.Workspace.ActiveDocument.SetResourceRoot (root);
		PintaCore.Workspace.ActiveDocument.History.SetDirty ();
		current_directory = rootPath;
		Refresh ();
	}

	private void RefreshFromDocument ()
	{
		if (!PintaCore.Workspace.HasOpenDocuments || PintaCore.Workspace.ActiveDocument.ResourceRootUri is not string uri) {
			current_directory = null;
			Refresh ();
			return;
		}

		current_directory = Gio.FileHelper.NewForUri (uri).GetPath ();
		Refresh ();
	}

	private void NavigateUp ()
	{
		if (current_directory is null || !PintaCore.Workspace.HasOpenDocuments || PintaCore.Workspace.ActiveDocument.ResourceRootUri is not string uri)
			return;

		string? root = Gio.FileHelper.NewForUri (uri).GetPath ();
		DirectoryInfo? parent = Directory.GetParent (current_directory);
		if (root is null || parent is null || !parent.FullName.StartsWith (root, StringComparison.OrdinalIgnoreCase))
			return;

		current_directory = parent.FullName;
		Refresh ();
	}

	private void Refresh ()
	{
		entries.RemoveAll ();
		if (current_directory is null) {
			path_label.SetText (Translations.GetString ("No resource root selected"));
			return;
		}
		if (!Directory.Exists (current_directory)) {
			path_label.SetText (Translations.GetString ("Resource folder is unavailable"));
			return;
		}

		path_label.SetText (current_directory);
		try {
			foreach (string directory in Directory.EnumerateDirectories (current_directory).Order ())
				entries.Append (CreateEntry (directory, isDirectory: true));

			foreach (string file in Directory.EnumerateFiles (current_directory).Where (IsSupportedImage).Order ())
				entries.Append (CreateEntry (file, isDirectory: false));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			entries.RemoveAll ();
			path_label.SetText (Translations.GetString ("Resource folder is unavailable"));
		}
	}

	private static bool IsSupportedImage (string path)
		=> PintaCore.ImageFormats.GetImporterByFile (path) is not null;

	private Gtk.Button CreateEntry (string path, bool isDirectory)
	{
		Gtk.Button button = Gtk.Button.New ();
		button.Halign = Gtk.Align.Fill;
		button.Label = Path.GetFileName (path);
		button.IconName = isDirectory ? Resources.StandardIcons.Folder : Resources.StandardIcons.ImageGeneric;
		button.TooltipText = path;
		if (!isDirectory) {
			Gtk.DragSource dragSource = Gtk.DragSource.New ();
			dragSource.Actions = Gdk.DragAction.Copy;
			dragSource.OnPrepare += (_, _) => CreateFileListProvider (path);
			button.AddController (dragSource);
		}
		button.OnClicked += (_, _) => {
			if (isDirectory) {
				current_directory = path;
				Refresh ();
			} else {
				AddReferenceLayer (path);
			}
		};
		return button;
	}

	private static Gdk.ContentProvider CreateFileListProvider (string path)
	{
		using Gdk.FileList files = Gdk.FileList.NewFromArray ([Gio.FileHelper.NewForPath (path)], 1);
		using GObject.Value value = new (Gdk.FileList.GetGType ());
		value.SetBoxed (files.Handle.DangerousGetHandle ());
		return Gdk.ContentProvider.NewForValue (value);
	}

	private static void AddReferenceLayer (string path)
	{
		Document document = PintaCore.Workspace.ActiveDocument;
		Gio.File file = Gio.FileHelper.NewForPath (path);
		if (!document.TryGetResourceRelativePath (file, out string relativePath))
			return;

		PintaCore.Tools.Commit ();
		Size imageSize = document.ImageSize;
		UserLayer layer = document.Layers.AddReferenceLayer (Path.GetFileName (path), relativePath, new PointD (imageSize.Width / 2.0, imageSize.Height / 2.0));
		document.History.PushNewItem (new AddLayerHistoryItem (Resources.Icons.LayerImport, Translations.GetString ("Import Referenced Image"), layer, document.Layers.GetPosition (layer)));
		document.Workspace.Invalidate ();
	}
}
