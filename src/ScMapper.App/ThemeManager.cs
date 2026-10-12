using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ScMapper.App;

/// <summary>
/// Switches the colour scheme at runtime by replacing the shared brushes of Theme.xaml (all XAML uses DynamicResource).
/// Shapes, fonts and layout are the same for every theme; only these values differ.
/// </summary>
public static class ThemeManager
{
    /// <param name="Pattern">Backdrop texture: "grid" (navigation display), "hatch" (diagonal industrial stripes) or "dots".</param>
    public sealed record ThemeInfo(string Key, string Title, string Hint, double Cut, Dictionary<string, string> Colors,
        string Pattern = "grid");

    public static readonly IReadOnlyList<ThemeInfo> Themes = new[]
    {
        new ThemeInfo("cyan", "Cyan Console", "Cool blue, close to the mobiGlas", 12, new()
        {
            ["Bg"] = "#060A0F", ["Panel"] = "#0B131C", ["Panel2"] = "#0F1B27", ["Border"] = "#1F3A4D", ["Border2"] = "#2C5570",
            ["Accent"] = "#4FD6FF", ["AccentDim"] = "#103246", ["AccentDeep"] = "#0A2230", ["OnAccent"] = "#02080C",
            ["Text"] = "#D7E8F2", ["Muted"] = "#6F8A9C", ["Good"] = "#4FE0A0", ["Warn"] = "#FFB648", ["Bad"] = "#FF5D5D",
            ["WarnDim"] = "#2A2210", ["Hover"] = "#12212E", ["Thumb"] = "#2C5570",
            ["Glow"] = "#0E2A3C", ["GridLine"] = "#10283A",
        }),
        new ThemeInfo("amber", "Amber Industrial", "Warm amber, heavier and rougher", 6, new()
        {
            ["Bg"] = "#0B0907", ["Panel"] = "#15110D", ["Panel2"] = "#1C1610", ["Border"] = "#3A2E22", ["Border2"] = "#5C4631",
            ["Accent"] = "#FF9D2E", ["AccentDim"] = "#3A260E", ["AccentDeep"] = "#2A1B09", ["OnAccent"] = "#120A02",
            ["Text"] = "#F0E5D4", ["Muted"] = "#8F7E69", ["Good"] = "#9AD45A", ["Warn"] = "#FFD04A", ["Bad"] = "#FF5A44",
            ["WarnDim"] = "#2E2608", ["Hover"] = "#241C13", ["Thumb"] = "#5C4631",
            ["Glow"] = "#2E1B09", ["GridLine"] = "#241A10",
        }, "hatch"),
        // Colours from das-kartell.org: near-black #151515, orange #E77E23, grey #3F3F3F, neutral (no tint) greys.
        new ThemeInfo("kartell", "Das Kartell", "Neutral charcoal with Kartell orange", 3, new()
        {
            ["Bg"] = "#0F0F0F", ["Panel"] = "#151515", ["Panel2"] = "#1C1C1C", ["Border"] = "#2B2B2B", ["Border2"] = "#3F3F3F",
            ["Accent"] = "#E77E23", ["AccentDim"] = "#3A220E", ["AccentDeep"] = "#251609", ["OnAccent"] = "#151515",
            ["Text"] = "#F2F2F2", ["Muted"] = "#8E8E8E", ["Good"] = "#6FCF7A", ["Warn"] = "#FFC14D", ["Bad"] = "#E8412F",
            ["WarnDim"] = "#2B2410", ["Hover"] = "#232323", ["Thumb"] = "#3F3F3F",
            ["Glow"] = "#2A1708", ["GridLine"] = "#232323",
        }, "dots"),
    };

    private static Effect Glow(Color c, double blur, double opacity)
    {
        var e = new DropShadowEffect { Color = c, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
        e.Freeze();
        return e;
    }

    public static ThemeInfo Current { get; private set; } = Themes[0];

    public static event Action? Changed;

    public static ThemeInfo Find(string? key) =>
        Themes.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    public static void Apply(string? key)
    {
        var t = Find(key);
        var res = Application.Current.Resources;
        foreach (var (name, hex) in t.Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            // Replace the brush: styles freeze the brushes they use, so DynamicResource lookups pick up the new one.
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[name + "Brush"] = brush;
            // Gradients, glows and masks need the bare colour as well.
            res[name + "Color"] = color;
        }
        var accent = (Color)ColorConverter.ConvertFromString(t.Colors["Accent"]);
        res["AccentGlow"] = Glow(accent, 18, 0.5);
        res["AccentGlowSoft"] = Glow(accent, 24, 0.32);
        res["CardCut"] = t.Cut;
        res["ControlCut"] = Math.Max(3, t.Cut / 2);
        Current = t;
        Changed?.Invoke();
    }
}
