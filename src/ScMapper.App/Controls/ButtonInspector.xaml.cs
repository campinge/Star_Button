using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.App.Controls;

/// <summary>Shows and edits everything attached to one physical input of one stick, as a list of actions.</summary>
public partial class ButtonInspector : UserControl
{
    private AppController? _c;
    private StickRecord? _stick;
    private string? _input;
    private bool _loading;

    public ButtonInspector()
    {
        InitializeComponent();
    }

    public void Show(AppController c, StickRecord? stick, string? input)
    {
        _c = c; _stick = stick; _input = input;
        Refresh();
    }

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Short name, shown on the collapsed card.</summary>
    private static string TriggerLabel(ButtonTrigger t) => t switch
    {
        ButtonTrigger.Pressed => "On press",
        ButtonTrigger.Released => "On release",
        ButtonTrigger.LongPress => "Long press",
        ButtonTrigger.DoublePress => "Double press",
        ButtonTrigger.Held => "While held",
        _ => t.ToString(),
    };

    /// <summary>Full explanation, shown in the "When" list.</summary>
    private static string TriggerChoice(ButtonTrigger t) => t switch
    {
        ButtonTrigger.Pressed => "On press - tap the key when the button goes down",
        ButtonTrigger.Released => "On release - tap the key when the button is let go",
        ButtonTrigger.LongPress => "Long press - tap the key once the button is held for a while",
        ButtonTrigger.DoublePress => "Double press - tap the key when the button is pressed twice",
        ButtonTrigger.Held => "While held - key stays down as long as the button is held",
        _ => t.ToString(),
    };

    /// <summary>Re-reads the current actions (call after external changes).</summary>
    public void Refresh()
    {
        if (_c == null || _stick == null || _input == null)
        {
            Empty.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Collapsed;
            return;
        }
        _loading = true;
        Empty.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        var prof = _c.Store.Profile(_stick.ProfileId);
        Title.Text = $"{_stick.Name} · {InputNames.Pretty(_input)}";
        Subtitle.Text = prof == null ? "No controller profile assigned" : $"Profile: {prof.Name}" +
            (_stick.ScInstance is { } n ? $"   ·   Star Citizen: js{n}_{_input}" : "");

        Rows.Children.Clear();
        var modifier = prof != null ? _c.KeyModifierOf(prof, _input) : null;
        if (modifier != null) Rows.Children.Add(BuildModifierRow(prof!, modifier));
        var rows = prof == null ? new List<ButtonActionRow>() : _c.ActionsOf(prof, _input).ToList();
        foreach (var r in rows) Rows.Children.Add(BuildRow(prof!, r));
        NoActions.Visibility = rows.Count == 0 && modifier == null ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    // ---- this button acts as a keyboard modifier -----------------------------------------------------

    private sealed record ModItem(string Token, string Label)
    {
        public override string ToString() => Label;
    }

    // ---- rows -----------------------------------------------------------------------------------

    private sealed record TriggerItem(ButtonTrigger Trigger, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Rows (and the modifier card) the user has opened; survives the rebuilds after each edit.</summary>
    private readonly HashSet<string> _open = new();

    private static string RowKey(ButtonActionRow r) =>
        r.Action != null ? r.Action.Id.ToString("N") : $"{r.Map}/{r.ActionName}";

    /// <summary>A collapsed card: one line, click to open. <paramref name="details"/> is only built when opened.</summary>
    private UIElement Card(string key, string title, string? sub, Action remove, Func<UIElement> details)
    {
        var detailHost = new ContentControl { Margin = new Thickness(0, 10, 0, 0) };
        var arrow = new TextBlock { Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Width = 18 };
        var x = new Button { Content = "✕", Style = (Style)FindResource("Flat"), MinWidth = 30, MinHeight = 30,
            ToolTip = "Remove", VerticalAlignment = VerticalAlignment.Center };
        x.Click += (_, _) => { _open.Remove(key); remove(); };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(sub))
            text.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Muted"), FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis });

        var head = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        DockPanel.SetDock(x, Dock.Right);
        DockPanel.SetDock(arrow, Dock.Left);
        head.Children.Add(x);
        head.Children.Add(arrow);
        head.Children.Add(text);

        void Sync()
        {
            bool open = _open.Contains(key);
            arrow.Text = open ? "▾" : "▸";
            detailHost.Content = open ? details() : null;
            detailHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, x)) return;
            if (!_open.Add(key)) _open.Remove(key);
            Sync();
        };
        Sync();

        var box = new StackPanel();
        box.Children.Add(head);
        box.Children.Add(detailHost);
        return new Border
        {
            Background = B("BgBrush"), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 8, 8), Margin = new Thickness(0, 0, 0, 8), Child = box,
        };
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var c = d; c != null; c = c is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(c) : LogicalTreeHelper.GetParent(c))
            if (ReferenceEquals(c, ancestor)) return true;
        return false;
    }

    private static Grid Field(string label, UIElement value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = B("MutedBrush") };
        Grid.SetColumn(value, 1);
        g.Children.Add(l);
        g.Children.Add(value);
        return g;
    }

    private UIElement BuildModifierRow(ControllerProfile prof, string current)
    {
        var c = _c!;
        var others = c.OtherLayerButtons(prof, _input!, current);
        return Card("modifier", $"Acts as {InputNames.ModifierName(current)} while held",
            "Keyboard modifier",
            () => { c.SetKeyModifierButton(prof, _input!, null); Refresh(); },
            () =>
            {
                var items = ScInputCodes.KeyModifiers.Select(t => new ModItem(t, InputNames.ModifierName(t))).ToList();
                var box = new ComboBox { MinWidth = 180, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Left,
                    ItemsSource = items, SelectedItem = items.FirstOrDefault(i => i.Token == current) ?? items[0] };
                box.SelectionChanged += (_, _) =>
                {
                    if (_loading || box.SelectedItem is not ModItem m || m.Token == current) return;
                    c.SetKeyModifierButton(prof, _input!, m.Token);
                    Refresh();
                };
                var st = new StackPanel();
                st.Children.Add(Field("Key", box));
                if (others.Count > 0)
                    st.Children.Add(new TextBlock
                    {
                        Text = $"{string.Join(", ", others.Select(InputNames.Pretty))} also hold(s) {InputNames.ModifierName(current)}.",
                        Foreground = B("WarnBrush"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    });
                return st;
            });
    }

    private UIElement BuildRow(ControllerProfile prof, ButtonActionRow row)
    {
        var c = _c!;
        var def = row.Map != null && row.ActionName != null ? c.Catalog?.Find(row.Map, row.ActionName) : null;
        var spec = row.ToSpec();
        bool isGame = spec.IsGameAction;

        string title = isGame ? def?.Label ?? row.ActionName! : $"Key {(string.IsNullOrWhiteSpace(row.Keys) ? "(not set)" : ScInputCodes.Pretty("kb1_" + row.Keys))}";
        var parts = new List<string> { TriggerLabel(row.Trigger) };
        if (row.Bind != null && row.KeyModifier is { } km) parts.Add("with " + InputNames.ModifierName(km));

        return Card(RowKey(row), title, string.Join("  ·  ", parts),
            () => { c.RemoveAction(prof, row); Refresh(); },
            () => BuildDetails(prof, row, spec, def, isGame));
    }

    private UIElement BuildDetails(ControllerProfile prof, ButtonActionRow row, ActionSpec spec, ActionDef? def, bool isGame)
    {
        var c = _c!;
        var st = new StackPanel();

        // When
        var options = new List<ButtonTrigger> { ButtonTrigger.Pressed, ButtonTrigger.Released };
        if (isGame && (def?.Joystick ?? true))
            options.AddRange(new[] { ButtonTrigger.LongPress, ButtonTrigger.DoublePress });
        if (!isGame) options.Add(ButtonTrigger.Held);
        var items = options.Select(t => new TriggerItem(t, TriggerChoice(t))).ToList();
        var trigger = new ComboBox { MinWidth = 200, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = items };
        trigger.SelectedItem = items.FirstOrDefault(i => i.Trigger == row.Trigger) ?? items[0];
        trigger.SelectionChanged += (_, _) =>
        {
            if (_loading || trigger.SelectedItem is not TriggerItem ti || ti.Trigger == row.Trigger) return;
            Apply(prof, row, spec with { Trigger = ti.Trigger });
        };
        st.Children.Add(Field("When", trigger));

        // What
        if (isGame)
        {
            var group = row.Map != null ? c.Catalog?.Groups.FirstOrDefault(g => g.Name == row.Map) : null;
            var what = new DockPanel();
            var change = new Button { Content = "Change", Style = (Style)FindResource("Flat"), MinHeight = 30 };
            change.Click += (_, _) => ChangeTarget(prof, row, spec);
            DockPanel.SetDock(change, Dock.Right);
            what.Children.Add(change);
            what.Children.Add(new TextBlock
            {
                Text = group != null ? $"{group.Category} › {group.Label}" : row.Map, Style = (Style)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            st.Children.Add(Field("Action", what));
        }

        var keyLine = BuildKeyLine(row, spec, prof);
        if (keyLine != null) st.Children.Add(keyLine);
        if (row.ViaApp)
            st.Children.Add(new TextBlock
            {
                Text = "Sent by Star Button - it has to be running.", Style = (Style)FindResource("Muted"), FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
            });
        return st;
    }

    private UIElement? BuildKeyLine(ButtonActionRow row, ActionSpec spec, ControllerProfile prof)
    {
        var c = _c!;
        if (!row.ViaApp) return null;

        if (!spec.IsGameAction)
        {
            var own = new KeyCaptureBox { MinHeight = 34, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            own.SetComboSilently(row.Keys);
            own.ComboChanged += combo => Apply(prof, row, spec with { Keys = combo }, rebuild: false);
            return Field("Key", own);
        }

        var key = c.KeyFor(spec.Map!, spec.Action!);
        if (key.Source == KeySource.Own)
        {
            var box = new KeyCaptureBox { MinHeight = 34, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            box.SetComboSilently(key.Combo);
            box.ComboChanged += combo =>
            {
                c.SetActionKey(spec.Map!, spec.Action!, combo);
                Refresh();
            };
            var st = new StackPanel();
            st.Children.Add(Field("Key", box));
            var clash = c.KeyConflicts(spec.Map!, spec.Action!, key.Combo!);
            if (clash.Count > 0)
                st.Children.Add(new TextBlock
                {
                    Text = "Also used by: " + string.Join(", ", clash.Take(4)) + (clash.Count > 4 ? ", …" : ""),
                    Foreground = B("WarnBrush"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                });
            return st;
        }
        if (key.Source == KeySource.Default)
            return Field("Key", new TextBlock { Text = ScInputCodes.Pretty("kb1_" + key.Combo), FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center });
        return Field("Key", new TextBlock { Text = "none available yet", Foreground = B("WarnBrush"), VerticalAlignment = VerticalAlignment.Center });
    }

    // ---- edits ----------------------------------------------------------------------------------

    private void Apply(ControllerProfile prof, ButtonActionRow row, ActionSpec spec, bool rebuild = true)
    {
        if (_c == null || _input == null) return;
        _c.UpdateAction(prof, _input, row, spec);
        if (rebuild) Refresh();
    }

    private void ChangeTarget(ControllerProfile prof, ButtonActionRow row, ActionSpec spec)
    {
        var pick = ActionPicker.Ask(Window.GetWindow(this), _c?.Catalog, "Change action");
        if (pick == null) return;
        Apply(prof, row, spec with { Map = pick.Map, Action = pick.Action, Keys = pick.Keys });
    }

    private void Add_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _stick == null || _input == null) return;
        var pick = ActionPicker.Ask(Window.GetWindow(this), _c.Catalog, "Add action", allowModifier: true);
        if (pick == null) return;
        var prof = _c.EnsureProfile(_stick);
        if (pick.Modifier != null) _c.SetKeyModifierButton(prof, _input, pick.Modifier);
        else _c.AddAction(prof, _input, new ActionSpec(ButtonTrigger.Pressed, pick.Map, pick.Action, pick.Keys));
        Refresh();
    }

    private void ClearAll_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _stick == null || _input == null) return;
        var prof = _c.Store.Profile(_stick.ProfileId);
        if (prof == null) return;
        prof.Binds.RemoveAll(b => ScInputCodes.SplitJsInput(b.Input).Input == _input);
        prof.Actions.RemoveAll(a => a.Input == _input);
        prof.Rules.Remove(_input);
        _c.Touch();
        Refresh();
    }
}
