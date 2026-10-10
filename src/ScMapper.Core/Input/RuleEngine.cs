using System.Collections.Concurrent;
using System.Threading.Channels;
using ScMapper.Core.Model;
using ScMapper.Core.Storage;

namespace ScMapper.Core.Input;

public sealed record FiredRule(string StickName, string Input, string Description, DateTime Utc);

/// <summary>
/// Turns physical input edges into keyboard output according to the profile assigned to the stick.
/// Output happens on a single worker so the polling thread never blocks.
/// </summary>
public sealed class RuleEngine : IDisposable
{
    private readonly AppStore _store;
    private readonly IKeyOutput _output;
    private readonly Func<Guid, StickRecord?> _stickForDevice;
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>();
    private readonly ConcurrentDictionary<(Guid, string), IReadOnlyList<KeyStroke>> _held = new();
    private readonly Task _worker;

    public bool Enabled { get; set; } = true;
    public int TapMilliseconds { get; set; } = 35;
    public event Action<FiredRule>? Fired;

    public RuleEngine(AppStore store, IKeyOutput output, Func<Guid, StickRecord?> stickForDevice)
    {
        _store = store;
        _output = output;
        _stickForDevice = stickForDevice;
        _worker = Task.Run(async () =>
        {
            await foreach (var work in _queue.Reader.ReadAllAsync())
            {
                try { work(); } catch { /* output must never kill the worker */ }
            }
        });
    }

    public void Handle(InputEvent e)
    {
        if (!Enabled) return;
        var stick = _stickForDevice(e.InstanceGuid);
        if (stick is null) return;
        var prof = _store.Profile(stick.ProfileId);
        if (prof is null || !prof.Rules.TryGetValue(e.Input, out var rule)) return;
        var held = _held;
        var id = (stick.Id, e.Input);

        if (e.Pressed)
        {
            if (rule.OnPress is { IsEmpty: false } p && KeyCombo.TryParse(p.Keys, out var keys, out _))
            {
                if (p.Hold)
                {
                    held[id] = keys;
                    _queue.Writer.TryWrite(() => _output.Down(keys));
                }
                else Tap(keys);
                Fired?.Invoke(new FiredRule(stick.Name, e.Input, $"press → {p.Keys}{(p.Hold ? " (hold)" : "")}", e.Utc));
            }
        }
        else
        {
            if (held.TryRemove(id, out var heldKeys))
                _queue.Writer.TryWrite(() => _output.Up(heldKeys));
            if (rule.OnRelease is { IsEmpty: false } r && KeyCombo.TryParse(r.Keys, out var keys, out _))
            {
                Tap(keys);
                Fired?.Invoke(new FiredRule(stick.Name, e.Input, $"release → {r.Keys}", e.Utc));
            }
        }
    }

    private void Tap(IReadOnlyList<KeyStroke> keys)
    {
        _queue.Writer.TryWrite(() =>
        {
            _output.Down(keys);
            Thread.Sleep(TapMilliseconds);
            _output.Up(keys);
        });
    }

    /// <summary>Releases anything still held (used when disabling output or shutting down).</summary>
    public void ReleaseAll()
    {
        foreach (var kv in _held.ToArray())
            if (_held.TryRemove(kv.Key, out var keys)) _queue.Writer.TryWrite(() => _output.Up(keys));
    }

    public void Dispose()
    {
        ReleaseAll();
        _queue.Writer.TryComplete();
        _worker.Wait(500);
    }
}
