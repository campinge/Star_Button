using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ScMapper.Core;
using ScMapper.Core.Sc;

namespace ScMapper.App.Controls;

/// <summary>
/// "What will this deploy change?" One row per action: what Star Citizen's file has now, what it has afterwards.
/// Stick inputs show the button's own name when it has one, otherwise stick and button.
/// </summary>
public static class DeployPreviewDialog
{
    private static readonly Regex JsInput = new(@"^js(\d+)_(.+)$", RegexOptions.Compiled);
    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>True = the user confirmed the deploy.</summary>
    public static bool Confirm(Window? owner, AppController c, IReadOnlyList<DeployChange> changes,
        IReadOnlyList<string> warnings)
    {
        var rows = changes
            .Select(ch =>
            {
                var def = c.Catalog?.Find(ch.Map, ch.Action);
                var path = c.Catalog?.PathOf(ch.Map, ch.Action) ?? ch.Map;
                return (Change: ch, Label: def?.Label ?? ch.Action, Group: path);
            })
            .OrderBy(r => r.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });

        int r = 0;
        void Cell(UIElement e, int col, int row)
        {
            Grid.SetColumn(e, col);
            Grid.SetRow(e, row);
            grid.Children.Add(e);
        }
        void NewRow() => grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        NewRow();
        Cell(new TextBlock { Style = S("H2"), Text = "ACTION", Margin = new Thickness(0, 0, 12, 8) }, 0, r);
        Cell(new TextBlock { Style = S("H2"), Text = "NOW IN STAR CITIZEN", Margin = new Thickness(0, 0, 12, 8) }, 1, r);
        Cell(new TextBlock { Style = S("H2"), Text = "AFTER DEPLOY", Margin = new Thickness(0, 0, 0, 8) }, 2, r);
        r++;

        foreach (var (ch, label, group) in rows)
        {
            NewRow();
            var edge = new Border
            {
                BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 0, 0, 0),
            };
            Grid.SetColumnSpan(edge, 3);
            Cell(edge, 0, r);

            var name = new StackPanel { Margin = new Thickness(0, 7, 12, 7) };
            name.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            name.Children.Add(new TextBlock { Style = S("Muted"), FontSize = 11, Text = group, TextWrapping = TextWrapping.Wrap });
            Cell(name, 0, r);
            Cell(Side(c, ch.Removed, "Game default", false), 1, r);
            Cell(Side(c, ch.Added, "Game default", true), 2, r);
            r++;
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 480,
            Content = grid,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ok = new Button
        {
            Content = "⇪ Deploy", Style = S("Accent"), Padding = new Thickness(18, 6, 18, 6), IsDefault = true,
        };
        var cancel = new Button
        {
            Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 700 };
        panel.Children.Add(new TextBlock { Text = "Deploy changes", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Style = S("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = $"Profile “{c.ActiveSetup.Name}” changes {rows.Count} action(s) in Star Citizen. " +
                   "A backup of actionmaps.xml is made first.",
        });
        if (rows.Count == 0)
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 14, 0, 0), Text = "No visible difference - only the file's layout would be rewritten.",
            });
        else
            panel.Children.Add(scroll);
        if (warnings.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Foreground = B("WarnBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                Text = "Warnings:\n• " + string.Join("\n• ", warnings.Take(6)),
            });
        panel.Children.Add(buttons);

        var win = new OverlayWindow { Content = panel };
        ok.Click += (_, _) => win.DialogResult = true;
        return win.ShowDialog() == true;
    }

    /// <summary>One side of a row: the rebinds as lines; nothing there means the game's default applies.</summary>
    private static UIElement Side(AppController c, IReadOnlyList<RebindState> states, string whenEmpty, bool after)
    {
        var box = new StackPanel { Margin = new Thickness(0, 7, 12, 7) };
        if (states.Count == 0)
        {
            box.Children.Add(new TextBlock { Style = S("Muted"), FontStyle = FontStyles.Italic, Text = whenEmpty });
            return box;
        }
        foreach (var s in states.OrderBy(s => s.Input, StringComparer.OrdinalIgnoreCase))
        {
            var (text, detail) = Describe(c, s);
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Run(text));
            if (detail != null) line.Inlines.Add(new Run("  " + detail) { Foreground = B("MutedBrush"), FontSize = 11.5 });
            if (s.IsCleared) line.Foreground = B("WarnBrush");
            else if (after) line.Foreground = B("AccentBrush");
            box.Children.Add(line);
        }
        return box;
    }

    /// <summary>Text of a rebind and, for stick inputs with an own name, where that button is.</summary>
    private static (string Text, string? Detail) Describe(AppController c, RebindState s)
    {
        var mode = s.Mode switch
        {
            null => "",
            "double_tap" => " · 2× TAP",
            var m => " · " + m.Replace('_', ' ').ToUpperInvariant(),
        };
        if (s.IsCleared) return ("Cleared" + mode, null);
        var m2 = JsInput.Match(s.Input);
        if (!m2.Success) return (ScInputCodes.Pretty(s.Input) + mode, null);

        int inst = int.Parse(m2.Groups[1].Value);
        var input = m2.Groups[2].Value;
        var pretty = InputNames.Pretty(input);
        var stick = c.StickByInstance(inst);
        var prof = stick?.ProfileId is { } pid ? c.Store.Profile(pid) : null;
        var label = prof == null ? null
            : c.ButtonLabel(prof, input) ?? c.ButtonLabel(prof, ScInputCodes.SplitJsInput(input).Input);
        var where = $"{(stick == null ? "js" + inst : c.StickLabel(stick))} · {pretty}";
        return label != null ? (label + mode, where) : (where + mode, null);
    }
}
