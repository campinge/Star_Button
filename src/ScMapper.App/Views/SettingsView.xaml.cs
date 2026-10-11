using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.App.Views;

public sealed record BackupRow(string Title, string Sub, string Path);

/// <summary>One entry of the session dropdown; <see cref="Install"/> is null for "choose folder manually".</summary>
public sealed record InstallRow(ScInstall? Install, string Title, string Sub)
{
    public override string ToString() => Title;
}

/// <summary>Star Citizen session, app options and actionmaps.xml backups.</summary>
public partial class SettingsView : UserControl
{
    private AppController? _c;
    private bool _loading;

    public SettingsView() => InitializeComponent();

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        // Look for game folders again whenever the page is opened.
        IsVisibleChanged += (_, _) => { if (IsVisible) _c.DiscoverInstall(); };
        Rebuild();
    }

    private void Rebuild()
    {
        if (_c == null) return;
        _loading = true;
        var rows = _c.Installs.Select(i => new InstallRow(i, $"{i.Channel} · {i.ReadVersion() ?? "?"}", i.Root)).ToList();
        rows.Add(new InstallRow(null, "Choose folder manually…", "Pick the folder that contains LIVE"));
        InstallCombo.ItemsSource = rows;
        InstallCombo.SelectedItem = rows.FirstOrDefault(r => r.Install != null && r.Install == _c.Install);
        _loading = false;

        var inst = _c.Install;
        SessionHint.Visibility = inst == null ? Visibility.Visible : Visibility.Collapsed;
        InstallCombo.ToolTip = inst?.ActionMapsPath;
        CatalogInfo.Text = _c.CatalogStatus;
        AutostartCheck.IsChecked = Autostart.IsEnabled;

        _loading = true;
        CloseCombo.SelectedIndex = (int)_c.Store.Data.Settings.OnClose;
        ThemeCombo.ItemsSource = ThemeManager.Themes;
        ThemeCombo.SelectedItem = ThemeManager.Find(_c.Store.Data.Settings.Theme);
        ThemeHint.Text = ThemeManager.Current.Hint;
        _loading = false;
        RebuildBackups();
    }

    // ---- session -------------------------------------------------------------------------------

    private void InstallCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || _c == null || InstallCombo.SelectedItem is not InstallRow row) return;
        if (row.Install != null) { _c.SelectInstall(row.Install); return; }
        ChooseFolder();
        // Put the dropdown back on the active session (also when the dialog was cancelled).
        Dispatcher.BeginInvoke(Rebuild);
    }

    private void ChooseFolder()
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
        var s = _c.Store.Data.Settings;
        s.ScRoot = dlg.SelectedPath;
        _c.Store.Save();
        _c.DiscoverInstall();
        _c.SelectInstall(found.FirstOrDefault(i => i.Channel.Equals(s.Channel, StringComparison.OrdinalIgnoreCase)) ?? found[0]);
    }

    private void Reload_Click(object s, RoutedEventArgs e) => _ = _c?.LoadCatalogAsync(true);

    // ---- app -----------------------------------------------------------------------------------

    private void CloseCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || _c == null || CloseCombo.SelectedIndex < 0) return;
        _c.Store.Data.Settings.OnClose = (CloseAction)CloseCombo.SelectedIndex;
        _c.Store.Save();
    }

    private void ThemeCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || _c == null || ThemeCombo.SelectedItem is not ThemeManager.ThemeInfo t) return;
        _c.Store.Data.Settings.Theme = t.Key;
        _c.Store.Save();
        ThemeManager.Apply(t.Key);
        _c.Touch();
        ThemeHint.Text = t.Hint;
    }

    private void Autostart_Click(object s, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartCheck.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change the Windows startup entry: " + ex.Message, "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AutostartCheck.IsChecked = Autostart.IsEnabled;
    }

    // ---- backups -------------------------------------------------------------------------------

    private void RebuildBackups()
    {
        if (_c == null) return;
        var rows = new List<BackupRow>();
        if (Directory.Exists(_c.Store.BackupDir))
            foreach (var f in Directory.GetFiles(_c.Store.BackupDir, "actionmaps_*.xml").OrderByDescending(f => f))
                rows.Add(new BackupRow(Title(f), Describe(f), f));
        var cur = (Backups.SelectedItem as BackupRow)?.Path;
        Backups.ItemsSource = rows;
        Backups.SelectedItem = rows.FirstOrDefault(r => r.Path == cur) ?? rows.FirstOrDefault();
        BackupCount.Text = rows.Count == 0 ? "" : rows.Count == 1 ? "1 backup" : $"{rows.Count} backups";
        NoBackups.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Backups.Visibility = BackupActions.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>"actionmaps_20261011_143005" → local date and time; falls back to the file name.</summary>
    private static string Title(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DateTime.TryParseExact(name["actionmaps_".Length..], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t.ToString("g") : name;
    }

    private static string Describe(string path)
    {
        try { return $"{ActionMapsFile.Load(path).Rebinds().Count(r => !r.IsUnbound)} bindings"; }
        catch { return "unreadable"; }
    }

    private void Restore_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null || Backups.SelectedItem is not BackupRow row)
        {
            ShowBackupResult("Select a backup first.");
            return;
        }
        if (ScLocator.IsGameRunning())
        {
            MessageBox.Show(AppController.GameRunningMessage + "\n\nClose Star Citizen, then try again.", "Game running",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Directory.CreateDirectory(_c.Store.BackupDir);
        var backup = Path.Combine(_c.Store.BackupDir, $"actionmaps_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
        if (File.Exists(_c.Install.ActionMapsPath)) File.Copy(_c.Install.ActionMapsPath, backup);
        Directory.CreateDirectory(Path.GetDirectoryName(_c.Install.ActionMapsPath)!);
        File.Copy(row.Path, _c.Install.ActionMapsPath, true);
        RebuildBackups();
        ShowBackupResult($"Restored the backup from {row.Title}. The previous file was saved as a new backup.");
    }

    private void ShowBackupResult(string text)
    {
        BackupResult.Text = text;
        BackupResult.Visibility = Visibility.Visible;
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (Backups.SelectedItem is BackupRow row)
            Process.Start("explorer.exe", $"/select,\"{row.Path}\"");
    }
}
