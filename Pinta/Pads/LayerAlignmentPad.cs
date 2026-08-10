using Pinta.Core;
using Pinta.Docking;
using Pinta.Gui.Widgets;

namespace Pinta;

internal sealed class LayerAlignmentPad : IDockPad
{
	public void Initialize (Dock workspace)
	{
		LayerAlignmentWindow alignment = LayerAlignmentWindow.New ();
		DockItem item = DockItem.New (
			child: alignment,
			uniqueName: "LayerAlignment",
			iconName: Resources.Icons.ResizeCanvasBase);
		item.Label = Translations.GetString ("Layer Alignment");
		workspace.AddItem (item, DockPlacement.Top);
	}
}
