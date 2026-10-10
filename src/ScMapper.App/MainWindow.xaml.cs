using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Sc;

namespace ScMapper.App;

public partial class MainWindow : Window
{
    private readonly AppController _c;
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };

    public bool ReallyClose { get; set; }

    public MainWindow(AppController c)
    {
        InitializeComponent();
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(App.MakeIcon().Handle, Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        _c = c;
        SticksPage.Attach(c);
        BindingsPage.Attach(c);
        ProfilesPage.Attach(c);
        DeployPage.Attach(c);
        c.Changed += () => Dispatcher.BeginInvoke(UpdateStatus);
        c.RuleFired += f => Dispatcher.BeginInvoke(() => StatusLast.Text = $"{f.StickName} {Controls.InputNames.Pretty(f.Input)}: {f.Description}");
        _status.Tick += (_, _) => UpdateStatus();
        _status.Start();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int n = _c.Match.Matched.Count;
        StatusSticks.Text = $"{n} stick{(n == 1 ? "" : "s")} connected" +
                            (_c.Match.Unmatched.Count > 0 ? $", {_c.Match.Unmatched.Count} new" : "") +
                            (_c.Match.Missing.Count > 0 ? $", {_c.Match.Missing.Count} missing" : "");
        if (string.IsNullOrEmpty(StatusLast.Text)) StatusLast.Text = "No keyboard rule fired yet";
        StatusGame.Text = ScLocator.IsGameRunning() ? "Star Citizen running"
            : _c.Install != null ? $"Star Citizen {_c.Install.ReadVersion() ?? ""} ({_c.Install.Channel})" : "Star Citizen not found";
        OutputToggle.IsChecked = _c.Engine.Enabled;
    }

    private void Nav_Checked(object s, RoutedEventArgs e)
    {
        if (SticksPage == null) return;
        SticksPage.Visibility = NavSticks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BindingsPage.Visibility = NavBindings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = NavProfiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DeployPage.Visibility = NavDeploy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OutputToggle_Click(object s, RoutedEventArgs e) => _c.SetOutputEnabled(OutputToggle.IsChecked == true);

    public void SelectStick(int index) => SticksPage.SelectIndex(index);
    public void SelectInput(string input) => SticksPage.SelectInput(input);
    public void SelectAction(string key) => BindingsPage.SelectAction(key);

    public void ShowPage(string name)
    {
        (name switch { "bindings" => NavBindings, "profiles" => NavProfiles, "deploy" => NavDeploy, _ => NavSticks }).IsChecked = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ReallyClose && _c.Store.Data.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
