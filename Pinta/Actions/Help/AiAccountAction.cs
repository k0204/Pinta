using System;
using System.Threading.Tasks;
using Pinta.Core;
using Pinta.Core.AI;

namespace Pinta.Actions;

internal sealed class AiAccountAction : IActionHandler
{
	private readonly AppActions app;
	private readonly ChromeManager chrome;
	private readonly AiAuthService auth;

	internal AiAccountAction (
		AppActions app,
		ChromeManager chrome,
		AiAuthService auth)
	{
		this.app = app;
		this.chrome = chrome;
		this.auth = auth;
	}

	void IActionHandler.Initialize ()
	{
		app.AiAccount.Activated += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		app.AiAccount.Activated -= Activated;
	}

	private async void Activated (object sender, EventArgs e)
	{
		if (auth.IsLoggedIn) {
			try {
				await auth.RefreshAccountSummaryAsync ();
				PintaCore.Settings.DoSaveSettingsBeforeQuit ();
				await chrome.ShowMessageDialog (
					chrome.MainWindow,
					Translations.GetString ("AI Account"),
					auth.AccountSummary);
				return;
			} catch (AiAuthenticationException) {
				PintaCore.Settings.DoSaveSettingsBeforeQuit ();
			} catch (Exception ex) {
				await chrome.ShowErrorDialog (
					chrome.MainWindow,
					Translations.GetString ("AI Account Failed"),
					GetErrorMessage (ex),
					ex.Message);
				return;
			}
		}

		using AiAccountDialog dialog = AiAccountDialog.New (chrome.MainWindow, auth);

		try {
			while (true) {
				Gtk.ResponseType response = await dialog.RunAsync ();
				if (response == Gtk.ResponseType.Cancel || response == Gtk.ResponseType.DeleteEvent)
					return;

				try {
					if (!await ValidateInputAsync (dialog, response == Gtk.ResponseType.Apply))
						continue;
					dialog.Hide ();

					if (response == Gtk.ResponseType.Apply)
						await auth.RegisterAsync (dialog.ApiBaseUri, dialog.Username, dialog.Password);
					else
						await auth.LoginAsync (dialog.ApiBaseUri, dialog.Username, dialog.Password);

					PintaCore.Settings.DoSaveSettingsBeforeQuit ();
					await chrome.ShowMessageDialog (
						chrome.MainWindow,
						Translations.GetString ("AI Account"),
						auth.AccountSummary);
					return;
				} catch (Exception ex) {
					dialog.Present ();
					await chrome.ShowErrorDialog (
						dialog,
						Translations.GetString ("AI Account Failed"),
						GetErrorMessage (ex),
						ex.Message);
				}
			}
		} finally {
			dialog.Destroy ();
		}
	}

	private static string GetErrorMessage (Exception ex)
	{
		if (ex.Message.Contains ("Incorrect email or password", StringComparison.OrdinalIgnoreCase))
			return Translations.GetString ("Incorrect email or password.");

		if (ex.Message.Contains ("Email already registered", StringComparison.OrdinalIgnoreCase))
			return Translations.GetString ("Email already registered.");

		return Translations.GetString ("Check the API server, email, and password, then try again.");
	}

	private async Task<bool> ValidateInputAsync (AiAccountDialog dialog, bool registering)
	{
		if (string.IsNullOrWhiteSpace (dialog.ApiBaseUri) ||
			string.IsNullOrWhiteSpace (dialog.Username) ||
			string.IsNullOrWhiteSpace (dialog.Password)) {
			await chrome.ShowMessageDialog (
				dialog,
				Translations.GetString ("AI Account"),
				Translations.GetString ("Enter the API server, email, and password."));
			return false;
		}

		if (!dialog.Username.Contains ('@')) {
			await chrome.ShowMessageDialog (
				dialog,
				Translations.GetString ("AI Account"),
				Translations.GetString ("Enter a valid email address."));
			return false;
		}

		if (registering && dialog.Password.Length < 8) {
			await chrome.ShowMessageDialog (
				dialog,
				Translations.GetString ("AI Account"),
				Translations.GetString ("Password must be at least 8 characters."));
			return false;
		}

		return true;
	}
}
