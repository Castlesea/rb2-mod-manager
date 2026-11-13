using System.Text.Json;
using SharpCompress.Archives;

namespace RB2ModCore;

/// <summary>Where an installed .rb2mod came from on GameBanana (for "Installed" marks now and update checks later).</summary>
/// <remarks>Kind "rb2mod": File is a .rb2mod name in Mods\ (or Mods\Disabled\). Kind "replace": File is a replacement folder name (see <see cref="ReplaceMods"/>).</remarks>
public sealed record GbInstall(string File, string Name, int ModId, int FileId, string Md5, string Version, DateTimeOffset FileDate, DateTimeOffset Installed, string Kind = "rb2mod");

/// <summary>
/// Downloads and their records: Modloader\ModManager\Downloads\ (archives as they came; the game never reads there) and
/// Modloader\ModManager\gamebanana.json (which installed file is which GameBanana mod and file).
/// </summary>
public static class ModDownloads
{
    public static string Folder(GameInstall game) => Path.Combine(game.Modloader, "ModManager", "Downloads");
    private static string SourcesFile(GameInstall game) => Path.Combine(game.Modloader, "ModManager", "gamebanana.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<GbInstall> Sources(GameInstall game)
    {
        try
        {
            var file = SourcesFile(game);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<GbInstall>>(File.ReadAllText(file), Options) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Notes that <paramref name="installedFile"/> (a file name in Mods\) is <paramref name="file"/> of <paramref name="mod"/>.</summary>
    public static void Record(GameInstall game, string installedFile, string name, GbMod mod, GbFile file, string kind = "rb2mod")
    {
        var list = Sources(game).Where(s => !(s.File.Equals(installedFile, StringComparison.OrdinalIgnoreCase) && s.Kind == kind)).ToList();
        list.Add(new GbInstall(installedFile, name, mod.Id, file.Id, file.Md5, mod.Version, file.Added, DateTimeOffset.Now, kind));
        Directory.CreateDirectory(Path.GetDirectoryName(SourcesFile(game))!);
        var temp = SourcesFile(game) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
        File.Move(temp, SourcesFile(game), true);
    }

    /// <summary>
    /// The .rb2mod files of a download: the file itself, or every .rb2mod inside an archive (.zip / .7z / .rar), taken
    /// out by file name only (no folders from the archive, nothing else unpacked) into <paramref name="folder"/>.
    /// </summary>
    public static List<string> ExtractRb2Mods(string download, string folder)
    {
        if (download.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase)) return [download];
        Directory.CreateDirectory(folder);
        var found = new List<string>();
        long total = 0;
        using var archive = ArchiveFactory.OpenArchive(download);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key is not null))
        {
            var name = entry.Key!.Replace('\\', '/').Split('/')[^1];
            if (!name.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            total += entry.Size;
            if (total > Rb2ModFile.MaxTotalBytes) throw new InvalidDataException("The download is larger than 8 GB.");
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var target = Path.Combine(folder, name);
            for (var n = 2; found.Contains(target, StringComparer.OrdinalIgnoreCase); n++)
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}_{n}{Rb2ModFile.Extension}");
            using (var input = entry.OpenEntryStream())
            using (var output = File.Create(target))
                input.CopyTo(output);
            found.Add(target);
        }
        if (found.Count == 0) throw new InvalidDataException("The download has no .rb2mod file: it isn't made for the Mod Manager.");
        return found;
    }
}
