using System;
using System.Diagnostics.CodeAnalysis;
using GObject;
using Pinta.Core;

namespace Pinta.Gui.Widgets;

public sealed partial class LayersListViewItem
{
	public bool Locked => UserLayer?.Locked ?? false;

	public void HandleLockToggled (bool locked)
	{
		if (document is null || UserLayer is null || UserLayer.Locked == locked)
			return;

		Document doc = PintaCore.Workspace.ActiveDocument;
		LayerLockHistoryItem historyItem = new (
			"object-locked-symbolic",
			locked ? Translations.GetString ("Lock Layer") : Translations.GetString ("Unlock Layer"),
			UserLayer,
			UserLayer.Locked,
			locked);

		historyItem.Redo ();
		doc.History.PushNewItem (historyItem);
	}
}

public sealed partial class LayersListViewItemWidget
{
	private Gtk.Image lock_button;

	[MemberNotNull (nameof (lock_button))]
	private Gtk.Image CreateLockButton ()
	{
		lock_button = Gtk.Image.New ();
		lock_button.WidthRequest = 16;
		lock_button.Halign = Gtk.Align.Start;
		lock_button.Valign = Gtk.Align.Center;
		AddActionButtonGesture (lock_button, () => item?.HandleLockToggled (!item.Locked), selectLayer: false);
		return lock_button;
	}

	private void UpdateLockButton ()
	{
		if (item is null)
			throw new InvalidOperationException ($"{nameof (item)} is null");

		lock_button.IconName = item.Locked ? "object-locked-symbolic" : "object-unlocked-symbolic";
		lock_button.TooltipText = item.Locked
			? Translations.GetString ("Unlock Layer")
			: Translations.GetString ("Lock Layer");
		lock_button.Visible = true;
		lock_button.Sensitive = true;
	}
}
