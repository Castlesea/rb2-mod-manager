using System.Text.Json.Nodes;

namespace RB2ModCore;

/// <summary>Package: a .rb2mod. WorkingFolder: an Engine folder. Game: the game's RB1 roster. Replace: a replacement folder (<see cref="ReplaceMods"/>).</summary>
public enum ModSource { Package, WorkingFolder, Game, Replace }

/// <summary>One installed thing: a .rb2mod (on or off), an Engine working folder, or a character the game ships.</summary>
public sealed class InstalledMod
{
    public required string Path { get; init; }
    public required ModSource Source { get; init; }
    public required bool Enabled { get; init; }
    public ModInfo? Info { get; init; }
    /// <summary>Why it could not be read (a broken or unsafe file).</summary>
    public string? Error { get; init; }
    public string Name => Info?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
    public IReadOnlyList<PackageInfo> Packages => Info?.Packages ?? [];
    /// <summary>A replacement folder: the game files it takes the place of (as the game matches them).</summary>
    public ReplaceMod? Replacement { get; init; }
}

/// <summary>Everything installed in a game folder, read once (<see cref="Scan"/>).</summary>
public sealed class ModLibrary
{
    public required GameInstall Game { get; init; }
    public List<InstalledMod> Mods { get; } = [];
    /// <summary>The Raging Blast 1 characters the game ships (content\rb1\characters).</summary>
    public List<InstalledMod> GameRoster { get; } = [];

    public static ModLibrary Scan(GameInstall game)
    {
        var library = new ModLibrary { Game = game };
        void Packages(string folder, bool enabled)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*" + Rb2ModFile.Extension).Order(StringComparer.OrdinalIgnoreCase))
            {
                try { library.Mods.Add(new InstalledMod { Path = file, Source = ModSource.Package, Enabled = enabled, Info = Rb2ModFile.Read(file) }); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    library.Mods.Add(new InstalledMod { Path = file, Source = ModSource.Package, Enabled = enabled, Error = e.Message });
                }
            }
        }
        Packages(game.Modloader, true);
        Packages(game.Mods, true);
        Packages(game.Disabled, false);
        foreach (var (folder, package) in Folders(game.WorkingFolders))
            library.Mods.Add(new InstalledMod
            {
                Path = folder, Source = ModSource.WorkingFolder, Enabled = true,
                Info = new ModInfo(folder, package.Name, "", "", "A working folder (Modloader\\Characters): the Raging Blast Engine edits it.", "", [package], null),
            });
        foreach (var replacement in ReplaceMods.Installed(game))
            library.Mods.Add(new InstalledMod
            {
                Path = replacement.Path, Source = ModSource.Replace, Enabled = replacement.Enabled, Replacement = replacement,
                Info = new ModInfo(replacement.Path, replacement.Name, "", "", "", "",
                    [new PackageInfo("replace", replacement.Name, -1, replacement.Name, false, [])],
                    ReplaceMods.Preview(game, replacement.Name) is { } picture ? File.ReadAllBytes(picture) : null),
            });
        foreach (var (folder, package) in Folders(game.Rb1Roster))
            library.GameRoster.Add(new InstalledMod
            {
                Path = folder, Source = ModSource.Game, Enabled = true,
                Info = new ModInfo(folder, package.Name, "Raging Blast 2 PC", "", "", "", [package], null),
            });
        return library;
    }

    /// <summary>The character folders (NNN_name with a character.json) of a directory.</summary>
    private static IEnumerable<(string Folder, PackageInfo Package)> Folders(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(folder);
            var file = System.IO.Path.Combine(folder, "character.json");
            if (name.Length < 4 || !char.IsAsciiDigit(name[0])) continue;
            // An add-on of the older layout has no character.json: mod.toml names it ([character] requested_id / name)
            // and each form is a Forms\NNN_* folder. The game loads it (RB1's Super Buu 183 with forms 184 and 185), so
            // its IDs are taken; skipping it showed 183..185 as free (2026-11-13).
            if (!File.Exists(file))
            {
                var toml = System.IO.Path.Combine(folder, "mod.toml");
                if (!File.Exists(toml) || !int.TryParse(name[..3], out var folderId)) continue;
                var text = File.ReadAllText(toml);
                var requested = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^\s*requested_id\s*=\s*(\d+)");
                var title = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^\s*name\s*=\s*""([^""]*)""");
                var baseId = requested.Success ? int.Parse(requested.Groups[1].Value) : folderId;
                var forms = new List<FormInfo> { new(baseId, title.Success ? title.Groups[1].Value : name, -1, []) };
                var formsRoot = System.IO.Path.Combine(folder, "Forms");
                if (Directory.Exists(formsRoot))
                    foreach (var formFolder in Directory.EnumerateDirectories(formsRoot).Order(StringComparer.OrdinalIgnoreCase))
                        if (System.IO.Path.GetFileName(formFolder) is { Length: > 3 } formName && int.TryParse(formName[..3], out var formId) && formId != baseId)
                            forms.Add(new FormInfo(formId, formName, -1, []));
                yield return (folder, new PackageInfo("character", name, baseId, forms[0].Name, false, forms));
                continue;
            }
            JsonObject? json;
            try { json = JsonNode.Parse(File.ReadAllText(file), documentOptions: Rb2ModFile.Lenient) as JsonObject; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { continue; }
            if (json is null) continue;
            var id = Rb2ModFile.Int(json["id"]) ?? int.Parse(name[..3]);
            yield return (folder, new PackageInfo("character", name, id, Rb2ModFile.Text(json["name"]) ?? name, false, Rb2ModFile.Forms(json, id)));
        }
    }
}
