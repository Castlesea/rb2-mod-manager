using System.IO.Pipes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Microsoft.Win32;
using RB2ModCore;

namespace RB2ModManager;

/// <summary>
/// 1-click install from the GameBanana site. GameBanana's mod-manager buttons open "&lt;scheme&gt;:https://gamebanana.com/mmdl/
/// &lt;file id&gt;,&lt;item type&gt;,&lt;mod id&gt;"; this program registers the scheme "rb2mm" for the user (HKCU\Software\Classes\rb2mm,
/// no admin, also under Wine) every time it starts from a game folder, pointing at itself. A click while the Mod Manager
/// is already open is handed to that window through a named pipe instead of opening a second one.
/// </summary>
public static class OneClick
{
    public const string Scheme = "rb2mm";
    private const string PipeName = "RB2ModManager.OneClick";

    public static bool IsLink(string text) => text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>(mod, file) of a GameBanana 1-click link, or null when it isn't one for a mod. The file's URL may carry a
    /// query (GameBanana adds "?toolid=N", the mod manager's own id: "mmdl/1887436?toolid=24935,Mod,739905").</summary>
    public static (int Mod, int File)? Parse(string link)
    {
        var match = Regex.Match(link, @"mmdl/(\d+)(?:\?[^,]*)?,(\w+),(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !match.Groups[2].Value.Equals("Mod", StringComparison.OrdinalIgnoreCase)) return null;
        return (int.Parse(match.Groups[3].Value), int.Parse(match.Groups[1].Value));
    }

    /// <summary>Points rb2mm: links at this program (only written when it points elsewhere).</summary>
    public static void Register()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var command = $"\"{exe}\" \"%1\"";
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            using var open = root.CreateSubKey(@"shell\open\command");
            if (open.GetValue(null) as string == command) return;
            root.SetValue(null, "URL:Raging Blast 2 Mod Manager");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            open.SetValue(null, command);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    /// <summary>Hands a link to an open Mod Manager; false when none is open.</summary>
    public static bool SendToRunning(string link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            using var writer = new StreamWriter(client);
            writer.WriteLine(link);
            writer.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Listens for links from later clicks while the window is open.</summary>
    public static void Listen(Action<string> received, CancellationToken cancel)
    {
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cancel);
                    using var reader = new StreamReader(server);
                    if (await reader.ReadLineAsync(cancel) is { Length: > 0 } link)
                        Dispatcher.UIThread.Post(() => received(link));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { await Task.Delay(500, CancellationToken.None); }
            }
        }, CancellationToken.None);
    }
}
