using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScMapper.Platform;

namespace ScMapper.App.Views;

/// <summary>Switch and live display for the anti-AFK walker: taps W, A, S, D in turn at random intervals.</summary>
public partial class AntiAfkView : UserControl
{
    private readonly AntiAfk _afk = new(new SendInputKeyboard());
    private readonly DispatcherTimer _paint = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };

    public AntiAfkView()
    {
        InitializeComponent();
        Dial.Source = _afk;
        _paint.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) _paint.Start(); else _paint.Stop(); };
        Application.Current.Exit += (_, _) => _afk.Stop();
    }

    private void Toggle_Click(object s, RoutedEventArgs e)
    {
        if (_afk.IsRunning) _afk.Stop();
        else
        {
            ApplyInterval();
            _afk.NextIndex = 0;
            _afk.Start();
        }
        Refresh();
    }

    private void Refresh()
    {
        bool run = _afk.IsRunning;
        Toggle.Content = run ? "Stop" : "Start";
        State.Text = !run ? "Stopped" : !_afk.GameRunning ? "Waiting for Star Citizen" : "Running";

        LastTap.Text = _afk.LastIndex >= 0
            ? $"Last tap  {AntiAfk.Keys[_afk.LastIndex]}  at {_afk.LastAt:HH:mm:ss}"
            : "No tap yet";
        if (run && _afk.Skipped > 0) LastTap.Text += $"  ·  {_afk.Skipped} skipped (game not in focus)";

        // The warning is always there; its wording gets specific once taps are actually being missed.
        bool blocked = run && !_afk.GameInFront;
        WarnTitle.Text = blocked
            ? (_afk.GameRunning ? "Star Citizen is not in focus: taps are skipped" : "Star Citizen is not running")
            : "Star Citizen has to be in focus";
        WarnText.Text = blocked
            ? "Click into the game window. The walking continues by itself once it is in front."
            : "The keys only reach the game while its window is in front. Taps are skipped while it is in the background.";
        Dial.InvalidateVisual();
    }

    private void Interval_Changed(object s, TextChangedEventArgs e) => ApplyInterval();

    private void ApplyInterval()
    {
        if (MinBox == null || MaxBox == null) return;
        if (!double.TryParse(MinBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var lo) || lo < 1) lo = 20;
        if (!double.TryParse(MaxBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var hi) || hi < lo) hi = Math.Max(lo, 60);
        _afk.MinInterval = TimeSpan.FromSeconds(lo);
        _afk.MaxInterval = TimeSpan.FromSeconds(hi);
    }
}
