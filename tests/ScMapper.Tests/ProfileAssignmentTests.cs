using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

public class ProfileAssignmentTests
{
    private static AppStore TmpStore() =>
        new(Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N")));

    private static DeviceInfo Dev(string path, int idx) =>
        new(Guid.NewGuid(), "Generic", "Generic", 8, 8, path, 12, 2, 0, idx);

    [Fact]
    public void Profile_manager_pairs_identical_sticks_with_the_chosen_profiles()
    {
        var hub = new FakeHub();
        for (int i = 0; i < 3; i++) hub.List.Add(Dev($"old{i}", i));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var names = new[] { "Comms", "Weapons", "Flight" };
        for (int i = 0; i < 3; i++)
        {
            c.Store.Profile(c.Store.Data.Sticks[i].ProfileId)!.Name = names[i];
        }

        // All three are plugged in again: new GUIDs and paths, different order.
        hub.List.Clear();
        for (int i = 0; i < 3; i++) hub.List.Add(Dev($"new{i}", i));
        c.Reconcile(); c.RegisterNewDevices();
        Assert.Equal(3, c.Match.Unmatched.Count);
        Assert.Equal(3, c.Store.Data.Sticks.Count);

        var dev = c.Match.Unmatched.ToList();
        Guid ProfileId(string n) => c.Store.Data.Profiles.Single(p => p.Name == n).Id;
        c.ApplyStickAssignments(new[]
        {
            (dev[0], ProfileId("Flight")),
            (dev[1], ProfileId("Comms")),
            (dev[2], ProfileId("Weapons")),
        });

        Assert.Equal(3, c.Store.Data.Sticks.Count);          // nothing duplicated
        Assert.Empty(c.Match.Unmatched);
        Assert.Empty(c.Match.Missing);
        Assert.Equal(dev[0].InstanceGuid, c.DeviceFor(c.Store.Data.Sticks.Single(s => c.Store.Profile(s.ProfileId)!.Name == "Flight"))!.InstanceGuid);
        Assert.Equal(dev[1].InstanceGuid, c.DeviceFor(c.Store.Data.Sticks.Single(s => c.Store.Profile(s.ProfileId)!.Name == "Comms"))!.InstanceGuid);
        Assert.Equal(dev[2].InstanceGuid, c.DeviceFor(c.Store.Data.Sticks.Single(s => c.Store.Profile(s.ProfileId)!.Name == "Weapons"))!.InstanceGuid);
        Assert.Equal(2, c.Store.Data.Sticks.Single(s => c.Store.Profile(s.ProfileId)!.Name == "Comms").ScInstance);   // js follows the live order
    }

    [Fact]
    public void Stick_profiles_survive_switching_binding_profiles()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var kb = c.Setups.First(s => s.NoStickDefaults);
        var home = c.ActiveSetup;
        Assert.Single(c.ProfileSlots());

        c.ActivateSetup(kb);
        Assert.Single(c.ProfileSlots());
        c.ProfileSlots()[0].Profile.Binds.Clear();
        c.RenameProfile(c.ProfileSlots()[0].Profile, "Throttle");

        c.ActivateSetup(home);
        var slot = Assert.Single(c.ProfileSlots());
        Assert.Equal("Throttle", slot.Profile.Name);
        Assert.True(slot.IsConnected);
    }

    [Fact]
    public void A_profile_nobody_refers_to_comes_back_after_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N"));
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        string name;
        using (var c = new AppController(new AppStore(dir), hub, new FakeOutput()))
        {
            c.Reconcile(); c.RegisterNewDevices();
            var slot = Assert.Single(c.ProfileSlots());
            name = slot.Profile.Name;
            // The stick loses its profile everywhere; the profile itself stays in the store.
            slot.Stick.ProfileId = null;
            foreach (var s in c.Setups) s.StickLayouts.Clear();
            c.Store.Save();
        }
        using var again = new AppController(new AppStore(dir), hub, new FakeOutput());
        again.Reconcile();
        var back = Assert.Single(again.ProfileSlots());
        Assert.Equal(name, back.Profile.Name);
    }

    [Fact]
    public void Swapping_two_connected_sticks_moves_the_profiles()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        hub.List.Add(Dev("b", 1));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var s0 = c.Store.Data.Sticks[0];
        var s1 = c.Store.Data.Sticks[1];
        var d0 = c.DeviceFor(s0)!;
        var d1 = c.DeviceFor(s1)!;

        c.ApplyStickAssignments(new[] { (d0, s1.ProfileId!.Value), (d1, s0.ProfileId!.Value) });

        Assert.Equal(d1.InstanceGuid, c.DeviceFor(s0)!.InstanceGuid);
        Assert.Equal(d0.InstanceGuid, c.DeviceFor(s1)!.InstanceGuid);
    }

    [Fact]
    public void Assigning_a_profile_of_another_setup_copies_it_and_keeps_the_stick_record()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var stick = c.Store.Data.Sticks.Single();
        var d = c.DeviceFor(stick)!;
        var had = stick.ProfileId!.Value;

        // A layout that only another setup uses, held by a stick that is not connected.
        var other = new ControllerProfile { Name = "Quantum and Comms" };
        other.Binds.Add(new ScBind { Map = "m", Action = "a", Input = "button1" });
        c.Store.Data.Profiles.Add(other);
        var elsewhere = new StickRecord { Name = "VKB", ProfileId = null };
        c.Store.Data.Sticks.Add(elsewhere);
        var setup = new Setup { Name = "Stefan1", StickLayouts = { [elsewhere.Id] = other.Id } };
        c.Store.Data.Setups.Add(setup);

        c.ApplyStickAssignments(new[] { (d, other.Id) });

        var connected = c.Store.Data.Sticks.Where(c.IsConnected).ToList();
        Assert.Single(connected);
        Assert.Equal(stick.Id, connected[0].Id);                       // the same stick record, no duplicate
        Assert.NotEqual(other.Id, connected[0].ProfileId);             // a copy: editing it leaves "Stefan1" alone
        var copy = c.Store.Profile(connected[0].ProfileId)!;
        Assert.Equal(other.Binds.Count, copy.Binds.Count);
        Assert.Equal(other.Id, setup.StickLayouts[elsewhere.Id]);
        // What the stick had before is not lost: it waits for a device.
        Assert.Contains(c.ProfileSlots(), s => s.Profile.Id == had && s.HasNoDevice);
    }

    [Fact]
    public void A_new_device_gets_no_profile_until_the_user_decides()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());

        c.Reconcile();

        Assert.Empty(c.Store.Data.Sticks);
        Assert.Empty(c.StickProfiles);
        Assert.Single(c.UnassignedDevices);

        var p = c.CreateProfile(device: c.UnassignedDevices[0]);

        Assert.Empty(c.UnassignedDevices);
        Assert.Equal("Generic", p.Name);
        Assert.Single(c.ProfileSlots().Where(s => s.IsConnected));
    }

    [Fact]
    public void A_profile_can_be_made_before_the_stick_exists_and_takes_the_device_later()
    {
        var hub = new FakeHub();
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile();
        var p = c.CreateProfile("Power und Comms");
        var slot = Assert.Single(c.ProfileSlots());
        Assert.True(slot.HasNoDevice);

        hub.List.Add(Dev("a", 0));
        c.Reconcile();
        Assert.Single(c.UnassignedDevices);
        c.ApplyStickAssignments(new[] { (c.UnassignedDevices[0], p.Id) });

        slot = Assert.Single(c.ProfileSlots());
        Assert.True(slot.IsConnected);
        Assert.Equal("Power und Comms", c.StickLabel(slot.Stick));
        Assert.Single(c.Store.Data.Sticks);
    }

    [Fact]
    public void Unplugged_stick_keeps_its_profile_and_is_not_asked_about_again()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile();
        c.CreateProfile("Flight", c.UnassignedDevices[0]);

        hub.List.Clear();
        c.Reconcile();

        var slot = Assert.Single(c.ProfileSlots());
        Assert.False(slot.IsConnected);
        Assert.False(slot.HasNoDevice);              // it knows its stick, which is just not plugged in
        Assert.Empty(c.UnassignedDevices);
    }

    [Fact]
    public void A_device_that_appears_while_running_is_announced_once()
    {
        var hub = new FakeHub();
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Start();
        var seen = new List<DeviceInfo>();
        c.NewDeviceSeen += seen.Add;

        hub.List.Add(Dev("a", 0));
        c.Reconcile();
        c.Reconcile();

        Assert.Single(seen);
    }

    [Fact]
    public void An_empty_profile_does_not_make_the_setup_modified_and_survives_a_revert()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile();
        c.SaveActiveSetup();

        var p = c.CreateProfile(device: c.UnassignedDevices[0]);
        Assert.False(c.IsActiveDirty);

        c.RevertSetup(c.ActiveSetup);

        Assert.Contains(c.ProfileSlots(), s => s.Profile.Id == p.Id);
    }

    [Fact]
    public void Discarding_changes_only_takes_back_bindings_never_a_stick_profile()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        c.SaveActiveSetup();                         // the stick's profile is still empty here

        var stick = c.Store.Data.Sticks[0];
        var layout = c.Store.Profile(stick.ProfileId)!;
        c.SetKeyModifierButton(layout, "button1", "lshift");   // belongs to the stick profile
        c.SetBind(layout, "seat_general", "v_toggle_mining_mode", "button2");   // belongs to the binding profile
        Assert.True(c.IsActiveDirty);

        c.RevertSetup(c.ActiveSetup);

        Assert.Equal(layout.Id, stick.ProfileId);    // still assigned, not orphaned
        Assert.Contains(c.Store.Profile(layout.Id)!.Actions, a => a.Input == "button1");   // kept
        Assert.Empty(layout.Binds);                  // taken back
        Assert.False(c.IsActiveDirty);
    }

    [Fact]
    public void Two_sticks_never_hold_the_same_profile()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        hub.List.Add(Dev("b", 1));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile();
        c.RegisterNewDevices();
        var s0 = c.Store.Data.Sticks[0];
        var s1 = c.Store.Data.Sticks[1];

        s1.ProfileId = s0.ProfileId;
        c.Touch();

        Assert.Single(c.Store.Data.Sticks.Where(s => s.ProfileId == s0.ProfileId));
    }

    [Fact]
    public void Registering_all_new_devices_is_an_explicit_action_and_names_them_after_the_model()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        hub.List.Add(Dev("b", 1));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile();
        Assert.Empty(c.StickProfiles);

        Assert.Equal(2, c.RegisterNewDevices());

        Assert.Equal(2, c.StickProfiles.Count());
        Assert.Empty(c.UnassignedDevices);
    }
}
