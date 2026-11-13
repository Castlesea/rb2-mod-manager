using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RB2ModCore;

namespace RB2ModManager;

/// <summary>
/// Updates of the mods installed from GameBanana: checked once when the window opens (quietly) and with the Installed
/// tab's button; an update downloads the newer file and installs it in place of the older one (kept in Removed).
/// </summary>
public sealed partial class MainWindow
{
    private List<ModUpdate> updates = [];
    private bool checkingUpdates;
    private Button updatesButton = null!;

    private Button BuildUpdatesButton()
    {
        updatesButton = SmallButton("Check for Updates", Icons.Sync, (_, _) => _ = UpdatesClicked());
        updatesButton.Height = 36;
        updatesButton.Margin = new Thickness(10, 0, 0, 0);
        ToolTip.SetTip(updatesButton, "Looks on GameBanana for newer files of the mods installed from there");
        return updatesButton;
    }

    private int PendingUpdates => updates.Select(u => (u.Installed.ModId, u.File.Id)).Distinct().Count();

    private void ShowUpdatesButton()
    {
        updatesButton.Content = ButtonContent(PendingUpdates > 0 ? Icons.Download : Icons.Sync,
            checkingUpdates ? "Checking…" : PendingUpdates > 0 ? $"Update All ({PendingUpdates})" : "Check for Updates", 13);
        updatesButton.IsEnabled = !checkingUpdates && game is not null && !(PendingUpdates > 0 && gameRunning);
        if (PendingUpdates > 0) { updatesButton.Background = new SolidColorBrush(App.Accent); updatesButton.BorderThickness = new Thickness(0); }
        else { updatesButton.Background = new SolidColorBrush(Color.FromArgb(200, 44, 22, 27)); updatesButton.BorderThickness = new Thickness(.65); }
    }

    private Task UpdatesClicked() => PendingUpdates > 0 ? UpdateAll() : CheckUpdates(manual: true);

    private async Task CheckUpdates(bool manual)
    {
        if (game is null || checkingUpdates) return;
        checkingUpdates = true;
        ShowUpdatesButton();
        try
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { game.Modloader, game.Mods, game.Disabled })
                if (Directory.Exists(folder))
                    foreach (var file in Directory.EnumerateFiles(folder, "*" + Rb2ModFile.Extension)) installed.Add(System.IO.Path.GetFileName(file));
            var game0 = game;
            updates = await ModUpdates.Check(banana, game0,
                r => r.Kind == "rb2mod" ? installed.Contains(r.File) : ReplaceMods.Exists(game0, r.File),
                async (r, f, cancel) => r.Kind == "rb2mod" ? (await banana.Rb2ModsIn(f, cancel)).Count > 0 : await ReplaceInstallable(f));
            if (manual)
                SetStatus(PendingUpdates == 0 ? "Every mod installed from GameBanana is up to date." : $"{PendingUpdates} update{(PendingUpdates == 1 ? "" : "s")} on GameBanana.",
                    PendingUpdates == 0 ? SuccessBrush : AccentHotBrush);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (manual) SetStatus($"Couldn't check GameBanana: {(e is TaskCanceledException ? "it took too long to answer." : e.Message)}", WarningBrush);
        }
        finally
        {
            checkingUpdates = false;
            ShowUpdatesButton();
            RebuildInstalled();
        }
    }

    private ModUpdate? UpdateFor(InstalledMod mod) => mod.Source switch
    {
        ModSource.Package => updates.FirstOrDefault(u => u.Installed.Kind == "rb2mod" && string.Equals(u.Installed.File, System.IO.Path.GetFileName(mod.Path), StringComparison.OrdinalIgnoreCase)),
        ModSource.Replace => updates.FirstOrDefault(u => u.Installed.Kind == "replace" && string.Equals(u.Installed.File, System.IO.Path.GetFileName(mod.Path), StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    private Control UpdateBanner(ModUpdate update)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text("Update on GameBanana", 13, AccentHotBrush, true));
        words.Children.Add(Text($"{update.File.Name}  ·  {update.File.Added.LocalDateTime:yyyy-MM-dd}  ·  {Size(update.File.Size)}", 11.5, MutedBrush));
        grid.Children.Add(words);
        var button = DialogButton("Update", Icons.Download, primary: true);
        button.MinWidth = 96;
        button.Height = 32;
        button.IsEnabled = !gameRunning;
        button.Click += (_, _) => _ = UpdateOne(update);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return new Border { Child = grid, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(90, 241, 84, 50)) };
    }

    private async Task<bool> UpdateOne(ModUpdate update)
    {
        if (game is null || library is null) return false;
        GbProfile profile;
        try { profile = await banana.Profile(update.Installed.ModId); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            await ShowMessage("Not updated", $"Couldn't read the mod on GameBanana: {e.Message}");
            return false;
        }
        var same = updates.Where(u => u.Installed.ModId == update.Installed.ModId && u.File.Id == update.File.Id).ToList();
        int count;
        if (update.Installed.Kind == "replace")
            count = await InstallReplaceFromBanana(profile.Mod, update.File, same.Select(u => u.Installed.File).ToList());
        else
        {
            var olds = library.Mods.Where(m => m.Source == ModSource.Package
                && same.Any(u => string.Equals(u.Installed.File, System.IO.Path.GetFileName(m.Path), StringComparison.OrdinalIgnoreCase))).ToList();
            count = await InstallFromBanana(profile.Mod, update.File, olds, ask: false);
        }
        if (count == 0) return false;
        updates.RemoveAll(u => same.Contains(u));
        Refresh();
        ShowUpdatesButton();
        SetStatus($"{profile.Mod.Name} updated.", SuccessBrush);
        return true;
    }

    private async Task UpdateAll()
    {
        var done = 0;
        foreach (var update in updates.GroupBy(u => (u.Installed.ModId, u.File.Id)).Select(g => g.First()).ToList())
            if (await UpdateOne(update)) done++;
        SetStatus(done == 0 ? "Nothing was updated." : $"{done} mod{(done == 1 ? "" : "s")} updated.", done == 0 ? WarningBrush : SuccessBrush);
    }
}
