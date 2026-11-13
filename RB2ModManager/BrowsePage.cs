using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RB2ModCore;

namespace RB2ModManager;

/// <summary>
/// Browse Mods: Raging Blast 2's GameBanana page (game 16075) in the window. A mod is ready for the Mod Manager when one
/// of its files is a .rb2mod or an archive with one inside (GameBanana lists archive contents, so this is known before
/// downloading); the others (made for the old folder layout or the PS3) show only on request. Install downloads the file
/// (MD5 checked), takes the .rb2mod files out and goes through the normal install (ID check included).
/// </summary>
public sealed partial class MainWindow
{
    private readonly GameBananaClient banana = new();
    private Control browsePage = null!;
    private Button browseTab = null!;
    private readonly StackPanel browseList = new();
    private readonly Border browseDetail = new();
    private readonly TextBox browseSearch = new();
    private readonly ComboBox browseCategory = new();
    private readonly ComboBox browseSort = new();
    private readonly CheckBox browseOther = new();
    private readonly TextBlock browseCount = new();
    private readonly List<GbMod> browseMods = [];
    private readonly Dictionary<int, List<GbFile>> browseFiles = [];
    /// <summary>Per mod: the .rb2mod files its downloads carry (missing = still checking).</summary>
    private readonly Dictionary<int, List<string>> browseRb2 = [];
    /// <summary>Mods whose downloads hold model files for a replacement install.</summary>
    private readonly HashSet<int> browseReplace = [];
    private readonly Dictionary<string, Bitmap?> webImages = new(StringComparer.Ordinal);
    private List<GbCategory> categories = [];
    private GbMod? browseSelected;
    private GbProfile? browseProfile;
    private int browsePageNumber;
    private bool browseComplete = true;
    private bool browseLoaded;
    private string? browseError;
    private bool browseLoading;
    private CancellationTokenSource? browseCancel;

    private Control BuildBrowsePage()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(400)));

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        left.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        browseSearch.Watermark = "Search GameBanana…";
        browseSearch.Height = 36;
        browseSearch.VerticalAlignment = VerticalAlignment.Top;
        browseSearch.FontSize = 14;
        browseSearch.VerticalContentAlignment = VerticalAlignment.Center;
        browseSearch.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = LoadBrowse(true); } };
        bar.Children.Add(browseSearch);
        browseCategory.ItemsSource = new[] { "All categories" };
        browseCategory.SelectedIndex = 0;
        browseCategory.Height = 36;
        browseCategory.VerticalAlignment = VerticalAlignment.Top;
        browseCategory.MinWidth = 150;
        browseCategory.Margin = new Thickness(8, 0, 0, 0);
        browseCategory.SelectionChanged += (_, _) => { if (browseLoaded) _ = LoadBrowse(true); };
        Grid.SetColumn(browseCategory, 1);
        bar.Children.Add(browseCategory);
        browseSort.ItemsSource = GameBananaClient.Sorts.Select(s => s.Label).ToList();
        browseSort.SelectedIndex = 0;
        browseSort.Height = 36;
        browseSort.VerticalAlignment = VerticalAlignment.Top;
        browseSort.MinWidth = 160;
        browseSort.Margin = new Thickness(8, 0, 0, 0);
        browseSort.SelectionChanged += (_, _) => { if (browseLoaded) _ = LoadBrowse(true); };
        Grid.SetColumn(browseSort, 2);
        bar.Children.Add(browseSort);
        var second = new Grid();
        second.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        second.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        browseCount.FontSize = 13;
        browseCount.Opacity = .7;
        browseCount.VerticalAlignment = VerticalAlignment.Center;
        second.Children.Add(browseCount);
        browseOther.Content = "Show mods the Mod Manager can't install";
        browseOther.FontSize = 13;
        ToolTip.SetTip(browseOther, "Mods with no .rb2mod and no model file the game loads from a mod folder (sound packs, old PS3 dumps).");
        browseOther.IsCheckedChanged += (_, _) => RebuildBrowseList();
        Grid.SetColumn(browseOther, 1);
        second.Children.Add(browseOther);
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(second);
        top.Margin = new Thickness(0, 0, 0, 8);
        left.Children.Add(top);

        var list = new Border
        {
            Background = PanelBrush, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(10),
            ClipToBounds = true, Child = new ScrollViewer { Content = browseList },
        };
        Grid.SetRow(list, 1);
        left.Children.Add(list);
        grid.Children.Add(left);

        browseDetail.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(browseDetail, 1);
        grid.Children.Add(browseDetail);
        return grid;
    }

    private void BrowseShown()
    {
        if (browseLoaded || browseLoading) return;
        _ = LoadBrowse(true);
    }

    /// <summary>Reads a page of mods (the first again when <paramref name="reset"/>), then which of them carry a .rb2mod.</summary>
    private async Task LoadBrowse(bool reset)
    {
        browseCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        browseCancel = cancel;
        browseLoading = true;
        browseError = null;
        if (reset)
        {
            browseMods.Clear();
            browsePageNumber = 0;
            browseComplete = true;
        }
        RebuildBrowseList();
        try
        {
            if (categories.Count == 0)
            {
                categories = await banana.Categories(cancel.Token);
                browseLoaded = false;
                var index = browseCategory.SelectedIndex;
                browseCategory.ItemsSource = new[] { "All categories" }.Concat(categories.Select(c => $"{c.Name} ({c.Count})")).ToList();
                browseCategory.SelectedIndex = Math.Max(0, index);
            }
            var sort = GameBananaClient.Sorts[Math.Max(0, browseSort.SelectedIndex)].Key;
            int? category = browseCategory.SelectedIndex > 0 && browseCategory.SelectedIndex - 1 < categories.Count ? categories[browseCategory.SelectedIndex - 1].Id : null;
            var search = browseSearch.Text?.Trim();
            var result = await banana.Browse(browsePageNumber + 1, sort, category, search, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            browsePageNumber++;
            // a search ignores the category on GameBanana's side: filtered here
            var mods = category is null || string.IsNullOrEmpty(search) ? result.Mods
                : result.Mods.Where(m => m.Category == categories[browseCategory.SelectedIndex - 1].Name).ToList();
            browseMods.AddRange(mods.Where(m => browseMods.All(o => o.Id != m.Id)));
            browseComplete = result.Complete || result.Mods.Count == 0;
            browseCount.Text = string.IsNullOrEmpty(search) ? $"{result.Total} mods on GameBanana" : $"{result.Total} found for \"{search}\"";
            browseLoaded = true;
            browseLoading = false;
            browseSelected ??= browseMods.FirstOrDefault();
            RebuildBrowseList();
            if (browseSelected is not null && browseProfile?.Mod.Id != browseSelected.Id) _ = SelectBrowse(browseSelected);

            var files = await banana.Files(mods.Select(m => m.Id), cancel.Token);
            foreach (var (id, list) in files) browseFiles[id] = list;
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(mods.Select(async mod =>
            {
                await gate.WaitAsync(cancel.Token);
                try
                {
                    var found = new List<string>();
                    var replace = false;
                    foreach (var file in browseFiles.GetValueOrDefault(mod.Id) ?? [])
                        try
                        {
                            found.AddRange(await banana.Rb2ModsIn(file, cancel.Token));
                            if (file.IsArchive && ReplaceMods.HasAssets(await banana.ArchiveContents(file.Id, cancel.Token))) replace = true;
                        }
                        catch (HttpRequestException) { /* unknown: treated as none */ }
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        browseRb2[mod.Id] = found;
                        if (replace) browseReplace.Add(mod.Id);
                        RebuildBrowseList();
                    });
                }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            browseError = e is TaskCanceledException ? "GameBanana took too long to answer." : e.Message;
        }
        finally
        {
            if (browseCancel == cancel)
            {
                browseLoading = false;
                RebuildBrowseList();
            }
        }
    }

    private HashSet<int> InstalledFromBanana()
    {
        if (game is null || library is null) return [];
        var files = library.Mods.Where(m => m.Source == ModSource.Package).Select(m => System.IO.Path.GetFileName(m.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = library.Mods.Where(m => m.Source == ModSource.Replace).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ModDownloads.Sources(game).Where(s => s.Kind == "replace" ? folders.Contains(s.File) : files.Contains(s.File)).Select(s => s.ModId).ToHashSet();
    }

    private void RebuildBrowseList()
    {
        browseList.Children.Clear();
        var installed = InstalledFromBanana();
        var showOther = browseOther.IsChecked == true;
        var shown = browseMods.Where(m => showOther || !browseRb2.TryGetValue(m.Id, out var rb2) || rb2.Count > 0 || browseReplace.Contains(m.Id)).ToList();
        var index = 0;
        foreach (var mod in shown) browseList.Children.Add(BrowseRow(mod, index++, installed.Contains(mod.Id)));
        var hidden = browseMods.Count - shown.Count;
        var note = new StackPanel { Spacing = 10, Margin = new Thickness(18, 18), HorizontalAlignment = HorizontalAlignment.Center };
        if (browseError is not null)
        {
            note.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { IconPath(Icons.Warning, 16, WarningBrush), Text($"Couldn't reach GameBanana: {browseError}", 13, WarningBrush) } });
            var retry = SmallButton("Try again", Icons.Sync, (_, _) => _ = LoadBrowse(browseMods.Count == 0));
            retry.HorizontalAlignment = HorizontalAlignment.Center;
            note.Children.Add(retry);
        }
        else if (browseLoading)
            note.Children.Add(new StackPanel
            {
                Spacing = 8, Width = 260,
                Children =
                {
                    Text("Reading GameBanana…", 13, MutedBrush),
                    new ProgressBar { IsIndeterminate = true, Height = 4, Foreground = AccentBrush, Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) },
                },
            });
        else if (shown.Count == 0 && browseLoaded)
        {
            note.Children.Add(IconPath(Icons.Puzzle, 34, AccentHotBrush));
            note.Children.Add(new TextBlock { Text = "No mod for the Mod Manager here yet", FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            note.Children.Add(new TextBlock
            {
                Text = hidden > 0 ? $"{hidden} mod{(hidden == 1 ? "" : "s")} the Mod Manager can't install: tick \"Show mods the Mod Manager can't install\" to see them." : "Mods posted as .rb2mod or as model files (inside a .zip, .7z or .rar) show here.",
                Opacity = .65, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460,
            });
        }
        if (!browseLoading && browseError is null && !browseComplete && browseMods.Count > 0)
        {
            var more = SmallButton("Load more", Icons.Download, (_, _) => _ = LoadBrowse(false));
            more.HorizontalAlignment = HorizontalAlignment.Center;
            note.Children.Add(more);
        }
        if (note.Children.Count > 0) browseList.Children.Add(note);
        browseDetail.Child = browseSelected is null ? null : BrowseDetail(browseSelected, installed.Contains(browseSelected.Id));
    }

    private Control BrowseRow(GbMod mod, int index, bool installed)
    {
        var row = new Grid { MinHeight = 66 };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(126)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var thumb = new Border { Width = 100, Height = 56, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = PictureBackground(), Margin = new Thickness(14, 0, 12, 0) };
        if (mod.Images.FirstOrDefault() is { } image) thumb.Child = WebImage(image.Small, 200, Stretch.UniformToFill);
        row.Children.Add(thumb);

        var other = browseRb2.TryGetValue(mod.Id, out var rb2) && rb2.Count == 0 && !browseReplace.Contains(mod.Id);
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Opacity = other ? .55 : 1 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = mod.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (installed) title.Children.Add(Badge("Installed", SuccessBrush));
        else if (rb2 is null) title.Children.Add(Badge("Checking…", MutedBrush));
        else if (rb2.Count > 0) title.Children.Add(Badge("Ready to install", AccentHotBrush));
        else if (browseReplace.Contains(mod.Id)) title.Children.Add(Badge("Replacement", WarningBrush));
        else title.Children.Add(Badge("Other setup", MutedBrush));
        words.Children.Add(title);
        words.Children.Add(new TextBlock { Text = $"by {mod.Submitter}  ·  {mod.Category}  ·  updated {Ago(mod.Modified)}", FontSize = 12, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        var stats = Text($"♥ {mod.Likes}    {Compact(mod.Views)} views", 12, MutedBrush);
        stats.Margin = new Thickness(8, 0, 16, 0);
        Grid.SetColumn(stats, 2);
        row.Children.Add(stats);

        var isSelected = mod.Id == browseSelected?.Id;
        var host = new Border
        {
            Child = row, Padding = new Thickness(0, 5),
            Background = isSelected ? new SolidColorBrush(Color.FromArgb(100, 241, 84, 50)) : RowBrush(index),
            BorderBrush = isSelected ? AccentHotBrush : Brushes.Transparent, BorderThickness = new Thickness(3, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (!isSelected)
        {
            var rest = host.Background;
            host.PointerEntered += (_, _) => host.Background = new SolidColorBrush(Color.FromArgb(70, 255, 129, 90));
            host.PointerExited += (_, _) => host.Background = rest;
        }
        host.PointerPressed += (_, _) => _ = SelectBrowse(mod);
        return host;
    }

    private async Task SelectBrowse(GbMod mod)
    {
        browseSelected = mod;
        browseProfile = null;
        RebuildBrowseList();
        try
        {
            var profile = await banana.Profile(mod.Id);
            if (browseSelected?.Id != mod.Id) return;
            browseProfile = profile;
            browseFiles[mod.Id] = profile.Files;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            SetStatus($"Couldn't read {mod.Name} on GameBanana: {e.Message}", WarningBrush);
        }
        RebuildBrowseList();
    }

    private Control BrowseDetail(GbMod mod, bool installed)
    {
        var body = new StackPanel { Spacing = 10 };
        var picture = new Border { Height = 205, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = PictureBackground() };
        var images = mod.Images;
        var shown = Math.Clamp(browseImageIndex.GetValueOrDefault(mod.Id), 0, Math.Max(0, images.Count - 1));
        if (images.Count > 0) picture.Child = WebImage(images[shown].Medium, 0, Stretch.Uniform);
        body.Children.Add(picture);
        if (images.Count > 1)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            for (var i = 0; i < images.Count && i < 8; i++)
            {
                var at = i;
                var small = new Border
                {
                    Width = 40, Height = 24, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = PictureBackground(),
                    BorderBrush = i == shown ? AccentHotBrush : Brushes.Transparent, BorderThickness = new Thickness(1.2),
                    Child = WebImage(images[i].Small, 80, Stretch.UniformToFill), Cursor = new Cursor(StandardCursorType.Hand),
                };
                small.PointerPressed += (_, _) => { browseImageIndex[mod.Id] = at; RebuildBrowseList(); };
                strip.Children.Add(small);
            }
            body.Children.Add(strip);
        }
        body.Children.Add(new TextBlock { Text = mod.Name, FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var facts = new List<string> { $"by {mod.Submitter}", mod.Category };
        if (mod.Version.Length > 0) facts.Add($"version {mod.Version}");
        if (browseProfile?.Mod.Id == mod.Id) facts.Add($"{Compact(browseProfile.Downloads)} downloads");
        body.Children.Add(Text(string.Join("  ·  ", facts), 12.5, MutedBrush));
        if (installed) body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { IconPath(Icons.Check, 14, SuccessBrush), Text("Installed", 13, SuccessBrush) } });

        body.Children.Add(new TextBlock { Text = "FILES", FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = AccentHotBrush, Margin = new Thickness(0, 4, 0, 0) });
        var files = browseFiles.GetValueOrDefault(mod.Id);
        if (files is null) body.Children.Add(Text("Reading…", 12, MutedBrush));
        else if (files.Count == 0) body.Children.Add(Text("No files.", 12, MutedBrush));
        else
            foreach (var file in files.OrderByDescending(f => f.Added))
                body.Children.Add(FileCard(mod, file));

        if (browseProfile?.Mod.Id == mod.Id && browseProfile.Text.Length > 0)
        {
            body.Children.Add(new TextBlock { Text = "ABOUT", FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = AccentHotBrush, Margin = new Thickness(0, 4, 0, 0) });
            var text = browseProfile.Text.Length > 2400 ? browseProfile.Text[..2400] + "…" : browseProfile.Text;
            body.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Opacity = .82, TextWrapping = TextWrapping.Wrap });
        }
        var open = SmallButton("Open on GameBanana", Icons.Info, (_, _) => OpenUrl(mod.Url));
        open.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(open);
        return new Border
        {
            Background = PanelBrush, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(12),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(14), Child = body } },
        };
    }

    private readonly Dictionary<int, int> browseImageIndex = [];

    private Control FileCard(GbMod mod, GbFile file)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var words = new StackPanel { Spacing = 2 };
        words.Children.Add(new TextBlock { Text = file.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(Text($"{Size(file.Size)}  ·  {file.Added.LocalDateTime:yyyy-MM-dd}{(file.Version.Length > 0 ? $"  ·  v{file.Version}" : "")}", 11.5, MutedBrush));
        var installable = false;
        if (!file.Clean) words.Children.Add(Text("GameBanana's virus check flagged this file.", 11.5, ErrorBrush));
        else if (file.IsRb2Mod) { installable = true; words.Children.Add(Text("A .rb2mod", 11.5, SuccessBrush)); }
        else if (file.IsArchive)
        {
            var inside = InsideCache(file);
            var rb2 = inside?.Where(f => f.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase)).Select(f => f.Split('/')[^1]).ToList();
            if (inside is null) { words.Children.Add(Text("Checking what's inside…", 11.5, MutedBrush)); _ = CheckInside(file); }
            else if (rb2!.Count > 0) { installable = true; words.Children.Add(Text($"Has {string.Join(", ", rb2)}", 11.5, SuccessBrush)); }
            else if (ReplaceMods.HasAssets(inside))
            {
                installable = true;
                words.Children.Add(Text("Replacement mod: takes the place of game models, in its own folder (the game's files stay as they are).", 11.5, WarningBrush));
            }
            else if (inside.Any(ReplaceMods.IsRawPs3)) words.Children.Add(Text("Old PS3 files (.spr / .ioram / .vram): the PC port can't load them.", 11.5, MutedBrush));
            else words.Children.Add(Text("Nothing the Mod Manager can install.", 11.5, MutedBrush));
        }
        else words.Children.Add(Text("Not a .rb2mod.", 11.5, MutedBrush));
        if (file.Description.Length > 0) words.Children.Add(new TextBlock { Text = file.Description, FontSize = 11.5, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(words);
        if (installable)
        {
            var install = DialogButton("Install", Icons.Download, primary: true);
            install.MinWidth = 100;
            install.Height = 32;
            install.VerticalAlignment = VerticalAlignment.Center;
            install.IsEnabled = !gameRunning;
            install.Margin = new Thickness(8, 0, 0, 0);
            install.Click += (_, _) => _ = InstallFromBanana(mod, file);
            Grid.SetColumn(install, 1);
            grid.Children.Add(install);
        }
        return new Border { Child = grid, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) };
    }

    private readonly Dictionary<int, List<string>> inside = [];
    private readonly HashSet<int> insideAsked = [];

    private List<string>? InsideCache(GbFile file) => inside.GetValueOrDefault(file.Id);

    private async Task CheckInside(GbFile file)
    {
        if (!insideAsked.Add(file.Id)) return;
        try { inside[file.Id] = (await banana.ArchiveContents(file.Id)).ToList(); }
        catch (HttpRequestException) { inside[file.Id] = []; }
        RebuildBrowseList();
    }

    /// <summary>Downloads a GameBanana file and installs what it holds: .rb2mod files, else a replacement mod.</summary>
    private async Task<int> InstallFromBanana(GbMod mod, GbFile file, IReadOnlyCollection<InstalledMod>? replacing = null, bool ask = true)
    {
        if (game is null) { SetStatus("Choose the game folder first.", WarningBrush); return 0; }
        if (game.GameRunning()) { await ShowMessage("Close the game first", "The game is using the mods: close it, then install."); return 0; }
        var download = await DownloadWithProgress(file);
        if (download is null) return 0;
        int count;
        try
        {
            count = file.IsRb2Mod
                ? await InstallFiles([download], (mod, file), replacing, ask)
                : await InstallArchive(download, ReplaceMods.FolderName(mod.Name), (mod, file), replacingPackages: replacing, ask: ask);
        }
        finally { try { File.Delete(download); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        RebuildBrowseList();
        if (count > 0) SetStatus($"{mod.Name} installed from GameBanana.", SuccessBrush);
        return count;
    }

    /// <summary>Downloads a file with a progress card that can be cancelled; null when cancelled or failed (said).</summary>
    private async Task<string?> DownloadWithProgress(GbFile file)
    {
        if (game is null) return null;
        var cancel = new CancellationTokenSource();
        var text = new TextBlock { Text = $"Downloading {file.Name}…", FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Foreground = AccentBrush, Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) };
        var detail = Text(Size(file.Size), 12, MutedBrush);
        var stop = DialogButton("Cancel", null, primary: false);
        stop.HorizontalAlignment = HorizontalAlignment.Right;
        stop.Click += (_, _) => cancel.Cancel();
        overlay.Children.Clear();
        overlay.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 460,
            Background = new SolidColorBrush(Color.FromArgb(255, 25, 9, 14)), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 241, 84, 50)),
            BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(10), Padding = new Thickness(24, 20),
            Child = new StackPanel { Spacing = 12, Children = { text, bar, detail, stop } },
        });
        overlay.IsVisible = true;
        string? failure = null;
        string? download = null;
        try
        {
            download = await banana.Download(file, ModDownloads.Folder(game), new Progress<double>(p =>
            {
                bar.Value = p;
                detail.Text = $"{Size((long)(file.Size * p))} of {Size(file.Size)}";
            }), cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { failure = ""; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            failure = e is TaskCanceledException ? "GameBanana took too long to answer." : e.Message;
        }
        overlay.IsVisible = false;
        overlay.Children.Clear();
        if (failure is null) return download;
        if (failure.Length > 0) await ShowMessage("Not downloaded", failure);
        else SetStatus("Download cancelled.", MutedBrush);
        return null;
    }

    /// <summary>An image from the web, filled in when it arrives (cached for the session).</summary>
    private Image WebImage(string url, int decodeWidth, Stretch stretch)
    {
        var image = new Image { Stretch = stretch };
        if (webImages.TryGetValue(url, out var known)) { image.Source = known; return image; }
        _ = Fill();
        return image;

        async Task Fill()
        {
            try
            {
                var bytes = await banana.Image(url);
                Bitmap? bitmap;
                using (var stream = new MemoryStream(bytes))
                    bitmap = decodeWidth > 0 ? Bitmap.DecodeToWidth(stream, decodeWidth) : new Bitmap(stream);
                webImages[url] = bitmap;
                image.Source = bitmap;
            }
            catch (Exception) { webImages[url] = null; }
        }
    }

    private static string Ago(DateTimeOffset time)
    {
        if (time == DateTimeOffset.MinValue) return "—";
        var span = DateTimeOffset.Now - time;
        return span.TotalDays switch
        {
            < 1 => "today",
            < 2 => "yesterday",
            < 31 => $"{(int)span.TotalDays} days ago",
            < 365 => $"{(int)(span.TotalDays / 30)} month{((int)(span.TotalDays / 30) == 1 ? "" : "s")} ago",
            _ => $"{(int)(span.TotalDays / 365)} year{((int)(span.TotalDays / 365) == 1 ? "" : "s")} ago",
        };
    }

    private static string Compact(int value) => value >= 1000 ? $"{value / 1000.0:0.#}k" : value.ToString();
}
