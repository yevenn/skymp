using System.Text;

namespace Numidium.Launcher.Core;

/// <summary>
/// Just enough of the NIF format (Skyrim SE: 20.2.0.7) to read a file's header
/// string table: the names of its nodes, shapes and extra data.
/// </summary>
public static class NifFile
{
    /// <summary>Reads only as much of the file as the header needs: its string
    /// table (node and bone names), without loading the blocks.</summary>
    public static List<string> ReadStrings(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(stream.Length, 1 << 16)];
        while (true)
        {
            stream.Position = 0;
            var read = stream.Read(buffer, 0, buffer.Length);
            try
            {
                return ParseStrings(buffer.AsSpan(0, read).ToArray());
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
            {
                if (buffer.Length >= stream.Length) throw new InvalidDataException("Truncated NIF header.");
                buffer = new byte[Math.Min(stream.Length, (long)buffer.Length * 4)];
            }
        }
    }

    private static List<string> ParseStrings(byte[] data)
    {
        var pos = Array.IndexOf(data, (byte)'\n') + 1;
        if (pos == 0) throw new InvalidDataException("Not a NIF file.");
        uint UInt() { var v = BitConverter.ToUInt32(data, pos); pos += 4; return v; }
        string SizedString() { var n = (int)UInt(); var s = Encoding.Latin1.GetString(data, pos, n); pos += n; return s; }

        if (UInt() != 0x14020007) return new();
        pos += 1 + 4; // endian, user version
        var numBlocks = (int)UInt();
        var bsVersion = UInt();
        for (var i = 0; i < (bsVersion >= 130 ? 4 : 3); i++) pos += 1 + data[pos]; // export info
        var numTypes = BitConverter.ToUInt16(data, pos); pos += 2;
        for (var i = 0; i < numTypes; i++) SizedString();
        pos += 6 * numBlocks; // type indices, block sizes
        var numStrings = UInt();
        pos += 4; // max string length
        var strings = new List<string>();
        for (var i = 0; i < numStrings; i++) strings.Add(SizedString());
        return strings;
    }
}
