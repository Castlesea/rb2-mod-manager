using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace RB2ModCore;

/// <summary>One installable part of a replacement download: a folder of model files (a costume, a form, one variant).</summary>
public sealed record ReplaceUnit(string Name, List<(string Source, string Target)> Files, List<string> Keys)
{
    public string Summary => ReplaceMods.Describe(Keys);
}

/// <summary>What a replacement download holds: its parts, and what was left out (and why).</summary>
public sealed record ReplaceScan(List<ReplaceUnit> Units, List<string> Skipped);

/// <summary>An installed replacement folder (on: Modloader\Characters\name, off: Modloader\ModManager\Disabled\Characters\name).</summary>
public sealed record ReplaceMod(string Path, string Name, bool Enabled, List<string> Keys);

/// <summary>
/// Replacement mods: the older kind of mod (most of GameBanana today), loose model files that take the place of the
/// game's own while they are installed. The game's files are never touched: each one lives in its own folder under
/// Modloader\Characters\ (any name not starting with "NNN_", which would claim a character ID), where the modloader
/// (src/modloader/character_mod_catalog.cpp) matches every .zpak/.pak by its name, without a 6-digit prefix and
/// without _PS3_ / _X360_ / _PC_ (000_goku_0_2p_PS3_i.zpak = 000_goku_0_2p_i.zpak). PS3 model triplets are converted
/// by the game. Two turned-on folders with the same file both stop loading, so the Mod Manager keeps them apart. A
/// turned-off folder waits outside Modloader\Characters (the game reads every folder in there).
/// </summary>
public static class ReplaceMods
{
    public static string Root(GameInstall game) => game.WorkingFolders;
    public static string DisabledRoot(GameInstall game) => System.IO.Path.Combine(game.Modloader, "ModManager", "Disabled", "Characters");
    public static string RemovedRoot(GameInstall game) => System.IO.Path.Combine(game.Modloader, "ModManager", "Removed");
    public static string PreviewsRoot(GameInstall game) => System.IO.Path.Combine(game.Modloader, "ModManager", "Previews");

    public static bool Exists(GameInstall game, string name) =>
        Directory.Exists(System.IO.Path.Combine(Root(game), name)) || Directory.Exists(System.IO.Path.Combine(DisabledRoot(game), name));

    public static bool IsAsset(string name) =>
        name.EndsWith(".zpak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    /// <summary>Old PS3 dumps the PC port can't read (no STPZ container).</summary>
    public static bool IsRawPs3(string name) =>
        name.EndsWith(".spr", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ioram", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vram", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a list of archive paths has model files this kind of install can use.</summary>
    public static bool HasAssets(IEnumerable<string> paths) => paths.Any(p => IsAsset(p) && SkipReason(p.Replace('\\', '/')) is null);

    /// <summary>The name the game matches a file by (character_mod_catalog.cpp ArchiveName + PortableArchiveKey).</summary>
    public static string Key(string fileName)
    {
        var name = Regex.Replace(NormalName(fileName), @"^\d{6}__", "");
        name = name.ToLowerInvariant();
        foreach (var platform in new[] { "_ps3_", "_x360_", "_pc_" }) name = name.Replace(platform, "_");
        return name;
    }

    /// <summary>
    /// Fixes two slips seen in real mods: "…_PS3-i.zpak" (a dash where the game wants "_") and Windows' "… - Copia.zpak" /
    /// "… - Copy.zpak" copies; both would replace nothing.
    /// </summary>
    public static string NormalName(string fileName)
    {
        var name = Regex.Replace(fileName, @"_(PS3|X360|PC)-([isv])(\.z?pak)$", "_$1_$2$3", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+-\s+(Copia|Copy|Cópia)(\s*\(\d+\))?(\.z?pak)$", "$3", RegexOptions.IgnoreCase);
        return name;
    }

    /// <summary>"000_goku_0 costume 2, 017_vegeta_1 costume 2" from the keys (model triplets counted once).</summary>
    public static string Describe(IEnumerable<string> keys)
    {
        var parts = new List<string>();
        foreach (var key in keys.Distinct())
        {
            var match = Regex.Match(key, @"^(\d{3}_[a-z0-9]+(?:_\d+)?)_(\d)p(?:_d)?(?:_[isv])?\.z?pak$");
            var text = match.Success ? $"{match.Groups[1].Value} costume {match.Groups[2].Value}" : System.IO.Path.GetFileNameWithoutExtension(key);
            if (!parts.Contains(text)) parts.Add(text);
        }
        return string.Join(", ", parts);
    }

    /// <summary>Why a file of a download is left out (null: it's used, or just ignored like a readme).</summary>
    public static string? SkipReason(string relative)
    {
        var name = relative.Split('/')[^1];
        var lower = relative.ToLowerInvariant();
        if (name.Equals("DB_SAVE", StringComparison.OrdinalIgnoreCase) || lower.Contains("savedata/")) return $"{relative}: a save file (your save is never replaced)";
        if (lower.StartsWith("assets/") || lower.Contains("/assets/")) return $"{relative}: one of the game's own files (never replaced)";
        if (Rb2ModFile.Refused.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant())) return $"{relative}: a program";
        if (IsRawPs3(name)) return $"{relative}: an old PS3 file the PC port can't load";
        if (name.EndsWith(".afs", StringComparison.OrdinalIgnoreCase)) return $"{relative}: a sound pack the Mod Manager can't place";
        return null;
    }

    /// <summary>
    /// Reads an extracted download: every folder with model files is one part, named after its path in the download
    /// ("Vegito SDBH - SSJ Blue"; a "Modloader/Characters/" start is dropped). Files under CharacterSelect/Archives stay
    /// with the part above them.
    /// </summary>
    public static ReplaceScan Scan(string root, string fallbackName)
    {
        var skipped = new List<string>();
        var groups = new Dictionary<string, List<(string Source, string Target)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (SkipReason(relative) is { } reason) { skipped.Add(reason); continue; }
            if (!IsAsset(relative)) continue;
            var directory = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            var target = NormalName(relative.Split('/')[^1]);
            var archives = Regex.Match(directory, @"^(.*?)/?CharacterSelect/Archives$", RegexOptions.IgnoreCase);
            if (archives.Success) { directory = archives.Groups[1].Value; target = "CharacterSelect/Archives/" + target; }
            (groups.TryGetValue(directory, out var list) ? list : groups[directory] = []).Add((file, target));
        }
        var units = new List<ReplaceUnit>();
        foreach (var (directory, files) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var path = Regex.Replace(directory, @"^(Modloader/)?Characters/?", "", RegexOptions.IgnoreCase).Trim('/');
            var name = path.Length == 0 ? fallbackName : string.Join(" - ", path.Split('/'));
            units.Add(new ReplaceUnit(FolderName(name), files, files.Select(f => Key(f.Target.Split('/')[^1])).Distinct().ToList()));
        }
        return new ReplaceScan(units, skipped);
    }

    /// <summary>A safe folder name: no "NNN_" start (that would claim a character ID), no characters Windows refuses.</summary>
    public static string FolderName(string name)
    {
        name = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim().TrimEnd('.');
        name = Regex.Replace(name, @"\s+", " ");
        if (name.Length == 0) name = "Replacement";
        if (Regex.IsMatch(name, @"^\d{3}_")) name = "Mod " + name;
        return name.Length > 80 ? name[..80].Trim() : name;
    }

    /// <summary>Extracts a whole download (no path leaving the folder, no links) for <see cref="Scan"/>.</summary>
    public static void ExtractAll(string archivePath, string folder)
    {
        Directory.CreateDirectory(folder);
        var root = System.IO.Path.GetFullPath(folder) + System.IO.Path.DirectorySeparatorChar;
        long total = 0;
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key is not null))
        {
            var relative = entry.Key!.Replace('\\', '/').TrimStart('/');
            if (relative.Split('/').Any(p => p == "..") || relative.Contains(':')) continue;
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            total += entry.Size;
            if (total > Rb2ModFile.MaxTotalBytes) throw new InvalidDataException("The download is larger than 8 GB.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var input = entry.OpenEntryStream();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    // ---- installed ------------------------------------------------------------------------------------------------

    /// <summary>The replacement folders: on (Modloader\Characters, the ones without character.json / an add-on mod.toml) and off.</summary>
    public static List<ReplaceMod> Installed(GameInstall game)
    {
        var list = new List<ReplaceMod>();
        void Read(string root, bool enabled)
        {
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(System.IO.Path.Combine(folder, "character.json"))) continue;
                var toml = System.IO.Path.Combine(folder, "mod.toml");
                if (File.Exists(toml) && !Regex.IsMatch(File.ReadAllText(toml), @"(?m)^\s*kind\s*=\s*""override""")) continue;
                if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var keys = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => IsAsset(f)).Select(f => Key(System.IO.Path.GetFileName(f))).Distinct().ToList();
                list.Add(new ReplaceMod(folder, System.IO.Path.GetFileName(folder), enabled, keys));
            }
        }
        Read(Root(game), true);
        Read(DisabledRoot(game), false);
        return list;
    }

    /// <summary>Turned-on folders that share files: the game loads neither copy of a shared file.</summary>
    public static Dictionary<string, List<(ReplaceMod Other, List<string> Keys)>> Clashes(IEnumerable<ReplaceMod> mods)
    {
        var on = mods.Where(m => m.Enabled).ToList();
        var result = new Dictionary<string, List<(ReplaceMod, List<string>)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in on)
            foreach (var other in on.Where(o => o != mod))
                if (mod.Keys.Intersect(other.Keys).ToList() is { Count: > 0 } shared)
                    (result.TryGetValue(mod.Path, out var list) ? list : result[mod.Path] = []).Add((other, shared));
        return result;
    }

    /// <summary>Copies a part into Modloader\Characters\name (an older folder of that name goes to Removed). Returns the folder.</summary>
    public static string Install(GameInstall game, ReplaceUnit unit)
    {
        var folder = System.IO.Path.Combine(Root(game), unit.Name);
        foreach (var old in new[] { folder, System.IO.Path.Combine(DisabledRoot(game), unit.Name) })
            if (Directory.Exists(old)) MoveAside(game, old);
        var temp = folder + ".installing";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        foreach (var (source, target) in unit.Files)
        {
            var destination = System.IO.Path.Combine(temp, target.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
        WritePs3Manifest(temp, unit.Name);
        Directory.Move(temp, folder);
        return folder;
    }

    // ---- PS3 manifest ---------------------------------------------------------------------------------------------
    // The game decides a file's platform by looking for "_ps3_" / "_x360_" inside its first 64 KB
    // (asset_platform_probe.cpp). Compressed .zpak files (most GameBanana PS3 models) don't show it, so a folder of
    // them loads nothing ("container has no proven platform marker"). A mod.toml with platform = "ps3" sends every
    // file of the folder through the game's PS3 converter instead, which is what these files are.

    private const string ManifestHeader = "# Written by the Raging Blast 2 Mod Manager: these are PS3 model files, the game converts them.";

    private static bool IsPs3Name(string name) =>
        name.Contains("_ps3_", StringComparison.OrdinalIgnoreCase) || name.Contains("_ps3.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The platform marker the game finds in a file (its probe: first 64 KB, any case): 'P', 'X', or null.</summary>
    private static char? ContentMarker(string path)
    {
        var buffer = new byte[64 * 1024];
        int read;
        using (var stream = File.OpenRead(path)) read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = System.Text.Encoding.Latin1.GetString(buffer, 0, read).ToLowerInvariant();
        var x360 = text.Contains("_x360_") || text.Contains("_x360.");
        var ps3 = text.Contains("_ps3_") || text.Contains("_ps3.");
        return x360 && !ps3 ? 'X' : ps3 && !x360 ? 'P' : x360 ? '?' : null;
    }

    /// <summary>
    /// Writes mod.toml (kind override, platform ps3) when the game couldn't place some model file of the folder on its
    /// own (no marker inside) and every file is a PS3 one (by its marker or, failing that, its name). A folder already
    /// having a mod.toml / character.json, one that loads as it is, or one with X360 files is left alone.
    /// </summary>
    public static bool WritePs3Manifest(string folder, string name)
    {
        var manifest = System.IO.Path.Combine(folder, "mod.toml");
        // the first version wrote content_root = "." , which the game refuses (the whole mod.toml is dropped): redo it
        if (File.Exists(manifest) && File.ReadAllText(manifest) is var old && old.StartsWith(ManifestHeader) && old.Contains("content_root"))
            File.Delete(manifest);
        if (File.Exists(manifest) || File.Exists(System.IO.Path.Combine(folder, "character.json"))) return false;
        var assets = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsAsset).Select(f => (Name: System.IO.Path.GetFileName(f), Marker: ContentMarker(f))).ToList();
        if (assets.Count == 0 || assets.All(a => a.Marker is not null)) return false;
        if (!assets.All(a => a.Marker == 'P' || a.Marker is null && IsPs3Name(a.Name))) return false;
        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        // no content_root: the game refuses "." and, without a content folder, reads the files beside mod.toml
        var text = string.Join("\n",
            ManifestHeader,
            "schema_version = 1",
            $"id = \"replace.{(slug.Length == 0 ? "mod" : slug)}\"",
            $"name = \"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
            "kind = \"override\"",
            "platform = \"ps3\"",
            "");
        File.WriteAllText(System.IO.Path.Combine(folder, "mod.toml"), text, new System.Text.UTF8Encoding(false));
        return true;
    }

    /// <summary>Adds the PS3 mod.toml to replacement folders installed before it existed (on and off). Returns how many.</summary>
    public static int RepairPs3Manifests(GameInstall game)
    {
        var count = 0;
        foreach (var root in new[] { Root(game), DisabledRoot(game) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var name = System.IO.Path.GetFileName(folder);
                // an "NNN_" folder is a character slot (the Engine's), not a replacement
                if (Regex.IsMatch(name, @"^\d{3}_") || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try { if (WritePs3Manifest(folder, name)) count++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return count;
    }

    public static string SetEnabled(GameInstall game, ReplaceMod mod, bool enabled)
    {
        var target = System.IO.Path.Combine(enabled ? Root(game) : DisabledRoot(game), mod.Name);
        if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(mod.Path), StringComparison.OrdinalIgnoreCase)) return target;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (Directory.Exists(target)) MoveAside(game, target);
        Directory.Move(mod.Path, target);
        return target;
    }

    public static string Remove(GameInstall game, ReplaceMod mod) => MoveAside(game, mod.Path);

    private static string MoveAside(GameInstall game, string folder)
    {
        Directory.CreateDirectory(RemovedRoot(game));
        var target = System.IO.Path.Combine(RemovedRoot(game), $"{System.IO.Path.GetFileName(folder)}_{DateTime.Now:yyyyMMdd_HHmmss}");
        for (var n = 2; Directory.Exists(target); n++) target = System.IO.Path.Combine(RemovedRoot(game), $"{System.IO.Path.GetFileName(folder)}_{DateTime.Now:yyyyMMdd_HHmmss}_{n}");
        Directory.Move(folder, target);
        return target;
    }

    /// <summary>The GameBanana picture kept for a folder installed from there (replacements carry none of their own).</summary>
    public static string? Preview(GameInstall game, string name)
    {
        var file = System.IO.Path.Combine(PreviewsRoot(game), FolderName(name) + ".jpg");
        return File.Exists(file) ? file : null;
    }

    public static void SavePreview(GameInstall game, string name, byte[] image)
    {
        Directory.CreateDirectory(PreviewsRoot(game));
        File.WriteAllBytes(System.IO.Path.Combine(PreviewsRoot(game), FolderName(name) + ".jpg"), image);
    }
}
