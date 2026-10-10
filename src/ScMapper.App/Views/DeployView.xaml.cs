using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Sc;

namespace ScMapper.App.Views;

public partial class DeployView : UserControl
{
    private AppController? _c;
    private bool _loading;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DeployView() => InitializeComponent();

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        _timer.Tick += (_, _) => RunWarn.Visibility = ScLocator.IsGameRunning() ? Visibility.Visible : Visibility.Collapsed;
        _timer.Start();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_c == null) return;
        _loading = true;
        InstallCombo.ItemsSource = _c.Installs;
        InstallCombo.SelectedItem = _c.Installs.FirstOrDefault(i => i.Root == _c.Install?.Root && i.Channel == _c.Install?.Channel);
        _loading = false;
        var inst = _c.Install;
        InstallInfo.Text = inst == null
            ? "Star Citizen was not found. Use Browse to select the folder that contains LIVE (e.g. …\\Roberts Space Industries\\StarCitizen)."
            : $"Build {inst.ReadVersion() ?? "?"} ({inst.ReadBranch() ?? "?"})\nactionmaps.xml: {inst.ActionMapsPath}" +
              (File.Exists(inst.ActionMapsPath) ? "" : "  (does not exist yet)");
        CatalogInfo.Text = "Action list: " + _c.CatalogStatus;
        OutputCheck.IsChecked = _c.Engine.Enabled;
        RunWarn.Visibility = ScLocator.IsGameRunning() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || _c == null || InstallCombo.SelectedItem is not ScInstall i) return;
        _c.SelectInstall(i);
    }

    private void Browse_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the StarCitizen folder (the one containing LIVE)",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var found = ScLocator.FindAll(dlg.SelectedPath);
        if (found.Count == 0)
        {
            MessageBox.Show("No Data.p4k found in a LIVE/PTU/... subfolder of that folder.", "Not found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _c.Store.Data.Settings.ScRoot = dlg.SelectedPath;
        _c.Store.Save();
        _c.DiscoverInstall();
        _ = _c.LoadCatalogAsync(false);
    }

    private void Rescan_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        _c.Store.Data.Settings.ScRoot = null;
        _c.DiscoverInstall();
    }

    private void Reload_Click(object s, RoutedEventArgs e) => _ = _c?.LoadCatalogAsync(true);

    private void Show(Core.Sc.DeployResult r, bool preview)
    {
        Result.Foreground = (System.Windows.Media.Brush)FindResource(r.Warnings.Count > 0 ? "WarnBrush" : "GoodBrush");
        Result.Text = (preview ? "Preview: " : "Deployed: ") +
                      $"{r.Added} binding(s) written, {r.Removed} old binding(s) replaced." +
                      (r.BackupPath != null ? $"\nBackup: {r.BackupPath}" : "") +
                      (preview ? "\nNothing was changed." : "");
        Warnings.ItemsSource = r.Warnings.Select(w => "⚠ " + w).ToList();
    }

    private void Preview_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null) return;
        try { Show(_c.Deploy(true), true); }
        catch (Exception ex) { Fail(ex); }
    }

    private void Deploy_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null) return;
        if (ScLocator.IsGameRunning() && MessageBox.Show(
                "Star Citizen is running. It may overwrite actionmaps.xml on exit, discarding these bindings.\n\nDeploy anyway?",
                "Game running", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Show(_c.Deploy(false), false); }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        Result.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");
        Result.Text = "Failed: " + ex.Message;
        Warnings.ItemsSource = null;
    }

    private void Output_Click(object s, RoutedEventArgs e) => _c?.SetOutputEnabled(OutputCheck.IsChecked == true);
}
