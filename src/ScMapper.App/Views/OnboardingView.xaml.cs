using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScMapper.Core;

namespace ScMapper.App.Views;

/// <summary>
/// The first thing a user sees, and only while there is no profile at all. One screen that says how the app
/// works (a profile holds the settings, a stick just uses one) and lists the sticks found. Leaving it makes a
/// profile for every stick, named after the stick; renaming and handing profiles to other sticks happens later
/// with the Profiles button.
/// </summary>
public partial class OnboardingView : UserControl
{
    private AppController? _c;
    private string? _lastName;
    private int _madeCount;
    private Storyboard? _flow;

    /// <summary>Raised when the screen is left: name of the last profile made and how many were made.</summary>
    public event Action<string?, int>? Completed;

    public OnboardingView()
    {
        InitializeComponent();
        // The picture of "stick → profile → Star Citizen" plays while the screen is visible.
        ExplainPanel.IsVisibleChanged += (_, _) =>
        {
            if (ExplainPanel.IsVisible) StartFlow();
            else StopFlow();
        };
    }

    public void Attach(AppController c)
    {
        _c = c;
        // A stick plugged in while the screen is up shows in the list.
        c.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
    }

    public void Begin()
    {
        _lastName = null;
        _madeCount = 0;
        Visibility = Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        if (_c == null) return;
        var waiting = _c.UnassignedDevices.OrderBy(d => d.EnumIndex).ToList();
        FoundList.Children.Clear();
        if (waiting.Count == 0)
        {
            FoundTitle.Text = "SCANNING  ·  NO STICK FOUND YET";
            FoundNote.Text = "Plug in your joystick: it shows up here. You can also continue now and plug it in later.";
            StartBtn.Content = "Continue";
            return;
        }
        FoundTitle.Text = waiting.Count == 1 ? "1 STICK FOUND" : $"{waiting.Count} STICKS FOUND";
        foreach (var d in waiting)
            FoundList.Children.Add(new TextBlock { Text = "●  " + d.ProductName.Trim(), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) });
        FoundNote.Text = waiting.Count == 1
            ? "It gets a profile with its own name."
            : "Each of them gets a profile with its own name.";
        StartBtn.Content = waiting.Count == 1 ? "Create my profile" : "Create my profiles";
    }

    private void Start_Click(object s, RoutedEventArgs e)
    {
        if (_c != null && _c.UnassignedDevices.Count > 0)
        {
            var before = _c.UnassignedDevices.Select(d => d.InstanceGuid).ToList();
            _madeCount = _c.RegisterNewDevices();
            _lastName = _c.ProfileSlots().Where(x => x.Device != null && before.Contains(x.Device.InstanceGuid))
                .OrderBy(x => x.Device!.EnumIndex).FirstOrDefault()?.Profile.Name;
        }
        Visibility = Visibility.Collapsed;
        Completed?.Invoke(_lastName, _madeCount);
    }

    // ---- the flow animation ----------------------------------------------------------------------

    private static DoubleAnimationUsingKeyFrames Keys(DependencyObject target, string property, params (double Sec, double Value)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (sec, value) in frames)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(sec)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(property));
        return a;
    }

    /// <summary>
    /// The three boxes appear one after the other, then a signal runs from the stick through the profile to
    /// the game, lighting each box as it passes; it repeats until the step is left.
    /// </summary>
    private void StartFlow()
    {
        StopFlow();
        const double travel = 78;   // the arrow cell is 110 wide, less its margins and the dot
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(6.2) };
        var glow = new[] { Box1Glow, Box2Glow, Box3Glow };
        var dots = new[] { Dot1, Dot2 };
        var lines = new[] { Line1, Line2 };
        var tr = new[] { (TranslateTransform)Dot1.RenderTransform, (TranslateTransform)Dot2.RenderTransform };

        // glow of each box: on while the signal is there
        double[] on = { 0.3, 2.3, 4.3 };
        for (int i = 0; i < 3; i++)
            sb.Children.Add(Keys(glow[i], "Opacity",
                (0, 0), (on[i], 0), (on[i] + 0.35, 1), (on[i] + 1.5, 1), (on[i] + 2.0, 0), (6.2, 0)));

        // the signal between the boxes: dot and the line it draws
        double[] go = { 1.1, 3.1 };
        for (int i = 0; i < 2; i++)
        {
            sb.Children.Add(Keys(dots[i], "Opacity", (0, 0), (go[i], 0), (go[i] + 0.1, 1), (go[i] + 0.9, 1), (go[i] + 1.1, 0), (6.2, 0)));
            sb.Children.Add(Keys(tr[i], "X", (0, 0), (go[i], 0), (go[i] + 1.0, travel), (6.2, travel)));
            sb.Children.Add(Keys(lines[i], "Opacity", (0, 0), (go[i], 0), (go[i] + 0.2, 0.7), (go[i] + 1.6, 0.7), (go[i] + 2.2, 0), (6.2, 0)));
            sb.Children.Add(Keys(lines[i], "Width", (0, 0), (go[i], 0), (go[i] + 1.0, travel), (6.2, travel)));
        }
        _flow = sb;
        sb.Begin(this, true);

        // entrance, once: boxes slide up and fade in one after the other
        var cols = new UIElement[] { Box1, Box2, Box3 };
        for (int i = 0; i < 3; i++)
        {
            var el = cols[i];
            el.Opacity = 0;
            el.RenderTransform = new TranslateTransform(0, 14);
            var delay = TimeSpan.FromSeconds(0.12 + i * 0.18);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45)) { BeginTime = delay };
            var rise = new DoubleAnimation(14, 0, TimeSpan.FromSeconds(0.45)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            el.BeginAnimation(OpacityProperty, fade);
            ((TranslateTransform)el.RenderTransform).BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    private void StopFlow()
    {
        _flow?.Stop(this);
        _flow = null;
    }
}
