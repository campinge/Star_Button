using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

internal sealed class FakeHub : IDeviceHub
{
    public List<DeviceInfo> List { get; } = new();
    public IReadOnlyList<DeviceInfo> Devices => List;
    public event Action? DevicesChanged;
    public event Action<InputEvent>? InputChanged;
    public Dictionary<Guid, DeviceState> States { get; } = new();
    public DeviceState? GetState(Guid g) => States.TryGetValue(g, out var s) ? s : new();
    public void Start() { }
    public void Dispose() { }
    public void Raise(Guid g, string input, bool pressed) => InputChanged?.Invoke(new InputEvent(g, input, pressed, DateTime.UtcNow));
    public void Plug() => DevicesChanged?.Invoke();
}

internal sealed class FakeOutput : IKeyOutput
{
    public List<string> Log { get; } = new();
    private readonly object _l = new();
    public void Down(IReadOnlyList<KeyStroke> k) { lock (_l) Log.Add("down:" + string.Join("+", k.Select(x => x.Scan.ToString("X")))); }
    public void Up(IReadOnlyList<KeyStroke> k) { lock (_l) Log.Add("up:" + string.Join("+", k.Select(x => x.Scan.ToString("X")))); }
    public string[] Snapshot() { lock (_l) return Log.ToArray(); }

    /// <summary>Scan codes that went down and were not released since.</summary>
    public string[] Held()
    {
        var down = new HashSet<string>();
        foreach (var e in Snapshot())
        {
            var set = e[(e.IndexOf(':') + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (e.StartsWith("down:")) down.UnionWith(set); else down.ExceptWith(set);
        }
        return down.ToArray();
    }
}

public class ControllerTests
{
    private static AppStore TmpStore() =>
        new(Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N")));

    private static DeviceInfo Dev(int idx, int vid, int pid, string name = "Dev") =>
        new(Guid.NewGuid(), name, name, vid, pid, $"path{idx}", 12, 2, 0, idx);

    private static void WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 100 && !cond(); i++) Thread.Sleep(20);
    }

    [Fact]
    public void New_devices_are_registered_with_profile_and_default_instance()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev(0, 1, 1, "Alpha"));
        hub.List.Add(Dev(1, 2, 2, "Beta"));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        Assert.Equal(2, c.Store.Data.Sticks.Count);
        Assert.All(c.Store.Data.Sticks, s => Assert.NotNull(c.Store.Profile(s.ProfileId)));
        Assert.Equal(new int?[] { 1, 2 }, c.Store.Data.Sticks.Select(s => s.ScInstance).ToArray());
    }

    [Fact]
    public void Instance_follows_replug_order_unless_manual()
    {
        var hub = new FakeHub();
        var a = Dev(0, 1, 1, "Alpha"); var b = Dev(1, 2, 2, "Beta");
        hub.List.AddRange(new[] { a, b });
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        hub.List.Clear();
        hub.List.Add(b with { EnumIndex = 0 });
        hub.List.Add(a with { EnumIndex = 1 });
        c.Reconcile(); c.RegisterNewDevices();
        var alpha = c.Store.Data.Sticks.Single(s => s.Name == "Alpha");
        var beta = c.Store.Data.Sticks.Single(s => s.Name == "Beta");
        Assert.Equal(2, alpha.ScInstance);
        Assert.Equal(1, beta.ScInstance);
    }

    [Fact]
    public void Identical_models_after_replug_need_manual_remap()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev(0, 8, 8, "Generic"));
        hub.List.Add(Dev(1, 8, 8, "Generic"));
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        Assert.Equal(2, c.Store.Data.Sticks.Count);
        var comms = c.Store.Data.Sticks[0];   // a stick keeps its device name; identity is the record
        // New instance GUIDs and paths for both after a reboot.
        hub.List.Clear();
        hub.List.Add(new DeviceInfo(Guid.NewGuid(), "Generic", "Generic", 8, 8, "new0", 12, 2, 0, 0));
        hub.List.Add(new DeviceInfo(Guid.NewGuid(), "Generic", "Generic", 8, 8, "new1", 12, 2, 0, 1));
        c.Reconcile(); c.RegisterNewDevices();
        Assert.Equal(2, c.Store.Data.Sticks.Count);          // no duplicates created
        Assert.Equal(2, c.Match.Unmatched.Count);
        Assert.Equal(2, c.Match.Missing.Count);

        // The user maps them by hand.
        c.AssignDevice(comms, c.Match.Unmatched[1]);
        Assert.True(c.IsConnected(comms));
        Assert.Equal(2, comms.ScInstance);
    }

    [Fact]
    public void Capture_with_axes_reports_the_moved_axis_by_name()
    {
        var hub = new FakeHub();
        var d = Dev(0, 1, 1, "Alpha") with { AxisSlots = new[] { 0, 1, 5 } };   // X, Y, RZ
        hub.List.Add(d);
        hub.States[d.InstanceGuid] = new DeviceState { Axes = new[] { 0.0, 0.0, -1.0 } };
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();

        CaptureResult? got = null;
        c.BeginCapture(r => got = r, includeAxes: true);
        Thread.Sleep(80);
        hub.States[d.InstanceGuid] = new DeviceState { Axes = new[] { 0.1, 0.0, 0.9 } };   // twist (RZ) moves a lot
        WaitFor(() => got != null);
        Assert.NotNull(got);
        Assert.Equal("rotz", got!.Input);
        Assert.False(c.IsCapturing);
    }

    [Fact]
    public void Capture_swallows_next_press_and_skips_rules()
    {
        var hub = new FakeHub();
        var d = Dev(0, 1, 1, "Alpha");
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(TmpStore(), hub, output);
        c.Reconcile(); c.RegisterNewDevices();
        var stick = c.Store.Data.Sticks[0];
        c.SetRule(c.Store.Profile(stick.ProfileId)!, "button3", new ButtonRule { OnPress = new KeyAction { Keys = "u" } });

        CaptureResult? got = null;
        c.BeginCapture(r => got = r);
        hub.Raise(d.InstanceGuid, "button3", true);
        Assert.NotNull(got);
        Assert.Equal("button3", got!.Input);
        Assert.Equal(stick.Id, got.Stick!.Id);
        Thread.Sleep(100);
        Assert.Empty(output.Snapshot());
    }

    [Fact]
    public void Rules_press_release_and_hold()
    {
        var hub = new FakeHub();
        var d = Dev(0, 1, 1, "Alpha");
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(TmpStore(), hub, output);
        c.Engine.TapMilliseconds = 1;
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;
        c.SetRule(prof, "button1", new ButtonRule { OnPress = new KeyAction { Keys = "u" }, OnRelease = new KeyAction { Keys = "lshift+d" } });
        c.SetRule(prof, "button2", new ButtonRule { OnPress = new KeyAction { Keys = "o", Hold = true } });

        hub.Raise(d.InstanceGuid, "button1", true);
        hub.Raise(d.InstanceGuid, "button1", false);
        WaitFor(() => output.Snapshot().Length >= 6);
        Assert.Equal(new[] { "down:16", "up:16", "down:2A+20", "up:2A+20" }[..4],
            output.Snapshot().Take(4).ToArray());

        output.Log.Clear();
        hub.Raise(d.InstanceGuid, "button2", true);
        WaitFor(() => output.Snapshot().Length >= 1);
        Assert.Equal(new[] { "down:18" }, output.Snapshot());
        hub.Raise(d.InstanceGuid, "button2", false);
        WaitFor(() => output.Snapshot().Length >= 2);
        Assert.Equal("up:18", output.Snapshot()[1]);
    }
}
