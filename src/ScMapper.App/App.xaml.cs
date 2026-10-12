using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using ScMapper.Core;
using ScMapper.Core.Storage;
using ScMapper.Platform;

namespace ScMapper.App;

public partial class App : Application
{
    private Mutex? _single;
    private AppController? _controller;
    private MainWindow? _window;
    private System.Windows.Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Nothing may take the app down silently: a crash with a key held leaves that key down in Windows.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            OnFault("UI", ex.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => OnFault("background thread", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ex.SetObserved();
            OnFault("task", ex.Exception);
        };
        // A custom --data folder (tests, screenshots) gets its own instance lock.
        int dataIdx = e.Args.ToList().IndexOf("--data");
        var lockName = dataIdx >= 0 && dataIdx + 1 < e.Args.Length
            ? "StarButton.SingleInstance." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(e.Args[dataIdx + 1]).ToLowerInvariant())))[..16]
            : "StarButton.SingleInstance";
        _single = new Mutex(true, lockName, out bool first);
        if (!first)
        {
            MessageBox.Show("Star Button is already running (check the tray).", "Star Button");
            Shutdown();
            return;
        }

        // Test hook: --data <dir> keeps screenshots/tests away from the real settings.
        var args = e.Args.ToList();
        int di = args.IndexOf("--data");
        var store = new AppStore(di >= 0 && di + 1 < args.Count ? args[di + 1] : null);
        ThemeManager.Apply(store.Data.Settings.Theme);
        _controller = new AppController(store, new DirectInputHub(), new SendInputKeyboard());
        _controller.Start();

        _window = new MainWindow(_controller);
        MakeTray();
        int pi = args.IndexOf("--page");
        if (pi >= 0 && pi + 1 < args.Count) _window.ShowPage(args[pi + 1]);
        int si = args.IndexOf("--stick");
        if (si >= 0 && si + 1 < args.Count && int.TryParse(args[si + 1], out var stickIndex)) _window.SelectStick(stickIndex);
        int ii = args.IndexOf("--input");
        if (ii >= 0 && ii + 1 < args.Count) _window.SelectInput(args[ii + 1]);
        int ai = args.IndexOf("--action");
        if (ai >= 0 && ai + 1 < args.Count)
        {
            var key = args[ai + 1];
            // The catalog loads asynchronously; apply once it is there.
            void Apply() { if (_controller!.Catalog != null) { _controller.Changed -= Apply; _window!.Dispatcher.BeginInvoke(() => _window.SelectAction(key)); } }
            _controller!.Changed += Apply;
            Apply();
        }
        // A stick without a profile is not guessed at: the user is asked, before anything else, with the window up.
        // Started minimized (autostart) the tray tells instead of a window popping up in front of the game.
        _controller.NewDeviceSeen += d => _window.Dispatcher.BeginInvoke(() => OnNewDevice(d));
        if (!args.Contains("--minimized"))
        {
            _window.Show();   // the Sticks page shows a stick that needs a profile; nothing pops up on top
        }
        else if (_controller.UnassignedDevices.Count > 0) Announce(_controller.UnassignedDevices.Count);
    }

    private DateTime _lastFaultNotice;

    /// <summary>Logs the fault, lets go of every key the app holds and tells the user once in a while.</summary>
    private void OnFault(string where, Exception? ex)
    {
        try { _controller?.Engine.ReleaseAll(); } catch { }
        try
        {
            var dir = _controller?.Store.Dir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}\r\n\r\n");
        }
        catch { }
        if (DateTime.UtcNow - _lastFaultNotice < TimeSpan.FromMinutes(1)) return;
        _lastFaultNotice = DateTime.UtcNow;
        try
        {
            _tray?.ShowBalloonTip(6000, "Star Button hit a problem",
                (ex?.Message ?? "Unknown error") + " - details are in error.log next to your settings.",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch { }
    }

    /// <summary>A joystick appeared while running: never a dialog over the game, only a notice and the badge.</summary>
    private void OnNewDevice(ScMapper.Core.Model.DeviceInfo d)
    {
        if (_window == null || _controller == null || (_window.IsVisible && _window.IsActive)) return;   // the badge says it
        Announce(_controller.UnassignedDevices.Count, d.ProductName.Trim());
    }

    private void Announce(int count, string? model = null)
    {
        if (_tray == null) return;
        var text = count == 1 ? $"{model ?? "A stick"} has no profile yet" : $"{count} sticks have no profile yet";
        _tray.ShowBalloonTip(5000, "New joystick detected", text + ". Open Star Button to assign one.",
            System.Windows.Forms.ToolTipIcon.Info);
        _tray.BalloonTipClicked -= OnBalloonClicked;
        _tray.BalloonTipClicked += OnBalloonClicked;
    }

    private void OnBalloonClicked(object? s, EventArgs e)
    {
        ShowWindow();
        _window?.OpenProfiles();
    }

    private void MakeTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Star Button",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Open Star Button", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    internal static Icon MakeIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } own)
                return own;
        }
        catch { /* fall back to the drawn icon */ }
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(22, 68, 95));
            g.FillEllipse(bg, 1, 1, 30, 30);
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? 12 : 5, a = -Math.PI / 2 + i * Math.PI / 5;
                pts[i] = new PointF(16 + (float)(r * Math.Cos(a)), 16 + (float)(r * Math.Sin(a)));
            }
            using var star = new SolidBrush(Color.FromArgb(61, 184, 255));
            g.FillPolygon(star, pts);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowWindow()
    {
        _window!.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    internal void Quit()
    {
        _window!.ReallyClose = true;
        _tray!.Visible = false;
        _tray.Dispose();
        _controller!.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Every way out (logoff, shutdown, Quit) lets go of held keys; Dispose is safe to call twice.
        try { _controller?.Dispose(); } catch { }
        _tray?.Dispose();
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
