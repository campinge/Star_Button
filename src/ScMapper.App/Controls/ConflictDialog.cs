using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScMapper.Core;

namespace ScMapper.App.Controls;

/// <summary>
/// Shown when a profile is loaded and something does not add up: a button that Star Button replaces still has a
/// Star Citizen binding, or an action has no key. Every row is the user's own decision - nothing is fixed here
/// unless a button is clicked.
/// </summary>
public static class ConflictDialog
{
    /// <summary>Opens the dialog when the active profile has unresolved conflicts; otherwise does nothing.</summary>
    public static void ShowIfAny(Window? owner, AppController c)
    {
        if (c.FindConflicts().Count == 0) return;

        var list = new StackPanel();
        var head = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
            Style = (Style)Application.Current.FindResource("Muted"),
        };
        var scroll = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, Padding = new Thickness(18, 6, 18, 6),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(20), Width = 620 };
        panel.Children.Add(head);
        panel.Children.Add(intro);
        panel.Children.Add(scroll);
        panel.Children.Add(close);

        var win = new Window
        {
            Title = "Check this profile",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            ShowInTaskbar = false,
        };

        Button Flat(string text, Action click)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("Flat"), MinHeight = 30,
                Margin = new Thickness(0, 6, 8, 0) };
            b.Click += (_, _) => { click(); Fill(); };
            return b;
        }

        void Fill()
        {
            list.Children.Clear();
            var all = c.FindConflicts();
            if (all.Count == 0) { win.Close(); return; }
            head.Text = $"Profile “{c.ActiveSetup.Name}”: {all.Count} thing(s) to check";
            intro.Text = "Star Button changes nothing on its own. Decide for each line: leave it as it is (Ignore) or fix it.";
            foreach (var cf in all)
            {
                var item = cf;
                string where = $"{item.Stick.Name} · {InputNames.Pretty(item.Input)}"
                               + (c.ButtonLabel(item.Layout, item.Input) is { } l ? $" “{l}”" : "")
                               + $"  ·  stick layout “{item.Layout.Name}”";
                var action = c.Catalog?.Find(item.Map, item.Action)?.Label ?? item.Action;
                var title = new TextBlock { Text = where, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var what = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("WarnBrush") };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                if (item.Kind == ConflictKind.DirectBinding)
                {
                    what.Text = $"Replaced by {item.Replacement}, but still bound to “{action}” in Star Citizen" +
                                (item.IsGameDefault ? " (game default)" : "") + " - that binding fires as well.";
                    buttons.Children.Add(Flat("Ignore", () => c.IgnoreConflict(item)));
                    buttons.Children.Add(Flat("Remove binding", () => c.RemoveConflictingBinding(item)));
                }
                else
                {
                    what.Text = $"{item.Replacement} has no key in this profile - nothing would be sent.";
                    buttons.Children.Add(Flat("Suggest free key", () =>
                    {
                        if (c.SuggestKey() is { } combo) c.SetActionKey(item.Map, item.Action, combo);
                    }));
                    buttons.Children.Add(Flat("Ignore", () => c.IgnoreConflict(item)));
                }
                var box = new StackPanel();
                box.Children.Add(title);
                box.Children.Add(what);
                box.Children.Add(buttons);
                list.Children.Add(new Border
                {
                    Background = (Brush)Application.Current.FindResource("PanelBrush"),
                    Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = box,
                });
            }
        }

        Fill();
        win.ShowDialog();
    }
}
