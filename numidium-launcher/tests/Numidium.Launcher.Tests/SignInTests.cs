using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Numidium.Launcher.Core;

namespace Numidium.Launcher.Tests;

public class SignInTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // What the browser does when Discord sends it back
    private static async Task<(int Status, string Body)> BrowserGet(string url)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(url);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TakesTheCodeOnlyFromThisSignInsCallback()
    {
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var waiting = DiscordSignIn.WaitForCodeAsync(redirect, "s1", cancel.Token);

        Assert.Equal(404, (await BrowserGet($"http://127.0.0.1:{port}/favicon.ico")).Status);
        Assert.Equal(400, (await BrowserGet($"{redirect}?code=stolen&state=other")).Status);
        Assert.False(waiting.IsCompleted);
        var (status, body) = await BrowserGet($"{redirect}?code=abc123&state=s1");
        Assert.Equal(200, status);
        Assert.Contains("Signed in", body);
        Assert.Equal("abc123", await waiting);
    }

    [Fact]
    public async Task ACancelledSignInEndsTheWait()
    {
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var waiting = DiscordSignIn.WaitForCodeAsync(redirect, "s2", CancellationToken.None);
        await BrowserGet($"{redirect}?error=access_denied&state=s2");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void AuthorizeUrlAsksDiscordForTheIdentityOnly()
    {
        var url = DiscordSignIn.AuthorizeUrl(new ServerManifest.ServerAuth("discord", "123", "http://127.0.0.1:53134/callback"), "st");
        Assert.StartsWith("https://discord.com/oauth2/authorize?client_id=123&response_type=code", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A53134%2Fcallback", url);
        Assert.Contains("scope=identify&state=st", url);
    }

    /// <summary>A Numidium server's /auth endpoints, answering from fixed responses.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Auth, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
            Seen.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            const string user = "\"discordId\":\"42\",\"username\":\"Yrsa\",\"profileId\":100,\"expiresAt\":1";
            return request.RequestUri.AbsolutePath switch
            {
                "/auth/discord" when body.Contains("\"good\"") => Json(HttpStatusCode.OK, "{\"session\":\"sess\"," + user + "}"),
                "/auth/discord" => Json(HttpStatusCode.Unauthorized, "{\"error\":\"Discord refused the sign-in code (400)\"}"),
                "/auth/session" when request.Headers.Authorization?.Parameter == "sess" => Json(HttpStatusCode.OK, "{" + user + "}"),
                "/auth/session" => Json(HttpStatusCode.Unauthorized, "{\"error\":\"not signed in\"}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task ExchangesTheCodeAndChecksTheSession()
    {
        var server = new FakeServer();
        var http = new HttpClient(server);
        var signedIn = await DiscordSignIn.ExchangeAsync(http, "http://host:3000", "good", "http://127.0.0.1:53134/callback");
        Assert.Equal(("sess", "Yrsa", 100), (signedIn.Session, signedIn.Username, signedIn.ProfileId));
        Assert.Contains("\"redirectUri\":\"http://127.0.0.1:53134/callback\"", server.Seen[0].Body);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DiscordSignIn.ExchangeAsync(http, "http://host:3000", "bad", "http://127.0.0.1:53134/callback"));
        Assert.Contains("Discord refused the sign-in code", error.Message);

        Assert.Equal("Yrsa", (await DiscordSignIn.CheckAsync(http, "http://host:3000", "sess"))?.Username);
        Assert.Equal("Bearer sess", server.Seen[^1].Auth);
        Assert.Null(await DiscordSignIn.CheckAsync(http, "http://host:3000", "expired"));
    }

    [Fact]
    public void ReadsTheServersSignInFromItsManifest()
    {
        var online = ServerManifest.Parse("""
            {"versionMajor":1,"mods":[],"loadOrder":[],
             "auth":{"type":"discord","discordClientId":"123","redirectUri":"http://127.0.0.1:53134/callback"}}
            """);
        Assert.True(online.UsesDiscordSignIn);
        Assert.Equal("123", online.Auth!.DiscordClientId);
        Assert.False(ServerManifest.Parse("{\"mods\":[]}").UsesDiscordSignIn);
    }

    [Fact]
    public void TheSessionReplacesTheDevProfileInTheClientSettings()
    {
        using var game = new TempDir();
        ClientSettings.Write(game.Path, "host", 7777, profileId: 3);
        ClientSettings.Write(game.Path, "host", 7777, session: "sess");
        var gameData = JsonNode.Parse(File.ReadAllText(ClientSettings.PathIn(game.Path)))!["gameData"]!.AsObject();
        Assert.Equal("sess", (string?)gameData["session"]);
        Assert.False(gameData.ContainsKey("profileId"));

        ClientSettings.Write(game.Path, "localhost", 7777, profileId: 2);
        gameData = JsonNode.Parse(File.ReadAllText(ClientSettings.PathIn(game.Path)))!["gameData"]!.AsObject();
        Assert.Equal(2, (int?)gameData["profileId"]);
        Assert.False(gameData.ContainsKey("session"));
    }

    [Fact]
    public void KeepsSessionsEncryptedForThisWindowsUser()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "launcher.json");
        var settings = new LauncherSettings();
        settings.SetSession("192.168.1.39", "secret-session");
        settings.Save(path);
        Assert.DoesNotContain("secret-session", File.ReadAllText(path));

        var loaded = LauncherSettings.Load(path);
        Assert.Equal("secret-session", loaded.GetSession("192.168.1.39"));
        Assert.Null(loaded.GetSession("other-host"));
        loaded.SetSession("192.168.1.39", null);
        Assert.Null(loaded.GetSession("192.168.1.39"));
    }
}
