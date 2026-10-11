using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Model;

namespace ScMapper.App.Views;

public sealed record StickRow(object Key, string Name, string Sub, string Badge, Brush Dot, bool IsDevice)
{
    public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record DeviceChoice(DeviceInfo Device, string Label)
{
    public override string ToString() => Label;
}

public sealed record InstanceChoice(int? N, string Label)
{
    public override string ToString() => Label;
}

public partial class SticksView : UserControl
{
    private AppController? _c;
    private bool _loading;
    private object? _selectedKey;
    private string? _selectedInput;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public SticksView()
    {
        InitializeComponent();
        ButtonsGrid.ButtonClicked += n => { _selectedInput = $"button{n}"; ShowInspector(); };
        _timer.Tick += (_, _) => Tick();
    }

    public void SelectInput(string input)
    {
        _selectedInput = input;
        ShowInspector();
    }

    /// <summary>Selects the n-th row (used by screenshot/test automation).</summary>
    public void SelectIndex(int index)
    {
        if (StickPicker.ItemsSource is List<StickRow> rows && index >= 0 && index < rows.Count)
        {
            _selectedKey = rows[index].Key;
            Rebuild();
        }
    }

    private StickRecord? SelectedStick => _selectedKey is Guid id && _c != null ? _c.Store.Data.Sticks.FirstOrDefault(s => s.Id == id) : null;
    private DeviceInfo? SelectedDevice => _selectedKey is DeviceInfo d ? d : null;

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        c.InputSeen += OnInputSeen;
        Rebuild();
        _timer.Start();
    }

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    private void Rebuild()
    {
        if (_c == null) return;
        _loading = true;
        var rows = new List<StickRow>();
        foreach (var s in _c.Store.Data.Sticks)
        {
            bool on = _c.IsConnected(s);
            var model = string.IsNullOrWhiteSpace(s.ProductName) ? "Unknown model" : s.ProductName.Trim();
            var sub = on ? (model == s.Name ? "" : model) : "Not connected";
            if (sub.Length == 0) sub = $"{s.Buttons} buttons · {s.Axes} axes";
            rows.Add(new StickRow(s.Id, s.Name, sub,
                s.ScInstance is { } n ? $"js{n}" : "", on ? B("GoodBrush") : B("WarnBrush"), false));
        }
        foreach (var d in _c.Match.Unmatched)
            rows.Add(new StickRow(d, d.ProductName.Trim(), $"New device · {d.Vid:X4}:{d.Pid:X4}",
                $"js{d.DefaultScInstance}", B("AccentBrush"), true));
        StickPicker.ItemsSource = rows;
        var sel = rows.FirstOrDefault(r => Equals(r.Key, _selectedKey))
                  ?? rows.FirstOrDefault(r => r.Key is DeviceInfo di && _selectedKey is DeviceInfo old && di.InstanceGuid == old.InstanceGuid)
                  ?? rows.FirstOrDefault();
        StickPicker.SelectedItem = sel;
        int online = _c.Store.Data.Sticks.Count(_c.IsConnected);
        int fresh = _c.Match.Unmatched.Count;
        CountText.Text = $"{online} connected" + (fresh > 0 ? $" · {fresh} new" : "");
        _selectedKey = sel?.Key;
        _loading = false;
        ShowSelected();
    }

    private void StickPicker_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _selectedKey = (StickPicker.SelectedItem as StickRow)?.Key;
        _selectedInput = null;
        ShowSelected();
    }

    private void ShowSelected()
    {
        if (_c == null) return;
        _loading = true;
        var stick = SelectedStick;
        var dev = SelectedDevice;
        EmptyHint.Visibility = stick == null && dev == null ? Visibility.Visible : Visibility.Collapsed;
        StickPanel.Visibility = stick != null ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = dev != null ? Visibility.Visible : Visibility.Collapsed;

        if (dev != null)
        {
            DevTitle.Text = dev.ProductName.Trim();
            DevSub.Text = $"{dev.Buttons} buttons · {dev.Axes} axes · {dev.Hats} hats · would be js{dev.DefaultScInstance} · {dev.Vid:X4}:{dev.Pid:X4}";
            var missing = _c.Match.Missing;
            LinkCombo.ItemsSource = missing;
            LinkCombo.SelectedIndex = missing.Count > 0 ? 0 : -1;
            LinkBox.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (stick != null)
        {
            var d = _c.DeviceFor(stick);
            var profile = _c.Store.Profile(stick.ProfileId);
            var profileName = profile?.Name ?? "";
            if (NameBox.Text != profileName) NameBox.Text = profileName;
            NameBox.IsEnabled = profile == null || !profile.IsSystem;
            SaveNameBtn.Content = "Save";
            StatusDot.Fill = d != null ? B("GoodBrush") : B("WarnBrush");
            StatusText.Text = d != null ? "Connected" : "Not connected";
            ModelText.Text = $"Stick: {stick.Name} · " +
                             $"{stick.Buttons} buttons · {stick.Axes} axes · {stick.Hats} hats · {stick.Vid:X4}:{stick.Pid:X4}";
            ProfileText.Text = profile != null ? "Profile name above - pick another profile in Profile assigner" : "No profile yet - type a name above to create one";
            var choices = new List<InstanceChoice>
            {
                new(null, d != null ? $"Auto (js{d.DefaultScInstance})" : "Auto"),
            };
            choices.AddRange(Enumerable.Range(1, 16).Select(i => new InstanceChoice(i, $"js{i}")));
            InstanceCombo.ItemsSource = choices;
            InstanceCombo.SelectedItem = stick.ScInstanceManual && stick.ScInstance is { } cur
                ? choices.FirstOrDefault(x => x.N == cur) : choices[0];
            RemapBox.Visibility = d == null ? Visibility.Visible : Visibility.Collapsed;
            RemapCombo.ItemsSource = _c.Match.Unmatched.Select(u => new DeviceChoice(u,
                $"{u.ProductName.Trim()} · {u.Vid:X4}:{u.Pid:X4} · {u.Buttons} btn · js{u.DefaultScInstance}")).ToList();
            RemapCombo.SelectedIndex = RemapCombo.Items.Count > 0 ? 0 : -1;
            ButtonsGrid.ButtonCount = stick.Buttons;
            ShowInspector();
        }
        _loading = false;
    }

    private void ShowInspector()
    {
        if (_c == null) return;
        var stick = SelectedStick;
        if (stick == null) return;
        Inspector.Show(_c, stick, _selectedInput);
        if (_selectedInput != null && _selectedInput.StartsWith("button") && int.TryParse(_selectedInput[6..], out var n))
            ButtonsGrid.Selected = n;
    }

    private long _conflictAt;
    private List<(Guid Stick, string Input)> _conflictRows = new();

    private void Tick()
    {
        if (_c == null || SelectedStick is not { } s) return;
        var d = _c.DeviceFor(s);
        if (d == null) { ButtonsGrid.SetLive(Array.Empty<bool>(), new HashSet<int>()); Axes.Update(null, null); return; }
        var st = _c.Hub.GetState(d.InstanceGuid);
        if (st == null) return;
        var prof = _c.Store.Profile(s.ProfileId);
        var marked = new HashSet<int>();
        var replaced = new HashSet<int>();
        var conflicts = new HashSet<int>();
        static bool Number(string k, out int n)
        {
            n = 0;
            return k.StartsWith("button") && int.TryParse(k[6..], out n);
        }
        if (prof != null)
        {
            foreach (var k in prof.Rules.Keys.Concat(prof.Actions.Select(a => a.Input)).Concat(prof.Binds.Select(b => ScMapper.Core.Sc.ScInputCodes.SplitJsInput(b.Input).Input)))
                if (Number(k, out var n)) marked.Add(n);
            foreach (var k in prof.Actions.Select(a => a.Input))
                if (Number(k, out var n)) replaced.Add(n);
            // Looking through the game's catalog is not free and Tick runs often: refresh twice a second.
            if (Environment.TickCount64 - _conflictAt > 500)
            {
                _conflictAt = Environment.TickCount64;
                _conflictRows = _c.FindConflicts().Where(x => x.Kind == ScMapper.Core.ConflictKind.DirectBinding)
                    .Select(x => (x.Stick.Id, x.Input)).ToList();
            }
            foreach (var (stickId, input) in _conflictRows)
                if (stickId == s.Id && Number(input, out var n)) conflicts.Add(n);
        }
        ButtonsGrid.SetLive((bool[])st.Buttons.Clone(), marked, replaced, conflicts);
        Axes.Update(d, st);
    }

    private readonly List<string> _events = new();

    private void OnInputSeen(ScMapper.Core.Input.InputEvent e, StickRecord? stick)
    {
        var name = stick?.Name ?? _c?.Hub.Devices.FirstOrDefault(d => d.InstanceGuid == e.InstanceGuid)?.ProductName.Trim() ?? "?";
        var line = $"{e.Utc.ToLocalTime():HH:mm:ss.f}  {name,-28} {Controls.InputNames.Pretty(e.Input),-14} {(e.Pressed ? "pressed" : "released")}";
        Dispatcher.BeginInvoke(() =>
        {
            _events.Insert(0, line);
            if (_events.Count > 10) _events.RemoveAt(10);
            EventLog.ItemsSource = _events.ToList();
            NoEvents.Visibility = Visibility.Collapsed;
        });
        if (!e.Pressed || stick == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) return;
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
            _selectedInput = e.Input;
            if (!Equals(_selectedKey, stick.Id))
            {
                _selectedKey = stick.Id;
                Rebuild();
            }
            else ShowInspector();
        });
    }

    // ---- editing ---------------------------------------------------------------------------

    private void NameBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_loading) return;
        SaveNameBtn.Content = "Save";
    }

    private void NameBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveName();
        e.Handled = true;
    }

    private void SaveName_Click(object s, RoutedEventArgs e) => SaveName();

    // Clicking anywhere outside a text box releases its focus, so the field can be left with the mouse.
    private void View_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Keyboard.FocusedElement is TextBox tb && !IsInside(d, tb))
            Keyboard.ClearFocus();
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    private void SaveName()
    {
        if (SelectedStick is not { } st || _c == null) return;
        // The box names the stick's profile; the stick itself keeps its device name.
        var profile = _c.EnsureProfile(st);
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Text = profile.Name; return; }
        _c.RenameProfile(profile, name);
        NameBox.Text = profile.Name;
        SaveNameBtn.Content = "✓ Saved";
        Keyboard.ClearFocus();
    }

    private void ProfileAssigner_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        Controls.ProfileAssignerDialog.Show(Window.GetWindow(this), _c);
        Controls.ConflictDialog.ShowIfAny(Window.GetWindow(this), _c);
    }

    private void ProfileManager_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        Controls.ProfileManagerDialog.Show(Window.GetWindow(this), _c);
    }

    private void InstanceCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedStick is not { } st || _c == null) return;
        if (InstanceCombo.SelectedItem is not InstanceChoice ch) return;
        if (ch.N is { } n)
        {
            st.ScInstanceManual = true;
            st.ScInstance = n;
        }
        else
        {
            st.ScInstanceManual = false;
            if (_c.DeviceFor(st) is { } d) st.ScInstance = d.DefaultScInstance;
        }
        _c.Touch();
    }

    private void AdvancedToggle_Click(object s, RoutedEventArgs e)
    {
        bool open = AdvancedBody.Visibility != Visibility.Visible;
        AdvancedBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedToggle.Content = (open ? "▾ " : "▸ ") + "Advanced";
    }

    private void EventsToggle_Click(object s, RoutedEventArgs e)
    {
        bool open = EventsBody.Visibility != Visibility.Visible;
        EventsBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        EventsToggle.Content = (open ? "▾ " : "▸ ") + "Recent events";
    }

    private void RemapAssign_Click(object s, RoutedEventArgs e)
    {
        if (SelectedStick is not { } st || _c == null) return;
        if (RemapCombo.SelectedItem is DeviceChoice ch)
        {
            _c.AssignDevice(st, ch.Device);
            Rebuild();
        }
    }

    private void CreateStick_Click(object s, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } d || _c == null) return;
        var rec = _c.AddStickFor(d);
        _selectedKey = rec.Id;
        Rebuild();
    }

    private void Link_Click(object s, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } d || _c == null) return;
        if (LinkCombo.SelectedItem is StickRecord st)
        {
            _c.AssignDevice(st, d);
            _selectedKey = st.Id;
            Rebuild();
        }
    }
}

