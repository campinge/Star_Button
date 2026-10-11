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

    /// <summary>Combos already taken: game defaults, our keyboard layer and (optionally) raw rebinds of the game's file.</summary>
    public static HashSet<string> UsedKeys(ControllerProfile keyboardLayer, ActionCatalog? catalog, IEnumerable<string>? rawRebinds = null)
    {
        var used = new HashSet<string>();
        void Add(string? sc) { if (!string.IsNullOrWhiteSpace(sc)) used.Add(Canonical(sc)); }
        if (catalog != null)
            foreach (var a in catalog.AllActions) Add(a.DefaultKeyboard);
        foreach (var b in keyboardLayer.Binds.Where(b => IsKeyboard(b.Input) && !b.IsUnbound)) Add(StripPrefix(b.Input));
        foreach (var r in rawRebinds ?? Array.Empty<string>())
            if (IsKeyboard(r) && !r.EndsWith(' ')) Add(StripPrefix(r));
        return used;
    }

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

    /// <summary>Candidate combos, least likely to be in the way first.</summary>
    public static IEnumerable<string> Candidates()
    {
        foreach (var mods in Modifiers)
            foreach (var k in Keys())
                yield return string.Join("+", mods.Append(k));
    }

    public static string? FreeKey(ISet<string> used) =>
        Candidates().FirstOrDefault(c => !used.Contains(Canonical(c)));

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
