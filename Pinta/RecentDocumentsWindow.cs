using System;
using System.Collections.Generic;
using Pinta.Core;

namespace Pinta;

internal sealed class RecentDocumentsWindow
{
	private readonly Adw.ApplicationWindow window;
	private readonly Adw.Application application;
	private readonly RecentFileManager recent_files;
	private readonly Func<bool> create_new_document;
	private readonly Func<string, bool> open_document;
	private readonly Gtk.ListBox list = Gtk.ListBox.New ();
	private readonly Gtk.Button new_button;
	private readonly Gtk.Button open_button;
	private readonly Dictionary<Gtk.ListBoxRow, string> row_uris = [];
	private bool continuing_to_main_window;

	public event EventHandler? ContinuedToMainWindow;

	public RecentDocumentsWindow (
		Adw.Application application,
		RecentFileManager recentFiles,
		Func<bool> createNewDocument,
		Func<string, bool> openDocument)
	{
		this.application = application;
		recent_files = recentFiles;
		create_new_document = createNewDocument;
		open_document = openDocument;

		window = Adw.ApplicationWindow.New (application);
		window.Title = Translations.GetString ("Document Open History");
		window.DefaultWidth = 680;
		window.DefaultHeight = 480;
		window.OnCloseRequest += HandleCloseRequest;
#if WINDOWS
		window.OnMap += (_, _) => GLib.Functions.IdleAdd (0, () => {
			WindowsWindowPlacement.Center ();
			return false;
		});
#endif

		new_button = CreateNewButton ();
		new_button.OnClicked += (_, _) => CreateNewDocument ();

		open_button = CreateOpenButton ();
		open_button.AddCssClass (AdwaitaStyles.SuggestedAction);
		open_button.Sensitive = false;
		open_button.OnClicked += (_, _) => OpenSelected ();

		Adw.ToolbarView layout = Adw.ToolbarView.New ();
		layout.AddTopBar (CreateHeader ());
		layout.SetContent (CreateLayout ());
		window.SetContent (layout);
	}

	public void Present () => window.Present ();

	private Gtk.Widget CreateLayout ()
	{
		Gtk.Box root = Gtk.Box.New (Gtk.Orientation.Vertical, 0);

		IReadOnlyList<RecentFileManager.RecentFile> files = recent_files.GetFiles ();
		if (files.Count == 0) {
			Gtk.Label empty = Gtk.Label.New (Translations.GetString ("No recently opened documents."));
			empty.AddCssClass (AdwaitaStyles.DimLabel);
			empty.Hexpand = true;
			empty.Vexpand = true;
			root.Append (empty);
			return root;
		}

		list.SelectionMode = Gtk.SelectionMode.Single;
		list.OnRowSelected += (_, args) => open_button.Sensitive = args.Row is not null;
		list.OnRowActivated += (_, args) => OpenRow (args.Row);
		foreach (RecentFileManager.RecentFile file in files)
			AppendRow (file);
		list.SelectRow (list.GetRowAtIndex (0));

		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
		scroll.VscrollbarPolicy = Gtk.PolicyType.Automatic;
		scroll.SetChild (list);
		scroll.Hexpand = true;
		scroll.Vexpand = true;
		root.Append (scroll);

		Gtk.Label count = Gtk.Label.New (Translations.GetString ("{0} recent documents", files.Count));
		count.Halign = Gtk.Align.Start;
		count.SetAllMargins (12);
		count.AddCssClass (AdwaitaStyles.DimLabel);
		root.Append (count);
		return root;
	}

	private static Gtk.Button CreateOpenButton ()
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		content.Append (Gtk.Image.NewFromIconName (Resources.StandardIcons.DocumentOpen));
		content.Append (Gtk.Label.New (Translations.GetString ("Open")));
		Gtk.Button button = Gtk.Button.New ();
		button.SetChild (content);
		return button;
	}

	private static Gtk.Button CreateNewButton ()
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		content.Append (Gtk.Image.NewFromIconName (Resources.StandardIcons.DocumentNew));
		content.Append (Gtk.Label.New (Translations.GetString ("New")));
		Gtk.Button button = Gtk.Button.New ();
		button.SetChild (content);
		return button;
	}

	private Adw.HeaderBar CreateHeader ()
	{
		Adw.HeaderBar header = Adw.HeaderBar.New ();
		header.SetShowStartTitleButtons (false);
		header.SetShowEndTitleButtons (false);

		Gtk.Label title = Gtk.Label.New (Translations.GetString ("Document Open History"));
		title.AddCssClass (AdwaitaStyles.Heading);
		header.TitleWidget = title;

		Gtk.Button close = Gtk.Button.NewFromIconName (Resources.StandardIcons.WindowClose);
		close.SetTooltipText (Translations.GetString ("Close"));
		close.OnClicked += (_, _) => window.Close ();
		header.PackEnd (close);
		header.PackEnd (open_button);
		header.PackEnd (new_button);
		return header;
	}

	private void AppendRow (RecentFileManager.RecentFile file)
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Horizontal, 10);
		content.SetAllMargins (10);
		content.Append (Gtk.Image.NewFromIconName (Resources.StandardIcons.DocumentOpen));

		Gtk.Box labels = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		Gtk.Label name = Gtk.Label.New (file.DisplayName);
		name.Halign = Gtk.Align.Start;
		name.Ellipsize = Pango.EllipsizeMode.End;
		Gtk.Label path = Gtk.Label.New (file.DisplayPath);
		path.Halign = Gtk.Align.Start;
		path.Ellipsize = Pango.EllipsizeMode.Middle;
		path.AddCssClass (AdwaitaStyles.DimLabel);
		labels.Append (name);
		labels.Append (path);
		content.Append (labels);

		Gtk.ListBoxRow row = Gtk.ListBoxRow.New ();
		row.SetChild (content);
		row.TooltipText = file.DisplayPath;
		row_uris.Add (row, file.Uri);
		list.Append (row);
	}

	private void OpenSelected ()
	{
		if (list.GetSelectedRow () is Gtk.ListBoxRow row)
			OpenRow (row);
	}

	private void CreateNewDocument ()
	{
		if (create_new_document ())
			ContinueToMainWindow ();
	}

	private void OpenRow (Gtk.ListBoxRow row)
	{
		if (!row_uris.TryGetValue (row, out string? uri))
			return;

		if (!open_document (uri))
			return;

		recent_files.AddFile (Gio.FileHelper.NewForUri (uri));
		ContinueToMainWindow ();
	}

	private void ContinueToMainWindow ()
	{
		continuing_to_main_window = true;
		ContinuedToMainWindow?.Invoke (this, EventArgs.Empty);
		window.Close ();
	}

	private bool HandleCloseRequest (Gtk.Window sender, EventArgs args)
	{
		if (continuing_to_main_window)
			return false;

		application.Quit ();
		return true;
	}
}
