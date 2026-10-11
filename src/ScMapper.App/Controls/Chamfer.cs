using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScMapper.App.Controls;

internal static class ChamferGeometry
{
    /// <summary>Rectangle with the top-left and bottom-right corner cut off at 45 degrees.</summary>
    public static Geometry Create(Rect r, double cut)
    {
        cut = Math.Max(0, Math.Min(cut, Math.Min(r.Width, r.Height) / 2));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left + cut, r.Top), true, true);
            c.LineTo(new Point(r.Right, r.Top), true, false);
            c.LineTo(new Point(r.Right, r.Bottom - cut), true, false);
            c.LineTo(new Point(r.Right - cut, r.Bottom), true, false);
            c.LineTo(new Point(r.Left, r.Bottom), true, false);
            c.LineTo(new Point(r.Left, r.Top + cut), true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Background layer of buttons, inputs and chips: a chamfered fill with an outline.</summary>
public sealed class ChamferFrame : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush),
        typeof(ChamferFrame), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(ChamferFrame), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(nameof(Cut), typeof(double),
        typeof(ChamferFrame), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var pen = Stroke == null ? null : new Pen(Stroke, 1);
        // Half a pixel in, so the 1px outline stays on whole pixels.
        var rect = new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1);
        dc.DrawGeometry(Fill, pen, ChamferGeometry.Create(rect, Cut));
    }
}

/// <summary>Panel with a chamfered outline and small corner ticks; replaces the rounded Border cards.</summary>
public class Card : Border
{
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(nameof(Cut), typeof(double),
        typeof(Card), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }

    public Card()
    {
        // The accent ticks are drawn with the current accent brush: repaint when the theme changes.
        Loaded += (_, _) => ThemeManager.Changed += Repaint;
        Unloaded += (_, _) => ThemeManager.Changed -= Repaint;
    }

    private void Repaint() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var rect = new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1);
        var pen = BorderBrush == null || BorderThickness.Left <= 0 ? null : new Pen(BorderBrush, 1);
        dc.DrawGeometry(Background, pen, ChamferGeometry.Create(rect, Cut));
        // Accent ticks on the two uncut corners.
        if (Application.Current.TryFindResource("AccentBrush") is Brush acc && ActualWidth > 40 && ActualHeight > 40)
        {
            var tick = new Pen(acc, 2);
            dc.DrawLine(tick, new Point(ActualWidth - 1, 1), new Point(ActualWidth - 11, 1));
            dc.DrawLine(tick, new Point(ActualWidth - 1, 0), new Point(ActualWidth - 1, 10));
            dc.DrawLine(tick, new Point(1, ActualHeight - 1), new Point(11, ActualHeight - 1));
            dc.DrawLine(tick, new Point(1, ActualHeight), new Point(1, ActualHeight - 10));
        }
    }
}
