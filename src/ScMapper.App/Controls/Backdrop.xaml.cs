using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScMapper.App.Controls;

/// <summary>See Backdrop.xaml. The texture and the corner brackets are drawn here because they follow the theme.</summary>
public partial class Backdrop : UserControl
{
    private bool _scanning;
    private bool _showScan = true;

    /// <summary>The slow scan line; the onboarding screen has it, the working app is calmer without.</summary>
    public bool ShowScan
    {
        get => _showScan;
        set
        {
            _showScan = value;
            ScanLine.Visibility = value && !ThemeManager.IsFlat ? Visibility.Visible : Visibility.Collapsed;
            UpdateScan();
        }
    }

    public Backdrop()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += Rebuild;
            Rebuild();
        };
        Unloaded += (_, _) => ThemeManager.Changed -= Rebuild;
        SizeChanged += (_, _) => DrawBrackets();
        // The scan line only runs while the backdrop is on screen.
        IsVisibleChanged += (_, _) => UpdateScan();
        Loaded += (_, _) => UpdateScan();
    }

    private void UpdateScan()
    {
        if (_showScan && !ThemeManager.IsFlat && IsVisible && IsLoaded && !_scanning)
        {
            _scanning = true;
            var h = Math.Max(ActualHeight, 800);
            var a = new DoubleAnimation(-10, h, TimeSpan.FromSeconds(11)) { RepeatBehavior = RepeatBehavior.Forever };
            Scan.BeginAnimation(TranslateTransform.YProperty, a);
        }
        else if ((!_showScan || ThemeManager.IsFlat || !IsVisible) && _scanning)
        {
            _scanning = false;
            Scan.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private void Rebuild()
    {
        // Flat themes (Kartell) are a plain ground: no light, vignette, texture, scan line or brackets.
        var flat = ThemeManager.IsFlat;
        var v = flat ? Visibility.Collapsed : Visibility.Visible;
        TopGlow.Visibility = CornerGlow.Visibility = Vignette.Visibility = Texture.Visibility = Brackets.Visibility = v;
        UpdateScan();
        ScanLine.Visibility = _showScan && !flat ? Visibility.Visible : Visibility.Collapsed;
        Texture.Fill = ThemeManager.Current.Pattern switch
        {
            "hatch" => Hatch(),
            "dots" => Dots(),
            "none" => null,
            _ => GridTexture(),
        };
        DrawBrackets();
    }

    private static Brush Line(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Brush Tile(Geometry g, Pen pen, double size)
    {
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, g))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size, size),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return brush;
    }

    /// <summary>Faint square grid, like a navigation display.</summary>
    private static Brush GridTexture()
    {
        var pen = new Pen(Line("GridLineBrush"), 1);
        return Tile(Geometry.Parse("M0,0 L56,0 M0,0 L0,56"), pen, 56);
    }

    /// <summary>Diagonal stripes, like painted hazard plating.</summary>
    private static Brush Hatch()
    {
        var pen = new Pen(Line("GridLineBrush"), 6);
        // three strokes per tile so the stripes run through the tile edges without gaps
        return Tile(Geometry.Parse("M-8,8 L8,-8 M-8,40 L40,-8 M24,40 L40,24"), pen, 32);
    }

    /// <summary>Sparse dot matrix: quiet, no lines.</summary>
    private static Brush Dots()
    {
        var brush = new DrawingBrush(new GeometryDrawing(Line("GridLineBrush"), null,
            new EllipseGeometry(new Point(12, 12), 1.5, 1.5)))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 24, 24),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        brush.Freeze();
        return brush;
    }

    private void DrawBrackets()
    {
        Brackets.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 200 || h < 200) return;
        const double m = 14, l = 26;
        var accent = Line("AccentBrush");
        void Corner(double x, double y, int sx, int sy)
        {
            var p = new System.Windows.Shapes.Path
            {
                Stroke = accent, StrokeThickness = 1.5,
                Data = Geometry.Parse($"M{x + sx * l},{y} L{x},{y} L{x},{y + sy * l}"),
            };
            Brackets.Children.Add(p);
        }
        Corner(m, m, 1, 1);
        Corner(w - m, m, -1, 1);
        Corner(m, h - m, 1, -1);
        Corner(w - m, h - m, -1, -1);
    }
}
