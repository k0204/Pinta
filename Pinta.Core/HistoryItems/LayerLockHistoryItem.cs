namespace Pinta.Core;

public sealed class LayerLockHistoryItem : BaseHistoryItem
{
	private readonly UserLayer layer;
	private readonly bool old_locked;
	private readonly bool new_locked;

	public LayerLockHistoryItem (
		string icon,
		string text,
		UserLayer layer,
		bool oldLocked,
		bool newLocked)
		: base (icon, text)
	{
		this.layer = layer;
		old_locked = oldLocked;
		new_locked = newLocked;
	}

	public override void Undo () => layer.Locked = old_locked;

	public override void Redo () => layer.Locked = new_locked;
}
