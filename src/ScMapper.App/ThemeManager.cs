using System.Windows;
using System.Windows.Media;

namespace ScMapper.App;

/// <summary>
/// Switches the colour scheme at runtime by replacing the shared brushes of Theme.xaml (all XAML uses DynamicResource).
/// Shapes, fonts and layout are the same for every theme; only these values differ.
/// </summary>
public static class ThemeManager
{
    public sealed record ThemeInfo(string Key, string Title, string Hint, double Cut, Dictionary<string, string> Colors);

    public static readonly IReadOnlyList<ThemeInfo> Themes = new[]
    {
        new ThemeInfo("cyan", "Cyan Console", "Cool blue, close to the mobiGlas", 12, new()
        {
            ["Bg"] = "#060A0F", ["Panel"] = "#0B131C", ["Panel2"] = "#0F1B27", ["Border"] = "#1F3A4D", ["Border2"] = "#2C5570",
            ["Accent"] = "#4FD6FF", ["AccentDim"] = "#103246", ["AccentDeep"] = "#0A2230", ["OnAccent"] = "#02080C",
            ["Text"] = "#D7E8F2", ["Muted"] = "#6F8A9C", ["Good"] = "#4FE0A0", ["Warn"] = "#FFB648", ["Bad"] = "#FF5D5D",
            ["WarnDim"] = "#2A2210", ["Hover"] = "#12212E", ["Thumb"] = "#2C5570",
        }),
        new ThemeInfo("amber", "Amber Industrial", "Warm amber, heavier and rougher", 6, new()
        {
            ["Bg"] = "#0B0907", ["Panel"] = "#15110D", ["Panel2"] = "#1C1610", ["Border"] = "#3A2E22", ["Border2"] = "#5C4631",
            ["Accent"] = "#FF9D2E", ["AccentDim"] = "#3A260E", ["AccentDeep"] = "#2A1B09", ["OnAccent"] = "#120A02",
            ["Text"] = "#F0E5D4", ["Muted"] = "#8F7E69", ["Good"] = "#9AD45A", ["Warn"] = "#FFD04A", ["Bad"] = "#FF5A44",
            ["WarnDim"] = "#2E2608", ["Hover"] = "#241C13", ["Thumb"] = "#5C4631",
        }),
    };

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
        }
        res["CardCut"] = t.Cut;
        res["ControlCut"] = Math.Max(3, t.Cut / 2);
        Current = t;
        Changed?.Invoke();
    }
}
