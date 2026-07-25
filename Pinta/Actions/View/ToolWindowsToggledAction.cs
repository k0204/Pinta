using Pinta.Core;

namespace Pinta.Actions;

internal sealed class ToolWindowsToggledAction : IActionHandler
{
	private readonly ViewActions view;
	private readonly ChromeManager chrome;
	internal ToolWindowsToggledAction (
		ViewActions view,
		ChromeManager chrome)
	{
		this.view = view;
		this.chrome = chrome;
	}

	void IActionHandler.Initialize ()
	{
		view.ToolWindows.Toggled += Activated;
		view.LayersWindow.Toggled += LayersToggled;
		view.HistoryWindow.Toggled += HistoryToggled;
		view.ResourcesWindow.Toggled += ResourcesToggled;
		view.ResetDockLayout.Activated += ResetLayout;
		((Docking.Dock) chrome.Dock).ItemVisibilityChanged += HandleVisibilityChanged;
	}

	void IActionHandler.Uninitialize ()
	{
		view.ToolWindows.Toggled -= Activated;
		view.LayersWindow.Toggled -= LayersToggled;
		view.HistoryWindow.Toggled -= HistoryToggled;
		view.ResourcesWindow.Toggled -= ResourcesToggled;
		view.ResetDockLayout.Activated -= ResetLayout;
		((Docking.Dock) chrome.Dock).ItemVisibilityChanged -= HandleVisibilityChanged;
	}

	private void Activated (bool value, bool interactive)
	{
		((Docking.Dock) chrome.Dock).ToolWindowsVisible = value;
	}

	private void LayersToggled (bool value, bool interactive)
		=> ((Docking.Dock) chrome.Dock).SetItemVisible ("Layers", value);

	private void HistoryToggled (bool value, bool interactive)
		=> ((Docking.Dock) chrome.Dock).SetItemVisible ("History", value);

	private void ResourcesToggled (bool value, bool interactive)
		=> ((Docking.Dock) chrome.Dock).SetItemVisible ("Resources", value);

	private void ResetLayout (object sender, System.EventArgs e)
		=> ((Docking.Dock) chrome.Dock).ResetLayout ();

	private void HandleVisibilityChanged (object? sender, Docking.DockItemVisibilityChangedEventArgs e)
	{
		switch (e.ItemName) {
			case "Layers": view.LayersWindow.Value = e.Visible; break;
			case "History": view.HistoryWindow.Value = e.Visible; break;
			case "Resources": view.ResourcesWindow.Value = e.Visible; break;
		}
	}
}
