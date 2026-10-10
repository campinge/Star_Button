using ScMapper.Core.Model;

namespace ScMapper.Core.Input;

/// <summary>Live state of one device. Axes are normalized to -1..1, hats are angles in degrees or -1.</summary>
public sealed class DeviceState
{
    public bool[] Buttons { get; init; } = Array.Empty<bool>();
    public double[] Axes { get; init; } = Array.Empty<double>();
    public int[] Hats { get; init; } = Array.Empty<int>();
}

/// <summary>An input edge: inputName is "button5" or "hat1_up".</summary>
public sealed record InputEvent(Guid InstanceGuid, string Input, bool Pressed, DateTime Utc);

public interface IDeviceHub : IDisposable
{
    IReadOnlyList<DeviceInfo> Devices { get; }
    DeviceState? GetState(Guid instanceGuid);
    event Action? DevicesChanged;
    event Action<InputEvent>? InputChanged;
    void Start();
}

public interface IKeyOutput
{
    void Down(IReadOnlyList<KeyStroke> keys);
    void Up(IReadOnlyList<KeyStroke> keys);
}

public static class HatMath
{
    /// <summary>Directions pressed by a POV angle (hundredths of degrees or degrees; -1 = centered).</summary>
    public static (bool Up, bool Right, bool Down, bool Left) Directions(int angle)
    {
        if (angle < 0 || angle == 0xFFFF || angle == 65535) return default;
        // DirectInput reports centidegrees (0..35999).
        double deg = angle / 100.0 % 360;
        bool up = deg >= 315 || deg <= 45;
        bool right = deg >= 45 && deg <= 135;
        bool down = deg >= 135 && deg <= 225;
        bool left = deg >= 225 && deg <= 315;
        return (up, right, down, left);
    }

    public static readonly string[] Names = { "up", "right", "down", "left" };
}
