using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RB2ModCore;

namespace RB2ModManager;

/// <summary>
/// Replacement mods (the older kind, most of GameBanana): installed as their own folders in Modloader\Characters, never
/// over the game's files; turned off by moving them out of there; folders that share a file are kept apart because the
/// game would load neither.
/// </summary>
public sealed partial class MainWindow
{
    private Dictionary<string, List<(ReplaceMod Other, List<string> Keys)>> replaceClashes = new(StringComparer.OrdinalIgnoreCase);

    private void ReadReplaceClashes() =>
        replaceClashes = library is null ? new(StringComparer.OrdinalIgnoreCase)
            : ReplaceMods.Clashes(library.Mods.Where(m => m.Replacement is not null).Select(m => m.Replacement!));

    private List<(ReplaceMod Other, List<string> Keys)> ClashesOf(InstalledMod mod) =>
        mod.Replacement is not null && replaceClashes.TryGetValue(mod.Path, out var list) ? list : [];

    private async Task<bool> ReplaceInstallable(GbFile file)
    {
        if (!file.IsArchive) return false;
        try { return ReplaceMods.HasAssets(await banana.ArchiveContents(file.Id)); }
        catch (HttpRequestException) { return false; }
    }

    /// <summary>A downloaded or picked archive: .rb2mod files inside go the .rb2mod way, otherwise it's a replacement mod.</summary>
    private async Task<int> InstallArchive(string archive, string name, (GbMod Mod, GbFile File)? source = null,
        IReadOnlyCollection<string>? replacing = null, IReadOnlyCollection<InstalledMod>? replacingPackages = null, bool ask = true)
    {
        if (game is null) return 0;
        var work = System.IO.Path.Combine(ModDownloads.Folder(game), $"x{Guid.NewGuid():N}"[..13]);
        try
        {
            List<string>? rb2 = null;
            ReplaceScan? scan = null;
            var failure = await RunBusy("Opening the archive…", () =>
            {
                try { rb2 = ModDownloads.ExtractRb2Mods(archive, System.IO.Path.Combine(work, "rb2mod")); return ""; }
                catch (InvalidDataException) { /* no .rb2mod: a replacement mod */ }
                ReplaceMods.ExtractAll(archive, System.IO.Path.Combine(work, "all"));
                scan = ReplaceMods.Scan(System.IO.Path.Combine(work, "all"), name);
                return "";
            });
            if (failure is null) return 0;
            if (rb2 is not null) return await InstallFiles(rb2, source, replacingPackages, ask);
            return await InstallReplace(scan!, source, replacing);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Asks which parts to install (variants of one costume exclude each other), then copies them in.</summary>
    private async Task<int> InstallReplace(ReplaceScan scan, (GbMod Mod, GbFile File)? source, IReadOnlyCollection<string>? replacing)
    {
        if (game is null || library is null) return 0;
        if (scan.Units.Count == 0)
        {
            await ShowMessage("Nothing to install", "This mod has no file the game can load from a mod folder."
                + (scan.Skipped.Count > 0 ? "\n\n" + string.Join("\n", scan.Skipped.Take(8)) : ""));
            return 0;
        }
        var installed = library.Mods.Where(m => m.Replacement is { Enabled: true }).Select(m => m.Replacement!)
            .Where(m => replacing is null || !replacing.Contains(m.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var boxes = new List<(ReplaceUnit Unit, CheckBox Box)>();
        var body = new StackPanel { Spacing = 10, MaxWidth = 600 };
        body.Children.Add(new TextBlock
        {
            Text = "These files take the place of the game's own models while the mod is on. The game's files aren't touched: the mod gets its own folder in Modloader\\Characters and can be turned off or removed any time.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = .8,
        });
        var list = new StackPanel { Spacing = 6 };
        var clashNote = new TextBlock { FontSize = 12, Foreground = WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var turnOff = new CheckBox { Content = "Turn off the installed mods that use the same files", FontSize = 13, IsChecked = true, IsVisible = false };
        var confirm = DialogButton("Install", Icons.Download, primary: true);
        var updating = false;
        void Update()
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Unit).ToList();
            var keys = chosen.SelectMany(u => u.Keys).ToHashSet();
            // a folder of the same name is replaced by the install, not in the way
            var clashing = installed.Where(m => m.Keys.Any(keys.Contains) && !chosen.Any(u => u.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            clashNote.IsVisible = turnOff.IsVisible = clashing.Count > 0;
            clashNote.Text = $"Same files as {string.Join(", ", clashing.Select(c => c.Name))}: the game loads neither copy of a file two mods share.";
            confirm.IsEnabled = chosen.Count > 0;
        }
        foreach (var unit in scan.Units)
        {
            var preselect = replacing is null ? !boxes.Any(b => b.Box.IsChecked == true && b.Unit.Keys.Intersect(unit.Keys).Any())
                : replacing.Contains(unit.Name, StringComparer.OrdinalIgnoreCase) || (boxes.Count == 0 && !scan.Units.Any(u => replacing.Contains(u.Name, StringComparer.OrdinalIgnoreCase)));
            var box = new CheckBox { IsChecked = preselect, VerticalAlignment = VerticalAlignment.Center };
            var words = new StackPanel { Spacing = 1 };
            words.Children.Add(new TextBlock { Text = unit.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(Text($"Replaces {unit.Summary}  ·  {unit.Files.Count} file{(unit.Files.Count == 1 ? "" : "s")}", 12, MutedBrush));
            box.Content = words;
            box.IsCheckedChanged += (_, _) =>
            {
                if (updating) return;
                updating = true;
                // variants of one costume: only one can be on
                if (box.IsChecked == true)
                    foreach (var other in boxes.Where(b => b.Box != box && b.Unit.Keys.Intersect(unit.Keys).Any())) other.Box.IsChecked = false;
                updating = false;
                Update();
            };
            boxes.Add((unit, box));
            list.Children.Add(box);
        }
        if (scan.Units.Count > 1 && scan.Units.Any(u => scan.Units.Any(o => o != u && o.Keys.Intersect(u.Keys).Any())))
            body.Children.Add(Text("Some parts are versions of the same costume: pick one of each.", 13, AccentHotBrush));
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300 });
        body.Children.Add(clashNote);
        body.Children.Add(turnOff);
        if (scan.Skipped.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = "Left out:\n" + string.Join("\n", scan.Skipped.Take(6)) + (scan.Skipped.Count > 6 ? $"\n… and {scan.Skipped.Count - 6} more" : ""),
                FontSize = 12, Opacity = .6, TextWrapping = TextWrapping.Wrap,
            });
        Update();
        if (!await ShowDialog(source is { } s ? $"Install {s.Mod.Name}" : "Install Replacement Mod", body, confirm)) return 0;
        var units = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Unit).ToList();
        var keysChosen = units.SelectMany(u => u.Keys).ToHashSet();
        var off = turnOff.IsChecked == true ? installed.Where(m => m.Keys.Any(keysChosen.Contains) && !units.Any(u => u.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))).ToList() : [];
        var game0 = game;
        var folders = await RunBusy("Installing…", () =>
        {
            if (game0.GameRunning()) throw new InvalidOperationException("Close the game first: it is using the mods.");
            foreach (var mod in off) ReplaceMods.SetEnabled(game0, mod, false);
            var done = units.Select(u => ReplaceMods.Install(game0, u)).ToList();
            // an update whose parts were renamed: the older folders go
            foreach (var old in replacing ?? [])
                if (!units.Any(u => u.Name.Equals(old, StringComparison.OrdinalIgnoreCase)) && ReplaceMods.Installed(game0).FirstOrDefault(m => m.Name.Equals(old, StringComparison.OrdinalIgnoreCase)) is { } gone)
                    ReplaceMods.Remove(game0, gone);
            return done;
        });
        if (folders is null) return 0;
        if (source is { } from)
        {
            byte[]? picture = null;
            if (from.Mod.Images.FirstOrDefault() is { } image)
                try { picture = await banana.Image(image.Medium); } catch (Exception) { }
            foreach (var unit in units)
                try
                {
                    ModDownloads.Record(game, unit.Name, unit.Name, from.Mod, from.File, "replace");
                    if (picture is not null) ReplaceMods.SavePreview(game, unit.Name, picture);
                }
                catch (IOException) { }
        }
        Refresh();
        selected = library?.Mods.FirstOrDefault(m => m.Source == ModSource.Replace && m.Name.Equals(units[0].Name, StringComparison.OrdinalIgnoreCase)) ?? selected;
        RebuildInstalled();
        SetStatus($"Installed {string.Join(", ", units.Select(u => u.Name))} in Modloader\\Characters." + (off.Count > 0 ? $" Turned off: {string.Join(", ", off.Select(o => o.Name))}." : ""), SuccessBrush);
        return units.Count;
    }

    /// <summary>A GameBanana file that holds no .rb2mod: downloaded, then installed as a replacement mod.</summary>
    private async Task<int> InstallReplaceFromBanana(GbMod mod, GbFile file, IReadOnlyCollection<string>? replacing = null)
    {
        var download = await DownloadWithProgress(file);
        if (download is null) return 0;
        try { return await InstallArchive(download, ReplaceMods.FolderName(mod.Name), (mod, file), replacing); }
        finally { try { File.Delete(download); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private Control ReplaceDetail(InstalledMod mod)
    {
        var box = new StackPanel { Spacing = 5 };
        box.Children.Add(new TextBlock { Text = "Replaces", FontSize = 15, FontWeight = FontWeight.SemiBold });
        var keys = mod.Replacement?.Keys ?? [];
        box.Children.Add(new TextBlock { Text = keys.Count == 0 ? "No model file the game can load." : ReplaceMods.Describe(keys), FontSize = 12.5, Opacity = .8, TextWrapping = TextWrapping.Wrap });
        box.Children.Add(Text($"{keys.Count} game file{(keys.Count == 1 ? "" : "s")}  ·  Modloader\\{(mod.Enabled ? "Characters" : "ModManager\\Disabled\\Characters")}\\{mod.Name}", 11.5, MutedBrush));
        foreach (var (other, shared) in ClashesOf(mod))
            box.Children.Add(IssueLine(new IdIssue(IssueLevel.Error, $"Same files as {other.Name} ({ReplaceMods.Describe(shared)}): the game loads neither. Turn one of them off.")));
        return new Border { Child = box, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) };
    }
}
