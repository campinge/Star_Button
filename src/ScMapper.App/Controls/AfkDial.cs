using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ScMapper.Platform;

namespace ScMapper.App.Controls;

/// <summary>
/// Ring with the four keys W, A, S, D at the compass points. The arc fills along the ring from the key that was
/// last tapped towards the next one (counter-clockwise, the way the character walks); the centre counts down.
/// </summary>
public sealed class AfkDial : FrameworkElement
{
    private const double KeySize = 60;

    public AntiAfk? Source { get; set; }

    private static Brush Res(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;

    private FormattedText Text(string s, double size, Brush b, bool bold = false, bool mono = false)
    {
        var family = Application.Current.TryFindResource(mono ? "MonoFont" : "UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static Point OnRing(Point c, double r, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var accent = Res("AccentBrush", Brushes.Cyan);
        var muted = Res("MutedBrush", Brushes.Gray);
        var text = Res("TextBrush", Brushes.White);
        var border = Res("BorderBrush", Brushes.DimGray);
        var border2 = Res("Border2Brush", Brushes.Gray);
        var panel = Res("PanelBrush", Brushes.Black);
        var panel2 = Res("Panel2Brush", Brushes.Black);
        var onAccent = Res("OnAccentBrush", Brushes.Black);
        var good = Res("GoodBrush", Brushes.Green);
        var warn = Res("WarnBrush", Brushes.Orange);

        double side = Math.Min(ActualWidth, ActualHeight);
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = side / 2 - KeySize / 2 - 6;
        var afk = Source;
        bool running = afk?.IsRunning == true;

        // Track and tick marks.
        dc.DrawEllipse(panel2, new Pen(border, 6), c, r, r);
        for (int i = 0; i < 48; i++)
        {
            double a = i * 7.5, len = i % 12 == 0 ? 10 : i % 4 == 0 ? 6 : 3;
            dc.DrawLine(new Pen(i % 12 == 0 ? border2 : border, 1), OnRing(c, r - 20, a), OnRing(c, r - 20 - len, a));
        }

        int next = afk?.NextIndex ?? 0;
        int holding = afk?.HoldingIndex ?? -1;
        double progress = 0;
        double left = 0;
        if (running && afk!.Wait > TimeSpan.Zero && holding < 0)
        {
            left = Math.Max(0, (afk.NextAt - DateTime.UtcNow).TotalSeconds);
            progress = Math.Clamp(1 - left / afk.Wait.TotalSeconds, 0, 1);
        }
        else if (holding >= 0) progress = 1;

        // Arc from the previous key to the next one.
        if (running && progress > 0.001)
        {
            double target = 90 + 90 * next, start = target - 90, end = start + 90 * progress;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(OnRing(c, r, start), false, false);
                g.ArcTo(OnRing(c, r, end), new Size(r, r), 0, 90 * progress > 180, SweepDirection.Counterclockwise, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        }

        // The four keys: W top, A left, S bottom, D right.
        for (int i = 0; i < 4; i++)
        {
            var p = OnRing(c, r, 90 + 90 * i);
            var box = new Rect(p.X - KeySize / 2, p.Y - KeySize / 2, KeySize, KeySize);
            bool isHold = running && i == holding, isNext = running && i == next && holding < 0;
            if (isHold)
                dc.DrawRoundedRectangle(null, new Pen(accent, 2) { Brush = Dim(accent, 0.35) }, Rect.Inflate(box, 6, 6), 12, 12);
            dc.DrawRoundedRectangle(isHold ? accent : panel, new Pen(isHold || isNext ? accent : border2, isNext ? 2 : 1.5),
                box, 8, 8);
            var t = Text(AntiAfk.Keys[i].ToString(), 24, isHold ? onAccent : isNext ? accent : muted, bold: true, mono: true);
            dc.DrawText(t, new Point(p.X - t.Width / 2, p.Y - t.Height / 2));
        }

        // Centre.
        string big, small;
        Brush bigBrush = text;
        if (!running) { big = "OFF"; small = "NOT RUNNING"; bigBrush = muted; }
        else if (!afk!.GameRunning) { big = "—"; small = "STAR CITIZEN NOT FOUND"; bigBrush = warn; }
        else if (!afk.GameInFront) { big = "II"; small = "WAITING FOR FOCUS"; bigBrush = warn; }
        else if (holding >= 0) { big = AntiAfk.Keys[holding].ToString(); small = "PRESSING"; bigBrush = accent; }
        else { big = Math.Ceiling(left).ToString("0"); small = $"SECONDS TO  {AntiAfk.Keys[next]}"; }

        var bt = Text(big, 54, bigBrush, bold: true, mono: true);
        dc.DrawText(bt, new Point(c.X - bt.Width / 2, c.Y - bt.Height / 2 - 8));
        var st = Text(small, 11, muted, mono: true);
        dc.DrawText(st, new Point(c.X - st.Width / 2, c.Y + bt.Height / 2 - 4));
        if (running && afk!.GameRunning && afk.GameInFront)
        {
            var dot = Text($"{afk.Taps} TAP{(afk.Taps == 1 ? "" : "S")}", 11, good, mono: true);
            dc.DrawText(dot, new Point(c.X - dot.Width / 2, c.Y - bt.Height / 2 - 28));
        }
    }

    private static Brush Dim(Brush b, double opacity)
    {
        var d = b.CloneCurrentValue();
        d.Opacity = opacity;
        return d;
    }
}
