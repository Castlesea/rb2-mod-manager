using System.Diagnostics;

namespace RB2ModCore;

/// <summary>
/// The game folder and the places the Mod Manager works in. The game loads every .rb2mod in Modloader\ and
/// Modloader\Mods\ (not their subfolders: src/modloader/rb2mod_cache.cpp PrepareRb2Mods), so a turned-off mod waits in
/// Mods\Disabled\ and a removed one in Mods\Removed\ (nothing is deleted). Working folders (Modloader\Characters\, made by
/// the Engine) and the Raging Blast 1 roster (content\rb1\characters\, part of the game) use IDs too.
/// </summary>
public sealed class GameInstall
{
    public const string GameExe = "Raging Blast 2.exe";

    public string Root { get; }
    public string Modloader => Path.Combine(Root, "Modloader");
    public string Mods => Path.Combine(Modloader, "Mods");
    public string Disabled => Path.Combine(Mods, "Disabled");
    public string Removed => Path.Combine(Mods, "Removed");
    public string WorkingFolders => Path.Combine(Modloader, "Characters");
    public string Rb1Roster => Path.Combine(Root, "content", "rb1", "characters");

    public GameInstall(string root) => Root = Path.GetFullPath(root);

    /// <summary>The game folder this program sits in (or one of its parents), else null.</summary>
    public static GameInstall? Find(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, GameExe)) || Directory.Exists(Path.Combine(dir.FullName, "Modloader", "Mods")))
                return new GameInstall(dir.FullName);
        return null;
    }

    public bool Valid => File.Exists(Path.Combine(Root, GameExe)) || Directory.Exists(Modloader);

    /// <summary>The game of this folder is running: mods are not touched then (it holds their unpacked copies).</summary>
    public bool GameRunning()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)))
            try
            {
                using (process)
                    if (process.MainModule?.FileName is { } file && Path.GetFullPath(file).StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // another user's process, or no access: not ours to judge, say it runs (safer)
                return true;
            }
        return false;
    }
}
