using System.IO.Compression;

namespace RB2ModCore;

/// <summary>
/// The Engine's chip pictures of add-ons, <c>&lt;game&gt;\CharacterSelect\Chips\Addons\chara_chips_&lt;ID&gt;.png</c>: the
/// live Character Select shows them before a mod's own picture. They are named by ID only, so after an ID changes hands
/// (a mod moved, removed, or another one installed at that ID) the old picture shows on the new character (Android 18
/// Revamp's, from when it was 108, on Super Baby 2 at 108). <see cref="Tidy"/> moves such pictures aside:
/// <list type="bullet">
/// <item>no installed character has that ID;</item>
/// <item>the ID belongs to a .rb2mod whose own picture for that form is a different image, and the render is older than
/// the mod. (The Engine's render of a form is the very picture it packs as charsel_chip_ID.png: identical = kept.)</item>
/// </list>
/// Engine working folders and the game's Raging Blast 1 roster keep theirs (the Engine makes them). Nothing is deleted:
/// the pictures go to <c>Modloader\ModManager\Removed\Chips</c>, and their line leaves the Engine's chips_generated.tsv
/// so it can render them again.
/// </summary>
public static class ChipRenders
{
    public sealed record Stale(string File, int Id, string Reason);

    public static string Folder(GameInstall game) => Path.Combine(game.Root, "CharacterSelect", "Chips", "Addons");
    public static string RemovedRoot(GameInstall game) => Path.Combine(ReplaceMods.RemovedRoot(game), "Chips");
    private const string Manifest = "chips_generated.tsv";

    /// <summary>The pictures that no longer belong to the character at their ID.</summary>
    public static List<Stale> Find(ModLibrary library)
    {
        var folder = Folder(library.Game);
        var stale = new List<Stale>();
        if (!Directory.Exists(folder)) return stale;

        // who has each ID now
        var packages = new Dictionary<int, (InstalledMod Mod, PackageInfo Package)>();
        var kept = new HashSet<int>();
        foreach (var mod in library.Mods)
            foreach (var package in mod.Packages.Where(p => p.Kind == "character"))
                foreach (var form in package.Forms)
                    if (mod.Source == ModSource.Package) packages.TryAdd(form.Id, (mod, package));
                    else kept.Add(form.Id);
        foreach (var form in library.GameRoster.SelectMany(m => m.Packages).SelectMany(p => p.Forms)) kept.Add(form.Id);

        foreach (var file in Directory.EnumerateFiles(folder, "chara_chips_*.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(file)["chara_chips_".Length..];
            if (!int.TryParse(stem, out var id)) continue;  // costume renders (NNN_Kp) are the Engine's
            if (kept.Contains(id)) continue;
            if (!packages.TryGetValue(id, out var owner))
            {
                stale.Add(new Stale(file, id, "no installed character has this ID"));
                continue;
            }
            if (File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(owner.Mod.Path)) continue;
            var own = OwnChip(owner.Mod.Path, owner.Package, id);
            if (own is not null && !own.AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                stale.Add(new Stale(file, id, $"another picture than {owner.Mod.Info?.FileName}'s own, and older"));
        }
        return stale;
    }

    /// <summary>Moves <see cref="Find"/>'s pictures aside; returns one line per picture moved.</summary>
    public static List<string> Tidy(ModLibrary library)
    {
        var lines = new List<string>();
        var stale = Find(library);
        if (stale.Count == 0) return lines;
        var removed = RemovedRoot(library.Game);
        Directory.CreateDirectory(removed);
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var picture in stale)
        {
            var name = Path.GetFileNameWithoutExtension(picture.File);
            var target = Path.Combine(removed, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            for (var n = 2; File.Exists(target); n++) target = Path.Combine(removed, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{n}.png");
            try
            {
                File.Move(picture.File, target);
                moved.Add(name);
                lines.Add($"{Path.GetFileName(picture.File)}: {picture.Reason}");
            }
            catch (IOException) { /* in use: next time */ }
            catch (UnauthorizedAccessException) { }
        }
        var manifest = Path.Combine(Folder(library.Game), Manifest);
        if (moved.Count > 0 && File.Exists(manifest))
        {
            try
            {
                var keep = File.ReadAllLines(manifest).Where(line => !moved.Contains(line.Split('\t')[0])).ToArray();
                File.WriteAllLines(manifest, keep);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return lines;
    }

    /// <summary>
    /// The Character Select picture the .rb2mod carries for form <paramref name="id"/> of <paramref name="package"/>, as the
    /// game picks it: charsel_chip_ID.png, else another charsel_chip_*, else chara_chips_ID.png, else another; null when none.
    /// </summary>
    private static byte[]? OwnChip(string rb2mod, PackageInfo package, int id)
    {
        try
        {
            using var zip = ZipFile.OpenRead(rb2mod);
            var forms = $"Characters/{package.Folder}/Forms/";
            var found = new List<(int Rank, ZipArchiveEntry Entry)>();
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (!path.StartsWith(forms, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = path[forms.Length..].Split('/');
                if (parts.Length != 3 || !parts[0].StartsWith($"{id}_", StringComparison.Ordinal) ||
                    !parts[1].Equals("CharacterSelect", StringComparison.OrdinalIgnoreCase) ||
                    !parts[2].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var name = parts[2];
                var rank = name.Equals($"charsel_chip_{id}.png", StringComparison.OrdinalIgnoreCase) ? 0
                    : name.StartsWith("charsel_chip_", StringComparison.OrdinalIgnoreCase) ? 1
                    : name.Equals($"chara_chips_{id:000}.png", StringComparison.OrdinalIgnoreCase) ? 2
                    : name.StartsWith("chara_chips_", StringComparison.OrdinalIgnoreCase) ? 3 : -1;
                if (rank >= 0) found.Add((rank, entry));
            }
            if (found.Count == 0) return null;
            using var stream = found.OrderBy(f => f.Rank).First().Entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
}
