using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RB2ModCore;

namespace RB2ModManager;

/// <summary>
/// Character IDs: one row per character a mod brings, with its ID and whether it collides with another installed one,
/// and one button that organizes them all (IdOrganizer pack mode: every mod character down to the first free IDs from
/// 101, the game's own 0..100 and its Raging Blast 1 roster never given out).
/// </summary>
public sealed partial class MainWindow
{
    private readonly StackPanel idRows = new();
    private readonly TextBlock idSummary = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private Button organizeButton = null!;

    private Control BuildIdsPage()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var words = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(idSummary);
        words.Children.Add(new TextBlock
        {
            Text = "IDs 0–100 and the Raging Blast 1 roster (120–192) belong to the game: mods never get them.",
            FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap,
        });
        top.Children.Add(words);
        organizeButton = PrimaryButton("Organize All IDs", Icons.Hash, (_, _) => _ = OrganizeClicked());
        organizeButton.Height = 44;
        organizeButton.Margin = new Thickness(16, 0, 0, 0);
        ToolTip.SetTip(organizeButton, "Gives every mod character its own IDs, in order from 101, skipping the game's.");
        Grid.SetColumn(organizeButton, 1);
        top.Children.Add(organizeButton);
        grid.Children.Add(top);
        var list = new Border
        {
            Background = PanelBrush, BorderBrush = PanelBorderBrush, BorderThickness = new Thickness(.65), CornerRadius = new CornerRadius(12),
            ClipToBounds = true, Child = new ScrollViewer { Content = idRows },
        };
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        return grid;
    }

    /// <summary>The characters from mods (and Engine folders), lowest ID first.</summary>
    private List<CharacterSlot> ModSlots() => map.Where(s => !s.FromGame).ToList();

    /// <summary>The other characters that use one of this one's IDs.</summary>
    private List<CharacterSlot> SameId(CharacterSlot slot)
    {
        var ids = slot.Package.Forms.Select(f => f.Id).ToHashSet();
        return map.Where(o => o != slot && o.Package.Forms.Any(f => ids.Contains(f.Id))).ToList();
    }

    private void RebuildIds()
    {
        idRows.Children.Clear();
        organizeButton.IsEnabled = !gameRunning && library is not null;
        if (library is null) return;
        var slots = ModSlots();
        var clashing = slots.Count(s => SameId(s).Count > 0);
        idSummary.Text = slots.Count == 0 ? "No mod characters installed."
            : $"{slots.Count} mod character{(slots.Count == 1 ? "" : "s")}  ·  " + (clashing == 0 ? "every ID is its own" : $"{clashing} share an ID");
        idSummary.Foreground = clashing > 0 ? ErrorBrush : Brushes.White;
        var index = 0;
        foreach (var slot in slots) idRows.Children.Add(SlotRow(slot, index++));
        if (slots.Count == 0)
            idRows.Children.Add(new TextBlock { Text = "Characters from .rb2mod files show here with their IDs.", Opacity = .6, Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center });
    }

    private Control SlotRow(CharacterSlot slot, int index)
    {
        var others = SameId(slot);
        var row = new Grid { MinHeight = 58 };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(56)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(52)));

        var bad = others.Count > 0 || slot.Worst == IssueLevel.Error;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5), Margin = new Thickness(14, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(bad ? Color.FromArgb(70, 255, 104, 88) : Color.FromArgb(60, 126, 224, 129)),
            Child = new TextBlock { Text = CharacterIds.Span(slot.Package), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = bad ? ErrorBrush : SuccessBrush },
        };
        row.Children.Add(chip);
        var face = Face(slot.Owner!, 40);
        face.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(face, 1);
        row.Children.Add(face);

        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
        words.Children.Add(new TextBlock { Text = slot.Package.Name, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var forms = slot.Package.Forms.Count;
        var from = slot.Owner!.Source == ModSource.WorkingFolder ? "Engine folder" : System.IO.Path.GetFileName(slot.Owner.Path);
        words.Children.Add(Text($"{(forms > 1 ? $"{forms} forms  ·  " : "")}{from}", 11.5, MutedBrush));
        Grid.SetColumn(words, 2);
        row.Children.Add(words);

        string status;
        IBrush brush;
        if (others.Count > 0)
            (status, brush) = ($"Same ID as {string.Join(", ", others.Select(o => o.FromGame ? $"{o.Package.Name} (game)" : o.Package.Name).Distinct())}", ErrorBrush);
        else if (slot.Worst == IssueLevel.Error)
            (status, brush) = ("Uses one of the game's IDs", ErrorBrush);
        else if (slot.Package.RetailEdit)
            (status, brush) = ("Edits a game character", MutedBrush);
        // the same character under two IDs (an Engine reinstall after an ID change, a copy): it shows twice in the game
        else if (map.FirstOrDefault(o => o != slot && !o.FromGame && o.Package.Folder.Length > 4 && slot.Package.Folder.Length > 4
                     && o.Package.Folder[3..].Equals(slot.Package.Folder[3..], StringComparison.OrdinalIgnoreCase)) is { } twin)
            (status, brush) = ($"Installed twice (also {(twin.Owner!.Source == ModSource.WorkingFolder ? "an Engine folder" : System.IO.Path.GetFileName(twin.Owner.Path))})", WarningBrush);
        else if (slot.Issues.Any(i => i.Level == IssueLevel.Warning))
            (status, brush) = ("Transforms into a missing ID", WarningBrush);
        else
            (status, brush) = ("OK", SuccessBrush);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = brush, BorderThickness = new Thickness(.8), MaxWidth = 300,
            Child = new TextBlock { Text = status, FontSize = 12, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        if (slot.Issues.Count > 0) ToolTip.SetTip(pill, string.Join("\n", slot.Issues.Select(i => i.Text)));
        Grid.SetColumn(pill, 3);
        row.Children.Add(pill);

        if (slot.Owner.Source == ModSource.Package && !slot.Package.RetailEdit)
        {
            var edit = SmallButton("", Icons.Edit, (_, _) => _ = ChangeIdClicked(slot.Owner, slot.Package));
            edit.Width = 34;
            edit.Padding = new Thickness(0);
            edit.HorizontalContentAlignment = HorizontalAlignment.Center;
            edit.HorizontalAlignment = HorizontalAlignment.Center;
            edit.IsEnabled = !gameRunning;
            ToolTip.SetTip(edit, "Pick its ID yourself");
            Grid.SetColumn(edit, 4);
            row.Children.Add(edit);
        }
        return new Border { Child = row, Background = RowBrush(index), Padding = new Thickness(0, 3) };
    }

    private async Task OrganizeClicked()
    {
        if (game is null || library is null) return;
        var moves = IdOrganizer.Plan(library, pack: true);
        if (moves.Count == 0)
        {
            await ShowMessage("IDs are organized", "Every mod character already has its own IDs, in order from 101.");
            return;
        }
        var list = new StackPanel { Spacing = 6 };
        foreach (var move in moves)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(46)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.Children.Add(Face(move.Mod, 34));
            var name = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = move.Package.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(move.Mod.Path), FontSize = 11, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var ids = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            ids.Children.Add(Text(CharacterIds.Span(move.Package), 14, MutedBrush));
            ids.Children.Add(Text("→", 14, MutedBrush));
            ids.Children.Add(Text(Moved(move.Package, move.To), 15, AccentHotBrush, true));
            Grid.SetColumn(ids, 2);
            row.Children.Add(ids);
            list.Children.Add(row);
        }
        var body = new StackPanel { Spacing = 12, MaxWidth = 560 };
        body.Children.Add(new TextBlock { Text = $"{moves.Count} character{(moves.Count == 1 ? "" : "s")} get new IDs:", FontSize = 14 });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 340 });
        body.Children.Add(new TextBlock
        {
            Text = "Their forms, pictures and transformations move with them. The old files are kept in Mods\\Removed.",
            FontSize = 12, Opacity = .6, TextWrapping = TextWrapping.Wrap,
        });
        if (!await ShowDialog("Organize All IDs", body, "Organize", Icons.Hash)) return;
        var game0 = game;
        var lines = await RunBusy($"Moving {moves.Count} character{(moves.Count == 1 ? "" : "s")}…", () => ModActions.Organize(game0, moves));
        Refresh();
        if (lines is not null) SetStatus($"IDs organized: {moves.Count} character{(moves.Count == 1 ? "" : "s")} moved.", SuccessBrush);
    }
}
