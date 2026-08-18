//
// LayerActions.BlendLowerIntoUpper.cs
//

namespace Pinta.Core;

public sealed partial class LayerActions
{
	private void HandleBlendLowerIntoUpperActivated (object sender, System.EventArgs e)
	{
		Document? document = workspace.ActiveDocumentOrDefault;
		if (!CanBlendLowerIntoUpper (document))
			return;

		tools.SetCurrentTool ("LayerBlendTool");
	}

	private static bool CanBlendLowerIntoUpper (Document? document)
	{
		if (document is null || !document.Layers.HasSelectedLayer)
			return false;

		UserLayer upper = document.Layers.CurrentUserLayer;
		if (!upper.IsEditable || !document.Layers.CanMoveCurrentLayerDown ())
			return false;

		return !document.Layers.GetSiblingBelow (upper).Hidden;
	}
}
