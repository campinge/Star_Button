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

/// <summary>When a button action fires.</summary>
public enum ButtonTrigger
{
    Pressed = 0,
    Released = 1,
    /// <summary>Button held for a while (Star Citizen "hold" mode). Only available as a direct binding.</summary>
    LongPress = 2,
    /// <summary>Button pressed twice (Star Citizen "double_tap"). Only available as a direct binding.</summary>
    DoublePress = 3,
    /// <summary>Keys stay down for as long as the button is held (own keys only).</summary>
    Held = 4,
}

/// <summary>
/// One thing the app does for a physical input: send the keyboard key of a Star Citizen action
/// (<see cref="Map"/>/<see cref="Action"/>) or an own key combo (<see cref="Keys"/>).
/// </summary>
public sealed class ButtonAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Physical input without device prefix: "button5", "hat1_up".</summary>
    public string Input { get; set; } = "";
    public ButtonTrigger Trigger { get; set; }
    public string? Map { get; set; }
    public string? Action { get; set; }
    /// <summary>Own key combo like "lshift+d"; used when no game action is set.</summary>
    public string? Keys { get; set; }
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

    /// <summary>
    /// A trailing space is Star Citizen's "unbound" marker ("js1_ ", "kb1_ "): the game's default for that
    /// device is removed for this action.
    /// </summary>
    public bool IsUnbound => Input.EndsWith(' ');
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
    /// <summary>
    /// What physical inputs do besides their direct Star Citizen binding: send the key of a game action or an
    /// own key on press, release or while held.
    /// </summary>
    public List<ButtonAction> Actions { get; set; } = new();
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
    /// <summary>The content as last saved by the user (JSON). Differs from the live content = unsaved changes.</summary>
    public string? SavedState { get; set; }
    /// <summary>
    /// The profile that mirrors Star Citizen itself ("Default"). Read SC refreshes it; it can be edited and
    /// saved as another profile, but never deleted or renamed.
    /// </summary>
    public bool IsGameProfile { get; set; }
    /// <summary>A profile that ships with the app ("Empty"): editable, but never deleted or renamed.</summary>
    public bool IsBuiltIn { get; set; }
    /// <summary>
    /// Starts from nothing: the game's own default bindings are not shown and, on deploy, are unbound in
    /// Star Citizen. Only what the user binds in this profile is active.
    /// </summary>
    public bool NoDefaults { get; set; }
    /// <summary>
    /// The game's defaults, but without the joystick ones: those are not shown and, on deploy, are unbound in
    /// Star Citizen, so own stick bindings never collide with them. Keyboard, mouse and gamepad stay as shipped.
    /// Stored as "KeyboardMouseOnly" in older files.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("KeyboardMouseOnly")]
    public bool NoStickDefaults { get; set; }
    /// <summary>Whether the game's default bindings of a device family ("kb", "mo", "gp", "js") count in this profile.</summary>
    public bool UsesDefaults(string device) => !NoDefaults && (!NoStickDefaults || device != "js");
    /// <summary>
    /// Conflicts of this profile the user chose to ignore (see AppController.FindConflicts). Not part of the
    /// saved content: ignoring something never makes the profile "modified".
    /// </summary>
    public List<string> IgnoredConflicts { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsProtected => IsGameProfile || IsBuiltIn;
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
    public CloseAction OnClose { get; set; } = CloseAction.Ask;
    /// <summary>Visual theme key, see ThemeManager in the app ("cyan" or "amber").</summary>
    public string Theme { get; set; } = "cyan";
}

/// <summary>What the window's X button does.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CloseAction
{
    /// <summary>Ask the first time (and every time until the user ticks "don't ask again").</summary>
    Ask,
    /// <summary>Hide the window; the app keeps running in the tray.</summary>
    Tray,
    /// <summary>Quit the app.</summary>
    Exit,
}
