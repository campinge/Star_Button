using System.Diagnostics;
using System.Runtime.InteropServices;
using ScMapper.Core.Input;

namespace ScMapper.Platform;

/// <summary>
/// Keeps the character walking in a circle: at random intervals it taps W, A, S, D one after the other.
/// The keys are sent as hardware scan codes, which Windows only delivers to the window in front, so a tap is
/// only made while Star Citizen has the focus (otherwise it is skipped and the same key stays next).
/// </summary>
public sealed class AntiAfk : IDisposable
{
    public const string Keys = "WASD";

    // W, A, S, D scan codes.
    private static readonly ushort[] Sequence = { 0x11, 0x1E, 0x1F, 0x20 };

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

    private readonly IKeyOutput _keys;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;

    public AntiAfk(IKeyOutput keys) => _keys = keys;

    public bool IsRunning => _cts != null;

    /// <summary>Time between two taps is drawn at random from this range.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a key is held per tap.</summary>
    public TimeSpan HoldTime { get; set; } = TimeSpan.FromMilliseconds(120);

    // ---- state for the display (written by the worker, read by the UI) --------------------------

    /// <summary>Index into <see cref="Keys"/> of the key that comes next.</summary>
    public volatile int NextIndex;
    /// <summary>Index of the key held right now, or -1.</summary>
    public volatile int HoldingIndex = -1;
    public DateTime NextAt { get; private set; }
    public TimeSpan Wait { get; private set; }
    public int LastIndex { get; private set; } = -1;
    public DateTime LastAt { get; private set; }
    public int Taps { get; private set; }
    public int Skipped { get; private set; }
    public bool GameRunning { get; private set; } = true;
    public bool GameInFront { get; private set; } = true;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Taps = Skipped = 0;
        LastIndex = -1;
        _ = Task.Run(() => Loop(token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        HoldingIndex = -1;
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var lo = MinInterval.TotalMilliseconds;
                var hi = Math.Max(lo, MaxInterval.TotalMilliseconds);
                Wait = TimeSpan.FromMilliseconds(lo + _rng.NextDouble() * (hi - lo));
                NextAt = DateTime.UtcNow + Wait;

                // Wait in short slices so the game state on the page stays current.
                while (DateTime.UtcNow < NextAt)
                {
                    Probe();
                    await Task.Delay(250, ct);
                }

                Probe();
                if (!GameInFront)
                {
                    Skipped++;
                    continue;
                }

                int index = NextIndex;
                var stroke = new[] { new KeyStroke(Sequence[index], false, false) };
                HoldingIndex = index;
                _keys.Down(stroke);
                try { await Task.Delay(HoldTime, ct); }
                finally
                {
                    _keys.Up(stroke);   // always let go again, also when stopped in the middle of a tap
                    HoldingIndex = -1;
                }

                LastIndex = index;
                LastAt = DateTime.Now;
                Taps++;
                NextIndex = (index + 1) % Sequence.Length;
            }
        }
        catch (OperationCanceledException) { }
        finally { HoldingIndex = -1; }
    }

    /// <summary>Is Star Citizen running, and is it the window in front?</summary>
    private void Probe()
    {
        var games = Process.GetProcessesByName("StarCitizen");
        try
        {
            GameRunning = games.Length > 0;
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            GameInFront = games.Any(g => g.Id == pid);
        }
        finally { foreach (var g in games) g.Dispose(); }
    }
}
