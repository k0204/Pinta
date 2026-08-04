using System;
using System.Collections.Generic;
using Cairo;

namespace Pinta.Core;

internal sealed partial class AnimationFrameEditor
{
	private const int default_left_panel_width = 220;
	private const int default_right_panel_width = 224;
	private const int panel_divider_width = 14;
	private const int collapse_button_width = 20;
	private const int min_left_panel_width = 180;
	private const int max_left_panel_width = 420;
	private const int min_right_panel_width = 190;
	private const int max_right_panel_width = 360;
	private int left_panel_width = default_left_panel_width;
	private int right_panel_width = default_right_panel_width;
	private readonly Gtk.Box source_section = CreateCard ();
	private Gtk.Box? source_preview_card;
	private Gtk.ScrolledWindow? source_preview_scroll;
	private Gtk.Box? attempt_section;

	/* ── Top-level layout ──────────────────────────────────── */

	private void BuildContent ()
	{
		// Main two-column area
		Gtk.Box main_row = Gtk.Box.New (Gtk.Orientation.Horizontal, 4);
		main_row.Hexpand = true;
		main_row.Vexpand = true;
		main_row.MarginStart = 8;
		main_row.MarginEnd = 8;
		main_row.MarginTop = 8;
		Gtk.Widget left_panel = BuildLeftPanel ();
		Gtk.Overlay left_divider = CreatePanelDivider (
			left_panel,
			true,
			() => left_panel_width,
			width => ResizeLeftPanel (left_panel, width));
		main_row.Append (left_panel);
		main_row.Append (left_divider);
		main_row.Append (BuildRightPanel ());

		// Bottom status line (validation chip)
		Gtk.Box status_line = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		status_line.SetAllMargins (8);
		status_line.Hexpand = true;
		validation_label.Xalign = 0.5f;
		validation_label.Halign = Gtk.Align.Center;
		validation_label.Hexpand = true;
		validation_label.AddCssClass (AdwaitaStyles.DimLabel);
		validation_label.AddCssClass ("caption");
		status_line.Append (validation_label);

		content.Append (main_row);
		content.Append (Gtk.Separator.New (Gtk.Orientation.Horizontal));
		content.Append (status_line);
	}

	/* ── Left column: source settings + frame list only ─────── */

	private Gtk.Widget BuildLeftPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		panel.SetSizeRequest (left_panel_width, -1);
		panel.Halign = Gtk.Align.Start;
		panel.Hexpand = false;
		panel.Vexpand = true;
		panel.Append (BuildSourceSection ());
		panel.Append (BuildSourcePreviewSection ());
		return panel;
	}

	private Gtk.Widget BuildSourceSection ()
	{
		source_section.SetAllMargins (8);
		source_section.SetSizeRequest (left_panel_width - 16, -1);
		source_section.Hexpand = false;
		source_section.Append (CreateHeading (Translations.GetString ("Source extraction")));
		Gtk.Widget source_controls = BuildSourceModeTabs ();
		InsetCardChild (source_controls);
		source_section.Append (source_controls);
		return source_section;
	}

	private Gtk.Widget BuildSourcePreviewSection ()
	{
		Gtk.Box card = CreateCard ();
		card.SetAllMargins (6);
		card.SetSizeRequest (left_panel_width - 12, -1);
		card.Hexpand = false;
		card.Vexpand = true;
		card.Append (CreateHeading (Translations.GetString ("Source preview")));

		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.Hexpand = true;
		scroll.Vexpand = true;
		scroll.SetSizeRequest (left_panel_width - 28, -1);
		scroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		InsetCardChild (scroll);
		scroll.SetChild (source_preview);
		source_preview_card = card;
		source_preview_scroll = scroll;
		UpdateSourcePreviewSize (left_panel_width - 28);
		card.Append (scroll);
		return card;
	}

	private Gtk.Widget BuildFrameListSection ()
	{
		Gtk.Box card = CreateCard ();
		card.SetAllMargins (6);
		card.Vexpand = true;
		card.Append (CreateHeading (Translations.GetString ("Frames")));

		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.Vexpand = true;
		scroll.MinContentHeight = 200;
		InsetCardChild (scroll);
		scroll.SetChild (frame_list);
		card.Append (scroll);
		return card;
	}

	/* ── Right column: preview | side settings ──────────────── */

	private Gtk.Widget BuildRightPanel ()
	{
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 4);
		row.Hexpand = true;
		row.Vexpand = true;

		// Left part: toolbar + preview (takes remaining space)
		row.Append (BuildPreviewPanel ());

		// Right part: Output canvas + Position (fixed width sidebar)
		Gtk.Widget sidebar = BuildPreviewSidebar ();
		Gtk.Overlay right_divider = CreatePanelDivider (
			sidebar,
			false,
			() => right_panel_width,
			width => ResizeRightPanel (sidebar, width));
		row.Append (right_divider);
		row.Append (sidebar);
		return row;
	}

	private Gtk.Widget BuildPreviewSidebar ()
	{
		Gtk.Box sidebar = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
		sidebar.SetSizeRequest (right_panel_width, -1);
		sidebar.Hexpand = false;
		sidebar.Vexpand = true;

		// Output canvas card
		Gtk.Box output_card = CreateCard ();
		output_card.SetAllMargins (6);
		output_card.Spacing = 6;
		output_card.Append (CreateHeading (Translations.GetString ("Output canvas")));
		Gtk.Grid output_grid = CreateGrid ([
			(Translations.GetString ("Width:"), canvas_width),
			(Translations.GetString ("Height:"), canvas_height),
		]);
		InsetCardChild (output_grid);
		output_card.Append (output_grid);
		if (output_attempts.Count > 0) {
			attempt_section = Gtk.Box.New (Gtk.Orientation.Vertical, 4);
			attempt_section.SetMarginTop (2);
			attempt_section.Append (CreateHeading (Translations.GetString ("Output attempt")));
			InsetCardChild (output_attempt);
			attempt_section.Append (output_attempt);
			output_card.Append (attempt_section);
		}
		sidebar.Append (output_card);

		// Position card
		Gtk.Box pos_card = CreateCard ();
		pos_card.SetAllMargins (6);
		pos_card.Spacing = 6;
		pos_card.Append (CreateHeading (Translations.GetString ("Position")));
		frame_x.SetSizeRequest (72, 28);
		frame_y.SetSizeRequest (72, 28);
		Gtk.Box position_row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		position_row.MarginStart = 8;
		position_row.MarginEnd = 8;
		Gtk.Label x_label = Gtk.Label.New (Translations.GetString ("X:"));
		x_label.Halign = Gtk.Align.Start;
		Gtk.Label y_label = Gtk.Label.New (Translations.GetString ("Y:"));
		y_label.Halign = Gtk.Align.Start;
		position_row.Append (x_label);
		position_row.Append (frame_x);
		position_row.Append (y_label);
		position_row.Append (frame_y);
		pos_card.Append (position_row);
		pos_card.Append (previous_frame_reference);
		Gtk.Box opacity_row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		opacity_row.MarginStart = 8;
		opacity_row.MarginEnd = 8;
		Gtk.Label opacity_label = Gtk.Label.New ("参考透明度:");
		opacity_label.Halign = Gtk.Align.Start;
		opacity_row.Append (opacity_label);
		opacity_row.Append (previous_frame_opacity);
		pos_card.Append (opacity_row);
		InsetCardChild (move_root);
		pos_card.Append (move_root);
		Gtk.Label hint = Gtk.Label.New (Translations.GetString (
			"拖动预览中的红色锚点，或输入 X/Y 数值。"));
		hint.Wrap = true;
		hint.MaxWidthChars = 24;
		hint.Xalign = 0;
		hint.Halign = Gtk.Align.Fill;
		hint.MarginStart = 8;
		hint.MarginEnd = 8;
		hint.MarginBottom = 4;
		hint.MarginTop = 2;
		hint.AddCssClass (AdwaitaStyles.DimLabel);
		hint.AddCssClass ("caption");
		pos_card.Append (hint);
		sidebar.Append (pos_card);
		sidebar.Append (BuildFrameListSection ());

		return sidebar;
	}

	/* ── Preview area: toolbar + canvas ─────────────────────── */

	private Gtk.Widget BuildPreviewPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		panel.Hexpand = true;
		panel.Vexpand = true;

		// Toolbar sits directly above preview
		panel.Append (BuildPreviewToolbar ());
		panel.Append (BuildRulerPreview ());
		return panel;
	}

	/* ── Preview toolbar (compact, icon-only) ────────────────── */

	private Gtk.Widget BuildPreviewToolbar ()
	{
		Gtk.Box bar = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		bar.AddCssClass (AdwaitaStyles.Toolbar);
		bar.SetAllMargins (0);

		// Frame navigation (linked group for visual cohesion)
		Gtk.Box nav_group = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		nav_group.AddCssClass (AdwaitaStyles.Linked);
		nav_group.Append (previous_frame);
		nav_group.Append (next_frame);
		bar.Append (nav_group);

		sprite_name_label.Halign = Gtk.Align.Start;
		sprite_name_label.MarginStart = 0;
		bar.Append (sprite_name_label);

		return bar;
	}

	/* ── Source mode tabs ───────────────────────────────────── */

	private Gtk.Widget BuildSourceModeTabs ()
	{
		Gtk.Box grid_controls = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		grid_controls.Append (CreateGrid ([
			(Translations.GetString ("Columns:"), columns),
			(Translations.GetString ("Rows:"), rows),
			(Translations.GetString ("Cell width:"), cell_width),
			(Translations.GetString ("Cell height:"), cell_height),
		]));
		Gtk.Expander advanced = Gtk.Expander.New (
			Translations.GetString ("Offsets and gaps"));
		advanced.Child = CreateGrid ([
			(Translations.GetString ("Left offset:"), offset_x),
			(Translations.GetString ("Top offset:"), offset_y),
			(Translations.GetString ("Horizontal gap:"), gap_x),
			(Translations.GetString ("Vertical gap:"), gap_y),
		]);
		grid_controls.Append (advanced);
		grid_controls.Append (align_character);

		source_mode_stack.AddTitled (BuildSmartAnalyzeControls (), ai_source_mode,
			Translations.GetString ("AI analysis"));
		source_mode_stack.AddTitled (grid_controls, grid_source_mode,
			Translations.GetString ("Grid"));
		source_mode_stack.VisibleChildName = grid_source_mode;

		// Build custom segmented switcher so labels are always readable
		Gtk.ToggleButton ai_btn = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("AI analysis"));
		ai_btn.AddCssClass (AdwaitaStyles.Linked);
		ai_btn.Active = false;
		ai_btn.OnToggled += (_, _) => {
			if (ai_btn.Active) source_mode_stack.VisibleChildName = ai_source_mode;
		};

		Gtk.ToggleButton grid_btn = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Grid"));
		grid_btn.AddCssClass (AdwaitaStyles.Linked);
		grid_btn.Active = true;
		grid_btn.OnToggled += (_, _) => {
			if (grid_btn.Active) source_mode_stack.VisibleChildName = grid_source_mode;
		};

		// Keep the buttons in sync with the stack
		source_mode_stack.OnNotify += (_, args) => {
			if (args.Pspec.GetName () != "visible-child-name") return;
			string? name = source_mode_stack.VisibleChildName;
			ai_btn.Active = name == ai_source_mode;
			grid_btn.Active = name == grid_source_mode;
		};

		Gtk.Box switcher = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		switcher.AddCssClass (AdwaitaStyles.Linked);
		switcher.Homogeneous = true;
		switcher.Hexpand = true;
		switcher.Append (ai_btn);
		switcher.Append (grid_btn);

		Gtk.Box result = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		result.Append (switcher);
		result.Append (source_mode_stack);
		return result;
	}

	private Gtk.Widget BuildSmartAnalyzeControls ()
	{
		IReadOnlyList<AI.AiProviderInfo> providers = PintaCore.AiProviders.ChatProviders;
		Gtk.Box controls = Gtk.Box.New (Gtk.Orientation.Vertical, 6);
		Gtk.Label label = Gtk.Label.New (Translations.GetString ("Analysis provider:"));
		label.Halign = Gtk.Align.Start;
		Gtk.ComboBoxText provider = Gtk.ComboBoxText.New ();
		foreach (AI.AiProviderInfo item in providers)
			provider.AppendText (item.Name);
		provider.Active = GetProviderIndex (
			providers,
			AI.AiRequestSettings.GetSpriteSegmentationProvider (PintaCore.Settings));
		provider.OnChanged += (_, _) => SaveProvider (provider, providers);
		Gtk.Button button = Gtk.Button.NewWithLabel (Translations.GetString ("Auto analyze"));
		button.TooltipText = Translations.GetString ("Use AI to detect sprite bounds and foot anchors");
		button.Sensitive = providers.Count > 0;
		button.OnClicked += async (_, _) => await AnalyzeAsync (button, provider, providers);
		controls.Append (label);
		controls.Append (provider);
		controls.Append (button);
		return controls;
	}

	/* ── Ruler + frame preview grid ─────────────────────────── */

	private Gtk.Widget BuildRulerPreview ()
	{
		horizontal_ruler.HeightRequest = 22;
		horizontal_ruler.Hexpand = true;
		horizontal_ruler.AddCssClass ("ruler");
		horizontal_ruler.SetDrawFunc ((area, context, width, height) =>
			DrawRuler (area, context, width, height, Gtk.Orientation.Horizontal));
		vertical_ruler.WidthRequest = 28;
		vertical_ruler.Vexpand = true;
		vertical_ruler.AddCssClass ("ruler");
		vertical_ruler.SetDrawFunc ((area, context, width, height) =>
			DrawRuler (area, context, width, height, Gtk.Orientation.Vertical));

		Gtk.Grid grid = Gtk.Grid.New ();
		grid.Hexpand = true;
		grid.Vexpand = true;
		grid.AddCssClass ("ruler-preview-grid");
		grid.Attach (horizontal_ruler, 1, 1, 1, 1);
		grid.Attach (vertical_ruler, 0, 2, 1, 1);
		grid.Attach (frame_preview, 1, 2, 1, 1);

		return grid;
	}

	/* ── Static helpers ──────────────────────────────────────── */

	private static Gtk.Box CreateCard ()
	{
		Gtk.Box card = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		card.AddCssClass ("card");
		return card;
	}

	private static Gtk.SpinButton CreateSpinner (double minimum, double maximum, double value)
	{
		Gtk.SpinButton spinner = Gtk.SpinButton.NewWithRange (minimum, maximum, 1);
		spinner.Value = Math.Clamp (value, minimum, maximum);
		spinner.SetSizeRequest (96, 28);
		spinner.AddCssClass (AdwaitaStyles.Compact);
		return spinner;
	}

	private static Gtk.DrawingArea CreatePreview (int height)
	{
		Gtk.DrawingArea preview = Gtk.DrawingArea.New ();
		if (height > 0)
			preview.HeightRequest = height;
		preview.Hexpand = true;
		preview.Vexpand = true;
		return preview;
	}

	private static Gtk.Button CreateNavigationButton (string icon, string tooltip)
	{
		Gtk.Button button = Gtk.Button.NewFromIconName (icon);
		button.SetTooltipText (tooltip);
		return button;
	}

	private static Gtk.Overlay CreatePanelDivider (
		Gtk.Widget panel,
		bool left,
		Func<int> getWidth,
		Action<int> setWidth)
	{
		Gtk.Overlay divider = Gtk.Overlay.New ();
		divider.SetSizeRequest (panel_divider_width, -1);
		divider.Hexpand = false;
		divider.Vexpand = true;

		Gtk.DrawingArea resize_area = Gtk.DrawingArea.New ();
		resize_area.Hexpand = true;
		resize_area.Vexpand = true;
		resize_area.Cursor = Gdk.Cursor.NewFromName ("col-resize", null);
		resize_area.SetTooltipText (Translations.GetString (left ? "Resize left panel" : "Resize right panel"));
		resize_area.SetDrawFunc ((_, context, width, height) =>
			DrawPanelDivider (context, width, height));
		divider.SetChild (resize_area);

		int dragStartWidth = 0;
		void AddResizeGesture (Gtk.Widget widget)
		{
			Gtk.GestureDrag drag = Gtk.GestureDrag.New ();
			drag.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
			drag.OnDragBegin += (_, _) => dragStartWidth = getWidth ();
			drag.OnDragUpdate += (_, args) => {
				int direction = left ? 1 : -1;
				setWidth (dragStartWidth + direction * (int) Math.Round (args.OffsetX));
			};
			widget.AddController (drag);
		}
		AddResizeGesture (resize_area);

		Gtk.Button collapse = Gtk.Button.NewFromIconName (
			GetPanelToggleIcon (left, panel.Visible));
		collapse.SetSizeRequest (collapse_button_width, -1);
		collapse.Halign = Gtk.Align.Center;
		collapse.Valign = Gtk.Align.Center;
		collapse.AddCssClass (AdwaitaStyles.Flat);
		collapse.Cursor = Gdk.Cursor.NewFromName ("default", null);
		collapse.SetTooltipText (Translations.GetString (
			panel.Visible
				? left ? "Collapse left panel" : "Collapse right panel"
				: left ? "Expand left panel" : "Expand right panel"));
		collapse.OnClicked += (_, _) => TogglePanel (panel, collapse, left);
		AddResizeGesture (collapse);
		divider.AddOverlay (collapse);
		return divider;
	}

	private static string GetPanelToggleIcon (bool left, bool visible)
		=> visible
			? left ? Resources.StandardIcons.GoPrevious : Resources.StandardIcons.GoNext
			: left ? Resources.StandardIcons.GoNext : Resources.StandardIcons.GoPrevious;

	private static void DrawPanelDivider (
		Context context,
		int width,
		int height)
	{
		context.SetSourceRgb (0.91, 0.91, 0.91);
		context.Rectangle (0, 0, width, height);
		context.Fill ();
		context.SetSourceRgb (0.78, 0.78, 0.78);
		context.Rectangle (width / 2d - 0.5, 0, 1, height);
		context.Fill ();

	}

	private static void InsetCardChild (Gtk.Widget widget)
	{
		widget.MarginStart = 8;
		widget.MarginEnd = 8;
	}

	private void UpdateSourcePreviewSize (int width)
	{
		if (width <= 0)
			return;
		int height = GetSourcePreviewHeight (width);
		if (source_preview.WidthRequest != width)
			source_preview.WidthRequest = width;
		if (height > 0 && source_preview.HeightRequest != height)
			source_preview.HeightRequest = height;
	}

	private void ResizeLeftPanel (Gtk.Widget panel, int width)
	{
		left_panel_width = Math.Clamp (width, min_left_panel_width, max_left_panel_width);
		panel.SetSizeRequest (left_panel_width, -1);
		source_section.SetSizeRequest (left_panel_width - 16, -1);
		source_preview_card?.SetSizeRequest (left_panel_width - 12, -1);
		source_preview_scroll?.SetSizeRequest (left_panel_width - 28, -1);
		UpdateSourcePreviewSize (left_panel_width - 28);
	}

	private void ResizeRightPanel (Gtk.Widget panel, int width)
	{
		right_panel_width = Math.Clamp (width, min_right_panel_width, max_right_panel_width);
		panel.SetSizeRequest (right_panel_width, -1);
	}

	private static void TogglePanel (Gtk.Widget panel, Gtk.Button toggle, bool left)
	{
		panel.Visible = !panel.Visible;
		toggle.IconName = GetPanelToggleIcon (left, panel.Visible);
		toggle.SetTooltipText (Translations.GetString (
			panel.Visible
				? left ? "Collapse left panel" : "Collapse right panel"
				: left ? "Expand left panel" : "Expand right panel"));
	}

	private static Gtk.Label CreateHeading (string text)
	{
		Gtk.Label label = Gtk.Label.New (text);
		label.Halign = Gtk.Align.Start;
		label.MarginStart = 8;
		label.MarginEnd = 8;
		label.MarginTop = 2;
		label.AddCssClass (AdwaitaStyles.Heading);
		return label;
	}

	// Vertical grid of label/spinner rows (used in source settings)
	private static Gtk.Grid CreateGrid (IReadOnlyList<(string Label, Gtk.SpinButton Input)> rows)
	{
		Gtk.Grid grid = Gtk.Grid.New ();
		grid.RowSpacing = 6;
		grid.ColumnSpacing = 8;
		for (int index = 0; index < rows.Count; index++) {
			Gtk.Label label = Gtk.Label.New (rows[index].Label);
			label.Halign = Gtk.Align.End;
			grid.Attach (label, 0, index, 1, 1);
			grid.Attach (rows[index].Input, 1, index, 1, 1);
		}
		return grid;
	}
}
