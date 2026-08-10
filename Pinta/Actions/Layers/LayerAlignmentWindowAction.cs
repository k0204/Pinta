using System;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class LayerAlignmentWindowAction : IActionHandler
{
	private readonly ChromeManager chrome;
	private readonly LayerActions layers;

	public LayerAlignmentWindowAction (ChromeManager chrome, LayerActions layers)
	{
		this.chrome = chrome;
		this.layers = layers;
	}

	void IActionHandler.Initialize ()
		=> layers.OpenLayerAlignmentWindow.Activated += HandleActivated;

	void IActionHandler.Uninitialize ()
		=> layers.OpenLayerAlignmentWindow.Activated -= HandleActivated;

	private void HandleActivated (object sender, EventArgs e)
		=> ((Docking.Dock) chrome.Dock).SetItemVisible ("LayerAlignment", true);
}
