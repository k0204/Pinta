using System;
using System.Diagnostics.CodeAnalysis;
using Pinta.Core;
using Pinta.Resources;

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
			Icons.LayerLocked,
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
	private Gtk.Button lock_button;
	private Gtk.Image lock_icon;

	[MemberNotNull (nameof (lock_button), nameof (lock_icon))]
	private Gtk.Button CreateLockButton ()
	{
		lock_button = Gtk.Button.New ();
		lock_icon = Gtk.Image.NewFromIconName (Icons.LayerUnlocked);
		lock_icon.PixelSize = 18;
		lock_button.SetChild (lock_icon);
		lock_button.WidthRequest = 24;
		lock_button.HeightRequest = 24;
		lock_button.FocusOnClick = false;
		lock_button.OnClicked += (_, _) => {
			if (item is not null)
				item.HandleLockToggled (!item.Locked);
		};
		return lock_button;
	}

	private void UpdateLockButton ()
	{
		if (item is null)
			throw new InvalidOperationException ($"{nameof (item)} is null");

		lock_icon.IconName = item.Locked ? Icons.LayerLocked : Icons.LayerUnlocked;
		lock_button.TooltipText = item.Locked
			? Translations.GetString ("Unlock Layer")
			: Translations.GetString ("Lock Layer");
		lock_button.Visible = true;
		lock_button.Sensitive = true;
	}

	private void ShowLockedLayerMenu ()
	{
		Gtk.Popover popover = Gtk.Popover.New ();
		Gtk.Button unlockButton = Gtk.Button.NewWithLabel (Translations.GetString ("Unlock Layer"));
		unlockButton.Halign = Gtk.Align.Fill;
		unlockButton.AddCssClass (AdwaitaStyles.Flat);
		unlockButton.OnClicked += (_, _) => {
			popover.Popdown ();
			item?.HandleLockToggled (false);
		};
		popover.Child = unlockButton;
		popover.SetParent (this);
		popover.Popup ();
	}
}
