namespace Pinta.Core;

public sealed class MoveSpriteSheetHistoryItem : BaseHistoryItem
{
	private readonly Document document;
	private readonly SpriteSheetLayer layer;
	private readonly PointD old_offset;
	private readonly PointD new_offset;

	public MoveSpriteSheetHistoryItem (
		string icon,
		string text,
		Document document,
		SpriteSheetLayer layer,
		PointD oldOffset,
		PointD newOffset)
		: base (icon, text)
	{
		this.document = document;
		this.layer = layer;
		old_offset = oldOffset;
		new_offset = newOffset;
	}

	public override void Undo () => layer.SetPositionOffset (old_offset, document.ImageSize);
	public override void Redo () => layer.SetPositionOffset (new_offset, document.ImageSize);
}
