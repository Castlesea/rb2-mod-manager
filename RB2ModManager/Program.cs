using System.Runtime.InteropServices;
using Avalonia;
using RB2ModCore;

namespace RB2ModManager;

public static class Program
{
    /// <summary>--game &lt;folder&gt;: work on that game folder (tests on a copy) instead of the one this program sits in.</summary>
    public static string? GameFolder { get; private set; }
    /// <summary>.rb2mod files given on the command line (opened with the program): offered for install at start.</summary>
    public static List<string> OpenFiles { get; } = [];
    /// <summary>--render-preview &lt;png&gt; [--page installed|ids|install|change]: draw the window to an image and exit.</summary>
    public static string? PreviewPath { get; private set; }
    public static string PreviewPage { get; private set; } = "installed";
    /// <summary>A GameBanana 1-click link (rb2mm:…) the program was opened with.</summary>
    public static string? OneClickLink { get; private set; }

    public static bool IsWine { get; } = DetectWine();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--cli") return Cli.Run(args[1..]);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--game", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) GameFolder = args[++i];
            else if (a.Equals("--render-preview", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) PreviewPath = Path.GetFullPath(args[++i]);
            else if (a.Equals("--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) PreviewPage = args[++i].ToLowerInvariant();
            else if (OneClick.IsLink(a)) OneClickLink = a;
            else if ((a.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase) || GameBananaClient.Archives.Contains(Path.GetExtension(a).ToLowerInvariant())) && File.Exists(a)) OpenFiles.Add(Path.GetFullPath(a));
        }
        // a click on GameBanana while the Mod Manager is open: that window installs it
        if (OneClickLink is not null && PreviewPath is null && OneClick.SendToRunning(OneClickLink)) return 0;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var win32 = new Win32PlatformOptions { OverlayPopups = true };
        if (IsWine)
        {
            // no DirectComposition under Wine: the software renderer works everywhere (the Launcher does the same)
            win32.RenderingMode = [Win32RenderingMode.Software];
            win32.CompositionMode = [Win32CompositionMode.RedirectionSurface];
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().With(win32);
    }

    private static bool DetectWine()
    {
        try
        {
            return NativeLibrary.TryLoad("ntdll.dll", out var ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);
        }
        catch
        {
            return false;
        }
    }

    public static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(directory);
            var text = exception.ToString();
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 3) text = text.Replace(profile, "<profile>", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(directory, "modmanager-crash.log"), $"Raging Blast 2 Mod Manager {typeof(Program).Assembly.GetName().Version}\n\n{text}");
        }
        catch
        {
            // nothing else to report to
        }
    }
}
