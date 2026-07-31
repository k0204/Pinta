using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal sealed partial class SpritesheetSplitDialog : IDisposable
{
	private const int max_frames = 256;
	private const long max_output_pixels = 64L * 1024 * 1024;
	private const string ai_source_mode = "ai";
	private const string grid_source_mode = "grid";
	private readonly UserLayer source;
	private readonly AI.SpritesheetAttemptInfo info;
	private readonly Action<SpritesheetSplitData> save_analysis;
	private readonly Gtk.Dialog dialog;
	private readonly Gtk.Widget submit;
	private readonly Gtk.SpinButton columns;
	private readonly Gtk.SpinButton rows;
	private readonly Gtk.SpinButton cell_width;
	private readonly Gtk.SpinButton cell_height;
	private readonly Gtk.SpinButton offset_x;
	private readonly Gtk.SpinButton offset_y;
	private readonly Gtk.SpinButton gap_x;
	private readonly Gtk.SpinButton gap_y;
	private readonly Gtk.SpinButton canvas_width;
	private readonly Gtk.SpinButton canvas_height;
	private readonly Gtk.CheckButton align_character;
	private readonly Adw.ViewStack source_mode_stack;
	private readonly Gtk.DrawingArea source_preview;
	private readonly Gtk.DrawingArea frame_preview;
	private readonly Gtk.ListBox frame_list;
	private readonly Gtk.SpinButton frame_x;
	private readonly Gtk.SpinButton frame_y;
	private readonly Gtk.Button previous_frame;
	private readonly Gtk.Button next_frame;
	private readonly Gtk.Button add_horizontal_guide;
	private readonly Gtk.Button add_vertical_guide;
	private readonly Gtk.Label validation_label;
	private readonly List<EditableFrame> frames = [];
	private int selected_frame;
	private bool syncing;
	private int drag_start_x;
	private int drag_start_y;

	public SpritesheetSplitDialog (
		Gtk.Window parent,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> saveAnalysis,
		SpritesheetSplitData? savedAnalysis)
	{
		this.source = source;
		this.info = info;
		this.analyze = analyze;
		save_analysis = saveAnalysis;
		dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Split Spritesheet");
		dialog.TransientFor = parent;
		dialog.Modal = true;
		dialog.DefaultWidth = 1180;
		dialog.DefaultHeight = 780;
		dialog.AddButton (Translations.GetString ("_Cancel"), (int) Gtk.ResponseType.Cancel);
		submit = dialog.AddButton (Translations.GetString ("Split"), (int) Gtk.ResponseType.Ok);
		submit.AddCssClass (AdwaitaStyles.SuggestedAction);

		columns = CreateSpinner (1, 32, info.Columns);
		rows = CreateSpinner (1, 32, info.Rows);
		cell_width = CreateSpinner (1, source.Surface.Width, source.Surface.Width / info.Columns);
		cell_height = CreateSpinner (1, source.Surface.Height, source.Surface.Height / info.Rows);
		offset_x = CreateSpinner (0, source.Surface.Width - 1, 0);
		offset_y = CreateSpinner (0, source.Surface.Height - 1, 0);
		gap_x = CreateSpinner (0, source.Surface.Width - 1, 0);
		gap_y = CreateSpinner (0, source.Surface.Height - 1, 0);
		canvas_width = CreateSpinner (1, 16384, cell_width.Value);
		canvas_height = CreateSpinner (1, 16384, cell_height.Value);
		align_character = Gtk.CheckButton.NewWithLabel (
			Translations.GetString ("Detect and align character registration"));
		align_character.Active = true;
		source_mode_stack = Adw.ViewStack.New ();

		source_preview = CreatePreview (260);
		frame_preview = CreatePreview (340);
		source_preview.SetDrawFunc ((_, context, width, height) => DrawSourcePreview (context, width, height));
		frame_preview.SetDrawFunc ((_, context, width, height) => DrawFramePreview (context, width, height));
		frame_list = Gtk.ListBox.New ();
		frame_list.SetSelectionMode (Gtk.SelectionMode.Single);
		frame_x = CreateSpinner (-16384, 16384, 0);
		frame_y = CreateSpinner (-16384, 16384, 0);
		previous_frame = CreateNavigationButton (Resources.StandardIcons.GoPrevious, Translations.GetString ("Previous frame"));
		next_frame = CreateNavigationButton (Resources.StandardIcons.GoNext, Translations.GetString ("Next frame"));
		add_horizontal_guide = Gtk.Button.NewWithLabel (Translations.GetString ("Horizontal guide"));
		add_vertical_guide = Gtk.Button.NewWithLabel (Translations.GetString ("Vertical guide"));
		validation_label = Gtk.Label.New (string.Empty);

		BuildContent ();
		ConnectEvents ();
		if (!TryRestoreAnalysis (savedAnalysis))
			RebuildFrames ();
		Refresh ();
	}

	public void Dispose () => dialog.Dispose ();

	public async Task<SpritesheetSplitData?> RunAsync ()
		=> await dialog.RunAsync () == Gtk.ResponseType.Ok ? ReadOptions () : null;

	private void BuildContent ()
	{
		Gtk.Box content = dialog.GetContentAreaBox ();
		Gtk.Box layout = Gtk.Box.New (Gtk.Orientation.Horizontal, 16);
		layout.Hexpand = true;
		layout.Vexpand = true;
		layout.SetAllMargins (12);
		layout.Append (BuildSettingsPanel ());
		layout.Append (BuildPreviewPanel ());
		layout.Append (BuildFramesPanel ());
		content.Append (layout);
	}

	private Gtk.Widget BuildSettingsPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 10);
		panel.WidthRequest = 260;
		panel.Append (CreateHeading (Translations.GetString ("Source extraction")));
		panel.Append (BuildSourceModeTabs ());
		panel.Append (Gtk.Separator.New (Gtk.Orientation.Horizontal));
		panel.Append (CreateHeading (Translations.GetString ("Output canvas")));
		panel.Append (CreateGrid ([
			(Translations.GetString ("Canvas width:"), canvas_width),
			(Translations.GetString ("Canvas height:"), canvas_height),
		]));
		validation_label.Wrap = true;
		validation_label.Halign = Gtk.Align.Start;
		panel.Append (validation_label);
		return panel;
	}

	private Gtk.Widget BuildPreviewPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		panel.Hexpand = true;
		panel.Vexpand = true;
		panel.Append (CreateHeading (Translations.GetString ("Source preview")));
		panel.Append (source_preview);
		panel.Append (CreateHeading (Translations.GetString ("Selected frame preview")));
		panel.Append (BuildRulerPreview ());
		panel.Append (BuildPreviewToolbar ());
		return panel;
	}

	private Gtk.Widget BuildFramesPanel ()
	{
		Gtk.Box panel = Gtk.Box.New (Gtk.Orientation.Vertical, 8);
		panel.WidthRequest = 300;
		panel.Append (CreateHeading (Translations.GetString ("Frames")));
		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.Vexpand = true;
		scroll.SetChild (frame_list);
		panel.Append (scroll);
		panel.Append (CreateHeading (Translations.GetString ("Frame position")));
		panel.Append (CreateGrid ([
			("X:", frame_x),
			("Y:", frame_y),
		]));
		Gtk.Label hint = Gtk.Label.New (Translations.GetString ("Drag the selected frame in the preview or enter its X/Y position."));
		hint.Wrap = true;
		hint.Halign = Gtk.Align.Start;
		hint.AddCssClass (AdwaitaStyles.DimLabel);
		panel.Append (hint);
		return panel;
	}

	private void ConnectEvents ()
	{
		foreach (Gtk.SpinButton spinner in new[] { cell_width, cell_height, offset_x, offset_y, gap_x, gap_y })
			spinner.OnValueChanged += (_, _) => ResetAnalysisAndRefresh ();
		foreach (Gtk.SpinButton spinner in new[] { canvas_width, canvas_height })
			spinner.OnValueChanged += (_, _) => ClampGuidesAndRefresh ();
		columns.OnValueChanged += (_, _) => ResetAnalysisAndRebuildFrames ();
		rows.OnValueChanged += (_, _) => ResetAnalysisAndRebuildFrames ();
		frame_list.OnRowSelected += (_, args) => SelectFrame (args.Row?.GetIndex () ?? 0);
		frame_x.OnValueChanged += (_, _) => UpdateSelectedPosition ();
		frame_y.OnValueChanged += (_, _) => UpdateSelectedPosition ();
		previous_frame.OnClicked += (_, _) => MoveFrameSelection (-1);
		next_frame.OnClicked += (_, _) => MoveFrameSelection (1);
		add_horizontal_guide.OnClicked += (_, _) => AddGuide (GuideOrientation.Horizontal);
		add_vertical_guide.OnClicked += (_, _) => AddGuide (GuideOrientation.Vertical);
		ConnectRulerAndGuidePointerEvents ();
		Adw.ViewStack.VisibleChildNamePropertyDefinition.Notify (
			source_mode_stack,
			(_, _) => ChangeSourceMode ());

		Gtk.GestureDrag drag = Gtk.GestureDrag.New ();
		drag.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		drag.OnDragBegin += (_, args) => BeginPreviewDrag (args.StartX, args.StartY);
		drag.OnDragUpdate += (_, args) => UpdatePreviewDrag (args.OffsetX, args.OffsetY);
		drag.OnDragEnd += (_, args) => EndPreviewDrag (args.OffsetX, args.OffsetY);
		frame_preview.AddController (drag);
	}

	private void RebuildFrames ()
		=> RebuildFrames (Math.Min ((int) (columns.Value * rows.Value), max_frames));

	private void RebuildFrames (int count)
	{
		while (frames.Count < count)
			frames.Add (new EditableFrame { Visible = frames.Count < ExpectedFrameCount });
		if (frames.Count > count)
			frames.RemoveRange (count, frames.Count - count);

		while (frame_list.GetRowAtIndex (0) is Gtk.ListBoxRow row)
			frame_list.Remove (row);
		for (int index = 0; index < frames.Count; index++)
			AppendFrameRow (index, frames[index]);

		if (frames.Count == 0) {
			selected_frame = 0;
			previous_frame.Sensitive = false;
			next_frame.Sensitive = false;
			Refresh ();
			return;
		}

		selected_frame = Math.Clamp (selected_frame, 0, frames.Count - 1);
		frame_list.SelectRow (frame_list.GetRowAtIndex (selected_frame));
		SelectFrame (selected_frame);
		Refresh ();
	}

	private void AppendFrameRow (int index, EditableFrame frame)
	{
		Gtk.CheckButton visible = Gtk.CheckButton.NewWithLabel (GetFrameLabel (index));
		visible.Active = frame.Visible;
		visible.Hexpand = true;
		visible.Halign = Gtk.Align.Fill;
		visible.OnToggled += (_, _) => {
			frame.Visible = visible.Active;
			frame_list.SelectRow (frame_list.GetRowAtIndex (index));
			Refresh ();
		};
		frame_list.Append (visible);
	}

	private void SelectFrame (int index)
	{
		if (frames.Count == 0)
			return;
		selected_frame = Math.Clamp (index, 0, frames.Count - 1);
		syncing = true;
		frame_x.Value = frames[selected_frame].X;
		frame_y.Value = frames[selected_frame].Y;
		syncing = false;
		previous_frame.Sensitive = selected_frame > 0;
		next_frame.Sensitive = selected_frame < frames.Count - 1;
		Refresh ();
	}

	private void MoveFrameSelection (int offset)
	{
		if (frames.Count == 0)
			return;
		Gtk.ListBoxRow? row = frame_list.GetRowAtIndex (Math.Clamp (selected_frame + offset, 0, frames.Count - 1));
		if (row is null)
			return;

		frame_list.SelectRow (row);
		row.GrabFocus ();
	}

	private void UpdateSelectedPosition ()
	{
		if (syncing || frames.Count == 0)
			return;
		frames[selected_frame].X = (int) frame_x.Value;
		frames[selected_frame].Y = (int) frame_y.Value;
		Refresh ();
	}

	private void DragSelectedFrame (double offsetX, double offsetY)
	{
		if (frames.Count == 0)
			return;
		double scale = GetPreviewScale (frame_preview.GetWidth (), frame_preview.GetHeight (), (int) canvas_width.Value, (int) canvas_height.Value);
		if (scale <= 0)
			return;
		frame_x.Value = drag_start_x + Math.Round (offsetX / scale);
		frame_y.Value = drag_start_y + Math.Round (offsetY / scale);
	}

	private void Refresh ()
	{
		bool valid = IsValid ();
		submit.Sensitive = valid;
		validation_label.SetText (GetValidationMessage (valid));
		validation_label.RemoveCssClass (AdwaitaStyles.Error);
		if (!valid && !(IsAiSourceMode && source_rectangles is null))
			validation_label.AddCssClass (AdwaitaStyles.Error);
		source_preview.QueueDraw ();
		frame_preview.QueueDraw ();
		horizontal_ruler.QueueDraw ();
		vertical_ruler.QueueDraw ();
	}

	private string GetValidationMessage (bool valid)
	{
		if (IsAiSourceMode && source_rectangles is null)
			return Translations.GetString ("Run AI analysis to detect sprites.");
		return valid
			? Translations.GetString ("{0} sprites will be created.", frames.Count)
			: Translations.GetString ("The grid exceeds the source image, contains more than 256 cells, or the output canvases are too large.");
	}

	private bool IsValid ()
	{
		if (IsAiSourceMode && source_rectangles is null)
			return false;
		if (source_rectangles is not null)
			return source_rectangles.Count == frames.Count
				&& frames.Count * (long) canvas_width.Value * (long) canvas_height.Value <= max_output_pixels;
		long right = (long) offset_x.Value + (long) columns.Value * (long) cell_width.Value + (long) (columns.Value - 1) * (long) gap_x.Value;
		long bottom = (long) offset_y.Value + (long) rows.Value * (long) cell_height.Value + (long) (rows.Value - 1) * (long) gap_y.Value;
		return columns.Value * rows.Value <= max_frames
			&& right <= source.Surface.Width
			&& bottom <= source.Surface.Height
			&& frames.Count * (long) canvas_width.Value * (long) canvas_height.Value <= max_output_pixels;
	}

	private bool IsAiSourceMode => source_mode_stack.VisibleChildName == ai_source_mode;

	private SpritesheetSplitData ReadOptions ()
		=> new (
			(int) columns.Value, (int) rows.Value, (int) cell_width.Value, (int) cell_height.Value,
			(int) offset_x.Value, (int) offset_y.Value, (int) gap_x.Value, (int) gap_y.Value,
			(int) canvas_width.Value, (int) canvas_height.Value, align_character.Active,
			[.. frames.Select (frame => new SpritesheetFrameSplit (frame.X, frame.Y, frame.Visible))],
			source_rectangles);

	private void DrawSourcePreview (Context context, int width, int height)
	{
		double scale = GetPreviewScale (width, height, source.Surface.Width, source.Surface.Height);
		if (scale <= 0)
			return;
		double left = (width - source.Surface.Width * scale) / 2;
		double top = (height - source.Surface.Height * scale) / 2;
		context.Translate (left, top);
		context.Scale (scale, scale);
		context.SetSourceSurface (source.Surface, 0, 0);
		context.Paint ();

		for (int cell = 0; cell < frames.Count; cell++) {
			RectangleD bounds = GetCellBounds (cell);
			Color color = cell == selected_frame ? new (1, 0.25, 0.1, 0.95) : new (0.1, 0.75, 1, 0.8);
			context.DrawRectangle (bounds, color, Math.Max (1, (int) Math.Ceiling (2 / scale)));
		}
	}

	private void DrawFramePreview (Context context, int width, int height)
	{
		int canvasWidth = (int) canvas_width.Value;
		int canvasHeight = (int) canvas_height.Value;
		double scale = GetPreviewScale (width, height, canvasWidth, canvasHeight);
		if (scale <= 0)
			return;
		double left = (width - canvasWidth * scale) / 2;
		double top = (height - canvasHeight * scale) / 2;
		context.Translate (left, top);
		context.Scale (scale, scale);
		DrawCheckerboard (context, canvasWidth, canvasHeight, scale);

		if (frames.Count > 0 && frames[selected_frame].Visible) {
			EditableFrame frame = frames[selected_frame];
			using ImageSurface crop = LayerActions.CreateSplitFrameSurface (source, info, ReadOptions (), selected_frame);
			context.Save ();
			context.Rectangle (0, 0, canvasWidth, canvasHeight);
			context.Clip ();
			context.SetSourceSurface (crop, frame.X, frame.Y);
			context.Paint ();
			context.Restore ();
		}
		context.DrawRectangle (new RectangleD (0, 0, canvasWidth, canvasHeight), new Color (0.25, 0.25, 0.25), Math.Max (1, (int) Math.Ceiling (1 / scale)));
		DrawPreviewGuides (context, scale, canvasWidth, canvasHeight);
	}

	private RectangleD GetCellBounds (int cell)
	{
		if (source_rectangles is not null) {
			RectangleI bounds = source_rectangles[cell];
			return new (bounds.X, bounds.Y, bounds.Width, bounds.Height);
		}
		int column = cell % (int) columns.Value;
		int row = cell / (int) columns.Value;
		return new (
			offset_x.Value + column * (cell_width.Value + gap_x.Value),
			offset_y.Value + row * (cell_height.Value + gap_y.Value),
			cell_width.Value,
			cell_height.Value);
	}

	private string GetFrameLabel (int index)
	{
		if (index >= ExpectedFrameCount)
			return Translations.GetString ("Cell {0} (extra)", index + 1);
		int direction = index / info.FrameCount;
		int frame = index % info.FrameCount;
		return $"{index + 1}: {info.DirectionIds[direction]} / {Translations.GetString ("Frame {0}", frame + 1)}";
	}

	private int ExpectedFrameCount => info.DirectionIds.Count * info.FrameCount;

	private static Gtk.SpinButton CreateSpinner (double minimum, double maximum, double value)
	{
		Gtk.SpinButton spinner = Gtk.SpinButton.NewWithRange (minimum, maximum, 1);
		spinner.Value = Math.Clamp (value, minimum, maximum);
		return spinner;
	}

	private static Gtk.DrawingArea CreatePreview (int height)
	{
		Gtk.DrawingArea preview = Gtk.DrawingArea.New ();
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

	private static Gtk.Label CreateHeading (string text)
	{
		Gtk.Label label = Gtk.Label.New (text);
		label.Halign = Gtk.Align.Start;
		label.AddCssClass (AdwaitaStyles.Heading);
		return label;
	}

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

	private static double GetPreviewScale (int width, int height, int contentWidth, int contentHeight)
		=> Math.Max (0, Math.Min ((width - 24) / (double) contentWidth, (height - 24) / (double) contentHeight));

	private static void DrawCheckerboard (Context context, int width, int height, double scale)
	{
		context.FillRectangle (new RectangleD (0, 0, width, height), new Color (0.92, 0.92, 0.92));
		int size = Math.Max (1, (int) Math.Ceiling (12 / scale));
		for (int y = 0; y < height; y += size)
			for (int x = (y / size % 2) * size; x < width; x += size * 2)
				context.FillRectangle (new RectangleD (x, y, Math.Min (size, width - x), Math.Min (size, height - y)), new Color (0.76, 0.76, 0.76));
	}

	private sealed class EditableFrame
	{
		public int X { get; set; }
		public int Y { get; set; }
		public bool Visible { get; set; }
	}
}
