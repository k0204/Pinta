namespace Pinta.Core;

public sealed class MoveLayerHistoryItem : BaseHistoryItem
{
	private readonly UserLayer layer;
	private readonly LayerPosition old_position;
	private readonly LayerPosition new_position;

	public MoveLayerHistoryItem (
		string icon,
		string text,
		UserLayer layer,
		LayerPosition oldPosition,
		LayerPosition newPosition)
		: base (icon, text)
	{
		this.layer = layer;
		old_position = oldPosition;
		new_position = newPosition;
	}

	public override void Undo () => Move (old_position);

	public override void Redo () => Move (new_position);

	private void Move (LayerPosition position)
	{
		var document = PintaCore.Workspace.ActiveDocument;
		document.Layers.MoveLayer (layer, position);
		document.Layers.SetCurrentUserLayer (layer);
	}
}
