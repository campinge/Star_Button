using System.Windows;
using System.Windows.Controls;

namespace ScMapper.App.Controls;

/// <summary>
/// Drop-in for <see cref="MessageBox"/> that shows the message as an overlay inside the main window
/// instead of a system dialog. Same arguments, same result values.
/// </summary>
public static class Msg
{
    public static MessageBoxResult Show(string text, string caption = "Star Button",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var accent = (Style)Application.Current.FindResource("Accent");
        var result = MessageBoxResult.None;
        OverlayWindow win = null!;

        Button Make(string label, MessageBoxResult r, bool isDefault, bool isCancel)
        {
            var b = new Button
            {
                Content = label, Padding = new Thickness(18, 6, 18, 6), MinWidth = 84, Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault, IsCancel = isCancel,
            };
            if (isDefault) b.Style = accent;
            b.Click += (_, _) => { result = r; win.DialogResult = r != MessageBoxResult.Cancel && r != MessageBoxResult.No; };
            return b;
        }

        var kinds = buttons switch
        {
            MessageBoxButton.OK => new[] { ("OK", MessageBoxResult.OK) },
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) },
            _ => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) },
        };
        // Enter confirms the safe choice: what the caller named, else the first button.
        var def = defaultResult != MessageBoxResult.None && kinds.Any(k => k.Item2 == defaultResult) ? defaultResult : kinds[0].Item2;
        // Escape: Cancel if there is one, otherwise No, otherwise the only button.
        var cancel = kinds.Any(k => k.Item2 == MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
            : kinds.Any(k => k.Item2 == MessageBoxResult.No) ? MessageBoxResult.No : kinds[0].Item2;

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (label, r) in kinds)
            row.Children.Add(Make(label, r, r == def, r == cancel));

        var panel = new StackPanel { Margin = new Thickness(22), MinWidth = 380, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Foreground = icon is MessageBoxImage.Warning or MessageBoxImage.Error
                ? (System.Windows.Media.Brush)Application.Current.FindResource(icon == MessageBoxImage.Error ? "BadBrush" : "WarnBrush")
                : (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"),
        });
        panel.Children.Add(row);

        win = new OverlayWindow { Content = panel };
        win.ShowDialog();
        // Closed without a button (main window going away): the safe answer.
        return result == MessageBoxResult.None ? cancel : result;
    }

    /// <summary>Overload that took an owner window; the overlay always sits in the main window.</summary>
    public static MessageBoxResult Show(Window? owner, string text, string caption = "Star Button",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(text, caption, buttons, icon, defaultResult);
}
