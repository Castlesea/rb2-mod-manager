using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RB2ModCore;

/// <summary>What installing one character package of a mod does.</summary>
public sealed record InstallStep(PackageInfo Package, int TargetId, string Note)
{
    public bool Moves => TargetId != Package.Id;
}

/// <summary>
/// Changes to the installed mods. Every one refuses while the game of that folder runs, and nothing is deleted: a replaced
/// or removed .rb2mod goes to Mods\Removed\ with the time in its name.
/// </summary>
public static class ModActions
{
    private static void NotWhileRunning(GameInstall game)
    {
        if (game.GameRunning()) throw new InvalidOperationException("Close the game first: it is using the mods.");
    }

    // ---- install ------------------------------------------------------------------------------------------------------------

    /// <summary>What installing <paramref name="info"/> would do: each character at its own ID, or moved to a free one.</summary>
    public static List<InstallStep> Plan(ModLibrary library, ModInfo info, IReadOnlyCollection<InstalledMod>? replacing = null)
    {
        // the same file already installed is replaced (and an update's older files): their own IDs are not in the way
        var replaced = library.Mods.Where(m => m.Source == ModSource.Package && m.Path.EndsWith(System.IO.Path.DirectorySeparatorChar + info.FileName, StringComparison.OrdinalIgnoreCase))
            .Concat(replacing ?? []).Distinct().ToList();
        var used = CharacterIds.Used(library, replaced);
        var steps = new List<InstallStep>();
        foreach (var package in info.Packages.Where(p => p.Kind == "character"))
        {
            var ids = package.Forms.Select(f => f.Id).ToList();
            if (package.RetailEdit || !ids.Any(used.Contains))
            {
                steps.Add(new InstallStep(package, package.Id, package.RetailEdit ? "changes one of the game's characters" : replaced.Count > 0 ? "updates the installed one" : "its own ID is free"));
                foreach (var id in ids) used.Add(id);
                continue;
            }
            var target = CharacterIds.FreeStart(used, package);
            steps.Add(new InstallStep(package, target, $"ID {package.Id} is taken: goes to {target}"));
            foreach (var id in CharacterIds.Shifted(package, target)) used.Add(id);
        }
        return steps;
    }

    /// <summary>Installs a .rb2mod into Mods\ (the same name replaced, kept in Removed\), moving characters as <paramref name="steps"/> say.</summary>
    public static List<string> Install(ModLibrary library, string file, IReadOnlyList<InstallStep> steps) => Install(library, file, steps, out _);

    /// <summary>Installs, and says where the file ended up (a moved character can rename it).</summary>
    public static List<string> Install(ModLibrary library, string file, IReadOnlyList<InstallStep> steps, out string installed)
    {
        var game = library.Game;
        NotWhileRunning(game);
        var info = Rb2ModFile.Read(file);
        Directory.CreateDirectory(game.Mods);
        var target = System.IO.Path.Combine(game.Mods, info.FileName);
        var lines = new List<string>();
        // copied first: the file may be the installed one itself (picked from Mods\ or Mods\Disabled\)
        var temp = target + ".tmp";
        File.Copy(file, temp, true);
        foreach (var old in library.Mods.Where(m => m.Source == ModSource.Package && string.Equals(System.IO.Path.GetFileName(m.Path), info.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(m.Path)))
        {
            MoveAside(game, old.Path);
            lines.Add($"The installed {info.FileName} was kept in Mods\\Removed.");
        }
        File.Move(temp, target, true);
        var path = target;
        foreach (var step in steps.Where(s => s.Moves))
        {
            path = ChangeId(game, path, step.Package.Folder, step.TargetId, keepOld: false);
            lines.Add($"{step.Package.Name}: ID {step.Package.Id} was taken, installed as {step.TargetId}.");
        }
        lines.Insert(0, $"{info.Name} installed.");
        installed = path;
        return lines;
    }

    // ---- on / off / remove ----------------------------------------------------------------------------------------------------

    /// <summary>Turns a .rb2mod on (Mods\) or off (Mods\Disabled\, which the game does not read); returns its new path.</summary>
    public static string SetEnabled(GameInstall game, InstalledMod mod, bool enabled)
    {
        if (mod.Source != ModSource.Package) throw new InvalidOperationException("Only .rb2mod files are turned on and off here.");
        NotWhileRunning(game);
        var folder = enabled ? game.Mods : game.Disabled;
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(mod.Path));
        if (string.Equals(target, mod.Path, StringComparison.OrdinalIgnoreCase)) return target;
        if (File.Exists(target)) MoveAside(game, target);
        File.Move(mod.Path, target);
        return target;
    }

    /// <summary>Takes a .rb2mod out of the game: kept in Mods\Removed\ (the user empties it).</summary>
    public static string Remove(GameInstall game, InstalledMod mod)
    {
        if (mod.Source != ModSource.Package) throw new InvalidOperationException("Only .rb2mod files are removed here.");
        NotWhileRunning(game);
        return MoveAside(game, mod.Path);
    }

    /// <summary>
    /// Deletes a mod (a .rb2mod, or a replacement folder) for good: it goes to the Windows Recycle Bin, so a wrong click
    /// can still be undone there. Engine working folders are not deleted here.
    /// </summary>
    public static void Delete(GameInstall game, InstalledMod mod)
    {
        NotWhileRunning(game);
        switch (mod.Source)
        {
            case ModSource.Package:
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(mod.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                break;
            case ModSource.Replace when mod.Replacement is { } replacement:
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(replacement.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                break;
            default:
                throw new InvalidOperationException("Engine working folders are changed in the Raging Blast Engine.");
        }
    }

    private static string MoveAside(GameInstall game, string file)
    {
        Directory.CreateDirectory(game.Removed);
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var target = System.IO.Path.Combine(game.Removed, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{System.IO.Path.GetExtension(file)}");
        for (var n = 2; File.Exists(target); n++)
            target = System.IO.Path.Combine(game.Removed, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{n}{System.IO.Path.GetExtension(file)}");
        File.Move(file, target);
        return target;
    }

    // ---- character IDs ------------------------------------------------------------------------------------------------------

    /// <summary>Carries out an <see cref="IdOrganizer"/> plan (each mod rebuilt once per character it moves).</summary>
    public static List<string> Organize(GameInstall game, IReadOnlyList<IdMove> moves)
    {
        NotWhileRunning(game);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var move in moves)
        {
            var path = paths.TryGetValue(move.Mod.Path, out var moved) ? moved : move.Mod.Path;
            paths[move.Mod.Path] = ChangeId(game, path, move.Package.Folder, move.To);
            lines.Add($"{move.Package.Name}: {CharacterIds.Span(move.Package)} → {move.To}");
        }
        return lines;
    }

    /// <summary>
    /// Moves the character <paramref name="folder"/> of a .rb2mod to ID <paramref name="to"/> (<see cref="Renumber"/>):
    /// the archive is rebuilt beside the old one, the old one kept in Removed\ (unless <paramref name="keepOld"/> is false, a
    /// file this run made). A file named after the package ("234_goku_ssj4_daima_0.rb2mod") takes the new name. Returns the
    /// new path.
    /// </summary>
    public static string ChangeId(GameInstall game, string path, string folder, int to, bool keepOld = true)
    {
        NotWhileRunning(game);
        var info = Rb2ModFile.Read(path);
        var package = info.Packages.FirstOrDefault(p => p.Kind == "character" && p.Folder == folder) ?? throw new InvalidOperationException($"The mod has no character {folder}.");
        if (package.RetailEdit) throw new InvalidOperationException($"{package.Name} changes one of the game's characters: its ID is the game's.");
        if (to == package.Id) return path;
        var newFolder = $"{to:000}{folder[3..]}";
        var work = System.IO.Path.Combine(game.Mods, ".work", Guid.NewGuid().ToString("N"));
        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                Rb2ModFile.Check(zip);
                var root = System.IO.Path.GetFullPath(work) + System.IO.Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(work, entry.FullName.Replace('\\', '/')));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe path in the mod: {entry.FullName}");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }
            var characterFolder = System.IO.Path.Combine(work, "Characters", folder);
            Renumber(characterFolder, package.Id, to);
            Directory.Move(characterFolder, System.IO.Path.Combine(work, "Characters", newFolder));
            // the manifest names the package by its new folder and ID
            var manifestPath = System.IO.Path.Combine(work, Rb2ModFile.ManifestName);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: Rb2ModFile.Lenient) as JsonObject ?? throw new InvalidDataException("rb2mod.json is not an object.");
            foreach (var entry in (manifest["packages"] as JsonArray ?? []).OfType<JsonObject>())
                if (Rb2ModFile.Text(entry["folder"]) == folder) { entry["folder"] = newFolder; entry["id"] = to; }
            File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            var directory = System.IO.Path.GetDirectoryName(path)!;
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            // a name another mod already has (here, in the other folder the game reads, or turned off) gets _2, _3…: the
            // game refuses two .rb2mod files of one name, and the other mod must not be pushed out
            var baseName = stem == folder ? newFolder : stem;
            var target = System.IO.Path.Combine(directory, baseName + Rb2ModFile.Extension);
            bool Taken(string name) => !string.Equals(name, System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                && new[] { game.Modloader, game.Mods, game.Disabled }.Any(d => File.Exists(System.IO.Path.Combine(d, name)));
            for (var n = 2; Taken(System.IO.Path.GetFileName(target)); n++)
                target = System.IO.Path.Combine(directory, $"{baseName}_{n}{Rb2ModFile.Extension}");
            var temp = target + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var name = System.IO.Path.GetRelativePath(work, file).Replace('\\', '/');
                    var level = Rb2ModFile.Stored.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    zip.CreateEntryFromFile(file, name, level);
                }
            if (keepOld) MoveAside(game, path);
            else File.Delete(path);
            File.Move(temp, target);
            return target;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { var parent = System.IO.Path.Combine(game.Mods, ".work"); if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Moves an extracted character from ID <paramref name="from"/> to <paramref name="to"/>: every form ID (and add-on
    /// picture number) shifts by the same amount, transformation targets follow, and the folders / files named after an
    /// ID ("240_goku_black_0…", "chara_chips_240.png", "FormName_241_US.txt") and the local: references are renamed with
    /// them. The Raging Blast Engine's ModPackage.Renumber does the same.
    /// </summary>
    public static void Renumber(string folder, int from, int to)
    {
        var delta = to - from;
        var map = new Dictionary<int, int>();
        var presentation = new Dictionary<int, int>();
        var jsonPath = System.IO.Path.Combine(folder, "character.json");
        var json = JsonNode.Parse(File.ReadAllText(jsonPath), documentOptions: Rb2ModFile.Lenient) as JsonObject
                   ?? throw new InvalidDataException("character.json is not an object");
        int Shift(int id) => id >= 100 ? map[id] = id + delta : id;
        json["id"] = Shift(from);
        foreach (var form in (json["forms"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Rb2ModFile.Int(form["id"]) is { } formId) form["id"] = Shift(formId);
            if (Rb2ModFile.Int(form["presentation_id"]) is { } pid && pid >= 100 && pid + delta is >= 101 and <= 255) { presentation[pid] = pid + delta; form["presentation_id"] = pid + delta; }
        }
        foreach (var form in (json["forms"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var branch in (form["transformer"]?["branches"] as JsonArray ?? []).OfType<JsonObject>())
                if (Rb2ModFile.Int(branch["destination_id"]) is { } destination && map.TryGetValue(destination, out var moved)) branch["destination_id"] = moved;
        // Folders and model files start with the form ID; Character Select images end with the picture number, the name
        // files with the form ID.
        string Rename(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            var sep = System.IO.Path.DirectorySeparatorChar;
            var image = path.Contains($"{sep}CharacterSelect{sep}", StringComparison.OrdinalIgnoreCase) && !path.Contains($"{sep}Names{sep}", StringComparison.OrdinalIgnoreCase);
            // one pass each (a step-by-step replace turns 108→106 into 104 when 106 also moves)
            name = Regex.Replace(name, @"^(\d{3})_", m => Moved(map, m, "", "_"));
            name = Regex.Replace(name, @"_(\d{3})(?=[._])", m => Moved(image ? presentation : map, m, "_", ""));
            // a form's skill names: CharacterSelect\SuperAttacks\Skill_<form ID x 100 + slot>.txt (Skill_NN.txt stays)
            if (Regex.Match(name, @"^Skill_(\d{5,6})\.txt$", RegexOptions.IgnoreCase) is { Success: true } skill
                && int.Parse(skill.Groups[1].Value, CultureInfo.InvariantCulture) is var number && map.TryGetValue(number / 100, out var formTo))
                name = $"Skill_{formTo * 100 + number % 100}.txt";
            return name;
        }
        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        text = Regex.Replace(text, @"/(\d{3})_", m => Moved(map, m, "/", "_"));
        File.WriteAllText(jsonPath, text, new UTF8Encoding(false));
        var toml = System.IO.Path.Combine(folder, "mod.toml");
        if (File.Exists(toml))
            File.WriteAllText(toml, Regex.Replace(File.ReadAllText(toml), @"(?m)^(\s*requested_id\s*=\s*)\d+", m => m.Groups[1].Value + to.ToString(CultureInfo.InvariantCulture)), new UTF8Encoding(false));
        // the Engine's loadout preset names the attack files it made (effect_blast_chara_106_00_i.zpak): renamed with them
        foreach (var preset in Directory.EnumerateFiles(folder, "loadout_preset.json", SearchOption.AllDirectories))
        {
            var presetText = File.ReadAllText(preset);
            presetText = Regex.Replace(presetText, @"_(\d{3})(?=[._])", m => Moved(map, m, "_", ""));
            File.WriteAllText(preset, presetText, new UTF8Encoding(false));
        }
        // every file moved into a new tree under its new path: renaming in place could land on a name another form's
        // file still has (106 and 108 moving down by 2)
        var renumbered = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + ".renumbered";
        if (Directory.Exists(renumbered)) Directory.Delete(renumbered, true);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            var parts = System.IO.Path.GetRelativePath(folder, file).Split(System.IO.Path.DirectorySeparatorChar);
            var original = folder;
            var destination = renumbered;
            foreach (var part in parts)
            {
                original = System.IO.Path.Combine(original, part);
                destination = System.IO.Path.Combine(destination, Rename(original));
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Move(file, destination);
        }
        Directory.Delete(folder, true);
        Directory.Move(renumbered, folder);
    }

    private static string Moved(Dictionary<int, int> map, Match match, string before, string after) =>
        map.TryGetValue(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), out var moved) ? $"{before}{moved:000}{after}" : match.Value;
}
