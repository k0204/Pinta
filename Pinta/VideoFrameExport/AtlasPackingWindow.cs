using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

internal sealed partial class AtlasPackingWindow : IDisposable
{
	private readonly Adw.ApplicationWindow window;
	private Gtk.Label fileSummary = null!;
	private Gtk.Entry outputFolderEntry = null!;
	private Gtk.Entry atlasNameEntry = null!;
	private Gtk.SpinButton scaleSpinner = null!;
	private Gtk.SpinButton minWidthSpinner = null!;
	private Gtk.SpinButton maxWidthSpinner = null!;
	private Gtk.SpinButton minHeightSpinner = null!;
	private Gtk.SpinButton maxHeightSpinner = null!;
	private Gtk.SpinButton spacingSpinner = null!;
	private Gtk.ToggleButton trimToggle = null!;
	private Gtk.Button buildButton = null!;
	private Gtk.ProgressBar progress = null!;
	private readonly List<string> paths = [];
	private readonly CancellationTokenSource lifetime = new ();
	private CancellationTokenSource? build_cts;
	private bool disposed;

	public event EventHandler? Closed;

	public AtlasPackingWindow (Adw.Application application, Gtk.Window parent, IReadOnlyList<string>? initialPaths = null)
	{
		window = Adw.ApplicationWindow.New (application);
		window.TransientFor = parent;
		window.Modal = true;
		window.DestroyWithParent = true;
		window.DefaultWidth = 1280;
		window.DefaultHeight = 760;
		window.Title = Translations.GetString ("Texture Atlas Packer");
		window.OnCloseRequest += HandleCloseRequest;

		Gtk.Box root = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		root.Append (CreateHeader ());
		Gtk.Box workspace = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		Gtk.Box sourcePanel = CreateSourcePanel ();
		sourcePanel.WidthRequest = 280;
		workspace.Append (sourcePanel);
		Gtk.Box previewPanel = CreatePreviewPanel ();
		previewPanel.Hexpand = true;
		previewPanel.Vexpand = true;
		workspace.Append (previewPanel);
		Gtk.ScrolledWindow settingsPanel = CreateContent ();
		settingsPanel.WidthRequest = 360;
		settingsPanel.Vexpand = true;
		workspace.Append (settingsPanel);
		workspace.Hexpand = true;
		workspace.Vexpand = true;
		root.Append (workspace);
		window.SetContent (root);
		SetInputPaths (initialPaths ?? Array.Empty<string> ());
	}

	public void Present ()
	{
		window.Present ();
		window.SetFocus (sourceGrid);
	}

	public void Dispose ()
	{
		if (disposed)
			return;
		disposed = true;
		build_cts?.Cancel ();
		build_cts?.Dispose ();
		lifetime.Cancel ();
		lifetime.Dispose ();
	}

	private Gtk.Box CreateHeader ()
	{
		Gtk.Box header = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		header.SetAllMargins (10);
		header.AddCssClass (AdwaitaStyles.Toolbar);

		Gtk.Button close = Gtk.Button.NewFromIconName (StandardIcons.WindowClose);
		close.SetTooltipText (Translations.GetString ("Close Texture Atlas Packer"));
		close.OnClicked += (_, _) => window.Close ();
		header.Append (close);

		Gtk.Label title = Gtk.Label.New (Translations.GetString ("Texture Atlas Packer"));
		title.Halign = Gtk.Align.Start;
		title.AddCssClass (AdwaitaStyles.Heading);
		header.Append (title);
		return header;
	}

	private Gtk.ScrolledWindow CreateContent ()
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 10);
		content.SetAllMargins (12);

		string defaultFolder = Path.Combine (
			Environment.GetFolderPath (Environment.SpecialFolder.DesktopDirectory),
			"atlas");
		outputFolderEntry = Gtk.Entry.New ();
		outputFolderEntry.SetText (defaultFolder);
		outputFolderEntry.OnChanged += (_, _) => UpdateState ();
		content.Append (CreateEntryRow (
			Translations.GetString ("Atlas folder"),
			outputFolderEntry,
			CreateFolderButton (outputFolderEntry)));

		atlasNameEntry = Gtk.Entry.New ();
		atlasNameEntry.SetText ("atlas");
		atlasNameEntry.OnChanged += (_, _) => UpdateState ();
		content.Append (CreateEntryRow (Translations.GetString ("Atlas filename"), atlasNameEntry));

		scaleSpinner = Gtk.SpinButton.NewWithRange (1, 100, 1);
		scaleSpinner.Value = 100;
		content.Append (CreateEntryRow (Translations.GetString ("Scale (%)"), scaleSpinner));

		minWidthSpinner = Gtk.SpinButton.NewWithRange (0, 16384, 1);
		maxWidthSpinner = Gtk.SpinButton.NewWithRange (1, 16384, 1);
		minHeightSpinner = Gtk.SpinButton.NewWithRange (0, 16384, 1);
		maxHeightSpinner = Gtk.SpinButton.NewWithRange (1, 16384, 1);
		maxWidthSpinner.Value = 2048;
		maxHeightSpinner.Value = 2048;
		content.Append (CreateEntryRow (Translations.GetString ("Minimum atlas width (0 = automatic)"), minWidthSpinner));
		content.Append (CreateEntryRow (Translations.GetString ("Maximum atlas width"), maxWidthSpinner));
		content.Append (CreateEntryRow (Translations.GetString ("Minimum atlas height (0 = automatic)"), minHeightSpinner));
		content.Append (CreateEntryRow (Translations.GetString ("Maximum atlas height"), maxHeightSpinner));

		spacingSpinner = Gtk.SpinButton.NewWithRange (0, 256, 1);
		spacingSpinner.Value = 2;
		content.Append (CreateEntryRow (Translations.GetString ("Spacing"), spacingSpinner));

		trimToggle = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Trim transparent pixels"));
		trimToggle.Active = true;
		content.Append (trimToggle);

		buildButton = Gtk.Button.NewWithLabel (Translations.GetString ("Build atlas"));
		buildButton.AddCssClass (AdwaitaStyles.SuggestedAction);
		buildButton.OnClicked += HandleBuildClicked;
		content.Append (buildButton);

		progress = Gtk.ProgressBar.New ();
		progress.ShowText = true;
		progress.Text = Translations.GetString ("Ready");
		progress.Hide ();
		content.Append (progress);

		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		scroll.SetChild (content);
		return scroll;
	}

	private Gtk.Button CreateFolderButton (Gtk.Entry target)
	{
		Gtk.Button button = Gtk.Button.NewFromIconName (StandardIcons.Folder);
		button.SetTooltipText (Translations.GetString ("Choose atlas output folder"));
		button.OnClicked += async (_, _) => {
			using Gtk.FileDialog dialog = Gtk.FileDialog.New ();
			dialog.SetTitle (Translations.GetString ("Choose atlas output folder"));
			Gio.File? folder = await dialog.SelectFolderAsync (window);
			if (folder?.GetPath () is string path)
				target.SetText (path);
		};
		return button;
	}

	private async void HandleBuildClicked (object sender, EventArgs args)
	{
		if (paths.Count == 0)
			return;

		build_cts?.Cancel ();
		build_cts?.Dispose ();
		build_cts = CancellationTokenSource.CreateLinkedTokenSource (lifetime.Token);
		CancellationToken cancellationToken = build_cts.Token;
		buildButton.Sensitive = false;
		progress.Fraction = 0;
		progress.Text = Translations.GetString ("Building atlas: {0} frames", paths.Count);
		progress.Show ();

		try {
			AtlasBuildResult result = await Task.Run (() => VideoAtlasBuilder.Build (
				paths.ToArray (),
				outputFolderEntry.GetText ().Trim (),
				atlasNameEntry.GetText ().Trim (),
				(int) scaleSpinner.GetValue (),
				(int) minWidthSpinner.GetValue (),
				(int) maxWidthSpinner.GetValue (),
				(int) minHeightSpinner.GetValue (),
				(int) maxHeightSpinner.GetValue (),
				(int) spacingSpinner.GetValue (),
				trimToggle.Active,
				cancellationToken), cancellationToken);
			progress.Fraction = 1;
			progress.Text = Translations.GetString (
				"Atlas saved: {0} page(s), metadata: {1}",
				result.ImagePaths.Count,
				result.MetadataPath);
			UpdateAtlasPreview (result.ImagePaths);
		} catch (OperationCanceledException) {
			progress.Fraction = 0;
			progress.Text = Translations.GetString ("Atlas build canceled.");
		} catch (VideoFrameExportException ex) {
			progress.Fraction = 0;
			progress.Text = ex.Message;
		} catch (Exception ex) {
			progress.Fraction = 0;
			progress.Text = Translations.GetString ("Atlas build failed.");
			Console.Error.WriteLine (ex);
		} finally {
			UpdateState ();
		}
	}

	private void UpdateState ()
	{
		fileSummary.SetText (paths.Count == 0
			? Translations.GetString ("No image frames selected")
			: Translations.GetString ("{0} image frames selected", paths.Count));
		buildButton.Sensitive = paths.Count > 0
			&& !string.IsNullOrWhiteSpace (outputFolderEntry.GetText ())
			&& !string.IsNullOrWhiteSpace (atlasNameEntry.GetText ());
	}

	private static Gtk.Box CreateEntryRow (string label, Gtk.Widget entry, Gtk.Widget? trailing = null)
	{
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		Gtk.Label name = Gtk.Label.New (label);
		name.WidthRequest = 112;
		name.Halign = Gtk.Align.Start;
		name.AddCssClass (AdwaitaStyles.DimLabel);
		row.Append (name);
		entry.Hexpand = true;
		row.Append (entry);
		if (trailing is not null)
			row.Append (trailing);
		return row;
	}

	private bool HandleCloseRequest (Gtk.Window sender, EventArgs args)
	{
		Dispose ();
		Closed?.Invoke (this, EventArgs.Empty);
		return false;
	}
}
