// 
// BaseBrushTool.cs
//  
// Author:
//       Joseph Hillenbrand <joehillen@gmail.com>
// 
// Copyright (c) 2010 Joseph Hillenbrand
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
using Gtk;
using Pinta.Core;

namespace Pinta.Tools;

// This is a base class for brush type tools (paintbrush, eraser, etc)
public abstract class BaseBrushTool : BaseTool
{
	protected IPaletteService Palette { get; }

	protected ImageSurface? undo_surface;
	protected Matrix? undo_transform;
	protected bool surface_modified;
	protected MouseButton mouse_button;

	protected BaseBrushTool (IServiceProvider services) : base (services)
	{
		Palette = services.GetService<IPaletteService> ();

		BrushWidthSpinButton.TooltipText = Translations.GetString ("Change brush width.") + "\n"
			+ "\n" + Translations.GetString ("Shortcut keys:")
			+ "\n" + Translations.GetString ("Press {0} to decrease brush width", "\"[\"")
			+ "\n" + Translations.GetString ("Press {0} to increase brush width", "\"]\"");
		BrushWidthSpinButton.OnValueChanged += (_, _) => OnBrushWidthChanged ();
	}

	protected override bool ShowAntialiasingButton => true;

	protected int BrushWidth {
		get => brush_width?.GetValueAsInt () ?? DEFAULT_BRUSH_WIDTH;
		set {
			if (brush_width is not null)
				brush_width.Value = value;
		}
	}

	protected override void OnBuildToolBar (Box tb)
	{
		base.OnBuildToolBar (tb);

		tb.Append (BrushWidthLabel);
		tb.Append (BrushWidthSpinButton);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		// If we are already drawing, ignore any additional mouse down events
		if (mouse_button != MouseButton.None)
			return;

		surface_modified = false;
		UserLayer layer = document.Layers.CurrentUserLayer;
		undo_surface = layer.Surface.Clone ();
		undo_transform = layer.Transform.Clone ();
		ExpandLayerToCanvas (document, layer);
		mouse_button = e.MouseButton;

		OnMouseMove (document, e);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (undo_surface != null && surface_modified) {
			document.History.PushNewItem (new SimpleHistoryItem (Icon, Name, undo_surface, document.Layers.CurrentUserLayer, undo_transform!));
		} else if (undo_surface != null) {
			document.Layers.CurrentUserLayer.Surface = undo_surface;
			document.Layers.CurrentUserLayer.Transform = undo_transform!;
		}

		surface_modified = false;
		undo_surface = null;
		undo_transform = null;
		mouse_button = MouseButton.None;
	}

	protected static void ExpandLayerToCanvas (Document document, UserLayer layer)
	{
		Matrix transform = layer.Transform.Clone ();
		bool identity = transform.TransformPoint (PointD.Zero) == PointD.Zero
			&& transform.TransformPoint (new PointD (1, 0)) == new PointD (1, 0)
			&& transform.TransformPoint (new PointD (0, 1)) == new PointD (0, 1);
		if (identity && layer.Surface.Width == document.ImageSize.Width && layer.Surface.Height == document.ImageSize.Height)
			return;

		ImageSurface expanded = CairoExtensions.CreateImageSurface (Format.Argb32, document.ImageSize.Width, document.ImageSize.Height);
		using (Context context = new (expanded))
			layer.DrawWithOperator (context, Operator.Over);

		layer.Surface = expanded;
		layer.Transform.InitIdentity ();
	}

	protected override bool OnKeyDown (Document document, ToolKeyEventArgs e)
	{
		switch (e.Key.Value) {
			case Gdk.Constants.KEY_bracketleft:
				BrushWidth--;
				return true;
			case Gdk.Constants.KEY_bracketright:
				BrushWidth++;
				return true;
		}

		return base.OnKeyDown (document, e);
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (brush_width is not null)
			settings.PutSetting (SettingNames.BrushWidth (this), brush_width.GetValueAsInt ());
	}

	protected virtual void OnBrushWidthChanged ()
	{
		// Change the cursor when the BrushWidth is changed.
		SetCursor (DefaultCursor);
	}

	protected static Context CreateCanvasClippedContext (Document document)
	{
		UserLayer layer = document.Layers.CurrentUserLayer;
		Context context = new (layer.Surface);
		Matrix inverse = layer.Transform.Clone ();
		if (inverse.Invert () == Status.Success)
			context.Transform (inverse);

		document.Selection.Clip (context);
		return context;
	}


	private SpinButton? brush_width;
	private Label? brush_width_label;

	protected SpinButton BrushWidthSpinButton => brush_width ??= GtkExtensions.CreateToolBarSpinButton (1, 1e5, 1, Settings.GetSetting (SettingNames.BrushWidth (this), DEFAULT_BRUSH_WIDTH));
	protected Label BrushWidthLabel => brush_width_label ??= Label.New (string.Format (" {0}: ", Translations.GetString ("Brush width")));
}
