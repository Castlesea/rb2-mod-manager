using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace RB2ModManager;

public sealed class App : Application
{
    // the Launcher's palette
    public static readonly Color Accent = Color.FromArgb(255, 241, 84, 50);
    public static readonly Color AccentHot = Color.FromArgb(255, 255, 145, 91);
    public static readonly Color Panel = Color.FromArgb(224, 21, 10, 14);
    public static readonly Color PanelBorder = Color.FromArgb(48, 255, 129, 90);
    public static readonly Color ControlFill = Color.FromArgb(220, 31, 22, 27);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        foreach (var (key, color) in new Dictionary<string, Color>
                 {
                     ["SystemAccentColor"] = Accent, ["SystemAccentColorLight1"] = AccentHot,
                     ["SystemAccentColorLight2"] = AccentHot, ["SystemAccentColorDark1"] = Accent,
                 })
            Resources[key] = color;
        void Brush(string key, Color color) => Resources[key] = new SolidColorBrush(color);
        Brush("ComboBoxBackground", ControlFill);
        Brush("ComboBoxBackgroundPointerOver", Color.FromArgb(235, 44, 28, 33));
        Brush("ComboBoxBackgroundPressed", Color.FromArgb(235, 52, 30, 34));
        Brush("ComboBoxBackgroundFocused", ControlFill);
        Brush("ComboBoxBackgroundUnfocused", ControlFill);
        Brush("ComboBoxBorderBrush", Color.FromArgb(60, 255, 129, 90));
        Brush("ComboBoxBorderBrushPointerOver", Color.FromArgb(90, 255, 129, 90));
        Brush("ComboBoxBorderBrushPressed", Color.FromArgb(110, 255, 129, 90));
        Brush("ComboBoxDropDownBackground", Color.FromArgb(255, 26, 14, 18));
        Brush("ComboBoxDropDownBorderBrush", Color.FromArgb(70, 241, 84, 50));
        Brush("ComboBoxItemBackgroundSelected", Color.FromArgb(90, 241, 84, 50));
        Brush("ComboBoxItemBackgroundSelectedPointerOver", Color.FromArgb(120, 241, 84, 50));
        Brush("ComboBoxItemBackgroundPointerOver", Color.FromArgb(50, 255, 255, 255));
        Brush("ToggleSwitchFillOn", Accent);
        Brush("ToggleSwitchFillOnPointerOver", AccentHot);
        Brush("ToggleSwitchFillOnPressed", Accent);
        Brush("ToggleSwitchFillOff", Color.FromArgb(220, 31, 22, 27));
        Brush("ToggleSwitchStrokeOff", Color.FromArgb(120, 255, 255, 255));
        Brush("ButtonBackgroundPointerOver", Color.FromArgb(200, 40, 24, 29));
        Brush("ButtonBackgroundPressed", Color.FromArgb(220, 52, 30, 34));
        Brush("ButtonBorderBrushPointerOver", Color.FromArgb(70, 255, 129, 90));
        Brush("ButtonForegroundPointerOver", Colors.White);
        Brush("ButtonForegroundPressed", Colors.White);
        Brush("TextControlBackground", ControlFill);
        Brush("TextControlBackgroundPointerOver", Color.FromArgb(235, 44, 28, 33));
        Brush("TextControlBackgroundFocused", Color.FromArgb(255, 31, 22, 27));
        Brush("TextControlBorderBrush", Color.FromArgb(60, 255, 129, 90));
        Brush("TextControlBorderBrushPointerOver", Color.FromArgb(90, 255, 129, 90));
        Brush("TextControlBorderBrushFocused", Accent);
        Brush("TextControlForegroundFocused", Colors.White);
        Brush("CheckBoxCheckBackgroundFillChecked", Accent);
        Brush("CheckBoxCheckBackgroundFillCheckedPointerOver", AccentHot);
        Brush("CheckBoxCheckBackgroundStrokeChecked", Accent);
        Brush("SystemControlFocusVisualPrimaryBrush", AccentHot);
        Brush("SystemControlFocusVisualSecondaryBrush", Color.FromArgb(160, 20, 8, 12));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(Program.GameFolder, Program.OpenFiles, preview: Program.PreviewPath is not null);
            if (Program.PreviewPath is { } path)
            {
                // shown off screen: lists (ScrollViewer content) only lay out in a live window
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(-20000, -20000);
                window.Opened += async (_, _) =>
                {
                    await window.PrepareForPreview(Program.PreviewPage);
                    await Task.Delay(600);
                    RenderPreview(window, path);
                    desktop.Shutdown();
                };
            }
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void RenderPreview(MainWindow window, string path)
    {
        var root = (Control)window.Content!;
        var size = root.Bounds.Size;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path);
    }
}
