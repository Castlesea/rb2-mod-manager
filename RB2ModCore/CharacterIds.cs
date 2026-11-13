namespace RB2ModCore;

public enum IssueLevel { Note, Warning, Error }

/// <summary>Something wrong (or worth knowing) about one character's IDs.</summary>
public sealed record IdIssue(IssueLevel Level, string Text);

/// <summary>One character on the ID map: who brings it, its forms, and what is wrong with them.</summary>
public sealed record CharacterSlot(InstalledMod? Owner, PackageInfo Package, IReadOnlyList<IdIssue> Issues)
{
    public int Id => Package.Id;
    public bool FromGame => Owner is null || Owner.Source == ModSource.Game;
    public IssueLevel? Worst => Issues.Count == 0 ? null : Issues.Max(i => i.Level);
}

/// <summary>
/// The character IDs the game sees. Raging Blast 2's own characters are 0..99 (100 is never given), the Raging Blast 1
/// roster it ships sits in content\rb1\characters (120..192 today), add-ons from 101 on. A character's forms are
/// numbered from its ID; transformation targets and Character Select picture numbers are one byte in the game, so they
/// must stay at 255 or below. Two characters on the same form ID: the game keeps one (a working folder or the
/// first loaded wins), the other is gone from the roster.
/// </summary>
public static class CharacterIds
{
    public const int FirstAddOn = 101, LastByte = 255, Reserved = 100, GameCharacters = 100;

    /// <summary>Every form ID something turned on uses (the game's own 0..100 included).</summary>
    public static HashSet<int> Used(ModLibrary library, InstalledMod? except = null, string? exceptFolder = null) =>
        Used(library, except is null ? [] : [except], exceptFolder);

    /// <summary>Every form ID in use, leaving out <paramref name="except"/> (mods an install replaces).</summary>
    public static HashSet<int> Used(ModLibrary library, IReadOnlyCollection<InstalledMod> except, string? exceptFolder = null)
    {
        var used = Enumerable.Range(0, GameCharacters + 1).ToHashSet();
        foreach (var (owner, package) in Characters(library))
        {
            if (except.Contains(owner) && (exceptFolder is null || package.Folder == exceptFolder)) continue;
            foreach (var form in package.Forms) used.Add(form.Id);
        }
        return used;
    }

    /// <summary>The first ID with <paramref name="forms"/> free IDs after it, at 255 or below when it fits (the Engine's rule).</summary>
    public static int FreeRun(HashSet<int> used, int forms)
    {
        forms = Math.Max(1, forms);
        bool Free(int start) => start != Reserved && !Enumerable.Range(start, forms).Any(used.Contains);
        for (var id = FirstAddOn; id + forms - 1 <= LastByte; id++)
            if (Free(id)) return id;
        var high = LastByte + 1;
        while (!Free(high)) high++;
        return high;
    }

    /// <summary>The form IDs of <paramref name="package"/> once moved to <paramref name="start"/> (gaps between forms kept).</summary>
    public static List<int> Shifted(PackageInfo package, int start) => package.Forms.Select(f => f.Id - package.Id + start).ToList();

    /// <summary>The first ID <paramref name="package"/> can move to (all its forms free, at 255 or below when it fits).</summary>
    public static int FreeStart(HashSet<int> used, PackageInfo package)
    {
        bool Free(int start) => Shifted(package, start).All(id => id >= FirstAddOn && !used.Contains(id));
        var last = Shifted(package, 0).Max();
        for (var id = FirstAddOn; id + last <= LastByte; id++)
            if (Free(id)) return id;
        var high = LastByte + 1;
        while (!Free(high)) high++;
        return high;
    }

    /// <summary>Whether <paramref name="start"/> fits a character of <paramref name="forms"/> forms (none of its IDs taken, 101 or more).</summary>
    public static bool Fits(HashSet<int> used, int start, int forms) =>
        start >= FirstAddOn && start != Reserved && !Enumerable.Range(start, Math.Max(1, forms)).Any(used.Contains);

    /// <summary>The turned-on characters: the game's Raging Blast 1 roster, the working folders and the packages.</summary>
    public static IEnumerable<(InstalledMod Owner, PackageInfo Package)> Characters(ModLibrary library) =>
        library.GameRoster.Concat(library.Mods.Where(m => m.Enabled))
            .SelectMany(m => m.Packages.Where(p => p.Kind == "character").Select(p => (m, p)));

    /// <summary>The ID map: every turned-on character with its issues, by ID.</summary>
    public static List<CharacterSlot> Map(ModLibrary library)
    {
        var characters = Characters(library).ToList();
        var byForm = new Dictionary<int, List<(InstalledMod Owner, PackageInfo Package)>>();
        foreach (var c in characters)
            foreach (var form in c.Package.Forms)
                (byForm.TryGetValue(form.Id, out var list) ? list : byForm[form.Id] = []).Add(c);
        var known = byForm.Keys.ToHashSet();
        var slots = new List<CharacterSlot>();
        foreach (var (owner, package) in characters)
        {
            var issues = new List<IdIssue>();
            var mine = owner.Source != ModSource.Game;
            // the same form IDs in another character: one issue per other character, with the IDs they share
            var clashes = new Dictionary<(InstalledMod Owner, PackageInfo Package), List<int>>();
            foreach (var form in package.Forms)
                foreach (var other in byForm[form.Id].Where(o => o.Owner != owner || o.Package != package))
                    (clashes.TryGetValue(other, out var ids) ? ids : clashes[other] = []).Add(form.Id);
            foreach (var ((otherOwner, otherPackage), ids) in clashes)
            {
                var which = Range(ids);
                // an Engine working folder and the .rb2mod of the same character: the game takes the folder
                if (otherPackage.Folder == package.Folder && (owner.Source == ModSource.WorkingFolder || otherOwner.Source == ModSource.WorkingFolder))
                    issues.Add(new IdIssue(IssueLevel.Note, owner.Source == ModSource.WorkingFolder
                        ? $"Also installed as {otherOwner.Name}: the game uses this working folder."
                        : $"The Engine's working folder of {package.Folder} is used instead of this one."));
                else
                    issues.Add(new IdIssue(IssueLevel.Error, $"{which} also used by {(otherOwner.Source == ModSource.Game ? $"{otherPackage.Name} (the game's Raging Blast 1 roster)" : $"{otherPackage.Name} ({(otherOwner.Source == ModSource.Package ? System.IO.Path.GetFileName(otherOwner.Path) : otherOwner.Name)})")}: only one of them shows in the game."));
            }
            foreach (var form in package.Forms)
            {
                var label = package.Forms.Count > 1 ? $"Form {form.Id}{(form.Name.Length > 0 ? $" ({form.Name})" : "")}" : $"ID {form.Id}";
                if (mine && !package.RetailEdit && form.Id <= Reserved)
                    issues.Add(new IdIssue(IssueLevel.Error, $"{label} is one of Raging Blast 2's own characters (0..100): move it to 101 or more."));
                if (form.PresentationId > LastByte)
                    issues.Add(new IdIssue(IssueLevel.Error, $"{label}: its picture number {form.PresentationId} is past 255; the game reads one byte."));
                foreach (var target in form.Destinations)
                {
                    if (target > LastByte)
                        issues.Add(new IdIssue(IssueLevel.Error, $"{label} transforms into {target}, past 255: the game reads one byte, the transformation breaks."));
                    else if (target > Reserved && !known.Contains(target))
                        issues.Add(new IdIssue(IssueLevel.Warning, $"{label} transforms into {target}, which nothing installed has."));
                }
            }
            if (mine && package.Forms.Any(f => f.Id > LastByte) && issues.All(i => !i.Text.Contains("past 255")))
                issues.Add(new IdIssue(IssueLevel.Note, "IDs past 255: fine on their own, but no other character can transform into it."));
            slots.Add(new CharacterSlot(owner, package, issues));
        }
        return slots.OrderBy(s => s.Id).ThenBy(s => s.FromGame ? 0 : 1).ToList();
    }

    /// <summary>"ID 240" / "IDs 240–243" / "IDs 240, 242".</summary>
    public static string Range(IReadOnlyCollection<int> ids)
    {
        var sorted = ids.Distinct().Order().ToList();
        if (sorted.Count == 1) return $"ID {sorted[0]} is";
        var contiguous = sorted[^1] - sorted[0] == sorted.Count - 1;
        return contiguous ? $"IDs {sorted[0]}–{sorted[^1]} are" : $"IDs {string.Join(", ", sorted)} are";
    }

    /// <summary>A character's form IDs as runs: "240", "240–243", "101–102, 105".</summary>
    public static string Span(PackageInfo package)
    {
        var ids = package.Forms.Select(f => f.Id).Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            var start = ids[i];
            while (i + 1 < ids.Count && ids[i + 1] == ids[i] + 1) i++;
            parts.Add(ids[i] == start ? $"{start}" : $"{start}–{ids[i]}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>The free IDs from 101 to 255, as runs (start, count).</summary>
    public static List<(int Start, int Count)> FreeRuns(HashSet<int> used)
    {
        var runs = new List<(int, int)>();
        for (var id = FirstAddOn; id <= LastByte; id++)
        {
            if (used.Contains(id)) continue;
            var start = id;
            while (id + 1 <= LastByte && !used.Contains(id + 1)) id++;
            runs.Add((start, id - start + 1));
        }
        return runs;
    }
}
