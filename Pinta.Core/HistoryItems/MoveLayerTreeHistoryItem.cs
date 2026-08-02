namespace Pinta.Core;

public sealed class MoveLayerTreeHistoryItem : BaseHistoryItem
{
	private readonly Document document;
	private readonly UserLayer layer;
	private readonly PointD delta;

	public MoveLayerTreeHistoryItem (
		string icon,
		string text,
		Document document,
		UserLayer layer,
		PointD delta)
		: base (icon, text)
	{
		this.document = document;
		this.layer = layer;
		this.delta = delta;
	}

	public override void Undo () => document.Layers.TranslateLayerTree (layer, new PointD (-delta.X, -delta.Y));
	public override void Redo () => document.Layers.TranslateLayerTree (layer, delta);
}
