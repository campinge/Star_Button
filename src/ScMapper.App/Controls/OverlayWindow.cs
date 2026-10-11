using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ScMapper.App.Controls;

/// <summary>
/// A modal dialog that lives inside the main window: a dimmed layer with a card in the middle. It offers the few
/// members of <see cref="Window"/> the dialogs used (Title, Content, DialogResult, Close, ShowDialog, KeyDown,
/// Loaded, Closed) so a dialog is built the same way as before - only it no longer opens a window of its own.
/// ShowDialog blocks like the window version did (nested dispatcher frame), so callers keep their simple flow.
/// </summary>
public sealed class OverlayWindow
{
    /// <summary>The grid in the main window that holds the layers. Set by MainWindow.</summary>
    public static Grid? Host { get; set; }

    private readonly Grid _layer = new();
    private DispatcherFrame? _frame;
    private IInputElement? _previousFocus;
    private bool _closed;
    private bool? _result;
    private Window? _fallback;

    public string Title { get; set; } = "";
    public object? Content { get; set; }
    public Dispatcher Dispatcher => Application.Current.Dispatcher;

    public event KeyEventHandler? KeyDown;
    public event RoutedEventHandler? Loaded;
    public event EventHandler? Closed;

    public bool? DialogResult
    {
        get => _result;
        set
        {
            _result = value;
            Close();
        }
    }

    public bool? ShowDialog()
    {
        if (Host == null || Window.GetWindow(Host) is not { IsVisible: true } main)
            return ShowAsWindow();

        _previousFocus = Keyboard.FocusedElement;
        var card = BuildCard();
        _layer.Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x02, 0x05, 0x08));
        _layer.Margin = new Thickness(0);
        _layer.Children.Add(new Border
        {
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = card,
        });
        KeyboardNavigation.SetTabNavigation(_layer, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(_layer, KeyboardNavigationMode.Cycle);
        _layer.Focusable = false;
        _layer.PreviewKeyDown += OnPreviewKey;
        _layer.KeyDown += (_, e) =>
        {
            KeyDown?.Invoke(this, e);
            if (!e.Handled && e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
            {
                e.Handled = true;
                CancelFromKey();
            }
        };
        _layer.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyClick));
        // Whatever sits behind the card must not be clickable, scrollable or focusable.
        _layer.MouseDown += (_, e) => e.Handled = true;
        _layer.MouseWheel += (_, e) => { if (e.OriginalSource == _layer) e.Handled = true; };

        Host.Children.Add(_layer);
        main.Closed += OnMainClosed;

        _layer.UpdateLayout();
        Loaded?.Invoke(this, new RoutedEventArgs());
        // First focusable control inside the card, unless the dialog already focused one.
        _layer.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_closed) return;
            if (Keyboard.FocusedElement is DependencyObject f && IsInside(f)) return;
            _layer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });

        _frame = new DispatcherFrame();
        Dispatcher.PushFrame(_frame);

        main.Closed -= OnMainClosed;
        return _result;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (_fallback != null)
        {
            var w = _fallback;
            _fallback = null;
            w.Content = null;
            if (_result is bool r) w.DialogResult = r;
            else w.Close();
        }
        else
        {
            Host?.Children.Remove(_layer);
            // Hand the focus back to where it was.
            if (_previousFocus is UIElement prev && prev.IsVisible) prev.Focus();
        }
        if (_frame != null) _frame.Continue = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnMainClosed(object? s, EventArgs e) => Close();

    private bool IsInside(DependencyObject d)
    {
        for (DependencyObject? p = d; p != null; p = p is Visual ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p == _layer) return true;
        return false;
    }

    private UIElement BuildCard()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (!string.IsNullOrWhiteSpace(Title))
        {
            var head = new Border
            {
                Padding = new Thickness(20, 14, 20, 12),
                BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeights.SemiBold },
            };
            grid.Children.Add(head);
        }
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Content,
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        return new Border
        {
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("Border2Brush"),
            BorderThickness = new Thickness(1),
            Child = grid,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black },
        };
    }

    /// <summary>IsCancel buttons close the dialog, like they did in a Window.</summary>
    private void OnAnyClick(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { IsCancel: true }) Close();
    }

    private void OnPreviewKey(object s, KeyEventArgs e)
    {
        var focus = Keyboard.FocusedElement as DependencyObject;
        if (e.Key == Key.Enter && focus is not (ButtonBase or ComboBox or ComboBoxItem or ListBoxItem or KeyCaptureBox) && !IsInCombo(focus))
        {
            if (FindButton(_layer, b => b.IsDefault) is { } def)
            {
                // Never let the page behind the layer see this key (its own default button would fire).
                e.Handled = true;
                def.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
        }
    }

    private static bool IsInCombo(DependencyObject? d)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ComboBox) return true;
        return false;
    }

    private static Button? FindButton(DependencyObject root, Func<Button, bool> match)
    {
        if (root is Button b && b.IsEnabled && b.IsVisible && match(b)) return b;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i), match) is { } hit) return hit;
        return null;
    }

    /// <summary>Called by the layer for Escape when no dialog handler took it.</summary>
    internal void CancelFromKey()
    {
        if (FindButton(_layer, b => b.IsCancel) is { } c) c.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    // ---- no main window to put the layer into (hidden in the tray, not yet shown): a plain window ----------
    private bool? ShowAsWindow()
    {
        var w = new Window
        {
            Title = string.IsNullOrWhiteSpace(Title) ? "Star Button" : Title,
            Content = Content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            ShowInTaskbar = false,
        }.DarkTitleBar();
        _fallback = w;
        w.KeyDown += (s, e) =>
        {
            KeyDown?.Invoke(this, e);
            if (!e.Handled && e.Key == Key.Escape) w.Close();
        };
        w.Loaded += (_, _) => Loaded?.Invoke(this, new RoutedEventArgs());
        w.Closed += (_, _) =>
        {
            if (!_closed)
            {
                _closed = true;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        };
        var r = w.ShowDialog();
        return _result ?? r;
    }
}
