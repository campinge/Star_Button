using System.Runtime.InteropServices;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using Vortice.DirectInput;

namespace ScMapper.Platform;

/// <summary>
/// Enumerates game controllers via DirectInput (the same API Star Citizen uses, so the order
/// matches its jsN numbering) and polls them in the background.
/// </summary>
public sealed class DirectInputHub : IDeviceHub
{
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();

    private readonly IDirectInput8 _di = DInput.DirectInput8Create();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Opened> _open = new();
    private Thread? _thread;
    private volatile bool _stop;
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

    private sealed class Opened
    {
        public required IDirectInputDevice8 Device;
        public required DeviceInfo Info;
        public bool[] Buttons = Array.Empty<bool>();
        public double[] Axes = Array.Empty<double>();
        public int[] Hats = Array.Empty<int>();
        public bool[] HatDirs = new bool[0];
    }

    public IReadOnlyList<DeviceInfo> Devices { get { lock (_gate) return _devices; } }
    public event Action? DevicesChanged;
    public event Action<InputEvent>? InputChanged;

    public void Start()
    {
        Refresh();
        _thread = new Thread(Loop) { IsBackground = true, Name = "DirectInput poll", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public DeviceState? GetState(Guid instanceGuid)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(instanceGuid, out var o)) return null;
            return new DeviceState { Buttons = (bool[])o.Buttons.Clone(), Axes = (double[])o.Axes.Clone(), Hats = (int[])o.Hats.Clone() };
        }
    }

    private static (int Vid, int Pid) VidPid(Guid productGuid)
    {
        uint d1 = BitConverter.ToUInt32(productGuid.ToByteArray(), 0);
        return ((int)(d1 & 0xFFFF), (int)(d1 >> 16));
    }

    /// <summary>Re-enumerates; opens new devices and drops vanished ones.</summary>
    public void Refresh()
    {
        var instances = _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly).ToList();
        bool changed = false;
        lock (_gate)
        {
            var present = instances.Select(i => i.InstanceGuid).ToHashSet();
            foreach (var gone in _open.Keys.Where(k => !present.Contains(k)).ToList())
            {
                try { _open[gone].Device.Unacquire(); _open[gone].Device.Dispose(); } catch { }
                _open.Remove(gone);
                changed = true;
            }
            for (int idx = 0; idx < instances.Count; idx++)
            {
                var inst = instances[idx];
                if (_open.ContainsKey(inst.InstanceGuid)) continue;
                try
                {
                    var dev = _di.CreateDevice(inst.InstanceGuid);
                    dev.SetDataFormat<RawJoystickState>();
                    dev.SetCooperativeLevel(GetDesktopWindow(), CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    var caps = dev.Capabilities;
                    // Normalize axes to a known range and remember which axis slots exist.
                    var slots = new List<int>();
                    int sliders = 0;
                    foreach (var obj in dev.GetObjects(DeviceObjectTypeFlags.Axis))
                    {
                        try { dev.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, 65535); } catch { }
                        var t = obj.ObjectType;
                        int slot = t == ObjectGuid.XAxis ? 0 : t == ObjectGuid.YAxis ? 1 : t == ObjectGuid.ZAxis ? 2
                            : t == ObjectGuid.RxAxis ? 3 : t == ObjectGuid.RyAxis ? 4 : t == ObjectGuid.RzAxis ? 5
                            : t == ObjectGuid.Slider && sliders < 2 ? 6 + sliders++ : -1;
                        if (slot >= 0 && !slots.Contains(slot)) slots.Add(slot);
                    }
                    slots.Sort();
                    // Button instance numbers can be sparse (e.g. a hat push exposed as a high-numbered button),
                    // so size by the highest instance index, not by the reported count.
                    int buttonCount = caps.ButtonCount;
                    foreach (var obj in dev.GetObjects(DeviceObjectTypeFlags.Button))
                        buttonCount = Math.Max(buttonCount, obj.ObjectId.InstanceNumber + 1);
                    buttonCount = Math.Min(buttonCount, 128);
                    dev.Acquire();
                    var (vid, pid) = VidPid(inst.ProductGuid);
                    string? path = null;
                    try { path = dev.Properties.InterfacePath; } catch { }
                    var info = new DeviceInfo(inst.InstanceGuid, inst.ProductName, inst.InstanceName, vid, pid, path,
                        buttonCount, slots.Count, caps.PovCount, idx) { AxisSlots = slots };
                    _open[inst.InstanceGuid] = new Opened
                    {
                        Device = dev, Info = info,
                        Buttons = new bool[buttonCount],
                        Axes = new double[slots.Count],
                        Hats = Enumerable.Repeat(-1, caps.PovCount).ToArray(),
                        HatDirs = new bool[caps.PovCount * 4],
                    };
                    changed = true;
                }
                catch { /* device may be busy or vanish; next refresh retries */ }
            }
            // Star Citizen numbers DirectInput joysticks in enumeration order. XInput pads ("IG_" in the
            // HID path) are exposed as gamepads there, so they do not take a jsN slot.
            int js = 0;
            var order = new Dictionary<Guid, int>();
            foreach (var inst in instances)
            {
                bool xinput = _open.TryGetValue(inst.InstanceGuid, out var op)
                              && op.Info.InterfacePath?.Contains("&ig_", StringComparison.OrdinalIgnoreCase) == true;
                order[inst.InstanceGuid] = xinput ? -1 : js++;
            }
            _devices = _open.Values.Select(o => o.Info with { EnumIndex = order.GetValueOrDefault(o.Info.InstanceGuid, -1) })
                .OrderBy(d => d.EnumIndex < 0 ? int.MaxValue : d.EnumIndex).ToList();
        }
        if (changed) DevicesChanged?.Invoke();
    }

    private void Loop()
    {
        int tick = 0;
        while (!_stop)
        {
            List<Opened> snapshot;
            lock (_gate) snapshot = _open.Values.ToList();
            foreach (var o in snapshot)
            {
                try { PollOne(o); }
                catch
                {
                    try { o.Device.Acquire(); } catch { }
                }
            }
            if (++tick % 200 == 0) // roughly every 2 s
            {
                try { Refresh(); } catch { }
            }
            Thread.Sleep(10);
        }
    }

    private void PollOne(Opened o)
    {
        o.Device.Poll();
        var s = o.Device.GetCurrentJoystickState();
        var events = new List<InputEvent>();
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            var btn = s.Buttons;
            for (int i = 0; i < o.Buttons.Length && i < btn.Length; i++)
            {
                if (btn[i] != o.Buttons[i])
                {
                    o.Buttons[i] = btn[i];
                    events.Add(new InputEvent(o.Info.InstanceGuid, $"button{i + 1}", btn[i], now));
                }
            }
            int[] raw = { s.X, s.Y, s.Z, s.RotationX, s.RotationY, s.RotationZ,
                          s.Sliders.Length > 0 ? s.Sliders[0] : 32767, s.Sliders.Length > 1 ? s.Sliders[1] : 32767 };
            var slots = o.Info.AxisSlots;
            for (int i = 0; i < o.Axes.Length && i < slots.Count; i++)
                o.Axes[i] = Math.Clamp((raw[slots[i]] - 32767.5) / 32767.5, -1, 1);
            var pov = s.PointOfViewControllers;
            for (int h = 0; h < o.Hats.Length && h < pov.Length; h++)
            {
                o.Hats[h] = pov[h];
                var d = HatMath.Directions(pov[h]);
                bool[] now4 = { d.Up, d.Right, d.Down, d.Left };
                for (int k = 0; k < 4; k++)
                {
                    int slot = h * 4 + k;
                    if (o.HatDirs[slot] != now4[k])
                    {
                        o.HatDirs[slot] = now4[k];
                        events.Add(new InputEvent(o.Info.InstanceGuid, $"hat{h + 1}_{HatMath.Names[k]}", now4[k], now));
                    }
                }
            }
        }
        // One failing subscriber must not swallow the other edges of this poll (a lost release is a stuck key).
        foreach (var e in events)
        {
            try { InputChanged?.Invoke(e); }
            catch { }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(500);
        lock (_gate)
        {
            foreach (var o in _open.Values) { try { o.Device.Dispose(); } catch { } }
            _open.Clear();
        }
        _di.Dispose();
    }
}
