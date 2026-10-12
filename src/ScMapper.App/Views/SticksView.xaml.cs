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
        // The live view is only needed while it is on screen (not on another page, not hidden in the tray).
        IsVisibleChanged += (_, _) =>
        {
            if (_c == null) return;
            if (IsVisible) _timer.Start(); else _timer.Stop();
        };
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

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        c.InputSeen += OnInputSeen;
        Rebuild();
        if (IsVisible) _timer.Start();
    }

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    private void Rebuild()
    {
        if (_c == null) return;
        _loading = true;
        // The list is the PROFILES that have a stick plugged in. A profile without a stick lies in wait: it is
        // given a stick with the Profiles button and shows up here from then on.
        var rows = new List<StickRow>();
        var slots = _c.ProfileSlots();
        foreach (var slot in slots.Where(x => x.IsConnected))
        {
            var s = slot.Stick;
            var model = string.IsNullOrWhiteSpace(s.ProductName) ? "stick" : s.ProductName.Trim();
            rows.Add(new StickRow(s.Id, slot.Profile.Name, $"Stick: {model}",
                s.ScInstance is { } n ? $"js{n}" : "", B("GoodBrush"), false));
        }
        StickPicker.ItemsSource = rows;
        StickPicker.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sel = rows.FirstOrDefault(r => Equals(r.Key, _selectedKey)) ?? rows.FirstOrDefault();
        StickPicker.SelectedItem = sel;
        int inactive = slots.Count - rows.Count;
        CountText.Text = slots.Count == 0 ? "No profiles yet"
            : $"{rows.Count} active profile{(rows.Count == 1 ? "" : "s")}" +
              (inactive > 0 ? $" · {inactive} without a stick (Profiles button)" : "");
        _selectedKey = sel?.Key;
        _loading = false;
        UpdateNoProfileHint();
        ShowSelected();
    }

    // ---- sticks without a profile ---------------------------------------------------------------
    // Nothing is offered or created here: the Profiles button is the one place for that. This page only says
    // that a stick is waiting, next to the button, and makes the button glow.

    private bool _hinting;

    private void UpdateNoProfileHint()
    {
        if (_c == null) return;
        int waiting = _c.UnassignedDevices.Count;
        bool on = waiting > 0;
        NoProfileToken.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        NoProfileText.Text = waiting == 1 ? "1 stick without a profile" : $"{waiting} sticks without a profile";
        if (on == _hinting) return;
        _hinting = on;
        if (on)
        {
            ProfilesBtn.Style = (Style)Application.Current.FindResource("Accent");
            Controls.Glow.Start(ProfilesBtn);
            ProfilesBtn.ToolTip = "Give the waiting stick a profile, or choose which stick each profile is on";
        }
        else
        {
            ProfilesBtn.ClearValue(StyleProperty);
            Controls.Glow.Stop(ProfilesBtn);
            ProfilesBtn.ToolTip = "Add, rename or delete profiles and choose which stick each one is on";
        }
    }

    /// <summary>The start screen is done: show the profile it made last.</summary>
    public void AfterOnboarding(string? lastName, int made = 1)
    {
        if (_c == null) return;
        if (lastName != null && _c.ProfileSlots().FirstOrDefault(x => x.Profile.Name == lastName) is { } slot)
            _selectedKey = slot.Stick.Id;
        Rebuild();
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
        EmptyHint.Text = _c.UnassignedDevices.Count > 0
            ? "A stick has no profile yet. Use the Profiles button to give it one."
            : _c.ProfileSlots().Count > 0
                ? "None of your profiles has a stick right now. Use the Profiles button to link a stick to a profile."
                : "Connect a joystick: it gets its profile with the Profiles button.";
        EmptyHint.Visibility = stick == null ? Visibility.Visible : Visibility.Collapsed;
        StickPanel.Visibility = stick != null ? Visibility.Visible : Visibility.Collapsed;

        if (stick != null)
        {
            var d = _c.DeviceFor(stick);
            var profile = _c.Store.Profile(stick.ProfileId);
            var profileName = profile?.Name ?? "";
            if (NameBox.Text != profileName) NameBox.Text = profileName;
            NameBox.IsEnabled = profile == null || !profile.IsSystem;
            SaveNameBtn.Content = "Save";
            bool noStick = d == null && stick.Vid == 0 && stick.Pid == 0 && stick.InstanceGuid == null;
            StatusDot.Fill = d != null ? B("GoodBrush") : B("WarnBrush");
            StatusText.Text = d != null ? "Connected" : noStick ? "No stick yet" : "Not connected · inactive";
            RemapText.Text = noStick ? "No stick yet. Which stick is it?" : "Not connected. Which device is it now?";
            StatusText.ToolTip = $"{stick.Name} · {stick.Buttons} buttons · {stick.Axes} axes · {stick.Hats} hats · " +
                                 $"{stick.Vid:X4}:{stick.Pid:X4}";
            NameBox.ToolTip = profile != null ? "Name of this stick's profile (Enter saves). Another profile on this stick: Profiles button."
                : "No profile yet - type a name to create one";
            RemapBox.Visibility = d == null ? Visibility.Visible : Visibility.Collapsed;
            RemapCombo.ItemsSource = _c.UnassignedDevices.Select(u => new DeviceChoice(u,
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
        var name = (stick != null && _c != null ? _c.StickLabel(stick) : null) ?? _c?.Hub.Devices.FirstOrDefault(d => d.InstanceGuid == e.InstanceGuid)?.ProductName.Trim() ?? "?";
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

        // A click on the free space of the right-hand column (not the inspector, the events card or the
        // scrollbar) drops the button selection.
        if (e.OriginalSource is System.Windows.Media.Visual v && IsInside(v, RightScroll)
            && !IsInside(v, Inspector) && !IsInside(v, EventsCard)
            && !IsInsideScrollBar(v) && _selectedInput != null)
            ClearSelection();
    }

    private static bool IsInsideScrollBar(DependencyObject d)
    {
        for (; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    private void ClearSelection()
    {
        _selectedInput = null;
        ButtonsGrid.Selected = -1;
        if (_c != null && SelectedStick is { } st) Inspector.Show(_c, st, null);
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
        var profile = _c.Store.Profile(st.ProfileId);
        if (profile == null || profile.IsSystem) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Text = profile.Name; return; }
        _c.RenameProfile(profile, name);
        NameBox.Text = profile.Name;
        SaveNameBtn.Content = "✓ Saved";
        Keyboard.ClearFocus();
    }

    private void Profiles_Click(object s, RoutedEventArgs e) => OpenProfiles();

    /// <summary>The profile list (rename, delete, which stick is on which); afterwards conflicts are checked again.</summary>
    public void OpenProfiles()
    {
        if (_c == null) return;
        Controls.ProfilesDialog.Show(Window.GetWindow(this), _c);
        Controls.ConflictDialog.ShowIfAny(Window.GetWindow(this), _c);
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
        if (RemapCombo.SelectedItem is DeviceChoice ch && _c.Store.Profile(st.ProfileId) is { } profile)
        {
            if (!Controls.ProfilesDialog.ConfirmFit(Window.GetWindow(this), _c, profile, ch.Device)) return;
            _c.ApplyStickAssignments(new[] { (ch.Device, profile.Id) });
            Rebuild();
        }
    }
}

