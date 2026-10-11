using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.App.Controls;

/// <summary>
/// Pairs the connected sticks with stick profiles. After re-plugging, identical sticks cannot be told apart by
/// the app: press a button on a stick and its row lights up, then pick the profile it should have.
/// </summary>
public static class ProfileAssignerDialog
{
    private sealed class Row
    {
        public required DeviceInfo Device;
        public required Border Card;
        public required ComboBox Combo;
        public DateTime LitUntil;
    }

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Returns true when an assignment was applied.</summary>
    public static bool Show(Window? owner, AppController c)
    {
        var devices = c.Hub.Devices.Where(d => d.HasJsNumber).OrderBy(d => d.EnumIndex).ToList();
        // Only layouts that can be used here: those of the active setup plus unused ones. Layouts that belong
        // to another setup stay out of the list.
        var active = c.ActiveSetup;
        var foreign = c.Setups.Where(s => !ReferenceEquals(s, active))
            .SelectMany(s => s.StickLayouts.Values).ToHashSet();
        var profiles = c.StickProfiles.Where(p => !foreign.Contains(p.Id)).ToList();
        var rows = new List<Row>();
        var preselected = devices.Select(d => c.StickForDevice(d.InstanceGuid)?.ProfileId)
            .Where(id => id != null).Select(id => id!.Value).ToList();

        var list = new StackPanel();
        foreach (var d in devices)
        {
            var current = c.StickForDevice(d.InstanceGuid);
            var currentProfile = current == null ? null : profiles.FirstOrDefault(p => p.Id == current.ProfileId);
            // No profile yet: offer an unused one of the same model (kept from before a reinstall, say).
            if (currentProfile == null)
            {
                currentProfile = c.FreeProfileFor(d.ProductName, preselected);
                // Nothing to reuse: the stick gets a fresh profile named after its model.
                if (currentProfile == null && current != null)
                    currentProfile = c.EnsureProfile(current);
                if (currentProfile != null)
                {
                    preselected.Add(currentProfile.Id);
                    if (!profiles.Contains(currentProfile)) profiles.Add(currentProfile);
                }
            }
            var title = new TextBlock
            {
                Text = $"js{d.DefaultScInstance}  ·  {d.ProductName.Trim()}",
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var sub = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("Muted"),
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                Text = $"{d.Buttons} buttons · {d.Vid:X4}:{d.Pid:X4}" +
                       (current != null ? $" · stick “{current.Name}”" : "") +
                       (currentProfile != null ? $" · profile “{currentProfile.Name}”" : " · no profile yet"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Background = B("Panel2Brush"),
                BorderBrush = B("BorderBrush"),
                Child = new StackPanel { Children = { title, sub } },
            };
            var combo = new ComboBox
            {
                ItemsSource = profiles,
                MinHeight = 38,
                VerticalAlignment = VerticalAlignment.Center,
                SelectedItem = currentProfile,
            };
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            Grid.SetColumn(combo, 2);
            grid.Children.Add(card);
            grid.Children.Add(combo);
            list.Children.Add(grid);
            rows.Add(new Row { Device = d, Card = card, Combo = combo });
        }

        var header = new Grid { Margin = new Thickness(0, 14, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        var h2 = (Style)Application.Current.FindResource("H2");
        var left = new TextBlock { Style = h2, Text = "Sticks (connected)" };
        var right = new TextBlock { Style = h2, Text = "Profile" };
        Grid.SetColumn(right, 2);
        header.Children.Add(left);
        header.Children.Add(right);

        var warn = new TextBlock
        {
            Foreground = B("BadBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var empty = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Muted"),
            Text = "No sticks connected.",
            Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 8),
        };

        var apply = new Button
        {
            Content = "Apply",
            Style = (Style)Application.Current.FindResource("Accent"),
            Padding = new Thickness(18, 6, 18, 6),
            IsDefault = true,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 430,
            Content = list,
        };

        var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 620 };
        panel.Children.Add(new TextBlock { Text = "Profile assigner", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Text = "Press a button on a stick: its row lights up. Then choose the profile that stick should have. " +
                   $"Active profile: “{c.ActiveSetup.Name}”.",
        });
        panel.Children.Add(header);
        panel.Children.Add(empty);
        panel.Children.Add(scroll);
        panel.Children.Add(warn);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Profile assigner",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = B("BgBrush"),
            ShowInTaskbar = false,
        };

        void Validate()
        {
            var chosen = rows.Where(r => r.Combo.SelectedItem is ControllerProfile)
                .GroupBy(r => ((ControllerProfile)r.Combo.SelectedItem).Id).ToList();
            var dup = chosen.Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            warn.Text = dup.Count > 0
                ? "A profile can only belong to one stick. Two sticks have the same profile selected."
                : "";
            apply.IsEnabled = dup.Count == 0 && chosen.Count > 0;
        }
        // Rows built early did not see profiles that were offered to later rows; refresh them all.
        foreach (var r in rows)
        {
            var sel = r.Combo.SelectedItem;
            r.Combo.ItemsSource = profiles.ToList();
            r.Combo.SelectedItem = sel;
        }

        // Picking a profile another stick holds swaps the two: that stick gets what this one had.
        var previous = rows.ToDictionary(r => r, r => r.Combo.SelectedItem as ControllerProfile);
        var swapping = false;
        foreach (var r in rows)
        {
            r.Combo.SelectionChanged += (_, _) =>
            {
                if (swapping) return;
                var now = r.Combo.SelectedItem as ControllerProfile;
                var before = previous[r];
                previous[r] = now;
                if (now != null)
                {
                    var other = rows.FirstOrDefault(o => o != r && ReferenceEquals(o.Combo.SelectedItem, now));
                    if (other != null)
                    {
                        swapping = true;
                        other.Combo.SelectedItem = before;
                        previous[other] = before;
                        swapping = false;
                    }
                }
                Validate();
            };
        }
        Validate();

        // Light up the row of the stick that was pressed.
        void OnInput(InputEvent e, StickRecord? _)
        {
            if (!e.Pressed) return;
            win.Dispatcher.BeginInvoke(() =>
            {
                var row = rows.FirstOrDefault(r => r.Device.InstanceGuid == e.InstanceGuid);
                if (row == null) return;
                row.LitUntil = DateTime.UtcNow.AddSeconds(2);
                row.Card.BringIntoView();
            });
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            foreach (var r in rows)
            {
                bool lit = DateTime.UtcNow < r.LitUntil;
                r.Card.Background = lit ? B("AccentDimBrush") : B("Panel2Brush");
                r.Card.BorderBrush = lit ? B("AccentBrush") : B("BorderBrush");
            }
        };
        c.InputSeen += OnInput;
        timer.Start();
        win.Closed += (_, _) => { c.InputSeen -= OnInput; timer.Stop(); };

        bool applied = false;
        apply.Click += (_, _) =>
        {
            var pairs = rows.Where(r => r.Combo.SelectedItem is ControllerProfile)
                .Select(r => (r.Device, ((ControllerProfile)r.Combo.SelectedItem).Id)).ToList();
            var running = ScLocator.IsGameRunning();
            var msg = "Star Citizen still has the old stick numbers in its controls file. After this, the stick " +
                      "bindings only work correctly once you press Deploy (marked red in Bindings)." +
                      (running ? "\n\nStar Citizen is running right now: close it before you deploy." : "") +
                      "\n\nApply the assignment?";
            if (MessageBox.Show(win, msg, "Deploy needed", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            c.ApplyStickAssignments(pairs);
            applied = true;
            win.DialogResult = true;
        };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
        return applied;
    }
}
