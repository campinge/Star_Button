using System.Collections.Concurrent;
using System.Threading.Channels;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;
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
    // Keyed by the physical device (not the stick record): a device can vanish or change owner while held.
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
        // Letting go never depends on what the profile says now: the rule may have been edited or removed, the
        // profile switched or the stick unmatched while the button was down. Whatever this button holds goes up.
        if (!e.Pressed) ReleaseInput(e.InstanceGuid, e.Input);
        if (_suspended) return;
        var stick = _stickForDevice(e.InstanceGuid);
        if (stick is null) return;
        var prof = _store.Profile(stick.ProfileId);
        if (prof is null) return;
        HandleActions(stick, prof, e);
        ButtonRule? rule;
        try { if (!prof.Rules.TryGetValue(e.Input, out rule)) return; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException) { return; }   // edited right now
        var held = _held;
        var id = (e.InstanceGuid, e.Input);

        if (e.Pressed)
        {
            if (rule.OnPress is { IsEmpty: false } p && KeyCombo.TryParse(p.Keys, out var keys, out _))
            {
                if (Blocked(p.Keys, prof, e)) return;
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
                if (Blocked(r.Keys, prof, e)) return;
                Tap(keys);
                Fired?.Invoke(new FiredRule(prof.Name, e.Input, $"release → {r.Keys}", e.Utc));
            }
        }
    }

    private void HandleActions(StickRecord stick, ControllerProfile prof, InputEvent e)
    {
        foreach (var a in ActionsFor(prof, e.Input))
        {
            var heldId = (e.InstanceGuid, $"{e.Input}#{a.Id:N}");
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
            if (Blocked(combo, prof, e)) continue;
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

    /// <summary>
    /// The actions of an input as a private copy. The UI edits the list on its own thread; a copy that races an
    /// edit is simply taken again.
    /// </summary>
    private static List<ButtonAction> ActionsFor(ControllerProfile prof, string input)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { return prof.Actions.Where(a => a != null && a.Input == input).ToList(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException) { }
        }
        return new List<ButtonAction>();
    }

    /// <summary>Lets go of everything the given input of a device holds (rule keys and "while held" actions).</summary>
    private void ReleaseInput(Guid device, string input)
    {
        string prefix = input + "#";
        foreach (var kv in _held.ToArray())
        {
            if (kv.Key.Item1 != device) continue;
            if (kv.Key.Item2 != input && !kv.Key.Item2.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (_held.TryRemove(kv.Key, out var keys)) _queue.Writer.TryWrite(() => _output.Up(keys));
        }
    }

    /// <summary>The device is gone (unplugged, unmatched): its buttons can no longer report a release.</summary>
    public void ReleaseDevice(Guid device)
    {
        foreach (var kv in _held.ToArray())
            if (kv.Key.Item1 == device && _held.TryRemove(kv.Key, out var keys))
                _queue.Writer.TryWrite(() => _output.Up(keys));
    }

    /// <summary>
    /// A joystick button must never close a window or switch tasks. This is checked where keys are sent, not
    /// where they are chosen: captured bindings, imported profiles and the game's file all end up here.
    /// </summary>
    private bool Blocked(string? combo, ControllerProfile prof, InputEvent e)
    {
        var why = KeyPlanner.DangerReason(combo);
        if (why == null) return false;
        Fired?.Invoke(new FiredRule(prof.Name, e.Input, $"blocked {combo}: {why}", e.Utc));
        return true;
    }

    private int _pendingTaps;
    private const int MaxPendingTaps = 24;

    private void Tap(IReadOnlyList<KeyStroke> keys)
    {
        // Each tap takes TapMilliseconds on the single output worker. A chattering switch or a mashed button would
        // otherwise queue seconds of stale keys; beyond a handful in flight a new tap is dropped.
        if (Interlocked.Increment(ref _pendingTaps) > MaxPendingTaps)
        {
            Interlocked.Decrement(ref _pendingTaps);
            return;
        }
        _queue.Writer.TryWrite(() =>
        {
            try
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
            }
            finally { Interlocked.Decrement(ref _pendingTaps); }
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
