using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScMapper.App.Controls;

/// <summary>Minimal themed single-line text prompt.</summary>
public static class InputDialog
{
    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 14), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Style = (Style)Application.Current.FindResource("Accent") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            ShowInTaskbar = false,
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); win.DialogResult = true; };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        return win.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }
}
