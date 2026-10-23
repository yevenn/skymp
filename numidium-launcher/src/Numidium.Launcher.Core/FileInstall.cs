namespace Numidium.Launcher.Core;

/// <summary>Writing files into the game copy safely.</summary>
public static class FileInstall
{
    /// <summary>
    /// The full path of <paramref name="relative"/> inside <paramref name="root"/>,
    /// refusing paths that would land outside it ("..\", absolute paths): archive
    /// entries are data from a download, not to be trusted with the disk.
    /// </summary>
    public static string SafePath(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"\"{relative}\" points outside {root}");
        return full;
    }

    /// <summary>
    /// Writes a file by replacing it, never by writing into the existing one: game
    /// copies hard-link Steam's files, and writing in place would change those too.
    /// </summary>
    public static void Replace(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".numidium-tmp";
        using (var stream = File.Create(temp)) write(stream);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }
}
