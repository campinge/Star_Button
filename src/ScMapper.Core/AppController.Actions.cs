using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.Core;

/// <summary>What a button action should do, as chosen in the UI.</summary>
public sealed record ActionSpec(ButtonTrigger Trigger, string? Map, string? Action, string? Keys,
    string? KeyModifier = null)
{
    public bool IsGameAction => Map != null && Action != null;
}

public enum VirtualPhase { Tap, Down, Up }

/// <summary>A key (combo) that Star Button sends in place of a button edge.</summary>
public sealed record VirtualKey(string Combo, VirtualPhase Phase);

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

    /// <summary>
    /// The custom actions of one input. Star Citizen bindings on the button are not listed: they belong to the
    /// game and show up as conflicts once the button is replaced.
    /// </summary>
    public IReadOnlyList<ButtonActionRow> ActionsOf(ControllerProfile p, string input)
    {
        var rows = new List<ButtonActionRow>();
        // A button that only holds a modifier is shown by the inspector's "Use as modifier" setting instead.
        foreach (var a in p.Actions.Where(a => a.Input == input && !IsLayerAction(a)))
            rows.Add(new ButtonActionRow { Action = a });
        return rows;
    }

    /// <summary>
    /// A direct binding hands the game the button as is, so the game sees it held for as long as the button is
    /// down. A button that also has an "on release" action is meant to give impulses: its plain presses go
    /// through the app too, which taps the key.
    /// </summary>
    private static bool HasReleaseAction(ControllerProfile p, string input) =>
        p.Actions.Any(a => a.Input == input && a.Trigger == ButtonTrigger.Released);

    private static void PulsePresses(ControllerProfile p, string input)
    {
        if (!HasReleaseAction(p, input)) return;
        foreach (var b in p.Binds.Where(b => !b.IsUnbound && string.IsNullOrEmpty(b.Mode)).ToList())
        {
            var split = ScInputCodes.SplitJsInput(b.Input);
            if (split.Input != input || split.Modifiers.Any()) continue;
            p.Binds.Remove(b);
            p.Actions.Add(new ButtonAction { Input = input, Trigger = ButtonTrigger.Pressed, Map = b.Map, Action = b.Action });
        }
    }

    public void AddAction(ControllerProfile p, string input, ActionSpec spec)
    {
        if (IsDirect(spec) && !(spec.Trigger == ButtonTrigger.Pressed && string.IsNullOrEmpty(spec.KeyModifier)
                                && HasReleaseAction(p, input)))
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
        PulsePresses(p, input);
        // No key is chosen here: the user picks or records it in the action's row (see SuggestKey), so
        // nothing is written into the profile's keyboard layer behind their back.
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

    // ---- what a button turns into -----------------------------------------------------------

    /// <summary>True when the button has actions of its own: it is replaced by what they send and no longer exists as a button.</summary>
    public bool IsReplaced(StickRecord stick, string input) =>
        Store.Profile(stick.ProfileId) is { } p && p.Actions.Any(a => string.Equals(a.Input, input, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The keys Star Button sends for one press or release of a button, exactly as the rule engine does.
    /// Empty when the button has no actions (it is then an ordinary input).
    /// </summary>
    public IReadOnlyList<VirtualKey> VirtualKeys(StickRecord stick, string input, bool pressed)
    {
        var list = new List<VirtualKey>();
        if (Store.Profile(stick.ProfileId) is not { } p) return list;
        foreach (var a in p.Actions.Where(a => string.Equals(a.Input, input, StringComparison.OrdinalIgnoreCase)))
        {
            VirtualPhase? phase = a.Trigger switch
            {
                ButtonTrigger.Held => pressed ? VirtualPhase.Down : VirtualPhase.Up,
                ButtonTrigger.Pressed => pressed ? VirtualPhase.Tap : null,
                ButtonTrigger.Released => pressed ? null : VirtualPhase.Tap,
                _ => null,
            };
            if (phase == null) continue;
            var combo = a.Keys ?? (a.Map != null && a.Action != null ? KeyFor(a.Map, a.Action).Combo : null);
            if (!string.IsNullOrWhiteSpace(combo)) list.Add(new VirtualKey(combo, phase.Value));
        }
        return list;
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
    /// A key combo nobody uses (game defaults, this profile's keyboard layer, the game's file), or null. Called
    /// again with the last suggestion it walks on to the next free one. Only suggests: the caller decides.
    /// </summary>
    public string? SuggestKey(string? after = null)
    {
        lock (_gate)
        {
            return KeyPlanner.NextFreeKey(UsageLocked().Keys.ToHashSet(), after);
        }
    }

    /// <summary>Own keys of the buttons in the active profile's stick layouts, with "Layout · button" as owner.</summary>
    private List<(string Combo, string Owner)> ButtonKeysLocked()
    {
        var list = new List<(string, string)>();
        foreach (var layoutId in ActiveSetup.StickLayouts.Values.Distinct())
        {
            var p = Store.Profile(layoutId);
            if (p == null) continue;
            foreach (var a in p.Actions.Where(a => a.Action == null && !string.IsNullOrWhiteSpace(a.Keys) && !IsLayerAction(a)))
                list.Add((a.Keys!, $"{p.Name} · {a.Input}"));
        }
        return list;
    }

    private Dictionary<string, string> UsageLocked()
    {
        var raw = new List<string>();
        try { raw = LoadActionMaps()?.Rebinds().Select(x => x.Input).ToList() ?? raw; } catch { /* unreadable file */ }
        return KeyPlanner.UsageMap(KeyboardMouseProfile, Catalog, raw, ButtonKeysLocked());
    }

    /// <summary>All key combos that are not bound by the game's defaults, a button, this profile or the game's file.</summary>
    public IReadOnlyList<string> FreeKeys()
    {
        lock (_gate)
        {
            var used = UsageLocked().Keys.ToHashSet();
            return KeyPlanner.FreeKeys(used);
        }
    }

    /// <summary>Who already holds <paramref name="combo"/> (a game action or another button), or null when it is free.</summary>
    public string? KeyOwner(string combo)
    {
        lock (_gate)
            return UsageLocked().GetValueOrDefault(KeyPlanner.Canonical(combo));
    }

    /// <summary>Every offered combo with its current holder (null = free), for pickers that show what is taken.</summary>
    public IReadOnlyList<(string Combo, string? Owner)> KeyChoices()
    {
        lock (_gate)
        {
            var usage = UsageLocked();
            return KeyPlanner.Candidates().Select(c => (c, usage.GetValueOrDefault(KeyPlanner.Canonical(c)))).ToList();
        }
    }

    /// <summary>Sets the keyboard key of a game action (replaces any key of our keyboard layer).</summary>
    public bool SetActionKey(string map, string action, string combo)
    {
        if (KeyPlanner.DangerReason(combo) != null) return false;
        var raw = KeyPlanner.ToRaw(combo);
        if (raw == null) return false;
        SetRawBind(KeyboardMouseProfile, map, action, raw);
        return true;
    }

    /// <summary>
    /// Key combos in the active profile's stick layouts that the app will refuse to send (Alt+F4 and the like).
    /// Used to tell the user after an import, so a shared profile never fails silently.
    /// </summary>
    public IReadOnlyList<string> BlockedCombos()
    {
        var found = new List<string>();
        lock (_gate)
            foreach (var layoutId in ActiveSetup.StickLayouts.Values.Distinct())
            {
                var p = Store.Profile(layoutId);
                if (p == null) continue;
                foreach (var a in p.Actions)
                {
                    var combo = a.Keys ?? (a.Map != null && a.Action != null ? KeyFor(a.Map, a.Action).Combo : null);
                    if (combo != null && KeyPlanner.DangerReason(combo) != null && !found.Contains(combo)) found.Add(combo);
                }
            }
        return found;
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
            foreach (var p in Store.Data.Profiles.Where(p => !p.IsSystem
                         && p.Rules.Values.Any(r => !(r.OnPress?.IsEmpty ?? true) || !(r.OnRelease?.IsEmpty ?? true)
                                                    || (string.IsNullOrWhiteSpace(r.Label)))))
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
                // Rules that carry a button name stay (name only); everything else was moved to actions.
                foreach (var input in p.Rules.Keys.ToList())
                {
                    var rule = p.Rules[input];
                    if (string.IsNullOrWhiteSpace(rule.Label)) p.Rules.Remove(input);
                    else { rule.OnPress = null; rule.OnRelease = null; }
                }
                changed = true;
            }
        }
        if (changed) Store.Save();
    }
}
