using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ScMapper.Core.Sc;

namespace ScMapper.App.Controls;

/// <summary>Result of the picker: a game action (Map/Action), an own key combo (Keys) or a keyboard modifier (Modifier).</summary>
public sealed record PickResult(string? Map, string? Action, string? Keys, string? Modifier = null);

/// <summary>Chooses what a button does: a Star Citizen action, an own key (combo) or - when allowed - a keyboard modifier.</summary>
public static class ActionPicker
{
    private sealed record ModItem(string Token, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record Item(ActionDef Def, string Label, string Sub, string Haystack);

    public static PickResult? Ask(Window? owner, ActionCatalog? catalog, string title = "Add action", bool allowModifier = false)
    {
        var items = new List<Item>();
        if (catalog != null)
            foreach (var g in catalog.Groups)
                foreach (var a in g.Actions)
                    items.Add(new Item(a, a.Label, $"{g.Category} › {g.Label}",
                        $"{a.Label} {a.Name} {g.Label} {g.Category}".ToLowerInvariant()));
        items = items.OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase).ToList();

        var search = new TextBox { MinHeight = 36, ToolTip = "Type part of the action name" };
        var list = new ListBox { Height = 300, Margin = new Thickness(0, 8, 0, 0) };
        var template = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var t1 = new FrameworkElementFactory(typeof(TextBlock));
        t1.SetBinding(TextBlock.TextProperty, new Binding(nameof(Item.Label)));
        var t2 = new FrameworkElementFactory(typeof(TextBlock));
        t2.SetBinding(TextBlock.TextProperty, new Binding(nameof(Item.Sub)));
        t2.SetValue(FrameworkElement.StyleProperty, Application.Current.FindResource("Muted"));
        t2.SetValue(TextBlock.FontSizeProperty, 11.0);
        stack.AppendChild(t1);
        stack.AppendChild(t2);
        template.VisualTree = stack;
        list.ItemTemplate = template;

        var info = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Text = catalog == null ? "Game data is not loaded yet - you can still enter an own key below." : "",
        };

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

        // what kind of action: one of three, each with its own small panel
        var modeGame = new RadioButton { Content = "Star Citizen action", IsChecked = true, Margin = new Thickness(0, 0, 18, 0) };
        var modeKey = new RadioButton { Content = "Own key / key combo", Margin = new Thickness(0, 0, 18, 0) };
        var modeMod = new RadioButton { Content = "Keyboard modifier (Shift / Ctrl / Alt)", Visibility = allowModifier ? Visibility.Visible : Visibility.Collapsed };
        var modes = new WrapPanel();
        modes.Children.Add(modeGame);
        modes.Children.Add(modeKey);
        modes.Children.Add(modeMod);

        var gamePanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        gamePanel.Children.Add(search);
        gamePanel.Children.Add(list);
        gamePanel.Children.Add(info);

        var keyPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        keyPanel.Children.Add(new TextBlock { Text = "Click the field and press the key or combination, e.g. Ctrl+K.", TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.FindResource("Muted") });
        keyPanel.Children.Add(keyRow);

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

        var panel = new StackPanel { Margin = new Thickness(20), Width = 520 };
        if (allowModifier) panel.Children.Add(new TextBlock { Text = "What should this button do?", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(modes);
        panel.Children.Add(gamePanel);
        panel.Children.Add(keyPanel);
        panel.Children.Add(modPanel);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            ShowInTaskbar = false,
        };

        void Filter()
        {
            var terms = search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = items.Where(i => terms.All(t => i.Haystack.Contains(t))).Take(200).ToList();
            list.ItemsSource = hits;
            info.Text = catalog == null ? info.Text : hits.Count == 0 ? "No action matches." : "";
        }
        void Validate() => ok.IsEnabled =
            modeGame.IsChecked == true ? list.SelectedItem is Item
            : modeKey.IsChecked == true ? !string.IsNullOrWhiteSpace(keyBox.Text)
            : modBox.SelectedItem is ModItem;
        void SetMode()
        {
            gamePanel.Visibility = modeGame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            keyPanel.Visibility = modeKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            modPanel.Visibility = modeMod.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Validate();
        }
        foreach (var r in new[] { modeGame, modeKey, modeMod }) r.Checked += (_, _) => SetMode();
        modBox.SelectionChanged += (_, _) => Validate();

        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) =>
        {
            Validate();
        };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is Item) ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        keyBox.ComboChanged += _ => Validate();
        keyClear.Click += (_, _) => { keyBox.SetComboSilently(""); Validate(); };

        PickResult? result = null;
        ok.Click += (_, _) =>
        {
            result = modeGame.IsChecked == true && list.SelectedItem is Item it ? new PickResult(it.Def.Map, it.Def.Name, null)
                : modeMod.IsChecked == true && modBox.SelectedItem is ModItem mi ? new PickResult(null, null, null, mi.Token)
                : new PickResult(null, null, keyBox.Text.Trim());
            win.DialogResult = true;
        };
        win.Loaded += (_, _) => { Filter(); search.Focus(); };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        return win.ShowDialog() == true ? result : null;
    }
}
