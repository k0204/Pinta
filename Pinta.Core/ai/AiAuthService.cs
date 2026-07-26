using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

public sealed class AiAuthService
{
	//public const string DefaultBaseUri = "http://101.42.29.148:8080/";
	public const string DefaultBaseUri = "http://localhost:8080/";
	private const string API_BASE_URI = "ai-api-base-uri";
	private const string USERNAME = "ai-username";
	private const string TOKEN = "ai-token";
	private const string ACCOUNT_SUMMARY = "ai-account-summary";
	private const string REQUEST_ADDRESS_FILE = "request-address.txt";

	private string? api_base_uri;
	private readonly ISettingsService settings;

	public event EventHandler? AccountChanged;

	public AiAuthService (ISettingsService settings)
	{
		this.settings = settings;
	}

	public string ApiBaseUri {
		get => api_base_uri ??= ReadApiBaseUri ();
		set {
			api_base_uri = AiApiClient.NormalizeBaseUri (value);
			try {
				WriteApiBaseUri (api_base_uri);
			} catch (Exception ex) {
				Console.Error.WriteLine (ex);
			}
		}
	}

	public string Username {
		get => settings.GetSetting (USERNAME, string.Empty);
		private set => settings.PutSetting (USERNAME, value);
	}

	public string Token {
		get => settings.GetSetting (TOKEN, string.Empty);
		private set => settings.PutSetting (TOKEN, value);
	}

	public string AccountSummary {
		get => settings.GetSetting (ACCOUNT_SUMMARY, Translations.GetString ("AI: Not logged in"));
		private set => settings.PutSetting (ACCOUNT_SUMMARY, value);
	}

	public bool IsLoggedIn => !string.IsNullOrWhiteSpace (Token);

	public void ClearLoginState ()
	{
		Token = string.Empty;
		AccountSummary = Translations.GetString ("AI: Not logged in");
		AccountChanged?.Invoke (this, EventArgs.Empty);
	}

	public Task LoginAsync (
		string baseUri,
		string username,
		string password,
		CancellationToken cancellationToken = default)
		=> LoginCoreAsync (baseUri, username, password, cancellationToken);

	public async Task RegisterAsync (
		string baseUri,
		string username,
		string password,
		CancellationToken cancellationToken = default)
	{
		string normalizedBaseUri = AiApiClient.NormalizeBaseUri (baseUri);
		AiApiClient api = new (normalizedBaseUri);
		var payload = new Dictionary<string, string> {
			["email"] = username,
			["password"] = password,
			["full_name"] = username,
		};

		_ = await api.PostJsonAsync ("api/auth/register", payload, cancellationToken);

		await LoginCoreAsync (normalizedBaseUri, username, password, cancellationToken);
	}

	public async Task RefreshAccountSummaryAsync (CancellationToken cancellationToken = default)
	{
		if (!IsLoggedIn)
			return;

		try {
			AccountSummary = await ReadAccountSummaryAsync (cancellationToken);
			AccountChanged?.Invoke (this, EventArgs.Empty);
		} catch (AiAuthenticationException) {
			ClearLoginState ();
			throw;
		}
	}

	private string ReadApiBaseUri ()
	{
		string requestAddressPath = GetRequestAddressPath ();

		try {
			if (File.Exists (requestAddressPath)) {
				string value = File.ReadAllText (requestAddressPath).Trim ();
				if (!string.IsNullOrWhiteSpace (value))
					return AiApiClient.NormalizeBaseUri (value);
			}
		} catch (Exception ex) {
			Console.Error.WriteLine (ex);
		}

		string legacyValue = settings.GetSetting (API_BASE_URI, DefaultBaseUri);
		try {
			WriteApiBaseUri (legacyValue);
		} catch (Exception ex) {
			Console.Error.WriteLine (ex);
		}

		return AiApiClient.NormalizeBaseUri (legacyValue);
	}

	private void WriteApiBaseUri (string value)
	{
		string normalized = AiApiClient.NormalizeBaseUri (value);
		string requestAddressPath = GetRequestAddressPath ();
		Directory.CreateDirectory (Path.GetDirectoryName (requestAddressPath)!);
		File.WriteAllText (requestAddressPath, normalized);
	}

	private static string GetRequestAddressPath ()
		=> Path.Combine (AppContext.BaseDirectory, "config", REQUEST_ADDRESS_FILE);

	private async Task LoginCoreAsync (
		string baseUri,
		string username,
		string password,
		CancellationToken cancellationToken)
	{
		string normalizedBaseUri = AiApiClient.NormalizeBaseUri (baseUri);
		AiApiClient api = new (normalizedBaseUri);
		var payload = new KeyValuePair<string, string>[] {
			new ("grant_type", "password"),
			new ("scope", string.Empty),
			new ("username", username),
			new ("password", password),
		};

		using JsonDocument json = JsonDocument.Parse (await api.PostFormAsync ("api/auth/login", payload, cancellationToken));

		string token = ReadToken (json.RootElement);
		ApiBaseUri = normalizedBaseUri;
		Username = username;
		Token = token;
		try {
			AccountSummary = await ReadAccountSummaryAsync (cancellationToken);
			AccountChanged?.Invoke (this, EventArgs.Empty);
		} catch (AiAuthenticationException) {
			ClearLoginState ();
			throw;
		}
	}

	private async Task<string> ReadAccountSummaryAsync (CancellationToken cancellationToken)
	{
		AiApiClient api = new (this);

		using JsonDocument userJson = JsonDocument.Parse (await api.GetStringAsync ("api/me", cancellationToken));
		JsonElement user = userJson.RootElement;
		string email = user.GetProperty ("email").GetString () ?? Username;
		int balance = user.TryGetProperty ("balance", out JsonElement balanceElement) ? balanceElement.GetInt32 () : 0;

		return $"{email}  Balance: {balance}";
	}

	private static string ReadToken (JsonElement root)
	{
		foreach (string name in new[] { "token", "access_token", "accessToken", "jwt" }) {
			if (root.TryGetProperty (name, out JsonElement token) && token.GetString () is string value && !string.IsNullOrWhiteSpace (value))
				return value;
		}

		if (root.TryGetProperty ("data", out JsonElement data))
			return ReadToken (data);

		throw new InvalidOperationException ("The authentication response did not include a token.");
	}
}
