using System.Text.RegularExpressions;

namespace ScMapper.Core.Sc;

/// <summary>Helpers for Star Citizen input codes of keyboard, mouse and gamepad ("kb1_f4", "mo1_mouse1", "gp1_a").</summary>
public static partial class ScInputCodes
{
    public static readonly string[] MouseTokens = { "mouse1", "mouse2", "mouse3", "mouse4", "mouse5", "mwheel_up", "mwheel_down" };

    [GeneratedRegex(@"^(kb|mo)\d+_")]
    public static partial Regex KbmPrefix();

    [GeneratedRegex(@"^gp\d+_")]
    public static partial Regex GamepadPrefix();

    public static bool IsKbm(string input) => KbmPrefix().IsMatch(input);
    public static bool IsGamepad(string input) => GamepadPrefix().IsMatch(input);

    /// <summary>Converts the app's key names (KeyCombo) to the tokens SC uses in its profile.</summary>
    public static string ToScToken(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Length == 3 && n.StartsWith("np") && char.IsDigit(n[2])) return "np_" + n[2];
        return n switch
        {
            "esc" => "escape",
            "return" => "enter",
            "pageup" => "pgup",
            "pagedown" => "pgdn",
            "shift" => "lshift",
            "ctrl" => "lctrl",
            "alt" => "lalt",
            _ => n,
        };
    }

    /// <summary>
    /// Builds the rebind input for keyboard/mouse: modifiers and key (app names) plus an optional mouse token.
    /// Pure mouse inputs use the mouse device prefix, everything else the keyboard prefix.
    /// </summary>
    public static string? BuildKbm(string? keyCombo, string? mouseToken)
    {
        var tokens = (keyCombo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToScToken).ToList();
        bool hasMouse = !string.IsNullOrWhiteSpace(mouseToken);
        if (tokens.Count == 0 && !hasMouse) return null;
        if (hasMouse) tokens.Add(mouseToken!.Trim());
        return (tokens.Count == 1 && hasMouse ? "mo1_" : "kb1_") + string.Join("+", tokens);
    }

    public static string? BuildGamepad(string? modifier, string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(modifier)) parts.Add(modifier.Trim());
        parts.Add(input.Trim());
        return "gp1_" + string.Join("+", parts);
    }

    /// <summary>Human readable form: "kb1_lshift+k" → "Lshift + K".</summary>
    public static string Pretty(string raw)
    {
        var s = Regex.Replace(raw, @"^(kb|mo|gp)\d+_", "");
        return string.Join(" + ", s.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(t =>
            t.Length == 1 ? t.ToUpperInvariant() : char.ToUpperInvariant(t[0]) + t[1..].Replace('_', ' ')));
    }
}
