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
        c.Reconcile();
        var names = new[] { "Comms", "Weapons", "Flight" };
        for (int i = 0; i < 3; i++)
        {
            c.Store.Data.Sticks[i].Name = names[i];
            c.Store.Profile(c.Store.Data.Sticks[i].ProfileId)!.Name = names[i];
        }

        // All three are plugged in again: new GUIDs and paths, different order.
        hub.List.Clear();
        for (int i = 0; i < 3; i++) hub.List.Add(Dev($"new{i}", i));
        c.Reconcile();
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
        Assert.Equal(dev[0].InstanceGuid, c.DeviceFor(c.Store.Data.Sticks.Single(s => s.Name == "Flight"))!.InstanceGuid);
        Assert.Equal(dev[1].InstanceGuid, c.DeviceFor(c.Store.Data.Sticks.Single(s => s.Name == "Comms"))!.InstanceGuid);
        Assert.Equal(dev[2].InstanceGuid, c.DeviceFor(c.Store.Data.Sticks.Single(s => s.Name == "Weapons"))!.InstanceGuid);
        Assert.Equal(2, c.Store.Data.Sticks.Single(s => s.Name == "Comms").ScInstance);   // js follows the live order
    }

    [Fact]
    public void Swapping_two_connected_sticks_moves_the_profiles()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev("a", 0));
        hub.List.Add(Dev("b", 1));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile();
        var s0 = c.Store.Data.Sticks[0];
        var s1 = c.Store.Data.Sticks[1];
        var d0 = c.DeviceFor(s0)!;
        var d1 = c.DeviceFor(s1)!;

        c.ApplyStickAssignments(new[] { (d0, s1.ProfileId!.Value), (d1, s0.ProfileId!.Value) });

        Assert.Equal(d1.InstanceGuid, c.DeviceFor(s0)!.InstanceGuid);
        Assert.Equal(d0.InstanceGuid, c.DeviceFor(s1)!.InstanceGuid);
    }
}
