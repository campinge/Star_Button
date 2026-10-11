using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScMapper.Core;
using ScMapper.Core.Model;

namespace ScMapper.App.Controls;

/// <summary>
/// Housekeeping for stick profiles: rename, delete, clear out the ones nobody uses. Assigning profiles to the
/// connected sticks is the job of <see cref="ProfileAssignerDialog"/>.
/// </summary>
public static class ProfileManagerDialog
{
    public static void Show(Window? owner, AppController c)
    {
        var list = new StackPanel();
        var summary = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };
        var deleteUnused = new Button { Content = "Delete unused", Padding = new Thickness(14, 6, 14, 6) };
        var deleteAll = new Button
        {
            Content = "Delete all",
            Style = (Style)Application.Current.FindResource("Danger"),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            IsDefault = true,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };

        void Rebuild()
        {
            list.Children.Clear();
            var profiles = c.StickProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var unused = 0;
            foreach (var p in profiles)
            {
                var usage = c.ProfileUsage(p);
                if (usage.Count == 0) unused++;
                var entries = p.Binds.Count + p.Rules.Count + p.Actions.Count;

                var name = new TextBox { Text = p.Name, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
                void Commit()
                {
                    if (name.Text.Trim() == p.Name) return;
                    c.RenameProfile(p, name.Text);
                    name.Text = p.Name;
                }
                name.LostFocus += (_, _) => Commit();
                name.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    Commit();
                    Keyboard.ClearFocus();
                    e.Handled = true;
                };
                var sub = new TextBlock
                {
                    Style = (Style)Application.Current.FindResource("Muted"),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = (usage.Count == 0 ? "unused" : "used by " + string.Join(", ", usage)) +
                           $" · {entries} " + (entries == 1 ? "entry" : "entries"),
                };
                var del = new Button { Content = "Delete", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, MinHeight = 34 };
                del.Click += (_, _) =>
                {
                    var warn = usage.Count > 0 ? $"\n\nIt is used by: {string.Join(", ", usage)}." : "";
                    if (MessageBox.Show($"Delete the profile “{p.Name}”?{warn}", "Delete profile",
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                    c.DeleteProfiles(new[] { p });
                    Rebuild();
                };
                var left = new StackPanel { Children = { name, sub } };
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(del, 1);
                row.Children.Add(left);
                row.Children.Add(del);
                list.Children.Add(row);
            }
            if (profiles.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Style = (Style)Application.Current.FindResource("Muted"),
                    Text = "No stick profiles. They are created when a stick needs one.",
                });
            summary.Text = $"{profiles.Count} stick profile(s), {unused} unused. Rename a profile by editing its name.";
            deleteUnused.IsEnabled = unused > 0;
            deleteAll.IsEnabled = profiles.Count > 0;
        }

        deleteUnused.Click += (_, _) =>
        {
            var unused = c.StickProfiles.Where(p => c.ProfileUsage(p).Count == 0).ToList();
            if (unused.Count == 0) return;
            if (MessageBox.Show($"Delete {unused.Count} unused profile(s)?", "Delete unused",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            c.DeleteProfiles(unused);
            Rebuild();
        };
        deleteAll.Click += (_, _) =>
        {
            var all = c.StickProfiles.ToList();
            if (MessageBox.Show(
                    $"Delete all {all.Count} stick profile(s), including their bindings and rules?\n\n" +
                    "Sticks and profiles (setups) are kept; every stick starts with an empty layout again. " +
                    "This cannot be undone.", "Delete all profiles",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            c.DeleteProfiles(all);
            Rebuild();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(deleteUnused);
        buttons.Children.Add(deleteAll);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 560 };
        panel.Children.Add(new TextBlock { Text = "Profile manager", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(summary);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 430, Content = list });
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Profile manager",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            ShowInTaskbar = false,
        };
        close.Click += (_, _) => win.Close();
        Rebuild();
        win.ShowDialog();
    }
}
