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

/// <summary>A binding shown as a chip. Kind is KB, PAD or JS; Stick/Input are set when it can light up live.</summary>
public sealed class TagVM : Notifying
{
    private bool _lit, _selected;
    private string? _mode;
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public Guid? StickId { get; init; }
    public string? Input { get; init; }
    /// <summary>Comes from the game's default profile, not from a rebind.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Action this chip belongs to and the binding's Star Citizen code ("js2_button3", "kb1_f4").</summary>
    public string Map { get; init; } = "";
    public string Action { get; init; } = "";
    public string? Raw { get; set; }
    /// <summary>Set for chips that come from this setup's profiles (removed directly when Raw is unknown).</summary>
    public ControllerProfile? Profile { get; set; }
    public ScBind? Bind { get; set; }
    /// <summary>"Set by you" / "Found in the game's file" / "Game default".</summary>
    public string Source { get; set; } = "";

    public string Id => $"{Map}/{Action}|{Raw ?? Kind + Text}";
    public bool IsAxis => Kind == "JS" && Input is "x" or "y" or "z" or "rotx" or "roty" or "rotz" or "slider1" or "slider2";

    /// <summary>press, tap, hold, double_tap ... null = the game decides.</summary>
    public string? Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Raise(nameof(Mode)); Raise(nameof(ModeLabel)); Raise(nameof(ModeVisibility));
        }
    }
    public string ModeLabel => Mode switch
    {
        null => "", "press" => "PRESS", "tap" => "TAP", "hold" => "HOLD", "double_tap" => "2× TAP",
        "delayed_press" => "DELAYED", "release" => "RELEASE", var m => m.ToUpperInvariant(),
    };
    public Visibility ModeVisibility => Mode == null ? Visibility.Collapsed : Visibility.Visible;

    public bool Lit
    {
        get => _lit;
        set { if (_lit != value) { _lit = value; Raise(nameof(Lit)); } }
    }

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Raise(nameof(Selected)); } }
    }

    /// <summary>The row keeps its displayed chip; only what the code behind needs is refreshed.</summary>
    public void UpdateFrom(TagVM o)
    {
        Raw = o.Raw; Profile = o.Profile; Bind = o.Bind; Source = o.Source; Mode = o.Mode;
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
    public Visibility NoneVisibility => Kind == 2 && Tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public int TitleSpan => Kind == 2 ? 1 : 2;
    public double TitleSize => Kind == 0 ? 14 : 13;
    public string Signature => $"{Kind}|{Key}|{Text}|{Count}|{Expanded}|{string.Join(",", Tags.Select(t => (t.IsDefault ? "d" : "") + t.Kind + t.Text + t.Mode))}";

    public bool Lit
    {
        get => _lit;
        set { if (_lit != value) { _lit = value; Raise(nameof(Lit)); } }
    }

    private bool _capturing;
    /// <summary>The row waits for the next key or button to bind.</summary>
    public bool Capturing
    {
        get => _capturing;
        set { if (_capturing != value) { _capturing = value; Raise(nameof(Capturing)); } }
    }
}

/// <summary>One entry of the trigger-mode list. Value null = the game decides.</summary>
public sealed record ModeOption(string? Value, string Title, string Description);

public partial class BindingsView : UserControl
{
    private static readonly ModeOption[] ModeOptions =
    {
        new(null, "Default", "The game decides. Right for almost everything."),
        new("press", "On press", "Fires the moment you press the button."),
        new("tap", "Short tap", "Fires on a quick press and release. Holding it does not count."),
        new("hold", "Hold", "Fires only after you keep the button pressed for a moment."),
        new("double_tap", "Double tap", "Fires when you press the button twice in quick succession."),
        new("release", "On release", "Fires when you let go of the button."),
        new("delayed_press", "Delayed press", "Like a press, but only after a short delay - avoids accidents."),
    };

    private const string GameRunningHint = AppController.GameRunningMessage;
    private static readonly Dictionary<string, string> AxisNames = new()
    {
        ["x"] = "Axis X", ["y"] = "Axis Y", ["z"] = "Axis Z", ["rotx"] = "Axis Rx", ["roty"] = "Axis Ry",
        ["rotz"] = "Axis Rz", ["slider1"] = "Slider 1", ["slider2"] = "Slider 2",
    };

    private AppController? _c;
    private readonly HashSet<string> _open = new();
    private readonly HashSet<string> _openCats = new();
    private List<TreeRow> _rows = new();
    private Dictionary<string, List<TagVM>> _actionTags = new();
    private Dictionary<string, List<string>> _groupActions = new();
    private Dictionary<string, List<string>> _catGroups = new();
    private readonly Dictionary<(Guid, string), DateTime> _lit = new();
    private readonly DispatcherTimer _litTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loading;
    private bool _statusDirty = true;
    private string? _flash;
    private DateTime _syncStamp;
    private bool _wasDirty;
    private DateTime _flashUntil;
    private string _totals = "";
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ActionDef? _action;
    private ActionMapsFile? _scFile;
    private DateTime _scFileStamp;

    // Binding mode (double click on an action): the next key or stick input is bound.
    private ActionDef? _bindAction;
    private Window? _bindWindow;
    private readonly List<string> _bindKeys = new();     // keyboard tokens in the order they went down
    private readonly HashSet<string> _bindDown = new();  // ... and which of them are still held
    private (StickRecord Stick, string Input)? _bindJs;
    private List<string> _bindJsMods = new();            // keyboard modifiers held when the stick button went down

    public BindingsView()
    {
        InitializeComponent();
        ModeList.ItemsSource = ModeOptions;
        _litTimer.Tick += (_, _) => ApplyLighting();
        _litTimer.Start();
        _statusTimer.Tick += (_, _) => UpdateSyncStatus();
        _statusTimer.Start();
        IsVisibleChanged += (_, _) => { if (!IsVisible) EndBind(); };
    }

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(RefreshAll);
        c.InputSeen += (e, stick) =>
        {
            if (stick == null && _bindAction == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_bindAction != null) { BindJoystick(e, stick); return; }
                if (stick == null) return;
                var key = (stick.Id, e.Input);
                _lit[key] = e.Pressed ? DateTime.MaxValue : DateTime.UtcNow.AddMilliseconds(450);
                ApplyLighting();
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
        var cat = _c!.Catalog!.Groups.FirstOrDefault(g => g.Name == a.Map)?.Category;
        if (cat != null) _openCats.Add(cat);
        _action = a;
        RebuildTree();
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
        RebuildTree();
        UpdatePanel();
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
        // Device families (kb, mo, gp, jsN) whose game default is replaced or unbound for an action.
        var overridden = new Dictionary<string, HashSet<string>>();
        void Add(string key, TagVM t)
        {
            if (!map.TryGetValue(key, out var l)) map[key] = l = new();
            l.Add(t);
        }
        void Override(string key, string input)
        {
            var m = DeviceFamily().Match(input);
            if (!m.Success) return;
            var fam = m.Groups[1].Value == "js" ? "js" + m.Groups[2].Value : m.Groups[1].Value;
            if (!overridden.TryGetValue(key, out var set)) overridden[key] = set = new();
            set.Add(fam);
        }
        // "device|action" of unbound markers held by this setup: they hide the game file's rebinds of that device.
        var markers = new HashSet<string>();
        string? Family(string input)
        {
            var m = DeviceFamily().Match(input);
            return !m.Success ? null : m.Groups[1].Value == "js" ? "js" + m.Groups[2].Value : m.Groups[1].Value;
        }
        void Layer(ControllerProfile layer, string kind)
        {
            foreach (var b in layer.Binds)
            {
                Override(b.ActionKey, b.Input);
                if (b.IsUnbound)
                {
                    if (Family(b.Input) is { } f) markers.Add($"{f}|{b.ActionKey}");
                    continue;
                }
                Add(b.ActionKey, new TagVM
                {
                    Kind = kind, Text = ScInputCodes.Pretty(b.Input), Map = b.Map, Action = b.Action, Raw = b.Input,
                    Profile = layer, Bind = b, Mode = b.Mode, Source = "Set by you in Star Button",
                });
                raws.Add($"{b.ActionKey}|{b.Input}");
            }
        }
        Layer(_c.KeyboardMouseProfile, "KB");
        Layer(_c.GamepadProfile, "PAD");
        foreach (var s in _c.Store.Data.Sticks)
        {
            var p = _c.Store.Profile(s.ProfileId);
            if (p == null || p.IsSystem) continue;
            foreach (var b in p.Binds)
            {
                if (s.ScInstance is { } n) Override(b.ActionKey, $"js{n}_");
                if (b.IsUnbound)
                {
                    if (s.ScInstance is { } mn) markers.Add($"js{mn}|{b.ActionKey}");
                    continue;
                }
                Add(b.ActionKey, new TagVM
                {
                    Kind = "JS", Text = $"{s.Name} · {Controls.InputNames.Pretty(b.Input)}", StickId = s.Id, Input = b.Input,
                    Map = b.Map, Action = b.Action, Raw = s.ScInstance is { } rn ? $"js{rn}_{b.Input}" : null,
                    Profile = p, Bind = b, Mode = b.Mode, Source = "Set by you in Star Button",
                });
                if (s.ScInstance is { } n2) raws.Add($"{b.ActionKey}|js{n2}_{b.Input}");
            }
        }
        // Only the profile that mirrors Star Citizen is shown together with the game's file. Every other
        // profile - "Empty" above all - shows just what it contains.
        var file = _c.ActiveSetup.IsGameProfile ? ScFile() : null;
        if (file != null)
        {
            foreach (var r in file.Rebinds())
            {
                var key = $"{r.Map}/{r.Action}";
                // This setup removed the device's binding: whatever the file still holds for it is on its way out.
                if (Family(r.Input) is { } fam && markers.Contains($"{fam}|{key}")) continue;
                // Any rebind of a device - also an "unbound" marker - replaces that device's game default.
                Override(key, r.Input);
                if (r.IsUnbound || raws.Contains($"{key}|{r.Input}")) continue;
                var fileMode = r.MultiTap == "2" ? "double_tap" : r.ActivationMode;
                const string fromFile = "Found in Star Citizen's actionmaps.xml";
                if (ScInputCodes.IsKbm(r.Input))
                    Add(key, new TagVM { Kind = "KB", Text = ScInputCodes.Pretty(r.Input), Map = r.Map, Action = r.Action, Raw = r.Input, Mode = fileMode, Source = fromFile });
                else if (ScInputCodes.IsGamepad(r.Input))
                    Add(key, new TagVM { Kind = "PAD", Text = ScInputCodes.Pretty(r.Input), Map = r.Map, Action = r.Action, Raw = r.Input, Mode = fileMode, Source = fromFile });
                else if (r.JsInstance is { } inst && r.JsInputName is { } inp)
                {
                    var stick = _c.Store.Data.Sticks.FirstOrDefault(s => s.ScInstance == inst);
                    var what = AxisNames.TryGetValue(inp, out var ax) ? ax : Controls.InputNames.Pretty(inp);
                    Add(key, new TagVM
                    {
                        Kind = "JS", Text = $"{stick?.Name ?? "js" + inst} · {what}", StickId = stick?.Id, Input = inp,
                        Map = r.Map, Action = r.Action, Raw = r.Input, Mode = fileMode, Source = fromFile,
                    });
                }
            }
        }
        // "Empty" profile: the game's defaults are switched off, so none of them is shown.
        if (_c.Catalog != null && !_c.ActiveSetup.NoDefaults) AddDefaults(_c.Catalog, overridden, Add);
        return map;
    }

    /// <summary>
    /// The game's default bindings (defaultProfile.xml) fill in whatever neither this setup nor
    /// actionmaps.xml replaced, so the picture matches what the game's own controls menu shows.
    /// Joystick defaults are written for js1.
    /// </summary>
    private void AddDefaults(ActionCatalog cat, Dictionary<string, HashSet<string>> overridden, Action<string, TagVM> add)
    {
        foreach (var a in cat.AllActions)
        {
            overridden.TryGetValue(a.Key, out var o);
            bool Free(string fam) => o == null || !o.Contains(fam);
            void Def(string kind, string raw, string fam)
            {
                if (string.IsNullOrWhiteSpace(raw) || !Free(fam)) return;
                add(a.Key, new TagVM
                {
                    Kind = kind, Text = ScInputCodes.Pretty(raw.Trim()), IsDefault = true,
                    Map = a.Map, Action = a.Name, Raw = $"{fam}1_{raw.Trim()}", Source = "Game default",
                });
            }
            Def("KB", a.DefaultKeyboard, "kb");
            Def("KB", a.DefaultMouse, "mo");
            Def("PAD", a.DefaultGamepad, "gp");
            if (a.Joystick && !string.IsNullOrWhiteSpace(a.DefaultJoystick) && Free("js1"))
            {
                var inp = a.DefaultJoystick.Contains('_') && a.DefaultJoystick.StartsWith("js")
                    ? a.DefaultJoystick[(a.DefaultJoystick.IndexOf('_') + 1)..] : a.DefaultJoystick;
                var stick = _c!.Store.Data.Sticks.FirstOrDefault(s => s.ScInstance == 1);
                var what = AxisNames.TryGetValue(inp, out var ax) ? ax : Controls.InputNames.Pretty(inp);
                add(a.Key, new TagVM
                {
                    Kind = "JS", Text = $"{stick?.Name ?? "js1"} · {what}", StickId = stick?.Id, Input = inp, IsDefault = true,
                    Map = a.Map, Action = a.Name, Raw = "js1_" + inp, Source = "Game default",
                });
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(kb|mo|gp|js)(\d+)_")]
    private static partial System.Text.RegularExpressions.Regex DeviceFamily();

    private void RebuildTree()
    {
        if (_c?.Catalog == null) { Tree.ItemsSource = null; return; }
        var cat = _c.Catalog;
        var q = Search.Text.Trim();
        bool searching = q.Length > 0;
        _actionTags = BuildTags();
        var rows = new List<TreeRow>();

        bool Match(ActionDef a)
        {
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
            bool catOpen = searching || _openCats.Contains(cg.Key);
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
                        Kind = 2, Key = "act:" + a.Key, Text = a.Label, Capturing = _bindAction?.Key == a.Key,
                        Tags = _actionTags.TryGetValue(a.Key, out var t) ? t : new(), HasBound = t != null,
                    });
            }
        }
        if (rows.Select(r => r.Signature).SequenceEqual(_rows.Select(r => r.Signature)))
        {
            // Same structure: keep the rows and the chips on screen, hand them the fresh data, and let
            // lighting work on the chips that are actually displayed.
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind != 2) continue;
                for (int j = 0; j < rows[i].Tags.Count; j++) _rows[i].Tags[j].UpdateFrom(rows[i].Tags[j]);
                _actionTags[rows[i].Key[4..]] = _rows[i].Tags;
            }
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
                t.Lit = active && t.StickId is { } id && t.Input != null
                        && _lit.ContainsKey((id, ScInputCodes.SplitJsInput(t.Input).Input));
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
        bool empty = Search.Text.Length == 0;
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!_loading) RebuildTree();
    }

    private void SearchClear_Click(object s, RoutedEventArgs e)
    {
        Search.Clear();
        Search.Focus();
    }

    // ---- profile header ------------------------------------------------------------------------

    private void RefreshSetups()
    {
        if (_c == null) return;
        var list = _c.Setups
            .OrderBy(x => x.IsGameProfile ? 0 : x.IsBuiltIn ? 1 : 2)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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
        bool dirty = _c.IsActiveDirty;
        SaveBtn.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveBtn.Content = _c.ActiveSetup.IsProtected ? "💾 Save as new…" : "💾 Save";
        if (dirty != _wasDirty) { _wasDirty = dirty; _statusDirty = true; }
        bool gameRunning = ScLocator.IsGameRunning();
        if (DeployBtn.IsEnabled == gameRunning)
        {
            DeployBtn.IsEnabled = !gameRunning;
            DeployBtn.ToolTip = gameRunning ? "Close Star Citizen first - it would overwrite the deploy when it exits"
                : "Write this profile to Star Citizen";
            _statusDirty = true;
        }
        // The game (or anything else) rewrote actionmaps.xml: compare again.
        var path = _c.Install?.ActionMapsPath;
        var stamp = path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (stamp != _syncStamp) { _syncStamp = stamp; _statusDirty = true; }
        // Compared even while the game runs: the alarm must show that a deploy is due before it can be done.
        if (_statusDirty)
        {
            _statusDirty = false;
            _pending = _c.PendingChanges();
            SetDeployAlarm(_pending > 0);
        }
        var n = _pending;
        if (gameRunning)
        {
            _flash = null;
            SyncStatus.Text = n > 0 ? $"⚠ Star Citizen is running - close it to deploy · {n} pending"
                                    : "⚠ Star Citizen is running - close it to deploy";
            SyncStatus.ToolTip = GameRunningHint;
            SyncStatus.Foreground = (Brush)FindResource(n > 0 ? "BadBrush" : "WarnBrush");
            return;
        }
        if (_flash != null && DateTime.UtcNow < _flashUntil)
        {
            SyncStatus.Text = _flash;
            SyncStatus.ToolTip = _flash;
            SyncStatus.Foreground = (Brush)FindResource("AccentBrush");
            return;
        }
        _flash = null;
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
            SyncStatus.Foreground = (Brush)FindResource("BadBrush");
            SyncStatus.ToolTip = $"{n} binding(s) differ from Star Citizen's actionmaps.xml. Deploy to apply them.";
        }
    }

    private int? _pending;

    /// <summary>Raised when "a deploy is due" changes, so the navigation can show it on other tabs.</summary>
    public event Action<bool>? DeployDueChanged;
    private bool _alarm;

    private void SetDeployAlarm(bool on)
    {
        DeployAlarm.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            DeployBtn.Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x1E, 0x1E));
            DeployBtn.BorderBrush = (Brush)FindResource("BadBrush");
            DeployBtn.Content = "⚠ Deploy";
        }
        else
        {
            DeployBtn.ClearValue(BackgroundProperty);
            DeployBtn.ClearValue(BorderBrushProperty);
            DeployBtn.Content = "⇪ Deploy";
        }
        if (on != _alarm) { _alarm = on; DeployDueChanged?.Invoke(on); }
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
        if (!ConfirmLeave())
        {
            _loading = true;
            SetupCombo.SelectedItem = _c.ActiveSetup;
            _loading = false;
            return;
        }
        _c.ActivateSetup(su);
        _statusDirty = true;
    }

    /// <summary>Unsaved changes: save them, throw them away, or stay. False = stay.</summary>
    private bool ConfirmLeave()
    {
        if (_c == null || !_c.IsActiveDirty) return true;
        var name = _c.ActiveSetup.Name;
        var save = _c.ActiveSetup.IsProtected ? "save them as a new profile" : "save them";
        var r = MessageBox.Show($"“{name}” has unsaved changes.\n\nYes: {save}\nNo: discard them\nCancel: stay here",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveActive();
        _c.RevertSetup(_c.ActiveSetup);
        return true;
    }

    /// <summary>
    /// Saves the active profile. Built-in profiles (game defaults, Empty) are never overwritten: their changes
    /// can only be stored as a new profile. False = the user cancelled the name dialog, nothing was saved.
    /// </summary>
    private bool SaveActive()
    {
        if (_c == null) return false;
        if (!_c.ActiveSetup.IsProtected)
        {
            _c.SaveActiveSetup();
            Flash($"Saved “{_c.ActiveSetup.Name}”");
            return true;
        }
        var from = _c.ActiveSetup.Name;
        var name = Controls.InputDialog.Ask(OwnerWindow, "Save as new profile",
            $"“{from}” is built-in and stays unchanged.\nName of the new profile (the current bindings are copied into it):", from + " 2");
        if (string.IsNullOrWhiteSpace(name)) return false;
        var copy = _c.SaveActiveSetupAs(name);
        Flash($"Saved as “{copy.Name}” - “{from}” is unchanged");
        return true;
    }

    private void SetupSave_Click(object s, RoutedEventArgs e) => SaveActive();

    private void SetupRevert_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || !_c.IsActiveDirty) return;
        if (MessageBox.Show($"Discard all unsaved changes in “{_c.ActiveSetup.Name}”?", "Discard changes",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _c.RevertSetup(_c.ActiveSetup);
        Flash($"Back to the saved state of “{_c.ActiveSetup.Name}”");
    }

    private void SetupMenu_Click(object s, RoutedEventArgs e)
    {
        bool dirty = _c?.IsActiveDirty == true;
        // The game's own profile stays: it can be changed and saved as another profile, not deleted or renamed.
        bool game = _c?.ActiveSetup.IsProtected == true;
        // Built-in profiles are never overwritten, so there is no plain "Save" for them.
        MenuSave.IsEnabled = dirty && !game;
        MenuSave.ToolTip = game ? "Built-in profile - use “Save as new profile…”" : null;
        MenuRevert.IsEnabled = dirty;
        MenuRename.IsEnabled = !game;
        MenuDelete.IsEnabled = !game;
        MenuDelete.ToolTip = game ? "Built-in profile - it cannot be deleted" : null;
        MenuRename.ToolTip = game ? "Built-in profile - it cannot be renamed" : null;
        SetupMenu.PlacementTarget = SetupMenuBtn;
        SetupMenu.IsOpen = true;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void SetupNew_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        var name = Controls.InputDialog.Ask(OwnerWindow, "New profile", "Name of the new (empty) profile:", "New profile");
        if (name != null) _c?.NewSetup(name);
    }

    private void SetupSaveAs_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        var from = _c.ActiveSetup.Name;
        var name = Controls.InputDialog.Ask(OwnerWindow, "Save as new profile",
            "Name of the new profile (the current bindings are copied into it):", from + " 2");
        if (string.IsNullOrWhiteSpace(name)) return;
        var copy = _c.SaveActiveSetupAs(name);
        Flash($"Saved as “{copy.Name}” - “{from}” is unchanged");
    }

    private void SetupRename_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _c.ActiveSetup.IsProtected) return;
        var name = Controls.InputDialog.Ask(OwnerWindow, "Rename profile", "New name:", _c.ActiveSetup.Name);
        if (name != null) _c.RenameSetup(_c.ActiveSetup, name);
    }

    private void SetupDelete_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        if (_c.ActiveSetup.IsProtected)
        {
            MessageBox.Show("This is a built-in profile, so it cannot be deleted.\n\n" +
                            "You can change it and use “Save as new profile…” to keep a variant of your own.",
                "Delete profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
        if (dlg.ShowDialog() != true || !ConfirmLeave()) return;
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
        ActionMapsFile? file;
        try { file = _c.LoadActionMaps(); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read actionmaps.xml: " + ex.Message, "Read Star Citizen",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // Reading replaces this profile's bindings - say so when that would throw work away.
        if (_c.IsActiveDirty && MessageBox.Show(
                $"“{_c.ActiveSetup.Name}” has unsaved changes.\n\n" +
                "Reading replaces this profile's bindings with what Star Citizen has now, so those changes are lost.\n\n" +
                "Tip: use ⋯ → “Save as new profile…” first to keep them.",
                "Read Star Citizen", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        var (setup, imported, skipped) = _c.ReadIntoActiveSetup(file);
        var extra = skipped > 0 ? $" ({skipped} skipped - no matching stick)" : "";
        Flash(imported == 0
            ? $"Star Citizen has no custom bindings - “{setup.Name}” now shows the game defaults"
            : $"Read {imported} binding(s) from Star Citizen into “{setup.Name}”{extra}");
    }

    private void Deploy_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null)
        {
            MessageBox.Show("Star Citizen was not found. Pick the installation in the Settings tab.", "Deploy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ScLocator.IsGameRunning())
        {
            MessageBox.Show(GameRunningHint + "\n\nClose Star Citizen, then deploy again.", "Deploy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (!_openCats.Remove(name)) _openCats.Add(name);
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
            if (a != null) _action = a;
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

    // ---- side panel: details of the selected chip ------------------------------------------------

    private string? _selId;

    private TagVM? SelectedTag() =>
        _selId == null ? null : _actionTags.Values.SelectMany(l => l).FirstOrDefault(t => t.Id == _selId);

    private void Tag_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).Tag is not TagVM t) return;
        e.Handled = true;
        _selId = t.Id;
        UpdatePanel();
    }

    private void PanelClose_Click(object s, RoutedEventArgs e)
    {
        _selId = null;
        UpdatePanel();
    }

    private void UpdatePanel()
    {
        foreach (var t in _actionTags.Values.SelectMany(l => l)) t.Selected = t.Id == _selId;
        var tag = SelectedTag();
        if (tag == null)
        {
            _selId = null;
            Panel.Visibility = Visibility.Collapsed;
            return;
        }
        Panel.Visibility = Visibility.Visible;
        var def = _c?.Catalog?.Find(tag.Map, tag.Action);
        var grp = _c?.Catalog?.Groups.FirstOrDefault(g => g.Name == tag.Map);
        PanelAction.Text = def?.Label ?? tag.Action;
        PanelPath.Text = grp != null ? $"{grp.Category} › {grp.Label}" : tag.Map;
        PanelInput.Text = $"{tag.Kind}  ·  {tag.Text}";
        PanelSource.Text = tag.Source;

        _loading = true;
        ModeList.SelectedItem = ModeOptions.FirstOrDefault(m => m.Value == tag.Mode) ?? ModeOptions[0];
        _loading = false;
        ModeList.IsEnabled = !tag.IsAxis;
        PanelModeNote.Text = tag.IsAxis
            ? "Axes are always continuous - they have no trigger mode."
            : def?.ActivationMode is { } dm && tag.Mode == null
                ? $"Game default for this action: {dm.Replace('_', ' ')}."
                : "Written to Star Citizen when you deploy. Only “Default” is thoroughly tested - check others in the game.";
    }

    private void ModeList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || _c == null || ModeList.SelectedItem is not ModeOption opt || SelectedTag() is not { } t) return;
        if (t.Raw == null || !_c.SetBindingMode(t.Map, t.Action, t.Raw, opt.Value))
            Flash("That binding belongs to a device that is not linked to a stick (Sticks tab).");
    }

    private void PanelRemove_Click(object s, RoutedEventArgs e)
    {
        if (SelectedTag() is { } t) RemoveTag(t);
    }

    private void PanelRebind_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Catalog == null || SelectedTag() is not { } t) return;
        var a = _c.Catalog.Find(t.Map, t.Action);
        var row = _rows.FirstOrDefault(r => r.Key == "act:" + t.Map + "/" + t.Action);
        if (a != null && row != null) StartBind(a, row);
    }

    // ---- remove a binding (x on a tag) ------------------------------------------------------

    private void TagRemove_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)s).Tag is TagVM t) RemoveTag(t);
    }

    private void RemoveTag(TagVM t)
    {
        if (_c == null) return;
        if (t.Id == _selId) _selId = null;
        if (t.Raw != null && _c.RemoveBinding(t.Map, t.Action, t.Raw)) return;
        if (t.Profile != null && t.Bind != null) { _c.RemoveBind(t.Profile, t.Bind); return; }
        Flash("That binding belongs to a device that is not linked to a stick (Sticks tab).");
    }

    // ---- binding mode -----------------------------------------------------------------------
    // Double click an action, then press a key or stick button. Hold one and press another to make a
    // combination (keyboard: lctrl+r). Esc cancels. Releasing the first input commits what was pressed.

    private void Tree_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (_c?.Catalog == null || e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(Tree, src) is not ListBoxItem { DataContext: TreeRow { Kind: 2 } row }) return;
        var a = _c.Catalog.AllActions.FirstOrDefault(x => x.Key == row.Key[4..]);
        if (a == null) return;
        e.Handled = true;
        _action = a;
        StartBind(a, row);
    }

    private void StartBind(ActionDef a, TreeRow row)
    {
        EndBind();
        _bindAction = a;
        row.Capturing = true;
        _bindWindow = Window.GetWindow(this);
        if (_bindWindow != null)
        {
            _bindWindow.PreviewKeyDown += Bind_KeyDown;
            _bindWindow.PreviewKeyUp += Bind_KeyUp;
            _bindWindow.PreviewMouseDown += Bind_MouseDown;
            _bindWindow.PreviewMouseWheel += Bind_Wheel;
        }
        _c?.BeginAxisWatch(r => Dispatcher.BeginInvoke(() => BindAxis(r)));
        Flash("Press the key or button to bind - hold one and press another to combine, Esc cancels");
    }

    private void EndBind()
    {
        if (_bindWindow != null)
        {
            _bindWindow.PreviewKeyDown -= Bind_KeyDown;
            _bindWindow.PreviewKeyUp -= Bind_KeyUp;
            _bindWindow.PreviewMouseDown -= Bind_MouseDown;
            _bindWindow.PreviewMouseWheel -= Bind_Wheel;
            _bindWindow = null;
        }
        _c?.EndAxisWatch();
        foreach (var r in _rows) r.Capturing = false;
        _bindAction = null;
        _bindKeys.Clear();
        _bindDown.Clear();
        _bindJs = null;
        _bindJsMods = new();
    }

    private void Bind_KeyDown(object s, KeyEventArgs e)
    {
        if (_bindAction == null) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndBind(); Flash("Binding cancelled"); return; }
        var tok = Controls.KeyCaptureBox.TokenOf(key);
        if (tok == null) return;
        if (_bindJs != null) { EndBind(); Flash("Stick buttons and keys cannot be combined yet"); return; }
        if (_bindDown.Add(tok)) _bindKeys.Add(tok);
    }

    private void Bind_KeyUp(object s, KeyEventArgs e)
    {
        if (_bindAction == null) return;
        e.Handled = true;
        // A stick button is down: its release completes the binding, the modifier may be let go first.
        if (_bindJs != null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var tok = Controls.KeyCaptureBox.TokenOf(key);
        if (tok != null && _bindDown.Contains(tok)) CommitKeyboard(null);
    }

    private void Bind_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (_bindAction == null) return;
        var token = e.ChangedButton switch
        {
            MouseButton.Right => "mouse2",
            MouseButton.Middle => "mouse3",
            MouseButton.XButton1 => "mouse4",
            MouseButton.XButton2 => "mouse5",
            _ => null,
        };
        if (token == null) { EndBind(); return; }   // a left click anywhere else leaves the mode
        e.Handled = true;
        CommitKeyboard(token);
    }

    private void Bind_Wheel(object s, MouseWheelEventArgs e)
    {
        if (_bindAction == null || e.Delta == 0) return;
        e.Handled = true;
        CommitKeyboard(e.Delta > 0 ? "mwheel_up" : "mwheel_down");
    }

    private void CommitKeyboard(string? mouse)
    {
        if (_c == null || _bindAction == null) return;
        var a = _bindAction;
        // SC wants the modifiers first; one non-modifier key is all it can combine them with.
        var combo = string.Join("+", _bindKeys.Where(Controls.KeyCaptureBox.IsModifierToken)
            .Concat(_bindKeys.Where(k => !Controls.KeyCaptureBox.IsModifierToken(k)).Take(1)));
        var raw = ScInputCodes.BuildKbm(combo, mouse);
        EndBind();
        if (raw == null) return;
        _c.SetRawBind(_c.KeyboardMouseProfile, a.Map, a.Name, raw, null);
        _selId = $"{a.Map}/{a.Name}|{raw}";
        Flash($"Bound {ScInputCodes.Pretty(raw)} → {a.Label}");
    }

    private void BindJoystick(ScMapper.Core.Input.InputEvent e, StickRecord? stick)
    {
        if (_c == null || _bindAction == null) return;
        var a = _bindAction;
        if (e.Pressed)
        {
            if (stick == null)
            {
                EndBind();
                Flash("That device is not linked to a stick yet - open the Sticks tab first");
                return;
            }
            // A keyboard Shift/Ctrl/Alt held while the stick button goes down becomes its modifier
            // ("lshift+button1"); anything else cannot be combined.
            if (_bindJs != null || !_bindKeys.All(Controls.KeyCaptureBox.IsModifierToken))
            {
                EndBind();
                Flash("Stick buttons can only be combined with Shift, Ctrl or Alt of the keyboard");
                return;
            }
            _bindJsMods = _bindKeys.ToList();
            _bindJs = (stick, e.Input);
        }
        else if (_bindJs is { } j && j.Input == e.Input && stick != null && j.Stick.Id == stick.Id)
        {
            var prof = _c.EnsureProfile(j.Stick);
            var input = ScInputCodes.ComposeJsInput(_bindJsMods, j.Input);
            EndBind();
            _c.SetBind(prof, a.Map, a.Name, input, null);
            if (j.Stick.ScInstance is { } inst) _selId = $"{a.Map}/{a.Name}|js{inst}_{input}";
            Flash($"Bound {j.Stick.Name} · {Controls.InputNames.Pretty(input)} → {a.Label}");
        }
    }

    private void BindAxis(CaptureResult r)
    {
        if (_c == null || _bindAction == null) return;
        var a = _bindAction;
        if (r.Stick == null)
        {
            EndBind();
            Flash("That device is not linked to a stick yet - open the Sticks tab first");
            return;
        }
        var prof = _c.EnsureProfile(r.Stick);
        EndBind();
        _c.SetBind(prof, a.Map, a.Name, r.Input, null);
        if (r.Stick.ScInstance is { } inst) _selId = $"{a.Map}/{a.Name}|js{inst}_{r.Input}";
        Flash($"Bound {r.Stick.Name} · {AxisNames.GetValueOrDefault(r.Input, r.Input)} → {a.Label}");
    }
}
