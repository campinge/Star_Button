namespace ScMapper.Core.Model;

/// <summary>What a button does when the key rule fires: a key combo like "lshift+d".</summary>
public sealed class KeyAction
{
    public string Keys { get; set; } = "";
    /// <summary>For press actions: keep the keys held until the button is released (otherwise tap).</summary>
    public bool Hold { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Keys);
}

/// <summary>Keyboard output rule for one physical input (e.g. "button5" or "hat1_up").</summary>
public sealed class ButtonRule
{
    public string? Label { get; set; }
    public KeyAction? OnPress { get; set; }
    public KeyAction? OnRelease { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Label)
                           && (OnPress?.IsEmpty ?? true) && (OnRelease?.IsEmpty ?? true);
}

/// <summary>Binding of a Star Citizen action to a physical input, independent of js numbers.</summary>
public sealed class ScBind
{
    public string Map { get; set; } = "";
    public string Action { get; set; } = "";
    /// <summary>Physical input without device prefix: "button5", "hat1_up".</summary>
    public string Input { get; set; } = "";
    /// <summary>press (default), tap, hold, double_tap, ... null = SC default.</summary>
    public string? Mode { get; set; }

    public string ActionKey => $"{Map}/{Action}";
}

public enum ProfileKind
{
    /// <summary>Per-stick profile: button rules and joystick bindings (jsN is resolved at deploy).</summary>
    Stick = 0,
    /// <summary>The single keyboard + mouse layer. Bind inputs are raw SC codes ("kb1_f4", "mo1_mouse1").</summary>
    KeyboardMouse = 1,
    /// <summary>The single gamepad layer. Bind inputs are raw SC codes ("gp1_shoulderl+a").</summary>
    Gamepad = 2,
}

/// <summary>A reusable controller profile ("Comms", "Flight", ...). Assigned to a stick.</summary>
public sealed class ControllerProfile
{
    public override string ToString() => Name;
    public ProfileKind Kind { get; set; } = ProfileKind.Stick;
    public bool IsSystem => Kind != ProfileKind.Stick;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public Dictionary<string, ButtonRule> Rules { get; set; } = new();
    public List<ScBind> Binds { get; set; } = new();
}

/// <summary>
/// A complete setup ("Mining", "Combat"): the keyboard/mouse layer, the gamepad layer and which stick
/// layout each stick uses. This is what the user calls a profile; it is deployed to Star Citizen as a whole.
/// </summary>
public sealed class Setup
{
    public override string ToString() => Name;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Profile";
    public ControllerProfile Keyboard { get; set; } = new() { Kind = ProfileKind.KeyboardMouse, Name = "Keyboard & Mouse" };
    public ControllerProfile Gamepad { get; set; } = new() { Kind = ProfileKind.Gamepad, Name = "Gamepad" };
    /// <summary>Stick record id → stick layout id (a <see cref="ControllerProfile"/> of kind Stick).</summary>
    public Dictionary<Guid, Guid> StickLayouts { get; set; } = new();
}

/// <summary>A stick the user has named. Matched to connected hardware by the Matcher.</summary>
public sealed class StickRecord
{
    public override string ToString() => Name;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Vid { get; set; }
    public int Pid { get; set; }
    public string ProductName { get; set; } = "";
    public Guid? InstanceGuid { get; set; }
    public string? InterfacePath { get; set; }
    public int Buttons { get; set; }
    public int Axes { get; set; }
    public int Hats { get; set; }
    /// <summary>The jsN number Star Citizen uses for this stick. Null = not part of deploys.</summary>
    public int? ScInstance { get; set; }
    /// <summary>When false the instance follows the live DirectInput order (what SC will use after a restart).</summary>
    public bool ScInstanceManual { get; set; }
    public Guid? ProfileId { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>A snapshot of a physical device as seen by the OS right now.</summary>
public sealed record DeviceInfo(
    Guid InstanceGuid, string ProductName, string InstanceName, int Vid, int Pid,
    string? InterfacePath, int Buttons, int Axes, int Hats, int EnumIndex)
{
    /// <summary>Axis slots present on the device: 0=X 1=Y 2=Z 3=RX 4=RY 5=RZ 6=Slider1 7=Slider2.</summary>
    public IReadOnlyList<int> AxisSlots { get; init; } = Array.Empty<int>();

    /// <summary>The jsN number Star Citizen will most likely use (DirectInput order + 1).</summary>
    public int DefaultScInstance => EnumIndex + 1;

    /// <summary>False for XInput pads, which Star Citizen lists as gamepads rather than js numbers.</summary>
    public bool HasJsNumber => EnumIndex >= 0;
}

public sealed class AppSettings
{
    public string? ScRoot { get; set; }
    public string Channel { get; set; } = "LIVE";
    public bool OutputEnabled { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
}
