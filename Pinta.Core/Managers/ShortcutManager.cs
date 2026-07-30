using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace Pinta.Core;

public sealed class ShortcutManager
{
	private const string command_prefix = "command:";
	private const string tool_group_prefix = "tool-group:";
	private const string tool_prefix = "tool:";

	private readonly SettingsManager settings;
	private readonly Dictionary<string, string?> overrides;
	private readonly Dictionary<string, (Gtk.Application Application, Command Command)> commands = [];

	public ShortcutManager (SettingsManager settings)
	{
		this.settings = settings;
		overrides = LoadOverrides (settings);
	}

	public event EventHandler? ToolShortcutsChanged;

	public IEnumerable<Command> RegisteredCommands => commands.Values.Select (item => item.Command);

	public static string GetCommandId (Command command)
		=> command_prefix + command.FullName;

	public static string GetToolId (BaseTool tool)
	{
		Gdk.Key key = tool.ShortcutKey.ToUpper ();
		return key == Gdk.Key.Invalid
			? tool_prefix + (tool.GetType ().FullName ?? tool.GetType ().Name)
			: tool_group_prefix + key.Name ();
	}

	public Gdk.Key GetToolShortcut (BaseTool tool)
	{
		if (!overrides.TryGetValue (GetToolId (tool), out string? shortcut))
			return tool.ShortcutKey;

		if (string.IsNullOrEmpty (shortcut))
			return Gdk.Key.Invalid;

		uint key = Gdk.Functions.KeyvalFromName (shortcut);
		return key == 0 ? tool.ShortcutKey : new Gdk.Key (key);
	}

	public void RegisterCommand (Gtk.Application application, Command command)
	{
		commands[GetCommandId (command)] = (application, command);
		ApplyCommandOverride (application, command);
	}

	public Dictionary<string, string?> CreateWorkingCopy ()
		=> new (overrides);

	public void Apply (IReadOnlyDictionary<string, string?> updatedOverrides)
	{
		overrides.Clear ();
		foreach ((string id, string? shortcut) in updatedOverrides)
			overrides[id] = shortcut;

		foreach ((Gtk.Application application, Command command) in commands.Values)
			ApplyCommandOverride (application, command);

		ToolShortcutsChanged?.Invoke (this, EventArgs.Empty);
		settings.PutSetting (SettingNames.KEYBOARD_SHORTCUTS, JsonSerializer.Serialize (overrides));
		settings.DoSaveSettingsBeforeQuit ();
	}

	private void ApplyCommandOverride (Gtk.Application application, Command command)
	{
		ImmutableArray<string> shortcuts = command.DefaultShortcuts;
		if (overrides.TryGetValue (GetCommandId (command), out string? shortcut))
			shortcuts = string.IsNullOrEmpty (shortcut) ? [] : [shortcut];

		command.SetShortcuts (shortcuts);
		application.SetAccelsForAction (
			command.FullName,
			[.. shortcuts.Select (PintaCore.System.ConvertPrimaryKey)]);
	}

	private static Dictionary<string, string?> LoadOverrides (SettingsManager settings)
	{
		string json = settings.GetSetting (SettingNames.KEYBOARD_SHORTCUTS, string.Empty);
		if (string.IsNullOrWhiteSpace (json))
			return [];

		try {
			return JsonSerializer.Deserialize<Dictionary<string, string?>> (json) ?? [];
		} catch (JsonException ex) {
			Console.Error.WriteLine ($"Failed to load keyboard shortcuts: {ex.Message}");
			return [];
		}
	}
}
