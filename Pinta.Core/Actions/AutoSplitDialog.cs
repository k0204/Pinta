using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

internal sealed partial class AutoSplitDialog : IDisposable
{
	private readonly PintaDialog dialog;
	private readonly UserLayer source;
	private readonly IReadOnlyList<AI.AiProviderInfo> providers;
	private readonly Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze;
	private readonly Func<bool>? ensure_ai_logged_in;
	private readonly List<AutoSplitRegion> regions = [];
	private readonly Gtk.DrawingArea preview = Gtk.DrawingArea.New ();
	private readonly Gtk.ListBox region_list = Gtk.ListBox.New ();
	private readonly Gtk.ComboBoxText detection_mode = Gtk.ComboBoxText.New ();
	private readonly Gtk.ComboBoxText api_provider = Gtk.ComboBoxText.New ();
	private readonly Gtk.Box api_provider_row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
	private readonly Gtk.Button detect_button = Gtk.Button.NewWithLabel (Translations.GetString ("Check Image"));
	private readonly Gtk.Button add_button = Gtk.Button.NewFromIconName (Resources.Icons.LayerNew);
	private readonly Gtk.Button delete_button = Gtk.Button.NewFromIconName (Resources.Icons.LayerDelete);
	private readonly Gtk.Button export_button = Gtk.Button.NewFromIconName (Resources.StandardIcons.Folder);
	private readonly Gtk.Button zoom_out_button = Gtk.Button.NewFromIconName (Resources.StandardIcons.ValueDecrease);
	private readonly Gtk.Button zoom_fit_button = Gtk.Button.NewFromIconName (Resources.StandardIcons.ZoomFitBest);
	private readonly Gtk.Button zoom_in_button = Gtk.Button.NewFromIconName (Resources.StandardIcons.ValueIncrease);
	private readonly Gtk.SpinButton x_spinner;
	private readonly Gtk.SpinButton y_spinner;
	private readonly Gtk.SpinButton width_spinner;
	private readonly Gtk.SpinButton height_spinner;
	private readonly Gtk.SpinButton minimum_tile_size_spinner;
	private readonly Gtk.Label status_label = Gtk.Label.New (string.Empty);
	private readonly Gtk.Label count_label = Gtk.Label.New (string.Empty);
	private readonly Gtk.Widget submit_button;
	private readonly Dictionary<Gtk.ListBoxRow, int> row_indices = [];
	private readonly HashSet<int> selected_regions = [];
	private int selected_region = -1;
	private bool syncing_fields;
	private bool syncing_list_selection;
	private bool analysis_running;
	private int preview_width = 620;
	private int preview_height = 520;

	public AutoSplitDialog (
		Gtk.Window parent,
		UserLayer source,
		IReadOnlyList<AI.AiProviderInfo> providers,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Func<bool>? ensureAiLoggedIn = null)
	{
		this.source = source;
		this.providers = providers;
		this.analyze = analyze;
		ensure_ai_logged_in = ensureAiLoggedIn;
		dialog = PintaDialog.NewWithProperties ([]);
		dialog.Title = Translations.GetString ("Auto Split Image");
		dialog.TransientFor = parent;
		dialog.DefaultWidth = 1120;
		dialog.DefaultHeight = 720;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		submit_button = dialog.AddButton (
		Translations.GetString ("Split Image"),
		(int) Gtk.ResponseType.Ok);
		submit_button.AddCssClass (AdwaitaStyles.SuggestedAction);
		dialog.SetDefaultResponse (Gtk.ResponseType.Ok);

		x_spinner = CreateSpinner (0, source.Surface.Width, 0);
		y_spinner = CreateSpinner (0, source.Surface.Height, 0);
		width_spinner = CreateSpinner (1, source.Surface.Width, Math.Min (source.Surface.Width, 1));
		height_spinner = CreateSpinner (1, source.Surface.Height, Math.Min (source.Surface.Height, 1));
		minimum_tile_size_spinner = CreateSpinner (1, Math.Min (source.Surface.Width, source.Surface.Height), 4);

		BuildDialogContent ();
		ConnectEvents ();
		ChangeDetectionMode ();
		ApplyLocalDetection ();
	}

	public async Task<IReadOnlyList<AutoSplitRegion>?> RunAsync ()
	{
		Gtk.ResponseType response = await dialog.RunAsync ();
		dialog.Close ();
		return response == Gtk.ResponseType.Ok && regions.Count > 0
			? [.. regions]
			: null;
	}

	public void Dispose () => dialog.Dispose ();

	private void BuildDialogContent ()
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		content.SetAllMargins (12);
		content.Hexpand = true;
		content.Vexpand = true;

		Gtk.Box main = Gtk.Box.New (Gtk.Orientation.Horizontal, 10);
		main.Hexpand = true;
		main.Vexpand = true;
		main.Append (BuildPreviewPanel ());
		main.Append (BuildSidebar ());
		content.Append (main);
		content.Append (status_label);
		dialog.GetContentAreaBox ().Append (content);
	}

	private Gtk.Widget BuildPreviewPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		panel.Hexpand = true;
		panel.Vexpand = true;
		Gtk.Label heading = Gtk.Label.New (Translations.GetString ("Preview and Split Regions"));
		heading.Halign = Gtk.Align.Start;
		heading.AddCssClass (AdwaitaStyles.Heading);
		panel.Append (heading);

		Gtk.Box toolbar = Gtk.Box.New (Gtk.Orientation.Horizontal, 2);
		toolbar.Halign = Gtk.Align.End;
		ConfigurePreviewButton (zoom_out_button, "Zoom Out");
		ConfigurePreviewButton (zoom_fit_button, "Best Fit");
		ConfigurePreviewButton (zoom_in_button, "Zoom In");
		toolbar.Append (zoom_out_button);
		toolbar.Append (zoom_fit_button);
		toolbar.Append (zoom_in_button);
		panel.Append (toolbar);

		preview.Hexpand = true;
		preview.Vexpand = true;
		preview.SetSizeRequest (620, 520);
		preview.AddCssClass ("card");
		preview.SetDrawFunc (DrawPreview);
		panel.Append (preview);
		return panel;
	}

	private Gtk.Widget BuildSidebar ()
	{
		Gtk.Box sidebar = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		sidebar.SetSizeRequest (340, -1);
		sidebar.Vexpand = true;

		Gtk.Box method_card = CreateCard ();
		method_card.Append (CreateHeading (Translations.GetString ("Detection Method")));
		detection_mode.AppendText (Translations.GetString ("Local Pixel Scan"));
		detection_mode.AppendText (Translations.GetString ("API Pixel Analysis"));
		detection_mode.AppendText (Translations.GetString ("Manual Selection"));
		detection_mode.Active = 0;
		method_card.Append (CreateFormRow (Translations.GetString ("Method:"), detection_mode));
		method_card.Append (CreateFormRow (Translations.GetString ("Minimum Tile Size:"), minimum_tile_size_spinner));

		foreach (AI.AiProviderInfo provider in providers)
			api_provider.AppendText (provider.Name);
		if (providers.Count == 0)
			api_provider.AppendText (Translations.GetString ("No API provider available"));
		api_provider.Active = 0;
		if (providers.Count > 0) {
			string preferred = AI.AiRequestSettings.GetSpriteSegmentationProvider (PintaCore.Settings);
			for (int index = 0; index < providers.Count; index++)
				if (providers[index].Id == preferred)
					api_provider.Active = index;
		}
		api_provider_row.Append (Gtk.Label.New (Translations.GetString ("Provider:")));
		api_provider_row.Append (api_provider);
		api_provider.Hexpand = true;
		method_card.Append (api_provider_row);
		method_card.Append (detect_button);
		method_card.Append (Gtk.Label.New (Translations.GetString (
			"Local scan finds connected non-transparent pixels. API analysis returns editable bounding boxes.")));
		sidebar.Append (method_card);

		Gtk.Box root_card = CreateCard ();
		root_card.Append (CreateHeading (Translations.GetString ("Root Layer")));
		Gtk.Label root_info = Gtk.Label.New (Translations.GetString (
			"{0}\n{1} x {2} pixels",
			source.Name,
			source.Surface.Width,
			source.Surface.Height));
		root_info.Halign = Gtk.Align.Start;
		root_info.Wrap = true;
		root_card.Append (root_info);
		sidebar.Append (root_card);

		Gtk.Box regions_card = CreateCard ();
		Gtk.Box regions_header = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		Gtk.Label regions_heading = CreateHeading (Translations.GetString ("Split Regions"));
		regions_heading.Hexpand = true;
		regions_header.Append (regions_heading);
		count_label.AddCssClass (AdwaitaStyles.DimLabel);
		regions_header.Append (count_label);
		add_button.SetTooltipText (Translations.GetString ("Add Region"));
		delete_button.SetTooltipText (Translations.GetString ("Delete Region"));
		export_button.SetTooltipText (Translations.GetString ("Export All Regions"));
		add_button.AddCssClass (AdwaitaStyles.Flat);
		delete_button.AddCssClass (AdwaitaStyles.Flat);
		export_button.AddCssClass (AdwaitaStyles.Flat);
		regions_header.Append (export_button);
		regions_header.Append (add_button);
		regions_header.Append (delete_button);
		regions_card.Append (regions_header);

		Gtk.ScrolledWindow list_scroll = Gtk.ScrolledWindow.New ();
		list_scroll.Vexpand = true;
		list_scroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		region_list.SetSelectionMode (Gtk.SelectionMode.Single);
		list_scroll.SetChild (region_list);
		regions_card.Append (list_scroll);

		Gtk.Grid editor = Gtk.Grid.New ();
		editor.RowSpacing = 5;
		editor.ColumnSpacing = 6;
		editor.Attach (Gtk.Label.New (Translations.GetString ("X:")), 0, 0, 1, 1);
		editor.Attach (x_spinner, 1, 0, 1, 1);
		editor.Attach (Gtk.Label.New (Translations.GetString ("Y:")), 2, 0, 1, 1);
		editor.Attach (y_spinner, 3, 0, 1, 1);
		editor.Attach (Gtk.Label.New (Translations.GetString ("Width:")), 0, 1, 1, 1);
		editor.Attach (width_spinner, 1, 1, 1, 1);
		editor.Attach (Gtk.Label.New (Translations.GetString ("Height:")), 2, 1, 1, 1);
		editor.Attach (height_spinner, 3, 1, 1, 1);
		regions_card.Append (editor);
		sidebar.Append (regions_card);
		return sidebar;
	}

	private void ConnectEvents ()
	{
		detection_mode.OnChanged += (_, _) => ChangeDetectionMode ();
		minimum_tile_size_spinner.OnValueChanged += (_, _) => {
			if (detection_mode.Active == 0 && !analysis_running)
				ApplyLocalDetection ();
		};
		detect_button.OnClicked += async (_, _) => await RunDetectionAsync ();
		add_button.OnClicked += (_, _) => AddDefaultRegion ();
		delete_button.OnClicked += (_, _) => DeleteSelectedRegion ();
		export_button.OnClicked += async (_, _) => await ExportAllRegionsAsync ();
		region_list.OnRowSelected += (_, args) => {
			if (!syncing_list_selection && args.Row is not null && row_indices.TryGetValue (args.Row, out int index))
				SelectRegion (index);
		};
		x_spinner.OnValueChanged += (_, _) => UpdateSelectedRegion ();
		y_spinner.OnValueChanged += (_, _) => UpdateSelectedRegion ();
		width_spinner.OnValueChanged += (_, _) => UpdateSelectedRegion ();
		height_spinner.OnValueChanged += (_, _) => UpdateSelectedRegion ();
		preview.OnResize += (_, args) => {
			preview_width = args.Width;
			preview_height = args.Height;
			ClampPreviewPan ();
			preview.QueueDraw ();
		};
		Gtk.GestureDrag drag = Gtk.GestureDrag.New ();
		drag.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		drag.OnDragBegin += (_, args) => BeginManualDrag (args.StartX, args.StartY);
		drag.OnDragUpdate += (_, args) => UpdateManualDrag (args.OffsetX, args.OffsetY);
		drag.OnDragEnd += (_, args) => EndManualDrag (args.OffsetX, args.OffsetY);
		preview.AddController (drag);

		Gtk.GestureClick click = Gtk.GestureClick.New ();
		click.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		click.OnPressed += (controller, args) => HandlePreviewClick (controller, args.X, args.Y);
		preview.AddController (click);

		Gtk.EventControllerScroll scroll = Gtk.EventControllerScroll.New (Gtk.EventControllerScrollFlags.BothAxes);
		scroll.OnScroll += HandlePreviewScroll;
		preview.AddController (scroll);

		Gtk.GestureDrag pan = Gtk.GestureDrag.New ();
		pan.SetButton (GtkExtensions.MOUSE_MIDDLE_BUTTON);
		pan.OnDragBegin += (_, _) => BeginPreviewPan ();
		pan.OnDragUpdate += (_, args) => UpdatePreviewPan (args.OffsetX, args.OffsetY);
		pan.OnDragEnd += (_, _) => EndPreviewPan ();
		preview.AddController (pan);

		zoom_out_button.OnClicked += (_, _) => ChangePreviewZoom (1 / 1.25);
		zoom_fit_button.OnClicked += (_, _) => ResetPreviewView ();
		zoom_in_button.OnClicked += (_, _) => ChangePreviewZoom (1.25);
	}

	private void ChangeDetectionMode ()
	{
		bool api = detection_mode.Active == 1;
		bool manual = detection_mode.Active == 2;
		api_provider_row.Visible = api;
		detect_button.Visible = !manual;
		detect_button.Sensitive = !manual && (!api || providers.Count > 0) && !analysis_running;
		string status = manual
			? Translations.GetString ("Drag on the preview to add a region.")
			: Translations.GetString ("Choose a method and check the image to refresh the regions.");
		status_label.SetText (status);
		UpdateActionState ();
	}

	private void SelectRegion (int index)
	{
		selected_regions.Clear ();
		selected_region = index >= 0 && index < regions.Count ? index : -1;
		if (selected_region >= 0)
			selected_regions.Add (selected_region);
		UpdateEditorValues ();
		UpdateActionState ();
		SyncRegionListSelection ();
		preview.QueueDraw ();
	}

	private void RefreshRegionList ()
	{
		row_indices.Clear ();
		region_list.RemoveAll ();
		for (int index = 0; index < regions.Count; index++) {
			RectangleI bounds = regions[index].Bounds;
			Gtk.Box labels = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
			labels.Append (Gtk.Label.New (Translations.GetString ("Region {0}", index + 1)));
			labels.Append (Gtk.Label.New (Translations.GetString (
				"X {0}, Y {1}, {2} x {3}", bounds.X, bounds.Y, bounds.Width, bounds.Height)));
			Gtk.ListBoxRow row = Gtk.ListBoxRow.New ();
			row.SetChild (labels);
			row_indices[row] = index;
			region_list.Append (row);
		}

		count_label.SetText (Translations.GetString ("{0} found", regions.Count));
		if (selected_region >= 0 && selected_region < regions.Count) {
			UpdateEditorValues ();
			SyncRegionListSelection ();
		} else {
			SelectRegion (-1);
		}
	}

	private void SyncRegionListSelection ()
	{
		if (selected_region < 0 || selected_region >= regions.Count)
			return;

		Gtk.ListBoxRow? row = row_indices.FirstOrDefault (item => item.Value == selected_region).Key;
		if (row is null)
			return;

		syncing_list_selection = true;
		region_list.SelectRow (row);
		syncing_list_selection = false;
	}

	private void UpdateEditorValues ()
	{
		syncing_fields = true;
		if (selected_region < 0 || selected_region >= regions.Count) {
			x_spinner.SetValue (0);
			y_spinner.SetValue (0);
			width_spinner.SetValue (1);
			height_spinner.SetValue (1);
			SetEditorSensitivity (false);
		} else {
			RectangleI bounds = regions[selected_region].Bounds;
			x_spinner.SetRange (0, Math.Max (0, source.Surface.Width - 1));
			y_spinner.SetRange (0, Math.Max (0, source.Surface.Height - 1));
			width_spinner.SetRange (1, Math.Max (1, source.Surface.Width - bounds.X));
			height_spinner.SetRange (1, Math.Max (1, source.Surface.Height - bounds.Y));
			x_spinner.SetValue (bounds.X);
			y_spinner.SetValue (bounds.Y);
			width_spinner.SetValue (bounds.Width);
			height_spinner.SetValue (bounds.Height);
			SetEditorSensitivity (selected_regions.Count == 1);
		}
		syncing_fields = false;
	}

	private void UpdateSelectedRegion ()
	{
		if (syncing_fields || selected_region < 0 || selected_region >= regions.Count)
			return;

		int x = Math.Clamp (x_spinner.GetValueAsInt (), 0, source.Surface.Width - 1);
		int y = Math.Clamp (y_spinner.GetValueAsInt (), 0, source.Surface.Height - 1);
		int width = Math.Clamp (width_spinner.GetValueAsInt (), 1, source.Surface.Width - x);
		int height = Math.Clamp (height_spinner.GetValueAsInt (), 1, source.Surface.Height - y);
		regions[selected_region].SetBounds (new RectangleI (x, y, width, height));
		RefreshRegionList ();
		UpdateEditorValues ();
		preview.QueueDraw ();
	}

	private void SetEditorSensitivity (bool sensitive)
	{
		x_spinner.Sensitive = sensitive;
		y_spinner.Sensitive = sensitive;
		width_spinner.Sensitive = sensitive;
		height_spinner.Sensitive = sensitive;
	}

	private void UpdateActionState ()
	{
		submit_button.Sensitive = regions.Count > 0 && !analysis_running;
		delete_button.Sensitive = selected_regions.Count > 0 && !analysis_running;
		export_button.Sensitive = regions.Count > 0 && !analysis_running;
		add_button.Sensitive = !analysis_running;
		detect_button.Sensitive = detection_mode.Active != 2
			&& (detection_mode.Active != 1 || providers.Count > 0)
			&& !analysis_running;
	}

	private static Gtk.Box CreateCard ()
	{
		Gtk.Box card = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		card.SetAllMargins (8);
		card.AddCssClass ("card");
		return card;
	}

	private static Gtk.Label CreateHeading (string text)
	{
		Gtk.Label heading = Gtk.Label.New (text);
		heading.Halign = Gtk.Align.Start;
		heading.AddCssClass (AdwaitaStyles.Heading);
		return heading;
	}

	private static Gtk.Box CreateFormRow (string label, Gtk.Widget value)
	{
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		Gtk.Label label_widget = Gtk.Label.New (label);
		label_widget.Halign = Gtk.Align.Start;
		row.Append (label_widget);
		value.Hexpand = true;
		row.Append (value);
		return row;
	}

	private static void ConfigurePreviewButton (Gtk.Button button, string tooltip)
	{
		button.SetTooltipText (Translations.GetString (tooltip));
		button.AddCssClass (AdwaitaStyles.Flat);
	}

	private static Gtk.SpinButton CreateSpinner (int minimum, int maximum, int value)
	{
		Gtk.SpinButton spinner = Gtk.SpinButton.NewWithRange (minimum, Math.Max (minimum, maximum), 1);
		spinner.SetValue (value);
		spinner.SetActivatesDefaultImmediate (true);
		spinner.WidthRequest = 70;
		return spinner;
	}
}
