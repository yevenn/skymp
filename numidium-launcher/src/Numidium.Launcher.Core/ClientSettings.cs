using System.Text;
using System.Text.Json.Nodes;

namespace Numidium.Launcher.Core;

/// <summary>
/// The SkyMP client's settings file in the game copy
/// (Data\Platform\Plugins\skymp5-client-settings.txt). Written without a BOM:
/// SkyrimPlatform's JSON parser rejects one.
/// </summary>
public static class ClientSettings
{
    public static string PathIn(string gameDir) =>
        Path.Combine(gameDir, "Data", "Platform", "Plugins", "skymp5-client-settings.txt");

    /// <summary>Points the client at a server, keeping any other settings in the file.
    /// <paramref name="session"/> is the server's Discord sign-in session;
    /// <paramref name="profileId"/> is for offline (dev) servers, which trust it.
    /// Whichever is given replaces the other.</summary>
    public static void Write(string gameDir, string host, int port, int? profileId = null, string? session = null)
    {
        var path = PathIn(gameDir);
        var settings = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
            ? existing
            : new JsonObject();
        settings["server-ip"] = host;
        settings["server-port"] = port;
        if (session != null || profileId != null)
        {
            var gameData = settings["gameData"] as JsonObject ?? new JsonObject();
            gameData.Remove("profileId");
            gameData.Remove("session");
            if (session != null) gameData["session"] = session;
            else gameData["profileId"] = profileId;
            settings["gameData"] = gameData;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, settings.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
    }
}
