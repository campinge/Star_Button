using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Sc;

namespace ScMapper.App.Controls;

/// <summary>
/// The screen shown while a deploy runs and after it: "Deploying..." first, then either what was written
/// (checked by reading the file back) or why it failed.
/// </summary>
public static class DeployProgressDialog
{
    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Null when the deploy failed (the dialog has shown the reason).</summary>
    public static DeployResult? Run(AppController c)
    {
        DeployResult? result = null;

        var icon = new TextBlock { FontSize = 30, Text = "⇪", Foreground = B("AccentBrush"), Margin = new Thickness(0, 0, 14, 0) };
        var title = new TextBlock { Text = "Deploying to Star Citizen…", FontSize = 18, FontWeight = FontWeights.SemiBold };
        var detail = new TextBlock
        {
            Text = "Backing up actionmaps.xml and writing your bindings.",
            Foreground = B("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button
        {
            Content = "Close", IsCancel = true, IsDefault = true, IsEnabled = false, Visibility = Visibility.Collapsed,
            Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var text = new StackPanel { Width = 520 };
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(bar);
        text.Children.Add(close);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24) };
        row.Children.Add(icon);
        row.Children.Add(text);

        void Finish(string head, string sub, string glyph, string brush)
        {
            icon.Text = glyph;
            icon.Foreground = B(brush);
            title.Text = head;
            detail.Text = sub;
            bar.Visibility = Visibility.Collapsed;
            close.Visibility = Visibility.Visible;
            close.IsEnabled = true;
            close.Focus();
        }

        var win = new OverlayWindow { Content = row };
        // The work starts once the "Deploying" screen has been drawn.
        win.Loaded += (_, _) => win.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                var res = c.Deploy(false);
                var missing = res.Result != null && c.Install != null
                    ? Deployer.Verify(res.Result, c.Install.ActionMapsPath)
                    : Array.Empty<string>();
                result = res;

                var lines = new List<string> { $"{res.Added} bindings written, {res.Removed} replaced." };
                if (res.BackupPath != null) lines.Add("Backup: " + Path.GetFileName(res.BackupPath));
                if (res.Warnings.Count > 0)
                    lines.Add("Warnings:\n• " + string.Join("\n• ", res.Warnings.Take(5)));

                if (missing.Count == 0)
                {
                    lines.Add("Checked: the file on disk holds every binding.");
                    lines.Add("Start Star Citizen to use them. Keep the game closed while you deploy.");
                    Finish("Deploy succeeded", string.Join("\n", lines), "✓", "GoodBrush");
                }
                else
                {
                    lines.Add($"But {missing.Count} binding(s) are missing from the file:\n• " +
                              string.Join("\n• ", missing.Take(5)));
                    Finish("Deployed, but the check found a problem", string.Join("\n", lines), "⚠", "WarnBrush");
                }
            }
            catch (Exception ex)
            {
                Finish("Deploy failed", ex.Message, "✗", "BadBrush");
            }
        });
        win.ShowDialog();
        return result;
    }
}
