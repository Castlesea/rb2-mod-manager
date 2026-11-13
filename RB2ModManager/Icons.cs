using Avalonia.Media;

namespace RB2ModManager;

/// <summary>Line icons drawn as 24×24 geometries (the Launcher's set): identical on Windows and under Wine/Proton.</summary>
public static class Icons
{
    public static Geometry Add { get; } = Parse("M12 4.5v15 M4.5 12h15");
    public static Geometry Folder { get; } = Parse(
        "M3 6.5a1.5 1.5 0 0 1 1.5-1.5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18Z");
    public static Geometry Sync { get; } = Parse(
        "M19.5 9a8 8 0 0 0-14.3-2.4 M4.5 3.5v3.6h3.6 M4.5 15a8 8 0 0 0 14.3 2.4 M19.5 20.5v-3.6h-3.6");
    public static Geometry Trash { get; } = Parse(
        "M4 6.5h16 M9.5 6.5V4h5v2.5 M6 6.5l1 14h10l1-14 M10 10.5v6.5 M14 10.5v6.5");
    public static Geometry Puzzle { get; } = Parse(
        "M4 8h4a2 2 0 1 1 4 0h4v4a2 2 0 1 1 0 4v4H4v-4a2 2 0 1 0 0-4Z");
    public static Geometry Hash { get; } = Parse("M9.5 3.5 7.5 20.5 M16.5 3.5l-2 17 M4 9h16.5 M3.5 15h16.5");
    public static Geometry Warning { get; } = Parse(
        "M12 3.5 21.5 20h-19Z M12 10v4.5 M12 17.2v.3");
    public static Geometry Check { get; } = Parse("M4.5 12.5 9.5 17.5 19.5 6.5");
    public static Geometry Close { get; } = Parse("M6 6l12 12 M18 6 6 18");
    public static Geometry Edit { get; } = Parse("M15 4.5l4.5 4.5L8.5 20H4v-4.5Z M12.5 7l4.5 4.5");
    public static Geometry Info { get; } = Parse(
        "M12 2.75a9.25 9.25 0 1 1 0 18.5a9.25 9.25 0 0 1 0-18.5Z M12 10.5V17 M12 7v.5");
    public static Geometry Download { get; } = Parse("M12 3.5v12 M7 11l5 5 5-5 M4 20.5h16");
    public static Geometry Play { get; } = Parse("M7 4.5v15l12-7.5Z");

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
