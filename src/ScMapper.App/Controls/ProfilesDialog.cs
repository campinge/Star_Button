using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Model;

namespace ScMapper.App.Controls;

/// <summary>
/// Sticks and profiles in one place. The rows are the PROFILES of the active setup ("Power und Comms"); each one
/// says which stick it is on and whether that stick is plugged in. A stick without a profile shows up on top and
/// waits for the user: nothing is created behind their back.
/// </summary>
public static class ProfilesDialog
{
    private sealed record DeviceChoice(DeviceInfo? Device, string Label)
    {
        public override string ToString() => Label;
    }

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    private static string DeviceLabel(DeviceInfo d) => $"JS{d.DefaultScInstance} · {d.ProductName.Trim()}";

    /// <summary>
    /// A profile that does not suit a stick is a reason to ask, never to refuse: a broken button or a
    /// replacement of another model is legitimate. Returns true when the profile may go on the stick.
    /// </summary>
    internal static bool ConfirmFit(Window? owner, AppController c, ControllerProfile p, DeviceInfo d)
    {
        var fit = c.CheckFit(p, d);
        if (!fit.NeedsConfirmation) return true;
        string text;
        if (fit.Level == FitLevel.MissingInputs)
        {
            var missing = string.Join(", ", fit.Missing.Take(8).Select(InputNames.Pretty)) + (fit.Missing.Count > 8 ? ", …" : "");
            text = $"“{p.Name}” uses inputs this stick does not have:\n\n    {missing}\n\n" +
                   $"{d.ProductName.Trim()} has {d.Buttons} buttons and {d.Hats} hat(s). Those bindings will do nothing.";
        }
        else
        {
            var o = fit.Origin!;
            text = $"“{p.Name}” was made for a {o.ProductName} ({o.Buttons} buttons); this is a {d.ProductName.Trim()} " +
                   $"({d.Buttons} buttons). The buttons are probably not where the profile expects them.";
        }
        return Msg.Show(text + "\n\nUse the profile on this stick anyway?", "Profile does not fit this stick",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public static void Show(Window? owner, AppController c)
    {
        var list = new StackPanel();
        var intro = new TextBlock { Style = S("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        var hint = new TextBlock { Style = S("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var cards = new List<(Border Card, Guid? Device)>();
        var lit = new Dictionary<Guid, DateTime>();
        var building = false;
        OverlayWindow win = null!;

        void Assign(ControllerProfile p, DeviceInfo d)
        {
            if (!ConfirmFit(owner, c, p, d)) return;
            c.ApplyStickAssignments(new[] { (d, p.Id) });
        }

        TextBlock Header(string text, Thickness margin) => new() { Style = S("H2"), Text = text, Margin = margin };

        Border Card(Guid? device, UIElement left, UIElement? right)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(left);
            if (right != null)
            {
                Grid.SetColumn(right, 1);
                g.Children.Add(right);
            }
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Background = B("Panel2Brush"),
                BorderBrush = B("BorderBrush"),
                Child = g,
            };
            cards.Add((card, device));
            return card;
        }

        void Rebuild()
        {
            building = true;
            list.Children.Clear();
            cards.Clear();
            var setup = c.ActiveSetup;
            var slots = c.ProfileSlots();
            var waiting = c.UnassignedDevices;
            var devices = c.Hub.Devices.Where(d => d.HasJsNumber).OrderBy(d => d.EnumIndex).ToList();

            intro.Text = "A profile sits on one stick. An unplugged stick makes its profile inactive: kept, but not deployed.";

            // ---- sticks without a profile -------------------------------------------------------------
            if (waiting.Count > 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = waiting.Count == 1 ? "⚠  1 STICK HAS NO PROFILE" : $"⚠  {waiting.Count} STICKS HAVE NO PROFILE",
                    FontSize = 17, FontWeight = FontWeights.Bold, Foreground = B("WarnBrush"), Margin = new Thickness(0, 4, 0, 4),
                });
                list.Children.Add(new TextBlock
                {
                    Text = "Without a profile a stick does nothing in Star Citizen. Give it one below: link it to a profile " +
                           "that is waiting, or make a new one.",
                    Style = S("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                });
                var free = slots.Where(s => !s.IsConnected).Select(s => s.Profile).ToList();
                foreach (var d in waiting)
                {
                    var title = new TextBlock { Text = DeviceLabel(d), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                    var sub = new TextBlock
                    {
                        Style = S("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
                        Text = "No profile yet. Press a button on the stick to find its row.",
                    };
                    var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    if (free.Count > 0)
                    {
                        var combo = new ComboBox { ItemsSource = free, MinWidth = 230, MinHeight = 36, Margin = new Thickness(12, 0, 8, 0) };
                        // Exactly one waiting profile made for this very model: offer it, the user still confirms.
                        var same = free.Where(p => c.OriginOf(p) is { } o && o.SameModel(d.Vid, d.Pid)).ToList();
                        if (same.Count == 1) combo.SelectedItem = same[0];
                        var assign = new Button
                        {
                            Content = "Assign", Style = S("Accent"), MinHeight = 36, Padding = new Thickness(16, 4, 16, 4),
                            IsEnabled = combo.SelectedItem != null,
                        };
                        combo.SelectionChanged += (_, _) => assign.IsEnabled = combo.SelectedItem != null;
                        assign.Click += (_, _) => { if (combo.SelectedItem is ControllerProfile p) Assign(p, d); };
                        right.Children.Add(combo);
                        right.Children.Add(assign);
                    }
                    else
                    {
                        var create = new Button
                        {
                            Content = $"+ New profile “{d.ProductName.Trim()}”", Style = S("Accent"), MinHeight = 36,
                            Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(12, 0, 0, 0),
                        };
                        create.Click += (_, _) => c.CreateProfile(device: d);
                        right.Children.Add(create);
                    }
                    var urgent = Card(d.InstanceGuid, new StackPanel { Children = { title, sub } }, right);
                    urgent.Tag = "urgent";
                    urgent.BorderThickness = new Thickness(2);
                    urgent.Padding = new Thickness(16, 14, 16, 14);
                    list.Children.Add(urgent);
                }
                if (waiting.Count > 1 && free.Count == 0)
                {
                    var all = new Button
                    {
                        Content = $"Create a profile for each of these {waiting.Count} sticks", HorizontalAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 0, 6),
                        ToolTip = "One profile per stick, named after its model. Identical sticks get a number: rename them afterwards.",
                    };
                    all.Click += (_, _) => c.RegisterNewDevices();
                    list.Children.Add(all);
                }
            }

            // ---- profiles ----------------------------------------------------------------------------
            // No free-standing "new profile" button: a profile is created for a stick that has none (above).
            list.Children.Add(Header("PROFILES", new Thickness(0, 14, 0, 8)));

            if (slots.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Style = S("Muted"), TextWrapping = TextWrapping.Wrap,
                    Text = devices.Count == 0
                        ? "No stick connected and no profile yet. Plug in a joystick: it gets its profile here."
                        : "No profiles yet. Give each stick above a profile.",
                });

            foreach (var slot in slots)
            {
                var p = slot.Profile;
                var entries = p.Binds.Count + p.Rules.Count + p.Actions.Count;
                var name = new TextBox { Text = p.Name, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
                void Commit()
                {
                    if (name.Text.Trim() == p.Name) return;
                    c.RenameProfile(p, name.Text);
                    name.Text = p.Name;
                }
                name.LostFocus += (_, _) => Commit();
                name.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    Commit();
                    Keyboard.ClearFocus();
                    e.Handled = true;
                };
                string state = slot.IsConnected ? "● " + DeviceLabel(slot.Device!)
                    : slot.HasNoDevice ? "○ no stick yet: choose one on the right"
                    : "○ " + (string.IsNullOrWhiteSpace(slot.Stick.ProductName) ? "stick" : slot.Stick.ProductName.Trim()) +
                      " is not plugged in: inactive, its bindings are kept but not deployed";
                var sub = new TextBlock
                {
                    Style = S("Muted"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
                    Foreground = slot.IsConnected ? B("GoodBrush") : B("WarnBrush"),
                    Text = $"{state}  ·  {entries} {(entries == 1 ? "entry" : "entries")}",
                };

                var choices = new List<DeviceChoice> { new(null, "— no stick —") };
                foreach (var d in devices)
                {
                    var holder = slots.FirstOrDefault(s => s.Device?.InstanceGuid == d.InstanceGuid);
                    var label = DeviceLabel(d) + (holder != null && holder.Profile.Id != p.Id ? $"   (now: {holder.Profile.Name})" : "");
                    choices.Add(new DeviceChoice(d, label));
                }
                var combo = new ComboBox { ItemsSource = choices, MinWidth = 300, MinHeight = 34, Margin = new Thickness(12, 0, 8, 0) };
                combo.SelectedItem = slot.Device == null ? choices[0] : choices.FirstOrDefault(x => x.Device?.InstanceGuid == slot.Device.InstanceGuid);
                combo.SelectionChanged += (_, _) =>
                {
                    if (building || combo.SelectedItem is not DeviceChoice ch) return;
                    if (ch.Device == null)
                    {
                        if (slot.IsConnected || !slot.HasNoDevice) c.DetachDevice(slot.Stick);
                        return;
                    }
                    if (slot.Device?.InstanceGuid == ch.Device.InstanceGuid) return;
                    Assign(p, ch.Device);
                    Rebuild();   // a refused change puts the old choice back
                };

                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                right.Children.Add(combo);
                list.Children.Add(Card(slot.Device?.InstanceGuid, new StackPanel { Children = { name, sub } }, right));
            }

            var pending = c.PendingChanges();
            hint.Text = pending is > 0
                ? "Star Citizen's file differs from these profiles. Press Deploy in the Bindings tab to write the change " +
                  "(sticks that are unplugged lose their bindings in the file; they come back when plugged in and deployed)."
                : "";
            building = false;
        }

        var close = new Button
        {
            Content = "Close", IsCancel = true, IsDefault = true, Padding = new Thickness(18, 6, 18, 6),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(20), Width = 860 };
        panel.Children.Add(new TextBlock { Text = "Sticks and profiles", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(intro);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520, Content = list });
        panel.Children.Add(hint);
        panel.Children.Add(close);

        win = new OverlayWindow { Content = panel };

        // Light up the card of the stick whose button was pressed.
        void OnInput(ScMapper.Core.Input.InputEvent e, StickRecord? _)
        {
            if (!e.Pressed) return;
            win.Dispatcher.BeginInvoke(() =>
            {
                lit[e.InstanceGuid] = DateTime.UtcNow.AddSeconds(2);
                cards.FirstOrDefault(x => x.Device == e.InstanceGuid).Card?.BringIntoView();
            });
        }
        void OnChanged() => win.Dispatcher.BeginInvoke(() =>
        {
            // Never rebuild under a text box the user is typing in; its commit raises Changed again.
            if (Keyboard.FocusedElement is TextBox tb && tb.IsDescendantOf(list)) return;
            Rebuild();
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            foreach (var (card, dev) in cards)
            {
                bool on = dev is { } g && lit.TryGetValue(g, out var until) && DateTime.UtcNow < until;
                bool urgent = card.Tag is "urgent";
                card.Background = on ? B("AccentDimBrush") : urgent ? B("WarnDimBrush") : B("Panel2Brush");
                card.BorderBrush = on ? B("AccentBrush") : urgent ? B("WarnBrush") : B("BorderBrush");
            }
        };
        c.InputSeen += OnInput;
        c.Changed += OnChanged;
        timer.Start();
        win.Closed += (_, _) =>
        {
            c.InputSeen -= OnInput;
            c.Changed -= OnChanged;
            timer.Stop();
            c.CancelCapture();
        };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) win.Close(); };
        close.Click += (_, _) => win.Close();

        Rebuild();
        win.ShowDialog();
    }
}
