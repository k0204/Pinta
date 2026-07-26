using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pinta.Core.AI;

public sealed class AiAuthenticationException : InvalidOperationException
{
	public AiAuthenticationException (string message)
		: base (message)
	{
	}
}

public sealed class AiApiClient
{
	private static readonly HttpClient client = new () { Timeout = TimeSpan.FromMinutes (6) };
	private static readonly TimeSpan[] retry_delays = [TimeSpan.FromSeconds (2), TimeSpan.FromSeconds (5)];

	private readonly AiAuthService? auth;
	private readonly string? baseUri;

	public AiApiClient (AiAuthService auth)
	{
		this.auth = auth;
	}

	public AiApiClient (string baseUri)
	{
		this.baseUri = NormalizeBaseUri (baseUri);
	}

	public async Task<string> PostJsonAsync (
		string path,
		object payload,
		CancellationToken cancellationToken = default)
	{
		string json = JsonSerializer.Serialize (payload);
		return await SendForStringAsync (
			HttpMethod.Post,
			path,
			() => new StringContent (json, Encoding.UTF8, "application/json"),
			cancellationToken);
	}

	public async Task<string> PostFormAsync (
		string path,
		IEnumerable<KeyValuePair<string, string>> fields,
		CancellationToken cancellationToken = default)
	{
		KeyValuePair<string, string>[] savedFields = fields.ToArray ();
		return await SendForStringAsync (
			HttpMethod.Post,
			path,
			() => new FormUrlEncodedContent (savedFields),
			cancellationToken);
	}

	public async Task<string> PostPngAsync (
		string path,
		byte[] png,
		string formName,
		string fileName,
		CancellationToken cancellationToken = default,
		IEnumerable<KeyValuePair<string, string>>? fields = null)
	{
		KeyValuePair<string, string>[] savedFields = fields?.ToArray () ?? [];
		return await SendForStringAsync (
			HttpMethod.Post,
			path,
			() => CreatePngContent (png, formName, fileName, savedFields),
			cancellationToken);
	}

	public async Task<byte[]> GetBytesAsync (
		string path,
		CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await SendWithRetriesAsync (
			HttpMethod.Get,
			path,
			createContent: null,
			cancellationToken);

		if (response.IsSuccessStatusCode)
			return await response.Content.ReadAsByteArrayAsync (cancellationToken);

		_ = await ReadApiResponseAsync (response, cancellationToken);
		throw new InvalidOperationException ("Unexpected API response.");
	}

	public async Task<string> GetStringAsync (
		string path,
		CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await SendWithRetriesAsync (
			HttpMethod.Get,
			path,
			createContent: null,
			cancellationToken);
		return await ReadApiResponseAsync (response, cancellationToken);
	}

	private async Task<string> SendForStringAsync (
		HttpMethod method,
		string path,
		Func<HttpContent> createContent,
		CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendWithRetriesAsync (
			method,
			path,
			createContent,
			cancellationToken);
		return await ReadApiResponseAsync (response, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendWithRetriesAsync (
		HttpMethod method,
		string path,
		Func<HttpContent>? createContent,
		CancellationToken cancellationToken)
	{
		for (int attempt = 0; ; attempt++) {
			try {
				using HttpRequestMessage request = CreateRequest (method, path);
				request.Content = createContent?.Invoke ();
				Console.WriteLine ($"AI HTTP request: method={method.Method}, url={request.RequestUri?.GetLeftPart (UriPartial.Path)}, auth={(request.Headers.Authorization is null ? "none" : "bearer")}, attempt={attempt + 1}");
				HttpResponseMessage response = await client.SendAsync (request, cancellationToken);
				if (!IsTransientStatus (response) || attempt >= retry_delays.Length)
					return response;

				response.Dispose ();
			} catch (Exception ex) when (IsTransientException (ex) && attempt < retry_delays.Length) {
			}

			await Task.Delay (retry_delays[attempt], cancellationToken);
		}
	}

	private static MultipartFormDataContent CreatePngContent (
		byte[] png,
		string formName,
		string fileName,
		IEnumerable<KeyValuePair<string, string>> fields)
	{
		MultipartFormDataContent content = new ();
		foreach (KeyValuePair<string, string> field in fields)
			content.Add (new StringContent (field.Value), field.Key);

		ByteArrayContent image = new (png);
		image.Headers.ContentType = new ("image/png");
		content.Add (image, formName, fileName);
		return content;
	}

	private static bool IsTransientStatus (HttpResponseMessage response)
		=> (int) response.StatusCode is 408 or 429 or >= 500;

	private static bool IsTransientException (Exception ex)
		=> ex is HttpRequestException or TaskCanceledException;

	private HttpRequestMessage CreateRequest (HttpMethod method, string path)
	{
		Uri apiUri = new (auth?.ApiBaseUri ?? baseUri!);
		Uri requestUri = new (apiUri, path.TrimStart ('/'));
		HttpRequestMessage request = new (method, requestUri);

		if (auth?.IsLoggedIn == true && IsApiServerUri (apiUri, requestUri))
			request.Headers.Authorization = new AuthenticationHeaderValue ("Bearer", auth.Token);

		return request;
	}

	private static bool IsApiServerUri (Uri apiUri, Uri requestUri)
		=> requestUri.Scheme == apiUri.Scheme &&
			requestUri.Host == apiUri.Host &&
			requestUri.Port == apiUri.Port;

	public static string NormalizeBaseUri (string baseUri)
	{
		if (!Uri.TryCreate (baseUri, UriKind.Absolute, out Uri? uri) ||
			(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			throw new InvalidOperationException ("Enter a valid HTTP API server URL.");

		string result = uri.ToString ();
		return result.EndsWith ('/') ? result : result + "/";
	}

	public static async Task<string> ReadApiResponseAsync (
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		string body = await response.Content.ReadAsStringAsync (cancellationToken);
		if (response.IsSuccessStatusCode)
			return body;

		string detail = body;
		try {
			using JsonDocument json = JsonDocument.Parse (body);
			if (json.RootElement.TryGetProperty ("detail", out JsonElement detailElement))
				detail = detailElement.ToString ();
		} catch (JsonException) {
		}

		string message = $"{(int) response.StatusCode} {response.ReasonPhrase}: {detail}";
		if (response.RequestMessage?.Headers.Authorization is not null &&
			(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
			throw new AiAuthenticationException (message);

		throw new InvalidOperationException (message);
	}
}
