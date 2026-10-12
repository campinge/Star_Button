using System.Text.RegularExpressions;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;
using ScMapper.Core.Storage;

namespace ScMapper.Core;

public sealed record CaptureResult(DeviceInfo Device, StickRecord? Stick, string Input);

/// <summary>
/// UI-independent application core: ties together storage, live devices, the rule engine,
/// the SC action catalog and deploy. Raises events from background threads; UIs must marshal.
/// </summary>
public sealed partial class AppController : IDisposable
{
    private readonly object _gate = new();
    private Action<CaptureResult>? _capture;
    private Dictionary<Guid, StickRecord> _byDevice = new();

    public AppStore Store { get; }
    public IDeviceHub Hub { get; }
    public RuleEngine Engine { get; }
    public ScInstall? Install { get; private set; }
    public IReadOnlyList<ScInstall> Installs { get; private set; } = Array.Empty<ScInstall>();
    public ActionCatalog? Catalog { get; private set; }
    public string CatalogStatus { get; private set; } = "Not loaded";
    public MatchResult Match { get; private set; } = new(new Dictionary<Guid, DeviceInfo>(), Array.Empty<DeviceInfo>(), Array.Empty<StickRecord>());

    /// <summary>Sticks, devices, profiles or catalog changed.</summary>
    public event Action? Changed;
    /// <summary>A physical input edge (after matching). Stick is null for unknown devices.</summary>
    public event Action<InputEvent, StickRecord?>? InputSeen;
    public event Action<FiredRule>? RuleFired;
    /// <summary>
    /// A joystick nobody registered showed up while the app runs (once per device and session). Raised from the
    /// device thread; UIs must marshal. Nothing is created for it: the user decides.
    /// </summary>
    public event Action<DeviceInfo>? NewDeviceSeen;

    private bool _announceNew;
    private readonly HashSet<Guid> _announced = new();
    private HashSet<Guid> _presentDevices = new();

    public AppController(AppStore store, IDeviceHub hub, IKeyOutput output)
    {
        Store = store;
        Hub = hub;
        Engine = new RuleEngine(store, output, g => StickForDevice(g));
        Engine.Fired += f => RuleFired?.Invoke(f);
        Engine.ActionKey = (map, action) => KeyFor(map, action).Combo;
        Hub.DevicesChanged += () => { Reconcile(); };
        Hub.InputChanged += OnInput;
    }

    // ---- setups (what the user calls "profiles") ---------------------------------------------

    private Setup? _active;

    /// <summary>The setup that edits apply to and that Deploy writes.</summary>
    public Setup ActiveSetup
    {
        get { lock (_gate) return _active ??= EnsureSetupsLocked(); }
    }

    public IReadOnlyList<Setup> Setups
    {
        get { _ = ActiveSetup; return Store.Data.Setups; }
    }

    public ControllerProfile KeyboardMouseProfile => ActiveSetup.Keyboard;
    public ControllerProfile GamepadProfile => ActiveSetup.Gamepad;

    /// <summary>Stick layouts that can be assigned to a stick.</summary>
    public IEnumerable<ControllerProfile> StickProfiles => Store.Data.Profiles.Where(p => !p.IsSystem);

    private Setup EnsureSetupsLocked()
    {
        var d = Store.Data;
        if (d.Setups.Count == 0)
        {
            // First start of this version: fold the existing layers and stick assignments into "Default".
            var s = new Setup { Name = "Default", IsGameProfile = true };
            var kbm = d.Profiles.FirstOrDefault(p => p.Kind == ProfileKind.KeyboardMouse);
            var pad = d.Profiles.FirstOrDefault(p => p.Kind == ProfileKind.Gamepad);
            if (kbm != null) s.Keyboard = kbm;
            if (pad != null) s.Gamepad = pad;
            d.Profiles.RemoveAll(p => p.IsSystem);
            foreach (var st in d.Sticks.Where(x => x.ProfileId != null)) s.StickLayouts[st.Id] = st.ProfileId!.Value;
            d.Setups.Add(s);
            d.ActiveSetupId = s.Id;
            Store.Save();
        }
        // Data from before the flag existed: the first profile is the game's one.
        if (!d.Setups.Any(x => x.IsGameProfile))
        {
            d.Setups[0].IsGameProfile = true;
            Store.Save();
        }
        // "Default" is the game's own defaults. Older versions let it mirror the game's file; whatever it
        // holds from back then moves into a profile of its own.
        foreach (var g in d.Setups.Where(x => x.IsGameProfile && HoldsContent(x)).ToList())
        {
            var kept = new Setup
            {
                Name = UniqueSetupName("My bindings"), Keyboard = g.Keyboard, Gamepad = g.Gamepad,
                StickLayouts = g.StickLayouts, SavedState = g.SavedState, IgnoredConflicts = g.IgnoredConflicts,
            };
            d.Setups.Add(kept);
            var fresh = new Setup();
            g.Keyboard = fresh.Keyboard;
            g.Gamepad = fresh.Gamepad;
            g.StickLayouts = fresh.StickLayouts;
            g.IgnoredConflicts = fresh.IgnoredConflicts;
            g.SavedState = TakeSnapshot(g);
            if (d.ActiveSetupId == g.Id) d.ActiveSetupId = kept.Id;
            Store.Save();
        }
        // Built-in profiles ship with the app; their content is only ever the game's defaults, as far as the
        // profile keeps them.
        void EnsureBuiltIn(Func<Setup, bool> exists, string name, Action<Setup> init)
        {
            if (d.Setups.Any(x => x.IsBuiltIn && exists(x))) return;
            var names = d.Setups.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var s = new Setup { Name = names.Contains(name) ? name + " (built-in)" : name, IsBuiltIn = true };
            init(s);
            d.Setups.Add(s);
            Store.Save();
        }
        // "Empty": nothing is bound, not even the game's defaults.
        EnsureBuiltIn(x => x.NoDefaults, "Empty", s => s.NoDefaults = true);
        // "Keyboard & Mouse": the game's defaults as they come, only the joystick ones are empty.
        EnsureBuiltIn(x => x.NoStickDefaults, "Keyboard & Mouse", s => s.NoStickDefaults = true);
        // Built-in profiles hold nothing by definition; repair copies that picked up bindings (e.g. from a game import).
        // An assigned stick profile without content is no content: it must survive a restart.
        foreach (var e in d.Setups.Where(x => x.IsBuiltIn))
        {
            bool Holds(ControllerProfile? p) => p != null && (p.Binds.Count > 0 || p.Rules.Count > 0 || p.Actions.Count > 0);
            if (!Holds(e.Keyboard) && !Holds(e.Gamepad) && !e.StickLayouts.Values.Any(id => Holds(Store.Profile(id)))) continue;
            e.Keyboard.Binds.Clear(); e.Keyboard.Rules.Clear(); e.Keyboard.Actions.Clear();
            e.Gamepad.Binds.Clear(); e.Gamepad.Rules.Clear(); e.Gamepad.Actions.Clear();
            e.StickLayouts.Clear();
            e.SavedState = null;
            Store.Save();
        }
        var a = d.Setups.FirstOrDefault(x => x.Id == d.ActiveSetupId) ?? d.Setups[0];
        d.ActiveSetupId = a.Id;
        AdoptOrphanLayouts(a);
        AdoptOrphanProfiles(a);
        return a;
    }

    /// <summary>
    /// A stick profile no setup and no stick refers to is invisible (a stick lost it, e.g. while binding profiles
    /// were switched). It goes back to a stick of its model that has no profile, as far as the device it was last
    /// used on tells; otherwise it waits in a slot, to be linked to a stick by the user.
    /// </summary>
    private void AdoptOrphanProfiles(Setup active)
    {
        var d = Store.Data;
        var used = d.Setups.SelectMany(x => x.StickLayouts.Values).Concat(d.Sticks.Where(s => s.ProfileId != null).Select(s => s.ProfileId!.Value)).ToHashSet();
        bool changed = false;
        foreach (var p in d.Profiles.Where(p => !p.IsSystem && !used.Contains(p.Id)).ToList())
        {
            d.ProfileDevices.TryGetValue(p.Id, out var origin);
            var free = origin == null ? null : d.Sticks
                .Where(s => s.ProfileId == null && origin.SameModel(s.Vid, s.Pid) && !active.StickLayouts.ContainsKey(s.Id))
                .OrderByDescending(s => s.Buttons == origin.Buttons && s.Axes == origin.Axes)
                .FirstOrDefault();
            if (free != null) { free.ProfileId = p.Id; active.StickLayouts[free.Id] = p.Id; }
            else
            {
                var slot = new StickRecord { Name = p.Name, ProfileId = p.Id };
                d.Sticks.Add(slot);
                active.StickLayouts[slot.Id] = p.Id;
            }
            changed = true;
        }
        if (changed) Store.Save();
    }

    /// <summary>
    /// A setup can name sticks this machine has no record of (imported, or the stick was forgotten). Their profiles
    /// would be invisible; give each one a record without a device, so it shows up as a profile waiting for one.
    /// </summary>
    private void AdoptOrphanLayouts(Setup setup)
    {
        var sticks = Store.Data.Sticks;
        foreach (var (stickId, layoutId) in setup.StickLayouts.ToList())
        {
            if (sticks.Any(s => s.Id == stickId)) continue;
            var layout = Store.Profile(layoutId);
            if (layout == null || layout.IsSystem) { setup.StickLayouts.Remove(stickId); continue; }
            sticks.Add(new StickRecord { Id = stickId, Name = layout.Name, ProfileId = layoutId });
        }
    }

    /// <summary>A stick record that never saw a device: just a profile waiting to be given one.</summary>
    private static bool IsSlot(StickRecord s) =>
        s.Vid == 0 && s.Pid == 0 && s.InstanceGuid == null && string.IsNullOrEmpty(s.InterfacePath);

    /// <summary>True when the setup's saved content (its live content if it was never saved) binds anything.</summary>
    private bool HoldsContent(Setup s)
    {
        static bool Holds(ControllerProfile? p) => p != null && (p.Binds.Count > 0 || p.Rules.Count > 0 || p.Actions.Count > 0);
        if (s.SavedState != null)
        {
            try
            {
                var snap = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(s.SavedState);
                return snap != null && (Holds(snap.Keyboard) || Holds(snap.Gamepad) || snap.Layouts.Any(l => Holds(l.Layout)));
            }
            catch (System.Text.Json.JsonException) { return false; }
        }
        return Holds(s.Keyboard) || Holds(s.Gamepad) || s.StickLayouts.Values.Any(id => Holds(Store.Profile(id)));
    }

    /// <summary>StickRecord.ProfileId is the live layout of the active setup; mirror it into the setup.</summary>
    private void SyncActiveFromSticks()
    {
        var a = _active;
        if (a == null) return;
        // A profile belongs to one stick. Should two hold it (two records pointing at one layout), the connected
        // one keeps it; a profile is never driven by two devices.
        var seen = new HashSet<Guid>();
        foreach (var s in Store.Data.Sticks.Where(s => s.ProfileId != null).OrderByDescending(IsConnected).ToList())
            if (!seen.Add(s.ProfileId!.Value)) s.ProfileId = null;
        foreach (var s in Store.Data.Sticks)
        {
            if (s.ProfileId is { } g) a.StickLayouts[s.Id] = g;
            else a.StickLayouts.Remove(s.Id);
        }
        PruneResidue();
        // Keys of records that are gone (a slot that took a device, a forgotten stick) would show a profile twice.
        var ids = Store.Data.Sticks.Select(s => s.Id).ToHashSet();
        foreach (var k in a.StickLayouts.Keys.Where(k => !ids.Contains(k)).ToList()) a.StickLayouts.Remove(k);
    }

    /// <summary>A record that never saw a device, holds no profile and is in no setup is residue.</summary>
    private void PruneResidue()
    {
        var referenced = Store.Data.Setups.SelectMany(x => x.StickLayouts.Keys).ToHashSet();
        Store.Data.Sticks.RemoveAll(s => IsSlot(s) && s.ProfileId == null && !referenced.Contains(s.Id));
    }

    public void ActivateSetup(Setup setup)
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            SyncActiveFromSticks();
            _active = setup;
            Store.Data.ActiveSetupId = setup.Id;
            AdoptOrphanLayouts(setup);
            var pool = Store.Data.Profiles.Select(p => p.Id).ToHashSet();
            foreach (var s in Store.Data.Sticks)
            {
                if (setup.StickLayouts.TryGetValue(s.Id, out var l) && pool.Contains(l)) { s.ProfileId = l; continue; }
                // A stick and its profile are the app's own setup, not part of a binding profile: switching
                // binding profiles must not make the stick lose its profile. The new setup gets an empty layout
                // of the same name for it (names may repeat across setups; they are one profile to the user).
                if (Store.Profile(s.ProfileId) is { IsSystem: false } held)
                {
                    var layout = new ControllerProfile { Name = held.Name };
                    Store.Data.Profiles.Add(layout);
                    if (Store.Data.ProfileDevices.TryGetValue(held.Id, out var origin)) Store.Data.ProfileDevices[layout.Id] = origin;
                    setup.StickLayouts[s.Id] = layout.Id;
                    s.ProfileId = layout.Id;
                }
                else s.ProfileId = null;
            }
            PruneResidue();
            Store.Save();
        }
        Changed?.Invoke();
    }

    /// <summary>The layouts of one stick across all setups: one profile to the user, one layout per setup.</summary>
    private HashSet<Guid> SiblingLayouts(Guid layoutId)
    {
        var res = new HashSet<Guid> { layoutId };
        foreach (var stickId in Store.Data.Setups.SelectMany(x => x.StickLayouts).Where(kv => kv.Value == layoutId).Select(kv => kv.Key).ToList())
            foreach (var x in Store.Data.Setups)
                if (x.StickLayouts.TryGetValue(stickId, out var sib)) res.Add(sib);
        return res;
    }

    // ---- saved state of a setup -----------------------------------------------------------------
    // Edits go straight into the setup (so nothing is lost on a crash); "saved" is a snapshot next to it.

    private sealed class SnapshotLayout
    {
        public Guid StickId { get; set; }
        public ControllerProfile? Layout { get; set; }
    }

    private sealed class Snapshot
    {
        public ControllerProfile Keyboard { get; set; } = new();
        public ControllerProfile Gamepad { get; set; } = new();
        public List<SnapshotLayout> Layouts { get; set; } = new();
    }

    private string TakeSnapshot(Setup s)
    {
        lock (_gate)
        {
            if (ReferenceEquals(s, _active)) SyncActiveFromSticks();
            var snap = new Snapshot { Keyboard = s.Keyboard, Gamepad = s.Gamepad };
            // A binding profile holds the Star Citizen bindings only. What a stick button does by itself (actions,
            // modifier, names) belongs to the stick's own profile: it is neither saved nor discarded here.
            // A stick without bindings is no part of what the user saved.
            foreach (var kv in s.StickLayouts.OrderBy(k => k.Key))
                if (Store.Profile(kv.Value) is { } layout && !IsEmptyLayout(layout))
                    snap.Layouts.Add(new SnapshotLayout
                    {
                        StickId = kv.Key,
                        Layout = new ControllerProfile { Id = layout.Id, Name = layout.Name, Binds = layout.Binds },
                    });
            return System.Text.Json.JsonSerializer.Serialize(snap);
        }
    }

    /// <summary>A saved state written by an older version also held actions and names of the stick profiles.</summary>
    private static string Normalize(string saved)
    {
        try
        {
            var snap = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(saved);
            if (snap == null) return saved;
            foreach (var l in snap.Layouts)
                if (l.Layout != null) l.Layout = new ControllerProfile { Id = l.Layout.Id, Name = l.Layout.Name, Binds = l.Layout.Binds };
            snap.Layouts.RemoveAll(l => l.Layout == null || l.Layout.Binds.Count == 0);
            return System.Text.Json.JsonSerializer.Serialize(snap);
        }
        catch { return saved; }
    }

    private static bool IsEmptyLayout(ControllerProfile p) => p.Binds.Count == 0;

    /// <summary>True when the setup differs from what the user last saved.</summary>
    public bool IsSetupDirty(Setup s)
    {
        if (s.SavedState == null) { s.SavedState = TakeSnapshot(s); return false; }
        return TakeSnapshot(s) != Normalize(s.SavedState);
    }

    public bool IsActiveDirty => IsSetupDirty(ActiveSetup);

    public void SaveActiveSetup()
    {
        var a = ActiveSetup;
        a.SavedState = TakeSnapshot(a);
        Store.Save();
        Changed?.Invoke();
    }

    /// <summary>Throws away unsaved changes: the setup goes back to its last saved content.</summary>
    public void RevertSetup(Setup s)
    {
        if (s.SavedState == null) return;
        Snapshot? snap;
        try { snap = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(s.SavedState); }
        catch (System.Text.Json.JsonException) { return; }
        if (snap == null) return;
        lock (_gate)
        {
            static void Copy(ControllerProfile to, ControllerProfile from)
            {
                to.Binds = from.Binds; to.Rules = from.Rules; to.Actions = from.Actions; to.Name = from.Name;
            }
            Copy(s.Keyboard, snap.Keyboard);
            Copy(s.Gamepad, snap.Gamepad);
            // Only the Star Citizen bindings of the sticks go back. A stick keeps its profile, and the profile keeps
            // everything it does by itself: nothing is removed, replaced or unassigned by discarding changes.
            var restored = new HashSet<Guid>();
            foreach (var l in snap.Layouts)
                if (l.Layout != null && Store.Profile(l.Layout.Id) is { } existing)
                {
                    existing.Binds = l.Layout.Binds;
                    restored.Add(existing.Id);
                }
            // Profiles without bindings in the saved state had none: what was bound to them since is dropped.
            foreach (var id in s.StickLayouts.Values)
                if (!restored.Contains(id) && Store.Profile(id) is { IsSystem: false } held)
                    held.Binds = new();
            Store.Save();
        }
        Changed?.Invoke();
    }

    private string UniqueSetupName(string name)
    {
        var names = Store.Data.Setups.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!names.Contains($"{name} {i}")) return $"{name} {i}";
    }

    public Setup NewSetup(string? name = null)
    {
        _ = ActiveSetup;
        var s = new Setup { Name = UniqueSetupName(name ?? "New profile") };
        s.SavedState = TakeSnapshot(s);
        Store.Data.Setups.Add(s);
        ActivateSetup(s);
        return s;
    }

    private static T Clone<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value))!;

    private ControllerProfile CloneLayout(ControllerProfile src)
    {
        var c = Clone(src);
        c.Id = Guid.NewGuid();
        if (!c.IsSystem) c.Name = UniqueProfileName(src.Name);
        return c;
    }

    public Setup DuplicateSetup(Setup src)
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            SyncActiveFromSticks();
            var s = new Setup
            {
                Name = UniqueSetupName(src.Name + " copy"),
                NoDefaults = src.NoDefaults,
                NoStickDefaults = src.NoStickDefaults,
                Keyboard = CloneLayout(src.Keyboard),
                Gamepad = CloneLayout(src.Gamepad),
            };
            foreach (var (stickId, layoutId) in src.StickLayouts)
            {
                var layout = Store.Profile(layoutId);
                if (layout == null) continue;
                var c = CloneLayout(layout);
                c.Name = layout.Name;   // the same profile in another setup keeps its name
                Store.Data.Profiles.Add(c);
                s.StickLayouts[stickId] = c.Id;
            }
            s.SavedState = TakeSnapshot(s);
            Store.Data.Setups.Add(s);
            Store.Save();
            return s;
        }
    }

    public void RenameSetup(Setup s, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || s.IsProtected) return;
        s.Name = name.Trim();
        Store.Save();
        Changed?.Invoke();
    }

    /// <summary>Deletes a setup and the stick layouts nobody else uses. The last setup cannot be deleted.</summary>
    public bool DeleteSetup(Setup s)
    {
        bool wasActive;
        lock (_gate)
        {
            _ = ActiveSetup;
            if (s.IsProtected || Store.Data.Setups.Count <= 1) return false;
            wasActive = ReferenceEquals(_active, s);
            // The sticks' live layouts belong to the setup being deleted: take them over into it first, so the
            // switch below does not write layouts that are about to disappear into the setup that takes over.
            if (wasActive) SyncActiveFromSticks();
            Store.Data.Setups.Remove(s);
            var used = Store.Data.Setups.SelectMany(x => x.StickLayouts.Values).ToHashSet();
            foreach (var l in s.StickLayouts.Values.Where(l => !used.Contains(l)).ToList())
                Store.Data.Profiles.RemoveAll(p => p.Id == l);
            if (wasActive) Store.Data.ActiveSetupId = Store.Data.Setups[0].Id;
            Store.Save();
        }
        // _active still points at the deleted setup, so ActivateSetup syncs into that one and not into the target.
        if (wasActive) ActivateSetup(Store.Data.Setups[0]);
        else Changed?.Invoke();
        return true;
    }

    /// <summary>Self-contained JSON of a setup including the stick layouts it uses.</summary>
    public string ExportSetup(Setup s)
    {
        SyncActiveFromSticks();
        var layouts = s.StickLayouts.Values.Select(id => Store.Profile(id)).Where(p => p != null).Select(p => p!).ToList();
        var shared = Clone(s);
        shared.SavedState = null;
        return System.Text.Json.JsonSerializer.Serialize(new { Setup = shared, Layouts = layouts },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public Setup ImportSetup(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var s = Clone<Setup>(System.Text.Json.JsonSerializer.Deserialize<Setup>(doc.RootElement.GetProperty("Setup").GetRawText())!);
        s.SavedState = null;
        s.IsGameProfile = false;
        s.IsBuiltIn = false;
        var layouts = System.Text.Json.JsonSerializer.Deserialize<List<ControllerProfile>>(
            doc.RootElement.GetProperty("Layouts").GetRawText()) ?? new();
        lock (_gate)
        {
            _ = ActiveSetup;
            s.Id = Guid.NewGuid();
            s.Name = UniqueSetupName(s.Name);
            s.Keyboard.Id = Guid.NewGuid();
            s.Gamepad.Id = Guid.NewGuid();
            var map = new Dictionary<Guid, Guid>();
            foreach (var l in layouts)
            {
                var old = l.Id;
                l.Id = Guid.NewGuid();
                l.Name = UniqueProfileName(l.Name);
                Store.Data.Profiles.Add(l);
                map[old] = l.Id;
            }
            foreach (var k in s.StickLayouts.Keys.ToList())
                if (map.TryGetValue(s.StickLayouts[k], out var nid)) s.StickLayouts[k] = nid; else s.StickLayouts.Remove(k);
            s.SavedState = TakeSnapshot(s);
            Store.Data.Setups.Add(s);
            Store.Save();
        }
        Changed?.Invoke();
        return s;
    }

    /// <summary>
    /// Makes the ACTIVE setup mirror Star Citizen's actionmaps.xml: its bindings are replaced by what the
    /// game's file holds. Keyboard-output rules and split buttons are not part of the game and are kept.
    /// </summary>
    public (Setup Setup, int Imported, int Skipped) ReadIntoActiveSetup(ActionMapsFile? file = null)
    {
        file ??= LoadActionMaps();
        Setup setup;
        lock (_gate)
        {
            setup = ActiveSetup;
            setup.Keyboard.Binds.Clear();
            setup.Gamepad.Binds.Clear();
            // An unplugged stick cannot take bindings back from the file (its js number is not its own right now),
            // so its profile is left alone instead of being emptied.
            foreach (var st in Store.Data.Sticks.Where(IsConnected))
                if (Store.Profile(st.ProfileId) is { } layout) layout.Binds.Clear();
        }
        var (imported, skipped) = file == null ? (0, 0) : ImportFromActionMaps(file);
        return (setup, imported, skipped);
    }

    /// <summary>
    /// Stores the current content as a new profile and switches to it. The profile it came from goes back to
    /// its last saved state, so "Profile 1 + a small change" becomes "Profile 2" and Profile 1 stays as it was.
    /// </summary>
    public Setup SaveActiveSetupAs(string name)
    {
        var src = ActiveSetup;
        var copy = DuplicateSetup(src);
        RenameSetup(copy, name);
        copy.SavedState = TakeSnapshot(copy);
        RevertSetup(src);
        ActivateSetup(copy);
        return copy;
    }

    private ControllerProfile NewLayoutFor(StickRecord s, string? name = null)
    {
        var p = new ControllerProfile { Name = UniqueProfileName(name ?? s.Name) };
        Store.Data.Profiles.Add(p);
        s.ProfileId = p.Id;
        return p;
    }

    /// <summary>
    /// For a profile without the game's defaults: an "unbound" marker for every default binding of the game
    /// (keyboard, mouse, gamepad, and the first joystick) of the device families this profile does not use.
    /// Null for normal profiles.
    /// </summary>
    private IReadOnlyList<(string Map, string Action, string Input)>? UnboundDefaults()
    {
        var setup = ActiveSetup;
        if (Catalog == null || (setup.UsesDefaults("kb") && setup.UsesDefaults("gp") && setup.UsesDefaults("js"))) return null;
        var list = new List<(string, string, string)>();
        foreach (var a in Catalog.AllActions)
        {
            if (!setup.UsesDefaults("kb") && !string.IsNullOrWhiteSpace(a.DefaultKeyboard)) list.Add((a.Map, a.Name, "kb1_ "));
            if (!setup.UsesDefaults("mo") && !string.IsNullOrWhiteSpace(a.DefaultMouse)) list.Add((a.Map, a.Name, "mo1_ "));
            if (!setup.UsesDefaults("gp") && !string.IsNullOrWhiteSpace(a.DefaultGamepad)) list.Add((a.Map, a.Name, "gp1_ "));
            if (!setup.UsesDefaults("js") && a.Joystick && !string.IsNullOrWhiteSpace(a.DefaultJoystick)) list.Add((a.Map, a.Name, "js1_ "));
        }
        return list;
    }

    /// <summary>
    /// Sticks that take part in a deploy: the connected ones. An unplugged stick is inactive - its bindings are
    /// not written (and the ones written earlier are removed), and its last js number, which another device may
    /// hold now, is never used.
    /// </summary>
    private List<StickRecord> DeploySticks() =>
        Store.Data.Sticks.Where(IsConnected).ToList();

    private IEnumerable<ControllerProfile> ProfilesForDeploy() =>
        Store.Data.Profiles.Where(p => !p.IsSystem).Concat(new[] { KeyboardMouseProfile, GamepadProfile });

    /// <summary>Number of rebinds that differ between the active setup and Star Citizen's file (null = no install).</summary>
    public int? PendingChanges()
    {
        var inst = Install;
        if (inst == null) return null;
        try
        {
            var d = Store.Data;
            var plan = Deployer.Apply(inst.ActionMapsPath, Store.BackupDir, DeploySticks(), ProfilesForDeploy(),
                d.ManagedKeys, dryRun: true, new ManagedKeys(d.ManagedKeys, d.ManagedKbmKeys, d.ManagedGamepadKeys),
                UnboundDefaults());
            static HashSet<string> Keys(ActionMapsFile f) => f.Rebinds()
                .Select(r => $"{r.Map}/{r.Action}|{r.Input}|{r.ActivationMode}|{r.MultiTap}").ToHashSet();
            var cur = Keys(ActionMapsFile.LoadOrEmpty(inst.ActionMapsPath));
            var next = Keys(plan.Result!);
            return next.Except(cur).Count() + cur.Except(next).Count();
        }
        catch { return null; }
    }

    /// <summary>
    /// What a deploy would change, per action: the rebinds of Star Citizen's file that go away and the ones that
    /// come in. Unchanged rebinds are left out. Null = no install or the file cannot be compared.
    /// </summary>
    public IReadOnlyList<DeployChange>? PendingDetails()
    {
        var inst = Install;
        if (inst == null) return null;
        try
        {
            var d = Store.Data;
            var plan = Deployer.Apply(inst.ActionMapsPath, Store.BackupDir, DeploySticks(), ProfilesForDeploy(),
                d.ManagedKeys, dryRun: true, new ManagedKeys(d.ManagedKeys, d.ManagedKbmKeys, d.ManagedGamepadKeys),
                UnboundDefaults());
            static Dictionary<string, RebindState> States(ActionMapsFile f) => f.Rebinds()
                .Select(r => (Key: $"{r.Map}/{r.Action}", State: new RebindState(r.Input,
                    r.MultiTap == "2" ? "double_tap" : r.ActivationMode)))
                .Distinct()
                .ToDictionary(x => $"{x.Key}|{x.State.Input}|{x.State.Mode}", x => x.State);
            var cur = States(ActionMapsFile.LoadOrEmpty(inst.ActionMapsPath));
            var next = States(plan.Result!);
            var byAction = new Dictionary<string, (List<RebindState> Removed, List<RebindState> Added)>();
            void Put(string full, RebindState s, bool added)
            {
                var key = full[..full.IndexOf('|')];
                if (!byAction.TryGetValue(key, out var e)) byAction[key] = e = (new(), new());
                (added ? e.Added : e.Removed).Add(s);
            }
            foreach (var (k, s) in cur) if (!next.ContainsKey(k)) Put(k, s, false);
            foreach (var (k, s) in next) if (!cur.ContainsKey(k)) Put(k, s, true);
            return byAction.Select(kv =>
            {
                int i = kv.Key.IndexOf('/');
                return new DeployChange(kv.Key[..i], kv.Key[(i + 1)..], kv.Value.Removed, kv.Value.Added);
            }).ToList();
        }
        catch { return null; }
    }

    public void Start()
    {
        _ = ActiveSetup;
        MigrateRules();
        DiscoverInstall();
        Hub.Start();
        Reconcile();
        // What is plugged in at start is for the start-up dialog; only later arrivals are announced.
        lock (_gate)
        {
            foreach (var d in Match.Unmatched) _announced.Add(d.InstanceGuid);
            _announceNew = true;
        }
        _ = LoadCatalogAsync(false);
    }
    // ---- install / catalog -----------------------------------------------------------

    public void DiscoverInstall()
    {
        var s = Store.Data.Settings;
        // Always scan the usual places; a folder the user picked by hand is added on top.
        var found = ScLocator.FindAll().ToList();
        if (!string.IsNullOrEmpty(s.ScRoot))
            found.AddRange(ScLocator.FindAll(s.ScRoot).Where(m => !found.Any(f =>
                f.Channel.Equals(m.Channel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(f.Root), Path.GetFullPath(m.Root), StringComparison.OrdinalIgnoreCase))));
        Installs = found;
        bool SameChannel(ScInstall i) => i.Channel.Equals(s.Channel, StringComparison.OrdinalIgnoreCase);
        Install = Installs.FirstOrDefault(i => SameChannel(i) &&
                      string.Equals(i.Root, s.ScRoot, StringComparison.OrdinalIgnoreCase))
                  ?? Installs.FirstOrDefault(SameChannel)
                  ?? Installs.FirstOrDefault();
        Changed?.Invoke();
    }

    public void SelectInstall(ScInstall install)
    {
        Install = install;
        Store.Data.Settings.ScRoot = install.Root;
        Store.Data.Settings.Channel = install.Channel;
        Store.Save();
        Catalog = null;
        _ = LoadCatalogAsync(false);
        Changed?.Invoke();
    }

    private readonly object _catalogLock = new();
    private Task? _catalogTask;
    private ScInstall? _catalogFor;
    private int _catalogGeneration;

    /// <summary>
    /// Builds or loads the action catalog. A second call for the same installation while one is running joins it
    /// (several clicks on "Re-read" are one scan); a call for another installation supersedes the running one, whose
    /// result is dropped when it arrives.
    /// </summary>
    public Task LoadCatalogAsync(bool force)
    {
        lock (_catalogLock)
        {
            if (_catalogTask is { IsCompleted: false } running && Equals(_catalogFor, Install)) return running;
            _catalogFor = Install;
            return _catalogTask = Task.Run(() => LoadCatalogCore(force));
        }
    }

    private async Task LoadCatalogCore(bool force)
    {
        var inst = Install;
        int gen = Interlocked.Increment(ref _catalogGeneration);
        if (inst == null) { CatalogStatus = "Star Citizen installation not found"; Changed?.Invoke(); return; }
        CatalogStatus = "Reading game data...";
        Changed?.Invoke();
        try
        {
            var cat = await Task.Run(() => ActionCatalog.LoadOrBuild(inst, Store.CacheDir, force,
                m => { if (gen == _catalogGeneration) { CatalogStatus = m; Changed?.Invoke(); } }));
            if (gen != _catalogGeneration) return;   // another installation was chosen meanwhile
            Catalog = cat;
            CatalogStatus = $"{cat.AllActions.Count()} actions in {cat.Groups.Count} groups (build {cat.BuildVersion})";
        }
        catch (Exception ex)
        {
            if (gen != _catalogGeneration) return;
            CatalogStatus = "Failed: " + ex.Message;
        }
        Changed?.Invoke();
    }

    // ---- devices & sticks ----------------------------------------------------------------

    public StickRecord? StickForDevice(Guid instanceGuid)
    {
        lock (_gate) return _byDevice.GetValueOrDefault(instanceGuid);
    }

    public DeviceInfo? DeviceFor(StickRecord s) => Match.Matched.GetValueOrDefault(s.Id);

    public bool IsConnected(StickRecord s) => Match.Matched.ContainsKey(s.Id);

    /// <summary>What the user calls this stick: the name of its profile ("Power und Comms"), else the device name.</summary>
    public string StickLabel(StickRecord s) => Store.Profile(s.ProfileId)?.Name ?? s.Name;

    /// <summary>The device the profile was last used on; null when it never was.</summary>
    public ProfileDevice? OriginOf(ControllerProfile p)
    {
        lock (_gate) return Store.Data.ProfileDevices.GetValueOrDefault(p.Id);
    }

    /// <summary>
    /// Whether the profile suits the device. A mismatch is a reason to ask the user, never to refuse: a broken
    /// button or a replacement stick of another model are legitimate.
    /// </summary>
    public ProfileFit CheckFit(ControllerProfile p, DeviceInfo d) => ProfileFitCheck.Check(p, OriginOf(p), d);

    /// <summary>Re-run matching after devices or records changed; auto-register unambiguous new devices.</summary>
    public void Reconcile()
    {
        List<DeviceInfo>? announce = null;
        List<Guid>? vanished = null;
        lock (_gate)
        {
            var devices = Hub.Devices;
            var nowPresent = devices.Select(d => d.InstanceGuid).ToHashSet();
            vanished = _presentDevices.Where(g => !nowPresent.Contains(g)).ToList();
            _presentDevices = nowPresent;
            var data = Store.Data;
            var m = DeviceMatcher.Match(data.Sticks, devices);

            // A stick keeps its device name; older versions overwrote it with the profile name.
            foreach (var s in data.Sticks)
            {
                var model = s.ProductName.Trim();
                if (model.Length == 0 || s.Name == model || s.Name.StartsWith(model + " ", StringComparison.Ordinal)) continue;
                var taken = data.Sticks.Where(o => o != s).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
                var restored = model;
                for (int i = 2; taken.Contains(restored); i++) restored = $"{model} {i}";
                s.Name = restored;
                Store.Save();
            }

            // A device nobody knows is NOT registered here: it waits in Match.Unmatched until the user gives it a
            // profile (see UnassignedDevices, CreateProfile, ApplyStickAssignments, RegisterNewDevices).
            foreach (var (stickId, dev) in m.Matched)
            {
                var s = data.Sticks.First(x => x.Id == stickId);
                DeviceMatcher.Refresh(s, dev);
                if (!s.ScInstanceManual) s.ScInstance = dev.HasJsNumber ? dev.DefaultScInstance : null;
                if (s.ProfileId is { } pid && Store.Profile(pid) is { IsSystem: false })
                    data.ProfileDevices[pid] = ProfileDevice.Of(s);
            }
            Match = m;
            _byDevice = m.Matched.ToDictionary(kv => kv.Value.InstanceGuid, kv => data.Sticks.First(s => s.Id == kv.Key));
            _ = ActiveSetup;
            SyncActiveFromSticks();
            Store.Save();
            if (_announceNew)
                foreach (var d in m.Unmatched.Where(IsRegistrable))
                    if (_announced.Add(d.InstanceGuid)) (announce ??= new()).Add(d);
        }
        // A device that is gone cannot report that its buttons were let go: whatever it held goes up now.
        foreach (var g in vanished) Engine.ReleaseDevice(g);
        Changed?.Invoke();
        if (announce != null)
            foreach (var d in announce) NewDeviceSeen?.Invoke(d);
    }

    /// <summary>A real joystick (js number, not an XInput pad).</summary>
    private static bool IsRegistrable(DeviceInfo d) => d.HasJsNumber;

    /// <summary>Devices that need a profile in the active setup: unknown ones and known ones without a profile here.</summary>
    public IReadOnlyList<DeviceInfo> UnassignedDevices
    {
        get
        {
            lock (_gate)
            {
                var list = Match.Unmatched.Where(IsRegistrable).ToList();
                foreach (var (stickId, dev) in Match.Matched)
                    if (IsRegistrable(dev) && Store.Data.Sticks.FirstOrDefault(s => s.Id == stickId)?.ProfileId == null)
                        list.Add(dev);
                return list.OrderBy(d => d.EnumIndex).ToList();
            }
        }
    }

    /// <summary>
    /// A profile of the active setup with the stick it is bound to. <see cref="ProfileSlot.Device"/> is null when
    /// the stick is not plugged in (the profile is inactive) or never had a device.
    /// </summary>
    public sealed record ProfileSlot(ControllerProfile Profile, StickRecord Stick, DeviceInfo? Device)
    {
        public bool IsConnected => Device != null;
        public bool HasNoDevice => Device == null && IsSlot(Stick);
    }

    /// <summary>The stick profiles of the active setup, in the order they were made.</summary>
    public IReadOnlyList<ProfileSlot> ProfileSlots()
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            // In the order the profiles were made: renaming a profile or moving it to another stick never
            // moves its row. A profile's layouts in other setups count as the same profile.
            var order = Store.Data.Profiles.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
            int Rank(ControllerProfile p) => SiblingLayouts(p.Id).Where(order.ContainsKey).Select(id => order[id]).DefaultIfEmpty(int.MaxValue).Min();
            return Store.Data.Sticks
                .Select(s => (Stick: s, Profile: Store.Profile(s.ProfileId)))
                .Where(x => x.Profile is { IsSystem: false })
                .Select(x => new ProfileSlot(x.Profile!, x.Stick, DeviceFor(x.Stick)))
                .OrderBy(x => Rank(x.Profile))
                .ToList();
        }
    }

    /// <summary>
    /// A new, empty stick profile in the active setup, named <paramref name="name"/> (or after the device).
    /// With a <paramref name="device"/> the profile is given to it right away. Only ever called by the user.
    /// </summary>
    public ControllerProfile CreateProfile(string? name = null, DeviceInfo? device = null)
    {
        ControllerProfile p;
        lock (_gate)
        {
            _ = ActiveSetup;
            var wanted = (name ?? device?.ProductName)?.Trim();
            p = new ControllerProfile { Name = UniqueProfileName(string.IsNullOrEmpty(wanted) ? "New profile" : wanted) };
            Store.Data.Profiles.Add(p);
            Store.Data.Sticks.Add(new StickRecord { Name = p.Name, ProfileId = p.Id });
        }
        if (device != null) ApplyStickAssignments(new[] { (device, p.Id) });
        else Touch();
        return p;
    }

    /// <summary>
    /// "A profile for every new stick": an explicit user action. A profile nobody uses and that was made for this
    /// model is reused, else a new one named after the model is made. A device that could be one of the sticks
    /// still waiting for its device is left alone: identical sticks cannot be told apart, the user identifies them.
    /// </summary>
    public int RegisterNewDevices()
    {
        int made = 0;
        foreach (var d in UnassignedDevices)
        {
            bool someoneWaiting;
            lock (_gate)
                someoneWaiting = Match.Missing.Any(s => s.Vid == d.Vid && s.Pid == d.Pid && Store.Profile(s.ProfileId) != null);
            if (someoneWaiting) continue;
            var profile = FreeProfileFor(d.ProductName, null, d.Vid, d.Pid);
            if (profile != null) ApplyStickAssignments(new[] { (d, profile.Id) });
            else CreateProfile(device: d);
            made++;
        }
        return made;
    }

    /// <summary>
    /// The connected stick that has this js number. An unplugged stick keeps its last number, which another
    /// device may hold now, so it never answers.
    /// </summary>
    public StickRecord? StickByInstance(int n)
    {
        lock (_gate) return DeploySticks().FirstOrDefault(s => s.ScInstance == n);
    }

    /// <summary>
    /// A stick profile of this stick model that nobody uses: not on any stick and not in any setup.
    /// Profiles are named after the model ("Generic USB Joystick", "Generic USB Joystick 2", ...), so four
    /// identical sticks reuse four existing profiles and only a fifth stick gets a new one.
    /// </summary>
    public ControllerProfile? FreeProfileFor(string productName, IEnumerable<Guid>? alsoTaken = null, int vid = 0, int pid = 0)
    {
        lock (_gate)
        {
            var data = Store.Data;
            var taken = data.Sticks.Where(s => s.ProfileId != null).Select(s => s.ProfileId!.Value)
                .Concat(data.Setups.SelectMany(s => s.StickLayouts.Values))
                .Concat(alsoTaken ?? Enumerable.Empty<Guid>())
                .ToHashSet();
            var rx = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(productName.Trim()) + @"( \d+)*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Made for this model: by the device it was last used on, so a renamed profile still counts.
            bool MadeFor(ControllerProfile p) => vid != 0 &&
                data.ProfileDevices.TryGetValue(p.Id, out var o) && o.SameModel(vid, pid);
            return data.Profiles.FirstOrDefault(p => !p.IsSystem && !taken.Contains(p.Id) && (MadeFor(p) || rx.IsMatch(p.Name)));
        }
    }

    private string UniqueProfileName(string name)
    {
        var names = Store.Data.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!names.Contains($"{name} {i}")) return $"{name} {i}";
    }

    /// <summary>Manual remap: this saved stick is now that device.</summary>
    public void AssignDevice(StickRecord stick, DeviceInfo device)
    {
        lock (_gate)
        {
            // Free the device from whoever claimed it by hint.
            foreach (var other in Store.Data.Sticks.Where(s => s.Id != stick.Id && s.InstanceGuid == device.InstanceGuid))
            {
                other.InstanceGuid = null;
                other.InterfacePath = null;
            }
            DeviceMatcher.Refresh(stick, device);
        }
        Reconcile();
    }

    /// <summary>
    /// Every listed device becomes the stick that owns the chosen profile. All pairs are applied before matching
    /// runs again, so a half-finished swap is never matched on its own. A profile of another setup is copied
    /// first: editing it here must not change the other setup.
    /// </summary>
    public void ApplyStickAssignments(IEnumerable<(DeviceInfo Device, Guid ProfileId)> pairs)
    {
        lock (_gate)
        {
            var active = ActiveSetup;
            SyncActiveFromSticks();
            var data = Store.Data;
            var sticks = data.Sticks;
            bool Hints(StickRecord s, DeviceInfo d) => s.InstanceGuid == d.InstanceGuid ||
                (!string.IsNullOrEmpty(d.InterfacePath) &&
                 string.Equals(s.InterfacePath, d.InterfacePath, StringComparison.OrdinalIgnoreCase));
            bool UsedByOtherSetup(Guid id, Guid? exceptStick = null) => data.Setups.Any(x =>
                !ReferenceEquals(x, active) && x.StickLayouts.Any(kv => kv.Value == id && kv.Key != exceptStick));

            foreach (var (dev, requested) in pairs)
            {
                var profile = Store.Profile(requested);
                if (profile == null || profile.IsSystem) continue;
                var profileId = profile.Id;
                if (!active.StickLayouts.ContainsValue(profileId) && UsedByOtherSetup(profileId))
                {
                    var copy = CloneLayout(profile);
                    data.Profiles.Add(copy);
                    if (data.ProfileDevices.TryGetValue(profile.Id, out var origin)) data.ProfileDevices[copy.Id] = origin;
                    profileId = copy.Id;
                }

                var devRec = _byDevice.GetValueOrDefault(dev.InstanceGuid) ?? sticks.FirstOrDefault(s => Hints(s, dev));
                var profRec = sticks.FirstOrDefault(s => s.ProfileId == profileId);
                StickRecord rec;
                if (profRec != null && IsSlot(profRec) && devRec != null && devRec != profRec)
                {
                    // A profile waiting for a device meets a device we already know: the device's own record
                    // takes the profile and what it had waits in the slot instead. No second record appears.
                    var had = devRec.ProfileId;
                    devRec.ProfileId = profileId;
                    profRec.ProfileId = had is { } h && h != profileId && Store.Profile(h) != null ? h : null;
                    if (profRec.ProfileId == null && !data.Setups.Any(x => x.StickLayouts.ContainsKey(profRec.Id) && !ReferenceEquals(x, active)))
                        sticks.Remove(profRec);
                    rec = devRec;
                }
                else if (profRec != null) rec = profRec;   // the stick of this profile moves to the device
                else if (devRec != null)
                {
                    // The device's stick takes another profile; the one it had stays, waiting for a device.
                    rec = devRec;
                    var had = rec.ProfileId;
                    rec.ProfileId = profileId;
                    if (had is { } h && h != profileId && Store.Profile(h) is { IsSystem: false } kept)
                        sticks.Add(new StickRecord { Name = kept.Name, ProfileId = h });
                }
                else
                {
                    var baseName = dev.ProductName.Trim();
                    int n = sticks.Count(s => s.ProductName.Trim() == baseName) + 1;
                    rec = new StickRecord { Name = n == 1 ? baseName : $"{baseName} {n}", ProfileId = profileId };
                    sticks.Add(rec);
                }
                foreach (var other in sticks.Where(s => s.Id != rec.Id && Hints(s, dev)))
                {
                    other.InstanceGuid = null;
                    other.InterfacePath = null;
                }
                DeviceMatcher.Refresh(rec, dev);
            }
        }
        Reconcile();
    }

    /// <summary>
    /// Takes the profile away from its stick (the profile stays, without a device) or from a device: the stick
    /// keeps its identity and just has no profile in the active setup.
    /// </summary>
    public void DetachDevice(StickRecord stick)
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            var profileId = stick.ProfileId;
            if (profileId == null) return;
            // The profile waits in a slot of its own; the physical stick is free for another profile.
            Store.Data.Sticks.Add(new StickRecord { Name = Store.Profile(profileId)?.Name ?? stick.Name, ProfileId = profileId });
            stick.ProfileId = null;
        }
        Reconcile();
    }

    public void Touch()
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            SyncActiveFromSticks();
        }
        Store.Save();
        Changed?.Invoke();
    }

    // ---- capture / identify ------------------------------------------------------------------

    /// <summary>Axis input names by axis slot (see DeviceInfo.AxisSlots).</summary>
    public static readonly string[] AxisInputNames = { "x", "y", "z", "rotx", "roty", "rotz", "slider1", "slider2" };

    /// <summary>
    /// The next button press (any device) is delivered to the callback instead of the rule engine.
    /// With <paramref name="includeAxes"/> a clear axis movement is captured as well ("x", "rotz", ...).
    /// </summary>
    public void BeginCapture(Action<CaptureResult> onCaptured, bool includeAxes = false)
    {
        lock (_gate) _capture = onCaptured;
        if (includeAxes) _ = Task.Run(() => WatchAxes(onCaptured));
    }

    private void WatchAxes(Action<CaptureResult> token)
    {
        var baseline = new Dictionary<Guid, double[]>();
        foreach (var d in Hub.Devices)
            if (Hub.GetState(d.InstanceGuid) is { } s) baseline[d.InstanceGuid] = s.Axes;
        var until = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
            lock (_gate) { if (!ReferenceEquals(_capture, token)) return; }
            foreach (var d in Hub.Devices)
            {
                if (!baseline.TryGetValue(d.InstanceGuid, out var b) || Hub.GetState(d.InstanceGuid) is not { } s) continue;
                for (int i = 0; i < s.Axes.Length && i < b.Length && i < d.AxisSlots.Count; i++)
                {
                    if (Math.Abs(s.Axes[i] - b[i]) < 0.45) continue;
                    StickRecord? stick;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_capture, token)) return;
                        _capture = null;
                        stick = _byDevice.GetValueOrDefault(d.InstanceGuid);
                    }
                    token(new CaptureResult(d, stick, AxisInputNames[d.AxisSlots[i]]));
                    return;
                }
            }
        }
    }

    public void CancelCapture()
    {
        lock (_gate) _capture = null;
    }

    private object? _axisWatch;

    /// <summary>
    /// Reports the first clear axis movement of any stick without taking over button input, so button
    /// presses keep arriving through <see cref="InputSeen"/>. Ends after one hit or <see cref="EndAxisWatch"/>.
    /// </summary>
    public void BeginAxisWatch(Action<CaptureResult> onAxis)
    {
        var token = new object();
        lock (_gate) _axisWatch = token;
        _ = Task.Run(() =>
        {
            var baseline = new Dictionary<Guid, double[]>();
            foreach (var d in Hub.Devices)
                if (Hub.GetState(d.InstanceGuid) is { } s) baseline[d.InstanceGuid] = s.Axes;
            var until = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
                lock (_gate) { if (!ReferenceEquals(_axisWatch, token)) return; }
                foreach (var d in Hub.Devices)
                {
                    if (!baseline.TryGetValue(d.InstanceGuid, out var b) || Hub.GetState(d.InstanceGuid) is not { } s) continue;
                    for (int i = 0; i < s.Axes.Length && i < b.Length && i < d.AxisSlots.Count; i++)
                    {
                        if (Math.Abs(s.Axes[i] - b[i]) < 0.45) continue;
                        StickRecord? stick;
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_axisWatch, token)) return;
                            _axisWatch = null;
                            stick = _byDevice.GetValueOrDefault(d.InstanceGuid);
                        }
                        onAxis(new CaptureResult(d, stick, AxisInputNames[d.AxisSlots[i]]));
                        return;
                    }
                }
            }
        });
    }

    public void EndAxisWatch()
    {
        lock (_gate) _axisWatch = null;
    }

    public bool IsCapturing { get { lock (_gate) return _capture != null; } }

    private void OnInput(InputEvent e)
    {
        Action<CaptureResult>? cap = null;
        StickRecord? stick;
        DeviceInfo? dev;
        lock (_gate)
        {
            stick = _byDevice.GetValueOrDefault(e.InstanceGuid);
            dev = Hub.Devices.FirstOrDefault(d => d.InstanceGuid == e.InstanceGuid);
            if (e.Pressed && _capture != null && dev != null) { cap = _capture; _capture = null; }
        }
        if (cap != null && dev != null)
        {
            cap(new CaptureResult(dev, stick, e.Input));
            return;
        }
        InputSeen?.Invoke(e, stick);
        Engine.Handle(e);
    }

    // ---- profiles -----------------------------------------------------------------------------

    public void RenameProfile(ControllerProfile p, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || p.IsSystem || name == p.Name) return;
        lock (_gate)
        {
            SyncActiveFromSticks();
            var siblings = SiblingLayouts(p.Id);
            var taken = Store.Data.Profiles.Where(o => !siblings.Contains(o.Id)).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var n = name;
            for (int i = 2; taken.Contains(n); i++) n = $"{name} {i}";
            foreach (var o in Store.Data.Profiles.Where(o => siblings.Contains(o.Id))) o.Name = n;
            p.Name = n;
        }
        Touch();
    }

    public void SetBind(ControllerProfile p, string map, string action, string input, string? mode = null)
    {
        // One physical input can carry several actions (SC allows it across action maps), but an action
        // is bound to a single input per profile.
        p.Binds.RemoveAll(b => b.Map == map && b.Action == action);
        p.Binds.Add(new ScBind { Map = map, Action = action, Input = input, Mode = mode });
        Touch();
    }

    /// <summary>
    /// Keyboard/mouse and gamepad layers: an action holds one raw input per device family
    /// (keyboard, mouse, gamepad), so a new bind replaces the old one of the same family.
    /// </summary>
    public void SetRawBind(ControllerProfile p, string map, string action, string rawInput, string? mode = null)
    {
        static string Family(string input) => input.Length >= 2 ? input[..2] : input;
        p.Binds.RemoveAll(b => b.Map == map && b.Action == action && Family(b.Input) == Family(rawInput));
        p.Binds.Add(new ScBind { Map = map, Action = action, Input = rawInput, Mode = mode });
        Touch();
    }

    public void RemoveBind(ControllerProfile p, ScBind b)
    {
        p.Binds.Remove(b);
        Touch();
    }

    public void SetRule(ControllerProfile p, string input, ButtonRule? rule)
    {
        if (rule == null || rule.IsEmpty) p.Rules.Remove(input);
        else p.Rules[input] = rule;
        Touch();
    }

    /// <summary>The user's own name for a physical input ("Landing gear"); null/empty removes it.</summary>
    public string? ButtonLabel(ControllerProfile p, string input) =>
        p.Rules.TryGetValue(input, out var r) && !string.IsNullOrWhiteSpace(r.Label) ? r.Label : null;

    public void SetButtonLabel(ControllerProfile p, string input, string? label)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        var rule = p.Rules.TryGetValue(input, out var r) ? r : new ButtonRule();
        rule.Label = label;
        SetRule(p, input, rule);
    }

    // ---- SC file integration ----------------------------------------------------------------

    public ActionMapsFile? LoadActionMaps()
    {
        var path = Install?.ActionMapsPath;
        return path != null && File.Exists(path) ? ActionMapsFile.Load(path) : null;
    }

    /// <summary>
    /// Imports jsN_button/hat rebinds from actionmaps.xml into the profiles of the sticks with that instance.
    /// "Unbound" markers are imported too, so a default removed in the game stays removed. With
    /// <paramref name="onlyKey"/> ("map/action") only that action is read.
    /// </summary>
    public (int Imported, int Skipped) ImportFromActionMaps(ActionMapsFile? file = null, string? onlyKey = null)
    {
        file ??= LoadActionMaps();
        if (file == null) return (0, 0);
        int imported = 0, skipped = 0;
        foreach (var r in file.Rebinds())
        {
            if (onlyKey != null && $"{r.Map}/{r.Action}" != onlyKey) continue;
            // Keyboard/mouse and gamepad rebinds go to their layer profiles, kept as raw SC codes.
            ControllerProfile? layer = ScInputCodes.IsKbm(r.Input) ? KeyboardMouseProfile
                : ScInputCodes.IsGamepad(r.Input) ? GamepadProfile : null;
            if (layer != null)
            {
                var m = r.MultiTap == "2" ? "double_tap" : r.ActivationMode;
                if (layer.Binds.Any(b => b.Map == r.Map && b.Action == r.Action && b.Input == r.Input)) continue;
                layer.Binds.Add(new ScBind { Map = r.Map, Action = r.Action, Input = r.Input, Mode = m });
                imported++;
                continue;
            }
            if (r.JsInstance is not { } inst || r.JsInputName is not { } input) continue;
            if (input != " " && !InputName().IsMatch(ScInputCodes.SplitJsInput(input).Input)) { skipped++; continue; }
            // Only a connected stick with a profile can take the binding; nothing is created for the rest.
            var stick = StickByInstance(inst);
            var prof = stick == null ? null : Store.Profile(stick.ProfileId);
            if (prof == null || prof.IsSystem) { skipped++; continue; }
            string? mode = r.MultiTap == "2" ? "double_tap" : r.ActivationMode;
            if (prof.Binds.Any(b => b.Map == r.Map && b.Action == r.Action && b.Input == input)) continue;
            prof.Binds.RemoveAll(b => b.Map == r.Map && b.Action == r.Action);
            prof.Binds.Add(new ScBind { Map = r.Map, Action = r.Action, Input = input, Mode = mode });
            imported++;
        }
        Touch();
        return (imported, skipped);
    }

    [GeneratedRegex(@"^(button\d+|hat\d+_(up|down|left|right|center)|x|y|z|rotx|roty|rotz|slider[12])$")]
    private static partial Regex InputName();

    [GeneratedRegex(@"^(kb|mo|gp|js)(\d{1,4})_")]
    private static partial Regex DeviceCode();

    /// <summary>
    /// Removes one binding of an action. <paramref name="raw"/> is its Star Citizen code ("kb1_lctrl+r",
    /// "js2_button3", "gp1_a", "mo1_mouse1"); it may be one of the game's defaults. When the device would
    /// fall back to a default or to a rebind in the game's file, an "unbound" marker is stored so the
    /// binding stays gone after Deploy. Returns false when no profile can hold the change (unknown js number).
    /// </summary>
    public bool RemoveBinding(string map, string action, string raw)
    {
        var m = DeviceCode().Match(raw);
        if (!m.Success) return false;
        string dev = m.Groups[1].Value;
        int n = int.Parse(m.Groups[2].Value);
        var key = $"{map}/{action}";

        ControllerProfile prof;
        string input, marker;
        if (dev == "js")
        {
            var stick = StickByInstance(n);
            if (stick == null || Store.Profile(stick.ProfileId) is not { IsSystem: false } held) return false;
            prof = held;
            input = raw[m.Length..];
            marker = " ";
        }
        else
        {
            prof = dev == "gp" ? GamepadProfile : KeyboardMouseProfile;
            input = raw;
            marker = $"{dev}{n}_ ";
        }

        // Deploy replaces everything of this action in the file for the device class, so keep what the
        // game's file holds for it before touching anything.
        var file = LoadActionMaps();
        if (file != null) ImportFromActionMaps(file, key);

        prof.Binds.RemoveAll(b => b.Map == map && b.Action == action && b.Input == input);
        bool SameDevice(ScBind b) => b.Map == map && b.Action == action
            && (dev == "js" || (b.Input.Length >= 2 && b.Input[..2] == dev));
        if (!prof.Binds.Any(SameDevice))
        {
            var def = ActiveSetup.UsesDefaults(dev) ? Catalog?.Find(map, action) : null;
            bool hasDefault = def != null && dev switch
            {
                "kb" => !string.IsNullOrWhiteSpace(def.DefaultKeyboard),
                "mo" => !string.IsNullOrWhiteSpace(def.DefaultMouse),
                "gp" => !string.IsNullOrWhiteSpace(def.DefaultGamepad),
                _ => n == 1 && !string.IsNullOrWhiteSpace(def.DefaultJoystick),
            };
            bool inFile = file != null && file.Rebinds().Any(r =>
                $"{r.Map}/{r.Action}" == key && !r.IsUnbound && DeviceCode().Match(r.Input) is { Success: true } rm
                && rm.Groups[1].Value == dev && (dev != "js" || rm.Groups[2].Value == n.ToString()));
            if (hasDefault || inFile) prof.Binds.Add(new ScBind { Map = map, Action = action, Input = marker });
        }
        Touch();
        return true;
    }

    /// <summary>
    /// Sets how a binding triggers (null = the game's default). <paramref name="raw"/> is its Star Citizen code;
    /// a binding that only exists as a game default or in the game's file becomes a rebind of this setup.
    /// </summary>
    public bool SetBindingMode(string map, string action, string raw, string? mode)
    {
        var m = DeviceCode().Match(raw);
        if (!m.Success) return false;
        string dev = m.Groups[1].Value;
        int n = int.Parse(m.Groups[2].Value);
        var key = $"{map}/{action}";
        if (mode is "default" or "") mode = null;

        ControllerProfile prof;
        string input;
        if (dev == "js")
        {
            var stick = StickByInstance(n);
            if (stick == null || Store.Profile(stick.ProfileId) is not { IsSystem: false } held) return false;
            prof = held;
            input = raw[m.Length..];
        }
        else
        {
            prof = dev == "gp" ? GamepadProfile : KeyboardMouseProfile;
            input = raw;
        }

        var file = LoadActionMaps();
        if (file != null) ImportFromActionMaps(file, key);

        var bind = prof.Binds.FirstOrDefault(b => b.Map == map && b.Action == action && b.Input == input);
        if (bind != null) bind.Mode = mode;
        else if (dev == "js") SetBind(prof, map, action, input, mode);
        else SetRawBind(prof, map, action, input, mode);
        Touch();
        return true;
    }

    public const string GameRunningMessage =
        "Star Citizen is running. Close the game first: it keeps its controls in memory and writes them over " +
        "actionmaps.xml when it exits, which would undo a deploy.";

    /// <summary>Previews always work; writing is refused while Star Citizen runs.</summary>
    public DeployResult Deploy(bool dryRun)
    {
        if (Install == null) throw new InvalidOperationException("No Star Citizen installation selected");
        if (!dryRun && ScLocator.IsGameRunning(fresh: true)) throw new InvalidOperationException(GameRunningMessage);
        var d = Store.Data;
        var res = Deployer.Apply(Install.ActionMapsPath, Store.BackupDir, DeploySticks(), ProfilesForDeploy(),
            d.ManagedKeys, dryRun, new ManagedKeys(d.ManagedKeys, d.ManagedKbmKeys, d.ManagedGamepadKeys),
            UnboundDefaults());
        if (!dryRun)
        {
            Store.Data.ManagedKeys = res.ManagedKeys.ToList();
            Store.Data.ManagedKbmKeys = res.KbmKeys.ToList();
            Store.Data.ManagedGamepadKeys = res.GamepadKeys.ToList();
            Store.Save();
        }
        return res;
    }

    public void Dispose()
    {
        Engine.Dispose();
        Hub.Dispose();
    }
}
