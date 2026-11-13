using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RB2ModCore;
using Path = Avalonia.Controls.Shapes.Path;

namespace RB2ModManager;

/// <summary>
/// The Mod Manager window: the installed mods (on/off, details, remove) and the character ID map (who uses which ID,
/// clashes, free runs, Change ID). Every change goes through RB2ModCore and is refused while the game runs.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(App.Accent);
    private static readonly IBrush AccentHotBrush = new SolidColorBrush(App.AccentHot);
    private static readonly IBrush PanelBrush = new SolidColorBrush(App.Panel);
    private static readonly IBrush PanelBorderBrush = new SolidColorBrush(App.PanelBorder);
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.FromArgb(255, 126, 224, 129));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromArgb(255, 255, 196, 92));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromArgb(255, 255, 104, 88));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly IBrush ChipBrush = new SolidColorBrush(Color.FromArgb(150, 44, 26, 31));
    private static readonly FontFamily InterFont = new("fonts:Inter#Inter");

    private readonly bool preview;
    private GameInstall? game;
    private ModLibrary? library;
    private List<CharacterSlot> map = [];
    private InstalledMod? selected;
    private string page = "installed";
    private bool gameRunning;
    private bool rebuilding;
    private readonly Dictionary<string, Bitmap?> previews = new(StringComparer.OrdinalIgnoreCase);

    private readonly StackPanel modList = new();
    private readonly TextBox search = new();
    private readonly TextBlock modCount = new();
    private readonly Dictionary<string, IImage?> faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Border detailHost = new();
    // The Installed list's order (Sort by): a fixed order, so turning a mod off no longer sends it to the end.
    private static readonly string[] SortChoices = ["ID", "Name", "Enabled first", "Disabled first", "Author"];
    private readonly ComboBox sortBox = new();
    private readonly TextBlock statusText = new() { FontSize = 12, Opacity = .72, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock gameText = new() { FontSize = 12, Opacity = .5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border runningBanner = new();
    private readonly Grid overlay = new() { IsVisible = false };
    private readonly Panel pageHost = new();
    private Control installedPage = null!;
    private Control idsPage = null!;
    private Control noGamePage = null!;
    private Button installedTab = null!;
    private Button idsTab = null!;
    private Button addButton = null!;
    private Button playButton = null!;
    private TaskCompletionSource<bool>? dialogResult;

    public MainWindow(string? gameFolder, List<string> openFiles, bool preview = false)
    {
        this.preview = preview;
        Title = "Dragon Ball: Raging Blast 2 Mod Manager";
        FontFamily = InterFont;
        Background = new SolidColorBrush(Color.FromArgb(255, 9, 4, 7));
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Raging Blast 2 Mod Manager/Assets/AppIcon.ico")));
        Width = 1180;
        Height = 760;
        MinWidth = 960;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        game = gameFolder is not null ? new GameInstall(gameFolder) : GameInstall.Find(AppContext.BaseDirectory);
        if (game is { Valid: false }) game = null;
        Content = BuildShell();
        Refresh();

        if (preview) return;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckRunning());
        Opened += (_, _) =>
        {
            timer.Start();
            CheckRunning();
            if (openFiles.Count > 0) _ = InstallAny(openFiles);
            _ = CheckUpdates(manual: false);
            if (Program.OneClickLink is { } link) _ = OneClickInstall(link);
        };
        Closed += (_, _) => timer.Stop();
        KeyDown += OnKeyDown;
        StartOneClick();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DroppedMods(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) => { if (DroppedMods(e) is { Count: > 0 } files) _ = InstallAny(files); });
    }

#pragma warning disable CS0618 // DataObject API: the 11.3 DataTransfer one is not in every 11.x
    private static List<string> DroppedMods(DragEventArgs e) =>
        (e.Data.GetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(p => p.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase) || GameBananaClient.Archives.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())).ToList();
#pragma warning restore CS0618

    // ---------------------------------------------------------------- layout

    private Control BuildShell()
    {
        var shell = new Grid { Background = Background };
        TextElement.SetForeground(shell, Brushes.White);

        var art = new Panel { ClipToBounds = true, IsHitTestVisible = false };
        art.Children.Add(new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://Raging Blast 2 Mod Manager/Assets/LauncherBackground.png"))),
            Stretch = Stretch.UniformToFill,
            Opacity = .38,
        });
        art.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(235, 9, 4, 7), 0),
                    new GradientStop(Color.FromArgb(200, 9, 4, 7), .55),
                    new GradientStop(Color.FromArgb(150, 9, 4, 7), 1),
                },
            },
        });
        shell.Children.Add(art);

        var content = new Grid { Margin = new Thickness(28, 18, 28, 14) };
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.Children.Add(BuildHeader());
        var tabs = BuildTabs();
        Grid.SetRow(tabs, 1);
        content.Children.Add(tabs);
        installedPage = BuildInstalledPage();
        idsPage = BuildIdsPage();
        noGamePage = BuildNoGamePage();
        pageHost.Children.Add(installedPage);
        pageHost.Children.Add(idsPage);
        browsePage = BuildBrowsePage();
        pageHost.Children.Add(browsePage);
        pageHost.Children.Add(noGamePage);
        Grid.SetRow(pageHost, 2);
        content.Children.Add(pageHost);
        statusText.Margin = new Thickness(2, 10, 0, 0);
        Grid.SetRow(statusText, 3);
        content.Children.Add(statusText);
        shell.Children.Add(content);

        overlay.Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        shell.Children.Add(overlay);
        return shell;
    }

    private Control BuildHeader()
    {
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var words = new StackPanel { Spacing = 1 };
        words.Children.Add(new TextBlock { Text = "DRAGON BALL: RAGING BLAST 2", FontSize = 11, LetterSpacing = 1.7, FontWeight = FontWeight.SemiBold, Foreground = AccentHotBrush });
        words.Children.Add(new TextBlock { Text = "Mod Manager", FontSize = 28, FontWeight = FontWeight.SemiBold });
        words.Children.Add(gameText);
        bar.Children.Add(words);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        addButton = PrimaryButton("Add Mod", Icons.Add, (_, _) => _ = AddClicked());
        buttons.Children.Add(addButton);
        buttons.Children.Add(GhostButton("Mods Folder", Icons.Folder, (_, _) => OpenModsFolder()));
        var refresh = GhostButton("", Icons.Sync, (_, _) => { Refresh(); SetStatus("Read the mods again.", MutedBrush); });
        refresh.MinWidth = 42;
        ToolTip.SetTip(refresh, "Read the mods again (F5)");
        buttons.Children.Add(refresh);
        playButton = PrimaryButton("Play", Icons.Play, (_, _) => _ = PlayClicked());
        playButton.Margin = new Thickness(10, 0, 0, 0);
        ToolTip.SetTip(playButton, "Start Raging Blast 2 with the mods turned on");
        buttons.Children.Add(playButton);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        return bar;
    }

    private Control BuildTabs()
    {
        var row = new Grid { Margin = new Thickness(0, 16, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        installedTab = TabButton("Installed", Icons.Puzzle, "installed");
        idsTab = TabButton("Character IDs", Icons.Hash, "ids");
        tabs.Children.Add(installedTab);
        tabs.Children.Add(idsTab);
        browseTab = TabButton("Browse Mods", Icons.Download, "browse");
        tabs.Children.Add(browseTab);
        row.Children.Add(tabs);

        var banner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        banner.Children.Add(IconPath(Icons.Warning, 16, WarningBrush));
        banner.Children.Add(new TextBlock { Text = "The game is running: close it to change mods.", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        runningBanner.Child = banner;
        runningBanner.Padding = new Thickness(12, 7);
        runningBanner.CornerRadius = new CornerRadius(8);
        runningBanner.Background = new SolidColorBrush(Color.FromArgb(150, 70, 44, 10));
        runningBanner.HorizontalAlignment = HorizontalAlignment.Right;
        runningBanner.VerticalAlignment = VerticalAlignment.Center;
        runningBanner.IsVisible = false;
        Grid.SetColumn(runningBanner, 1);
        row.Children.Add(runningBanner);
        return row;
    }

    private Button TabButton(string text, Geometry icon, string target)
    {
        var button = new Button
        {
            Content = ButtonContent(icon, text, 15),
            Height = 38,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            FontSize = 14,
        };
        button.Click += (_, _) => ShowPage(target);
        return button;
    }

    private Control BuildInstalledPage()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        // the detail panel grows with the window: 30 % of it, 360 (the smallest window) to 460 px
        var panel = new ColumnDefinition(new GridLength(360));
        grid.ColumnDefinitions.Add(panel);
        grid.SizeChanged += (_, e) => panel.Width = new GridLength(Math.Clamp(Math.Round(e.NewSize.Width * .3), 360, 460));

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        left.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        left.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        search.Watermark = "Search mods…";
        search.Height = 36;
        search.FontSize = 14;
        search.VerticalContentAlignment = VerticalAlignment.Center;
        search.TextChanged += (_, _) => RebuildInstalled();
        top.Children.Add(search);
        sortBox.ItemsSource = SortChoices.Select(c => $"Sort: {c}").ToList();
        sortBox.SelectedIndex = 0;
        sortBox.Height = 36;
        sortBox.FontSize = 13;
        sortBox.MinWidth = 136;
        sortBox.Margin = new Thickness(10, 0, 0, 0);
        sortBox.VerticalContentAlignment = VerticalAlignment.Center;
        sortBox.SelectionChanged += (_, _) => RebuildInstalled();
        top.ColumnDefinitions.Insert(1, new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(sortBox, 1);
        top.Children.Add(sortBox);
        modCount.FontSize = 13;
        modCount.Opacity = .7;
        modCount.VerticalAlignment = VerticalAlignment.Center;
        modCount.Margin = new Thickness(14, 0, 2, 0);
        Grid.SetColumn(modCount, 2);
        top.Children.Add(modCount);
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var check = BuildUpdatesButton();
        Grid.SetColumn(check, 3);
        top.Children.Add(check);
        left.Children.Add(top);

        var header = ModRowGrid();
        header.Margin = new Thickness(0, 0, 0, 4);
        AddCell(header, 0, Text("ON", 11, MutedBrush, true));
        AddCell(header, 2, Text("NAME", 11, MutedBrush, true));
        AddCell(header, 3, Text("IDS", 11, MutedBrush, true));
        AddCell(header, 4, Text("AUTHOR", 11, MutedBrush, true));
        AddCell(header, 5, Text("VERSION", 11, MutedBrush, true));
        Grid.SetRow(header, 1);
        left.Children.Add(header);

        var list = new Border
        {
            Background = PanelBrush, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(10),
            ClipToBounds = true, Child = new ScrollViewer { Content = modList },
        };
        Grid.SetRow(list, 2);
        left.Children.Add(list);
        grid.Children.Add(left);

        detailHost.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(detailHost, 1);
        grid.Children.Add(detailHost);
        return grid;
    }

    /// <summary>The mod list's columns: on, face, name, IDs, author, version.</summary>
    private static Grid ModRowGrid()
    {
        var row = new Grid();
        foreach (var width in new[] { 46d, 52d, -1d, 118d, 120d, 70d })
            row.ColumnDefinitions.Add(new ColumnDefinition(width < 0 ? GridLength.Star : new GridLength(width)));
        return row;
    }

    private static void AddCell(Grid row, int column, Control cell)
    {
        cell.VerticalAlignment = VerticalAlignment.Center;
        if (cell is TextBlock text) { text.TextTrimming = TextTrimming.CharacterEllipsis; text.TextWrapping = TextWrapping.NoWrap; }
        var host = new Border { Child = cell, Padding = new Thickness(column == 0 ? 14 : 6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(host, column);
        row.Children.Add(host);
    }

    private Control BuildNoGamePage()
    {
        var body = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
        body.Children.Add(IconPath(Icons.Folder, 40, AccentHotBrush));
        body.Children.Add(new TextBlock { Text = "Where is Raging Blast 2?", FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(new TextBlock
        {
            Text = "The Mod Manager goes in the game folder, beside Raging Blast 2.exe. It is not there now: choose the game folder.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = .75,
        });
        var choose = PrimaryButton("Choose the Game Folder", Icons.Folder, (_, _) => _ = ChooseGameClicked());
        choose.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(choose);
        return body;
    }

    // ---------------------------------------------------------------- state

    private void Refresh()
    {
        var path = selected?.Path;
        if (game is null)
        {
            library = null;
            map = [];
            selected = null;
            gameText.Text = "No game folder";
        }
        else
        {
            ReplaceMods.RepairPs3Manifests(game);
            library = ModLibrary.Scan(game);
            // chip pictures left at an ID by another character (installs, ID changes and removals all end here)
            if (!game.GameRunning() && ChipRenders.Tidy(library) is { Count: > 0 } tidied)
                SetStatus($"Character Select pictures tidied: {tidied.Count} old picture{(tidied.Count == 1 ? "" : "s")} moved to ModManager\\Removed\\Chips.", MutedBrush);
            map = CharacterIds.Map(library);
            ReadReplaceClashes();
            selected = library.Mods.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))
                       ?? library.Mods.FirstOrDefault();
            gameText.Text = game.Root;
        }
        RebuildInstalled();
        RebuildIds();
        ShowPage(page);
    }

    private void ShowPage(string target)
    {
        page = target;
        installedPage.IsVisible = game is not null && page == "installed";
        idsPage.IsVisible = game is not null && page == "ids";
        browsePage.IsVisible = game is not null && page == "browse";
        if (browsePage.IsVisible) BrowseShown();
        noGamePage.IsVisible = game is null;
        foreach (var (tab, name) in new[] { (installedTab, "installed"), (idsTab, "ids"), (browseTab, "browse") })
        {
            var on = name == page;
            tab.Background = on ? new SolidColorBrush(Color.FromArgb(230, 52, 22, 24)) : new SolidColorBrush(Color.FromArgb(110, 18, 12, 16));
            tab.Foreground = on ? Brushes.White : MutedBrush;
            tab.BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0);
            tab.BorderBrush = AccentBrush;
            tab.IsEnabled = game is not null;
        }
        addButton.IsEnabled = game is not null;
        playButton.IsEnabled = game is not null && !gameRunning;
    }

    private void CheckRunning()
    {
        var running = game?.GameRunning() ?? false;
        if (running == gameRunning) return;
        gameRunning = running;
        runningBanner.IsVisible = running;
        playButton.IsEnabled = game is not null && !running;
        RebuildInstalled();
        RebuildIds();
    }

    private void SetStatus(string text, IBrush brush)
    {
        statusText.Text = text;
        statusText.Foreground = brush;
    }

    private IEnumerable<CharacterSlot> SlotsOf(InstalledMod mod) => map.Where(s => s.Owner == mod);

    private Bitmap? Preview(InstalledMod mod)
    {
        if (mod.Info?.Preview is not { } bytes) return null;
        var key = $"{mod.Path}|{bytes.Length}|{(File.Exists(mod.Path) ? File.GetLastWriteTimeUtc(mod.Path).Ticks : 0)}";
        if (previews.TryGetValue(key, out var cached)) return cached;
        Bitmap? bitmap = null;
        try { bitmap = new Bitmap(new MemoryStream(bytes)); }
        catch (Exception) { /* a broken picture: the mod shows without one */ }
        return previews[key] = bitmap;
    }

    // ---------------------------------------------------------------- installed

    private void RebuildInstalled()
    {
        rebuilding = true;
        modList.Children.Clear();
        if (library is not null)
        {
            var mods = library.Mods;
            var packages = mods.Where(m => m.Source == ModSource.Package).ToList();
            long bytes = 0;
            foreach (var mod in packages)
                try { bytes += new FileInfo(mod.Path).Length; } catch (IOException) { }
            var replacements = mods.Count(m => m.Source == ModSource.Replace);
            modCount.Text = $"{packages.Count} mod{(packages.Count == 1 ? "" : "s")}{(replacements > 0 ? $" + {replacements} replacement{(replacements == 1 ? "" : "s")}" : "")}  ·  {mods.Count(m => m.Enabled && m.Source is ModSource.Package or ModSource.Replace)} on  ·  {Size(bytes)}";
            if (mods.Count == 0)
            {
                var empty = new StackPanel { Spacing = 10, Margin = new Thickness(0, 70, 0, 70), HorizontalAlignment = HorizontalAlignment.Center };
                empty.Children.Add(IconPath(Icons.Puzzle, 40, AccentHotBrush));
                empty.Children.Add(new TextBlock { Text = "No mods yet", FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
                empty.Children.Add(new TextBlock { Text = "Add a .rb2mod file, or drop one on this window.", Opacity = .7, HorizontalAlignment = HorizontalAlignment.Center });
                modList.Children.Add(empty);
            }
            var query = search.Text?.Trim() ?? "";
            bool Matches(InstalledMod mod) => query.Length == 0
                || mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(mod.Path).Contains(query, StringComparison.OrdinalIgnoreCase)
                || (mod.Info?.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || mod.Packages.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Forms.Any(f => f.Id.ToString() == query));
            var index = 0;
            void Section(string? title, IEnumerable<InstalledMod> list)
            {
                var items = Sorted(list.Where(Matches)).ToList();
                if (items.Count == 0) return;
                if (title is not null)
                    modList.Children.Add(new Border
                    {
                        Padding = new Thickness(14, 12, 14, 6),
                        Child = new TextBlock { Text = $"{title}  ·  {items.Count}", FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = AccentHotBrush },
                    });
                foreach (var mod in items) modList.Children.Add(ModRow(mod, index++));
            }
            Section(null, packages);
            Section("REPLACEMENTS", mods.Where(m => m.Source == ModSource.Replace));
            Section("ENGINE WORKING FOLDERS", mods.Where(m => m.Source == ModSource.WorkingFolder));
            if (query.Length > 0 && index == 0)
                modList.Children.Add(new TextBlock { Text = $"No mod matches \"{query}\".", Opacity = .6, Margin = new Thickness(14, 16) });
        }
        detailHost.Child = selected is null ? null : Detail(selected);
        rebuilding = false;
    }

    /// <summary>The list in the Sort by order; ties by ID, then name.</summary>
    private IEnumerable<InstalledMod> Sorted(IEnumerable<InstalledMod> mods)
    {
        static int FirstId(InstalledMod mod) =>
            mod.Packages.SelectMany(p => p.Forms).Select(f => f.Id).DefaultIfEmpty(int.MaxValue).Min();
        var byId = mods.OrderBy(FirstId).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
        return SortChoices[Math.Clamp(sortBox.SelectedIndex, 0, SortChoices.Length - 1)] switch
        {
            "Name" => mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(FirstId),
            "Enabled first" => byId.OrderByDescending(m => m.Enabled),
            "Disabled first" => byId.OrderBy(m => m.Enabled),
            "Author" => mods.OrderBy(m => m.Info?.Author is { Length: > 0 } a ? a : "￿", StringComparer.OrdinalIgnoreCase)
                .ThenBy(FirstId).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            _ => byId,
        };
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    private Control ModRow(InstalledMod mod, int index)
    {
        var row = ModRowGrid();
        row.MinHeight = 50;
        if (mod.Source is ModSource.Package or ModSource.Replace)
        {
            var check = new CheckBox { IsChecked = mod.Enabled, IsEnabled = !gameRunning, MinWidth = 0, Padding = new Thickness(0) };
            ToolTip.SetTip(check, mod.Enabled ? "On: the game loads it" : "Off: the game doesn't load it");
            check.IsCheckedChanged += (_, _) => { if (!rebuilding) _ = SetEnabledClicked(mod, check.IsChecked == true); };
            AddCell(row, 0, check);
        }
        else
        {
            var always = IconPath(Icons.Folder, 15, MutedBrush);
            ToolTip.SetTip(always, "Engine working folder: the game always reads it");
            AddCell(row, 0, always);
        }
        AddCell(row, 1, Face(mod, 40));

        var name = new StackPanel { Spacing = 1, Opacity = mod.Enabled ? 1 : .5 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = mod.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var worst = SlotsOf(mod).Select(s => s.Worst).Max();
        if (UpdateFor(mod) is not null) title.Children.Add(Badge("Update", AccentHotBrush));
        if (mod.Error is not null) title.Children.Add(Badge("Broken", ErrorBrush));
        else if (ClashesOf(mod).Count > 0) title.Children.Add(Badge("Same files", ErrorBrush));
        else if (mod.Enabled && worst == IssueLevel.Error) title.Children.Add(Badge("ID clash", ErrorBrush));
        else if (mod.Enabled && worst == IssueLevel.Warning) title.Children.Add(Badge("Check IDs", WarningBrush));
        name.Children.Add(title);
        name.Children.Add(new TextBlock
        {
            Text = mod.Error ?? (mod.Replacement is { } replaced ? (replaced.Keys.Count > 0 ? "Replaces " + ReplaceMods.Describe(replaced.Keys) : "No model file the game loads") : System.IO.Path.GetFileName(mod.Path)), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = mod.Error is null ? MutedBrush : ErrorBrush,
        });
        AddCell(row, 2, name);
        var ids = string.Join("  ", mod.Packages.Select(p => p.Kind switch
        {
            "character" => p.RetailEdit ? $"edits {p.Id}" : CharacterIds.Span(p),
            "maps" => "stages",
            "replace" => "—",
            _ => "game files",
        }));
        AddCell(row, 3, Text(ids, 13, worst == IssueLevel.Error && mod.Enabled ? ErrorBrush : null));
        AddCell(row, 4, Text(mod.Info?.Author is { Length: > 0 } author ? author : "—", 13, MutedBrush));
        AddCell(row, 5, Text(mod.Info?.Version is { Length: > 0 } version ? version : "—", 13, MutedBrush));

        var isSelected = mod == selected;
        var host = new Border
        {
            Child = row,
            Padding = new Thickness(0, 4),
            Background = isSelected ? new SolidColorBrush(Color.FromArgb(100, 241, 84, 50)) : RowBrush(index),
            BorderBrush = isSelected ? AccentHotBrush : Brushes.Transparent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (!isSelected)
        {
            var rest = host.Background;
            host.PointerEntered += (_, _) => host.Background = new SolidColorBrush(Color.FromArgb(70, 255, 129, 90));
            host.PointerExited += (_, _) => host.Background = rest;
        }
        host.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>(true) is not null) return;
            // a right click opens the row's menu: rebuilding the list now would take the menu's row away
            if (e.GetCurrentPoint(host).Properties.IsRightButtonPressed) return;
            selected = mod;
            RebuildInstalled();
        };
        host.ContextMenu = RowMenu(mod);
        return host;
    }

    /// <summary>The right-click menu of a mod in the list.</summary>
    private ContextMenu RowMenu(InstalledMod mod)
    {
        var items = new List<Control>();
        MenuItem Item(string header, Geometry icon, Action click, bool enabled = true, bool danger = false)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = IconPath(icon, 14, danger ? ErrorBrush : MutedBrush),
                IsEnabled = enabled,
            };
            if (danger) item.Foreground = ErrorBrush;
            item.Click += (_, _) => click();
            return item;
        }
        var editable = mod.Source is ModSource.Package or ModSource.Replace;
        if (editable)
            items.Add(Item(mod.Enabled ? "Turn Off" : "Turn On", mod.Enabled ? Icons.Close : Icons.Check,
                () => _ = SetEnabledClicked(mod, !mod.Enabled), !gameRunning));
        items.Add(Item("Show in Folder", Icons.Folder, () => ShowInFolder(mod.Path)));
        if (editable)
        {
            items.Add(new Separator());
            items.Add(Item("Remove (keep a copy)", Icons.Trash, () => _ = RemoveClicked(mod), !gameRunning));
            items.Add(Item("Delete", Icons.Trash, () => _ = DeleteClicked(mod), !gameRunning, danger: true));
        }
        return new ContextMenu { ItemsSource = items };
    }

    private async Task DeleteClicked(InstalledMod mod)
    {
        if (game is null) return;
        var body = new TextBlock
        {
            Text = $"{mod.Name} is deleted from the game and goes to the Recycle Bin.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 560,
        };
        if (!await ShowDialog("Delete Mod", body, "Delete", Icons.Trash, danger: true)) return;
        var game0 = game;
        var done = await RunBusy("Deleting…", () =>
        {
            ModActions.Delete(game0, mod);
            return "deleted";
        });
        if (selected == mod) selected = null;
        Refresh();
        if (done is not null) SetStatus($"{mod.Name} deleted (it is in the Recycle Bin).", MutedBrush);
    }

    private static Border Badge(string text, IBrush brush) => new()
    {
        CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1), BorderBrush = brush, BorderThickness = new Thickness(.8),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = brush },
    };

    private Control Detail(InstalledMod mod)
    {
        var body = new StackPanel { Spacing = 10 };
        var picture = Picture(mod, 328, 340);
        picture.Width = double.NaN;  // as wide as the panel (it follows the window)
        body.Children.Add(picture);
        body.Children.Add(new TextBlock { Text = mod.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (UpdateFor(mod) is { } update) body.Children.Add(UpdateBanner(update));
        if (mod.Info is { } info)
        {
            var by = new List<string>();
            if (info.Author.Length > 0) by.Add($"by {info.Author}");
            if (info.Version.Length > 0) by.Add($"version {info.Version}");
            if (by.Count > 0) body.Children.Add(new TextBlock { Text = string.Join("  ·  ", by), FontSize = 13, Opacity = .65, Margin = new Thickness(0, -6, 0, 0) });
            if (info.Description.Length > 0) body.Children.Add(new TextBlock { Text = info.Description, FontSize = 13, Opacity = .82, TextWrapping = TextWrapping.Wrap });
        }
        if (mod.Error is not null)
            body.Children.Add(new TextBlock { Text = $"This file can't be used: {mod.Error}", Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap });

        if (mod.Source == ModSource.Replace) body.Children.Add(ReplaceDetail(mod));
        foreach (var package in mod.Packages.Where(p => p.Kind != "replace"))
        {
            var box = new StackPanel { Spacing = 5 };
            if (package.Kind == "character")
            {
                var title = new Grid();
                title.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                title.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var names = new StackPanel { Spacing = 1 };
                names.Children.Add(new TextBlock { Text = package.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                names.Children.Add(Text(package.RetailEdit
                    ? $"Changes the game's character {package.Id}"
                    : $"{(package.Forms.Count == 1 ? "ID" : "IDs")} {CharacterIds.Span(package)}  ·  {package.Forms.Count} form{(package.Forms.Count == 1 ? "" : "s")}", 12, MutedBrush));
                title.Children.Add(names);
                if (mod.Source == ModSource.Package && !package.RetailEdit)
                {
                    var change = SmallButton("Change ID", Icons.Edit, (_, _) => _ = ChangeIdClicked(mod, package));
                    change.IsEnabled = !gameRunning;
                    Grid.SetColumn(change, 1);
                    title.Children.Add(change);
                }
                box.Children.Add(title);
                if (package.Forms.Count > 1)
                {
                    var forms = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
                    foreach (var form in package.Forms)
                        forms.Children.Add(Text($"{form.Id}   {(form.Name.Length > 0 ? form.Name : "—")}{(form.Destinations.Count > 0 ? $"   → {string.Join(", ", form.Destinations)}" : "")}", 12, MutedBrush));
                    box.Children.Add(forms);
                }
                if (mod.Enabled)
                    foreach (var issue in map.Where(s => s.Owner == mod && s.Package == package).SelectMany(s => s.Issues))
                        box.Children.Add(IssueLine(issue));
            }
            else
            {
                box.Children.Add(new TextBlock { Text = package.Kind == "maps" ? "Stages" : "Game files", FontSize = 15, FontWeight = FontWeight.SemiBold });
                box.Children.Add(Text(package.Kind == "maps" ? "New stages for Stage Select." : "Replaces some of the game's files.", 12, MutedBrush));
            }
            body.Children.Add(new Border { Child = box, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) });
        }
        if (mod.Source == ModSource.WorkingFolder)
            body.Children.Add(new TextBlock { Text = "A working folder of the Raging Blast Engine: the game reads it as it is. Change it (and its ID) in the Engine.", FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(SmallButton("Show in Folder", Icons.Folder, (_, _) => ShowInFolder(mod.Path)));
        if (mod.Source is ModSource.Package or ModSource.Replace)
        {
            var remove = SmallButton("Remove", Icons.Trash, (_, _) => _ = RemoveClicked(mod));
            remove.IsEnabled = !gameRunning;
            actions.Children.Add(remove);
        }
        body.Children.Add(actions);

        return new Border
        {
            Background = PanelBrush, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(12),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(16), Child = body } },
        };
    }

    private static IBrush PictureBackground() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(255, 58, 20, 22), 0), new GradientStop(Color.FromArgb(255, 20, 9, 13), 1) },
    };

    private Control PictureIcon(InstalledMod mod, double size) =>
        IconPath(mod.Error is not null ? Icons.Warning : mod.Source == ModSource.WorkingFolder ? Icons.Folder : Icons.Puzzle, size,
            mod.Error is not null ? ErrorBrush : new SolidColorBrush(Color.FromArgb(150, 255, 145, 91)));

    /// <summary>The whole preview (a full-body character picture): nothing cut off.</summary>
    private Control Picture(InstalledMod mod, double width, double height)
    {
        var frame = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = PictureBackground() };
        frame.Child = Preview(mod) is { } bitmap
            ? new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(6) }
            : PictureIcon(mod, Math.Min(width, height) * .32);
        if (frame.Child is Path icon) icon.HorizontalAlignment = HorizontalAlignment.Center;
        return frame;
    }

    /// <summary>A small square on the character's face, cut from the preview (see <see cref="FaceCrop"/>).</summary>
    private Control Face(InstalledMod mod, double size)
    {
        var frame = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = PictureBackground() };
        frame.Child = FaceOf(mod) is { } face
            ? new Image { Source = face, Stretch = Stretch.UniformToFill }
            : PictureIcon(mod, size * .5);
        if (frame.Child is Path icon) icon.HorizontalAlignment = HorizontalAlignment.Center;
        return frame;
    }

    private IImage? FaceOf(InstalledMod mod)
    {
        if (mod.Info?.Preview is not { } bytes) return null;
        var key = $"{mod.Path}|{bytes.Length}|{(File.Exists(mod.Path) ? File.GetLastWriteTimeUtc(mod.Path).Ticks : 0)}";
        if (faces.TryGetValue(key, out var cached)) return cached;
        IImage? face = null;
        try { face = FaceCrop(bytes); }
        catch (Exception) { /* a broken picture: the icon shows */ }
        return faces[key] = face;
    }

    /// <summary>
    /// Mod pictures are full-body characters on a transparent background (the Engine's 1024² previews): the face is the
    /// top of the figure. The square starts at the figure's top row, about a quarter of its height, centred on the
    /// figure's pixels in those rows. A picture without transparency is shown whole.
    /// </summary>
    private static IImage? FaceCrop(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var small = Bitmap.DecodeToWidth(stream, 256);
        var (w, h) = (small.PixelSize.Width, small.PixelSize.Height);
        if (small.Format is { } format && format.BitsPerPixel != 32) return small;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { small.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { handle.Free(); }
        bool Solid(int x, int y) => pixels[y * stride + x * 4 + 3] > 40;
        int top = -1, bottom = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x += 2)
                if (Solid(x, y)) { if (top < 0) top = y; bottom = y; break; }
        if (top < 0) return small;
        var allSolid = Solid(0, 0) && Solid(w - 1, 0) && Solid(0, h - 1) && Solid(w - 1, h - 1);
        if (allSolid) return small;
        var side = Math.Clamp((int)((bottom - top + 1) * .26), 24, Math.Min(w, h));
        long sum = 0, count = 0;
        for (var y = top; y < Math.Min(h, top + side); y++)
            for (var x = 0; x < w; x++)
                if (Solid(x, y)) { sum += x; count++; }
        var centre = count > 0 ? (int)(sum / count) : w / 2;
        var x0 = Math.Clamp(centre - side / 2, 0, w - side);
        var y0 = Math.Clamp(top - side / 12, 0, h - side);
        return new CroppedBitmap(small, new PixelRect(x0, y0, side, side));
    }

    // ---------------------------------------------------------------- character IDs

    private static IBrush RowBrush(int index) => index % 2 == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));

    private static Control IssueLine(IdIssue issue)
    {
        var brush = issue.Level switch { IssueLevel.Error => ErrorBrush, IssueLevel.Warning => WarningBrush, _ => MutedBrush };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(20)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var icon = IconPath(issue.Level == IssueLevel.Note ? Icons.Info : Icons.Warning, 13, brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        row.Children.Add(icon);
        var text = new TextBlock { Text = issue.Text, FontSize = 12, Foreground = brush, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    // ---------------------------------------------------------------- actions

    private async Task AddClicked()
    {
        if (game is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a Raging Blast 2 mod",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Raging Blast 2 mod") { Patterns = ["*.rb2mod", "*.zip", "*.7z", "*.rar"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await InstallAny(paths);
    }

    /// <summary>Offers each .rb2mod for install (one dialog each); <paramref name="source"/> notes where a download came from. Returns how many went in.</summary>
    private async Task<int> InstallFiles(IReadOnlyList<string> files, (GbMod Mod, GbFile File)? source = null,
        IReadOnlyCollection<InstalledMod>? replacing = null, bool ask = true)
    {
        if (game is null || library is null) { SetStatus("Choose the game folder first.", WarningBrush); return 0; }
        var count = 0;
        foreach (var file in files)
        {
            ModInfo info;
            try { info = Rb2ModFile.Read(file); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                await ShowMessage("Not installed", $"{System.IO.Path.GetFileName(file)} is not a mod the game can use.\n\n{e.Message}");
                continue;
            }
            var steps = ModActions.Plan(library, info, replacing);
            var body = new StackPanel { Spacing = 10 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            head.Children.Add(Picture(new InstalledMod { Path = file, Source = ModSource.Package, Enabled = true, Info = info }, 130, 130));
            var words = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = info.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (info.Author.Length > 0 || info.Version.Length > 0)
                words.Children.Add(Text(string.Join("  ·  ", new[] { info.Author.Length > 0 ? $"by {info.Author}" : "", info.Version.Length > 0 ? $"version {info.Version}" : "" }.Where(s => s.Length > 0)), 13, MutedBrush));
            if (info.Description.Length > 0)
                words.Children.Add(new TextBlock { Text = info.Description.Length > 240 ? info.Description[..240] + "…" : info.Description, FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(words, 1);
            head.Children.Add(words);
            body.Children.Add(head);
            foreach (var step in steps)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                line.Children.Add(IconPath(step.Moves ? Icons.Hash : Icons.Check, 14, step.Moves ? AccentHotBrush : SuccessBrush));
                line.Children.Add(new TextBlock { Text = $"{step.Package.Name}: {step.Note}", FontSize = 13, Foreground = step.Moves ? AccentHotBrush : Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
                body.Children.Add(line);
            }
            foreach (var package in info.Packages.Where(p => p.Kind != "character"))
                body.Children.Add(Text(package.Kind == "maps" ? "Adds stages." : "Replaces some of the game's files.", 13, null));
            if (library.Mods.Any(m => m.Source == ModSource.Package && string.Equals(System.IO.Path.GetFileName(m.Path), info.FileName, StringComparison.OrdinalIgnoreCase)))
                body.Children.Add(Text($"Replaces the installed {info.FileName} (the old one is kept in Mods\\Removed).", 12, MutedBrush));
            if (ask && !await ShowDialog("Install Mod", body, "Install", Icons.Download)) continue;
            var installedPath = "";
            var lines = await RunBusy($"Installing {info.Name}…", () =>
            {
                var result = ModActions.Install(library, file, steps, out var path);
                installedPath = path;
                return result;
            });
            if (lines is null) continue;
            count++;
            if (source is { } from && installedPath.Length > 0)
                try { ModDownloads.Record(game, System.IO.Path.GetFileName(installedPath), info.Name, from.Mod, from.File); }
                catch (IOException) { /* only the "Installed" mark is lost */ }
            // an update under another file name: the older file goes (kept in Mods\Removed)
            foreach (var old in replacing ?? [])
                if (!string.Equals(old.Path, installedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(old.Path))
                    try { ModActions.Remove(game, old); }
                    catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { SetStatus($"Couldn't take out the older {System.IO.Path.GetFileName(old.Path)}: {e.Message}", WarningBrush); }
            Refresh();
            selected = library.Mods.FirstOrDefault(m => m.Source == ModSource.Package && m.Enabled && m.Name == info.Name) ?? selected;
            RebuildInstalled();
            SetStatus(string.Join("  ", lines), SuccessBrush);
        }
        return count;
    }

    /// <summary>Files picked, dropped or opened with the program: .rb2mod files, and archives (.rb2mod inside, or a replacement mod).</summary>
    private async Task InstallAny(IReadOnlyList<string> paths)
    {
        var packages = paths.Where(p => p.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (packages.Count > 0) await InstallFiles(packages);
        foreach (var archive in paths.Except(packages))
            await InstallArchive(archive, ReplaceMods.FolderName(System.IO.Path.GetFileNameWithoutExtension(archive)));
    }

    private async Task SetEnabledClicked(InstalledMod mod, bool enabled)
    {
        if (game is null) return;
        var game0 = game;
        var path = await RunBusy(enabled ? "Turning on…" : "Turning off…", () =>
        {
            if (mod.Replacement is null) return ModActions.SetEnabled(game0, mod, enabled);
            if (game0.GameRunning()) throw new InvalidOperationException("Close the game first: it is using the mods.");
            return ReplaceMods.SetEnabled(game0, mod.Replacement, enabled);
        });
        Refresh();
        if (path is null) return;
        selected = library?.Mods.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)) ?? selected;
        RebuildInstalled();
        SetStatus(enabled ? $"{mod.Name} is on." + (selected is { Replacement: not null } on && ClashesOf(on).Count > 0 ? $" It shares files with {string.Join(", ", ClashesOf(on).Select(c => c.Other.Name))}: the game loads neither copy." : "")
            : $"{mod.Name} is off: the game doesn't load it (it waits in {(mod.Replacement is null ? "Mods\\Disabled" : "ModManager\\Disabled")}).", MutedBrush);
    }

    private async Task RemoveClicked(InstalledMod mod)
    {
        if (game is null) return;
        var body = new TextBlock
        {
            Text = $"{mod.Name} is taken out of the game. It is kept in {(mod.Replacement is null ? "Mods\\Removed" : "Modloader\\ModManager\\Removed")}: delete it there when you are sure.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 560,
        };
        if (!await ShowDialog("Remove Mod", body, "Remove", Icons.Trash, danger: true)) return;
        var game0 = game;
        var path = await RunBusy("Removing…", () =>
        {
            if (mod.Replacement is null) return ModActions.Remove(game0, mod);
            if (game0.GameRunning()) throw new InvalidOperationException("Close the game first: it is using the mods.");
            return ReplaceMods.Remove(game0, mod.Replacement);
        });
        if (selected == mod) selected = null;
        Refresh();
        if (path is not null) SetStatus($"{mod.Name} removed (kept as {System.IO.Path.GetFileName(path)} in {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path))}).", MutedBrush);
    }

    private async Task ChangeIdClicked(InstalledMod mod, PackageInfo package)
    {
        if (game is null || library is null) return;
        var used = CharacterIds.Used(library, mod, package.Folder);
        var forms = package.Forms.Count;
        // the first free place other than where it is now
        var suggestion = CharacterIds.FreeStart([.. used, .. package.Forms.Select(f => f.Id)], package);
        var box = new TextBox { Text = suggestion.ToString(), Width = 110, FontSize = 16, MaxLength = 4 };
        var check = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        var confirm = DialogButton("Change ID", Icons.Edit, primary: true);
        int? Parsed() => int.TryParse(box.Text?.Trim(), out var v) ? v : null;
        void Validate()
        {
            var start = Parsed();
            string text;
            IBrush brush;
            var ok = false;
            var ids = start is null ? new List<int>() : CharacterIds.Shifted(package, start.Value);
            var named = start is null ? "" : forms == 1 ? $"ID {start} is" : $"IDs {Moved(package, start.Value)} are";
            if (start is null) (text, brush) = ("Type a number.", WarningBrush);
            else if (start == package.Id) (text, brush) = ("That is its ID now.", MutedBrush);
            else if (ids.Min() < CharacterIds.FirstAddOn) (text, brush) = ("0..100 are Raging Blast 2's own characters: use 101 or more.", ErrorBrush);
            else if (ids.FirstOrDefault(used.Contains) is var taken && taken != 0)
            {
                var who = map.FirstOrDefault(s => s.Package.Forms.Any(f => f.Id == taken) && !(s.Owner == mod && s.Package == package));
                (text, brush) = ($"ID {taken} is taken{(who is null ? "" : $" by {who.Package.Name}")}.", ErrorBrush);
            }
            else
            {
                ok = true;
                (text, brush) = ids.Max() > CharacterIds.LastByte
                    ? ($"{named} free, but past 255: no other character can transform into it.", WarningBrush)
                    : ($"{named} free.", SuccessBrush);
            }
            check.Text = text;
            check.Foreground = brush;
            confirm.IsEnabled = ok;
        }
        box.TextChanged += (_, _) => Validate();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = $"{package.Name} uses {(forms == 1 ? $"ID {package.Id}" : $"IDs {CharacterIds.Span(package)} ({forms} forms)")}. Pick its new first ID:",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 560,
        });
        var input = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        input.Children.Add(box);
        var suggest = SmallButton($"First free: {suggestion}", Icons.Check, (_, _) => box.Text = suggestion.ToString());
        input.Children.Add(suggest);
        body.Children.Add(input);
        body.Children.Add(check);
        body.Children.Add(new TextBlock
        {
            Text = "Its forms, Character Select pictures and transformations move with it. The old file is kept in Mods\\Removed.",
            FontSize = 12, Opacity = .6, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
        });
        Validate();
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
        if (!await ShowDialog("Change Character ID", body, confirm) || Parsed() is not { } to) return;
        var path = await RunBusy($"Moving {package.Name} to {to}…", () => ModActions.ChangeId(game, mod.Path, package.Folder, to));
        Refresh();
        if (path is null) return;
        selected = library?.Mods.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)) ?? selected;
        RebuildInstalled();
        SetStatus($"{package.Name} now uses {(forms == 1 ? $"ID {to}" : $"IDs {Moved(package, to)}")}.", SuccessBrush);
    }

    /// <summary>The form IDs of <paramref name="package"/> once at <paramref name="start"/>, as "101–102, 105".</summary>
    private static string Moved(PackageInfo package, int start) =>
        CharacterIds.Span(package with { Id = start, Forms = package.Forms.Select(f => f with { Id = f.Id - package.Id + start }).ToList() });

    private async Task ChooseGameClicked()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The Raging Blast 2 folder" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var found = new GameInstall(path);
        if (!found.Valid)
        {
            await ShowMessage("Not the game folder", $"{path} has no Raging Blast 2.exe and no Modloader folder.");
            return;
        }
        game = found;
        Refresh();
    }

    /// <summary>Starts the game of this folder like the Launcher's Play; the Mod Manager stays open (mods lock while it runs).</summary>
    private async Task PlayClicked()
    {
        if (game is null || gameRunning) return;
        var exe = System.IO.Path.Combine(game.Root, GameInstall.GameExe);
        if (!File.Exists(exe))
        {
            await ShowMessage("Game not found", $"{GameInstall.GameExe} is not in {game.Root}.");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = game.Root, UseShellExecute = false })?.Dispose();
            SetStatus("Starting Raging Blast 2…", MutedBrush);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            await ShowMessage("Unable to start the game", e.Message);
        }
    }

    private void OpenModsFolder()
    {
        if (game is null) return;
        Directory.CreateDirectory(game.Mods);
        Open("explorer.exe", $"\"{game.Mods}\"");
    }

    private static void ShowInFolder(string path) =>
        Open("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"");

    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void Open(string program, string arguments)
    {
        try { Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Runs a file job off the window, with a busy card; a refusal or a file error becomes a message.</summary>
    private async Task<T?> RunBusy<T>(string text, Func<T> work) where T : class
    {
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(255, 25, 9, 14)), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 241, 84, 50)),
            BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(10), Padding = new Thickness(28, 22), MinWidth = 360,
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold },
                    new ProgressBar { IsIndeterminate = true, Height = 5, Foreground = AccentBrush, Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) },
                },
            },
        };
        overlay.Children.Clear();
        overlay.Children.Add(card);
        overlay.IsVisible = true;
        string? failure = null;
        T? result = null;
        try { result = await Task.Run(work); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            failure = e.Message;
        }
        overlay.IsVisible = false;
        overlay.Children.Clear();
        if (failure is not null) await ShowMessage("Could not finish", failure);
        return result;
    }

    // ---------------------------------------------------------------- dialogs

    private Task ShowMessage(string title, string message) =>
        ShowDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 560 }, null);

    private Task<bool> ShowDialog(string title, Control body, string confirm, Geometry icon, bool danger = false) =>
        ShowDialog(title, body, DialogButton(confirm, icon, primary: true, danger: danger));

    /// <summary>A dialog over the window: true when <paramref name="confirm"/> was pressed (without one, just Close).</summary>
    private Task<bool> ShowDialog(string title, Control body, Button? confirm)
    {
        dialogResult?.TrySetResult(false);
        var result = new TaskCompletionSource<bool>();
        dialogResult = result;
        var cancel = DialogButton(confirm is null ? "Close" : "Cancel", null, primary: false);
        cancel.Click += (_, _) => CloseDialog(false);
        if (confirm is not null) confirm.Click += (_, _) => CloseDialog(true);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(cancel);
        if (confirm is not null) footer.Children.Add(confirm);
        var layout = new StackPanel();
        layout.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(24, 20, 24, 10) });
        layout.Children.Add(new Border { Padding = new Thickness(24, 4, 24, 22), Child = body });
        layout.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 34, 13, 19)), Padding = new Thickness(24, 16),
            CornerRadius = new CornerRadius(0, 0, 10, 10), Child = footer,
        });
        var dialog = new Border
        {
            MinWidth = 560, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(255, 25, 9, 14)), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 241, 84, 50)),
            BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(10), Child = layout,
        };
        overlay.Children.Clear();
        overlay.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = dialog });
        overlay.IsVisible = true;
        if (body.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault() is null)
            Dispatcher.UIThread.Post(() => (confirm ?? cancel).Focus(NavigationMethod.Directional));
        return result.Task;
    }

    private void CloseDialog(bool confirmed)
    {
        overlay.IsVisible = false;
        overlay.Children.Clear();
        var result = dialogResult;
        dialogResult = null;
        result?.TrySetResult(confirmed);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (dialogResult is not null)
        {
            if (e.Key == Key.Escape) { CloseDialog(false); e.Handled = true; }
            return;
        }
        if (overlay.IsVisible) return;
        if (e.Key == Key.F5) { Refresh(); e.Handled = true; }
        else if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = AddClicked(); e.Handled = true; }
    }

    // ---------------------------------------------------------------- preview (--render-preview)

    public Task PrepareForPreview(string target)
    {
        switch (target)
        {
            case "ids":
                ShowPage("ids");
                break;
            case "browse":
                ShowPage("browse");
                return Task.Delay(9000);
            case "open":
                if (Program.OpenFiles.Count > 0) _ = InstallAny(Program.OpenFiles);
                return Task.Delay(4000);
            case "replace":
                selected = library?.Mods.FirstOrDefault(m => m.Source == ModSource.Replace && ClashesOf(m).Count > 0) ?? selected;
                RebuildInstalled();
                break;
            case "updates":
                selected = library?.Mods.FirstOrDefault(m => m.Name.StartsWith("Vegito")) ?? selected;
                return CheckUpdates(manual: true);
            case "oneclick" when Program.OneClickLink is { } click:
                _ = OneClickInstall(click);
                return Task.Delay(9000);
            case "browse-all":
                browseOther.IsChecked = true;
                ShowPage("browse");
                return Task.Delay(9000);
            case "organize":
                ShowPage("ids");
                _ = OrganizeClicked();
                break;
            case "install" when library?.Mods.FirstOrDefault(m => m.Source == ModSource.Package && m.Error is null) is { } mod:
                _ = InstallFiles([mod.Path]);
                break;
            case "change" when library?.Mods.FirstOrDefault(m => m.Source == ModSource.Package && m.Packages.Any(p => p.Kind == "character" && !p.RetailEdit)) is { } mod:
                _ = ChangeIdClicked(mod, mod.Packages.First(p => p.Kind == "character" && !p.RetailEdit));
                break;
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- visuals

    private static TextBlock Text(string text, double size, IBrush? brush, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (brush is not null) block.Foreground = brush;
        if (bold) block.FontWeight = FontWeight.SemiBold;
        return block;
    }

    private static Border Chip(string text, IBrush? brush) => new()
    {
        Background = ChipBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 6, 4),
        BorderBrush = brush ?? Brushes.Transparent, BorderThickness = new Thickness(brush is null ? 0 : .8),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = brush ?? new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)) },
    };

    private static Path IconPath(Geometry geometry, double size, IBrush brush) => new()
    {
        Data = geometry, Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = brush, StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel ButtonContent(Geometry? icon, string text, double size)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) row.Children.Add(IconPath(icon, size, Brushes.White));
        if (text.Length > 0) row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Button GhostButton(string text, Geometry icon, EventHandler<RoutedEventArgs> click)
    {
        var button = new Button
        {
            Content = ButtonContent(icon, text, 16), Height = 42, Padding = new Thickness(14, 8),
            Background = new SolidColorBrush(Color.FromArgb(155, 18, 12, 16)), BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += click;
        return button;
    }

    private static Button PrimaryButton(string text, Geometry icon, EventHandler<RoutedEventArgs> click)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 187, 40, 32), 0), new GradientStop(Color.FromArgb(255, 244, 77, 42), .55),
                new GradientStop(Color.FromArgb(255, 255, 126, 61), 1),
            },
        };
        var button = new Button
        {
            Content = ButtonContent(icon, text, 16), Height = 42, Padding = new Thickness(18, 8), Background = gradient, Foreground = Brushes.White,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10), FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Resources["ButtonBackgroundPointerOver"] = gradient;
        button.Resources["ButtonBackgroundPressed"] = gradient;
        button.Click += click;
        return button;
    }

    private static Button SmallButton(string text, Geometry icon, EventHandler<RoutedEventArgs> click)
    {
        var button = new Button
        {
            Content = ButtonContent(icon, text, 13), Height = 32, Padding = new Thickness(10, 4), FontSize = 12.5,
            Background = new SolidColorBrush(Color.FromArgb(200, 44, 22, 27)), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 129, 90)),
            BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += click;
        return button;
    }

    private static Button DialogButton(string text, Geometry? icon, bool primary, bool danger = false)
    {
        var fill = new SolidColorBrush(danger ? Color.FromArgb(255, 150, 34, 30) : primary ? App.Accent : Color.FromArgb(255, 48, 19, 25));
        var button = new Button
        {
            Content = ButtonContent(icon, text, 15), MinWidth = 130, Height = 36, CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center, Background = fill, Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 241, 84, 50)), BorderThickness = new Thickness(primary ? 0 : 1),
            FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (primary)
        {
            var hot = new SolidColorBrush(danger ? Color.FromArgb(255, 180, 44, 38) : App.AccentHot);
            button.Resources["ButtonBackgroundPointerOver"] = hot;
            button.Resources["ButtonBackgroundPressed"] = fill;
        }
        return button;
    }
}

internal static class SlotExtensions
{
    public static IEnumerable<int> Forms(this CharacterSlot slot) => slot.Package.Forms.Select(f => f.Id);
}
