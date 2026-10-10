using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.App.Views;

public abstract class Notifying : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A binding shown as a tag. Kind is KB, PAD or JS; Stick/Input are set when it can light up live.</summary>
public sealed class TagVM : Notifying
{
    private bool _lit;
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public Guid? StickId { get; init; }
    public string? Input { get; init; }
    public bool Lit
    {
        get => _lit;
        set { if (_lit != value) { _lit = value; Raise(nameof(Lit)); } }
    }
}

public sealed class TreeRow : Notifying
{
    private bool _lit;
    public int Kind { get; init; }              // 0 category, 1 group, 2 action
    public string Key { get; init; } = "";
    public string Text { get; init; } = "";
    public bool Expanded { get; init; }
    public string Count { get; init; } = "";
    public bool HasBound { get; init; }
    public List<TagVM> Tags { get; init; } = new();

    public Thickness Pad => new(Kind * 16, 0, 0, 0);
    public string Glyph => Kind == 2 ? "" : (Expanded ? "▾" : "▸");
    public FontWeight Weight => Kind == 2 ? FontWeights.Normal : FontWeights.SemiBold;
    public Brush Fore => Kind == 0 ? (Brush)Application.Current.FindResource("AccentBrush")
        : (Brush)Application.Current.FindResource("TextBrush");
    public Visibility CountVisibility => Kind == 2 || Count.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TagsVisibility => Kind == 2 && Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Signature => $"{Kind}|{Key}|{Text}|{Count}|{Expanded}|{string.Join(",", Tags.Select(t => t.Kind + t.Text))}";

    public bool Lit
    {
        get => _lit;
        set { if (_lit != value) { _lit = value; Raise(nameof(Lit)); } }
    }
}

/// <summary>One binding of the selected action: Stick is null for the keyboard/mouse and gamepad layers.</summary>
public sealed record BoundRow(ControllerProfile Profile, StickRecord? Stick, ScBind Bind, string Text, string Mode);

public partial class BindingsView : UserControl
{
    private const string NoneItem = "—";
    private const string CaptureLabel = "Press a button or move an axis…";
    private static readonly string[] Filters = { "All actions", "Joystick", "Keyboard & Mouse", "Gamepad" };
    private static readonly Dictionary<string, string> AxisNames = new()
    {
        ["x"] = "Axis X", ["y"] = "Axis Y", ["z"] = "Axis Z", ["rotx"] = "Axis Rx", ["roty"] = "Axis Ry",
        ["rotz"] = "Axis Rz", ["slider1"] = "Slider 1", ["slider2"] = "Slider 2",
    };

    private AppController? _c;
    private readonly HashSet<string> _open = new();
    private readonly HashSet<string> _closedCats = new();
    private List<TreeRow> _rows = new();
    private Dictionary<string, List<TagVM>> _actionTags = new();
    private Dictionary<string, List<string>> _groupActions = new();
    private Dictionary<string, List<string>> _catGroups = new();
    private readonly Dictionary<(Guid, string), DateTime> _lit = new();
    private readonly DispatcherTimer _litTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading;
    private bool _statusDirty = true;
    private string? _flash;
    private DateTime _flashUntil;
    private string _totals = "";
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ActionDef? _action;
    private ActionMapsFile? _scFile;
    private DateTime _scFileStamp;

    public BindingsView()
    {
        InitializeComponent();
        ModeCombo.ItemsSource = new[] { "default" };
        ModeCombo.SelectedIndex = 0;
        DeviceFilter.ItemsSource = Filters;
        DeviceFilter.SelectedIndex = 0;
        KbmMouse.ItemsSource = new[] { NoneItem }.Concat(ScInputCodes.MouseTokens).ToList();
        KbmMouse.SelectedIndex = 0;
        _litTimer.Tick += (_, _) => ApplyLighting();
        _litTimer.Start();
        _statusTimer.Tick += (_, _) => UpdateSyncStatus();
        _statusTimer.Start();
    }

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(RefreshAll);
        c.InputSeen += (e, stick) =>
        {
            if (stick == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                var key = (stick.Id, e.Input);
                _lit[key] = e.Pressed ? DateTime.MaxValue : DateTime.UtcNow.AddMilliseconds(450);
                ApplyLighting();
                if (e.Pressed && IsVisible && Keyboard.FocusedElement is not TextBox)
                {
                    Inspector.Visibility = Visibility.Visible;
                    Inspector.Show(c, stick, e.Input);
                }
            });
        };
        RefreshAll();
    }

    /// <summary>Expands the group and selects the action "map/name" (automation hook).</summary>
    public void SelectAction(string key)
    {
        var a = _c?.Catalog?.AllActions.FirstOrDefault(x => x.Key == key);
        if (a == null) return;
        _open.Add(a.Map);
        _action = a;
        RebuildTree();
        ShowAction(a);
        var row = _rows.FirstOrDefault(r => r.Key == "act:" + a.Key);
        // Scroll after layout, otherwise the freshly assigned list is not measured yet.
        if (row != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Tree.ScrollIntoView(row));
    }

    private void RefreshAll()
    {
        if (_c == null) return;
        CatalogStatus.Text = _c.CatalogStatus;
        RefreshSetups();
        _statusDirty = true;
        var modes = new List<string> { "default" };
        if (_c.Catalog != null)
            modes.AddRange(new[] { "press", "tap", "hold", "double_tap", "delayed_press", "release" }
                .Concat(_c.Catalog.ActivationModes).Distinct().Where(m => m != "all"));
        if (!modes.SequenceEqual((IEnumerable<string>)ModeCombo.ItemsSource))
        {
            var cur = ModeCombo.SelectedItem as string;
            ModeCombo.ItemsSource = modes;
            ModeCombo.SelectedItem = cur != null && modes.Contains(cur) ? cur : "default";
        }
        if (_c.Catalog != null && PadInput.Items.Count == 0)
        {
            var tokens = _c.Catalog.GamepadTokens();
            PadInput.ItemsSource = tokens;
            PadMod.ItemsSource = new[] { NoneItem }.Concat(tokens).ToList();
            PadMod.SelectedIndex = 0;
            PadInput.SelectedIndex = tokens.Count > 0 ? 0 : -1;
        }
        RebuildTree();
        if (_action != null) ShowAction(_action);
    }

    // ---- tags and counts ------------------------------------------------------------------------

    /// <summary>
    /// Every binding of every action as tags: our profiles first, then whatever else is in the
    /// game's actionmaps.xml (other axes, buttons set in game) so the picture is complete.
    /// </summary>
    private Dictionary<string, List<TagVM>> BuildTags()
    {
        var map = new Dictionary<string, List<TagVM>>();
        if (_c == null) return map;
        var raws = new HashSet<string>();
        void Add(string key, TagVM t)
        {
            if (!map.TryGetValue(key, out var l)) map[key] = l = new();
            l.Add(t);
        }
        foreach (var b in _c.KeyboardMouseProfile.Binds)
        {
            Add(b.ActionKey, new TagVM { Kind = "KB", Text = ScInputCodes.Pretty(b.Input) });
            raws.Add($"{b.ActionKey}|{b.Input}");
        }
        foreach (var b in _c.GamepadProfile.Binds)
        {
            Add(b.ActionKey, new TagVM { Kind = "PAD", Text = ScInputCodes.Pretty(b.Input) });
            raws.Add($"{b.ActionKey}|{b.Input}");
        }
        foreach (var s in _c.Store.Data.Sticks)
        {
            var p = _c.Store.Profile(s.ProfileId);
            if (p == null || p.IsSystem) continue;
            foreach (var b in p.Binds)
            {
                Add(b.ActionKey, new TagVM
                {
                    Kind = "JS", Text = $"{s.Name} · {Controls.InputNames.Pretty(b.Input)}", StickId = s.Id, Input = b.Input,
                });
                if (s.ScInstance is { } n) raws.Add($"{b.ActionKey}|js{n}_{b.Input}");
            }
        }
        var file = ScFile();
        if (file != null)
        {
            foreach (var r in file.Rebinds())
            {
                if (r.IsUnbound || raws.Contains($"{r.Map}/{r.Action}|{r.Input}")) continue;
                var key = $"{r.Map}/{r.Action}";
                if (ScInputCodes.IsKbm(r.Input)) Add(key, new TagVM { Kind = "KB", Text = ScInputCodes.Pretty(r.Input) });
                else if (ScInputCodes.IsGamepad(r.Input)) Add(key, new TagVM { Kind = "PAD", Text = ScInputCodes.Pretty(r.Input) });
                else if (r.JsInstance is { } inst && r.JsInputName is { } inp)
                {
                    var stick = _c.Store.Data.Sticks.FirstOrDefault(s => s.ScInstance == inst);
                    var what = AxisNames.TryGetValue(inp, out var ax) ? ax : Controls.InputNames.Pretty(inp);
                    Add(key, new TagVM
                    {
                        Kind = "JS", Text = $"{stick?.Name ?? "js" + inst} · {what}", StickId = stick?.Id, Input = inp,
                    });
                }
            }
        }
        return map;
    }

    private void RebuildTree()
    {
        if (_c?.Catalog == null) { Tree.ItemsSource = null; return; }
        var cat = _c.Catalog;
        var q = Search.Text.Trim();
        bool searching = q.Length > 0;
        _actionTags = BuildTags();
        var rows = new List<TreeRow>();
        int filter = Math.Max(0, DeviceFilter.SelectedIndex);

        bool Match(ActionDef a)
        {
            bool deviceOk = filter switch
            {
                1 => a.Joystick,
                2 => a.Keyboard || a.Mouse,
                3 => a.Gamepad,
                _ => true,
            };
            if (!deviceOk) return false;
            if (OnlyBound.IsChecked == true && !_actionTags.ContainsKey(a.Key)) return false;
            if (!searching) return true;
            return a.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || a.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        _groupActions = cat.Groups.ToDictionary(g => g.Name, g => g.Actions.Select(a => a.Key).ToList());
        _catGroups = new();
        int allBound = cat.AllActions.Count(a => _actionTags.ContainsKey(a.Key));
        _totals = $"{allBound} bound";

        foreach (var cg in cat.Groups.GroupBy(g => g.Category).OrderBy(g => g.Key == "Other" ? 1 : 0))
        {
            var groups = cg.Select(g => (Group: g, Actions: g.Actions.Where(Match).ToList())).Where(x => x.Actions.Count > 0).ToList();
            if (groups.Count == 0) continue;
            _catGroups[cg.Key] = cg.Select(g => g.Name).ToList();
            int catBound = cg.Sum(g => g.Actions.Count(a => _actionTags.ContainsKey(a.Key)));
            int catTotal = cg.Sum(g => g.Actions.Count);
            bool catOpen = searching || !_closedCats.Contains(cg.Key);
            rows.Add(new TreeRow
            {
                Kind = 0, Key = "cat:" + cg.Key, Text = cg.Key, Expanded = catOpen,
                Count = $"{catBound} / {catTotal}", HasBound = catBound > 0,
            });
            if (!catOpen) continue;
            foreach (var (g, actions) in groups)
            {
                bool open = searching || _open.Contains(g.Name);
                int bound = g.Actions.Count(a => _actionTags.ContainsKey(a.Key));
                rows.Add(new TreeRow
                {
                    Kind = 1, Key = "grp:" + g.Name, Text = g.Label, Expanded = open,
                    Count = $"{bound} / {g.Actions.Count}", HasBound = bound > 0,
                });
                if (!open) continue;
                foreach (var a in actions)
                    rows.Add(new TreeRow
                    {
                        Kind = 2, Key = "act:" + a.Key, Text = a.Label,
                        Tags = _actionTags.TryGetValue(a.Key, out var t) ? t : new(), HasBound = t != null,
                    });
            }
        }
        if (rows.Select(r => r.Signature).SequenceEqual(_rows.Select(r => r.Signature)))
        {
            // Same structure: keep the existing rows but point them at the fresh tag objects.
            ApplyLighting();
            return;
        }
        _rows = rows;
        _loading = true;
        Tree.ItemsSource = rows;
        var sel = _action != null ? rows.FirstOrDefault(r => r.Key == "act:" + _action.Key) : null;
        Tree.SelectedItem = sel;
        _loading = false;
        ApplyLighting();
    }

    // ---- live lighting ------------------------------------------------------------------------

    /// <summary>
    /// A pressed stick input lights its tag, its action row, and - when collapsed - the group and category
    /// that contain it. Only while this window is the active one.
    /// </summary>
    private void ApplyLighting()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _lit.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) _lit.Remove(k);
        bool active = IsVisible && Window.GetWindow(this)?.IsActive == true;

        var litActions = new HashSet<string>();
        foreach (var (key, tags) in _actionTags)
        {
            bool any = false;
            foreach (var t in tags)
            {
                t.Lit = active && t.StickId is { } id && t.Input != null && _lit.ContainsKey((id, t.Input));
                any |= t.Lit;
            }
            if (any) litActions.Add(key);
        }

        foreach (var r in _rows)
        {
            r.Lit = r.Kind switch
            {
                2 => litActions.Contains(r.Key[4..]),
                1 => !r.Expanded && _groupActions.TryGetValue(r.Key[4..], out var ks) && ks.Any(litActions.Contains),
                _ => !r.Expanded && _catGroups.TryGetValue(r.Key[4..], out var gs)
                     && gs.Any(g => _groupActions.TryGetValue(g, out var k2) && k2.Any(litActions.Contains)),
            };
        }
    }

    private void Filter_Changed(object s, RoutedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading) RebuildTree();
    }

    // ---- profile header ------------------------------------------------------------------------

    private void RefreshSetups()
    {
        if (_c == null) return;
        var list = _c.Setups.ToList();
        var active = _c.ActiveSetup;
        var shown = SetupCombo.Items.Cast<Setup>().ToList();
        _loading = true;
        if (!shown.Select(x => x.Id).SequenceEqual(list.Select(x => x.Id)) ||
            !shown.Select(x => x.Name).SequenceEqual(list.Select(x => x.Name)))
        {
            SetupCombo.ItemsSource = null;
            SetupCombo.ItemsSource = list;
        }
        SetupCombo.SelectedItem = active;
        _loading = false;
    }

    private void UpdateSyncStatus()
    {
        if (_c == null) return;
        if (_flash != null && DateTime.UtcNow < _flashUntil)
        {
            SyncStatus.Text = _flash;
            SyncStatus.Foreground = (Brush)FindResource("AccentBrush");
            return;
        }
        _flash = null;
        if (!_statusDirty) return;
        _statusDirty = false;
        var n = _c.PendingChanges();
        if (n == null)
        {
            SyncStatus.Text = "Star Citizen not found";
            SyncStatus.Foreground = (Brush)FindResource("MutedBrush");
            SyncStatus.ToolTip = null;
        }
        else if (n == 0)
        {
            SyncStatus.Text = $"✓ in sync · {_totals}";
            SyncStatus.Foreground = (Brush)FindResource("GoodBrush");
            SyncStatus.ToolTip = "This profile matches Star Citizen's actionmaps.xml";
        }
        else
        {
            SyncStatus.Text = $"● {n} pending · {_totals}";
            SyncStatus.Foreground = (Brush)FindResource("WarnBrush");
            SyncStatus.ToolTip = $"{n} binding(s) differ from Star Citizen's actionmaps.xml. Deploy to apply them.";
        }
    }

    private void Flash(string text)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(6);
        _statusDirty = true;
        UpdateSyncStatus();
    }

    private void SetupCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || _c == null || SetupCombo.SelectedItem is not Setup su || ReferenceEquals(su, _c.ActiveSetup)) return;
        _c.ActivateSetup(su);
        _statusDirty = true;
    }

    private void SetupMenu_Click(object s, RoutedEventArgs e)
    {
        SetupMenu.PlacementTarget = SetupMenuBtn;
        SetupMenu.IsOpen = true;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void SetupNew_Click(object s, RoutedEventArgs e)
    {
        var name = Controls.InputDialog.Ask(OwnerWindow, "New profile", "Name of the new (empty) profile:", "New profile");
        if (name != null) _c?.NewSetup(name);
    }

    private void SetupDuplicate_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        _c.ActivateSetup(_c.DuplicateSetup(_c.ActiveSetup));
    }

    private void SetupRename_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        var name = Controls.InputDialog.Ask(OwnerWindow, "Rename profile", "New name:", _c.ActiveSetup.Name);
        if (name != null) _c.RenameSetup(_c.ActiveSetup, name);
    }

    private void SetupDelete_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        if (_c.Setups.Count <= 1)
        {
            MessageBox.Show("The last profile cannot be deleted.", "Delete profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Delete profile “{_c.ActiveSetup.Name}”? Star Citizen's files are not touched.",
                "Delete profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _c.DeleteSetup(_c.ActiveSetup);
    }

    private void SetupExport_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _c.ActiveSetup.Name + ".starbutton-profile.json",
            Filter = "Star Button profile (*.json)|*.json",
        };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, _c.ExportSetup(_c.ActiveSetup));
    }

    private void SetupImport_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Star Button profile (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _c.ActivateSetup(_c.ImportSetup(File.ReadAllText(dlg.FileName)));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not import: " + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Read_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        if (_c.Install == null || !File.Exists(_c.Install.ActionMapsPath))
        {
            MessageBox.Show("No Star Citizen actionmaps.xml was found. Start the game once or pick the installation in the Deploy tab.",
                "Read Star Citizen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (setup, imported, skipped) = _c.ReadFromStarCitizen();
        var extra = skipped > 0 ? $" ({skipped} skipped)" : "";
        Flash($"Read {imported} bindings into “{setup.Name}”{extra}");
    }

    private void Deploy_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null)
        {
            MessageBox.Show("Star Citizen was not found. Pick the installation in the Deploy tab.", "Deploy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var plan = _c.Deploy(true);
            var msg = $"Write profile “{_c.ActiveSetup.Name}” to Star Citizen?\n\n" +
                      $"{plan.Added} binding(s) will be written, {plan.Removed} replaced.\n" +
                      "A backup of actionmaps.xml is made first.";
            if (plan.Warnings.Count > 0)
                msg += "\n\nWarnings:\n• " + string.Join("\n• ", plan.Warnings.Take(6));
            if (ScLocator.IsGameRunning())
                msg += "\n\n⚠ Star Citizen is running and may overwrite the file when it exits.";
            if (MessageBox.Show(msg, "Deploy", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var res = _c.Deploy(false);
            Flash($"Deployed {res.Added} bindings ✓" + (res.BackupPath != null ? $" · backup {Path.GetFileName(res.BackupPath)}" : ""));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Deploy failed: " + ex.Message, "Deploy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Tree_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || Tree.SelectedItem is not TreeRow row || _c?.Catalog == null) return;
        if (row.Kind == 0)
        {
            var name = row.Key[4..];
            if (!_closedCats.Remove(name)) _closedCats.Add(name);
            RebuildTree();
        }
        else if (row.Kind == 1)
        {
            var name = row.Key[4..];
            if (!_open.Remove(name)) _open.Add(name);
            RebuildTree();
        }
        else
        {
            var key = row.Key[4..];
            var a = _c.Catalog.AllActions.FirstOrDefault(x => x.Key == key);
            if (a != null) { _action = a; ShowAction(a); }
        }
    }

    // ---- detail -------------------------------------------------------------------------------

    private ActionMapsFile? ScFile()
    {
        var path = _c?.Install?.ActionMapsPath;
        if (path == null || !File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_scFile == null || stamp != _scFileStamp)
        {
            try { _scFile = ActionMapsFile.Load(path); _scFileStamp = stamp; } catch { _scFile = null; }
        }
        return _scFile;
    }

    private void ShowAction(ActionDef a)
    {
        if (_c == null) return;
        _loading = true;
        Hint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;
        var grp = _c.Catalog!.Groups.First(g => g.Name == a.Map);
        ActTitle.Text = a.Label;
        ActPath.Text = $"{grp.Category} › {grp.Label}\n{a.Map}/{a.Name}" +
                       (a.ActivationMode != null ? $" · default mode: {a.ActivationMode}" : "");
        ActDesc.Text = a.Description;
        ActDesc.Visibility = string.IsNullOrWhiteSpace(a.Description) ? Visibility.Collapsed : Visibility.Visible;

        string D(string v, bool supported) => !supported ? "not available" : string.IsNullOrWhiteSpace(v) ? "default: none" : "default: " + v;
        KbmDefault.Text = $"keyboard {D(a.DefaultKeyboard, a.Keyboard)} · mouse {D(a.DefaultMouse, a.Mouse)}";
        PadDefault.Text = D(a.DefaultGamepad, a.Gamepad);
        JsDefault.Text = D(a.DefaultJoystick, a.Joystick);

        var seen = ScFile()?.Rebinds().Where(r => r.Map == a.Map && r.Action == a.Name).ToList();
        ActInSc.Text = seen is { Count: > 0 }
            ? "In actionmaps.xml: " + string.Join(", ", seen.Select(r => r.IsUnbound ? r.Input.Trim() + " (unbound)" : r.Input))
            : "Not changed in your actionmaps.xml.";

        var kbm = new List<BoundRow>();
        var pad = new List<BoundRow>();
        var js = new List<BoundRow>();
        foreach (var b in _c.KeyboardMouseProfile.Binds.Where(b => b.ActionKey == a.Key))
            kbm.Add(new BoundRow(_c.KeyboardMouseProfile, null, b, ScInputCodes.Pretty(b.Input), b.Mode ?? ""));
        foreach (var b in _c.GamepadProfile.Binds.Where(b => b.ActionKey == a.Key))
            pad.Add(new BoundRow(_c.GamepadProfile, null, b, ScInputCodes.Pretty(b.Input), b.Mode ?? ""));
        foreach (var s in _c.Store.Data.Sticks)
        {
            var p = _c.Store.Profile(s.ProfileId);
            if (p == null || p.IsSystem) continue;
            foreach (var b in p.Binds.Where(b => b.ActionKey == a.Key))
                js.Add(new BoundRow(p, s, b, $"{s.Name} · {Controls.InputNames.Pretty(b.Input)}", b.Mode ?? ""));
        }
        KbmList.ItemsSource = kbm; KbmNone.Visibility = kbm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PadList.ItemsSource = pad; PadNone.Visibility = pad.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BoundList.ItemsSource = js; NoneBound.Visibility = js.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Same input used by another action of the same group on the same device.
        var conflicts = new List<string>();
        foreach (var r in kbm.Concat(pad).Concat(js))
            foreach (var other in r.Profile.Binds.Where(b => b.Map == a.Map && b.Input == r.Bind.Input && b.Action != a.Name))
                conflicts.Add($"{r.Text} is also used by “{_c.Catalog.Find(other.Map, other.Action)?.Label ?? other.Action}”");
        Conflicts.Text = conflicts.Count == 0 ? "" : "⚠ Conflict: " + string.Join("; ", conflicts.Distinct());
        Conflicts.Visibility = conflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var sticks = _c.Store.Data.Sticks.ToList();
        var prev = StickCombo.SelectedItem as StickRecord;
        StickCombo.ItemsSource = sticks;
        StickCombo.SelectedItem = prev != null && sticks.Contains(prev) ? prev
            : sticks.FirstOrDefault(_c.IsConnected) ?? sticks.FirstOrDefault();
        FillInputs();
        _loading = false;
    }

    private void FillInputs()
    {
        var st = StickCombo.SelectedItem as StickRecord;
        var list = new List<string>();
        if (st != null)
        {
            for (int i = 1; i <= Math.Max(st.Buttons, 1); i++) list.Add($"button{i}");
            for (int h = 1; h <= st.Hats; h++)
                foreach (var d in new[] { "up", "right", "down", "left" }) list.Add($"hat{h}_{d}");
            var dev = _c?.DeviceFor(st);
            var slots = dev != null && dev.AxisSlots.Count > 0 ? dev.AxisSlots : Enumerable.Range(0, Math.Min(st.Axes, 8)).ToList();
            foreach (var slot in slots) list.Add(AppController.AxisInputNames[slot]);
        }
        InputCombo.ItemsSource = list;
        InputCombo.SelectedIndex = list.Count > 0 ? 0 : -1;
    }

    private void StickCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (!_loading) FillInputs();
    }

    private string? Mode => ModeCombo.SelectedItem is string m && m != "default" ? m : null;

    // ---- binding ------------------------------------------------------------------------------

    private void Capture_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _action == null) return;
        if (_c.IsCapturing)
        {
            _c.CancelCapture();
            CaptureHint.Visibility = Visibility.Collapsed;
            CaptureBtn.Content = CaptureLabel;
            return;
        }
        var action = _action;
        CaptureHint.Visibility = Visibility.Visible;
        CaptureBtn.Content = "Cancel";
        var mode = Mode;
        _c.BeginCapture(r => Dispatcher.BeginInvoke(() =>
        {
            CaptureHint.Visibility = Visibility.Collapsed;
            CaptureBtn.Content = CaptureLabel;
            if (r.Stick == null)
            {
                MessageBox.Show("That device is not linked to a stick yet. Open the Sticks tab and create or link it first.",
                    "Unknown stick", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var prof = _c.EnsureProfile(r.Stick);
            _c.SetBind(prof, action.Map, action.Name, r.Input, mode);
            Inspector.Visibility = Visibility.Visible;
            Inspector.Show(_c, r.Stick, r.Input);
        }), includeAxes: true);
    }

    private void BindManual_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _action == null) return;
        if (StickCombo.SelectedItem is not StickRecord st || InputCombo.SelectedItem is not string input) return;
        var prof = _c.EnsureProfile(st);
        _c.SetBind(prof, _action.Map, _action.Name, input, Mode);
    }

    private void BindKbm_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _action == null) return;
        var mouse = KbmMouse.SelectedItem as string;
        var raw = ScInputCodes.BuildKbm(KbmKey.Text, mouse == NoneItem ? null : mouse);
        if (raw == null) return;
        _c.SetRawBind(_c.KeyboardMouseProfile, _action.Map, _action.Name, raw, Mode);
        KbmKey.SetComboSilently("");
        KbmMouse.SelectedIndex = 0;
    }

    private void KbmKeyClear_Click(object s, RoutedEventArgs e) => KbmKey.SetComboSilently("");

    private void BindPad_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _action == null) return;
        var mod = PadMod.SelectedItem as string;
        var raw = ScInputCodes.BuildGamepad(mod == NoneItem ? null : mod, PadInput.SelectedItem as string);
        if (raw == null) return;
        _c.SetRawBind(_c.GamepadProfile, _action.Map, _action.Name, raw, Mode);
    }

    private void RemoveBound_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || ((FrameworkElement)s).Tag is not BoundRow row) return;
        _c.RemoveBind(row.Profile, row.Bind);
    }
}
