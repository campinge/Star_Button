using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScMapper.Core.Input;
using ScMapper.Core.Model;

namespace ScMapper.App.Controls;

internal static class Brushes2
{
    public static Brush Get(string key) => (Brush)Application.Current.FindResource(key);
}

public static class InputNames
{
    private static readonly Dictionary<string, string> Axes = new()
    {
        ["x"] = "Axis X", ["y"] = "Axis Y", ["z"] = "Axis Z", ["rotx"] = "Axis Rx", ["roty"] = "Axis Ry",
        ["rotz"] = "Axis Rz", ["slider1"] = "Slider 1", ["slider2"] = "Slider 2",
    };

    private static readonly Dictionary<string, string> Mods = new()
    {
        ["lshift"] = "Left Shift", ["rshift"] = "Right Shift", ["lctrl"] = "Left Ctrl", ["rctrl"] = "Right Ctrl",
        ["lalt"] = "Left Alt", ["ralt"] = "Right Alt",
    };

    public static string ModifierName(string token) => Mods.GetValueOrDefault(token.ToLowerInvariant(), token);

    public static string Pretty(string input)
    {
        // "lshift+button1": keyboard modifier in front of a stick input.
        var (mods, bare) = ScMapper.Core.Sc.ScInputCodes.SplitJsInput(input);
        if (mods.Count > 0) return string.Join(" + ", mods.Select(ModifierName).Append(Pretty(bare)));
        if (Axes.TryGetValue(input, out var axis)) return axis;
        if (input.StartsWith("button")) return "Button " + input[6..];
        if (input.StartsWith("hat"))
        {
            var parts = input.Split('_');
            var arrow = parts.Length > 1 ? parts[1] switch { "up" => "↑", "down" => "↓", "left" => "←", "right" => "→", _ => "●" } : "";
            return $"Hat {parts[0][3..]} {arrow}";
        }
        return input;
    }

    public static readonly string[] AxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2" };
}

/// <summary>Grid of numbered buttons that light up live; click selects one.</summary>
public sealed class ButtonGridControl : FrameworkElement
{
    private const double Cell = 56, Gap = 8, CellH = 46;
    private bool[] _lit = Array.Empty<bool>();
    private HashSet<int> _marked = new();
    private int _count;
    private int _selected = -1;
    private int _hover = -1;

    public event Action<int>? ButtonClicked;

    public int ButtonCount
    {
        get => _count;
        set { _count = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    public int Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; InvalidateVisual(); } }
    }

    private HashSet<int> _replaced = new();
    private HashSet<int> _conflicts = new();

    /// <param name="replaced">Buttons an action of Star Button stands in for (hollow square).</param>
    /// <param name="conflicts">Replaced buttons that still carry a Star Citizen binding (warning dot).</param>
    public void SetLive(bool[] lit, HashSet<int> marked, HashSet<int>? replaced = null, HashSet<int>? conflicts = null)
    {
        replaced ??= new HashSet<int>();
        conflicts ??= new HashSet<int>();
        bool same = lit.Length == _lit.Length && lit.AsSpan().SequenceEqual(_lit) && marked.SetEquals(_marked)
                    && replaced.SetEquals(_replaced) && conflicts.SetEquals(_conflicts);
        if (same) return;
        _lit = lit; _marked = marked; _replaced = replaced; _conflicts = conflicts;
        InvalidateVisual();
    }

    private int Columns(double width) => Math.Max(1, (int)((width + Gap) / (Cell + Gap)));

    protected override Size MeasureOverride(Size a)
    {
        double w = double.IsInfinity(a.Width) ? 600 : a.Width;
        int rows = (int)Math.Ceiling(_count / (double)Columns(w));
        return new Size(w, Math.Max(0, rows * (CellH + Gap) - Gap));
    }

    private int HitTest(Point p)
    {
        int cols = Columns(ActualWidth);
        int c = (int)(p.X / (Cell + Gap)), r = (int)(p.Y / (CellH + Gap));
        if (c >= cols || c < 0 || r < 0) return -1;
        double x = p.X - c * (Cell + Gap), y = p.Y - r * (CellH + Gap);
        if (x > Cell || y > CellH) return -1;
        int idx = r * cols + c;
        return idx < _count ? idx + 1 : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.GetPosition(this));
        if (h != _hover) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int h = HitTest(e.GetPosition(this));
        if (h > 0) { Selected = h; ButtonClicked?.Invoke(h); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        int cols = Columns(ActualWidth);
        var accent = Brushes2.Get("AccentBrush");
        var panel = Brushes2.Get("PanelBrush");
        var border = Brushes2.Get("Border2Brush");
        var deep = Brushes2.Get("AccentDeepBrush");
        var redirect = Brushes2.Get("RedirectBrush");
        var onAccent = Brushes2.Get("OnAccentBrush");
        var text = Brushes2.Get("TextBrush");
        var muted = Brushes2.Get("MutedBrush");
        double cut = Application.Current.TryFindResource("ControlCut") is double c ? c : 6;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tf = new Typeface("Consolas");
        for (int i = 0; i < _count; i++)
        {
            int n = i + 1;
            var rect = new Rect((i % cols) * (Cell + Gap) + 0.5, (i / cols) * (CellH + Gap) + 0.5, Cell - 1, CellH - 1);
            bool lit = i < _lit.Length && _lit[i];
            bool sel = n == _selected;
            // A held button that is redirected to an action lights in its own colour.
            var litBrush = _replaced.Contains(n) ? redirect : accent;
            var fill = lit ? litBrush : sel ? deep : (n == _hover ? Brushes2.Get("HoverBrush") : panel);
            var pen = new Pen(lit ? litBrush : sel || n == _hover ? accent : border, sel ? 2 : 1);
            dc.DrawGeometry(fill, pen, ChamferGeometry.Create(rect, cut));
            var ft = new FormattedText(n.ToString("00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 15, lit ? onAccent : (_marked.Contains(n) || sel ? text : muted), dpi);
            dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
            if (_marked.Contains(n))
                dc.DrawRectangle(lit ? onAccent : accent, null, new Rect(rect.Right - 9, rect.Y + 5, 4, 4));
            if (_replaced.Contains(n))
                dc.DrawRectangle(null, new Pen(lit ? onAccent : accent, 1), new Rect(rect.Right - 17.5, rect.Y + 5.5, 4, 4));
            if (_conflicts.Contains(n))
                dc.DrawRectangle(Brushes2.Get("WarnBrush"), null, new Rect(rect.Right - 25, rect.Y + 5, 4, 4));
        }
    }
}

/// <summary>Read-only display of axes (pads + bars) and hats.</summary>
public sealed class AxesControl : FrameworkElement
{
    private DeviceInfo? _info;
    private DeviceState? _state;

    public void Update(DeviceInfo? info, DeviceState? state)
    {
        _info = info; _state = state;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 600 : a.Width, 126);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_info == null || _state == null) return;
        var accent = Brushes2.Get("AccentBrush");
        var border = Brushes2.Get("Border2Brush");
        var grid = Brushes2.Get("BorderBrush");
        var panel = Brushes2.Get("PanelBrush");
        var muted = Brushes2.Get("MutedBrush");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tf = new Typeface("Consolas");
        double cut = Application.Current.TryFindResource("ControlCut") is double cc ? cc : 6;
        var slots = _info.AxisSlots;
        double Val(int slot) { int i = ((List<int>)slots.ToList()).IndexOf(slot); return i >= 0 && i < _state.Axes.Length ? _state.Axes[i] : 0; }
        bool Has(int slot) => slots.Contains(slot);

        double x = 0;
        var used = new HashSet<int>();
        void Pad(int sx, int sy, string title)
        {
            const double S = 100;
            var r = new Rect(x, 0, S, S);
            dc.DrawGeometry(panel, new Pen(border, 1), ChamferGeometry.Create(new Rect(r.X + 0.5, r.Y + 0.5, S - 1, S - 1), cut));
            var cp = new Pen(grid, 1);
            var mid = new Point(r.X + S / 2, r.Y + S / 2);
            dc.DrawEllipse(null, cp, mid, S / 4, S / 4);
            dc.DrawEllipse(null, cp, mid, S / 2 - 8, S / 2 - 8);
            dc.DrawLine(cp, new Point(r.X + S / 2, r.Y + 6), new Point(r.X + S / 2, r.Bottom - 6));
            dc.DrawLine(cp, new Point(r.X + 6, r.Y + S / 2), new Point(r.Right - 6, r.Y + S / 2));
            double px = r.X + S / 2 + Val(sx) * (S / 2 - 8), py = r.Y + S / 2 + Val(sy) * (S / 2 - 8);
            var ap = new Pen(accent, 1.5);
            dc.DrawEllipse(null, ap, new Point(px, py), 6, 6);
            dc.DrawEllipse(accent, null, new Point(px, py), 2, 2);
            dc.DrawLine(ap, new Point(px - 11, py), new Point(px - 7, py));
            dc.DrawLine(ap, new Point(px + 7, py), new Point(px + 11, py));
            dc.DrawLine(ap, new Point(px, py - 11), new Point(px, py - 7));
            dc.DrawLine(ap, new Point(px, py + 7), new Point(px, py + 11));
            var ft = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, muted, dpi);
            dc.DrawText(ft, new Point(r.X + (S - ft.Width) / 2, r.Bottom + 4));
            x += S + 14;
            used.Add(sx); used.Add(sy);
        }
        if (Has(0) && Has(1)) Pad(0, 1, "X / Y");
        if (Has(3) && Has(4)) Pad(3, 4, "Rx / Ry");

        // Remaining axes as vertical bars.
        foreach (int slot in slots.Where(s => !used.Contains(s)))
        {
            const double W = 22, H = 100;
            var r = new Rect(x, 0, W, H);
            dc.DrawRectangle(panel, new Pen(border, 1), new Rect(r.X + 0.5, r.Y + 0.5, W - 1, H - 1));
            double v = (Val(slot) + 1) / 2;          // 0..1 bottom to top
            double fh = Math.Max(2, v * (H - 4));
            dc.DrawRectangle(accent, null, new Rect(r.X + 3, r.Bottom - 2 - fh, W - 6, fh));
            for (int t = 1; t < 4; t++) dc.DrawLine(new Pen(grid, 1), new Point(r.X + 1, r.Y + t * H / 4), new Point(r.X + 6, r.Y + t * H / 4));
            var ft = new FormattedText(InputNames.AxisNames[slot].Replace("Slider ", "S"), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 11, muted, dpi);
            dc.DrawText(ft, new Point(r.X + (W - ft.Width) / 2, r.Bottom + 4));
            x += W + 10;
        }

        // Hats as compass.
        for (int h = 0; h < _state.Hats.Length; h++)
        {
            const double S = 70;
            x += 8;
            var c = new Point(x + S / 2, S / 2 + 6);
            dc.DrawEllipse(panel, new Pen(border, 1), c, S / 2, S / 2);
            var d = HatMath.Directions(_state.Hats[h]);
            DrawWedge(dc, c, S / 2 - 6, 0, d.Up, accent, border);
            DrawWedge(dc, c, S / 2 - 6, 90, d.Right, accent, border);
            DrawWedge(dc, c, S / 2 - 6, 180, d.Down, accent, border);
            DrawWedge(dc, c, S / 2 - 6, 270, d.Left, accent, border);
            var ft = new FormattedText($"Hat {h + 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, muted, dpi);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, S + 10));
            x += S + 10;
        }
    }

    private static void DrawWedge(DrawingContext dc, Point c, double r, double centerDeg, bool on, Brush accent, Brush border)
    {
        double a1 = (centerDeg - 90 - 38) * Math.PI / 180, a2 = (centerDeg - 90 + 38) * Math.PI / 180;
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var p2 = new Point(c.X + r * Math.Cos(a2), c.Y + r * Math.Sin(a2));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + 8 * Math.Cos((centerDeg - 90) * Math.PI / 180), c.Y + 8 * Math.Sin((centerDeg - 90) * Math.PI / 180)), true, true);
            ctx.LineTo(p1, true, false);
            ctx.ArcTo(p2, new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(on ? accent : Brushes.Transparent, new Pen(border, 1), g);
    }
}

/// <summary>Read-only box that records a keyboard combination (e.g. "lshift+d") when focused.</summary>
public sealed class KeyCaptureBox : TextBox
{
    public event Action<string>? ComboChanged;

    public KeyCaptureBox()
    {
        // Implicit styles do not apply to derived types.
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the key combination";
        GotKeyboardFocus += (_, _) => { if (string.IsNullOrEmpty(Text)) Tag = "capturing"; };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>
    /// The key as a position on the keyboard: Star Citizen binds and Star Button sends scan codes, so the key
    /// labelled Z on a German keyboard has to be recorded as the position US keyboards call Y. Falls back to the
    /// layout based table for keys that have no scan code name.
    /// </summary>
    private static string? PositionName(Key k)
    {
        int vk = KeyInterop.VirtualKeyFromKey(k);
        if (vk != 0 && vk != 0x90 /* NumLock shares a scan code with Pause */)
        {
            uint scan = MapVirtualKey((uint)vk, 0);   // MAPVK_VK_TO_VSC
            bool ext = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x6F;
            if (scan != 0 && ScMapper.Core.Input.KeyCombo.NameOfScan((ushort)scan, ext) is { } byPosition) return byPosition;
        }
        return LayoutName(k);
    }

    private static string? LayoutName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => k.ToString().ToLowerInvariant(),
        >= Key.D0 and <= Key.D9 => ((int)k - (int)Key.D0).ToString(),
        >= Key.F1 and <= Key.F12 => k.ToString().ToLowerInvariant(),
        >= Key.NumPad0 and <= Key.NumPad9 => "np" + ((int)k - (int)Key.NumPad0),
        Key.Space => "space", Key.Return => "enter", Key.Tab => "tab", Key.Escape => "esc", Key.Back => "backspace",
        Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right",
        Key.Home => "home", Key.End => "end", Key.PageUp => "pgup", Key.PageDown => "pgdn",
        Key.Insert => "insert", Key.Delete => "delete",
        Key.OemComma => "comma", Key.OemPeriod => "period", Key.OemMinus => "minus", Key.OemPlus => "equals",
        Key.Oem1 => "semicolon", Key.OemQuotes => "apostrophe", Key.Oem3 => "grave", Key.OemOpenBrackets => "lbracket",
        Key.Oem6 => "rbracket", Key.Oem5 => "backslash", Key.Oem2 => "slash",
        Key.Add => "np_add", Key.Subtract => "np_subtract", Key.Multiply => "np_multiply", Key.Divide => "np_divide",
        _ => null,
    };

    private static string? ModName(Key k) => k switch
    {
        Key.LeftShift => "lshift", Key.RightShift => "rshift", Key.LeftCtrl => "lctrl", Key.RightCtrl => "rctrl",
        Key.LeftAlt => "lalt", Key.RightAlt => "ralt",
        _ => null,
    };

    /// <summary>App name of a key or modifier ("lctrl", "r", "f5"), null for keys that cannot be bound.</summary>
    internal static string? TokenOf(Key k) => ModName(k) ?? PositionName(k);

    internal static bool IsModifierToken(string t) =>
        t is "lshift" or "rshift" or "lctrl" or "rctrl" or "lalt" or "ralt";

    private readonly List<string> _mods = new();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = ModName(key);
        if (mod != null)
        {
            if (!_mods.Contains(mod)) _mods.Add(mod);
            Set(string.Join("+", _mods), commit: false);
            return;
        }
        var n = PositionName(key);
        if (n == null) return;
        Set(string.Join("+", _mods.Append(n)), commit: true);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mod = ModName(key);
        if (mod != null)
        {
            // A modifier pressed and released alone is a valid combo (e.g. "lshift").
            if (_mods.Count > 0 && Text == string.Join("+", _mods)) ComboChanged?.Invoke(Text);
            _mods.Remove(mod);
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _mods.Clear();
        base.OnLostKeyboardFocus(e);
    }

    private void Set(string combo, bool commit)
    {
        Text = combo;
        if (commit) ComboChanged?.Invoke(combo);
    }

    public void SetComboSilently(string? combo)
    {
        _mods.Clear();
        Text = combo ?? "";
    }
}
