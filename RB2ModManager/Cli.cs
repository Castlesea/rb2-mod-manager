using RB2ModCore;

namespace RB2ModManager;

/// <summary>
/// --cli commands (tests on a copy of a game folder; the window does the same through the same calls):
///   map GAME                         the ID map with its issues and the free runs
///   list GAME                        the installed mods
///   plan GAME FILE                   what installing FILE would do
///   install GAME FILE                install it (moving characters whose IDs are taken)
///   change-id GAME MODFILE FOLDER ID move one character of an installed .rb2mod
///   enable GAME MODFILE on|off       turn a mod on or off
///   remove GAME MODFILE              take a mod out (kept in Mods\Removed)
///   delete GAME MODFILE              delete a mod (to the Recycle Bin)
///   chips GAME [tidy]                Character Select pictures left by another character (tidy: move them aside)
/// </summary>
public static class Cli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0].StartsWith("gb-")) return GameBanana(args).GetAwaiter().GetResult();
            if (args.Length < 2) return Usage();
            var game = new GameInstall(args[1]);
            if (!game.Valid) { Console.Error.WriteLine($"Not a game folder: {game.Root}"); return 2; }
            var library = ModLibrary.Scan(game);
            InstalledMod Installed(string path) =>
                library.Mods.FirstOrDefault(m => string.Equals(Path.GetFullPath(m.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Not installed: {path}");
            switch (args[0])
            {
                case "map":
                    foreach (var slot in CharacterIds.Map(library))
                    {
                        Console.WriteLine($"{CharacterIds.Span(slot.Package),-9} {slot.Package.Name,-32} {(slot.FromGame ? "game" : slot.Owner!.Name)}");
                        foreach (var issue in slot.Issues) Console.WriteLine($"          {issue.Level}: {issue.Text}");
                    }
                    Console.WriteLine("Free: " + string.Join(", ", CharacterIds.FreeRuns(CharacterIds.Used(library)).Select(r => r.Count == 1 ? $"{r.Start}" : $"{r.Start}-{r.Start + r.Count - 1}")));
                    return 0;
                case "list":
                    foreach (var mod in library.Mods)
                        Console.WriteLine($"{(mod.Enabled ? "on " : "off")} {mod.Source,-13} {mod.Name}  [{string.Join(", ", mod.Packages.Select(p => $"{p.Kind} {p.Folder}"))}]{(mod.Error is null ? "" : "  ERROR " + mod.Error)}");
                    return 0;
                case "plan" or "install" when args.Length >= 3:
                {
                    var info = Rb2ModFile.Read(args[2]);
                    var steps = ModActions.Plan(library, info);
                    foreach (var step in steps) Console.WriteLine($"{step.Package.Folder}: {step.Note}");
                    if (args[0] == "install")
                        foreach (var line in ModActions.Install(library, args[2], steps)) Console.WriteLine(line);
                    return 0;
                }
                case "replace-install" when args.Length >= 3:
                {
                    // replace-install GAME ARCHIVE [i,j]: the parts (default: the first of each group of versions)
                    var work = Path.Combine(Path.GetTempPath(), "rb2mm_" + Guid.NewGuid().ToString("N")[..8]);
                    ReplaceMods.ExtractAll(args[2], work);
                    var scan = ReplaceMods.Scan(work, ReplaceMods.FolderName(Path.GetFileNameWithoutExtension(args[2])));
                    for (var i = 0; i < scan.Units.Count; i++) Console.WriteLine($"  [{i}] {scan.Units[i].Name}: {scan.Units[i].Files.Count} files, replaces {scan.Units[i].Summary}");
                    foreach (var skip in scan.Skipped) Console.WriteLine($"  left out: {skip}");
                    var pick = args.Length > 3 ? args[3].Split(',').Select(int.Parse).ToList() : [];
                    if (pick.Count == 0)
                        for (var i = 0; i < scan.Units.Count; i++)
                            if (!pick.Any(p => scan.Units[p].Keys.Intersect(scan.Units[i].Keys).Any())) pick.Add(i);
                    foreach (var i in pick) Console.WriteLine("installed " + ReplaceMods.Install(game, scan.Units[i]));
                    Directory.Delete(work, true);
                    return 0;
                }
                case "updates":
                {
                    var client = new GameBananaClient();
                    var found = ModUpdates.Check(client, game, r => r.Kind == "replace" ? ReplaceMods.Exists(game, r.File) : library.Mods.Any(m => Path.GetFileName(m.Path).Equals(r.File, StringComparison.OrdinalIgnoreCase)),
                        async (r, f, c) => r.Kind == "replace" ? f.IsArchive && ReplaceMods.HasAssets(await client.ArchiveContents(f.Id, c)) : (await client.Rb2ModsIn(f, c)).Count > 0).GetAwaiter().GetResult();
                    foreach (var u in found) Console.WriteLine($"{u.Installed.File} ({u.Installed.Kind}, file {u.Installed.FileId}) -> {u.File.Name} ({u.File.Id}, {u.File.Added:yyyy-MM-dd})");
                    if (found.Count == 0) Console.WriteLine("No updates.");
                    return 0;
                }
                case "replace-repair":
                    Console.WriteLine($"PS3 mod.toml written to {ReplaceMods.RepairPs3Manifests(game)} folder(s)");
                    return 0;
                case "replace-list":
                    foreach (var mod in ReplaceMods.Installed(game)) Console.WriteLine($"{(mod.Enabled ? "on " : "off")} {mod.Name}: {mod.Keys.Count} keys ({ReplaceMods.Describe(mod.Keys)})");
                    foreach (var (path, list) in ReplaceMods.Clashes(ReplaceMods.Installed(game)))
                        Console.WriteLine($"CLASH {Path.GetFileName(path)} vs {string.Join(", ", list.Select(l => l.Other.Name + " [" + string.Join(",", l.Keys) + "]"))}");
                    return 0;
                case "organize":
                {
                    var moves = IdOrganizer.Plan(library, args.Contains("pack"));
                    foreach (var move in moves) Console.WriteLine($"{move.Package.Name} ({Path.GetFileName(move.Mod.Path)}): {CharacterIds.Span(move.Package)} -> {move.To}  ({move.Why})");
                    if (moves.Count == 0) Console.WriteLine("Nothing to move.");
                    if (args.Contains("apply"))
                        foreach (var line in ModActions.Organize(game, moves)) Console.WriteLine(line);
                    return 0;
                }
                case "change-id" when args.Length >= 5:
                    Console.WriteLine(ModActions.ChangeId(game, Installed(args[2]).Path, args[3], int.Parse(args[4])));
                    return 0;
                case "enable" when args.Length >= 4:
                    Console.WriteLine(ModActions.SetEnabled(game, Installed(args[2]), args[3] == "on"));
                    return 0;
                case "remove" when args.Length >= 3:
                    Console.WriteLine(ModActions.Remove(game, Installed(args[2])));
                    return 0;
                case "chips":
                {
                    var scanned = ModLibrary.Scan(game);
                    if (args.Length >= 3 && args[2] == "tidy")
                        foreach (var line in ChipRenders.Tidy(scanned)) Console.WriteLine("moved " + line);
                    else
                        foreach (var stale in ChipRenders.Find(scanned)) Console.WriteLine($"{System.IO.Path.GetFileName(stale.File)}: {stale.Reason}");
                    return 0;
                }
                case "delete" when args.Length >= 3:
                    ModActions.Delete(game, Installed(args[2]));
                    Console.WriteLine("deleted (Recycle Bin)");
                    return 0;
                default:
                    return Usage();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            return 1;
        }
    }

    /// <summary>gb-list [search] | gb-mod MODID | gb-get MODID FILEID OUTDIR (download + the .rb2mod files in it)</summary>
    private static async Task<int> GameBanana(string[] args)
    {
        var client = new GameBananaClient();
        try
        {
            switch (args[0])
            {
                case "gb-list":
                {
                    var page = await client.Browse(1, GameBananaClient.Sorts[0].Key, null, args.Length > 1 ? args[1] : null);
                    Console.WriteLine($"{page.Total} mods");
                    var files = await client.Files(page.Mods.Select(m => m.Id));
                    foreach (var mod in page.Mods)
                    {
                        var rb2 = new List<string>();
                        foreach (var file in files.GetValueOrDefault(mod.Id) ?? []) rb2.AddRange(await client.Rb2ModsIn(file));
                        Console.WriteLine($"{mod.Id} {mod.Name} by {mod.Submitter} [{mod.Category}] images={mod.Images.Count} files={files.GetValueOrDefault(mod.Id)?.Count} rb2mod={string.Join(",", rb2)}");
                    }
                    return 0;
                }
                case "gb-link" when args.Length > 1:
                    // a 1-click link as GameBanana sends it: which mod and file it installs
                    Console.WriteLine(OneClick.Parse(args[1]) is { } target ? $"mod {target.Mod} file {target.File}" : "not a mod link");
                    return 0;
                case "gb-mod" when args.Length > 1:
                {
                    var profile = await client.Profile(int.Parse(args[1]));
                    Console.WriteLine($"{profile.Mod.Name} v{profile.Mod.Version} downloads={profile.Downloads}\n{profile.Text[..Math.Min(400, profile.Text.Length)]}");
                    foreach (var file in profile.Files) Console.WriteLine($"  {file.Id} {file.Name} {file.Size} md5={file.Md5} clean={file.Clean}");
                    return 0;
                }
                case "gb-extract" when args.Length > 2:
                    foreach (var mod in ModDownloads.ExtractRb2Mods(args[1], args[2])) Console.WriteLine($"  {mod} {Rb2ModFile.Read(mod).Name}");
                    return 0;
                case "gb-get" when args.Length > 3:
                {
                    var profile = await client.Profile(int.Parse(args[1]));
                    var file = profile.Files.First(f => f.Id == int.Parse(args[2]));
                    var path = await client.Download(file, args[3], new Progress<double>(_ => { }));
                    Console.WriteLine($"downloaded {path} ({new FileInfo(path).Length} bytes, MD5 ok)");
                    try { foreach (var mod in ModDownloads.ExtractRb2Mods(path, Path.Combine(args[3], "rb2mod"))) Console.WriteLine("  " + mod); }
                    catch (InvalidDataException e) { Console.WriteLine("  " + e.Message); }
                    return 0;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            return 1;
        }
        return Usage();
    }

    private static int Usage()
    {
        Console.Error.WriteLine("--cli map|list GAME | plan|install GAME FILE | change-id GAME MODFILE FOLDER ID | enable GAME MODFILE on|off | remove GAME MODFILE");
        return 2;
    }
}
