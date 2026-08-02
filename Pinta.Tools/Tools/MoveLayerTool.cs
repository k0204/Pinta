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
using Pinta.Core;

namespace Pinta.Tools;

public sealed class MoveLayerTool : BaseTool
{
	private Gtk.CheckButton? auto_select_layer;
	private UserLayer? dragged_layer;
	private PointD drag_start_point;
	private PointD applied_drag_delta;

	private readonly SystemManager system_manager;
	public MoveLayerTool (IServiceProvider services) : base (services)
	{
		system_manager = services.GetService<SystemManager> ();
	}

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

		base.OnBuildToolBar (toolbar);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		if (e.MouseButton != MouseButton.Left)
			return;

		if (!document.Workspace.PointInCanvas (e.PointDouble)) {
			document.Layers.ClearCurrentUserLayer ();
			return;
		}

		if (auto_select_layer?.Active == true && e.MouseButton == MouseButton.Left) {
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
		if (dragged_layer is null) {
			base.OnMouseMove (document, e);
			return;
		}

		PointD totalDelta = GetDragDelta (e.PointDouble);
		document.Layers.TranslateLayerTree (dragged_layer, totalDelta - applied_drag_delta);
		applied_drag_delta = totalDelta;
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (dragged_layer is null) {
			base.OnMouseUp (document, e);
			return;
		}

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
		return true;
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
		=> new (
			Math.Floor (point.X - drag_start_point.X),
			Math.Floor (point.Y - drag_start_point.Y));

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

	private void FinishLayerDrag (Document document)
	{
		if (dragged_layer is not null && applied_drag_delta != PointD.Zero)
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
		if (document is null)
			return;

		FinishLayerDrag (document);
		document.FinishSelection ();
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (auto_select_layer is not null)
			settings.PutSetting (SettingNames.MOVE_LAYER_AUTO_SELECT_LAYER, auto_select_layer.Active);
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		base.OnDeactivated (document, newTool);

		if (document is not null) {
			FinishLayerDrag (document);
			document.FinishSelection ();
		}
	}
}
