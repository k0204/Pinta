using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta;

internal sealed class KeyboardShortcutsDialog : IDisposable
{
	private readonly Gtk.Dialog dialog;
	private readonly ActionManager actions;
	private readonly ToolManager tools;
	private readonly Dictionary<string, string?> working;
	private readonly List<Binding> bindings;
	private readonly Gtk.SearchEntry search = Gtk.SearchEntry.New ();
	private readonly Gtk.ListBox list = Gtk.ListBox.New ();
	private readonly Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
	private readonly Gtk.ToggleButton commands_button;
	private readonly Gtk.ToggleButton tools_button;
	private readonly Gtk.Label status = Gtk.Label.New (string.Empty);
	private Binding? capturing;

	internal KeyboardShortcutsDialog (
		ActionManager actions,
		ChromeManager chrome,
		ToolManager tools,
		ShortcutManager shortcuts)
	{
		this.actions = actions;
		this.tools = tools;
		working = shortcuts.CreateWorkingCopy ();
		bindings = BuildBindings (shortcuts.RegisteredCommands);

		dialog = Gtk.Dialog.New ();
		dialog.Title = Translations.GetString ("Keyboard Shortcuts");
		dialog.TransientFor = chrome.MainWindow;
		dialog.Modal = true;
		dialog.DefaultWidth = 680;
		dialog.DefaultHeight = 680;

		Gtk.Widget resetAll = dialog.AddButton (
			Translations.GetString ("Reset All"),
			(int) Gtk.ResponseType.Apply);
		resetAll.AddCssClass (AdwaitaStyles.DestructiveAction);
		dialog.AddCancelOkButtons ();

		commands_button = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Commands"));
		tools_button = Gtk.ToggleButton.NewWithLabel (Translations.GetString ("Tools"));
		ConfigureContent ();
		RebuildList ();
	}

	internal IReadOnlyDictionary<string, string?> WorkingCopy => working;

	internal Task<Gtk.ResponseType> RunAsync ()
		=> RunUntilClosedAsync ();

	public void Dispose ()
		=> dialog.Dispose ();

	private void ConfigureContent ()
	{
		commands_button.Group = tools_button;
		commands_button.Active = true;
		commands_button.OnToggled += (_, _) => RebuildList ();
		tools_button.OnToggled += (_, _) => RebuildList ();

		Gtk.Box modes = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
		modes.AddCssClass (AdwaitaStyles.Linked);
		modes.Halign = Gtk.Align.Center;
		modes.Append (commands_button);
		modes.Append (tools_button);

		search.PlaceholderText = Translations.GetString ("Search shortcuts");
		search.OnSearchChanged += (_, _) => RebuildList ();

		list.SelectionMode = Gtk.SelectionMode.None;
		list.AddCssClass ("boxed-list");

		scroll.Child = list;
		scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
		scroll.VscrollbarPolicy = Gtk.PolicyType.Automatic;
		scroll.Vexpand = true;

		status.Halign = Gtk.Align.Start;
		status.Wrap = true;
		status.AddCssClass (AdwaitaStyles.Error);

		Gtk.Box content = dialog.GetContentAreaBox ();
		content.Spacing = 10;
		content.SetAllMargins (12);
		content.Append (modes);
		content.Append (search);
		content.Append (scroll);
		content.Append (status);

		Gtk.EventControllerKey keys = Gtk.EventControllerKey.New ();
		keys.PropagationPhase = Gtk.PropagationPhase.Capture;
		keys.OnKeyPressed += HandleKeyPressed;
		dialog.AddController (keys);
	}

	private async Task<Gtk.ResponseType> RunUntilClosedAsync ()
	{
		while (true) {
			Gtk.ResponseType response = await dialog.RunAsync ();
			if (response != Gtk.ResponseType.Apply) {
				dialog.Close ();
				return response;
			}

			await ResetAllAsync ();
		}
	}

	private void RebuildList ()
	{
		list.RemoveAll ();
		capturing = null;
		status.SetText (string.Empty);

		string filter = search.GetText ().Trim ();
		bool showTools = tools_button.Active;
		var visible = bindings
			.Where (item => item.IsTool == showTools)
			.Where (item => filter.Length == 0 || item.Label.Contains (filter, StringComparison.CurrentCultureIgnoreCase))
			.GroupBy (item => item.Category);

		bool any = false;
		foreach (var category in visible) {
			any = true;
			list.Append (CreateCategoryLabel (category.Key));
			foreach (Binding binding in category.OrderBy (item => item.Label))
				list.Append (CreateRow (binding));
		}

		if (!any)
			list.Append (CreateCategoryLabel (Translations.GetString ("No shortcuts found")));

		GLib.Functions.IdleAdd (GLib.Constants.PRIORITY_LOW, (() => {
			scroll.Vadjustment?.SetValue (0);
			return false;
		}));
	}

	private static Gtk.Label CreateCategoryLabel (string text)
	{
		Gtk.Label label = Gtk.Label.New (text);
		label.Halign = Gtk.Align.Start;
		label.SetAllMargins (8);
		label.AddCssClass (AdwaitaStyles.Heading);
		return label;
	}

	private Gtk.Widget CreateRow (Binding binding)
	{
		Gtk.Label name = Gtk.Label.New (binding.Label);
		name.Halign = Gtk.Align.Start;
		name.Hexpand = true;
		name.WidthChars = 24;
		name.Ellipsize = Pango.EllipsizeMode.End;

		Gtk.Button shortcut = Gtk.Button.NewWithLabel (FormatShortcuts (GetEffectiveShortcuts (binding)));
		shortcut.WidthRequest = 170;
		shortcut.OnClicked += (_, _) => BeginCapture (binding, shortcut);

		Gtk.Button clear = Gtk.Button.NewFromIconName (Resources.StandardIcons.WindowClose);
		clear.TooltipText = Translations.GetString ("Clear Shortcut");
		clear.Sensitive = GetEffectiveShortcuts (binding).Length > 0;
		clear.OnClicked += (_, _) => {
			working[binding.Id] = null;
			RebuildList ();
		};

		Gtk.Button reset = Gtk.Button.NewFromIconName (Resources.StandardIcons.EditUndo);
		reset.TooltipText = Translations.GetString ("Reset Shortcut");
		reset.Sensitive = working.ContainsKey (binding.Id);
		reset.OnClicked += async (_, _) => await ResetBindingAsync (binding);

		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);
		row.SetAllMargins (8);
		row.Append (name);
		row.Append (shortcut);
		row.Append (clear);
		row.Append (reset);
		return row;
	}

	private void BeginCapture (Binding binding, Gtk.Button button)
	{
		capturing = binding;
		status.SetText (string.Empty);
		button.Label = Translations.GetString ("Press shortcut");
		button.GrabFocus ();
	}

	private bool HandleKeyPressed (
		Gtk.EventControllerKey controller,
		Gtk.EventControllerKey.KeyPressedSignalArgs args)
	{
		if (capturing is not Binding binding)
			return false;

		Gdk.Key key = args.GetKey ();
		if (key.Value == Gdk.Constants.KEY_Escape) {
			RebuildList ();
			return true;
		}

		if (IsModifierKey (key))
			return true;
		if (args.State.HasFlag (Gdk.ModifierType.SuperMask)
			|| args.State.HasFlag (Gdk.ModifierType.HyperMask)) {
			status.SetText (Translations.GetString ("This modifier key is not supported."));
			return true;
		}

		Gdk.ModifierType modifiers = GetShortcutModifiers (args.State);
		if (binding.IsTool && modifiers != 0) {
			status.SetText (Translations.GetString ("Tool shortcuts cannot use modifier keys."));
			return true;
		}

		if (modifiers == 0 && IsReservedKey (key)) {
			status.SetText (Translations.GetString ("This key is reserved for canvas controls."));
			return true;
		}

		string shortcut = binding.IsTool
			? key.ToUpper ().Name ()
			: ToPortableAccelerator (key, modifiers);
		capturing = null;
		_ = AssignAsync (binding, shortcut);
		return true;
	}

	private async Task AssignAsync (Binding binding, string shortcut)
	{
		Binding[] conflicts = bindings
			.Where (item => item.Id != binding.Id)
			.Where (item => GetEffectiveShortcuts (item).Any (value => ShortcutEquals (value, shortcut)))
			.ToArray ();

		if (conflicts.Length > 0 && !await ConfirmReplaceAsync (conflicts, FormatShortcut (shortcut))) {
			RebuildList ();
			return;
		}

		foreach (Binding conflict in conflicts)
			working[conflict.Id] = null;

		working[binding.Id] = shortcut;
		RebuildList ();
	}

	private async Task ResetBindingAsync (Binding binding)
	{
		Binding[] conflicts = bindings
			.Where (item => item.Id != binding.Id)
			.Where (item => GetEffectiveShortcuts (item).Any (
				current => binding.Defaults.Any (value => ShortcutEquals (current, value))))
			.ToArray ();

		if (conflicts.Length > 0
			&& !await ConfirmReplaceAsync (conflicts, FormatShortcuts (binding.Defaults)))
			return;

		foreach (Binding conflict in conflicts)
			working[conflict.Id] = null;

		working.Remove (binding.Id);
		RebuildList ();
	}

	private async Task<bool> ConfirmReplaceAsync (IReadOnlyCollection<Binding> conflicts, string shortcutLabel)
	{
		string names = string.Join (", ", conflicts.Select (item => item.Label));
		using Adw.MessageDialog message = Adw.MessageDialog.New (
			dialog,
			Translations.GetString ("Shortcut already in use"),
			Translations.GetString ("{0} is assigned to {1}.", shortcutLabel, names));
		message.AddResponse ("cancel", Translations.GetString ("_Cancel"));
		message.AddResponse ("replace", Translations.GetString ("Replace"));
		message.SetResponseAppearance ("replace", Adw.ResponseAppearance.Destructive);
		message.CloseResponse = "cancel";
		message.DefaultResponse = "replace";
		return await message.RunAsync () == "replace";
	}

	private async Task ResetAllAsync ()
	{
		using Adw.MessageDialog message = Adw.MessageDialog.New (
			dialog,
			Translations.GetString ("Reset all shortcuts?"),
			Translations.GetString ("All custom keyboard shortcuts will be removed."));
		message.AddResponse ("cancel", Translations.GetString ("_Cancel"));
		message.AddResponse ("reset", Translations.GetString ("Reset All"));
		message.SetResponseAppearance ("reset", Adw.ResponseAppearance.Destructive);
		message.CloseResponse = "cancel";
		message.DefaultResponse = "reset";
		if (await message.RunAsync () != "reset")
			return;

		working.Clear ();
		RebuildList ();
	}

	private ImmutableArray<string> GetEffectiveShortcuts (Binding binding)
	{
		if (!working.TryGetValue (binding.Id, out string? shortcut))
			return binding.Defaults;
		return string.IsNullOrEmpty (shortcut) ? [] : [shortcut];
	}

	private static string FormatShortcuts (ImmutableArray<string> shortcuts)
		=> shortcuts.Length == 0
			? Translations.GetString ("None")
			: string.Join (", ", shortcuts.Select (FormatShortcut));

	private static string FormatShortcut (string shortcut)
		=> GtkExtensions.ReadableAcceleratorLabel (shortcut);

	private static bool ShortcutEquals (string left, string right)
		=> string.Equals (ForComparison (left), ForComparison (right), StringComparison.OrdinalIgnoreCase);

	private static string ForComparison (string shortcut)
	{
		string normalized = shortcut.Replace ("<Ctrl>", "<Control>");
		return PintaCore.System.OperatingSystem == OS.Mac
			? normalized.Replace ("<Primary>", "<Meta>")
			: normalized.Replace ("<Primary>", "<Control>");
	}

	private static Gdk.ModifierType GetShortcutModifiers (Gdk.ModifierType state)
		=> state & (Gdk.ModifierType.ShiftMask
			| Gdk.ModifierType.ControlMask
			| Gdk.ModifierType.AltMask
			| Gdk.ModifierType.MetaMask);

	private static string ToPortableAccelerator (Gdk.Key key, Gdk.ModifierType modifiers)
	{
		string accelerator = Gtk.Functions.AcceleratorName (key.Value, modifiers);
		return PintaCore.System.OperatingSystem == OS.Mac
			? accelerator.Replace ("<Meta>", "<Primary>")
			: accelerator.Replace ("<Control>", "<Primary>");
	}

	private static bool IsModifierKey (Gdk.Key key)
		=> key.Value is Gdk.Constants.KEY_Shift_L or Gdk.Constants.KEY_Shift_R
			or Gdk.Constants.KEY_Control_L or Gdk.Constants.KEY_Control_R
			or Gdk.Constants.KEY_Alt_L or Gdk.Constants.KEY_Alt_R
			or Gdk.Constants.KEY_Meta_L or Gdk.Constants.KEY_Meta_R
			or Gdk.Constants.KEY_Super_L or Gdk.Constants.KEY_Super_R;

	private static bool IsReservedKey (Gdk.Key key)
		=> key.ToUpper ().Value is Gdk.Constants.KEY_X
			or Gdk.Constants.KEY_space
			or Gdk.Constants.KEY_bracketleft
			or Gdk.Constants.KEY_bracketright
			or Gdk.Constants.KEY_Tab
			or Gdk.Constants.KEY_Return
			or Gdk.Constants.KEY_KP_Enter
			or Gdk.Constants.KEY_Left
			or Gdk.Constants.KEY_Right
			or Gdk.Constants.KEY_Up
			or Gdk.Constants.KEY_Down
			or Gdk.Constants.KEY_Home
			or Gdk.Constants.KEY_End
			or Gdk.Constants.KEY_Page_Up
			or Gdk.Constants.KEY_Page_Down;

	private List<Binding> BuildBindings (IEnumerable<Command> registeredCommands)
	{
		Dictionary<Command, string> categories = BuildCommandCategories ();
		List<Binding> result = registeredCommands
			.Distinct ()
			.Select (command => new Binding (
				ShortcutManager.GetCommandId (command),
				categories.GetValueOrDefault (command, Translations.GetString ("Other")),
				command.Label.Replace ("_", string.Empty),
				false,
				command.DefaultShortcuts))
			.ToList ();

		result.AddRange (tools
			.GroupBy (ShortcutManager.GetToolId)
			.Select (group => {
				BaseTool first = group.First ();
				ImmutableArray<string> defaults = first.ShortcutKey == Gdk.Key.Invalid
					? []
					: [first.ShortcutKey.ToUpper ().Name ()];
				return new Binding (
					group.Key,
					Translations.GetString ("Tools"),
					string.Join (" / ", group.Select (tool => tool.Name)),
					true,
					defaults);
			}));

		return result;
	}

	private Dictionary<Command, string> BuildCommandCategories ()
	{
		Dictionary<Command, string> result = [];
		AddCategory (result, Translations.GetString ("Application"), GetCommands (actions.App));
		AddCategory (result, Translations.GetString ("File"), GetCommands (actions.File));
		AddCategory (result, Translations.GetString ("Edit"), GetCommands (actions.Edit));
		AddCategory (result, Translations.GetString ("View"), GetCommands (actions.View));
		AddCategory (result, Translations.GetString ("Image"), GetCommands (actions.Image));
		AddCategory (result, Translations.GetString ("Layers"), GetCommands (actions.Layers));
		AddCategory (result, Translations.GetString ("Adjustments"), actions.Adjustments.Actions);
		AddCategory (result, Translations.GetString ("Effects"), actions.Effects.Actions);
		AddCategory (result, Translations.GetString ("Window"), GetCommands (actions.Window));
		AddCategory (result, Translations.GetString ("Help"), GetCommands (actions.Help));
		AddCategory (result, Translations.GetString ("Add-ins"), GetCommands (actions.Addins));
		return result;
	}

	private static void AddCategory (
		Dictionary<Command, string> categories,
		string category,
		IEnumerable<Command> commands)
	{
		foreach (Command command in commands)
			categories[command] = category;
	}

	private static IEnumerable<Command> GetCommands (object collection)
		=> collection.GetType ()
			.GetProperties (BindingFlags.Instance | BindingFlags.Public)
			.Where (property => typeof (Command).IsAssignableFrom (property.PropertyType))
			.Select (property => property.GetValue (collection))
			.OfType<Command> ();

	private sealed record Binding (
		string Id,
		string Category,
		string Label,
		bool IsTool,
		ImmutableArray<string> Defaults);
}
