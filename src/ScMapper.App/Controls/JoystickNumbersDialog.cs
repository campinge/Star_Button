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
/// The js numbers Star Citizen uses for the sticks: what its controls file lists, what Star Button maps, and the
/// tools to bring the two together (swap two numbers, take them over from the file, follow Windows' order).
/// </summary>
public static class JoystickNumbersDialog
{
    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>Returns true when any number was changed.</summary>
    public static bool Show(Window? owner, AppController c, int? preselect = null)
    {
        bool changed = false;
        var list = new StackPanel();
        var rowCards = new Dictionary<int, Border>();
        var litUntil = new Dictionary<int, DateTime>();
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = B("GoodBrush") };
        var preview = new TextBlock { Style = S("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        var banner = new Border
        {
            Background = B("WarnDimBrush"), BorderBrush = B("WarnBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = B("WarnBrush") },
        };

        var fromBox = new ComboBox { MinHeight = 38, MinWidth = 90, ItemStringFormat = "js{0}" };
        var toBox = new ComboBox { MinHeight = 38, MinWidth = 90, ItemStringFormat = "js{0}" };
        var swapBtn = new Button
        {
            Content = "⇄  Swap", Style = S("Accent"), MinHeight = 38, MinWidth = 110, Margin = new Thickness(12, 0, 0, 0),
        };

        void Header(Grid g, params string[] cols)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                var t = new TextBlock { Style = S("H2"), Text = cols[i] };
                Grid.SetColumn(t, i * 2);
                g.Children.Add(t);
            }
        }
        Grid Cols()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 210 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 210 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            return g;
        }

        void UpdatePreview()
        {
            if (fromBox.SelectedItem is not int a || toBox.SelectedItem is not int b || a == b)
            {
                preview.Text = "";
                swapBtn.IsEnabled = false;
                return;
            }
            var moves = c.PreviewJsSwap(a, b);
            swapBtn.IsEnabled = moves.Count > 0;
            preview.Text = moves.Count == 0
                ? $"No stick uses js{a} or js{b}: nothing to swap."
                : string.Join("\n", moves.Select(m =>
                    $"{c.StickLabel(m.Stick)}:  js{m.From} → js{m.To}   ({m.Binds} binding{(m.Binds == 1 ? "" : "s")})"));
        }

        void Refresh()
        {
            list.Children.Clear();
            rowCards.Clear();
            var file = TryFile(c);
            var slots = c.JsSlots(file);
            var head = Cols();
            Header(head, "NUMBER", "IN STAR CITIZEN'S FILE", "STICK IN STAR BUTTON", "STATUS");
            head.Margin = new Thickness(12, 0, 12, 6);
            list.Children.Add(head);

            bool anyAttention = false;
            foreach (var slot in slots)
            {
                var state = slot.Sticks.Count == 1 ? c.JsState(slot.Sticks[0], file) : null;
                string statusText;
                Brush statusBrush;
                if (slot.IsDoubled)
                {
                    statusText = "✗ two sticks use this number";
                    statusBrush = B("BadBrush");
                    anyAttention = true;
                }
                else if (state == null)
                {
                    statusText = slot.GameProduct != null ? "no stick mapped" : "free";
                    statusBrush = B("MutedBrush");
                }
                else
                {
                    switch (state.Status)
                    {
                        case JsStatus.Matches:
                            statusText = "✓ matches the file"; statusBrush = B("GoodBrush"); break;
                        case JsStatus.MatchesAmbiguous:
                            statusText = "? same model more than once: check in game"; statusBrush = B("WarnBrush"); break;
                        case JsStatus.DiffersFromGame:
                            statusText = "≠ differs from the file"; statusBrush = B("WarnBrush"); anyAttention = true; break;
                        default:
                            statusText = "no file to compare"; statusBrush = B("MutedBrush"); break;
                    }
                    if (state.WindowsDiffers)
                    {
                        statusText += $"\nWindows order now says js{state.WindowsNumber}";
                        statusBrush = B("WarnBrush");
                        anyAttention = true;
                    }
                }

                var g = Cols();
                var num = new TextBlock { Text = $"js{slot.Number}", FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
                var gameCell = new TextBlock
                {
                    Text = AppController.ProductDisplayName(slot.GameProduct) ?? "—",
                    Style = S("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var stickCell = new TextBlock
                {
                    Text = slot.Sticks.Count == 0 ? "—" : string.Join("  +  ", slot.Sticks.Select(s => c.StickLabel(s))),
                    FontWeight = slot.Sticks.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var statusCell = new TextBlock
                {
                    Text = statusText, Foreground = statusBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(gameCell, 2);
                Grid.SetColumn(stickCell, 4);
                Grid.SetColumn(statusCell, 6);
                g.Children.Add(num);
                g.Children.Add(gameCell);
                g.Children.Add(stickCell);
                g.Children.Add(statusCell);

                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Background = B("Panel2Brush"),
                    BorderBrush = B("BorderBrush"),
                    Child = g,
                    Cursor = Cursors.Hand,
                    ToolTip = "Click to pick this number for the swap",
                };
                int n = slot.Number;
                card.MouseLeftButtonUp += (_, _) =>
                {
                    // First click fills "from", the next one "to".
                    if (fromBox.SelectedItem is not int) fromBox.SelectedItem = n;
                    else if (toBox.SelectedItem is not int || (int)fromBox.SelectedItem == n) toBox.SelectedItem = n;
                    else { fromBox.SelectedItem = (int)toBox.SelectedItem; toBox.SelectedItem = n; }
                };
                rowCards[slot.Number] = card;
                list.Children.Add(card);
            }

            var numbers = slots.Select(s => s.Number).ToList();
            var oldFrom = fromBox.SelectedItem;
            var oldTo = toBox.SelectedItem;
            fromBox.ItemsSource = numbers.ToList();
            toBox.ItemsSource = numbers.ToList();
            if (oldFrom is int f && numbers.Contains(f)) fromBox.SelectedItem = f;
            if (oldTo is int t && numbers.Contains(t)) toBox.SelectedItem = t;

            ((TextBlock)banner.Child).Text = file == null
                ? "No Star Citizen controls file was found, so the numbers cannot be compared with the game."
                : "Some numbers differ from Star Citizen's file or from Windows' order. Re-plugged since you last played? " +
                  "Then “Follow Windows order” is right. Otherwise take the numbers from the file or swap them below.";
            banner.Visibility = file == null || anyAttention ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreview();
        }

        fromBox.SelectionChanged += (_, _) => UpdatePreview();
        toBox.SelectionChanged += (_, _) => UpdatePreview();

        swapBtn.Click += (_, _) =>
        {
            if (fromBox.SelectedItem is not int a || toBox.SelectedItem is not int b) return;
            var moves = c.SwapJsNumbers(a, b);
            if (moves.Count == 0) return;
            changed = true;
            fromBox.SelectedItem = null;
            toBox.SelectedItem = null;
            Refresh();
            note.Text = $"Swapped js{a} and js{b}. Deploy (Bindings tab) writes the new numbers into Star Citizen.";
        };

        var takeBtn = new Button
        {
            Content = "Take numbers from Star Citizen's file", MinHeight = 38, Padding = new Thickness(14, 6, 14, 6),
            ToolTip = "Pairs your sticks with the numbers Star Citizen listed at its last run, by model",
        };
        takeBtn.Click += (_, _) =>
        {
            var file = TryFile(c);
            if (file == null)
            {
                Msg.Show("No Star Citizen controls file (actionmaps.xml) was found. Start the game once or pick " +
                                "the installation in Settings.", "Joystick numbers", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var plan = c.PlanJsFromGame(file);
            var diff = plan.Where(m => m.From != m.To).ToList();
            if (plan.Count == 0)
            {
                Msg.Show("None of your connected sticks is listed in Star Citizen's file.", "Joystick numbers",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (diff.Count == 0)
            {
                note.Text = "Everything already matches Star Citizen's file.";
                return;
            }
            var text = string.Join("\n", diff.Select(m =>
                $"{c.StickLabel(m.Stick)}:  {(m.From is { } f ? $"js{f}" : "no number")} → js{m.To}" + (m.Ambiguous ? "   (same model more than once)" : "")));
            var ambiguous = diff.Any(m => m.Ambiguous)
                ? "\n\nSticks of the same model cannot be told apart by the file; they were paired in Windows' order. " +
                  "Check in the game and swap them here if they are the wrong way round."
                : "";
            if (Msg.Show($"Take these numbers over?\n\n{text}{ambiguous}", "Joystick numbers",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            c.ApplyJsMoves(diff);
            changed = true;
            Refresh();
            note.Text = $"{diff.Count} stick(s) changed. Deploy (Bindings tab) writes the new numbers into Star Citizen.";
        };

        var followBtn = new Button
        {
            Content = "Follow Windows order", MinHeight = 38, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Drops all hand-set numbers: sticks are numbered like Star Citizen will after its next start",
        };
        followBtn.Click += (_, _) =>
        {
            c.FollowWindowsOrder();
            changed = true;
            Refresh();
            note.Text = "Numbers follow Windows' device order again. Deploy (Bindings tab) writes them into Star Citizen.";
        };

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinHeight = 38, Padding = new Thickness(20, 6, 20, 6), HorizontalAlignment = HorizontalAlignment.Right };

        var swapRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        swapRow.Children.Add(new TextBlock { Text = "Swap", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        swapRow.Children.Add(fromBox);
        swapRow.Children.Add(new TextBlock { Text = "with", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
        swapRow.Children.Add(toBox);
        swapRow.Children.Add(swapBtn);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        tools.Children.Add(takeBtn);
        tools.Children.Add(followBtn);

        // Fixed width: with SizeToContent the texts below would never wrap and the window would grow with them.
        var panel = new StackPanel { Margin = new Thickness(20), Width = 800 };
        panel.Children.Add(new TextBlock { Text = "Joystick numbers", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Style = S("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Star Citizen numbers your sticks js1, js2, … in the order Windows lists them. A binding is stored with " +
                   "that number, so it only works when Star Button uses the same number as the game. " +
                   "Press a button on a stick to find it in the list.",
        });
        panel.Children.Add(banner);
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380, Margin = new Thickness(0, 12, 0, 0), Content = list,
        });
        panel.Children.Add(new TextBlock { Style = S("H2"), Text = "SWAP TWO NUMBERS", Margin = new Thickness(0, 16, 0, 0) });
        panel.Children.Add(swapRow);
        panel.Children.Add(preview);
        panel.Children.Add(tools);
        panel.Children.Add(note);
        panel.Children.Add(new Border { Height = 12 });
        panel.Children.Add(close);

        var win = new OverlayWindow { Content = panel };

        // Pressing a button on a stick lights its row and picks its number for the swap.
        void OnInput(InputEvent e, StickRecord? stick)
        {
            if (!e.Pressed || stick?.ScInstance is not { } n) return;
            win.Dispatcher.BeginInvoke(() =>
            {
                if (!rowCards.TryGetValue(n, out var card)) return;
                litUntil[n] = DateTime.UtcNow.AddSeconds(2);
                card.BringIntoView();
                if (fromBox.SelectedItem is not int || toBox.SelectedItem is int) { fromBox.SelectedItem = n; toBox.SelectedItem = null; }
                else if ((int)fromBox.SelectedItem != n) toBox.SelectedItem = n;
            });
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            foreach (var (n, card) in rowCards)
            {
                bool lit = litUntil.TryGetValue(n, out var until) && DateTime.UtcNow < until;
                card.Background = lit ? B("AccentDimBrush") : B("Panel2Brush");
                card.BorderBrush = lit ? B("AccentBrush") : B("BorderBrush");
            }
        };
        c.InputSeen += OnInput;
        timer.Start();
        win.Closed += (_, _) => { c.InputSeen -= OnInput; timer.Stop(); };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };

        Refresh();
        if (preselect is { } p && rowCards.ContainsKey(p)) fromBox.SelectedItem = p;
        win.ShowDialog();
        return changed;
    }

    private static ActionMapsFile? TryFile(AppController c)
    {
        try { return c.LoadActionMaps(); }
        catch { return null; }
    }
}
