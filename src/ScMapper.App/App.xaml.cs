using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
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
        // A custom --data folder (tests, screenshots) gets its own instance lock.
        int dataIdx = e.Args.ToList().IndexOf("--data");
        var lockName = dataIdx >= 0 && dataIdx + 1 < e.Args.Length
            ? "StarButton.SingleInstance." + Math.Abs(e.Args[dataIdx + 1].ToLowerInvariant().GetHashCode())
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
        if (!args.Contains("--minimized")) _window.Show();
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
        _tray?.Dispose();
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
