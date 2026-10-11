using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Input;
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
    /// <summary>Belongs to a stick that is not plugged in: kept, but not deployed.</summary>
    public bool IsInactive { get; init; }
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

    /// <summary>Name of the category / group this row sits in (a category row has only CatName).</summary>
    public string CatName { get; init; } = "";
    public string GroupName { get; init; } = "";
    /// <summary>Vertical guide lines on the left: one per ancestor level, clickable to collapse that ancestor.</summary>
    public Visibility Guide0Visibility => Kind >= 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Guide1Visibility => Kind >= 2 ? Visibility.Visible : Visibility.Collapsed;
    public string Guide0Tip => $"Collapse {CatName}";
    public string Guide1Tip => $"Collapse {GroupLabel}";
    public string GroupLabel { get; init; } = "";
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
    // Last press/release edge per input: "on press" and "on release" tags only flash for a moment.
    private readonly Dictionary<(Guid, string), (bool Pressed, DateTime Utc)> _edge = new();
    private static readonly TimeSpan EdgeFlash = TimeSpan.FromMilliseconds(350);
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
                // A button with actions is replaced by them: only the keys it sends exist here, never the button.
                if (stick != null && c.IsReplaced(stick, e.Input))
                {
                    _lit.Remove((stick.Id, e.Input));
                    OnVirtualKeys(c.VirtualKeys(stick, e.Input, e.Pressed));
                    return;
                }
                if (_bindAction != null) { BindJoystick(e, stick); return; }
                if (stick == null) return;
                var key = (stick.Id, e.Input);
                // Lit exactly as long as the input is held.
                if (e.Pressed) _lit[key] = DateTime.MaxValue; else _lit.Remove(key);
                _edge[key] = (e.Pressed, DateTime.UtcNow);
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
        var (cat, grp) = CatalogLayout.Place(a.Map, a.Name);
        _openCats.Add(cat);
        _open.Add(cat + "/" + grp);
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
    /// Every binding of every action as tags: what the active profile holds, then the game's defaults
    /// for whatever the profile does not replace. The game's actionmaps.xml only enters through Read SC.
    /// </summary>
    private Dictionary<string, List<TagVM>> BuildTags()
    {
        var map = new Dictionary<string, List<TagVM>>();
        if (_c == null) return map;
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
        void Layer(ControllerProfile layer, string kind)
        {
            foreach (var b in layer.Binds)
            {
                Override(b.ActionKey, b.Input);
                if (b.IsUnbound) continue;
                Add(b.ActionKey, new TagVM
                {
                    Kind = kind, Text = ScInputCodes.Pretty(b.Input), Map = b.Map, Action = b.Action, Raw = b.Input,
                    Profile = layer, Bind = b, Mode = b.Mode, Source = "Set by you in Star Button",
                });
            }
        }
        Layer(_c.KeyboardMouseProfile, "KB");
        Layer(_c.GamepadProfile, "PAD");
        foreach (var s in _c.Store.Data.Sticks)
        {
            var p = _c.Store.Profile(s.ProfileId);
            if (p == null || p.IsSystem) continue;
            // A stick that is not plugged in is inactive: not deployed, so it replaces nothing of the game's.
            bool active = _c.IsConnected(s);
            foreach (var b in p.Binds)
            {
                if (active && s.ScInstance is { } n) Override(b.ActionKey, $"js{n}_");
                if (b.IsUnbound) continue;
                Add(b.ActionKey, new TagVM
                {
                    Kind = "JS", Text = $"{p.Name} · {Controls.InputNames.Pretty(b.Input)}", StickId = s.Id, Input = b.Input,
                    Map = b.Map, Action = b.Action,
                    Raw = active && s.ScInstance is { } rn ? $"js{rn}_{b.Input}" : null,
                    Profile = p, Bind = b, Mode = b.Mode, IsInactive = !active,
                    Source = active ? "Set by you in Star Button" : "Set by you - this stick is not plugged in, so it is not deployed",
                });
            }
        }
        // The game's defaults come last; profiles without them ("Empty", "Keyboard & Mouse") leave out what they drop.
        if (_c.Catalog != null) AddDefaults(_c.Catalog, overridden, Add);
        return map;
    }

    /// <summary>
    /// The game's default bindings (defaultProfile.xml) fill in whatever this setup did not replace, so a
    /// profile that has not read the game's file shows what the game's controls menu shows out of the box.
    /// Joystick defaults are written for js1.
    /// </summary>
    private void AddDefaults(ActionCatalog cat, Dictionary<string, HashSet<string>> overridden, Action<string, TagVM> add)
    {
        foreach (var a in cat.AllActions)
        {
            overridden.TryGetValue(a.Key, out var o);
            bool Free(string fam) => (o == null || !o.Contains(fam)) && _c!.ActiveSetup.UsesDefaults(fam[..2]);
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
                var stick = _c!.StickByInstance(1);
                var what = AxisNames.TryGetValue(inp, out var ax) ? ax : Controls.InputNames.Pretty(inp);
                add(a.Key, new TagVM
                {
                    Kind = "JS", Text = $"{(stick == null ? "js1" : _c!.StickLabel(stick))} · {what}", StickId = stick?.Id, Input = inp, IsDefault = true,
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

        _groupActions = cat.Layout.ToDictionary(g => g.Key, g => g.Actions.Select(a => a.Key).ToList());
        _catGroups = new();
        int allBound = cat.AllActions.Count(a => _actionTags.ContainsKey(a.Key));
        _totals = $"{allBound} bound";

        foreach (var cg in cat.Layout.GroupBy(g => g.Category))
        {
            var groups = cg.Select(g => (Group: g, Actions: g.Actions.Where(Match).ToList())).Where(x => x.Actions.Count > 0).ToList();
            if (groups.Count == 0) continue;
            _catGroups[cg.Key] = cg.Select(g => g.Key).ToList();
            int catBound = cg.Sum(g => g.Actions.Count(a => _actionTags.ContainsKey(a.Key)));
            int catTotal = cg.Sum(g => g.Actions.Count);
            bool catOpen = searching || _openCats.Contains(cg.Key);
            rows.Add(new TreeRow
            {
                Kind = 0, Key = "cat:" + cg.Key, Text = cg.Key, Expanded = catOpen, CatName = cg.Key,
                Count = $"{catBound} / {catTotal}", HasBound = catBound > 0,
            });
            if (!catOpen) continue;
            foreach (var (g, actions) in groups)
            {
                bool open = searching || _open.Contains(g.Key);
                int bound = g.Actions.Count(a => _actionTags.ContainsKey(a.Key));
                rows.Add(new TreeRow
                {
                    Kind = 1, Key = "grp:" + g.Key, Text = g.Label, Expanded = open, CatName = cg.Key, GroupName = g.Key,
                    Count = $"{bound} / {g.Actions.Count}", HasBound = bound > 0,
                });
                if (!open) continue;
                foreach (var a in actions)
                    rows.Add(new TreeRow
                    {
                        Kind = 2, Key = "act:" + a.Key, Text = a.Label, Capturing = _bindAction?.Key == a.Key,
                        CatName = cg.Key, GroupName = g.Key, GroupLabel = g.Label,
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
        bool active = IsVisible && Window.GetWindow(this)?.IsActive == true;

        // Buttons are also read from the live device state, so a combo's button lights even when its
        // press edge was missed (e.g. another button of the same device was already held).
        var down = new HashSet<(Guid, string)>(_lit.Keys);
        if (active && _c != null)
            foreach (var s in _c.Store.Data.Sticks)
            {
                var st = _c.DeviceFor(s) is { } d ? _c.Hub.GetState(d.InstanceGuid) : null;
                if (st == null) continue;
                for (int i = 0; i < st.Buttons.Length; i++)
                    if (st.Buttons[i]) down.Add((s.Id, $"button{i + 1}"));
            }

        var mods = active ? HeldModifiers(down) : new HashSet<string>();
        // Keyboard keys light their chips only while nothing is being typed (search box, rename, ...)
        // and no binding is being captured; otherwise ordinary typing would make the table flicker.
        bool typing = !active || _bindAction != null
                       || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
                       || (Keyboard.FocusedElement as ComboBox)?.IsEditable == true
            ? true : false;

        // A button with actions of its own is replaced by them: it no longer exists here, only the keys Star
        // Button sends for it do (see OnVirtualKeys).
        var replaced = new HashSet<(Guid, string)>();
        if (active && _c != null)
            foreach (var s in _c.Store.Data.Sticks)
                if (_c.Store.Profile(s.ProfileId) is { } p)
                    foreach (var a in p.Actions) replaced.Add((s.Id, a.Input.ToLowerInvariant()));

        // A held key can only come from a button that is down: if none is, a release was missed - let go.
        if (_vkHeld.Count > 0 && !replaced.Any(down.Contains)) _vkHeld.Clear();

        var litActions = new HashSet<string>();
        foreach (var (key, tags) in _actionTags)
        {
            bool any = false;
            foreach (var t in tags)
            {
                t.Lit = false;
                if (active && t.StickId is { } id && t.Input != null)
                {
                    var (need, input) = ScInputCodes.SplitJsInput(t.Input);
                    var lowered = input.ToLowerInvariant();
                    if (replaced.Contains((id, lowered))) continue;
                    // "lshift+button1" lights only while lshift is really held as well.
                    // A press/release binding is an impulse, not a held state: it flashes at its edge only.
                    if (t.Mode is "press" or "release")
                        t.Lit = _edge.TryGetValue((id, lowered), out var edge)
                                && edge.Pressed == (t.Mode == "press")
                                && DateTime.UtcNow - edge.Utc < EdgeFlash && need.All(mods.Contains);
                    else
                        t.Lit = down.Contains((id, lowered)) && need.All(mods.Contains);
                }
                else if (!typing && t.Kind == "KB" && t.Raw is { } raw && KbRaw().IsMatch(raw))
                    t.Lit = KeysDown(raw[(raw.IndexOf('_') + 1)..]);
                // Keys Star Button sends for a button: a tap is too short for the real key state to be seen.
                if (active && !t.Lit && t.Kind == "KB" && t.Raw is { } vraw && KbRaw().IsMatch(vraw))
                    t.Lit = VirtualDown(vraw[(vraw.IndexOf('_') + 1)..]);
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

    // Keys that Star Button sends in place of buttons that have actions: held ones, and recent taps.
    private readonly HashSet<string> _vkHeld = new();
    private readonly Dictionary<string, DateTime> _vkFlash = new();

    private static IEnumerable<string> TokensOf(string combo) =>
        combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ScInputCodes.ToScToken);

    /// <summary>True when every key of a combo is held or was tapped a moment ago by a button's action.</summary>
    private bool VirtualDown(string combo)
    {
        bool any = false;
        foreach (var t in TokensOf(combo))
        {
            var tok = t.ToLowerInvariant();
            bool on = _vkHeld.Contains(tok) || (_vkFlash.TryGetValue(tok, out var at) && DateTime.UtcNow - at < EdgeFlash);
            if (!on) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// A button with actions arrives as the keys it sends: they light their tags and, in binding mode, are
    /// bound exactly as if they had been typed on the keyboard. The button itself is never seen.
    /// </summary>
    private void OnVirtualKeys(IReadOnlyList<VirtualKey> keys)
    {
        foreach (var k in keys)
        {
            var tokens = TokensOf(k.Combo).Select(t => t.ToLowerInvariant()).ToList();
            foreach (var t in tokens)
            {
                _vkFlash[t] = DateTime.UtcNow;
                if (k.Phase == VirtualPhase.Down) _vkHeld.Add(t);
                else if (k.Phase == VirtualPhase.Up) _vkHeld.Remove(t);
            }
            if (_bindAction == null) continue;
            if (_bindJs != null) { EndBind(); Flash("Stick buttons and keys cannot be combined yet"); continue; }
            if (k.Phase != VirtualPhase.Up)
                foreach (var t in tokens)
                    if (_bindDown.Add(t)) _bindKeys.Add(t);
            if (k.Phase == VirtualPhase.Tap || (k.Phase == VirtualPhase.Up && tokens.Any(_bindDown.Contains)))
                CommitKeyboard(null);
        }
        ApplyLighting();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    [System.Text.RegularExpressions.GeneratedRegex(@"^kb\d+_.+")]
    private static partial System.Text.RegularExpressions.Regex KbRaw();

    private static readonly Dictionary<string, int?> VkCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows virtual key of a Star Citizen key token ("u", "lshift", "np_1", "pgup"); null = unknown.</summary>
    private static int? VkOf(string token)
    {
        if (VkCache.TryGetValue(token, out var cached)) return cached;
        var name = token.Length == 4 && token.StartsWith("np_", StringComparison.OrdinalIgnoreCase) && char.IsDigit(token[3])
            ? "np" + token[3] : token;
        int? vk = null;
        if (KeyCombo.TryParse(name, out var keys, out _) && keys.Count == 1)
        {
            // MAPVK_VSC_TO_VK_EX; extended keys carry the 0xE000 prefix.
            var v = MapVirtualKey((uint)(keys[0].Scan | (keys[0].Extended ? 0xE000 : 0)), 3);
            if (v != 0) vk = (int)v;
        }
        return VkCache[token] = vk;
    }

    /// <summary>True when every key of a combo ("lshift+u") is physically down right now.</summary>
    private static bool KeysDown(string combo)
    {
        bool any = false;
        foreach (var t in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (VkOf(t) is not { } vk || (GetAsyncKeyState(vk) & 0x8000) == 0) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Keyboard modifiers ("lshift", "rctrl", ...) that are down right now: the real keyboard, plus
    /// stick buttons whose profile output holds modifier keys (a "layer" button on any stick).
    /// </summary>
    private HashSet<string> HeldModifiers(HashSet<(Guid, string)> down)
    {
        var mods = new HashSet<string>();
        var kb = new (System.Windows.Input.Key Key, string Name)[]
        {
            (System.Windows.Input.Key.LeftShift, "lshift"), (System.Windows.Input.Key.RightShift, "rshift"),
            (System.Windows.Input.Key.LeftCtrl, "lctrl"), (System.Windows.Input.Key.RightCtrl, "rctrl"),
            (System.Windows.Input.Key.LeftAlt, "lalt"), (System.Windows.Input.Key.RightAlt, "ralt"),
        };
        foreach (var (key, name) in kb)
            if (System.Windows.Input.Keyboard.IsKeyDown(key)) mods.Add(name);
        if (_c == null) return mods;

        void AddCombo(string? combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            var tokens = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ScInputCodes.ToScToken).ToList();
            // Only pure modifier outputs act as a layer; "lshift+d" is a normal key press.
            if (tokens.Count > 0 && tokens.All(ScInputCodes.IsKeyModifier)) mods.UnionWith(tokens);
        }

        foreach (var s in _c.Store.Data.Sticks)
        {
            var p = _c.Store.Profile(s.ProfileId);
            if (p == null) continue;
            foreach (var a in p.Actions)
            {
                if (a.Trigger != ButtonTrigger.Held || !down.Contains((s.Id, a.Input.ToLowerInvariant()))) continue;
                AddCombo(a.Keys ?? (a.Map != null && a.Action != null ? _c.KeyFor(a.Map, a.Action).Combo : null));
            }
            foreach (var (input, rule) in p.Rules)
                if (rule.OnPress is { Hold: true } hold && down.Contains((s.Id, input.ToLowerInvariant())))
                    AddCombo(hold.Keys);
        }
        return mods;
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
        // Deploy writes the saved profile: with unsaved changes the file would get something else than what
        // the user keeps, so those have to be saved (or discarded) first.
        bool canDeploy = !gameRunning && !dirty;
        if (DeployBtn.IsEnabled != canDeploy || _gameWasRunning != gameRunning)
        {
            _gameWasRunning = gameRunning;
            DeployBtn.IsEnabled = canDeploy;
            DeployBtn.ToolTip = gameRunning ? "Close Star Citizen first - it would overwrite the deploy when it exits"
                : dirty ? "Save the profile first - Deploy writes the saved profile"
                : "Write this profile to Star Citizen";
            _statusDirty = true;
        }
        // A built-in profile that was not read yet is the starting point: Read SC is the next step.
        SetReadHint(_c.ActiveSetup.IsProtected && !dirty && _c.Install != null);
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
            ShowJsDrift();
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
        if (dirty)
        {
            SyncStatus.Text = _c.ActiveSetup.IsProtected
                ? $"✎ Unsaved changes - “{_c.ActiveSetup.Name}” can only be kept with Save as new… · deploy after saving"
                : "✎ Unsaved changes - save the profile to deploy it";
            SyncStatus.Foreground = (Brush)FindResource("WarnBrush");
            SyncStatus.ToolTip = "Deploy writes the saved profile. Save the changes (or discard them) first.";
        }
        else if (n == null)
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

    /// <summary>Tells when a stick's bindings are stored under another js number than the stick has now.</summary>
    private void ShowJsDrift()
    {
        if (_c == null) return;
        var drift = _c.JsDrift();
        if (drift.Count == 0) { JsDriftBox.Visibility = Visibility.Collapsed; return; }
        var parts = drift.Select(d => $"“{_c.StickLabel(d.Stick)}” is js{d.InFile} in Star Citizen's file, js{d.Now} now ({d.Binds} bindings)");
        JsDriftText.Text = "⚠ " + string.Join("; ", parts) +
                           ". Deploy moves them to the new number. If the game uses another number, change it under Joystick numbers.";
        JsDriftBox.Visibility = Visibility.Visible;
    }

    private int? _pending;
    private bool _gameWasRunning;
    private bool _readHint;

    /// <summary>Makes "Read SC" stand out with a slow glow - it is what the user has to press next.</summary>
    private void SetReadHint(bool on)
    {
        if (on == _readHint) return;
        _readHint = on;
        if (!on)
        {
            ReadBtn.ClearValue(StyleProperty);
            Controls.Glow.Stop(ReadBtn);
            ReadBtn.ToolTip = "Replace this profile's bindings with what Star Citizen currently has";
            return;
        }
        ReadBtn.Style = (Style)FindResource("Accent");
        Controls.Glow.Start(ReadBtn);
        ReadBtn.ToolTip = "Start here: show what Star Citizen currently has bound on top of this profile";
    }

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
        CheckConflicts();
    }

    /// <summary>After a profile was loaded: show what is off in it (nothing is changed without a click).</summary>
    private void CheckConflicts()
    {
        var c = _c;
        if (c == null) return;
        Dispatcher.BeginInvoke(() => Controls.ConflictDialog.ShowIfAny(OwnerWindow, c));
    }

    /// <summary>Unsaved changes: save them, throw them away, or stay. False = stay.</summary>
    private bool ConfirmLeave()
    {
        if (_c == null || !_c.IsActiveDirty) return true;
        var name = _c.ActiveSetup.Name;
        var save = _c.ActiveSetup.IsProtected ? "save them as a new profile" : "save them";
        var r = Controls.Msg.Show($"“{name}” has unsaved changes.\n\nYes: {save}\nNo: discard them\nCancel: stay here",
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
        if (Controls.Msg.Show($"Discard all unsaved changes in “{_c.ActiveSetup.Name}”?", "Discard changes",
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

    private void JsNumbers_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        // Changing a number only moves the jsN prefix of what a deploy writes; the pending count shows it.
        Controls.JoystickNumbersDialog.Show(OwnerWindow, _c);
    }

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
            Controls.Msg.Show("This is a built-in profile, so it cannot be deleted.\n\n" +
                            "You can change it and use “Save as new profile…” to keep a variant of your own.",
                "Delete profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_c.Setups.Count <= 1)
        {
            Controls.Msg.Show("The last profile cannot be deleted.", "Delete profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Controls.Msg.Show($"Delete profile “{_c.ActiveSetup.Name}”? Star Citizen's files are not touched.",
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
            CheckConflicts();
        }
        catch (Exception ex)
        {
            Controls.Msg.Show("Could not import: " + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Read_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        if (_c.Install == null || !File.Exists(_c.Install.ActionMapsPath))
        {
            Controls.Msg.Show("No Star Citizen actionmaps.xml was found. Start the game once or pick the installation in the Deploy tab.",
                "Read Star Citizen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ActionMapsFile? file;
        try { file = _c.LoadActionMaps(); }
        catch (Exception ex)
        {
            Controls.Msg.Show("Could not read actionmaps.xml: " + ex.Message, "Read Star Citizen",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // Reading replaces this profile's bindings - say so when that would throw work away.
        if (_c.IsActiveDirty && Controls.Msg.Show(
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
        CheckConflicts();
    }

    private void Deploy_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null)
        {
            Controls.Msg.Show("Star Citizen was not found. Pick the installation in the Settings tab.", "Deploy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ScLocator.IsGameRunning())
        {
            Controls.Msg.Show(GameRunningHint + "\n\nClose Star Citizen, then deploy again.", "Deploy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var plan = _c.Deploy(true);
            var changes = _c.PendingDetails() ?? new List<DeployChange>();
            // Open conflicts are shown as warnings; they never block the deploy.
            var warnings = plan.Warnings.Concat(_c.FindConflicts().Select(x =>
                x.Kind == ConflictKind.DirectBinding
                    ? $"{_c.StickLabel(x.Stick)} · {Controls.InputNames.Pretty(x.Input)} is replaced by {x.Replacement} but still bound to " +
                      $"{_c.Catalog?.Find(x.Map, x.Action)?.Label ?? x.Action}"
                    : $"{x.Replacement} on {_c.StickLabel(x.Stick)} · {Controls.InputNames.Pretty(x.Input)} has no key"))
                .ToList();
            if (!Controls.DeployPreviewDialog.Confirm(OwnerWindow, _c, changes, warnings)) return;
            var res = _c.Deploy(false);
            Flash($"Deployed {res.Added} bindings ✓" + (res.BackupPath != null ? $" · backup {Path.GetFileName(res.BackupPath)}" : ""));
        }
        catch (Exception ex)
        {
            Controls.Msg.Show("Deploy failed: " + ex.Message, "Deploy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>A guide line on the left of a row: collapses the category (level 0) or group (level 1) it belongs to.</summary>
    private void Guide_MouseDown(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (s is not FrameworkElement { DataContext: TreeRow row } fe) return;
        string headKey;
        if (fe.Tag as string == "0") { _openCats.Remove(row.CatName); headKey = "cat:" + row.CatName; }
        else { _open.Remove(row.GroupName); headKey = "grp:" + row.GroupName; }
        RebuildTree();
        // The header may be far above the viewport; bring it back so the user keeps their place.
        var head = _rows.FirstOrDefault(r => r.Key == headKey);
        if (head != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Tree.ScrollIntoView(head));
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
        PanelAction.Text = def?.Label ?? tag.Action;
        PanelPath.Text = _c?.Catalog?.PathOf(tag.Map, tag.Action) ?? tag.Map;
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
        // The keys a button would send are what gets bound here; none of them may reach the other windows.
        if (_c != null) _c.Engine.Suspended = true;
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
        if (_c != null) _c.Engine.Suspended = false;
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
        _selId = null;   // a fresh binding leaves nothing selected or open
        Flash($"Bound {ScInputCodes.Pretty(raw)} → {a.Label}");
    }

    private void BindJoystick(ScMapper.Core.Input.InputEvent e, StickRecord? stick)
    {
        if (_c == null || _bindAction == null) return;
        var a = _bindAction;
        if (e.Pressed)
        {
            if (stick == null || _c.Store.Profile(stick.ProfileId) is not { IsSystem: false })
            {
                EndBind();
                Flash("That stick has no profile yet - give it one in the Sticks tab first");
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
        else if (stick != null && _bindJs == null)
        {
            // The button (a toggle switch, say) was already down when the binding mode started, so its press was
            // never seen: letting go is what the user does to choose it. Same result as a press and release.
            var prof = _c.Store.Profile(stick.ProfileId);
            if (prof == null || prof.IsSystem) { EndBind(); Flash("That stick has no profile yet - give it one in the Sticks tab first"); return; }
            var input = ScInputCodes.ComposeJsInput(_bindKeys.Where(Controls.KeyCaptureBox.IsModifierToken).ToList(), e.Input);
            EndBind();
            _c.SetBind(prof, a.Map, a.Name, input, null);
            _selId = null;
            Flash($"Bound {_c.StickLabel(stick)} · {Controls.InputNames.Pretty(input)} → {a.Label}");
        }
        else if (_bindJs is { } j && j.Input == e.Input && stick != null && j.Stick.Id == stick.Id)
        {
            var prof = _c.Store.Profile(j.Stick.ProfileId);
            if (prof == null) { EndBind(); Flash("That stick has no profile yet - give it one in the Sticks tab first"); return; }
            var input = ScInputCodes.ComposeJsInput(_bindJsMods, j.Input);
            EndBind();
            _c.SetBind(prof, a.Map, a.Name, input, null);
            _selId = null;
            Flash($"Bound {_c.StickLabel(j.Stick)} · {Controls.InputNames.Pretty(input)} → {a.Label}");
        }
    }

    private void BindAxis(CaptureResult r)
    {
        if (_c == null || _bindAction == null) return;
        var a = _bindAction;
        var prof = r.Stick == null ? null : _c.Store.Profile(r.Stick.ProfileId);
        if (r.Stick == null || prof == null || prof.IsSystem)
        {
            EndBind();
            Flash("That stick has no profile yet - give it one in the Sticks tab first");
            return;
        }
        EndBind();
        _c.SetBind(prof, a.Map, a.Name, r.Input, null);
        _selId = null;
        Flash($"Bound {_c.StickLabel(r.Stick)} · {AxisNames.GetValueOrDefault(r.Input, r.Input)} → {a.Label}");
    }
}
