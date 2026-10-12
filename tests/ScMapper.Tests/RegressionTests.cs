using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

/// <summary>Regressions found by the technical assessment (state transitions, held keys, hostile data).</summary>
public class RegressionTests
{
    private static string TmpDir() => Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N"));

    private static DeviceInfo Dev(int idx, string name = "Stick", int vid = 1, int pid = 1) =>
        new(Guid.NewGuid(), name, name, vid, pid, $"path{Guid.NewGuid():N}", 32, 6, 1, idx);

    private static (AppController C, FakeHub Hub, FakeOutput Out) Make(string? dir = null)
    {
        var hub = new FakeHub();
        var output = new FakeOutput();
        return (new AppController(new AppStore(dir ?? TmpDir()), hub, output), hub, output);
    }

    private static ControllerProfile ProfileOf(AppController c, DeviceInfo d) =>
        c.Store.Profile(c.StickForDevice(d.InstanceGuid)!.ProfileId)!;

    private static void Settle() => Thread.Sleep(150);

    // ---- 1. deleting the active profile ---------------------------------------------------------

    [Fact]
    public void Deleting_the_active_profile_keeps_the_stick_profile_of_the_profile_that_takes_over()
    {
        var (c, hub, _) = Make();
        using var _c = c;
        var d = Dev(0, "Alpha");
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        var original = ProfileOf(c, d);
        c.SetBind(original, "spaceship_weapons", "v_attack1_group1", "button1");
        c.SaveActiveSetup();

        var mining = c.NewSetup("Mining");
        c.SetBind(ProfileOf(c, d), "spaceship_weapons", "v_attack1_group1", "button2");
        c.SaveActiveSetup();

        Assert.True(c.DeleteSetup(mining));

        var stick = c.StickForDevice(d.InstanceGuid)!;
        Assert.NotNull(stick.ProfileId);
        Assert.Same(original, c.Store.Profile(stick.ProfileId));
        Assert.Empty(c.UnassignedDevices);
        Assert.Equal(stick.ProfileId, c.ActiveSetup.StickLayouts[stick.Id]);
    }

    // ---- 2. stuck keys -----------------------------------------------------------------------------

    private static (AppController C, FakeHub Hub, FakeOutput Out, DeviceInfo D, ControllerProfile P) HeldRig(string keys = "lshift")
    {
        var (c, hub, o) = Make();
        var d = Dev(0);
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        var p = ProfileOf(c, d);
        p.Actions.Add(new ButtonAction { Input = "button1", Trigger = ButtonTrigger.Held, Keys = keys });
        hub.Raise(d.InstanceGuid, "button1", true);
        Settle();
        Assert.NotEmpty(o.Held());
        return (c, hub, o, d, p);
    }

    [Fact]
    public void Held_key_is_released_when_the_stick_is_unplugged()
    {
        var (c, hub, o, d, _) = HeldRig();
        using var _c = c;
        hub.List.Clear();
        hub.Plug();
        Settle();
        Assert.Empty(o.Held());
    }

    [Fact]
    public void Held_key_is_released_when_its_action_is_removed_while_pressed()
    {
        var (c, hub, o, d, p) = HeldRig("lctrl");
        using var _c = c;
        c.RemoveAction(p, new ButtonActionRow { Action = p.Actions[0] });
        hub.Raise(d.InstanceGuid, "button1", false);
        Settle();
        Assert.Empty(o.Held());
    }

    [Fact]
    public void Held_key_is_released_when_the_profile_is_switched_while_pressed()
    {
        var (c, hub, o, d, _) = HeldRig();
        using var _c = c;
        c.NewSetup("Other");
        hub.Raise(d.InstanceGuid, "button1", false);
        Settle();
        Assert.Empty(o.Held());
    }

    [Fact]
    public void Legacy_hold_rule_removed_while_pressed_is_released()
    {
        var (c, hub, o) = Make();
        using var _c = c;
        var d = Dev(0);
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        var p = ProfileOf(c, d);
        p.Rules["button2"] = new ButtonRule { OnPress = new KeyAction { Keys = "lalt", Hold = true } };
        hub.Raise(d.InstanceGuid, "button2", true);
        Settle();
        c.SetRule(p, "button2", null);
        hub.Raise(d.InstanceGuid, "button2", false);
        Settle();
        Assert.Empty(o.Held());
    }

    // ---- 3./4. hostile files and the danger guard ------------------------------------------------

    private static (AppController C, FakeHub Hub, FakeOutput Out, string ActionMaps) MakeWithInstall()
    {
        var (c, hub, o) = Make();
        var root = Path.Combine(TmpDir(), "sc");
        var live = Path.Combine(root, "LIVE");
        var am = Path.Combine(live, "USER", "Client", "0", "Profiles", "default", "actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(am)!);
        File.WriteAllText(Path.Combine(live, "build_manifest.id"), "{\"Data\":{\"Version\":\"1.0\"}}");
        File.WriteAllBytes(Path.Combine(live, "Data.p4k"), Array.Empty<byte>());
        c.SelectInstall(new ScMapper.Core.Sc.ScInstall(root, "LIVE"));
        return (c, hub, o, am);
    }

    [Theory]
    [InlineData("<ActionMaps/>")]
    [InlineData("<ActionMaps><ActionProfiles><actionmap name=\"m\"><action name=\"a\"><rebind input=\"js99999999999_button1\"/></action></actionmap></ActionProfiles></ActionMaps>")]
    [InlineData("<ActionMaps><ActionProfiles><actionmap name=\"m\"><action name=\"a\"><rebind/></action></actionmap></ActionProfiles></ActionMaps>")]
    public void Unexpected_actionmaps_content_never_throws_on_the_read_paths(string xml)
    {
        var (c, hub, _, am) = MakeWithInstall();
        using var _c = c;
        var d = Dev(0);
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        c.SetBind(ProfileOf(c, d), "m", "a", "button1");
        File.WriteAllText(am, xml);

        _ = c.JsDrift();
        _ = c.JsSlots();
        _ = c.PendingChanges();
        c.ImportFromActionMaps();
        c.ReadIntoActiveSetup();
    }

    [Fact]
    public void Two_deploys_in_the_same_second_both_succeed_and_both_back_up()
    {
        var (c, hub, _, am) = MakeWithInstall();
        using var _c = c;
        var d = Dev(0);
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        c.SetBind(ProfileOf(c, d), "m", "a", "button1");
        File.WriteAllText(am, "<ActionMaps><ActionProfiles version=\"1\"><modifiers/></ActionProfiles></ActionMaps>");
        c.Deploy(false);
        c.Deploy(false);
        Assert.Equal(2, Directory.GetFiles(c.Store.BackupDir, "actionmaps_*.xml").Length);
    }

    [Fact]
    public void Backups_are_pruned_to_the_retention_limit()
    {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        for (int i = 0; i < ScMapper.Core.Sc.BackupFiles.Keep + 10; i++)
            File.WriteAllText(Path.Combine(dir, $"actionmaps_20260101_{i:000000}.xml"), "<a/>");
        ScMapper.Core.Sc.BackupFiles.Prune(dir);
        var left = Directory.GetFiles(dir);
        Assert.Equal(ScMapper.Core.Sc.BackupFiles.Keep, left.Length);
        Assert.Contains(left, f => f.EndsWith("000059.xml"));      // the newest survive
        Assert.DoesNotContain(left, f => f.EndsWith("000000.xml"));
    }

    [Theory]
    [InlineData("{\"Sticks\":null}")]
    [InlineData("{\"Profiles\":null}")]
    [InlineData("{\"Setups\":null}")]
    [InlineData("{\"Settings\":null}")]
    [InlineData("{\"Setups\":[null]}")]
    [InlineData("{\"Setups\":[{\"Name\":\"x\",\"Keyboard\":null,\"Gamepad\":null}]}")]
    [InlineData("{\"Setups\":[{\"Name\":\"x\",\"StickLayouts\":null}]}")]
    [InlineData("{\"Profiles\":[{\"Name\":\"p\",\"Binds\":null,\"Rules\":null,\"Actions\":null}]}")]
    [InlineData("{\"Profiles\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\"},{\"Id\":\"11111111-1111-1111-1111-111111111111\"}]," +
                "\"Sticks\":[{\"Name\":\"s\",\"ProfileId\":\"11111111-1111-1111-1111-111111111111\",\"ScInstance\":1}]}")]
    [InlineData("{\"Setups\":[{\"Name\":\"Default\",\"IsGameProfile\":true,\"SavedState\":\"not json\"}]}")]
    public void Wrong_shaped_data_file_still_starts_and_deploys(string json)
    {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "data.json"), json);
        var (c, hub, _) = Make(dir);
        using var _c = c;
        var d = Dev(0);
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        _ = c.ActiveSetup; _ = c.ProfileSlots(); _ = c.IsActiveDirty; _ = c.FindConflicts();
        _ = c.ExportSetup(c.ActiveSetup);
        c.SaveActiveSetup();
    }

    [Fact]
    public void Imported_dangerous_combos_are_never_sent_and_are_reported()
    {
        var (c, hub, o) = Make();
        using var _c = c;
        var d = Dev(0);
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        var stick = c.Store.Data.Sticks[0];
        var layout = Guid.NewGuid();
        var json = "{\"Setup\":{\"Name\":\"Shared\",\"StickLayouts\":{\"" + stick.Id + "\":\"" + layout + "\"}}," +
                   "\"Layouts\":[{\"Id\":\"" + layout + "\",\"Name\":\"Mining\",\"Actions\":[" +
                   "{\"Input\":\"button1\",\"Trigger\":0,\"Keys\":\"lalt+f4\"}," +
                   "{\"Input\":\"button2\",\"Trigger\":0,\"Keys\":\"f5\"}]}]}";
        c.ActivateSetup(c.ImportSetup(json));
        Assert.Equal(new[] { "lalt+f4" }, c.BlockedCombos());

        foreach (var b in new[] { "button1", "button2" })
        {
            hub.Raise(d.InstanceGuid, b, true);
            hub.Raise(d.InstanceGuid, b, false);
        }
        Thread.Sleep(400);
        var log = string.Join(" ", o.Snapshot());
        Assert.Contains("down:3F", log);        // F5 went out
        Assert.DoesNotContain("38+3E", log);    // Alt+F4 did not
    }

    // ---- profile bookkeeping --------------------------------------------------------------------

    [Fact]
    public void Save_as_new_profile_keeps_the_names_of_the_stick_profiles()
    {
        var (c, hub, _) = Make();
        using var _c = c;
        var d = Dev(0, "Alpha");
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        c.RenameProfile(ProfileOf(c, d), "Power");
        c.SetBind(ProfileOf(c, d), "m", "a", "button1");
        c.SaveActiveSetup();
        for (int i = 1; i <= 3; i++) c.SaveActiveSetupAs("Variant" + i);
        Assert.Equal("Power", ProfileOf(c, d).Name);
    }

    [Fact]
    public void Reading_the_game_file_leaves_an_unplugged_sticks_bindings_alone()
    {
        var (c, hub, _, am) = MakeWithInstall();
        using var _c = c;
        var a = Dev(0, "Alpha", 1, 1); var b = Dev(1, "Beta", 2, 2);
        hub.List.AddRange(new[] { a, b });
        c.Reconcile(); c.RegisterNewDevices();
        var pb = ProfileOf(c, b);
        c.SetBind(pb, "m", "b", "button2");
        c.SaveActiveSetup();
        hub.List.Remove(b);
        hub.List[0] = a with { EnumIndex = 0 };
        c.Reconcile();
        File.WriteAllText(am, "<ActionMaps><ActionProfiles version=\"1\"><actionmap name=\"m\"><action name=\"a\"><rebind input=\"js1_button1\"/></action></actionmap><modifiers/></ActionProfiles></ActionMaps>");
        c.ReadIntoActiveSetup();
        Assert.Single(pb.Binds);
    }

    [Fact]
    public void Deleting_profiles_repeatedly_leaves_no_stick_record_residue()
    {
        var (c, hub, _) = Make();
        using var _c = c;
        var d = Dev(0, "Alpha");
        hub.List.Add(d);
        c.Reconcile(); c.RegisterNewDevices();
        int before = c.Store.Data.Sticks.Count;
        for (int i = 0; i < 10; i++)
        {
            var s = c.NewSetup("T" + i);
            c.SetBind(ProfileOf(c, d), "m", "a", "button" + (i + 1));
            c.SaveActiveSetup();
            c.DeleteSetup(s);
        }
        Assert.Equal(before, c.Store.Data.Sticks.Count);
        Assert.NotNull(c.StickForDevice(d.InstanceGuid)!.ProfileId);
    }

    // ---- file format and keys -------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_actionmaps_keeps_the_byte_order_mark_and_adds_no_declaration(bool bom)
    {
        var path = Path.Combine(TmpDir(), "actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new System.Text.UTF8Encoding(bom);
        File.WriteAllText(path, "<ActionMaps><ActionProfiles version=\"1\"><modifiers/></ActionProfiles></ActionMaps>", enc);
        ScMapper.Core.Sc.ActionMapsFile.Load(path).Save(path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(bom, bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB);
        Assert.StartsWith("<ActionMaps>", System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
    }

    [Theory]
    [InlineData("+")]
    [InlineData(" + ")]
    [InlineData("")]
    public void A_combo_without_keys_is_not_a_combo(string combo) =>
        Assert.False(KeyCombo.TryParse(combo, out _, out _));

    [Theory]
    [InlineData(0x1E, false, "a")]
    [InlineData(0x15, false, "y")]       // the physical key labelled Z on a German keyboard sits where US has Y
    [InlineData(0x1A, false, "lbracket")]  // ... and the key labelled Ü sits where US has [
    [InlineData(0x01, false, "esc")]
    [InlineData(0x4B, true, "left")]
    [InlineData(0x4B, false, "np4")]
    public void Scan_codes_map_to_the_position_based_key_names(int scan, bool ext, string name) =>
        Assert.Equal(name, KeyCombo.NameOfScan((ushort)scan, ext));
}
