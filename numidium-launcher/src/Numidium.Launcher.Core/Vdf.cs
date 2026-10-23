namespace Numidium.Launcher.Core;

/// <summary>
/// Reads Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf):
/// quoted keys followed by a quoted value or a braced block.
/// </summary>
public static class Vdf
{
    public sealed class Node
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static Node Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new Node();
        ParseBlock(tokens, root, isRoot: true);
        return root;
    }

    private static void ParseBlock(IEnumerator<string?> tokens, Node node, bool isRoot)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key is null) // a closing brace
            {
                if (isRoot) throw new FormatException("Unexpected '}'");
                return;
            }
            if (!tokens.MoveNext()) throw new FormatException($"Missing value for \"{key}\"");
            if (tokens.Current == "{")
            {
                var child = new Node();
                ParseBlock(tokens, child, isRoot: false);
                node.Children[key] = child;
            }
            else if (tokens.Current is null)
            {
                throw new FormatException($"Missing value for \"{key}\"");
            }
            else
            {
                node.Values[key] = tokens.Current;
            }
        }
        if (!isRoot) throw new FormatException("Missing '}'");
    }

    // Yields quoted strings (unescaped), "{" for an opening brace and null for a closing one
    private static IEnumerable<string?> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{') { i++; yield return "{"; continue; }
            if (c == '}') { i++; yield return null; continue; }
            if (c != '"') throw new FormatException($"Unexpected '{c}' at {i}");
            var sb = new System.Text.StringBuilder();
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                }
                else
                {
                    sb.Append(text[i]);
                }
                i++;
            }
            if (i >= text.Length) throw new FormatException("Unterminated string");
            i++;
            yield return sb.ToString();
        }
    }
}
