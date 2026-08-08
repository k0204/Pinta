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
using Pinta.Core;

namespace Pinta.Tools;

public sealed class MoveLayerTool : BaseTool
{
	private Gtk.CheckButton? auto_select_layer;
	private Gtk.CheckButton? show_transform_controls;
	private UserLayer? dragged_layer;
	private UserLayer? resized_layer;
	private PointD drag_start_point;
	private PointD applied_drag_delta;
	private RectangleD resize_start_bounds;
	private RectangleD resize_current_bounds;

	private readonly IWorkspaceService workspace;
	private readonly SystemManager system_manager;
	private readonly RectangleHandle transform_handle;

	public MoveLayerTool (IServiceProvider services) : base (services)
	{
		workspace = services.GetService<IWorkspaceService> ();
		system_manager = services.GetService<SystemManager> ();
		transform_handle = new (workspace) {
			DrawOutline = true,
			InvertIfNegative = true,
			PreserveAspectRatio = true,
		};
	}

	public override IEnumerable<IToolHandle> Handles => [transform_handle];
	public override string Name => Translations.GetString ("Move Layer");
	public override string Icon => Pinta.Resources.Icons.ToolMove;
	// Translators: {0} is 'Ctrl', or a platform-specific key such as 'Command' on macOS.
	public override string StatusBarText => Translations.GetString (
		"Left click and drag to move the layer." +
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
		UpdateTransformHandle (document);
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		FinishResize (document);
		FinishLayerDrag (document);
		workspace.ActiveDocumentChanged -= HandleTransformTargetChanged;
		workspace.SelectedLayerChanged -= HandleTransformTargetChanged;
		workspace.LayerTreeChanged -= HandleTransformTargetChanged;
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

		if (StartResize (document, e))
			return;

		if (!document.Workspace.PointInCanvas (e.PointDouble)) {
			document.Layers.ClearCurrentUserLayer ();
			return;
		}

		if (auto_select_layer?.Active == true) {
			UserLayer? layer = document.Layers.FindTopmostLayerAtPoint (e.PointDouble);
			if (layer is null) {
				document.Layers.ClearCurrentUserLayer ();
				return;
			}

			if (layer != document.Layers.CurrentUserLayer) {
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
		if (resized_layer is not null) {
			UpdateResize (document, e);
			return;
		}

		if (dragged_layer is null) {
			UpdateCursor (e.WindowPoint);
			return;
		}

		PointD totalDelta = GetDragDelta (e.PointDouble);
		PointD delta = totalDelta - applied_drag_delta;
		document.Layers.TranslateLayerTree (dragged_layer, delta);
		applied_drag_delta = totalDelta;
		TranslateTransformHandle (delta);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (resized_layer is not null) {
			FinishResize (document);
			return;
		}

		if (dragged_layer is null)
			return;

		FinishLayerDrag (document);
	}

	protected override bool OnKeyDown (Document document, ToolKeyEventArgs e)
	{
		if (!document.Layers.HasSelectedLayer || !document.Layers.CurrentUserLayer.CanMoveOnCanvas)
			return false;

		PointD delta = GetKeyDelta (e);
		if (delta == PointD.Zero)
			return false;

		document.FinishSelection ();
		document.ResetSelectionPaths ();
		UserLayer layer = document.Layers.CurrentUserLayer;
		document.Layers.TranslateLayerTree (layer, delta);
		document.History.PushNewItem (new MoveLayerTreeHistoryItem (Icon, Name, document, layer, delta));
		TranslateTransformHandle (delta);
		return true;
	}

	private bool StartResize (Document document, ToolMouseEventArgs e)
	{
		if (!transform_handle.Active || !transform_handle.BeginDrag (e.PointDouble, document.ImageSize))
			return false;

		if (!document.Layers.TryGetResizableLayerTreeBounds (document.Layers.CurrentUserLayer, out resize_start_bounds)) {
			transform_handle.EndDrag ();
			return false;
		}

		document.FinishSelection ();
		resized_layer = document.Layers.CurrentUserLayer;
		resize_current_bounds = resize_start_bounds;
		return true;
	}

	private void UpdateResize (Document document, ToolMouseEventArgs e)
	{
		transform_handle.UpdateDrag (e.PointDouble, e.IsShiftPressed);
		RectangleD target = transform_handle.Rectangle;
		if (target.Width < 1 || target.Height < 1)
			return;

		document.Layers.ResizeLayerTree (resized_layer!, resize_current_bounds, target);
		resize_current_bounds = target;
	}

	private void FinishResize (Document? document)
	{
		if (resized_layer is null)
			return;

		if (transform_handle.IsDragging)
			transform_handle.EndDrag ();

		if (document is not null && resize_start_bounds != resize_current_bounds)
			document.History.PushNewItem (new ResizeLayerTreeHistoryItem (
				Pinta.Resources.Icons.ImageResize,
				Translations.GetString ("Resize Layer"),
				document,
				resized_layer,
				resize_start_bounds,
				resize_current_bounds));

		resized_layer = null;
		resize_start_bounds = RectangleD.Zero;
		resize_current_bounds = RectangleD.Zero;
		if (document is not null)
			UpdateTransformHandle (document);
	}

	private void StartLayerDrag (Document document, PointD point)
	{
		document.FinishSelection ();
		document.ResetSelectionPaths ();
		dragged_layer = document.Layers.CurrentUserLayer;
		drag_start_point = point;
		applied_drag_delta = PointD.Zero;
		document.Workspace.Invalidate ();
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
		if (document is not null && dragged_layer is not null && applied_drag_delta != PointD.Zero)
			document.History.PushNewItem (new MoveLayerTreeHistoryItem (
				Icon,
				Name,
				document,
				dragged_layer,
				applied_drag_delta));

		dragged_layer = null;
		applied_drag_delta = PointD.Zero;
	}

	protected override void OnCommit (Document? document)
	{
		FinishResize (document);
		FinishLayerDrag (document);
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
		if (resized_layer is null)
			UpdateTransformHandle (workspace.HasOpenDocuments ? workspace.ActiveDocument : null);
	}

	private void UpdateTransformHandle (Document? document)
	{
		if (document is null
			|| show_transform_controls?.Active != true
			|| !document.Layers.TryGetResizableLayerTreeBounds (document.Layers.CurrentUserLayer, out RectangleD bounds)) {
			transform_handle.Active = false;
			return;
		}

		transform_handle.Rectangle = bounds;
		transform_handle.Active = bounds.Width > 0 && bounds.Height > 0;
	}

	private void TranslateTransformHandle (PointD delta)
	{
		if (!transform_handle.Active)
			return;

		RectangleD bounds = transform_handle.Rectangle;
		transform_handle.Rectangle = bounds with { X = bounds.X + delta.X, Y = bounds.Y + delta.Y };
	}

	private void UpdateCursor (in PointD windowPoint)
	{
		SetCursor (transform_handle.Active
			? transform_handle.GetCursorAtPoint (windowPoint) ?? DefaultCursor
			: DefaultCursor);
	}
}
