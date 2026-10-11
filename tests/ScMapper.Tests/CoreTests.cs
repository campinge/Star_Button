using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

public class CoreTests
{
    private const string Sample = """
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="joystick" instance="1" Product="Generic USB Joystick  {E0010810-0000-0000-0000-504944564944}"/>
  <options type="joystick" instance="2" Product=" VKB-Sim Gladiator NXT L    {0201231D-0000-0000-0000-504944564944}"/>
  <modifiers />
  <actionmap name="spaceship_movement">
   <action name="v_pitch"><rebind input="js2_ "/></action>
   <action name="v_roll"><rebind input="js2_x"/><rebind input="kb1_a"/></action>
  </actionmap>
  <actionmap name="custom_unknown"><action name="foo"><rebind input="kb1_b"/></action></actionmap>
 </ActionProfiles>
</ActionMaps>
""";

    private static string Tmp(string name) =>
        Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void Reads_rebinds_and_unbound_marker()
    {
        var f = ActionMapsFile.Parse(Sample);
        var r = f.Rebinds().ToList();
        Assert.Equal(4, r.Count);
        Assert.True(r[0].IsUnbound);
        Assert.Equal(2, r[1].JsInstance);
        Assert.Equal("x", r[1].JsInputName);
        Assert.Null(r[2].JsInstance);
    }

    [Fact]
    public void Deploy_merges_and_keeps_unknown_nodes()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Sample);
        var profile = new ControllerProfile { Name = "Comms" };
        profile.Binds.Add(new ScBind { Map = "spaceship_movement", Action = "v_roll", Input = "button3" });
        profile.Binds.Add(new ScBind { Map = "spaceship_targeting", Action = "v_target_cycle", Input = "hat1_up", Mode = "double_tap" });
        var stick = new StickRecord { Name = "Left", Vid = 0x231D, Pid = 0x0201, ProductName = "VKB-Sim Gladiator NXT L", ScInstance = 2, ProfileId = profile.Id };

        var res = Deployer.Apply(path, Path.Combine(Path.GetDirectoryName(path)!, "bk"), new[] { stick }, new[] { profile }, Array.Empty<string>());
        Assert.Equal(2, res.Added);
        Assert.NotNull(res.BackupPath);
        Assert.True(File.Exists(res.BackupPath));

        var f = ActionMapsFile.Load(path);
        var r = f.Rebinds().ToList();
        // keyboard bind and unknown actionmap survive, old js bind of v_roll is replaced, v_pitch's unbound marker stays.
        Assert.Contains(r, x => x.Action == "v_roll" && x.Input == "kb1_a");
        Assert.Contains(r, x => x.Action == "v_roll" && x.Input == "js2_button3");
        Assert.DoesNotContain(r, x => x.Action == "v_roll" && x.Input == "js2_x");
        Assert.Contains(r, x => x.Map == "custom_unknown" && x.Action == "foo");
        Assert.Contains(r, x => x.Action == "v_pitch" && x.IsUnbound);
        var dt = r.Single(x => x.Action == "v_target_cycle");
        Assert.Equal("2", dt.MultiTap);
        Assert.Equal("js2_hat1_up", dt.Input);
    }

    [Fact]
    public void Deploy_removes_previously_managed_when_unbound()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Sample);
        var res = Deployer.Apply(path, Path.Combine(Path.GetDirectoryName(path)!, "bk"),
            Array.Empty<StickRecord>(), Array.Empty<ControllerProfile>(), new[] { "spaceship_movement/v_roll" });
        Assert.Equal(1, res.Removed);
        var r = ActionMapsFile.Load(path).Rebinds().ToList();
        Assert.DoesNotContain(r, x => x.Input == "js2_x");
        Assert.Contains(r, x => x.Input == "kb1_a");
    }

    [Fact]
    public void Deploy_warns_on_duplicate_input_and_missing_instance()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var p = new ControllerProfile();
        p.Binds.Add(new ScBind { Map = "m", Action = "a", Input = "button1" });
        p.Binds.Add(new ScBind { Map = "m", Action = "b", Input = "button1" });
        var s1 = new StickRecord { Name = "A", ProfileId = p.Id, ScInstance = 1, ProductName = "X" };
        var s2 = new StickRecord { Name = "B", ProfileId = p.Id, ScInstance = null, ProductName = "X" };
        var res = Deployer.Apply(path, Path.Combine(Path.GetDirectoryName(path)!, "bk"), new[] { s1, s2 }, new[] { p }, Array.Empty<string>(), dryRun: true);
        Assert.Contains(res.Warnings, w => w.Contains("bound to 2 actions"));
        Assert.Contains(res.Warnings, w => w.Contains("no SC instance"));
    }

    [Fact]
    public void Matcher_pairs_by_guid_then_unique_model_and_reports_leftovers()
    {
        Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid(), g3 = Guid.NewGuid();
        var saved = new[]
        {
            new StickRecord { Name = "Left", Vid = 1, Pid = 1, InstanceGuid = g1 },
            new StickRecord { Name = "Gen1", Vid = 2, Pid = 2, InstanceGuid = g2 },
            new StickRecord { Name = "Gen2", Vid = 2, Pid = 2, InstanceGuid = Guid.NewGuid() },
            new StickRecord { Name = "Gone", Vid = 3, Pid = 3 },
        };
        var devs = new[]
        {
            new DeviceInfo(g1, "VKB", "VKB", 1, 1, null, 0, 0, 0, 0),
            new DeviceInfo(g2, "Gen", "Gen", 2, 2, null, 0, 0, 0, 1),
            new DeviceInfo(g3, "Gen", "Gen", 2, 2, null, 0, 0, 0, 2),
        };
        var m = DeviceMatcher.Match(saved, devs);
        Assert.Equal(g1, m.Matched[saved[0].Id].InstanceGuid);
        Assert.Equal(g2, m.Matched[saved[1].Id].InstanceGuid);
        // Gen2 vs the new generic device: two same-model sticks are not guessed once one is already taken -> ok to pair uniquely
        Assert.Contains(m.Missing, s => s.Name == "Gone");
        Assert.True(m.Matched.ContainsKey(saved[2].Id) || m.Unmatched.Any(d => d.InstanceGuid == g3));
    }

    [Fact]
    public void Matcher_never_guesses_between_identical_models()
    {
        var saved = new[]
        {
            new StickRecord { Name = "A", Vid = 2, Pid = 2, InstanceGuid = Guid.NewGuid() },
            new StickRecord { Name = "B", Vid = 2, Pid = 2, InstanceGuid = Guid.NewGuid() },
        };
        var devs = new[]
        {
            new DeviceInfo(Guid.NewGuid(), "Gen", "Gen", 2, 2, null, 0, 0, 0, 0),
            new DeviceInfo(Guid.NewGuid(), "Gen", "Gen", 2, 2, null, 0, 0, 0, 1),
        };
        var m = DeviceMatcher.Match(saved, devs);
        Assert.Empty(m.Matched);
        Assert.Equal(2, m.Unmatched.Count);
        Assert.Equal(2, m.Missing.Count);
    }

    [Theory]
    [InlineData("lshift+d", 2)]
    [InlineData("D+LShift", 2)]
    [InlineData("u", 1)]
    [InlineData("ctrl+alt+f5", 3)]
    public void KeyCombo_parses(string combo, int count)
    {
        Assert.True(KeyCombo.TryParse(combo, out var keys, out _));
        Assert.Equal(count, keys.Count);
        Assert.True(keys[0].IsModifier || count == 1);
    }

    [Fact]
    public void KeyCombo_rejects_unknown()
    {
        Assert.False(KeyCombo.TryParse("lshift+banana", out _, out var err));
        Assert.Contains("banana", err);
    }

    [Fact]
    public void Store_roundtrip()
    {
        var dir = Tmp("store");
        var s = new AppStore(dir);
        s.Data.Profiles.Add(new ControllerProfile { Name = "Comms" });
        s.Data.Sticks.Add(new StickRecord { Name = "Left", ScInstance = 3 });
        s.Save();
        var s2 = new AppStore(dir);
        Assert.Equal("Comms", s2.Data.Profiles.Single().Name);
        Assert.Equal(3, s2.Data.Sticks.Single().ScInstance);
    }

    [Fact]
    public void Real_actionmaps_roundtrip_is_semantically_unchanged_and_deploy_is_safe()
    {
        var inst = ScLocator.FindAll().FirstOrDefault();
        if (inst == null || !File.Exists(inst.ActionMapsPath)) return;
        var copy = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(inst.ActionMapsPath, copy);

        var a = ActionMapsFile.Load(copy);
        var beforeRebinds = a.Rebinds().ToList();
        var beforeOptions = a.JoystickOptions().ToList();
        a.Save(copy);
        var b = ActionMapsFile.Load(copy);
        Assert.Equal(beforeRebinds, b.Rebinds().ToList());
        Assert.Equal(beforeOptions, b.JoystickOptions().ToList());

        // Deploying with nothing configured must not change any binding or option.
        var res = Deployer.Apply(copy, Path.Combine(Path.GetDirectoryName(copy)!, "bk"),
            Array.Empty<StickRecord>(), Array.Empty<ControllerProfile>(), Array.Empty<string>());
        Assert.Equal(0, res.Added);
        var c = ActionMapsFile.Load(copy);
        Assert.Equal(beforeRebinds, c.Rebinds().ToList());
        Assert.Equal(beforeOptions, c.JoystickOptions().ToList());
    }

    [Fact]
    public void Import_from_actionmaps_maps_js_numbers_to_sticks()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <actionmap name="spaceship_view"><action name="v_view_cycle_fwd"><rebind input="js2_button7"/></action>
   <action name="v_view_pitch"><rebind input="js2_y"/></action></actionmap>
 <actionmap name="spaceship_targeting"><action name="x"><rebind input="js9_button1"/></action>
   <action name="dbl"><rebind input="js2_hat1_up" multiTap="2"/></action></actionmap>
</ActionProfiles></ActionMaps>
""");
        var hub = new FakeHub();
        using var c = new AppController(new AppStore(Tmp("store")), hub, new FakeOutput());
        var s = new StickRecord { Name = "Left", ScInstance = 2 };
        var prof = new ControllerProfile { Name = "P" };
        s.ProfileId = prof.Id;
        c.Store.Data.Sticks.Add(s);
        c.Store.Data.Profiles.Add(prof);

        var (imported, skipped) = c.ImportFromActionMaps(ActionMapsFile.Load(path));
        Assert.Equal(3, imported);   // button7, axis y and the double-tap hat
        Assert.Equal(1, skipped);    // js9 has no matching stick
        Assert.Contains(prof.Binds, b => b.Action == "v_view_pitch" && b.Input == "y");
        Assert.Contains(prof.Binds, b => b.Action == "v_view_cycle_fwd" && b.Input == "button7");
        Assert.Contains(prof.Binds, b => b.Action == "dbl" && b.Input == "hat1_up" && b.Mode == "double_tap");
    }

    [Fact]
    public void Deploy_writes_keyboard_mouse_and_gamepad_layers_and_replaces_only_managed()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <modifiers />
 <actionmap name="spaceship_view">
  <action name="a"><rebind input="kb1_ "/><rebind input="js1_button1"/></action>
  <action name="b"><rebind input="kb1_f4"/><rebind input="gp1_a"/></action>
  <action name="old"><rebind input="kb1_x"/></action>
 </actionmap>
</ActionProfiles></ActionMaps>
""");
        var kbm = new ControllerProfile { Kind = ProfileKind.KeyboardMouse, Name = "KBM" };
        kbm.Binds.Add(new ScBind { Map = "spaceship_view", Action = "a", Input = ScInputCodes.BuildKbm("lshift+k", null)! });
        kbm.Binds.Add(new ScBind { Map = "spaceship_view", Action = "b", Input = ScInputCodes.BuildKbm(null, "mouse1")! });
        var pad = new ControllerProfile { Kind = ProfileKind.Gamepad, Name = "Pad" };
        pad.Binds.Add(new ScBind { Map = "spaceship_view", Action = "b", Input = ScInputCodes.BuildGamepad("shoulderl", "y")! });

        var res = Deployer.Apply(path, Path.Combine(Path.GetDirectoryName(path)!, "bk"), Array.Empty<StickRecord>(),
            new[] { kbm, pad }, Array.Empty<string>(), managed: new ManagedKeys(
                Array.Empty<string>(), new[] { "spaceship_view/old" }, Array.Empty<string>()));
        Assert.Equal(3, res.Added);

        var r = ActionMapsFile.Load(path).Rebinds().ToList();
        Assert.Contains(r, x => x.Action == "a" && x.Input == "kb1_lshift+k");
        Assert.DoesNotContain(r, x => x.Action == "a" && x.Input == "kb1_ ");        // marker replaced by real bind
        Assert.Contains(r, x => x.Action == "a" && x.Input == "js1_button1");        // other device class untouched
        Assert.Contains(r, x => x.Action == "b" && x.Input == "mo1_mouse1");
        Assert.DoesNotContain(r, x => x.Action == "b" && x.Input == "kb1_f4");
        Assert.Contains(r, x => x.Action == "b" && x.Input == "gp1_shoulderl+y");
        Assert.DoesNotContain(r, x => x.Action == "old");                            // previously managed, now gone
    }

    [Fact]
    public void Import_puts_kbm_and_gamepad_rebinds_into_layer_profiles()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <actionmap name="m"><action name="fire"><rebind input="mo1_mouse1"/><rebind input="gp1_triggerr_btn"/><rebind input="kb1_ "/></action></actionmap>
</ActionProfiles></ActionMaps>
""");
        using var c = new AppController(new AppStore(Tmp("store")), new FakeHub(), new FakeOutput());
        var (imported, _) = c.ImportFromActionMaps(ActionMapsFile.Load(path));
        Assert.Equal(3, imported);
        Assert.Contains(c.KeyboardMouseProfile.Binds, b => b.Input == "mo1_mouse1");
        Assert.Contains(c.GamepadProfile.Binds, b => b.Input == "gp1_triggerr_btn");
        // "kb1_ " is the game's "unbound" marker; it is imported so a removed default stays removed.
        Assert.Contains(c.KeyboardMouseProfile.Binds, b => b.IsUnbound);
    }

    [Fact]
    public void Setups_migrate_duplicate_switch_and_delete()
    {
        var hub = new FakeHub();
        var d = new DeviceInfo(Guid.NewGuid(), "Alpha", "Alpha", 1, 1, null, 12, 2, 0, 0);
        hub.List.Add(d);
        var store = new AppStore(Tmp("store"));
        // Data as written by the previous version: layers among the profiles, stick bound to a layout.
        var layout = new ControllerProfile { Name = "Comms" };
        layout.Binds.Add(new ScBind { Map = "m", Action = "a", Input = "button1" });
        var kbm = new ControllerProfile { Kind = ProfileKind.KeyboardMouse, Name = "Keyboard & Mouse" };
        kbm.Binds.Add(new ScBind { Map = "m", Action = "a", Input = "kb1_f4" });
        store.Data.Profiles.AddRange(new[] { layout, kbm });
        store.Data.Sticks.Add(new StickRecord { Name = "Alpha", InstanceGuid = d.InstanceGuid, ProfileId = layout.Id, Vid = 1, Pid = 1 });

        using var c = new AppController(store, hub, new FakeOutput());
        c.Reconcile();
        // The old data becomes "Default"; the built-in "Empty" profile ships next to it.
        Assert.Equal(new[] { "Default", "Empty" }, c.Setups.Select(s => s.Name).ToArray());
        Assert.Equal("Default", c.ActiveSetup.Name);
        Assert.Equal("kb1_f4", c.KeyboardMouseProfile.Binds.Single().Input);
        Assert.DoesNotContain(store.Data.Profiles, p => p.IsSystem);
        Assert.Equal(layout.Id, c.ActiveSetup.StickLayouts[store.Data.Sticks[0].Id]);

        // Duplicate must not share layouts: editing the copy leaves the original alone.
        var copy = c.DuplicateSetup(c.ActiveSetup);
        c.ActivateSetup(copy);
        var copyLayout = c.Store.Profile(store.Data.Sticks[0].ProfileId)!;
        Assert.NotEqual(layout.Id, copyLayout.Id);
        copyLayout.Binds.Clear();
        c.KeyboardMouseProfile.Binds.Clear();
        c.Touch();
        c.ActivateSetup(c.Setups.First(s => s.Name == "Default"));
        Assert.Single(c.Store.Profile(store.Data.Sticks[0].ProfileId)!.Binds);
        Assert.Single(c.KeyboardMouseProfile.Binds);

        // New setups start empty; the sticks have no layout until something is bound.
        var fresh = c.NewSetup("Mining");
        Assert.Null(store.Data.Sticks[0].ProfileId);
        Assert.Empty(c.KeyboardMouseProfile.Binds);

        Assert.True(c.DeleteSetup(fresh));
        Assert.Equal(3, c.Setups.Count);                                  // Default, its copy, Empty
        Assert.NotNull(c.ActiveSetup);
        Assert.False(c.DeleteSetup(c.Setups.First(s => s.IsGameProfile)));   // Default and Empty are protected
        Assert.False(c.DeleteSetup(c.Setups.First(s => s.IsBuiltIn)));
    }

    [Fact]
    public void Read_from_star_citizen_creates_new_setup_and_keeps_rules()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <actionmap name="m"><action name="fire"><rebind input="js1_button3"/><rebind input="mo1_mouse1"/></action>
  <action name="pitch"><rebind input="js1_y"/></action></actionmap>
</ActionProfiles></ActionMaps>
""");
        var hub = new FakeHub();
        var d = new DeviceInfo(Guid.NewGuid(), "Alpha", "Alpha", 1, 1, null, 12, 2, 0, 0);
        hub.List.Add(d);
        using var c = new AppController(new AppStore(Tmp("store")), hub, new FakeOutput());
        c.Reconcile();
        var stick = c.Store.Data.Sticks[0];
        c.SetRule(c.Store.Profile(stick.ProfileId)!, "button9", new ButtonRule { OnPress = new KeyAction { Keys = "u" } });
        var firstSetup = c.ActiveSetup;

        var (setup, imported, _) = c.ReadFromStarCitizen(ActionMapsFile.Load(path), "From SC");
        Assert.NotSame(firstSetup, setup);
        Assert.Same(setup, c.ActiveSetup);
        Assert.Equal(3, imported);
        var layout = c.Store.Profile(stick.ProfileId)!;
        Assert.Contains(layout.Binds, b => b.Action == "fire" && b.Input == "button3");
        Assert.Contains(layout.Binds, b => b.Action == "pitch" && b.Input == "y");
        Assert.True(layout.Rules.ContainsKey("button9"));              // keyboard output rules carried over
        Assert.Contains(c.KeyboardMouseProfile.Binds, b => b.Input == "mo1_mouse1");
        // The original setup is untouched.
        c.ActivateSetup(firstSetup);
        Assert.Empty(c.KeyboardMouseProfile.Binds);
    }

    [Fact]
    public void Setup_export_import_roundtrip()
    {
        var hub = new FakeHub();
        hub.List.Add(new DeviceInfo(Guid.NewGuid(), "Alpha", "Alpha", 1, 1, null, 12, 2, 0, 0));
        using var c = new AppController(new AppStore(Tmp("store")), hub, new FakeOutput());
        c.Reconcile();
        var layout = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;
        c.SetBind(layout, "m", "a", "button4");
        c.SetRawBind(c.KeyboardMouseProfile, "m", "a", "kb1_k");
        var json = c.ExportSetup(c.ActiveSetup);

        var imported = c.ImportSetup(json);
        Assert.NotEqual(c.Setups[0].Id, imported.Id);
        Assert.Equal("kb1_k", imported.Keyboard.Binds.Single().Input);
        var l2 = c.Store.Profile(imported.StickLayouts[c.Store.Data.Sticks[0].Id])!;
        Assert.NotEqual(layout.Id, l2.Id);
        Assert.Equal("button4", l2.Binds.Single().Input);
    }

    [Fact]
    public void Real_install_catalog_if_present()
    {
        var inst = ScLocator.FindAll().FirstOrDefault();
        if (inst == null) return; // not available on CI machines
        var cat = ActionCatalog.LoadOrBuild(inst, Tmp("cache"), forceRefresh: true);
        Assert.True(cat.Groups.Count > 10);
        Assert.NotNull(cat.Find("spaceship_movement", "v_pitch"));
        Assert.Contains("press", cat.ActivationModes);
    }
}
