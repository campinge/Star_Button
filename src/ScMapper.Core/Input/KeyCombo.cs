namespace ScMapper.Core.Input;

public readonly record struct KeyStroke(ushort Scan, bool Extended, bool IsModifier);

/// <summary>Parses combos like "lshift+d" or "ctrl+f5" into hardware scan codes (set 1).</summary>
public static class KeyCombo
{
    private static readonly Dictionary<string, (ushort Scan, bool Ext)> Table = Build();

    private static Dictionary<string, (ushort, bool)> Build()
    {
        var t = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase);
        void A(string n, ushort s, bool e = false) => t[n] = (s, e);

        const string row1 = "qwertyuiop", row2 = "asdfghjkl", row3 = "zxcvbnm";
        for (int i = 0; i < row1.Length; i++) A(row1[i].ToString(), (ushort)(0x10 + i));
        for (int i = 0; i < row2.Length; i++) A(row2[i].ToString(), (ushort)(0x1E + i));
        for (int i = 0; i < row3.Length; i++) A(row3[i].ToString(), (ushort)(0x2C + i));
        for (int i = 1; i <= 9; i++) A(i.ToString(), (ushort)(0x01 + i));
        A("0", 0x0B);
        for (int i = 1; i <= 10; i++) A("f" + i, (ushort)(0x3A + i));
        A("f11", 0x57); A("f12", 0x58);
        A("esc", 0x01); A("escape", 0x01); A("minus", 0x0C); A("-", 0x0C); A("equals", 0x0D); A("=", 0x0D);
        A("backspace", 0x0E); A("tab", 0x0F); A("lbracket", 0x1A); A("[", 0x1A); A("rbracket", 0x1B); A("]", 0x1B);
        A("enter", 0x1C); A("return", 0x1C); A("semicolon", 0x27); A(";", 0x27); A("apostrophe", 0x28); A("'", 0x28);
        A("grave", 0x29); A("`", 0x29); A("backslash", 0x2B); A("\\", 0x2B);
        A("comma", 0x33); A(",", 0x33); A("period", 0x34); A(".", 0x34); A("slash", 0x35); A("/", 0x35);
        A("space", 0x39); A("capslock", 0x3A);
        A("lshift", 0x2A); A("shift", 0x2A); A("rshift", 0x36);
        A("lctrl", 0x1D); A("ctrl", 0x1D); A("rctrl", 0x1D, true);
        A("lalt", 0x38); A("alt", 0x38); A("ralt", 0x38, true);
        A("insert", 0x52, true); A("delete", 0x53, true); A("home", 0x47, true); A("end", 0x4F, true);
        A("pgup", 0x49, true); A("pageup", 0x49, true); A("pgdn", 0x51, true); A("pagedown", 0x51, true);
        A("up", 0x48, true); A("down", 0x50, true); A("left", 0x4B, true); A("right", 0x4D, true);
        A("scrolllock", 0x46); A("pause", 0x45);
        ushort[] np = { 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
        for (int i = 0; i <= 9; i++) A("np" + i, np[i]);
        A("np_add", 0x4E); A("np_subtract", 0x4A); A("np_multiply", 0x37); A("np_divide", 0x35, true);
        A("np_period", 0x53); A("np_enter", 0x1C, true);
        return t;
    }

    private static readonly HashSet<ushort> ModScans = new() { 0x2A, 0x36, 0x1D, 0x38 };

    public static IReadOnlyCollection<string> KnownKeys => Table.Keys;

    public static bool TryParse(string? combo, out List<KeyStroke> keys, out string? error)
    {
        keys = new List<KeyStroke>();
        error = null;
        if (string.IsNullOrWhiteSpace(combo)) { error = "empty"; return false; }
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Table.TryGetValue(part, out var v)) { error = $"unknown key '{part}'"; keys.Clear(); return false; }
            keys.Add(new KeyStroke(v.Scan, v.Ext, ModScans.Contains(v.Scan)));
        }
        // Modifiers go first so "d+lshift" and "lshift+d" behave the same.
        keys = keys.OrderByDescending(k => k.IsModifier).ToList();
        return true;
    }
}
