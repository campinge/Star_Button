using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.Core;

public enum ConflictKind
{
    /// <summary>The button is replaced by a Star Button action, but Star Citizen still has a binding on it.</summary>
    DirectBinding,
    /// <summary>An action sent by Star Button has no keyboard key in this profile, so nothing would be sent.</summary>
    MissingKey,
}

/// <summary>
/// One thing the user should look at when a profile is loaded. Nothing here is fixed automatically: every
/// resolution (ignore, remove the binding, pick a key) is the user's own click.
/// </summary>
public sealed record ButtonConflict(ConflictKind Kind, StickRecord Stick, ControllerProfile Layout, string Input,
    string Map, string Action, string Id, ScBind? Bind, bool IsGameDefault, string Replacement)
{
    /// <summary>Star Citizen's code of the binding ("js1_button3"), or null when the stick has no js number.</summary>
    public string? Raw => Kind != ConflictKind.DirectBinding ? null
        : IsGameDefault ? $"js1_{Input}"
        : Stick.ScInstance is { } n && Bind != null ? $"js{n}_{Bind.Input}" : null;
}

public sealed partial class AppController
{
    /// <summary>"Shift while held", "Landing gear (on release)", "Key F9 (on press)".</summary>
    public string DescribeAction(ButtonAction a)
    {
        string when = a.Trigger switch
        {
            ButtonTrigger.Released => "on release",
            ButtonTrigger.Held => "while held",
            _ => "on press",
        };
        if (IsLayerAction(a)) return $"{ScInputCodes.Pretty("kb1_" + a.Keys)} while held";
        string what = a.Map != null && a.Action != null
            ? Catalog?.Find(a.Map, a.Action)?.Label ?? a.Action
            : "Key " + ScInputCodes.Pretty("kb1_" + a.Keys);
        return $"{what} ({when})";
    }

    /// <summary>
    /// What is off in the ACTIVE profile: replaced buttons that still carry a Star Citizen binding (a game
    /// default included), and actions without a key. Checked when a profile is loaded, not when an action is
    /// added - a stick layout is shared by many profiles, so only the loaded profile can say.
    /// </summary>
    public IReadOnlyList<ButtonConflict> FindConflicts(bool includeIgnored = false)
    {
        Setup setup;
        List<(StickRecord Stick, ControllerProfile Layout)> layouts;
        lock (_gate)
        {
            setup = ActiveSetup;
            // An unplugged stick is inactive: its bindings are not deployed, so they cannot clash with anything.
            layouts = Store.Data.Sticks.Where(IsConnected)
                .Select(s => (Stick: s, Layout: Store.Profile(s.ProfileId)))
                .Where(x => x.Layout != null).Select(x => (x.Stick, x.Layout!)).ToList();
        }
        var ignored = includeIgnored ? new HashSet<string>() : setup.IgnoredConflicts.ToHashSet();
        var boundActions = layouts.SelectMany(x => x.Layout.Binds).Select(b => (b.Map, b.Action)).ToHashSet();
        var list = new List<ButtonConflict>();

        void Add(ButtonConflict c)
        {
            if (!ignored.Contains(c.Id)) list.Add(c);
        }

        foreach (var (stick, layout) in layouts)
        {
            foreach (var group in layout.Actions.GroupBy(a => a.Input))
            {
                string input = group.Key;
                var pressing = group.Where(a => a.Trigger is ButtonTrigger.Pressed or ButtonTrigger.Held).ToList();
                if (pressing.Count > 0)
                {
                    var repl = DescribeAction(pressing[0]) + (pressing.Count > 1 ? $" +{pressing.Count - 1} more" : "");
                    foreach (var b in layout.Binds.Where(b => !b.IsUnbound && ScInputCodes.SplitJsInput(b.Input).Input == input))
                        Add(new ButtonConflict(ConflictKind.DirectBinding, stick, layout, input, b.Map, b.Action,
                            $"bind|{stick.Id:N}|{input}|{b.Map}/{b.Action}|{b.Input}|{b.Mode}", b, false, repl));

                    // The game's own joystick defaults only exist for the first stick, and only while the action
                    // has no joystick binding of its own (an "unbound" marker counts as one).
                    if (setup.UsesDefaults("js") && stick.ScInstance == 1 && Catalog != null)
                        foreach (var d in Catalog.AllActions)
                        {
                            if (!d.Joystick || string.IsNullOrWhiteSpace(d.DefaultJoystick)) continue;
                            if (boundActions.Contains((d.Map, d.Name))) continue;
                            var raw = d.DefaultJoystick;
                            if (raw.StartsWith("js", StringComparison.Ordinal) && raw.Contains('_')) raw = raw[(raw.IndexOf('_') + 1)..];
                            if (ScInputCodes.SplitJsInput(raw).Input != input) continue;
                            Add(new ButtonConflict(ConflictKind.DirectBinding, stick, layout, input, d.Map, d.Name,
                                $"default|{stick.Id:N}|{input}|{d.Map}/{d.Name}", null, true, repl));
                        }
                }

                foreach (var a in group.Where(a => a.Map != null && a.Action != null && !IsLayerAction(a))
                             .GroupBy(a => (a.Map, a.Action)).Select(g => g.First()))
                    if (KeyFor(a.Map!, a.Action!).Combo == null)
                        Add(new ButtonConflict(ConflictKind.MissingKey, stick, layout, input, a.Map!, a.Action!,
                            $"key|{stick.Id:N}|{input}|{a.Map}/{a.Action}", null, false, DescribeAction(a)));
            }
        }
        return list;
    }

    /// <summary>Conflicts of one physical input (what the button's inspector shows).</summary>
    public IReadOnlyList<ButtonConflict> ConflictsOf(StickRecord stick, string input) =>
        FindConflicts().Where(c => c.Stick.Id == stick.Id && c.Input == input).ToList();

    /// <summary>The user chose to remove the Star Citizen binding that collides with a replaced button.</summary>
    public bool RemoveConflictingBinding(ButtonConflict c)
    {
        if (c.Kind != ConflictKind.DirectBinding) return false;
        if (c.Raw is { } raw && RemoveBinding(c.Map, c.Action, raw)) return true;
        if (c.Bind == null) return false;
        RemoveBind(c.Layout, c.Bind);
        return true;
    }

    /// <summary>The user chose to leave the conflict as it is; it stays quiet until the binding changes.</summary>
    public void IgnoreConflict(ButtonConflict c)
    {
        var setup = ActiveSetup;
        if (!setup.IgnoredConflicts.Contains(c.Id)) setup.IgnoredConflicts.Add(c.Id);
        Store.Save();
        Changed?.Invoke();
    }
}
