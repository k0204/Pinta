using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pinta.Core;
using Pinta.Resources;

namespace Pinta;

[GObject.Subclass<PintaDialog>]
internal sealed partial class AtlasPackingWindow
{
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
	private CancellationTokenSource? preview_cts;
	private int preview_version;
	private bool disposed;

	public event EventHandler? Closed;

	public static AtlasPackingWindow New (Gtk.Window parent, IReadOnlyList<string>? initialPaths = null)
	{
		AtlasPackingWindow window = NewWithProperties ([]);
		window.Configure (parent, initialPaths);
		return window;
	}

	partial void Initialize ()
	{
		Gtk.Box root = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
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
		this.GetContentAreaBox ().Append (root);
	}

	private void Configure (Gtk.Window parent, IReadOnlyList<string>? initialPaths)
	{
		TransientFor = parent;
		DestroyWithParent = true;
		DefaultWidth = 1280;
		DefaultHeight = 760;
		Title = Translations.GetString ("Texture Atlas Packer");
		OnCloseRequest += HandleCloseRequest;
		SetInputPaths (initialPaths ?? Array.Empty<string> ());
	}

	public new void Present ()
	{
		base.Present ();
		SetFocus (sourceGrid);
	}

	public override void Dispose ()
	{
		if (!disposed) {
			disposed = true;
			build_cts?.Cancel ();
			build_cts?.Dispose ();
			CancelAtlasPreviewBuild ();
			lifetime.Cancel ();
			lifetime.Dispose ();
		}
		base.Dispose ();
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
		scaleSpinner.OnValueChanged += (_, _) => RequestAtlasPreview ();
		content.Append (CreateEntryRow (Translations.GetString ("Scale (%)"), scaleSpinner));

		minWidthSpinner = Gtk.SpinButton.NewWithRange (0, 16384, 1);
		maxWidthSpinner = Gtk.SpinButton.NewWithRange (1, 16384, 1);
		minHeightSpinner = Gtk.SpinButton.NewWithRange (0, 16384, 1);
		maxHeightSpinner = Gtk.SpinButton.NewWithRange (1, 16384, 1);
		maxWidthSpinner.Value = 2048;
		maxHeightSpinner.Value = 2048;
		minWidthSpinner.OnValueChanged += (_, _) => RequestAtlasPreview ();
		maxWidthSpinner.OnValueChanged += (_, _) => RequestAtlasPreview ();
		minHeightSpinner.OnValueChanged += (_, _) => RequestAtlasPreview ();
		maxHeightSpinner.OnValueChanged += (_, _) => RequestAtlasPreview ();
		content.Append (CreateEntryRow (Translations.GetString ("Minimum atlas width (0 = automatic)"), minWidthSpinner));
		content.Append (CreateEntryRow (Translations.GetString ("Maximum atlas width"), maxWidthSpinner));
		content.Append (CreateEntryRow (Translations.GetString ("Minimum atlas height (0 = automatic)"), minHeightSpinner));
		content.Append (CreateEntryRow (Translations.GetString ("Maximum atlas height"), maxHeightSpinner));

		spacingSpinner = Gtk.SpinButton.NewWithRange (0, 256, 1);
		spacingSpinner.Value = 2;
		spacingSpinner.OnValueChanged += (_, _) => RequestAtlasPreview ();
		content.Append (CreateEntryRow (Translations.GetString ("Spacing"), spacingSpinner));

		trimToggle = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Trim transparent pixels"));
		trimToggle.Active = true;
		trimToggle.OnToggled += (_, _) => RequestAtlasPreview ();
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
			Gio.File? folder = await dialog.SelectFolderAsync (this);
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
		CancelAtlasPreviewBuild ();
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
