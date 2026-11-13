using Avalonia.Controls;
using RB2ModCore;

namespace RB2ModManager;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource closing = new();

    /// <summary>Started from a game folder (not a test copy): rb2mm: links open this program, and later ones come to this window.</summary>
    private void StartOneClick()
    {
        if (Program.GameFolder is null && GameInstall.Find(AppContext.BaseDirectory) is { Valid: true }) OneClick.Register();
        OneClick.Listen(link => _ = OneClickInstall(link), closing.Token);
        Closed += (_, _) => closing.Cancel();
    }

    /// <summary>A GameBanana 1-click link: the mod's page is read, then its file installs like the Install button.</summary>
    private async Task OneClickInstall(string link)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (game is null) { await ShowMessage("Choose the game folder first", "The Mod Manager needs the Raging Blast 2 folder to install mods."); return; }
        if (OneClick.Parse(link) is not { } target)
        {
            await ShowMessage("Not a mod link", $"This link isn't a GameBanana mod download:\n{link}");
            return;
        }
        SetStatus("Reading the mod on GameBanana…", MutedBrush);
        GbProfile profile;
        try { profile = await banana.Profile(target.Mod); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            await ShowMessage("Not installed", $"Couldn't read the mod on GameBanana: {e.Message}");
            return;
        }
        if (profile.Files.FirstOrDefault(f => f.Id == target.File) is not { } file)
        {
            await ShowMessage("Not installed", $"{profile.Mod.Name} has no file {target.File} any more on GameBanana.");
            return;
        }
        if (!file.Clean)
        {
            await ShowMessage("Not installed", $"GameBanana's virus check flagged {file.Name}.");
            return;
        }
        var count = await InstallFromBanana(profile.Mod, file);
        if (count > 0) ShowPage("installed");
    }
}
