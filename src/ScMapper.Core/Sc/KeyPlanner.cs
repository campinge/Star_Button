using ScMapper.Core.Input;
using ScMapper.Core.Model;

namespace ScMapper.Core.Sc;

public enum KeySource
{
    None,
    /// <summary>The key lives in our keyboard layer (set by the user or assigned automatically): editable.</summary>
    Own,
    /// <summary>The game's own default key for the action.</summary>
    Default,
}

/// <summary>The keyboard combo (app notation, e.g. "lctrl+f9") that triggers a game action.</summary>
public readonly record struct ResolvedKey(string? Combo, KeySource Source);

/// <summary>Finds the key of a game action and proposes free key combos for actions that have none.</summary>
public static class KeyPlanner
{
    /// <summary>Star Citizen key tokens → the app's key names; null when the combo cannot be sent by the app.</summary>
    public static string? ToApp(string? scCombo)
    {
        if (string.IsNullOrWhiteSpace(scCombo)) return null;
        var parts = scCombo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Select(t => t.Length == 4 && t.StartsWith("np_") && char.IsDigit(t[3]) ? "np" + t[3] : t == "escape" ? "esc" : t)
            .ToList();
        var combo = string.Join("+", parts);
        return KeyCombo.TryParse(combo, out _, out _) ? combo : null;
    }

    /// <summary>Order-independent form used to compare combos.</summary>
    public static string Canonical(string combo) => string.Join("+",
        combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ScInputCodes.ToScToken).OrderBy(t => t, StringComparer.Ordinal));

    private static bool IsKeyboard(string input) => input.StartsWith("kb", StringComparison.Ordinal) && ScInputCodes.IsKbm(input);

    private static string StripPrefix(string raw) => raw[(raw.IndexOf('_') + 1)..];

    public static ResolvedKey Resolve(ControllerProfile keyboardLayer, ActionCatalog? catalog, string map, string action)
    {
        bool unbound = false;
        foreach (var b in keyboardLayer.Binds.Where(b => b.Map == map && b.Action == action && IsKeyboard(b.Input)))
        {
            if (b.IsUnbound) { unbound = true; continue; }
            if (ToApp(StripPrefix(b.Input)) is { } own) return new ResolvedKey(own, KeySource.Own);
        }
        if (!unbound && catalog?.Find(map, action) is { } def && ToApp(def.DefaultKeyboard) is { } dflt)
            return new ResolvedKey(dflt, KeySource.Default);
        return new ResolvedKey(null, KeySource.None);
    }

    /// <summary>
    /// Combos already taken, each with a short note on who holds it: game defaults, our keyboard layer, raw
    /// rebinds of the game's file and the own keys of button actions (<paramref name="buttonKeys"/>).
    /// Keyed by <see cref="Canonical"/>.
    /// </summary>
    public static Dictionary<string, string> UsageMap(ControllerProfile keyboardLayer, ActionCatalog? catalog,
        IEnumerable<string>? rawRebinds = null, IEnumerable<(string Combo, string Owner)>? buttonKeys = null)
    {
        var used = new Dictionary<string, string>();
        void Add(string? sc, string owner)
        {
            if (!string.IsNullOrWhiteSpace(sc)) used.TryAdd(Canonical(sc), owner);
        }
        // Buttons first: those are what the user just set up and wants to see named.
        foreach (var (combo, owner) in buttonKeys ?? Array.Empty<(string, string)>()) Add(combo, owner);
        if (catalog != null)
            foreach (var a in catalog.AllActions) Add(a.DefaultKeyboard, a.Label);
        foreach (var b in keyboardLayer.Binds.Where(b => IsKeyboard(b.Input) && !b.IsUnbound))
            Add(StripPrefix(b.Input), b.Action);
        foreach (var r in rawRebinds ?? Array.Empty<string>())
            if (IsKeyboard(r) && !r.EndsWith(' ')) Add(StripPrefix(r), "Star Citizen");
        return used;
    }

    /// <summary>Combos already taken (see <see cref="UsageMap"/>).</summary>
    public static HashSet<string> UsedKeys(ControllerProfile keyboardLayer, ActionCatalog? catalog,
        IEnumerable<string>? rawRebinds = null, IEnumerable<(string Combo, string Owner)>? buttonKeys = null) =>
        UsageMap(keyboardLayer, catalog, rawRebinds, buttonKeys).Keys.ToHashSet();

    // ---- combos we never offer ------------------------------------------------------------------

    private static readonly HashSet<string> CtrlKeys = new() { "lctrl", "rctrl", "ctrl" };
    private static readonly HashSet<string> AltKeys = new() { "lalt", "ralt", "alt" };
    private static readonly HashSet<string> ShiftKeys = new() { "lshift", "rshift", "shift" };

    /// <summary>
    /// Why a combo must not be bound to a button, or null when it is fine. Pressing a joystick button must
    /// never close a window, switch tasks, rotate the screen or type garbage.
    /// </summary>
    public static string? DangerReason(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToList();
        bool ctrl = parts.Any(CtrlKeys.Contains), alt = parts.Any(AltKeys.Contains), shift = parts.Any(ShiftKeys.Contains);
        var keys = parts.Where(p => !CtrlKeys.Contains(p) && !AltKeys.Contains(p) && !ShiftKeys.Contains(p)).ToList();
        bool Has(string k) => keys.Contains(k);

        if (Has("f4") && alt) return "Alt+F4 closes the active program (Star Citizen).";
        if (Has("f4") && ctrl) return "Ctrl+F4 closes the active window.";
        if (Has("w") && ctrl) return "Ctrl+W closes windows and tabs.";
        if (Has("delete") && ctrl && alt) return "Ctrl+Alt+Delete opens the Windows security screen.";
        if ((Has("esc") || Has("escape")) && (ctrl || alt))
            return "Ctrl/Alt+Esc switches programs or opens Task Manager.";
        if (Has("tab") && (alt || ctrl)) return "Alt/Ctrl+Tab switches programs.";
        if (Has("space") && alt) return "Alt+Space opens the window menu.";
        if (Has("enter") && alt) return "Alt+Enter toggles fullscreen.";
        if (ctrl && alt && keys.Any(k => k is "up" or "down" or "left" or "right"))
            return "Ctrl+Alt+Arrow rotates the screen on many graphics drivers.";
        if (ctrl && alt && keys.Any(k => k.Length >= 2 && k[0] == 'f' && int.TryParse(k.AsSpan(1), out _)))
            return "Ctrl+Alt+F-keys are graphics-driver hotkeys.";
        if (alt && keys.Any(IsNumpadDigit))
            return "Alt+Numpad digits type special characters.";
        if (shift && keys.Any(IsNumpadDigit))
            return "Shift+Numpad digits turn into cursor keys while Num Lock is on.";
        return null;
    }

    private static bool IsNumpadDigit(string k) => k.Length == 3 && k.StartsWith("np") && char.IsDigit(k[2]);

    private static readonly string[][] Modifiers =
    {
        new[] { "lctrl" }, new[] { "lalt" }, new[] { "lshift" },
        new[] { "lctrl", "lalt" }, new[] { "lctrl", "lshift" }, new[] { "lalt", "lshift" },
    };

    private static IEnumerable<string> Keys()
    {
        for (int i = 1; i <= 12; i++) yield return "f" + i;
        for (int i = 0; i <= 9; i++) yield return "np" + i;
        yield return "np_add"; yield return "np_subtract"; yield return "np_multiply"; yield return "np_divide";
    }

    /// <summary>Candidate combos, least likely to be in the way first. Dangerous ones are never offered.</summary>
    public static IEnumerable<string> Candidates()
    {
        foreach (var mods in Modifiers)
            foreach (var k in Keys())
            {
                var combo = string.Join("+", mods.Append(k));
                if (DangerReason(combo) == null) yield return combo;
            }
    }

    public static string? FreeKey(ISet<string> used) =>
        Candidates().FirstOrDefault(c => !used.Contains(Canonical(c)));

    /// <summary>All candidate combos no game action, profile or rebind uses.</summary>
    public static List<string> FreeKeys(ISet<string> used) =>
        Candidates().Where(c => !used.Contains(Canonical(c))).ToList();

    /// <summary>
    /// The next free candidate after <paramref name="after"/> (wrapping around), so repeated asks walk through
    /// the free combos. Without <paramref name="after"/> it is the first free one.
    /// </summary>
    public static string? NextFreeKey(ISet<string> used, string? after = null)
    {
        var all = Candidates().ToList();
        int start = after == null ? -1 : all.FindIndex(c => Canonical(c) == Canonical(after));
        for (int i = 1; i <= all.Count; i++)
        {
            var c = all[(start + i + all.Count) % all.Count];
            if (!used.Contains(Canonical(c))) return c;
        }
        return null;
    }

    /// <summary>The combo as written into the game's profile (kb1_...).</summary>
    public static string? ToRaw(string combo) =>
        KeyCombo.TryParse(combo, out _, out _) ? ScInputCodes.BuildKbm(combo, null) : null;
}
