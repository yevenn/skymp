using System.Windows.Media;
using Numidium.Launcher.Core;

namespace Numidium.Launcher;

/// <summary>One collection file in the mod list.</summary>
public sealed class ModRow
{
    public enum State { Installed, Downloaded, Missing, ByLauncher }

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0x7A));
    private static readonly Brush Ready = new SolidColorBrush(Color.FromRgb(0xC9, 0xA4, 0x5C));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x6A));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x95, 0x8A));

    public Collection.Mod Mod { get; }
    public State Status_ { get; }
    public string? ArchivePath { get; }

    public ModRow(Collection.Mod mod, State state, string? archivePath) =>
        (Mod, Status_, ArchivePath) = (mod, state, archivePath);

    public string ModName => Mod.Optional ? Mod.Name + " (optional)" : Mod.Name;
    public string FileName => $"{Mod.LogicalFileName} {Mod.Version}".Trim();
    public string PageUrl => Mod.PageUrl;

    public string Status => Status_ switch
    {
        State.Installed => "Installed",
        State.Downloaded => "Downloaded",
        State.ByLauncher => "By launcher",
        _ => "Not downloaded",
    };

    public Brush StatusBrush => Status_ switch
    {
        State.Installed => Good,
        State.Downloaded => Ready,
        State.ByLauncher => Muted,
        _ => Mod.Optional ? Muted : Bad,
    };
}
