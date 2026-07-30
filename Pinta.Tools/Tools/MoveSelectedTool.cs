//
// MoveSelectedTool.cs
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
using Cairo;
using Pinta.Core;

namespace Pinta.Tools;

public sealed class MoveSelectedTool : BaseTransformTool
{
	private MovePixelsHistoryItem? hist;
	private DocumentSelection? original_selection;
	private readonly Matrix original_transform = CairoExtensions.CreateIdentityMatrix ();
	private Gtk.CheckButton? auto_select_layer;

	private readonly SystemManager system_manager;
	public MoveSelectedTool (IServiceProvider services) : base (services)
	{
		system_manager = services.GetService<SystemManager> ();
	}

	public override string Name => Translations.GetString ("Move Selected Pixels");
	public override string Icon => Pinta.Resources.Icons.ToolMove;
	// Translators: {0} is 'Ctrl', or a platform-specific key such as 'Command' on macOS.
	public override string StatusBarText => Translations.GetString (
		"Left click and drag the selection to move selected content." +
		"\nHold {0} to scale instead of move." +
		"\nRight click and drag the selection to rotate selected content." +
		"\nHold Shift to rotate in steps." +
		"\nUse arrow keys to move selected content by a single pixel.",
		system_manager.CtrlLabel ());

	public override Gdk.Cursor DefaultCursor => Gdk.Cursor.NewFromTexture (Resources.GetIcon (Pinta.Resources.Icons.ToolMoveCursor), 0, 0, null);
	public override Gdk.Key ShortcutKey => new (Gdk.Constants.KEY_V);
	public override int Priority => 5;
	public override bool RequiresEditableLayer => false;

	protected override void OnBuildToolBar (Gtk.Box toolbar)
	{
		auto_select_layer ??= Gtk.CheckButton.NewWithLabel (Translations.GetString ("Auto-select layer"));
		auto_select_layer.Active = Settings.GetSetting (SettingNames.MOVE_SELECTED_AUTO_SELECT_LAYER, false);
		toolbar.Append (auto_select_layer);

		base.OnBuildToolBar (toolbar);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
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

			if (!layer.IsEditable)
				return;
		}

		if (!document.Layers.HasSelectedLayer || !document.Layers.CurrentUserLayer.IsEditable)
			return;

		base.OnMouseDown (document, e);
	}

	protected override bool OnKeyDown (Document document, ToolKeyEventArgs e)
	{
		if (!document.Layers.HasSelectedLayer || !document.Layers.CurrentUserLayer.IsEditable)
			return false;

		return base.OnKeyDown (document, e);
	}

	protected override RectangleD GetSourceRectangle (Document document)
		=> document.Selection.Visible
		? document.Selection.GetBounds ()
		: Utility.GetAlphaBounds (document.Layers.CurrentUserLayer.Surface).ToDouble ();

	protected override bool ShouldShowTransformHandle (Document document)
		=> TransformControlsVisible && document.Layers.CurrentUserLayer.IsEditable;

	protected override void OnStartTransform (Document document)
	{
		base.OnStartTransform (document);

		if (!document.Selection.Visible)
			document.Selection.CreateRectangleSelection (Utility.GetAlphaBounds (document.Layers.CurrentUserLayer.Surface).ToDouble ());

		original_selection = document.Selection.Clone ();
		original_transform.InitMatrix (document.Layers.SelectionLayer.Transform);

		hist = new MovePixelsHistoryItem (Icon, Name, document);
		hist.TakeSnapshot (!document.Layers.ShowSelectionLayer);

		if (!document.Layers.ShowSelectionLayer) {
			// Copy the selection to the temp layer
			document.Layers.CreateSelectionLayer ();
			document.Layers.ShowSelectionLayer = true;
			// Use same BlendMode, Opacity and Visibility for SelectionLayer
			document.Layers.SelectionLayer.BlendMode = document.Layers.CurrentUserLayer.BlendMode;
			document.Layers.SelectionLayer.Opacity = document.Layers.CurrentUserLayer.Opacity;
			document.Layers.SelectionLayer.Hidden = document.Layers.CurrentUserLayer.Hidden;

			using Context selection_ctx = new (document.Layers.SelectionLayer.Surface);
			selection_ctx.AppendPath (document.Selection.SelectionPath);
			selection_ctx.FillRule = FillRule.EvenOdd;
			selection_ctx.SetSourceSurface (document.Layers.CurrentUserLayer.Surface, 0, 0);
			selection_ctx.Clip ();
			selection_ctx.Paint ();

			var surf = document.Layers.CurrentUserLayer.Surface;

			using Context surf_ctx = new (surf);
			surf_ctx.AppendPath (document.Selection.SelectionPath);
			surf_ctx.FillRule = FillRule.EvenOdd;
			surf_ctx.Operator = Cairo.Operator.Clear;
			surf_ctx.Fill ();
		}

		document.Workspace.Invalidate ();
	}

	protected override void OnUpdateTransform (Document document, Matrix transform)
	{
		base.OnUpdateTransform (document, transform);

		document.Selection = original_selection!.Transform (transform); // NRT - Set in OnStartTransform
		document.Selection.Visible = true;

		document.Layers.SelectionLayer.Transform.InitMatrix (original_transform);
		document.Layers.SelectionLayer.Transform.Multiply (transform);

		document.Workspace.Invalidate ();
	}

	protected override void OnFinishTransform (Document document, Matrix transform)
	{
		base.OnFinishTransform (document, transform);

		// Also transform the base selection used for the various select modes.
		var prev_selection = document.PreviousSelection;
		document.PreviousSelection = prev_selection.Transform (transform);

		if (hist != null)
			document.History.PushNewItem (hist);

		hist = null;
		original_selection = null;
		original_transform.InitIdentity ();
	}

	protected override void OnCommit (Document? document)
	{
		document?.FinishSelection ();
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (auto_select_layer is not null)
			settings.PutSetting (SettingNames.MOVE_SELECTED_AUTO_SELECT_LAYER, auto_select_layer.Active);
	}

	protected override void OnDeactivated (Document? document, BaseTool? newTool)
	{
		base.OnDeactivated (document, newTool);

		document?.FinishSelection ();
	}
}
