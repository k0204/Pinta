using System;
using System.Collections.Generic;
using Cairo;
using Pinta.Core;

namespace Pinta.Tools;

public sealed partial class DetectBorderTool
{
	private const string mask_brush_width_setting = "detect-border-brush-width";
	private readonly Stack<ImageSurface> mask_undo = [];
	private readonly Stack<ImageSurface> mask_redo = [];
	private bool mask_mouse_down;
	private PointD? mask_last_point;
	private Gtk.SpinButton? mask_brush_width;
	private ToolBarDropDownButton? mask_mode_button;

	private enum MaskMode
	{
		Select,
		Keep,
		Erase,
	}

	private int MaskBrushWidth => mask_brush_width?.GetValueAsInt () ?? 32;

	private void BuildMaskToolbar (Gtk.Box toolbar)
	{
		toolbar.Append (Gtk.Label.New ($" {Translations.GetString ("Guidance")}: "));
		mask_mode_button = ToolBarDropDownButton.New (true);
		mask_mode_button.AddItem (Translations.GetString ("Rectangle selection"), Pinta.Resources.Icons.ToolSelectRectangle, MaskMode.Select);
		mask_mode_button.AddItem (Translations.GetString ("Keep brush"), Pinta.Resources.Icons.ToolPaintBrush, MaskMode.Keep);
		mask_mode_button.AddItem (Translations.GetString ("Erase brush"), Pinta.Resources.Icons.ToolEraser, MaskMode.Erase);
		mask_mode_button.SelectedItemChanged += (_, _) => {
			mask_mode = mask_mode_button.SelectedItem.GetTagOrDefault (MaskMode.Select);
		};
		toolbar.Append (mask_mode_button);

		toolbar.Append (Gtk.Label.New ($" {Translations.GetString ("Brush width")}: "));
		mask_brush_width = GtkExtensions.CreateToolBarSpinButton (
			1,
			1000,
			1,
			Settings.GetSetting (mask_brush_width_setting, 32));
		toolbar.Append (mask_brush_width);
	}

	private void SetMaskMode (MaskMode mode)
	{
		mask_mode = mode;
		if (mask_mode_button is not null)
			mask_mode_button.SelectedIndex = (int) mode;
	}

	private void ResetMask (Document document)
	{
		ClearMaskHistory ();
		Layer mask = document.Layers.ToolLayer;
		mask.Clear ();
		mask.Hidden = false;
		mask_mouse_down = false;
		mask_last_point = null;
	}

	private void HideMask (Document document)
	{
		ClearMaskHistory ();
		document.Layers.ToolLayer.Clear ();
		document.Layers.ToolLayer.Hidden = true;
		mask_mouse_down = false;
		mask_last_point = null;
	}

	private void BeginMaskStroke (Document document, PointD point)
	{
		mask_mouse_down = true;
		mask_last_point = document.ClampToImageSize (point);
		mask_undo.Push (document.Layers.ToolLayer.Surface.Clone ());
		ClearStack (mask_redo);
		DrawMaskStroke (document, mask_last_point.Value, mask_last_point.Value);
	}

	private void ContinueMaskStroke (Document document, PointD point)
	{
		if (!mask_mouse_down || !mask_last_point.HasValue)
			return;

		PointD next = document.ClampToImageSize (point);
		DrawMaskStroke (document, mask_last_point.Value, next);
		mask_last_point = next;
	}

	private void EndMaskStroke (Document document)
	{
		if (!mask_mouse_down)
			return;

		mask_mouse_down = false;
		mask_last_point = null;
		UpdateRecognitionButton (document);
	}

	private void DrawMaskStroke (Document document, PointD start, PointD end)
	{
		ImageSurface surface = document.Layers.ToolLayer.Surface;
		using Context context = new (surface);
		document.Selection.Clip (context);
		context.Operator = Operator.Source;
		context.Antialias = UseAntialiasing ? Antialias.Subpixel : Antialias.None;
		context.SetSourceColor (MaskColor ());
		context.MoveTo (start.X + 0.5, start.Y + 0.5);
		context.LineTo (end.X + 0.5, end.Y + 0.5);
		context.LineWidth = MaskBrushWidth;
		context.LineJoin = LineJoin.Round;
		context.LineCap = LineCap.Round;
		context.Stroke ();
		surface.MarkDirty ();
		document.Workspace.Invalidate ();
	}

	private Cairo.Color MaskColor ()
		=> mask_mode == MaskMode.Keep
			? new Cairo.Color (0.1, 0.85, 0.2, 0.55)
			: new Cairo.Color (0.95, 0.1, 0.1, 0.55);

	protected override bool OnHandleUndo (Document document)
	{
		if (document.Layers.ToolLayer.Hidden) {
			ClearMaskHistory ();
			return false;
		}

		if (mask_undo.Count == 0)
			return false;

		SwapMaskSurface (document, mask_undo, mask_redo);
		return true;
	}

	protected override bool OnHandleRedo (Document document)
	{
		if (document.Layers.ToolLayer.Hidden) {
			ClearMaskHistory ();
			return false;
		}

		if (mask_redo.Count == 0)
			return false;

		SwapMaskSurface (document, mask_redo, mask_undo);
		return true;
	}

	private static void SwapMaskSurface (
		Document document,
		Stack<ImageSurface> from,
		Stack<ImageSurface> to)
	{
		ImageSurface current = document.Layers.ToolLayer.Surface;
		document.Layers.ToolLayer.Surface = from.Pop ();
		to.Push (current);
		document.Layers.ToolLayer.Surface.MarkDirty ();
		document.Workspace.Invalidate ();
	}

	private void ClearMaskHistory ()
	{
		ClearStack (mask_undo);
		ClearStack (mask_redo);
	}

	private static void ClearStack (Stack<ImageSurface> stack)
	{
		while (stack.Count > 0)
			stack.Pop ().Dispose ();
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);
		if (mask_brush_width is not null)
			settings.PutSetting (mask_brush_width_setting, mask_brush_width.GetValueAsInt ());
	}
}
