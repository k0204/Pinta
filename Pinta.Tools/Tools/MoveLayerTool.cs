//
// MoveLayerTool.cs
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
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Pinta.Core;

namespace Pinta.Tools;

public sealed partial class MoveLayerTool : BaseTool
{
	private Gtk.CheckButton? auto_select_layer;
	private Gtk.CheckButton? show_transform_controls;
	private readonly List<UserLayer> dragged_layers = [];
	private readonly List<UserLayer> resized_layers = [];
	private readonly Dictionary<Layer, Matrix> resize_initial_transforms = [];
	private readonly Gdk.Cursor marquee_cursor;
	private PointD drag_start_point;
	private PointD applied_drag_delta;
	private RectangleD drag_start_bounds;
	private bool has_drag_start_bounds;
	private PointD last_window_point;
	private bool marquee_active;
	private bool shift_pressed;
	private PointD marquee_start_point;
	private RectangleD marquee_rectangle;
	private RectangleD resize_start_bounds;
	private RectangleD resize_current_bounds;

	private readonly IWorkspaceService workspace;
	private readonly SystemManager system_manager;
	private readonly RectangleHandle transform_handle;

	public MoveLayerTool (IServiceProvider services) : base (services)
	{
		workspace = services.GetService<IWorkspaceService> ();
		system_manager = services.GetService<SystemManager> ();
		marquee_cursor = Gdk.Cursor.NewFromTexture (Resources.GetIcon ("Cursor.RectangleSelect.png"), 9, 18, null);
		transform_handle = new (workspace) {
			DrawOutline = true,
			InvertIfNegative = true,
			PreserveAspectRatio = true,
		};
	}

	public override string Name => Translations.GetString ("Move Layer");
	public override string Icon => Pinta.Resources.Icons.ToolMove;
	// Translators: {0} is 'Ctrl', or a platform-specific key such as 'Command' on macOS.
	public override string StatusBarText => Translations.GetString (
		"Left click and drag to move the layer." +
		"\nHold Shift while dragging a transform handle to resize proportionally." +
		"\nHold Shift or drag on empty canvas to select multiple layers." +
		"\nUse arrow keys to move the layer by a single pixel." +
		"\nHold {0} while using arrow keys to move ten pixels.",
		system_manager.CtrlLabel ());

	public override Gdk.Cursor DefaultCursor => Gdk.Cursor.NewFromTexture (Resources.GetIcon (Pinta.Resources.Icons.ToolMoveCursor), 0, 0, null);
	public override Gdk.Key ShortcutKey => new (Gdk.Constants.KEY_V);
	public override int Priority => 5;
	public override bool RequiresEditableLayer => false;

	protected override void OnBuildToolBar (Gtk.Box toolbar)
	{
		auto_select_layer ??= Gtk.CheckButton.NewWithLabel (Translations.GetString ("Auto-select layer"));
		auto_select_layer.Active = Settings.GetSetting (SettingNames.MOVE_LAYER_AUTO_SELECT_LAYER, false);
		toolbar.Append (auto_select_layer);

		if (show_transform_controls is null) {
			show_transform_controls = Gtk.CheckButton.NewWithLabel (Translations.GetString ("Show transform controls"));
			show_transform_controls.Active = Settings.GetSetting (SettingNames.SHOW_TRANSFORM_CONTROLS, true);
			show_transform_controls.OnToggled += HandleTransformControlsToggled;
		}

		toolbar.Append (show_transform_controls);
		base.OnBuildToolBar (toolbar);
	}

	protected override void OnActivated (Document? document)
	{
		base.OnActivated (document);
		workspace.ActiveDocumentChanged += HandleTransformTargetChanged;
		workspace.SelectedLayerChanged += HandleTransformTargetChanged;
		workspace.LayerTreeChanged += HandleTransformTargetChanged;
		workspace.LayerPropertyChanged += HandleSmartGuideLayerPropertyChanged;
		InvalidateSmartGuideCandidates ();
		UpdateTransformHandle (document);
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		shift_pressed = false;
		FinishResize (document);
		FinishLayerDrag (document);
		FinishLayerMarquee (document);
		workspace.ActiveDocumentChanged -= HandleTransformTargetChanged;
		workspace.SelectedLayerChanged -= HandleTransformTargetChanged;
		workspace.LayerTreeChanged -= HandleTransformTargetChanged;
		workspace.LayerPropertyChanged -= HandleSmartGuideLayerPropertyChanged;
		document?.FinishSelection ();
		base.OnDeactivated (document, newTool);
	}

	protected override void OnAfterUndo (Document document)
	{
		base.OnAfterUndo (document);
		UpdateTransformHandle (document);
	}

	protected override void OnAfterRedo (Document document)
	{
		base.OnAfterRedo (document);
		UpdateTransformHandle (document);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		if (e.MouseButton != MouseButton.Left)
			return;

		last_window_point = e.WindowPoint;

		// Dragging a transform handle takes priority over Shift-marquee selection, so that
		// Shift + dragging a corner/edge handle resizes the layer proportionally.
		if (StartResize (document, e))
			return;

		bool select_multiple = shift_pressed || e.IsShiftPressed;
		if (select_multiple) {
			StartLayerMarquee (document, e.PointDouble);
			return;
		}

		if (!document.Workspace.PointInCanvas (e.PointDouble)) {
			document.Layers.ClearCurrentUserLayer ();
			return;
		}

		UserLayer? layer = document.Layers.FindTopmostLayerAtPoint (e.PointDouble);
		if (layer is null) {
			StartLayerMarquee (document, e.PointDouble);
			return;
		}

		if (auto_select_layer?.Active == true) {
			if (layer != document.Layers.CurrentUserLayer
				&& !document.Layers.SelectedUserLayers.Contains (layer)) {
				document.Layers.SetCurrentUserLayer (layer);
				document.ResetSelectionPaths ();
				document.Workspace.Invalidate ();
			}

			if (!layer.CanMoveOnCanvas)
				return;
		}

		if (!document.Layers.HasSelectedLayer || !document.Layers.CurrentUserLayer.CanMoveOnCanvas)
			return;

		StartLayerDrag (document, e.PointDouble);
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		last_window_point = e.WindowPoint;

		if (resized_layers.Count > 0) {
			UpdateResize (document, e);
			return;
		}

		if (marquee_active) {
			UpdateLayerMarquee (document, e.PointDouble);
			return;
		}

		if (dragged_layers.Count == 0) {
			UpdateCursor (e.WindowPoint, e.IsShiftPressed);
			return;
		}

		PointD totalDelta = GetDragDelta (e.PointDouble);
		if (has_drag_start_bounds)
			totalDelta = ApplySmartGuideSnap (document, totalDelta);
		PointD delta = totalDelta - applied_drag_delta;
		foreach (UserLayer layer in dragged_layers)
			document.Layers.TranslateLayerTree (layer, delta);
		applied_drag_delta = totalDelta;
		UpdateTransformHandle (document);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		last_window_point = e.WindowPoint;

		if (resized_layers.Count > 0) {
			FinishResize (document);
			return;
		}

		if (marquee_active) {
			FinishLayerMarquee (document, e.PointDouble);
			UpdateCursor (e.WindowPoint);
			return;
		}

		if (dragged_layers.Count == 0)
			return;

		FinishLayerDrag (document);
	}

	protected override bool OnKeyDown (Document document, ToolKeyEventArgs e)
	{
		if (IsShiftKey (e.Key)) {
			shift_pressed = true;
			SetCursor (marquee_cursor);
			return true;
		}

		IReadOnlyList<UserLayer> layers = document.Layers.GetSelectedLayerRoots ();
		if (layers.Count == 0 || layers.Any (layer => !layer.CanMoveOnCanvas))
			return false;

		PointD delta = GetKeyDelta (e);
		if (delta == PointD.Zero)
			return false;

		if (document.Layers.TryGetSelectedLayerTreeBounds (out RectangleD bounds))
			delta = ClampMoveDelta (document, bounds, delta);
		if (delta == PointD.Zero)
			return false;

		document.FinishSelection ();
		document.ResetSelectionPaths ();
		foreach (UserLayer layer in layers)
			document.Layers.TranslateLayerTree (layer, delta);
		PushMoveHistory (document, layers, delta);
		UpdateTransformHandle (document);
		return true;
	}

	protected override bool OnKeyUp (Document document, ToolKeyEventArgs e)
	{
		if (!IsShiftKey (e.Key))
			return false;

		shift_pressed = false;
		UpdateCursor (last_window_point);
		return true;
	}

	private bool StartResize (Document document, ToolMouseEventArgs e)
	{
		if (!transform_handle.Active || !transform_handle.BeginDrag (e.PointDouble, document.ImageSize))
			return false;

		if (!document.Layers.TryGetSelectedLayerTreeBounds (out resize_start_bounds)) {
			transform_handle.EndDrag ();
			return false;
		}

		document.FinishSelection ();
		resized_layers.Clear ();
		resized_layers.AddRange (document.Layers.GetSelectedLayerRoots ());
		CaptureResizeTransforms ();
		resize_current_bounds = resize_start_bounds;
		return true;
	}

	private void UpdateResize (Document document, ToolMouseEventArgs e)
	{
		transform_handle.UpdateDrag (e.PointDouble, e.IsShiftPressed);
		RectangleD target = transform_handle.Rectangle;
		if (target.Width < 1 || target.Height < 1)
			return;

		RestoreResizeTransforms ();
		foreach (UserLayer layer in resized_layers)
			document.Layers.ResizeLayerTree (layer, resize_start_bounds, target);
		resize_current_bounds = target;
	}

	private void FinishResize (Document? document)
	{
		if (resized_layers.Count == 0)
			return;

		if (transform_handle.IsDragging)
			transform_handle.EndDrag ();

		if (document is not null && resize_start_bounds != resize_current_bounds)
			PushResizeHistory (document);

		resized_layers.Clear ();
		resize_initial_transforms.Clear ();
		resize_start_bounds = RectangleD.Zero;
		resize_current_bounds = RectangleD.Zero;
		if (document is not null)
			UpdateTransformHandle (document);
	}

	private void CaptureResizeTransforms ()
	{
		resize_initial_transforms.Clear ();
		foreach (UserLayer root in resized_layers)
			CaptureResizeTransforms (root);
	}

	private void CaptureResizeTransforms (UserLayer node)
	{
		foreach (Layer layer in node.GetLayersToPaint ())
			resize_initial_transforms[layer] = layer.Transform.Clone ();

		foreach (UserLayer child in node.Children)
			CaptureResizeTransforms (child);
	}

	private void RestoreResizeTransforms ()
	{
		foreach ((Layer layer, Matrix transform) in resize_initial_transforms)
			layer.Transform = transform.Clone ();
	}

	private void StartLayerDrag (Document document, PointD point)
	{
		document.FinishSelection ();
		document.ResetSelectionPaths ();
		dragged_layers.Clear ();
		dragged_layers.AddRange (document.Layers.GetSelectedLayerRoots ());
		drag_start_point = point;
		applied_drag_delta = PointD.Zero;
		has_drag_start_bounds = document.Layers.TryGetSelectedLayerTreeBounds (out drag_start_bounds);
		BeginSmartGuideDrag (document);
		document.Workspace.Invalidate ();
	}

	private void StartLayerMarquee (Document document, PointD point)
	{
		document.FinishSelection ();
		document.ResetSelectionPaths ();
		marquee_active = true;
		marquee_start_point = point;
		marquee_rectangle = RectangleD.Zero;
		SetCursor (marquee_cursor);
		document.Layers.ToolLayer.Clear ();
		document.Layers.ToolLayer.Hidden = true;
		UpdateMarqueeSelection (document, RectangleD.Zero, requireFullyContained: true);
	}

	private void UpdateLayerMarquee (Document document, PointD point)
	{
		RectangleD rectangle = RectangleD.FromPoints (marquee_start_point, point);
		if (rectangle == marquee_rectangle)
			return;

		Layer toolLayer = document.Layers.ToolLayer;
		toolLayer.Clear ();
		toolLayer.Hidden = false;
		using Context context = new (toolLayer.Surface);
		context.Rectangle (rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
		context.SetSourceColor (new Color (0.21, 0.52, 0.89, 0.16));
		context.FillPreserve ();
		context.SetSourceColor (new Color (0.21, 0.52, 0.89, 0.9));
		context.LineWidth = 1;
		context.Stroke ();

		marquee_rectangle = rectangle;
		UpdateMarqueeSelection (document, rectangle, point.X >= marquee_start_point.X);
		document.Workspace.Invalidate ();
	}

	private void UpdateMarqueeSelection (Document document, RectangleD selection, bool requireFullyContained)
	{
		IReadOnlyList<UserLayer> layers = selection.Width > 0 && selection.Height > 0
			? document.Layers.FindLayersInSelection (selection, requireFullyContained)
			: [];
		document.Layers.SetSelectedUserLayers (layers, commitCurrentTool: false);
	}

	private void FinishLayerMarquee (Document? document, PointD? point = null)
	{
		if (!marquee_active || document is null)
			return;

		if (point.HasValue)
			UpdateLayerMarquee (document, point.Value);

		bool hasDragged = marquee_rectangle.Width > 1 || marquee_rectangle.Height > 1;
		RectangleD selection = marquee_rectangle;
		marquee_active = false;
		Layer toolLayer = document.Layers.ToolLayer;
		toolLayer.Clear ();
		toolLayer.Hidden = true;
		marquee_rectangle = RectangleD.Zero;
		document.Workspace.Invalidate ();

		if (!hasDragged)
			document.Layers.ClearCurrentUserLayer (commitCurrentTool: false);
	}

	private PointD GetDragDelta (PointD point)
		=> new (Math.Floor (point.X - drag_start_point.X), Math.Floor (point.Y - drag_start_point.Y));

	private static PointD GetKeyDelta (ToolKeyEventArgs e)
	{
		double distance = e.IsControlPressed ? 10 : 1;
		return e.Key.Value switch {
			Gdk.Constants.KEY_Left => new PointD (-distance, 0),
			Gdk.Constants.KEY_Right => new PointD (distance, 0),
			Gdk.Constants.KEY_Up => new PointD (0, -distance),
			Gdk.Constants.KEY_Down => new PointD (0, distance),
			_ => PointD.Zero,
		};
	}

	private void FinishLayerDrag (Document? document)
	{
		EndSmartGuideDrag (document);

		if (document is not null && dragged_layers.Count > 0 && applied_drag_delta != PointD.Zero)
			PushMoveHistory (document, dragged_layers, applied_drag_delta);

		dragged_layers.Clear ();
		applied_drag_delta = PointD.Zero;
		drag_start_bounds = RectangleD.Zero;
		has_drag_start_bounds = false;
	}

	private static PointD ClampMoveDelta (Document document, RectangleD bounds, PointD requestedDelta)
	{
		double targetX = ClampMovePosition (bounds.X + requestedDelta.X, bounds.Width, document.ImageSize.Width);
		double targetY = ClampMovePosition (bounds.Y + requestedDelta.Y, bounds.Height, document.ImageSize.Height);
		return new PointD (targetX - bounds.X, targetY - bounds.Y);
	}

	private static double ClampMovePosition (double position, double contentSize, int canvasSize)
	{
		double min = Math.Min (0, canvasSize - contentSize);
		double max = Math.Max (0, canvasSize - contentSize);
		return Math.Clamp (position, min, max);
	}

	private void PushMoveHistory (Document document, IReadOnlyList<UserLayer> layers, PointD delta)
	{
		if (layers.Count == 1) {
			document.History.PushNewItem (new MoveLayerTreeHistoryItem (Icon, Name, document, layers[0], delta));
			return;
		}

		CompoundHistoryItem history = new (Icon, Name);
		foreach (UserLayer layer in layers)
			history.Push (new MoveLayerTreeHistoryItem (Icon, Name, document, layer, delta));
		document.History.PushNewItem (history);
	}

	private void PushResizeHistory (Document document)
	{
		string name = Translations.GetString ("Resize Layer");
		if (resized_layers.Count == 1) {
			document.History.PushNewItem (new ResizeLayerTreeHistoryItem (
				Pinta.Resources.Icons.ImageResize,
				name,
				document,
				resized_layers[0],
				resize_start_bounds,
				resize_current_bounds));
			return;
		}

		CompoundHistoryItem history = new (Pinta.Resources.Icons.ImageResize, name);
		foreach (UserLayer layer in resized_layers)
			history.Push (new ResizeLayerTreeHistoryItem (
				Pinta.Resources.Icons.ImageResize,
				name,
				document,
				layer,
				resize_start_bounds,
				resize_current_bounds));
		document.History.PushNewItem (history);
	}

	protected override void OnCommit (Document? document)
	{
		FinishResize (document);
		FinishLayerDrag (document);
		FinishLayerMarquee (document);
		document?.FinishSelection ();
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);
		if (auto_select_layer is not null)
			settings.PutSetting (SettingNames.MOVE_LAYER_AUTO_SELECT_LAYER, auto_select_layer.Active);
		if (show_transform_controls is not null)
			settings.PutSetting (SettingNames.SHOW_TRANSFORM_CONTROLS, show_transform_controls.Active);
	}

	private void HandleTransformControlsToggled (Gtk.CheckButton sender, EventArgs e)
	{
		UpdateTransformHandle (workspace.HasOpenDocuments ? workspace.ActiveDocument : null);
		workspace.Invalidate ();
	}

	private void HandleTransformTargetChanged (object? sender, EventArgs e)
	{
		InvalidateSmartGuideCandidates ();
		if (resized_layers.Count == 0)
			UpdateTransformHandle (workspace.HasOpenDocuments ? workspace.ActiveDocument : null);
	}

	private void UpdateTransformHandle (Document? document)
	{
		if (document is null
			|| show_transform_controls?.Active != true
			|| !document.Layers.TryGetSelectedLayerTreeBounds (out RectangleD bounds)) {
			transform_handle.Active = false;
			return;
		}

		transform_handle.Rectangle = bounds;
		transform_handle.Active = bounds.Width > 0 && bounds.Height > 0;
	}

	private static bool IsShiftKey (Gdk.Key key)
		=> key.Value is Gdk.Constants.KEY_Shift_L or Gdk.Constants.KEY_Shift_R;

	private void UpdateCursor (in PointD windowPoint, bool shiftModifier = false)
	{
		if (marquee_active) {
			SetCursor (marquee_cursor);
			return;
		}

		// Show the resize cursor over a transform handle even while Shift is held,
		// since Shift + dragging a handle resizes proportionally.
		if (transform_handle.Active && transform_handle.GetCursorAtPoint (windowPoint) is { } cursor) {
			SetCursor (cursor);
			return;
		}

		if (shift_pressed || shiftModifier) {
			SetCursor (marquee_cursor);
			return;
		}

		SetCursor (DefaultCursor);
	}
}
