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
        // "Empty" ships with the app: a profile where nothing is bound, not even the game's defaults.
        if (!d.Setups.Any(x => x.NoDefaults && x.IsBuiltIn))
        {
            var names = d.Setups.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            d.Setups.Add(new Setup { Name = names.Contains("Empty") ? "Empty (built-in)" : "Empty", IsBuiltIn = true, NoDefaults = true });
            Store.Save();
        }
        var a = d.Setups.FirstOrDefault(x => x.Id == d.ActiveSetupId) ?? d.Setups[0];
        d.ActiveSetupId = a.Id;
        return a;
    }

    /// <summary>StickRecord.ProfileId is the live layout of the active setup; mirror it into the setup.</summary>
    private void SyncActiveFromSticks()
    {
        var a = _active;
        if (a == null) return;
        foreach (var s in Store.Data.Sticks)
        {
            if (s.ProfileId is { } g) a.StickLayouts[s.Id] = g;
            else a.StickLayouts.Remove(s.Id);
        }
    }

    public void ActivateSetup(Setup setup)
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            SyncActiveFromSticks();
            _active = setup;
            Store.Data.ActiveSetupId = setup.Id;
            var pool = Store.Data.Profiles.Select(p => p.Id).ToHashSet();
            foreach (var s in Store.Data.Sticks)
                s.ProfileId = setup.StickLayouts.TryGetValue(s.Id, out var l) && pool.Contains(l) ? l : null;
            Store.Save();
        }
        Changed?.Invoke();
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
            foreach (var kv in s.StickLayouts.OrderBy(k => k.Key))
                snap.Layouts.Add(new SnapshotLayout { StickId = kv.Key, Layout = Store.Profile(kv.Value) });
            return System.Text.Json.JsonSerializer.Serialize(snap);
        }
    }

    /// <summary>True when the setup differs from what the user last saved.</summary>
    public bool IsSetupDirty(Setup s)
    {
        if (s.SavedState == null) { s.SavedState = TakeSnapshot(s); return false; }
        return TakeSnapshot(s) != s.SavedState;
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
        var snap = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(s.SavedState);
        if (snap == null) return;
        lock (_gate)
        {
            static void Copy(ControllerProfile to, ControllerProfile from)
            {
                to.Binds = from.Binds; to.Rules = from.Rules; to.Actions = from.Actions; to.Name = from.Name;
            }
            Copy(s.Keyboard, snap.Keyboard);
            Copy(s.Gamepad, snap.Gamepad);
            var keep = new Dictionary<Guid, Guid>();
            foreach (var l in snap.Layouts)
            {
                if (l.Layout == null) continue;
                var existing = Store.Data.Profiles.FirstOrDefault(p => p.Id == l.Layout.Id);
                if (existing != null) Copy(existing, l.Layout);
                else Store.Data.Profiles.Add(l.Layout);
                keep[l.StickId] = l.Layout.Id;
            }
            s.StickLayouts = keep;
            if (ReferenceEquals(s, _active))
            {
                var pool = Store.Data.Profiles.Select(p => p.Id).ToHashSet();
                foreach (var st in Store.Data.Sticks)
                    st.ProfileId = keep.TryGetValue(st.Id, out var id) && pool.Contains(id) ? id : null;
            }
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
                Keyboard = CloneLayout(src.Keyboard),
                Gamepad = CloneLayout(src.Gamepad),
            };
            foreach (var (stickId, layoutId) in src.StickLayouts)
            {
                var layout = Store.Profile(layoutId);
                if (layout == null) continue;
                var c = CloneLayout(layout);
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
        lock (_gate)
        {
            _ = ActiveSetup;
            if (s.IsProtected || Store.Data.Setups.Count <= 1) return false;
            bool wasActive = ReferenceEquals(_active, s);
            Store.Data.Setups.Remove(s);
            var used = Store.Data.Setups.SelectMany(x => x.StickLayouts.Values).ToHashSet();
            foreach (var l in s.StickLayouts.Values.Where(l => !used.Contains(l)).ToList())
                Store.Data.Profiles.RemoveAll(p => p.Id == l);
            if (wasActive) { _active = null; Store.Data.ActiveSetupId = Store.Data.Setups[0].Id; }
            Store.Save();
        }
        if (_active == null) ActivateSetup(Store.Data.Setups[0]);
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
    /// Reads a Star Citizen actionmaps.xml into a NEW setup and activates it. Keyboard-output rules are not
    /// part of Star Citizen, so they are carried over from the previously active setup.
    /// </summary>
    public (Setup Setup, int Imported, int Skipped) ReadFromStarCitizen(ActionMapsFile? file = null, string? name = null)
    {
        file ??= LoadActionMaps();
        var rulesByStick = new Dictionary<Guid, (string Name, Dictionary<string, ButtonRule> Rules, List<ButtonAction> Actions)>();
        lock (_gate)
        {
            _ = ActiveSetup;
            foreach (var st in Store.Data.Sticks)
            {
                var layout = Store.Profile(st.ProfileId);
                if (layout != null && (layout.Rules.Count > 0 || layout.Actions.Count > 0))
                    rulesByStick[st.Id] = (layout.Name, Clone(layout.Rules), Clone(layout.Actions));
            }
        }
        var setup = NewSetup(name ?? $"Star Citizen · {DateTime.Now:dd.MM. HH:mm}");
        foreach (var st in Store.Data.Sticks.ToList())
        {
            if (!rulesByStick.TryGetValue(st.Id, out var r)) continue;
            var layout = NewLayoutFor(st, r.Name);
            layout.Rules = r.Rules;
            layout.Actions = r.Actions;
        }
        var (imported, skipped) = file == null ? (0, 0) : ImportFromActionMaps(file);
        return (setup, imported, skipped);
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
            foreach (var st in Store.Data.Sticks)
                if (Store.Profile(st.ProfileId) is { } layout) layout.Binds.Clear();
        }
        var (imported, skipped) = file == null ? (0, 0) : ImportFromActionMaps(file);
        return (setup, imported, skipped);
    }

    /// <summary>True while the active setup holds no binding at all (a fresh install).</summary>
    public bool ActiveSetupIsEmpty()
    {
        lock (_gate)
        {
            var a = ActiveSetup;
            if (a.Keyboard.Binds.Count > 0 || a.Gamepad.Binds.Count > 0) return false;
            return !Store.Data.Sticks.Any(st => Store.Profile(st.ProfileId) is { } l && l.Binds.Count > 0);
        }
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
    /// (keyboard, mouse, gamepad, and the first joystick). Null for normal profiles.
    /// </summary>
    private IReadOnlyList<(string Map, string Action, string Input)>? UnboundDefaults()
    {
        if (!ActiveSetup.NoDefaults || Catalog == null) return null;
        var list = new List<(string, string, string)>();
        foreach (var a in Catalog.AllActions)
        {
            if (!string.IsNullOrWhiteSpace(a.DefaultKeyboard)) list.Add((a.Map, a.Name, "kb1_ "));
            if (!string.IsNullOrWhiteSpace(a.DefaultMouse)) list.Add((a.Map, a.Name, "mo1_ "));
            if (!string.IsNullOrWhiteSpace(a.DefaultGamepad)) list.Add((a.Map, a.Name, "gp1_ "));
            if (a.Joystick && !string.IsNullOrWhiteSpace(a.DefaultJoystick)) list.Add((a.Map, a.Name, "js1_ "));
        }
        return list;
    }

    /// <summary>
    /// Sticks that take part in a deploy: the connected ones, plus those with a hand-set js number. A stick that
    /// is not plugged in keeps its last js number, which another device may hold now - never write that.
    /// </summary>
    private List<StickRecord> DeploySticks() =>
        Store.Data.Sticks.Where(s => s.ScInstanceManual || IsConnected(s)).ToList();

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

    public void Start()
    {
        _ = ActiveSetup;
        MigrateRules();
        DiscoverInstall();
        Hub.Start();
        Reconcile();
        // First start: show what the game already has instead of an empty profile.
        try
        {
            if (Install != null && File.Exists(Install.ActionMapsPath) && ActiveSetupIsEmpty())
            {
                ReadIntoActiveSetup();
                SaveActiveSetup();
            }
        }
        catch { /* an unreadable file just leaves the profile empty */ }
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

    public async Task LoadCatalogAsync(bool force)
    {
        var inst = Install;
        if (inst == null) { CatalogStatus = "Star Citizen installation not found"; Changed?.Invoke(); return; }
        CatalogStatus = "Reading game data...";
        Changed?.Invoke();
        try
        {
            var cat = await Task.Run(() => ActionCatalog.LoadOrBuild(inst, Store.CacheDir, force,
                m => { CatalogStatus = m; Changed?.Invoke(); }));
            Catalog = cat;
            CatalogStatus = $"{cat.AllActions.Count()} actions in {cat.Groups.Count} groups (build {cat.BuildVersion})";
        }
        catch (Exception ex)
        {
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

    /// <summary>Re-run matching after devices or records changed; auto-register unambiguous new devices.</summary>
    public void Reconcile()
    {
        lock (_gate)
        {
            var devices = Hub.Devices;
            var data = Store.Data;
            var m = DeviceMatcher.Match(data.Sticks, devices);

            // New device of a model nobody is waiting for -> register it with sensible defaults.
            foreach (var d in m.Unmatched.ToList())
            {
                bool someoneWaiting = m.Missing.Any(s => s.Vid == d.Vid && s.Pid == d.Pid);
                if (someoneWaiting) continue;
                var baseName = d.ProductName.Trim();
                int n = data.Sticks.Count(s => s.ProductName.Trim() == baseName) + 1;
                var profile = new ControllerProfile { Name = UniqueProfileName(n == 1 ? baseName : $"{baseName} {n}") };
                data.Profiles.Add(profile);
                var rec = new StickRecord
                {
                    Name = n == 1 ? baseName : $"{baseName} {n}",
                    ProfileId = profile.Id,
                };
                DeviceMatcher.Refresh(rec, d);
                data.Sticks.Add(rec);
            }
            m = DeviceMatcher.Match(data.Sticks, devices);

            foreach (var (stickId, dev) in m.Matched)
            {
                var s = data.Sticks.First(x => x.Id == stickId);
                DeviceMatcher.Refresh(s, dev);
                if (!s.ScInstanceManual) s.ScInstance = dev.HasJsNumber ? dev.DefaultScInstance : null;
            }
            Match = m;
            _byDevice = m.Matched.ToDictionary(kv => kv.Value.InstanceGuid, kv => data.Sticks.First(s => s.Id == kv.Key));
            _ = ActiveSetup;
            SyncActiveFromSticks();
            Store.Save();
        }
        Changed?.Invoke();
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
    /// Profile manager: every listed device becomes the stick that owns the chosen profile. All pairs are
    /// applied before matching runs again, so a half-finished swap is never matched on its own.
    /// </summary>
    public void ApplyStickAssignments(IEnumerable<(DeviceInfo Device, Guid ProfileId)> pairs)
    {
        lock (_gate)
        {
            _ = ActiveSetup;
            var sticks = Store.Data.Sticks;
            foreach (var (dev, profileId) in pairs)
            {
                var profile = Store.Profile(profileId);
                if (profile == null) continue;
                var rec = sticks.FirstOrDefault(s => s.ProfileId == profileId);
                if (rec == null)
                {
                    rec = new StickRecord { Name = profile.Name, ProfileId = profileId };
                    sticks.Add(rec);
                }
                foreach (var other in sticks.Where(s => s.Id != rec.Id && (s.InstanceGuid == dev.InstanceGuid ||
                             (!string.IsNullOrEmpty(dev.InterfacePath) &&
                              string.Equals(s.InterfacePath, dev.InterfacePath, StringComparison.OrdinalIgnoreCase)))))
                {
                    other.InstanceGuid = null;
                    other.InterfacePath = null;
                }
                DeviceMatcher.Refresh(rec, dev);
            }
        }
        Reconcile();
    }

    public StickRecord AddStickFor(DeviceInfo device, string? name = null)
    {
        StickRecord rec;
        lock (_gate)
        {
            var nm = name ?? device.ProductName.Trim();
            var profile = new ControllerProfile { Name = UniqueProfileName(nm) };
            Store.Data.Profiles.Add(profile);
            rec = new StickRecord { Name = nm, ProfileId = profile.Id };
            DeviceMatcher.Refresh(rec, device);
            Store.Data.Sticks.Add(rec);
        }
        Reconcile();
        return rec;
    }

    public void ForgetStick(StickRecord s)
    {
        lock (_gate) Store.Data.Sticks.Remove(s);
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

    public ControllerProfile NewProfile(string? name = null)
    {
        var p = new ControllerProfile { Name = UniqueProfileName(name ?? "New profile") };
        Store.Data.Profiles.Add(p);
        Touch();
        return p;
    }

    public ControllerProfile DuplicateProfile(ControllerProfile src)
    {
        var json = AppStore.ExportProfile(src);
        var p = System.Text.Json.JsonSerializer.Deserialize<ControllerProfile>(json)!;
        p.Id = Guid.NewGuid();
        p.Name = UniqueProfileName(src.Name + " copy");
        Store.Data.Profiles.Add(p);
        Touch();
        return p;
    }

    public void DeleteProfile(ControllerProfile p)
    {
        if (p.IsSystem) return;
        Store.Data.Profiles.Remove(p);
        foreach (var s in Store.Data.Sticks.Where(s => s.ProfileId == p.Id)) s.ProfileId = null;
        Touch();
    }

    public ControllerProfile EnsureProfile(StickRecord s)
    {
        var p = Store.Profile(s.ProfileId);
        if (p != null) return p;
        p = NewProfile(s.Name);
        s.ProfileId = p.Id;
        Touch();
        return p;
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
            var stick = Store.Data.Sticks.FirstOrDefault(s => s.ScInstance == inst);
            if (stick == null) { skipped++; continue; }
            var prof = EnsureProfile(stick);
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

    [GeneratedRegex(@"^(kb|mo|gp|js)(\d+)_")]
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
            var stick = Store.Data.Sticks.FirstOrDefault(s => s.ScInstance == n);
            if (stick == null) return false;
            prof = EnsureProfile(stick);
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
            var def = ActiveSetup.NoDefaults ? null : Catalog?.Find(map, action);
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
            var stick = Store.Data.Sticks.FirstOrDefault(s => s.ScInstance == n);
            if (stick == null) return false;
            prof = EnsureProfile(stick);
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
        if (!dryRun && ScLocator.IsGameRunning()) throw new InvalidOperationException(GameRunningMessage);
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
