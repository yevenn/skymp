using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Numidium.Launcher.Core;

/// <summary>
/// Signing in to a Numidium server with Discord. The launcher opens Discord's
/// authorization page in the browser; Discord sends the browser back to the
/// launcher on <see cref="ServerManifest.ServerAuth.RedirectUri"/> (127.0.0.1)
/// with a one-time code; the launcher hands the code to the server, which trades
/// it with Discord (it holds the Discord application's secret) and answers with a
/// session for its game. The launcher never sees a Discord token, and the session
/// works only on that server.
/// </summary>
public static class DiscordSignIn
{
    public sealed record SignedIn(
        [property: JsonPropertyName("session")] string? Session,
        [property: JsonPropertyName("discordId")] string DiscordId,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("profileId")] int ProfileId,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt);

    private sealed record Error([property: JsonPropertyName("error")] string? Message);

    public static string NewState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string AuthorizeUrl(ServerManifest.ServerAuth auth, string state) =>
        "https://discord.com/oauth2/authorize" +
        $"?client_id={Uri.EscapeDataString(auth.DiscordClientId)}&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(auth.RedirectUri)}&scope=identify&state={state}&prompt=none";

    /// <summary>
    /// Waits for the browser to come back from Discord to <paramref name="redirectUri"/>
    /// and returns the code it brings. Requests without this sign-in's
    /// <paramref name="state"/> are turned away, so another page can't slip a code in.
    /// </summary>
    public static async Task<string> WaitForCodeAsync(string redirectUri, string state, CancellationToken cancel)
    {
        var expected = new Uri(redirectUri);
        var listener = new TcpListener(IPAddress.Loopback, expected.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Port {expected.Port} is in use, so the launcher can't receive Discord's answer ({ex.Message}).");
        }
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancel);
                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, cancel);
                if (target == null || !Uri.TryCreate(expected, target, out var uri) || uri.AbsolutePath != expected.AbsolutePath)
                {
                    await RespondAsync(stream, 404, "Not found", "", cancel);
                    continue;
                }
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (query["state"] != state)
                {
                    await RespondAsync(stream, 400, "Not this sign-in",
                        "This page isn't from the sign-in the launcher started. Click Sign in with Discord in the launcher again.", cancel);
                    continue;
                }
                if (query["error"] is string error)
                {
                    await RespondAsync(stream, 200, "Not signed in", "Discord sign-in was cancelled. You can close this tab.", cancel);
                    throw new OperationCanceledException($"Discord sign-in was cancelled ({error}).");
                }
                if (query["code"] is string code && code.Length > 0)
                {
                    await RespondAsync(stream, 200, "Signed in", "Signed in with Discord. You can close this tab and go back to the Numidium launcher.", cancel);
                    return code;
                }
                await RespondAsync(stream, 400, "No code", "Discord sent no sign-in code. Try again from the launcher.", cancel);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Hands Discord's code to the server, which answers with a session.</summary>
    public static async Task<SignedIn> ExchangeAsync(HttpClient http, string serverBaseUrl, string code, string redirectUri,
        CancellationToken cancel = default)
    {
        using var response = await http.PostAsJsonAsync($"{serverBaseUrl}/auth/discord", new { code, redirectUri }, cancel);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancel);
            throw new InvalidOperationException($"The server didn't accept the sign-in: {error}");
        }
        var signedIn = await response.Content.ReadFromJsonAsync<SignedIn>(cancel) ?? throw new InvalidDataException("The server's answer was empty.");
        if (string.IsNullOrEmpty(signedIn.Session)) throw new InvalidDataException("The server sent no session.");
        return signedIn;
    }

    /// <summary>Who <paramref name="session"/> signs in as on the server, or null when
    /// the server doesn't know it (expired, signed out, or the server was reset).</summary>
    public static async Task<SignedIn?> CheckAsync(HttpClient http, string serverBaseUrl, string session, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{serverBaseUrl}/auth/session");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The server couldn't check the sign-in: {await ReadErrorAsync(response, cancel)}");
        return await response.Content.ReadFromJsonAsync<SignedIn>(cancel);
    }

    public static async Task SignOutAsync(HttpClient http, string serverBaseUrl, string session, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverBaseUrl}/auth/signout");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
        using var response = await http.SendAsync(request, cancel);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<Error>(cancel);
            if (!string.IsNullOrEmpty(error?.Message)) return error.Message;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not our server's JSON; fall back to the status
        }
        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
    }

    // The path and query of an HTTP request: "GET /callback?code=... HTTP/1.1"
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (n == 0) break;
            length += n;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n")) break;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
        return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string text, CancellationToken cancel)
    {
        var body = $"<!doctype html><meta charset=\"utf-8\"><title>Numidium · {WebUtility.HtmlEncode(title)}</title>" +
                   "<body style=\"background:#16161a;color:#e8e4da;font:16px system-ui;display:grid;place-items:center;height:90vh\">" +
                   $"<p>{WebUtility.HtmlEncode(text)}</p>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {title}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancel);
        await stream.WriteAsync(bytes, cancel);
    }
}
