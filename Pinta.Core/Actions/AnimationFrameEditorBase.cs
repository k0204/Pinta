using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cairo;

namespace Pinta.Core;

internal abstract partial class AnimationFrameEditorBase
{
	private const int max_frames = 256;
	private const long max_output_pixels = 64L * 1024 * 1024;
	private const string ai_source_mode = "ai";
	private const string grid_source_mode = "grid";
	private readonly UserLayer source;
	protected readonly AI.SpritesheetAttemptInfo info;
	private readonly IReadOnlyList<ImageSurface>? frame_surfaces;
	private readonly bool editing_existing_frames;
	private readonly bool allow_ai_analysis;
	private readonly IReadOnlyList<UserLayer> output_attempts;
	private readonly Action<SpritesheetSplitData> save_analysis;
	private readonly Gtk.Window host_window;
	private readonly Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
	private readonly Action<bool> set_submit_sensitive;
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
	private readonly Gtk.ComboBoxText output_attempt;
	private readonly Adw.ViewStack source_mode_stack;
	private readonly Gtk.DrawingArea source_preview;
	private readonly Gtk.DrawingArea frame_preview;
	private readonly Gtk.ListBox frame_list;
	private readonly Gtk.SpinButton frame_x;
	private readonly Gtk.SpinButton frame_y;
	private readonly Gtk.Button previous_frame;
	private readonly Gtk.Button next_frame;
	private readonly Gtk.Button undo_position;
	private readonly Gtk.Button redo_position;
	private readonly Gtk.CheckButton move_root;
	private readonly Gtk.CheckButton previous_frame_reference;
	private readonly Gtk.Scale previous_frame_opacity;
	private readonly Gtk.Label validation_label;
	private readonly Gtk.Label sprite_name_label;
	private readonly List<EditableFrame> frames = [];
	private int[] frame_display_order = [];
	private int selected_frame;
	private bool syncing;
	private int root_dx;
	private int root_dy;
	private int drag_start_x;
	private int drag_start_y;

	protected AnimationFrameEditorBase (
		Gtk.Window hostWindow,
		Action<bool> setSubmitSensitive,
		UserLayer source,
		AI.SpritesheetAttemptInfo info,
		IReadOnlyList<UserLayer> outputAttempts,
		Func<string, Task<AI.SpriteSegmentationAnalysis>> analyze,
		Action<SpritesheetSplitData> saveAnalysis,
		SpritesheetSplitData? savedAnalysis,
		IReadOnlyList<ImageSurface>? frameSurfaces = null,
		IReadOnlyList<SpritesheetFrameSplit>? existingFrames = null,
		bool allowAiAnalysis = true)
	{
		host_window = hostWindow;
		set_submit_sensitive = setSubmitSensitive;
		content.Hexpand = true;
		content.Vexpand = true;
		this.source = source;
		this.info = info;
		frame_surfaces = frameSurfaces;
		editing_existing_frames = frameSurfaces is not null;
		allow_ai_analysis = allowAiAnalysis;
		output_attempts = outputAttempts;
		this.analyze = analyze;
		save_analysis = saveAnalysis;
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
		output_attempt = CreateOutputAttemptCombo ();
		source_mode_stack = Adw.ViewStack.New ();

		source_preview = CreatePreview (0);
		source_preview.Vexpand = true;
		source_preview.Valign = Gtk.Align.Fill;
		frame_preview = CreatePreview (0);
		frame_preview.Vexpand = true;
		frame_preview.Focusable = true;
		source_preview.SetDrawFunc ((_, context, width, height) => DrawSourcePreview (context, width, height));
		frame_preview.SetDrawFunc ((_, context, width, height) => DrawFramePreview (context, width, height));
		frame_list = Gtk.ListBox.New ();
		frame_list.SetSelectionMode (Gtk.SelectionMode.Single);
		frame_x = CreateSpinner (-16384, 16384, 0);
		frame_y = CreateSpinner (-16384, 16384, 0);
		previous_frame = CreateNavigationButton (Resources.StandardIcons.GoPrevious, Translations.GetString ("Previous frame"));
		next_frame = CreateNavigationButton (Resources.StandardIcons.GoNext, Translations.GetString ("Next frame"));
		undo_position = CreateNavigationButton (Resources.StandardIcons.EditUndo, Translations.GetString ("Undo frame position"));
		redo_position = CreateNavigationButton (Resources.StandardIcons.EditRedo, Translations.GetString ("Redo frame position"));
		move_root = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Move root anchor"));
		move_root.SetTooltipText (Translations.GetString ("Shift the root anchor 30 pixels up by default; drag the anchor cross in the preview to adjust it."));
		previous_frame_reference = Gtk.CheckButton.NewWithLabel (
			Translations.GetString ("Previous Frame Reference"));
		previous_frame_reference.SetTooltipText (Translations.GetString (
			"Show the previous frame on the canvas for reference only. It cannot be edited."));
		previous_frame_opacity = Gtk.Scale.NewWithRange (Gtk.Orientation.Horizontal, 0, 100, 1);
		previous_frame_opacity.SetValue (70);
		previous_frame_opacity.Digits = 0;
		previous_frame_opacity.DrawValue = true;
		previous_frame_opacity.Hexpand = true;
		previous_frame_opacity.SetTooltipText (Translations.GetString (
			"Adjust the opacity of the previous frame reference."));
		previous_frame_opacity.Sensitive = false;
		validation_label = Gtk.Label.New (string.Empty);
		sprite_name_label = Gtk.Label.New (string.Empty);
		sprite_name_label.AddCssClass (AdwaitaStyles.Title4);

		BuildContent ();
		ConnectEvents ();
		if (editing_existing_frames) {
			source_section.Visible = false;
			if (attempt_section is not null)
				attempt_section.Visible = false;
		}
		if (!TryRestoreAnalysis (savedAnalysis)) {
			if (editing_existing_frames)
				frames.AddRange ((existingFrames ?? []).Select (frame => new EditableFrame {
					X = frame.X,
					Y = frame.Y,
					Visible = frame.Visible,
				}));
			RebuildFrames ();
		}
		Refresh ();
	}

	private void ConnectEvents ()
	{
		foreach (Gtk.SpinButton spinner in new[] { cell_width, cell_height, offset_x, offset_y, gap_x, gap_y })
			spinner.OnValueChanged += (_, _) => ResetAnalysisAndRefresh ();
		foreach (Gtk.SpinButton spinner in new[] { canvas_width, canvas_height })
			spinner.OnValueChanged += (_, _) => {
				RepositionFramesAroundAnchor ();
				ClampGuidesAndRefresh ();
			};
		output_attempt.OnChanged += (_, _) => Refresh ();
		columns.OnValueChanged += (_, _) => ResetAnalysisAndRebuildFrames ();
		rows.OnValueChanged += (_, _) => ResetAnalysisAndRebuildFrames ();
		frame_list.OnRowSelected += (_, args) => SelectFrame (GetFrameIndex (args.Row));
		frame_x.OnValueChanged += (_, _) => UpdateSelectedPosition ();
		frame_y.OnValueChanged += (_, _) => UpdateSelectedPosition ();
		previous_frame.OnClicked += (_, _) => MoveFrameSelection (-1);
		next_frame.OnClicked += (_, _) => MoveFrameSelection (1);
		undo_position.OnClicked += (_, _) => UndoFramePosition ();
		redo_position.OnClicked += (_, _) => RedoFramePosition ();
		move_root.OnToggled += (_, _) => {
			root_dx = 0;
			root_dy = move_root.Active ? -30 : 0;
			RepositionFramesAroundAnchor ();
			Refresh ();
		};
		previous_frame_reference.OnToggled += (_, _) => {
			previous_frame_opacity.Sensitive = previous_frame_reference.Active;
			frame_preview.QueueDraw ();
		};
		previous_frame_opacity.OnValueChanged += (_, _) => frame_preview.QueueDraw ();
		ConnectRulerAndGuidePointerEvents ();

		Gtk.EventControllerKey keys = Gtk.EventControllerKey.New ();
		keys.PropagationPhase = Gtk.PropagationPhase.Capture;
		keys.OnKeyPressed += HandlePositionHistoryKeyPressed;
		keys.OnKeyPressed += HandlePreviewSpaceKeyPressed;
		keys.OnKeyReleased += HandlePreviewSpaceKeyReleased;
		content.AddController (keys);
		Adw.ViewStack.VisibleChildNamePropertyDefinition.Notify (
			source_mode_stack,
			(_, _) => ChangeSourceMode ());
		source_preview.OnResize += (_, args) => UpdateSourcePreviewSize (args.Width);

		Gtk.GestureClick source_click = Gtk.GestureClick.New ();
		source_click.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		source_click.OnPressed += (_, args) => SelectSourceFrame (args.X, args.Y);
		source_preview.AddController (source_click);

		Gtk.GestureDrag drag = Gtk.GestureDrag.New ();
		drag.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		drag.OnDragBegin += (_, args) => BeginPreviewDrag (args.StartX, args.StartY);
		drag.OnDragUpdate += (_, args) => UpdatePreviewDrag (
		args.OffsetX,
		args.OffsetY,
		drag.GetCurrentEventState ().IsShiftPressed ());
		drag.OnDragEnd += (_, args) => EndPreviewDrag (
		args.OffsetX,
		args.OffsetY,
		drag.GetCurrentEventState ().IsShiftPressed ());
		frame_preview.AddController (drag);

		Gtk.EventControllerScroll scroll = Gtk.EventControllerScroll.New (Gtk.EventControllerScrollFlags.BothAxes);
		scroll.OnScroll += HandlePreviewScroll;
		frame_preview.AddController (scroll);

		Gtk.GestureDrag pan = Gtk.GestureDrag.New ();
		pan.SetButton (GtkExtensions.MOUSE_MIDDLE_BUTTON);
		pan.OnDragBegin += (_, _) => BeginPreviewPan ();
		pan.OnDragUpdate += (_, args) => UpdatePreviewPan (args.OffsetX, args.OffsetY);
		pan.OnDragEnd += (_, _) => EndPreviewPan ();
		frame_preview.AddController (pan);

		Gtk.GestureClick preview_click = Gtk.GestureClick.New ();
		preview_click.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		preview_click.OnPressed += async (_, args) => {
			if (args.NPress == 2 && FindGuideAtPoint (args.X, args.Y) is GuideDragState state) {
				drag.SetState (Gtk.EventSequenceState.Denied);
				CancelPreviewDrag ();
				preview_click.SetState (Gtk.EventSequenceState.Claimed);
				await EditGuidePositionAsync (state.Index);
				return;
			}

			frame_preview.GrabFocus ();
		};
		frame_preview.AddController (preview_click);

		Gtk.EventControllerKey preview_keys = Gtk.EventControllerKey.New ();
		preview_keys.PropagationPhase = Gtk.PropagationPhase.Capture;
		preview_keys.OnKeyPressed += HandlePreviewSpaceKeyPressed;
		preview_keys.OnKeyReleased += HandlePreviewSpaceKeyReleased;
		preview_keys.OnKeyPressed += HandlePreviewArrowKeyPressed;
		frame_preview.AddController (preview_keys);
	}

	private void RebuildFrames ()
		=> RebuildFrames (Math.Min ((int) (columns.Value * rows.Value), max_frames));

	private void RebuildFrames (int count)
	{
		ClearPositionHistory ();
		while (frames.Count < count)
			frames.Add (new EditableFrame { Visible = frames.Count < ExpectedFrameCount });
		if (frames.Count > count)
			frames.RemoveRange (count, frames.Count - count);

		while (frame_list.GetRowAtIndex (0) is Gtk.ListBoxRow row)
			frame_list.Remove (row);
		frame_display_order = CreateFrameNavigationOrder (frames.Count);
		for (int displayIndex = 0; displayIndex < frame_display_order.Length; displayIndex++) {
			int frameIndex = frame_display_order[displayIndex];
			AppendFrameRow (displayIndex, frameIndex, frames[frameIndex]);
		}

		if (frames.Count == 0) {
			selected_frame = 0;
			Refresh ();
			return;
		}

		selected_frame = Math.Clamp (selected_frame, 0, frames.Count - 1);
		if (!IsAiSourceMode && !editing_existing_frames)
			ApplyGridPlacement ();
		frame_list.SelectRow (frame_list.GetRowAtIndex (GetDisplayIndex (selected_frame)));
		SelectFrame (selected_frame);
		Refresh ();
	}

	private void AppendFrameRow (int displayIndex, int frameIndex, EditableFrame frame)
	{
		Gtk.CheckButton visible = Gtk.CheckButton.NewWithLabel (GetFrameLabel (displayIndex, frameIndex));
		visible.Active = frame.Visible;
		visible.Hexpand = true;
		visible.Halign = Gtk.Align.Fill;
		visible.OnToggled += (_, _) => {
			frame.Visible = visible.Active;
			if (visible.Active)
				frame_list.SelectRow (frame_list.GetRowAtIndex (displayIndex));
			Refresh ();
		};
		frame_list.Append (visible);
	}

	private int GetFrameIndex (Gtk.ListBoxRow? row)
	{
		int displayIndex = row?.GetIndex () ?? 0;
		return displayIndex >= 0 && displayIndex < frame_display_order.Length
			? frame_display_order[displayIndex]
			: 0;
	}

	private int GetDisplayIndex (int frameIndex)
	{
		int displayIndex = Array.IndexOf (frame_display_order, frameIndex);
		return displayIndex >= 0 ? displayIndex : 0;
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
		UpdateSpriteNameLabel ();
		Refresh ();
	}

	public Gtk.Widget Content => content;

	private void SelectSourceFrame (double x, double y)
	{
		if (frames.Count == 0)
			return;
		double scale = GetSourcePreviewScale (source_preview.GetWidth ());
		if (scale <= 0)
			return;

		double sourceX = x / scale;
		double sourceY = y / scale;
		for (int index = 0; index < frames.Count; index++) {
			RectangleD bounds = GetCellBounds (index);
			if (sourceX < bounds.Left || sourceX > bounds.Right
				|| sourceY < bounds.Top || sourceY > bounds.Bottom)
				continue;

			Gtk.ListBoxRow? row = frame_list.GetRowAtIndex (GetDisplayIndex (index));
			if (row is null) {
				SelectFrame (index);
				return;
			}
			frame_list.SelectRow (row);
			row.GrabFocus ();
			return;
		}
	}

	private int FindVisibleFrame (int from, int direction)
	{
		if (frames.Count == 0)
			return -1;

		int[] order = CreateFrameNavigationOrder (frames.Count);
		int position = Array.IndexOf (order, from);
		if (position < 0)
			return -1;

		for (int count = 0; count < order.Length; count++) {
			position = (position + direction + order.Length) % order.Length;
			int index = order[position];
			if (frames[index].Visible)
				return index;
		}
		return -1;
	}

	private void MoveFrameSelection (int offset)
	{
		if (frames.Count == 0)
			return;
		int target = FindVisibleFrame (selected_frame, offset);
		if (target < 0 || target == selected_frame)
			return;
		Gtk.ListBoxRow? row = frame_list.GetRowAtIndex (GetDisplayIndex (target));
		if (row is null)
			return;

		frame_list.SelectRow (row);
		row.GrabFocus ();
	}

	private void UpdateSelectedPosition ()
	{
		if (syncing || frames.Count == 0)
			return;
		EditableFrame frame = frames[selected_frame];
		int x = (int) frame_x.Value;
		int y = (int) frame_y.Value;
		int oldX = frame.X;
		int oldY = frame.Y;
		ApplyFramePosition (selected_frame, x, y);
		if (!frame_position_dragging)
			RecordPositionChange (selected_frame, oldX, oldY, x, y);
	}

	private void RepositionFramesAroundAnchor ()
	{
		double rootX = canvas_width.Value / 2.0 + root_dx;
		double rootY = canvas_height.Value + root_dy;
		foreach (EditableFrame frame in frames) {
			if (frame.AnchorX is not double anchorX || frame.AnchorY is not double anchorY)
				continue;
			frame.X = (int) Math.Round (rootX - anchorX);
			frame.Y = (int) Math.Round (rootY - anchorY);
		}

		if (frames.Count == 0)
			return;
		syncing = true;
		frame_x.Value = frames[selected_frame].X;
		frame_y.Value = frames[selected_frame].Y;
		syncing = false;
	}

	private void ApplyGridPlacement ()
	{
		for (int index = 0; index < frames.Count; index++) {
			RectangleD bounds = GetCellBounds (index);
			frames[index].AnchorX = bounds.Width / 2.0;
			frames[index].AnchorY = bounds.Height;
		}
		RepositionFramesAroundAnchor ();
	}

	private void DragSelectedFrame (double offsetX, double offsetY)
	{
		if (frames.Count == 0)
			return;
		double scale = GetFramePreviewTransform ().Scale;
		if (scale <= 0)
			return;
		frame_x.Value = drag_start_x + Math.Round (offsetX / scale);
		frame_y.Value = drag_start_y + Math.Round (offsetY / scale);
	}

	private void Refresh ()
	{
		bool valid = IsValid ();
		set_submit_sensitive (valid);
		bool canLoop = frames.Count (frame => frame.Visible) > 1;
		previous_frame.Sensitive = canLoop;
		next_frame.Sensitive = canLoop;
		UpdatePositionHistoryButtons ();
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
		if (!OutputCanvasMatchesTarget ())
			return Translations.GetString ("The output canvas must match the frames already in the selected attempt.");
		return valid
			? Translations.GetString ("{0} sprites will be created.", frames.Count)
			: Translations.GetString ("The grid exceeds the source image, contains more than 256 cells, or the output canvases are too large.");
	}

	private bool IsValid ()
	{
		if (!OutputCanvasMatchesTarget ())
			return false;
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

	public SpritesheetSplitData ReadOptions ()
		=> new (
			(int) columns.Value, (int) rows.Value, (int) cell_width.Value, (int) cell_height.Value,
			(int) offset_x.Value, (int) offset_y.Value, (int) gap_x.Value, (int) gap_y.Value,
			(int) canvas_width.Value, (int) canvas_height.Value, align_character.Active,
			[.. frames.Select (frame => new SpritesheetFrameSplit (frame.X, frame.Y, frame.Visible))],
			source_rectangles);

	private void DrawSourcePreview (Context context, int width, int height)
	{
		double scale = GetSourcePreviewScale (width);
		if (scale <= 0)
			return;
		double left = (width - source.Surface.Width * scale) / 2;
		double top = 0;
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
		(double scale, double left, double top) = GetFramePreviewTransform (width, height);
		if (scale <= 0)
			return;
		context.Translate (left, top);
		context.Scale (scale, scale);
		DrawCheckerboard (context, canvasWidth, canvasHeight, scale);

		int referenceFrame = previous_frame_reference.Active
			? FindVisibleFrame (selected_frame, -1)
			: -1;
		if (referenceFrame >= 0 && referenceFrame != selected_frame)
			DrawPreviewFrameSurface (context, referenceFrame, canvasWidth, canvasHeight, previous_frame_opacity.GetValue () / 100);
		DrawPreviewFrameSurface (context, selected_frame, canvasWidth, canvasHeight);
		context.DrawRectangle (new RectangleD (0, 0, canvasWidth, canvasHeight), new Color (0.25, 0.25, 0.25), Math.Max (1, (int) Math.Ceiling (1 / scale)));
		DrawAnchorMarker (context, canvasWidth / 2.0 + root_dx, canvasHeight + root_dy);
		DrawPreviewGuides (context, scale, canvasWidth, canvasHeight);
	}

	private void DrawPreviewFrameSurface (Context context, int frameIndex, int canvasWidth, int canvasHeight, double opacity = 1)
	{
		if (frameIndex < 0 || frameIndex >= frames.Count || !frames[frameIndex].Visible)
			return;

		EditableFrame frame = frames[frameIndex];
		using ImageSurface surface = CreatePreviewFrameSurface (frameIndex);
		context.Save ();
		context.Rectangle (0, 0, canvasWidth, canvasHeight);
		context.Clip ();
		context.SetSourceSurface (surface, frame.X, frame.Y);
		if (opacity < 1)
			context.PaintWithAlpha (opacity);
		else
			context.Paint ();
		context.Restore ();
	}

	private ImageSurface CreatePreviewFrameSurface (int frame)
		=> frame_surfaces is not null && frame < frame_surfaces.Count
			? frame_surfaces[frame].Clone ()
			: LayerActions.CreateSplitFrameSurface (source, info, ReadOptions (), frame);

	private static void DrawAnchorMarker (Context context, double x, double y)
	{
		const double size = 8;
		context.Save ();
		context.SetSourceColor (new Color (0.9, 0.1, 0.1, 0.95));
		context.LineWidth = 2;
		context.MoveTo (x - size, y);
		context.LineTo (x + size, y);
		context.MoveTo (x, y - size);
		context.LineTo (x, y + size);
		context.Stroke ();
		context.Restore ();
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

	protected abstract int ExpectedFrameCount { get; }

	protected abstract int[] CreateFrameNavigationOrder (int frameCount);

	protected abstract string GetFrameLabel (int displayIndex, int index);

	private void UpdateSpriteNameLabel ()
	{
		if (frames.Count == 0 || selected_frame < 0 || selected_frame >= frames.Count) {
			sprite_name_label.SetText (string.Empty);
			return;
		}
		int displayIndex = GetDisplayIndex (selected_frame);
		sprite_name_label.SetText (GetFrameLabel (displayIndex, selected_frame));
	}

	private static double GetPreviewScale (int width, int height, int contentWidth, int contentHeight)
		=> Math.Max (0, Math.Min (width / (double) contentWidth, height / (double) contentHeight));

	private double GetSourcePreviewScale (int width)
		=> width > 0 ? width / (double) source.Surface.Width : 0;

	private int GetSourcePreviewHeight (int width)
	{
		double scale = GetSourcePreviewScale (width);
		return scale > 0
			? Math.Max (1, (int) Math.Ceiling (source.Surface.Height * scale))
			: 0;
	}

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
		public double? AnchorX { get; set; }
		public double? AnchorY { get; set; }
		public bool Visible { get; set; }
	}
}
