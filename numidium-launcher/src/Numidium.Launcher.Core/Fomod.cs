using System.Xml.Linq;

namespace Numidium.Launcher.Core;

/// <summary>
/// Runs a FOMOD installer (fomod\ModuleConfig.xml) without its dialog, from the
/// choices Vortex recorded in the collection, the way Vortex would: required
/// files, the chosen options' files and flags, then conditional files whose
/// conditions hold. Produces which archive paths go where in Data.
/// </summary>
public static class Fomod
{
    /// <summary>One archive file and where it goes in Data. Both use backslashes.</summary>
    public sealed record Placement(string Source, string Destination);

    /// <summary>The folder holding fomod\ModuleConfig.xml (empty for the archive root),
    /// or null when the archive has no installer.</summary>
    public static string? FindModuleRoot(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var normalized = Normalize(key);
            const string marker = "fomod\\moduleconfig.xml";
            if (!normalized.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var root = normalized[..^marker.Length].TrimEnd('\\');
            if (normalized.Length == marker.Length || normalized[^(marker.Length + 1)] == '\\') return root;
        }
        return null;
    }

    /// <summary>
    /// Where each archive file goes. <paramref name="keys"/> are all archive paths,
    /// <paramref name="moduleRoot"/> from FindModuleRoot, and <paramref name="installedFiles"/>
    /// answers fileDependency conditions (is that file in Data already).
    /// </summary>
    public static List<Placement> Plan(string moduleConfigXml, IReadOnlyCollection<string> keys, string moduleRoot,
        IReadOnlyList<Collection.FomodChoice> choices, Func<string, bool>? installedFiles = null)
    {
        var doc = XDocument.Parse(moduleConfigXml);
        var config = doc.Root ?? throw new InvalidDataException("ModuleConfig.xml is empty");
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var installs = new List<(XElement Element, int Order)>();
        var order = 0;

        foreach (var element in Child(config, "requiredInstallFiles")?.Elements() ?? Enumerable.Empty<XElement>())
            installs.Add((element, order++));

        foreach (var step in Children(Child(config, "installSteps"), "installStep"))
        {
            var visible = Child(step, "visible");
            if (visible != null && !Evaluate(visible, flags, installedFiles)) continue;
            var stepName = Attr(step, "name");
            foreach (var group in Children(Child(step, "optionalFileGroups"), "group"))
            {
                var groupName = Attr(group, "name");
                var groupType = Attr(group, "type");
                foreach (var plugin in Children(Child(group, "plugins"), "plugin"))
                {
                    var pluginName = Attr(plugin, "name");
                    var chosen = choices.Any(c => Same(c.Step, stepName) && Same(c.Group, groupName) && Same(c.Option, pluginName));
                    // Required options and SelectAll groups are taken whether or not they were recorded
                    var required = Same(groupType, "SelectAll") || Same(PluginType(plugin, flags, installedFiles), "Required");
                    if (!chosen && !required) continue;
                    foreach (var element in Child(plugin, "files")?.Elements() ?? Enumerable.Empty<XElement>())
                        installs.Add((element, order++));
                    foreach (var flag in Children(Child(plugin, "conditionFlags"), "flag"))
                        flags[Attr(flag, "name")] = flag.Value;
                }
            }
        }

        foreach (var pattern in Children(Child(Child(config, "conditionalFileInstalls"), "patterns"), "pattern"))
        {
            var dependencies = Child(pattern, "dependencies");
            if (dependencies != null && !Evaluate(dependencies, flags, installedFiles)) continue;
            foreach (var element in Child(pattern, "files")?.Elements() ?? Enumerable.Empty<XElement>())
                installs.Add((element, order++));
        }

        // Lower priority first, so higher priority overwrites; document order within a priority
        var placements = new List<Placement>();
        var rootPrefix = moduleRoot.Length == 0 ? "" : moduleRoot + "\\";
        var keyList = keys.Select(Normalize).ToList();
        foreach (var (element, _) in installs.OrderBy(i => int.TryParse(Attr(i.Element, "priority"), out var p) ? p : 0).ThenBy(i => i.Order))
        {
            var source = Normalize(Attr(element, "source"));
            var destination = element.Attribute("destination") is XAttribute d ? Normalize(d.Value) : source;
            if (element.Name.LocalName.Equals("folder", StringComparison.OrdinalIgnoreCase))
            {
                var prefix = rootPrefix + (source.Length == 0 ? "" : source + "\\");
                foreach (var key in keyList.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var rel = key[prefix.Length..];
                    placements.Add(new Placement(key, destination.Length == 0 ? rel : destination + "\\" + rel));
                }
            }
            else
            {
                var key = keyList.FirstOrDefault(k => k.Equals(rootPrefix + source, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"The installer names \"{source}\", which isn't in the archive");
                placements.Add(new Placement(key, destination.Length == 0 ? Path.GetFileName(source) : destination));
            }
        }
        return placements;
    }

    private static string PluginType(XElement plugin, Dictionary<string, string> flags, Func<string, bool>? installedFiles)
    {
        var descriptor = Child(plugin, "typeDescriptor");
        var dependencyType = Child(descriptor, "dependencyType");
        if (dependencyType != null)
        {
            foreach (var pattern in Children(Child(dependencyType, "patterns"), "pattern"))
            {
                var dependencies = Child(pattern, "dependencies");
                if (dependencies == null || Evaluate(dependencies, flags, installedFiles))
                    return Attr(Child(pattern, "type"), "name");
            }
            return Attr(Child(dependencyType, "defaultType"), "name");
        }
        return Attr(Child(descriptor, "type"), "name");
    }

    /// <summary>A dependencies element: And (default) or Or over flag, file, game
    /// and nested conditions. Game and mod manager version conditions count as met.</summary>
    private static bool Evaluate(XElement dependencies, Dictionary<string, string> flags, Func<string, bool>? installedFiles)
    {
        var results = new List<bool>();
        foreach (var condition in dependencies.Elements())
        {
            switch (condition.Name.LocalName)
            {
                case "flagDependency":
                    results.Add(flags.TryGetValue(Attr(condition, "flag"), out var value)
                        ? value == Attr(condition, "value")
                        : Attr(condition, "value").Length == 0);
                    break;
                case "fileDependency":
                    var present = installedFiles?.Invoke(Normalize(Attr(condition, "file"))) ?? false;
                    var state = Attr(condition, "state");
                    results.Add(Same(state, "Missing") ? !present : Same(state, "Inactive") ? !present : present);
                    break;
                case "dependencies":
                    results.Add(Evaluate(condition, flags, installedFiles));
                    break;
                default: // gameDependency, fommDependency, foseDependency...
                    results.Add(true);
                    break;
            }
        }
        return Same(Attr(dependencies, "operator"), "Or") ? results.Any(r => r) : results.All(r => r);
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<XElement>();

    private static string Attr(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path) => path.Replace('/', '\\').Trim().Trim('\\');
}
