namespace Pinta.Core;

public sealed class ResizeLayerTreeHistoryItem : BaseHistoryItem
{
	private readonly Document document;
	private readonly UserLayer layer;
	private readonly RectangleD oldBounds;
	private readonly RectangleD newBounds;

	public ResizeLayerTreeHistoryItem (
		string icon,
		string text,
		Document document,
		UserLayer layer,
		RectangleD oldBounds,
		RectangleD newBounds)
		: base (icon, text)
	{
		this.document = document;
		this.layer = layer;
		this.oldBounds = oldBounds;
		this.newBounds = newBounds;
	}

	public override void Undo () => document.Layers.ResizeLayerTree (layer, newBounds, oldBounds);
	public override void Redo () => document.Layers.ResizeLayerTree (layer, oldBounds, newBounds);
}
