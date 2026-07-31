using System.Threading.Tasks;
using Pinta.Core;
using Pinta.Gui.Widgets;

namespace Pinta;

internal sealed partial class MainWindow
{
	private bool HandleFocusedEntryShortcut (Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (!args.State.IsControlPressed ())
			return false;

		Gtk.Entry? entry = FindFocusedEntry ();
		Gtk.Editable? editable = entry?.GetDelegate ();
		if (entry is null || editable is null || !entry.IsEditingText ())
			return false;

		uint key = args.GetKey ().ToUpper ().Value;
		if (key == Gdk.Constants.KEY_C) {
			if (editable.GetSelectionBounds (out int start, out int end))
				GdkExtensions.GetDefaultClipboard ().SetText (editable.GetText ()[start..end]);
			return true;
		}

		if (key != Gdk.Constants.KEY_V)
			return false;

		_ = PasteFocusedEntryAsync (entry, editable);
		return true;
	}

	private void HandleGlobalPointerPressed (
		Gtk.GestureClick controller,
		Gtk.GestureClick.PressedSignalArgs args)
	{
		Gtk.Entry? entry = FindFocusedEntry ();
		if (entry is null || IsInsideEntry (entry, args.X, args.Y))
			return;

		Gtk.Root? root = entry.GetRoot ();
		if (root is null)
			return;

		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_DEFAULT_IDLE, () => {
			RestoreCanvasFocusIfEntryStillFocused (entry, root);
			return false;
		});
	}

	private bool IsInsideEntry (Gtk.Entry entry, double x, double y)
	{
		if (!entry.TranslateCoordinates (window_shell.Window, PointD.Zero, out PointD origin))
			return false;

		return x >= origin.X
			&& y >= origin.Y
			&& x < origin.X + entry.GetWidth ()
			&& y < origin.Y + entry.GetHeight ();
	}

	private static void RestoreCanvasFocusIfEntryStillFocused (Gtk.Entry entry, Gtk.Root root)
	{
		if (!entry.IsEditingText ())
			return;

		root.SetFocus (null);
		if (PintaCore.Workspace.HasOpenDocuments)
			root.SetFocus ((CanvasWindow) PintaCore.Workspace.ActiveWorkspace.CanvasWindow);
	}

	private Gtk.Entry? FindFocusedEntry ()
	{
		for (Gtk.Widget? widget = window_shell.Window.FocusWidget; widget is not null; widget = widget.Parent) {
			if (widget is Gtk.Entry entry)
				return entry;
		}

		return null;
	}

	private async Task PasteFocusedEntryAsync (Gtk.Entry entry, Gtk.Editable editable)
	{
		string? text = await GdkExtensions.GetDefaultClipboard ().ReadTextAsync ();
		if (text is null || !entry.IsEditingText ())
			return;

		editable.DeleteSelection ();
		int position = editable.GetPosition ();
		editable.InsertText (text, -1, ref position);
		editable.SetPosition (position);
	}
}
