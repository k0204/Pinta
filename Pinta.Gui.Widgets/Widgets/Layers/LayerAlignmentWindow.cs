//
// LayerAlignmentWindow.cs
//

using Pinta.Core;

namespace Pinta.Gui.Widgets;

[GObject.Subclass<Gtk.Box>]
public sealed partial class LayerAlignmentWindow
{
	public static LayerAlignmentWindow New ()
	{
		LayerAlignmentWindow window = NewWithProperties ([]);
		return window;
	}

	partial void Initialize ()
	{
		Gtk.Box alignmentButtons = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		alignmentButtons.Halign = Gtk.Align.Start;
		alignmentButtons.Append (PintaCore.Actions.Layers.AlignLayersLeft.CreateToolBarItem (force_icon_only: true));
		alignmentButtons.Append (PintaCore.Actions.Layers.AlignLayersCenterHorizontal.CreateToolBarItem (force_icon_only: true));
		alignmentButtons.Append (PintaCore.Actions.Layers.AlignLayersRight.CreateToolBarItem (force_icon_only: true));
		alignmentButtons.Append (GtkExtensions.CreateToolBarSeparator ());
		alignmentButtons.Append (PintaCore.Actions.Layers.AlignLayersTop.CreateToolBarItem (force_icon_only: true));
		alignmentButtons.Append (PintaCore.Actions.Layers.AlignLayersCenterVertical.CreateToolBarItem (force_icon_only: true));
		alignmentButtons.Append (PintaCore.Actions.Layers.AlignLayersBottom.CreateToolBarItem (force_icon_only: true));

		SetOrientation (Gtk.Orientation.Horizontal);
		MarginStart = 8;
		MarginEnd = 8;
		MarginTop = 6;
		MarginBottom = 6;
		Append (alignmentButtons);
	}
}
