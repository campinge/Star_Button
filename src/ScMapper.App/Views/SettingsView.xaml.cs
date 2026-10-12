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
    private bool _stale;

    public SettingsView() => InitializeComponent();

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) Rebuild();
            else _stale = true;
        });
        // Look for game folders again whenever the page is opened.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _c.DiscoverInstall();
            if (_stale) { _stale = false; Rebuild(); }
        };
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
            Controls.Msg.Show("No Data.p4k found in a LIVE/PTU/... subfolder of that folder.", "Not found",
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
            Controls.Msg.Show("Could not change the Windows startup entry: " + ex.Message, "Settings",
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
        return BackupFiles.StampOf(path) is { } t ? t.ToString("g") : Path.GetFileNameWithoutExtension(path);
    }

    private static readonly Dictionary<(string, long, DateTime), string> Described = new();

    private static string Describe(string path)
    {
        var info = new FileInfo(path);
        var key = (path, info.Length, info.LastWriteTimeUtc);
        if (Described.TryGetValue(key, out var cached)) return cached;
        string text;
        try { text = $"{ActionMapsFile.Load(path).Rebinds().Count(r => !r.IsUnbound)} bindings"; }
        catch { text = "unreadable"; }
        Described[key] = text;
        return text;
    }

    private void Restore_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null || Backups.SelectedItem is not BackupRow row)
        {
            ShowBackupResult("Select a backup first.");
            return;
        }
        if (ScLocator.IsGameRunning(fresh: true))
        {
            Controls.Msg.Show(AppController.GameRunningMessage + "\n\nClose Star Citizen, then try again.", "Game running",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            // Only a file that parses is put in place of the game's file.
            ActionMapsFile.Load(row.Path);
        }
        catch (Exception ex)
        {
            ShowBackupResult($"That backup cannot be read ({ex.Message.Split('\n')[0]}), so it was not restored.");
            return;
        }
        try
        {
            var target = _c.Install.ActionMapsPath;
            if (File.Exists(target)) File.Copy(target, BackupFiles.NewPath(_c.Store.BackupDir));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".restore.tmp";
            File.Copy(row.Path, tmp, true);
            File.Move(tmp, target, overwrite: true);
            BackupFiles.Prune(_c.Store.BackupDir);
        }
        catch (Exception ex)
        {
            ShowBackupResult("Could not restore the backup: " + ex.Message);
            return;
        }
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
