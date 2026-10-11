using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScMapper.Core.Model;

namespace ScMapper.App.Controls;

/// <summary>Asks what the X button should do. Returns null when cancelled.</summary>
public static class CloseDialog
{
    public static (CloseAction Action, bool Remember)? Ask(Window? owner)
    {
        var accent = (Style)Application.Current.FindResource("Accent");
        var tray = new Button { Content = "Keep running in tray", Style = accent, IsDefault = true, Padding = new Thickness(16, 6, 16, 6) };
        var exit = new Button { Content = "Exit", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        var remember = new CheckBox { Content = "Don't ask me again (changeable in Settings)", Margin = new Thickness(0, 14, 0, 16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(tray);
        buttons.Children.Add(exit);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = "Close Star Button?", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Button rules only work while the app is running. It can keep running in the tray, or quit completely.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush"),
        });
        panel.Children.Add(remember);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Star Button",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            ShowInTaskbar = false,
        };
        CloseAction? choice = null;
        tray.Click += (_, _) => { choice = CloseAction.Tray; win.DialogResult = true; };
        exit.Click += (_, _) => { choice = CloseAction.Exit; win.DialogResult = true; };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
        return choice is { } c ? (c, remember.IsChecked == true) : null;
    }
}
