namespace RB2ModCore;

/// <summary>One character the organizer moves: from its ID to <see cref="To"/>, and why.</summary>
public sealed record IdMove(InstalledMod Mod, PackageInfo Package, int To, string Why);

/// <summary>
/// The automatic ID organizer. Only .rb2mod add-ons move: the game's own characters (0..100), its Raging Blast 1 roster
/// (content\rb1\characters, 120..192 today), the Engine's working folders and retail edits stay where they are and
/// their IDs are never given to anything else.
///   Fix: a character keeps its IDs unless they clash (the first the game loads keeps them: Modloader\ then Mods\, by
///   file name), sit in 0..100, or are past 255 while there is room below (then nothing could transform into it).
///   Pack: every add-on also moves down to the first free IDs from 101, closing the gaps, in the order of its IDs now.
/// </summary>
public static class IdOrganizer
{
    public static List<IdMove> Plan(ModLibrary library, bool pack)
    {
        var placed = new Dictionary<int, string>();
        for (var id = 0; id <= CharacterIds.GameCharacters; id++) placed[id] = "Raging Blast 2's own characters";
        var movable = new List<(InstalledMod Mod, PackageInfo Package)>();
        foreach (var (owner, package) in CharacterIds.Characters(library))
        {
            if (owner.Source == ModSource.Package && !package.RetailEdit) { movable.Add((owner, package)); continue; }
            var who = owner.Source switch
            {
                ModSource.Game => $"{package.Name} (the game's Raging Blast 1 roster)",
                ModSource.WorkingFolder => $"{package.Name} (Engine folder)",
                _ => package.Name,
            };
            foreach (var form in package.Forms) placed.TryAdd(form.Id, who);
        }
        var game = library.Game;
        int LoadOrder(InstalledMod mod) =>
            string.Equals(Path.GetDirectoryName(mod.Path), game.Modloader, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        movable = movable.OrderBy(c => LoadOrder(c.Mod)).ThenBy(c => Path.GetFileName(c.Mod.Path), StringComparer.OrdinalIgnoreCase).ToList();
        var used = () => placed.Keys.ToHashSet();
        void Place(InstalledMod mod, PackageInfo package, int start)
        {
            foreach (var id in CharacterIds.Shifted(package, start)) placed[id] = $"{package.Name} ({Path.GetFileName(mod.Path)})";
        }
        var moves = new List<IdMove>();

        if (pack)
        {
            // lowest IDs first, so each one only moves down
            foreach (var (mod, package) in movable.OrderBy(c => c.Package.Forms.Min(f => f.Id)))
            {
                var to = CharacterIds.FreeStart(used(), package);
                if (to != package.Id)
                    moves.Add(new IdMove(mod, package, to, Clash(package, placed) ?? (to < package.Id ? "closes the gap below it" : "its IDs are taken")));
                Place(mod, package, to);
            }
            return moves;
        }

        var problems = new List<(InstalledMod Mod, PackageInfo Package, string Why)>();
        var high = new List<(InstalledMod Mod, PackageInfo Package)>();
        foreach (var (mod, package) in movable)
        {
            if (Clash(package, placed) is { } why) { problems.Add((mod, package, why)); continue; }
            Place(mod, package, package.Id);
            if (package.Forms.Max(f => f.Id) > CharacterIds.LastByte) high.Add((mod, package));
        }
        foreach (var (mod, package, why) in problems)
        {
            var to = CharacterIds.FreeStart(used(), package);
            moves.Add(new IdMove(mod, package, to, why));
            Place(mod, package, to);
        }
        foreach (var (mod, package) in high)
        {
            foreach (var form in package.Forms) placed.Remove(form.Id);
            var to = CharacterIds.FreeStart(used(), package);
            if (CharacterIds.Shifted(package, to).Max() <= CharacterIds.LastByte)
            {
                moves.Add(new IdMove(mod, package, to, "past 255: no other character could transform into it"));
                Place(mod, package, to);
            }
            else Place(mod, package, package.Id);
        }
        return moves;
    }

    /// <summary>Why a character can't keep its IDs (one of the game's, or taken), else null.</summary>
    private static string? Clash(PackageInfo package, Dictionary<int, string> placed)
    {
        foreach (var form in package.Forms.OrderBy(f => f.Id))
        {
            if (form.Id <= CharacterIds.Reserved) return $"ID {form.Id} is one of Raging Blast 2's own (0..100)";
            if (placed.TryGetValue(form.Id, out var who)) return $"ID {form.Id} is also used by {who}";
        }
        return null;
    }
}
