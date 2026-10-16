using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.App;

public partial class MainWindow : Window
{
    private readonly AppController _c;
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _waitingShown;
    private bool _closeAsked;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public bool ReallyClose { get; set; }

    public MainWindow(AppController c)
    {
        InitializeComponent();
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(App.MakeIcon().Handle, Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        _c = c;
        Controls.OverlayWindow.Host = OverlayHost;
        // Dark title bar to match the theme (Windows 10 build 18985+ / Windows 11).
        SourceInitialized += (_, _) =>
        {
            int on = 1;
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };
        SticksPage.Attach(c);
        BindingsPage.Attach(c);
        SettingsPage.Attach(c);
        Onboarding.Attach(c);
        Onboarding.Completed += (last, made) =>
        {
            Shell.IsEnabled = true;
            SticksPage.AfterOnboarding(last, made);
            ShowPage("sticks");
        };
        // Only while there is no profile at all: this comes before everything, behind it nothing can be clicked
        // or tabbed to. With at least one profile the app starts normally; a stick without a profile is pointed
        // out on the Sticks page (token and glowing Profiles button).
        bool firstRun = !c.StickProfiles.Any();
        if (firstRun)
        {
            Shell.IsEnabled = false;
            Onboarding.Begin();
        }
        BindingsPage.DeployDueChanged += due => NavBindings.Content = NavLabel("02  BINDINGS", due);
        c.Changed += () => Dispatcher.BeginInvoke(UpdateStatus);
        c.RuleFired += f => Dispatcher.BeginInvoke(() => StatusLast.Text = $"{f.StickName} {Controls.InputNames.Pretty(f.Input)}: {f.Description}");
        _status.Tick += (_, _) => UpdateStatus();
        _status.Start();
        UpdateStatus();
    }

    /// <summary>Navigation label; with <paramref name="alarm"/> a red dot says "something has to be deployed".</summary>
    private static object NavLabel(string text, bool alarm, string tip = "Changes are waiting to be deployed to Star Citizen")
    {
        if (!alarm) return text;
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(new System.Windows.Controls.TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = (System.Windows.Media.Brush)Application.Current.FindResource("BadBrush"),
            ToolTip = tip,
        });
        return row;
    }

    private void UpdateStatus()
    {
        var slots = _c.ProfileSlots();
        int waiting = _c.UnassignedDevices.Count;
        int n = slots.Count(s => s.IsConnected) + waiting;
        int inactive = slots.Count(s => !s.IsConnected);
        StatusSticks.Text = $"{n} stick{(n == 1 ? "" : "s")} connected" +
                            (waiting > 0 ? $" ({waiting} without profile)" : "") +
                            (inactive > 0 ? $", {inactive} inactive" : "");
        // A red dot on STICKS while a connected stick still needs its profile.
        if (waiting != _waitingShown)
        {
            _waitingShown = waiting;
            NavSticks.Content = NavLabel("01  STICKS", waiting > 0, "A stick is waiting for its profile (Sticks page)");
        }
        if (string.IsNullOrEmpty(StatusLast.Text)) StatusLast.Text = "No keyboard rule fired yet";
        StatusGame.Text = ScLocator.IsGameRunning() ? "Star Citizen running"
            : _c.Install != null ? $"Star Citizen {_c.Install.ReadVersion() ?? ""} ({_c.Install.Channel})" : "Star Citizen not found";
    }

    private void Nav_Checked(object s, RoutedEventArgs e)
    {
        if (SticksPage == null) return;
        SticksPage.Visibility = NavSticks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BindingsPage.Visibility = NavBindings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AntiAfkPage.Visibility = NavAntiAfk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SelectStick(int index) => SticksPage.SelectIndex(index);

    /// <summary>The Sticks page is where a stick without a profile is handled (the wizard).</summary>
    public void OpenProfiles() => ShowPage("sticks");
    public void SelectInput(string input) => SticksPage.SelectInput(input);
    public void SelectAction(string key) => BindingsPage.SelectAction(key);

    private void Kartell_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://das-kartell.org") { UseShellExecute = true }); }
        catch { /* no browser registered: nothing to do */ }
    }

    public void ShowPage(string name)
    {
        (name switch { "bindings" => NavBindings, "settings" => NavSettings, "antiafk" => NavAntiAfk, _ => NavSticks }).IsChecked = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ReallyClose) { base.OnClosing(e); return; }

        var settings = _c.Store.Data.Settings;
        var action = settings.OnClose;
        if (action == CloseAction.Ask)
        {
            if (_closeAsked) { e.Cancel = true; return; }   // a second X while the question is open
            _closeAsked = true;
            (CloseAction Action, bool Remember)? answer;
            try { answer = Controls.CloseDialog.Ask(this); }
            finally { _closeAsked = false; }
            if (answer == null) { e.Cancel = true; return; }
            action = answer.Value.Action;
            if (answer.Value.Remember)
            {
                settings.OnClose = action;
                _c.Store.Save();
                _c.Touch();
            }
        }

        e.Cancel = true;
        if (action == CloseAction.Exit)
            Dispatcher.BeginInvoke(() => ((App)Application.Current).Quit());
        else
            Hide();
    }
}
