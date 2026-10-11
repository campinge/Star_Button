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

    /// <summary>Resolves the key combo (app notation) of a game action, "map", "action".</summary>
    public Func<string, string, string?>? ActionKey { get; set; }

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

    private volatile bool _suspended;

    /// <summary>
    /// While true no key is sent (the binding mode wants to see what a button would send, not send it into
    /// whatever window has the focus). Turning it on lets go of everything that is held.
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value) ReleaseAll();
        }
    }

    public void Handle(InputEvent e)
    {
        if (_suspended) return;
        var stick = _stickForDevice(e.InstanceGuid);
        if (stick is null) return;
        var prof = _store.Profile(stick.ProfileId);
        if (prof is null) return;
        HandleActions(stick, prof, e);
        if (!prof.Rules.TryGetValue(e.Input, out var rule)) return;
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
                Fired?.Invoke(new FiredRule(prof.Name, e.Input, $"press → {p.Keys}{(p.Hold ? " (hold)" : "")}", e.Utc));
            }
        }
        else
        {
            if (held.TryRemove(id, out var heldKeys))
                _queue.Writer.TryWrite(() => _output.Up(heldKeys));
            if (rule.OnRelease is { IsEmpty: false } r && KeyCombo.TryParse(r.Keys, out var keys, out _))
            {
                Tap(keys);
                Fired?.Invoke(new FiredRule(prof.Name, e.Input, $"release → {r.Keys}", e.Utc));
            }
        }
    }

    private void HandleActions(StickRecord stick, ControllerProfile prof, InputEvent e)
    {
        List<ButtonAction> mine = prof.Actions.Where(a => a.Input == e.Input).ToList();
        foreach (var a in mine)
        {
            var heldId = (stick.Id, $"{e.Input}#{a.Id:N}");
            if (a.Trigger == ButtonTrigger.Held && !e.Pressed)
            {
                // Always let go, even if the modifier was released first.
                if (_held.TryRemove(heldId, out var heldKeys)) _queue.Writer.TryWrite(() => _output.Up(heldKeys));
                continue;
            }
            bool edge = a.Trigger switch
            {
                ButtonTrigger.Pressed or ButtonTrigger.Held => e.Pressed,
                ButtonTrigger.Released => !e.Pressed,
                _ => false,
            };
            if (!edge) continue;

            string? combo = a.Keys ?? (a.Map != null && a.Action != null ? ActionKey?.Invoke(a.Map, a.Action) : null);
            if (!KeyCombo.TryParse(combo, out var keys, out _)) continue;
            if (a.Trigger == ButtonTrigger.Held)
            {
                _held[heldId] = keys;
                _queue.Writer.TryWrite(() => _output.Down(keys));
            }
            else Tap(keys);
            var what = a.Action ?? combo;
            Fired?.Invoke(new FiredRule(prof.Name, e.Input, $"{(e.Pressed ? "press" : "release")} → {what} ({combo})", e.Utc));
        }
    }

    private void Tap(IReadOnlyList<KeyStroke> keys)
    {
        _queue.Writer.TryWrite(() =>
        {
            // A button held as a keyboard modifier ("layer" button) would turn this tap into shift+key for the
            // game: lift it for the tap and put it back afterwards.
            var layer = HeldLayerModifiers();
            var lifted = layer.Where(m => !keys.Any(k => k.Scan == m.Scan && k.Extended == m.Extended)).ToList();
            if (lifted.Count > 0) _output.Up(lifted);
            _output.Down(keys);
            Thread.Sleep(TapMilliseconds);
            _output.Up(keys);
            // Only what is still held now: the button may have been released during the tap.
            var restore = HeldLayerModifiers().Where(m => layer.Contains(m)).ToList();
            if (restore.Count > 0) _output.Down(restore);
        });
    }

    /// <summary>Keys of held actions that consist of modifiers only (a button used as shift/ctrl/alt).</summary>
    private List<KeyStroke> HeldLayerModifiers() =>
        _held.Values.Where(v => v.Count > 0 && v.All(k => k.IsModifier)).SelectMany(v => v).Distinct().ToList();

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
