using System.Buffers.Binary;

namespace Numidium.Launcher.Core;

/// <summary>
/// Limits RaceMenu's Height slider (Body Scales) to what the Numidium server
/// allows, as numidium-dev/patch-racemenu-height.py does: reads the slider
/// script from the installed RaceMenu.bsa, changes the Height range from
/// 0.25-2.00 to <see cref="Min"/>-<see cref="Max"/>, and writes it as a loose
/// Data\Scripts\racemenuplugin.pex, which the game prefers over the archive.
/// Patches the player's own RaceMenu, so no modified file is shipped. Keep the
/// limits in sync with BODY_SCALE_LIMITS in numidium-gamemode.
/// </summary>
public static class HeightPatch
{
    public const float Min = 0.95f;
    public const float Max = 1.05f;
    public const string ScriptPath = @"scripts\racemenuplugin.pex";

    public static string LoosePath(string gameDir) => Path.Combine(gameDir, "Data", "Scripts", "racemenuplugin.pex");

    /// <summary>Patches RaceMenu in the game copy. Returns false when RaceMenu isn't
    /// installed; throws when its slider script isn't the one this patch knows.</summary>
    public static bool Apply(string gameDir)
    {
        var bsa = Path.Combine(gameDir, "Data", "RaceMenu.bsa");
        if (!File.Exists(bsa)) return false;
        var patched = Patch(Bsa.ReadFile(bsa, ScriptPath));
        var target = LoosePath(gameDir);
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(patched)) return true;
        FileInstall.Replace(target, s => s.Write(patched));
        return true;
    }

    /// <summary>Swaps the Height slider's min and max in the compiled script.</summary>
    public static byte[] Patch(byte[] pex)
    {
        // AddSliderEx("$Height", ..., 0.25, 2.00, 0.01, ...): min, max, step as float arguments
        var original = FloatArgs(0.25f, 2.00f, 0.01f);
        var at = IndexOf(pex, original);
        if (at < 0 || IndexOf(pex, original, at + 1) >= 0)
            throw new InvalidDataException("RaceMenu's Height slider range wasn't found exactly once; a different RaceMenu version?");
        var result = (byte[])pex.Clone();
        FloatArgs(Min, Max, 0.01f).CopyTo(result, at);
        return result;
    }

    // A float argument in Papyrus bytecode: type 4, then the value big-endian
    private static byte[] FloatArgs(params float[] values)
    {
        var bytes = new byte[values.Length * 5];
        for (var i = 0; i < values.Length; i++)
        {
            bytes[i * 5] = 4;
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(i * 5 + 1), values[i]);
        }
        return bytes;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0) =>
        haystack.AsSpan(from).IndexOf(needle) is var i and >= 0 ? i + from : -1;
}

/// <summary>Reads a file from a Skyrim Special Edition (version 105) BSA archive.</summary>
public static class Bsa
{
    public static byte[] ReadFile(string archivePath, string wanted)
    {
        var data = File.ReadAllBytes(archivePath);
        if (data.Length < 36 || data[0] != 'B' || data[1] != 'S' || data[2] != 'A' || data[3] != 0)
            throw new InvalidDataException($"{archivePath} isn't a BSA archive");
        int U32(int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
        var version = U32(4);
        if (version != 105) throw new InvalidDataException($"{archivePath} is BSA version {version}, not Skyrim Special Edition's 105");
        var offset = U32(8);
        var flags = U32(12);
        var folders = U32(16);
        var files = U32(20);
        var folderNamesLength = U32(24);
        var fileNamesLength = U32(28);

        var entries = new List<(string Folder, uint Size, int Offset)>();
        for (var f = 0; f < folders; f++)
        {
            var record = offset + f * 24;
            var count = U32(record + 8);
            var at = (int)(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(record + 16)) - fileNamesLength);
            var nameLength = data[at];
            var folder = System.Text.Encoding.Latin1.GetString(data, at + 1, nameLength).TrimEnd('\0');
            at += 1 + nameLength;
            for (var k = 0; k < count; k++)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + k * 16 + 8));
                entries.Add((folder, size, U32(at + k * 16 + 12)));
            }
        }
        var namesAt = offset + folders * 24 + folderNamesLength + folders + files * 16;
        var names = System.Text.Encoding.Latin1.GetString(data, namesAt, fileNamesLength).Split('\0');
        for (var i = 0; i < entries.Count; i++)
        {
            var (folder, size, at) = entries[i];
            if (!(folder + "\\" + names[i]).Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var compressed = ((flags & 0x4) != 0) != ((size & 0x40000000) != 0);
            var blob = data.AsSpan(at, (int)(size & 0x3FFFFFFF));
            return compressed ? Lz4.DecodeFrame(blob[4..]) : blob.ToArray(); // compressed blobs start with the original size
        }
        throw new FileNotFoundException($"{wanted} isn't in {Path.GetFileName(archivePath)}");
    }
}

/// <summary>LZ4 frame decoding, as SSE BSAs compress their files.</summary>
public static class Lz4
{
    public static byte[] DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame) != 0x184D2204) throw new InvalidDataException("Not an LZ4 frame");
        var flg = frame[4];
        var i = 6 + ((flg & 0x08) != 0 ? 8 : 0) + ((flg & 0x01) != 0 ? 4 : 0) + 1;
        var output = new List<byte>();
        while (true)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(frame[i..]);
            i += 4;
            if (header == 0) break;
            var size = (int)(header & 0x7FFFFFFF);
            var block = frame.Slice(i, size);
            i += size;
            if ((header & 0x80000000) != 0) output.AddRange(block.ToArray());
            else DecodeBlock(block, output);
            if ((flg & 0x10) != 0) i += 4; // block checksum
        }
        return output.ToArray();
    }

    private static void DecodeBlock(ReadOnlySpan<byte> src, List<byte> output)
    {
        var i = 0;
        while (i < src.Length)
        {
            var token = src[i++];
            var literals = token >> 4;
            if (literals == 15) { byte b; do { b = src[i++]; literals += b; } while (b == 255); }
            for (var k = 0; k < literals; k++) output.Add(src[i++]);
            if (i >= src.Length) break;
            var offset = src[i] | (src[i + 1] << 8);
            i += 2;
            var length = token & 15;
            if (length == 15) { byte b; do { b = src[i++]; length += b; } while (b == 255); }
            length += 4;
            var start = output.Count - offset;
            for (var k = 0; k < length; k++) output.Add(output[start + k]);
        }
    }
}
