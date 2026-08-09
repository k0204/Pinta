using System;
using Cairo;

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private void HandleResetLayerPositionActivated (object sender, EventArgs e)
	{
		Document document = workspace.ActiveDocument;
		if (document.Layers.CurrentUserLayer is not AnimationOutputLayer layer
			|| layer.PositionOffset == PointD.Zero)
			return;

		tools.Commit ();

		PointD delta = new (-layer.PositionOffset.X, -layer.PositionOffset.Y);
		document.Layers.TranslateLayerTree (layer, delta);
		document.History.PushNewItem (new MoveLayerTreeHistoryItem (
			Resources.StandardIcons.ViewRefresh,
			Translations.GetString ("Reset Layer Position"),
			document,
			layer,
			delta));
	}
}
