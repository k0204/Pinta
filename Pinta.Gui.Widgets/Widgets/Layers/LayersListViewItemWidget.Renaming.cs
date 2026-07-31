using Pinta.Core;

namespace Pinta.Gui.Widgets;

public sealed partial class LayersListViewItemWidget
{
	private Gtk.Entry? name_entry;
	private Gtk.Stack? name_editor;
	private bool canceling_rename;
	private bool IsRenaming => name_editor?.VisibleChildName == "entry";

	private Gtk.Widget CreateNameEditor (Gtk.Label label)
	{
		Gtk.GestureClick renameGesture = Gtk.GestureClick.New ();
		renameGesture.SetButton (GtkExtensions.MOUSE_LEFT_BUTTON);
		renameGesture.OnPressed += (_, args) => {
			if (args.NPress != 2)
				return;

			renameGesture.SetState (Gtk.EventSequenceState.Claimed);
			UserLayer? layer = item?.UserLayer;
			GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT_IDLE, () => {
				if (item?.UserLayer == layer)
					BeginRename ();
				return false;
			});
		};
		label.AddController (renameGesture);

		Gtk.Entry entry = Gtk.Entry.New ();
		entry.Hexpand = true;
		entry.OnActivate += (_, _) => CommitRename ();
		(entry.GetFirstChild () ?? entry).OnMap += (_, _) =>
			GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT_IDLE, () => {
				FocusNameEntry (entry);
				return false;
			});

		Gtk.EventControllerFocus focus = Gtk.EventControllerFocus.New ();
		focus.OnLeave += (_, _) => CommitRename ();
		entry.AddController (focus);

		Gtk.EventControllerKey keys = Gtk.EventControllerKey.New ();
		keys.SetPropagationPhase (Gtk.PropagationPhase.Capture);
		keys.OnKeyPressed += HandleRenameKeyPressed;
		entry.AddController (keys);

		Gtk.Stack editor = Gtk.Stack.New ();
		editor.Hexpand = true;
		editor.AddNamed (label, "label");
		editor.AddNamed (entry, "entry");
		editor.VisibleChildName = "label";

		name_entry = entry;
		name_editor = editor;
		return editor;
	}

	public void BeginRename ()
	{
		if (item?.UserLayer is null || name_entry is null || name_editor is null)
			return;

		name_entry.SetText (item.UserLayer.Name);
		name_editor.VisibleChildName = "entry";
	}

	private void FocusNameEntry (Gtk.Entry entry)
	{
		if (name_entry != entry || !IsRenaming)
			return;

		Gtk.Widget target = entry.GetFirstChild () ?? entry;
		entry.GetRoot ()?.SetFocus (target);
		entry.SelectRegion (0, (int) entry.TextLength);
	}

	private bool HandleRenameKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (args.GetKey ().Value != Gdk.Constants.KEY_Escape)
			return false;

		canceling_rename = true;
		EndRename ();
		return true;
	}

	internal void CommitRename ()
	{
		if (name_editor?.VisibleChildName != "entry" || item?.UserLayer is not UserLayer layer)
			return;

		string name = name_entry!.GetText ().Trim ();
		if (!canceling_rename && name.Length > 0 && name != layer.Name) {
			LayerProperties initial = new (layer.Name, layer.Hidden, layer.Opacity, layer.BlendMode);
			LayerProperties updated = initial with { Name = name };
			UpdateLayerPropertiesHistoryItem historyItem = new (
				Resources.Icons.LayerProperties,
				Translations.GetString ("Rename Layer"),
				layer,
				initial,
				updated);

			historyItem.Redo ();
			PintaCore.Workspace.ActiveDocument.History.PushNewItem (historyItem);
		}

		EndRename ();
	}

	private void EndRename ()
	{
		if (name_editor is not null)
			name_editor.VisibleChildName = "label";

		canceling_rename = false;
	}
}
