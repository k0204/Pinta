using System;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class KeyboardShortcutsDialogAction : IActionHandler
{
	private readonly AppActions app;
	private readonly ActionManager actions;
	private readonly ChromeManager chrome;
	private readonly ToolManager tools;

	internal KeyboardShortcutsDialogAction (
		AppActions app,
		ActionManager actions,
		ChromeManager chrome,
		ToolManager tools)
	{
		this.app = app;
		this.actions = actions;
		this.chrome = chrome;
		this.tools = tools;
	}

	void IActionHandler.Initialize ()
		=> app.KeyboardShortcuts.Activated += Activated;

	void IActionHandler.Uninitialize ()
		=> app.KeyboardShortcuts.Activated -= Activated;

	private async void Activated (object sender, EventArgs e)
	{
		using KeyboardShortcutsDialog dialog = new (actions, chrome, tools, PintaCore.Shortcuts);
		if (await dialog.RunAsync () == Gtk.ResponseType.Ok)
			PintaCore.Shortcuts.Apply (dialog.WorkingCopy);
	}
}
