using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.Core;

/// <summary>What a button action should do, as chosen in the UI.</summary>
public sealed record ActionSpec(ButtonTrigger Trigger, string? Map, string? Action, string? Keys,
    string? KeyModifier = null)
{
    public bool IsGameAction => Map != null && Action != null;
}

/// <summary>One line of the inspector: a direct Star Citizen binding or an action the app performs.</summary>
public sealed class ButtonActionRow
{
    public ScBind? Bind { get; init; }
    public ButtonAction? Action { get; init; }

    /// <summary>True when the app has to run for this action (keys are sent by the app).</summary>
    public bool ViaApp => Action != null;
    public ButtonTrigger Trigger => Action?.Trigger ?? AppController.TriggerOfMode(Bind?.Mode);
    public string? Map => Action?.Map ?? Bind?.Map;
    public string? ActionName => Action?.Action ?? Bind?.Action;
    public string? Keys => Action?.Keys;

    /// <summary>Keyboard modifier written in front of a direct binding ("lshift" of "lshift+button1").</summary>
    public string? KeyModifier => Bind == null || Bind.IsUnbound ? null
        : ScInputCodes.SplitJsInput(Bind.Input).Modifiers.FirstOrDefault();

    public ActionSpec ToSpec() => new(Trigger, Map, ActionName, Keys, KeyModifier);
}

public sealed partial class AppController
{
    // ---- direct vs. app-driven --------------------------------------------------------------

    public static ButtonTrigger TriggerOfMode(string? mode) => mode switch
    {
        null or "" or "press" => ButtonTrigger.Pressed,
        "hold" or "delayed_hold" or "delayed_hold_long" or "delayed_hold_no_retrigger" or "hold_no_retrigger" => ButtonTrigger.LongPress,
        "double_tap" or "double_tap_nonblocking" => ButtonTrigger.DoublePress,
        _ => ButtonTrigger.Pressed,
    };

    private static string? ModeOfTrigger(ButtonTrigger t) => t switch
    {
        ButtonTrigger.LongPress => "hold",
        ButtonTrigger.DoublePress => "double_tap",
        _ => null,
    };

    /// <summary>
    /// A game action on a plain press / long press / double press needs no app at all: it is written
    /// straight onto the joystick button in actionmaps.xml.
    /// </summary>
    public bool IsDirect(ActionSpec s)
    {
        if (!s.IsGameAction) return false;
        if (s.Trigger is not (ButtonTrigger.Pressed or ButtonTrigger.LongPress or ButtonTrigger.DoublePress)) return false;
        return Catalog?.Find(s.Map!, s.Action!)?.Joystick ?? true;
    }

    /// <summary>Everything attached to one input: direct bindings first, then app-driven actions.</summary>
    public IReadOnlyList<ButtonActionRow> ActionsOf(ControllerProfile p, string input)
    {
        var rows = new List<ButtonActionRow>();
        foreach (var b in p.Binds.Where(b => !b.IsUnbound && ScInputCodes.SplitJsInput(b.Input).Input == input))
            rows.Add(new ButtonActionRow { Bind = b });
        // A button that only holds a modifier is shown by the inspector's "Use as modifier" setting instead.
        foreach (var a in p.Actions.Where(a => a.Input == input && !IsLayerAction(a)))
            rows.Add(new ButtonActionRow { Action = a });
        return rows;
    }

    public void AddAction(ControllerProfile p, string input, ActionSpec spec)
    {
        if (IsDirect(spec))
        {
            SetBind(p, spec.Map!, spec.Action!, ScInputCodes.ComposeJsInput(new[] { spec.KeyModifier }, input),
                ModeOfTrigger(spec.Trigger));
            return;
        }
        var trigger = spec.Trigger is ButtonTrigger.LongPress or ButtonTrigger.DoublePress ? ButtonTrigger.Pressed : spec.Trigger;
        p.Actions.Add(new ButtonAction
        {
            Input = input,
            Trigger = trigger,
            Map = spec.IsGameAction ? spec.Map : null,
            Action = spec.IsGameAction ? spec.Action : null,
            Keys = spec.IsGameAction ? null : spec.Keys,
        });
        if (spec.IsGameAction) EnsureKey(spec.Map!, spec.Action!);
        Touch();
    }

    public void RemoveAction(ControllerProfile p, ButtonActionRow row)
    {
        if (row.Bind != null) p.Binds.Remove(row.Bind);
        if (row.Action != null) p.Actions.Remove(row.Action);
        Touch();
    }

    /// <summary>Replaces a row; keeps an unusual activation mode of a direct binding while the trigger stays.</summary>
    public void UpdateAction(ControllerProfile p, string input, ButtonActionRow row, ActionSpec spec)
    {
        var keepMode = row.Bind?.Mode;
        bool sameTrigger = row.Trigger == spec.Trigger;
        if (row.Bind != null) p.Binds.Remove(row.Bind);
        if (row.Action != null) p.Actions.Remove(row.Action);
        AddAction(p, input, spec);
        var bindInput = ScInputCodes.ComposeJsInput(new[] { spec.KeyModifier }, input);
        if (sameTrigger && keepMode != null && IsDirect(spec))
            foreach (var b in p.Binds.Where(b => b.Map == spec.Map && b.Action == spec.Action && b.Input == bindInput))
                b.Mode = keepMode;
        Touch();
    }

    // ---- button as keyboard modifier --------------------------------------------------------

    /// <summary>The modifier ("lshift") this button holds down while pressed, or null.</summary>
    public string? KeyModifierOf(ControllerProfile p, string input) =>
        p.Actions.FirstOrDefault(a => a.Input == input && IsLayerAction(a))?.Keys;

    private static bool IsLayerAction(ButtonAction a) =>
        a.Trigger == ButtonTrigger.Held && a.Action == null
        && a.Keys != null && ScInputCodes.IsKeyModifier(a.Keys);

    /// <summary>
    /// Makes the button hold a keyboard modifier while it is pressed (null removes it). Star Citizen then sees
    /// a real shift/ctrl/alt, so direct bindings like "lshift+button1" fire on the other buttons.
    /// </summary>
    public void SetKeyModifierButton(ControllerProfile p, string input, string? modifier)
    {
        p.Actions.RemoveAll(a => a.Input == input && IsLayerAction(a));
        if (!string.IsNullOrEmpty(modifier) && ScInputCodes.IsKeyModifier(modifier))
            p.Actions.Add(new ButtonAction
            {
                Input = input, Trigger = ButtonTrigger.Held, Keys = modifier.ToLowerInvariant(),
            });
        Touch();
    }

    /// <summary>Other buttons of the profile that hold the same modifier (two layer buttons doing the same is rarely intended).</summary>
    public IReadOnlyList<string> OtherLayerButtons(ControllerProfile p, string input, string modifier) =>
        p.Actions.Where(a => a.Input != input && IsLayerAction(a) && a.Keys == modifier).Select(a => a.Input).Distinct().ToList();

    // ---- keys of game actions ---------------------------------------------------------------

    public ResolvedKey KeyFor(string map, string action) =>
        KeyPlanner.Resolve(KeyboardMouseProfile, Catalog, map, action);

    /// <summary>
    /// Makes sure the action has a key the app can send: its own binding, the game default, or - if neither
    /// exists - a free combo that is written into the keyboard layer (and deployed like any other binding).
    /// </summary>
    public ResolvedKey EnsureKey(string map, string action)
    {
        var r = KeyFor(map, action);
        if (r.Combo != null) return r;
        string? combo;
        lock (_gate)
        {
            var raw = new List<string>();
            try { raw = LoadActionMaps()?.Rebinds().Select(x => x.Input).ToList() ?? raw; } catch { /* unreadable file */ }
            combo = KeyPlanner.FreeKey(KeyPlanner.UsedKeys(KeyboardMouseProfile, Catalog, raw));
        }
        if (combo == null || !SetActionKey(map, action, combo)) return r;
        return new ResolvedKey(combo, KeySource.Own);
    }

    /// <summary>Sets the keyboard key of a game action (replaces any key of our keyboard layer).</summary>
    public bool SetActionKey(string map, string action, string combo)
    {
        var raw = KeyPlanner.ToRaw(combo);
        if (raw == null) return false;
        SetRawBind(KeyboardMouseProfile, map, action, raw);
        return true;
    }

    /// <summary>Other game actions (map/action) that use the same key combo as <paramref name="combo"/>.</summary>
    public IReadOnlyList<string> KeyConflicts(string map, string action, string combo)
    {
        var canon = KeyPlanner.Canonical(combo);
        var hits = new List<string>();
        if (Catalog != null)
            foreach (var a in Catalog.AllActions)
            {
                if (a.Map == map && a.Name == action) continue;
                var r = KeyFor(a.Map, a.Name);
                if (r.Combo != null && KeyPlanner.Canonical(r.Combo) == canon) hits.Add(a.Label);
            }
        return hits;
    }

    // ---- migration ------------------------------------------------------------------------------

    /// <summary>Turns the old "on press / on release key" rules of stick layouts into button actions.</summary>
    public void MigrateRules()
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var p in Store.Data.Profiles.Where(p => !p.IsSystem && p.Rules.Count > 0))
            {
                foreach (var (input, rule) in p.Rules)
                {
                    if (rule.OnPress is { IsEmpty: false } press)
                        p.Actions.Add(new ButtonAction
                        {
                            Input = input, Keys = press.Keys,
                            Trigger = press.Hold ? ButtonTrigger.Held : ButtonTrigger.Pressed,
                        });
                    if (rule.OnRelease is { IsEmpty: false } rel)
                        p.Actions.Add(new ButtonAction { Input = input, Keys = rel.Keys, Trigger = ButtonTrigger.Released });
                }
                p.Rules.Clear();
                changed = true;
            }
        }
        if (changed) Store.Save();
    }
}
