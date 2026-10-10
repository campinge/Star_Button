using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.App.Views;

public sealed record ProfileRow(Setup Setup, string Name, string Sub, bool Active)
{
    public Visibility ActiveVisibility => Active ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record EntryRow(string Input, string What);
public sealed record ScFileRow(string Title, string Sub, string Kind, string Path);

/// <summary>Manages profiles (setups) and shows the Star Citizen files they can be read from.</summary>
public partial class ProfilesView : UserControl
{
    private AppController? _c;
    private bool _loading;
    private Guid? _selected;

    public ProfilesView() => InitializeComponent();

    public void Attach(AppController c)
    {
        _c = c;
        c.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        IsVisibleChanged += (_, _) => { if (IsVisible) RebuildFiles(); };
        Rebuild();
    }

    private Setup? Selected => _c?.Setups.FirstOrDefault(p => p.Id == _selected);

    private static string Summary(Setup p, AppController c)
    {
        var sticks = p.StickLayouts.Count(kv => c.Store.Profile(kv.Value) is { Binds.Count: > 0 } or { Rules.Count: > 0 });
        return $"{p.Keyboard.Binds.Count} keyboard/mouse · {p.Gamepad.Binds.Count} gamepad · {sticks} stick(s) configured";
    }

    private void Rebuild()
    {
        if (_c == null) return;
        _loading = true;
        var active = _c.ActiveSetup;
        var rows = _c.Setups.Select(p => new ProfileRow(p, p.Name, Summary(p, _c), ReferenceEquals(p, active))).ToList();
        ProfileList.ItemsSource = rows;
        ProfileList.SelectedItem = rows.FirstOrDefault(r => r.Setup.Id == _selected)
                                   ?? rows.FirstOrDefault(r => r.Active) ?? rows.FirstOrDefault();
        _selected = (ProfileList.SelectedItem as ProfileRow)?.Setup.Id;
        _loading = false;
        ShowDetail();
        RebuildFiles();
    }

    private void ShowDetail()
    {
        var p = Selected;
        NoProfile.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        ProfileDetail.Visibility = p != null ? Visibility.Visible : Visibility.Collapsed;
        if (p == null || _c == null) return;
        _loading = true;
        if (PName.Text != p.Name) PName.Text = p.Name;
        ActivateBtn.IsEnabled = !ReferenceEquals(p, _c.ActiveSetup);
        ActivateBtn.Content = ActivateBtn.IsEnabled ? "Make active" : "Active profile";

        var rows = new List<EntryRow>
        {
            new("Keyboard & Mouse", $"{p.Keyboard.Binds.Count} binding(s)" + Names(p.Keyboard, _c)),
            new("Gamepad", $"{p.Gamepad.Binds.Count} binding(s)" + Names(p.Gamepad, _c)),
        };
        foreach (var st in _c.Store.Data.Sticks)
        {
            if (!p.StickLayouts.TryGetValue(st.Id, out var lid) || _c.Store.Profile(lid) is not { } layout) continue;
            if (layout.Binds.Count == 0 && layout.Rules.Count == 0) continue;
            rows.Add(new EntryRow(st.Name, $"{layout.Binds.Count} binding(s), {layout.Rules.Count} keyboard rule(s)"));
        }
        Entries.ItemsSource = rows;
        _loading = false;
    }

    private static string Names(ControllerProfile layer, AppController c)
    {
        var labels = layer.Binds.Select(b => c.Catalog?.Find(b.Map, b.Action)?.Label ?? b.Action).Distinct().Take(4).ToList();
        return labels.Count == 0 ? "" : " · " + string.Join(", ", labels) + (layer.Binds.Count > 4 ? ", …" : "");
    }

    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _selected = (ProfileList.SelectedItem as ProfileRow)?.Setup.Id;
        ShowDetail();
    }

    private void PName_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_loading || Selected is not { } p || _c == null) return;
        if (string.IsNullOrWhiteSpace(PName.Text)) return;
        p.Name = PName.Text;
        _c.Store.Save();
    }

    private void Activate_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || Selected is not { } p) return;
        _c.ActivateSetup(p);
    }

    private void New_Click(object s, RoutedEventArgs e)
    {
        var name = Controls.InputDialog.Ask(Window.GetWindow(this), "New profile", "Name of the new (empty) profile:", "New profile");
        if (name != null && _c != null) _selected = _c.NewSetup(name).Id;
    }

    private void Duplicate_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || Selected is not { } p) return;
        _selected = _c.DuplicateSetup(p).Id;
        _c.Touch();
    }

    private void Delete_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || Selected is not { } p) return;
        if (_c.Setups.Count <= 1)
        {
            MessageBox.Show("The last profile cannot be deleted.", "Delete profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Delete profile “{p.Name}”? Star Citizen's files are not touched.", "Delete profile",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _selected = null;
        _c.DeleteSetup(p);
    }

    private void Export_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || Selected is not { } p) return;
        var dlg = new SaveFileDialog { FileName = p.Name + ".starbutton-profile.json", Filter = "Star Button profile (*.json)|*.json" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, _c.ExportSetup(p));
    }

    private void Import_Click(object s, RoutedEventArgs e)
    {
        if (_c == null) return;
        var dlg = new OpenFileDialog { Filter = "Star Button profile (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _selected = _c.ImportSetup(File.ReadAllText(dlg.FileName)).Id;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not import: " + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- SC files -----------------------------------------------------------------------------

    private void RebuildFiles()
    {
        if (_c == null) return;
        var rows = new List<ScFileRow>();
        var inst = _c.Install;
        if (inst == null)
        {
            ScNote.Text = "Star Citizen was not found. Pick it in the Deploy tab.";
            ScFiles.ItemsSource = rows;
            return;
        }
        ScNote.Text = $"{inst.Channel} · {inst.UserClientDir}";
        if (File.Exists(inst.ActionMapsPath))
            rows.Add(Describe(inst.ActionMapsPath, "Current actionmaps.xml", "In use"));
        if (Directory.Exists(inst.MappingsDir))
            foreach (var f in Directory.GetFiles(inst.MappingsDir, "*.xml").OrderBy(f => f))
                rows.Add(Describe(f, Path.GetFileNameWithoutExtension(f), "Exported layout"));
        if (Directory.Exists(_c.Store.BackupDir))
            foreach (var f in Directory.GetFiles(_c.Store.BackupDir, "actionmaps_*.xml").OrderByDescending(f => f))
                rows.Add(Describe(f, Path.GetFileNameWithoutExtension(f), "Backup"));
        var cur = (ScFiles.SelectedItem as ScFileRow)?.Path;
        ScFiles.ItemsSource = rows;
        ScFiles.SelectedItem = rows.FirstOrDefault(r => r.Path == cur) ?? rows.FirstOrDefault();
    }

    private static ScFileRow Describe(string path, string title, string kind)
    {
        string sub;
        try
        {
            var rb = ActionMapsFile.Load(path).Rebinds().Where(r => !r.IsUnbound).ToList();
            sub = $"{rb.Count} binding(s) · " + File.GetLastWriteTime(path).ToString("g");
        }
        catch { sub = "Unreadable"; }
        return new ScFileRow(title, sub, kind, path);
    }

    private void ImportSc_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || ScFiles.SelectedItem is not ScFileRow row) { ScResult.Text = "Select a file first."; return; }
        try
        {
            var (setup, imported, skipped) = _c.ReadFromStarCitizen(ActionMapsFile.Load(row.Path),
                $"{row.Title} · {DateTime.Now:dd.MM. HH:mm}");
            _selected = setup.Id;
            ScResult.Text = $"Created profile “{setup.Name}” with {imported} binding(s)" +
                            (skipped > 0 ? $"; {skipped} skipped (no matching stick)." : ".");
        }
        catch (Exception ex)
        {
            ScResult.Text = "Could not read that file: " + ex.Message;
        }
    }

    private void Restore_Click(object s, RoutedEventArgs e)
    {
        if (_c?.Install == null || ScFiles.SelectedItem is not ScFileRow row) return;
        if (row.Path == _c.Install.ActionMapsPath) { ScResult.Text = "That is already the active file."; return; }
        if (ScLocator.IsGameRunning() && MessageBox.Show(
                "Star Citizen is running and may overwrite the file when it exits. Continue anyway?",
                "Game running", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Directory.CreateDirectory(_c.Store.BackupDir);
        var backup = Path.Combine(_c.Store.BackupDir, $"actionmaps_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
        if (File.Exists(_c.Install.ActionMapsPath)) File.Copy(_c.Install.ActionMapsPath, backup);
        Directory.CreateDirectory(Path.GetDirectoryName(_c.Install.ActionMapsPath)!);
        File.Copy(row.Path, _c.Install.ActionMapsPath, true);
        ScResult.Text = $"Copied '{row.Title}' to actionmaps.xml. Previous file saved as {Path.GetFileName(backup)}.";
        RebuildFiles();
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (ScFiles.SelectedItem is ScFileRow row)
            Process.Start("explorer.exe", $"/select,\"{row.Path}\"");
    }
}
