using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScMapper.Core;
using ScMapper.Core.Sc;

namespace ScMapper.App.Controls;

/// <summary>Result of the picker: an own key combo (Keys) or a keyboard modifier (Modifier).</summary>
public sealed record PickResult(string? Keys, string? Modifier = null);

/// <summary>Chooses what a button does: a key (combo) - from the free ones or recorded - or, when allowed, a keyboard modifier.</summary>
public static class ActionPicker
{
    private sealed record ModItem(string Token, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>A candidate combo; taken ones stay in the list but are greyed out and cannot be picked.</summary>
    private sealed record KeyItem(string Combo, string Label, bool Free)
    {
        public override string ToString() => Label;
    }

    public static PickResult? Ask(Window? owner, AppController c, string title = "Add action", bool allowModifier = false)
    {
        var items = c.KeyChoices()
            .Select(k => new KeyItem(k.Combo,
                k.Owner == null ? ScInputCodes.Pretty("kb1_" + k.Combo) : $"{ScInputCodes.Pretty("kb1_" + k.Combo)}   -   in use: {k.Owner}",
                k.Owner == null))
            .OrderByDescending(i => i.Free)
            .ToList();

        var search = new TextBox { MinHeight = 36, ToolTip = "Type part of a key name, e.g. F5 or Ctrl" };
        var list = new ListBox { Height = 420, Margin = new Thickness(0, 8, 0, 0) };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(KeyItem.Free))));
        var taken = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(KeyItem.Free)), Value = false };
        taken.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        itemStyle.Triggers.Add(taken);
        list.ItemContainerStyle = itemStyle;
        var info = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var keyBox = new KeyCaptureBox { MinHeight = 36, MinWidth = 220 };
        var keyClear = new Button { Content = "✕", MinWidth = 36, MinHeight = 36, Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Flat") };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        keyRow.Children.Add(keyBox);
        keyRow.Children.Add(keyClear);

        var ok = new Button { Content = "OK", IsDefault = true, IsEnabled = false, Padding = new Thickness(18, 6, 18, 6),
            Style = (Style)Application.Current.FindResource("Accent") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        // what kind of action: a key or a keyboard modifier, each with its own small panel
        var tabStyle = (Style)Application.Current.FindResource("TabHeader");
        var modeKey = new RadioButton { Content = "Key / Control", IsChecked = true, Style = tabStyle, GroupName = "mode" };
        var modeMod = new RadioButton { Content = "Keyboard Modifier", Style = tabStyle, GroupName = "mode",
            Visibility = allowModifier ? Visibility.Visible : Visibility.Collapsed };
        var modes = new UniformGrid { Rows = 1 };
        modes.Children.Add(modeKey);
        modes.Children.Add(modeMod);
        var modesBar = new Border
        {
            Child = modes,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush"),
        };

        var keyPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        keyPanel.Children.Add(new TextBlock { Text = "Keys Star Citizen does not use by default:", TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.FindResource("Muted") });
        keyPanel.Children.Add(search);
        keyPanel.Children.Add(list);
        keyPanel.Children.Add(info);
        keyPanel.Children.Add(new TextBlock { Text = "Or enter your own: click the field and press the key or combination, e.g. Ctrl+K.",
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 12, 0, 0) });
        keyPanel.Children.Add(keyRow);
        keyPanel.Children.Add(warn);

        var modItems = ScInputCodes.KeyModifiers.Select(t => new ModItem(t, InputNames.ModifierName(t))).ToList();
        var modBox = new ComboBox { MinHeight = 34, MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = modItems,
            SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 0) };
        var modPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        modPanel.Children.Add(new TextBlock
        {
            Text = "While this button is held it acts as that keyboard key. Other buttons can then be bound to “modifier + button” in Star Citizen.",
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Muted"),
        });
        modPanel.Children.Add(modBox);

        var panel = new StackPanel { Margin = new Thickness(20), Width = 640 };
        if (allowModifier) panel.Children.Add(new TextBlock { Text = "What should this button do?", Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(modesBar);
        panel.Children.Add(keyPanel);
        panel.Children.Add(modPanel);
        panel.Children.Add(buttons);

        var win = new OverlayWindow { Title = title, Content = panel };

        void Filter()
        {
            var terms = search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = items.Where(i => terms.All(t => i.Label.ToLowerInvariant().Contains(t))).ToList();
            list.ItemsSource = hits;
            int free = items.Count(i => i.Free);
            info.Text = free == 0 ? "No free key combination left - enter your own below."
                : hits.Count == 0 ? "No key matches."
                : $"{free} of {items.Count} combinations are free. Greyed-out ones are already in use.";
        }
        void Validate()
        {
            if (modeKey.IsChecked != true)
            {
                ok.IsEnabled = modBox.SelectedItem is ModItem;
                return;
            }
            var typed = keyBox.Text.Trim();
            var danger = KeyPlanner.DangerReason(typed);
            var holder = danger == null && typed.Length > 0 ? c.KeyOwner(typed) : null;
            ok.IsEnabled = typed.Length > 0 && danger == null;
            warn.Text = danger != null ? "Not allowed: " + danger
                : holder != null ? $"Already in use by {holder} - pressing this button would trigger that too." : "";
            warn.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource(danger != null ? "BadBrush" : "WarnBrush");
        }
        void SetMode()
        {
            keyPanel.Visibility = modeKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            modPanel.Visibility = modeMod.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Validate();
        }
        foreach (var r in new[] { modeKey, modeMod }) r.Checked += (_, _) => SetMode();
        modBox.SelectionChanged += (_, _) => Validate();

        search.TextChanged += (_, _) => Filter();
        // a list choice fills the key field, so what is in the field is what gets bound
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is KeyItem k) keyBox.SetComboSilently(k.Combo);
            Validate();
        };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is KeyItem && ok.IsEnabled) ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        keyBox.ComboChanged += _ => { list.SelectedItem = null; Validate(); };
        keyClear.Click += (_, _) => { keyBox.SetComboSilently(""); list.SelectedItem = null; Validate(); };

        PickResult? result = null;
        ok.Click += (_, _) =>
        {
            result = modeMod.IsChecked == true && modBox.SelectedItem is ModItem mi
                ? new PickResult(null, mi.Token)
                : new PickResult(keyBox.Text.Trim());
            win.DialogResult = true;
        };
        // The list takes what is left of the window, so OK / Cancel always stay visible. Measured, not guessed:
        // everything but the list is laid out once with a small list, the rest of the window height is the list's.
        void FitList()
        {
            const double min = 120, max = 420, cardChrome = 24 * 2 + 64 /* layer margin + title bar + border */;
            double screen = OverlayWindow.Host is { ActualHeight: > 0 } h ? h.ActualHeight : SystemParameters.WorkArea.Height;
            list.Height = min;
            info.Text = "x";
            warn.Text = "Reserve for a two-line warning, so showing one does not push the buttons out.\n.";
            panel.Measure(new Size(panel.Width, double.PositiveInfinity));
            double rest = panel.DesiredSize.Height - min;
            list.Height = Math.Clamp(screen - cardChrome - rest, min, max);
            // the warning's room stays reserved while it is empty (see MinHeight), the text itself starts blank
            warn.Text = "";
            warn.MinHeight = 34;
        }
        win.Loaded += (_, _) => { FitList(); Filter(); search.Focus(); };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        return win.ShowDialog() == true ? result : null;
    }
}
