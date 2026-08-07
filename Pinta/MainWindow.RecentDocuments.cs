using Pinta.Core;

namespace Pinta;

internal sealed partial class MainWindow
{
	public void ShowMainWindow ()
	{
		if (!PintaCore.Workspace.HasOpenDocuments) {
			PintaCore.Workspace.NewDocument (
				new Core.Size (800, 600),
				new Cairo.Color (1, 1, 1));
		}

		window_shell.Window.Present ();
	}
}
