using System;
using System.Linq;
using Pinta.Core;

namespace Pinta;

internal sealed class MarkToolbarWidget
{
	private readonly Gtk.Box widget;
	private readonly ToolManager tools;
	private readonly Gtk.ToggleButton[] buttons;
	private readonly IMarkTool mark_tool;
	private bool updating_buttons;

	public MarkToolbarWidget (ToolManager tools)
	{
		this.tools = tools;
		widget = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		mark_tool = tools.OfType<IMarkTool> ().FirstOrDefault ()
			?? throw new InvalidOperationException ("The mark tool must be registered before the canvas is created.");

		widget.Halign = Gtk.Align.Center;
		widget.Valign = Gtk.Align.Start;
		widget.MarginTop = 8;
		widget.MarginStart = 2;
		widget.MarginEnd = 2;

		buttons = [
			CreateButton (Resources.Icons.ToolRectangle, "Rectangle", 0),
			CreateButton (Resources.Icons.ToolEllipse, "Circle", 1),
			CreateButton (Resources.Icons.LassoPolygon, "Polygon", 2),
		];

		foreach (Gtk.ToggleButton button in buttons)
			widget.Append (button);

		buttons[0].SetGroup (buttons[1]);
		buttons[2].SetGroup (buttons[1]);
		UpdateActiveButton ();
		mark_tool.ShapeChanged += HandleShapeChanged;
		tools.ToolActivated += HandleToolActivated;
	}

	private Gtk.ToggleButton CreateButton (string icon, string tooltip, int shape)
	{
		Gtk.ToggleButton button = Gtk.ToggleButton.New ();
		button.Child = Gtk.Image.NewFromIconName (icon);
		button.TooltipText = Translations.GetString (tooltip);
		button.WidthRequest = 34;
		button.HeightRequest = 34;
		button.OnToggled += (_, _) => {
			if (!button.Active || updating_buttons)
				return;

			mark_tool.SetShape (shape);
			if (mark_tool is BaseTool tool)
				tools.SetCurrentTool (tool);
		};
		return button;
	}

	public Gtk.Widget Widget => widget;

	private void HandleShapeChanged (object? sender, EventArgs e)
	{
		UpdateActiveButton ();
	}

	private void HandleToolActivated (object? sender, ToolEventArgs e)
	{
		UpdateActiveButton ();
	}

	private void UpdateActiveButton ()
	{
		int shape = Math.Clamp (mark_tool.CurrentShape, 0, buttons.Length - 1);
		updating_buttons = true;
		try {
			buttons[shape].Active = true;
		} finally {
			updating_buttons = false;
		}
	}
}
