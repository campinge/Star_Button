using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

public class ActionTests
{
    private static AppStore TmpStore() =>
        new(Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N")));

    private static DeviceInfo Dev() => new(Guid.NewGuid(), "Alpha", "Alpha", 1, 1, "p0", 12, 2, 0, 0);

    private static void WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 100 && !cond(); i++) Thread.Sleep(20);
    }

    private static ActionCatalog Catalog(params (string Name, string Kb)[] actions) => new()
    {
        Groups =
        {
            new ActionGroup
            {
                Name = "m", Label = "M", Category = "C",
                Actions = actions.Select(a => new ActionDef { Map = "m", Name = a.Name, Label = a.Name, DefaultKeyboard = a.Kb, Joystick = true }).ToList(),
            },
        },
    };

    [Fact]
    public void Own_key_wins_over_default_and_unbound_marker_hides_default()
    {
        var cat = Catalog(("power", "u"));
        var layer = new ControllerProfile { Kind = ProfileKind.KeyboardMouse };
        Assert.Equal(new ResolvedKey("u", KeySource.Default), KeyPlanner.Resolve(layer, cat, "m", "power"));

        layer.Binds.Add(new ScBind { Map = "m", Action = "power", Input = "kb1_lalt+np_5" });
        Assert.Equal(new ResolvedKey("lalt+np5", KeySource.Own), KeyPlanner.Resolve(layer, cat, "m", "power"));

        layer.Binds.Clear();
        layer.Binds.Add(new ScBind { Map = "m", Action = "power", Input = "kb1_ " });
        Assert.Equal(KeySource.None, KeyPlanner.Resolve(layer, cat, "m", "power").Source);
    }

    [Fact]
    public void Free_key_skips_game_defaults_and_own_binds()
    {
        var cat = Catalog(("a", "lctrl+f1"), ("b", "f1+lctrl"), ("c", "lctrl+f2"));
        var layer = new ControllerProfile { Kind = ProfileKind.KeyboardMouse };
        layer.Binds.Add(new ScBind { Map = "m", Action = "x", Input = "kb1_lctrl+f3" });
        var free = KeyPlanner.FreeKey(KeyPlanner.UsedKeys(layer, cat));
        Assert.Equal("lctrl+f5", free);
    }

    [Fact]
    public void Press_and_release_actions_are_two_short_taps_even_when_the_button_is_held()
    {
        var hub = new FakeHub();
        var d = Dev();
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(TmpStore(), hub, output);
        c.Engine.TapMilliseconds = 1;
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;
        c.AddAction(prof, "button1", new ActionSpec(ButtonTrigger.Pressed, null, null, "lctrl+f9"));
        c.AddAction(prof, "button1", new ActionSpec(ButtonTrigger.Released, null, null, "ralt"));

        hub.Raise(d.InstanceGuid, "button1", true);
        Thread.Sleep(300);                       // held for a while
        Assert.Equal(new[] { "down:1D+43", "up:1D+43" }, output.Snapshot());
        hub.Raise(d.InstanceGuid, "button1", false);
        WaitFor(() => output.Snapshot().Length >= 4);
        Assert.Equal(new[] { "down:1D+43", "up:1D+43", "down:38", "up:38" }, output.Snapshot());
    }

    [Fact]
    public void Button_with_actions_is_replaced_by_the_keys_it_sends_and_output_can_be_suspended()
    {
        var hub = new FakeHub();
        var d = Dev();
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(TmpStore(), hub, output);
        c.Engine.TapMilliseconds = 1;
        c.Reconcile(); c.RegisterNewDevices();
        var stick = c.Store.Data.Sticks[0];
        var prof = c.Store.Profile(stick.ProfileId)!;

        Assert.False(c.IsReplaced(stick, "button1"));
        Assert.Empty(c.VirtualKeys(stick, "button1", true));

        c.AddAction(prof, "button1", new ActionSpec(ButtonTrigger.Pressed, null, null, "lalt"));
        c.AddAction(prof, "button1", new ActionSpec(ButtonTrigger.Released, null, null, "ralt"));

        Assert.True(c.IsReplaced(stick, "button1"));
        Assert.False(c.IsReplaced(stick, "button2"));
        Assert.Equal(new[] { new VirtualKey("lalt", VirtualPhase.Tap) }, c.VirtualKeys(stick, "button1", true));
        Assert.Equal(new[] { new VirtualKey("ralt", VirtualPhase.Tap) }, c.VirtualKeys(stick, "button1", false));

        // Suspended output (binding mode) sends nothing at all.
        c.Engine.Suspended = true;
        hub.Raise(d.InstanceGuid, "button1", true);
        hub.Raise(d.InstanceGuid, "button1", false);
        Thread.Sleep(80);
        Assert.Empty(output.Snapshot());
    }

    [Fact]
    public void Released_game_action_has_no_key_until_the_user_picks_one_and_fires_on_release_only()
    {
        var hub = new FakeHub();
        var d = Dev();
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(TmpStore(), hub, output);
        c.Engine.TapMilliseconds = 1;
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;

        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Released, "spaceship_movement", "v_retract_landing_system", null));

        // Adding an action writes nothing into the keyboard layer on its own.
        Assert.Equal(KeySource.None, c.KeyFor("spaceship_movement", "v_retract_landing_system").Source);
        Assert.Empty(c.KeyboardMouseProfile.Binds);

        // The user asks for a suggestion and takes it.
        var suggested = c.SuggestKey();
        Assert.Equal("lctrl+f1", suggested);
        Assert.True(c.SetActionKey("spaceship_movement", "v_retract_landing_system", suggested!));

        var key = c.KeyFor("spaceship_movement", "v_retract_landing_system");
        Assert.Equal(KeySource.Own, key.Source);
        Assert.Equal("lctrl+f1", key.Combo);
        Assert.Contains(c.KeyboardMouseProfile.Binds, b => b.Action == "v_retract_landing_system" && b.Input == "kb1_lctrl+f1");
        Assert.Empty(prof.Binds);                                     // not a direct joystick binding

        hub.Raise(d.InstanceGuid, "button4", true);
        Thread.Sleep(80);
        Assert.Empty(output.Snapshot());
        hub.Raise(d.InstanceGuid, "button4", false);
        WaitFor(() => output.Snapshot().Length >= 2);
        Assert.Equal(new[] { "down:1D+3B", "up:1D+3B" }, output.Snapshot());
    }

    [Fact]
    public void Pressed_game_action_is_a_direct_binding_without_app_output()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev());
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;
        c.AddAction(prof, "button2", new ActionSpec(ButtonTrigger.Pressed, "m", "a", null));
        Assert.Single(prof.Binds);
        Assert.Empty(prof.Actions);
        Assert.Empty(c.KeyboardMouseProfile.Binds);
        var row = Assert.Single(c.ActionsOf(prof, "button2"));
        Assert.False(row.ViaApp);
        Assert.Equal(ButtonTrigger.Pressed, row.Trigger);
    }

    [Fact]
    public void Old_press_release_rules_become_actions()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev());
        using var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;
        prof.Rules["button5"] = new ButtonRule
        {
            OnPress = new KeyAction { Keys = "u", Hold = true },
            OnRelease = new KeyAction { Keys = "lshift+d" },
        };
        c.MigrateRules();
        Assert.Empty(prof.Rules);
        Assert.Equal(new[] { ButtonTrigger.Held, ButtonTrigger.Released }, prof.Actions.Select(a => a.Trigger).ToArray());
    }

    // ---- conflicts of the loaded profile ------------------------------------------------------

    private static (AppController C, ControllerProfile Prof) Controller()
    {
        var hub = new FakeHub();
        hub.List.Add(Dev());
        var c = new AppController(TmpStore(), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        return (c, c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!);
    }

    [Fact]
    public void Adding_an_action_checks_nothing_and_removes_nothing()
    {
        var (c, prof) = Controller();
        using var _ = c;
        c.SetBind(prof, "m", "fire", "button4");
        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Held, null, null, "lshift"));
        Assert.Single(prof.Binds);                       // the Star Citizen binding is still there
        Assert.Single(prof.Actions);
    }

    [Fact]
    public void Replaced_button_with_a_binding_is_reported_and_stays_until_the_user_decides()
    {
        var (c, prof) = Controller();
        using var _ = c;
        c.SetBind(prof, "m", "fire", "button4");
        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Held, null, null, "lshift"));

        var conflict = Assert.Single(c.FindConflicts());
        Assert.Equal(ConflictKind.DirectBinding, conflict.Kind);
        Assert.Equal("button4", conflict.Input);
        Assert.Equal("fire", conflict.Action);

        c.IgnoreConflict(conflict);
        Assert.Empty(c.FindConflicts());
        Assert.Single(c.FindConflicts(includeIgnored: true));
        Assert.Single(prof.Binds);                       // ignoring changes nothing in the profile
    }

    [Fact]
    public void Removing_the_conflicting_binding_is_an_explicit_call()
    {
        var (c, prof) = Controller();
        using var _ = c;
        c.SetBind(prof, "m", "fire", "button4");
        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Held, null, null, "lshift"));
        Assert.True(c.RemoveConflictingBinding(c.FindConflicts().Single()));
        Assert.DoesNotContain(prof.Binds, b => !b.IsUnbound && b.Action == "fire");
        Assert.Empty(c.FindConflicts());
    }

    [Fact]
    public void Press_becomes_an_impulse_when_the_button_also_has_a_release_action()
    {
        var (c, prof) = Controller();
        using var _ = c;
        c.SetBind(prof, "m", "fire", "button4");
        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Released, null, null, "f9"));
        // A direct binding would be held by the game as long as the button is down.
        Assert.DoesNotContain(prof.Binds, b => !b.IsUnbound && b.Action == "fire");
        Assert.Contains(prof.Actions, a => a.Trigger == ButtonTrigger.Pressed && a.Action == "fire");

        // A press added afterwards is routed through the app as well.
        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Pressed, "m", "gear", null));
        Assert.DoesNotContain(prof.Binds, b => !b.IsUnbound && b.Action == "gear");
        Assert.Contains(prof.Actions, a => a.Trigger == ButtonTrigger.Pressed && a.Action == "gear");
    }

    [Fact]
    public void Game_action_without_a_key_is_reported_until_a_key_is_set()
    {
        var (c, prof) = Controller();
        using var _ = c;
        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Released, "m", "gear", null));
        var conflict = Assert.Single(c.FindConflicts());
        Assert.Equal(ConflictKind.MissingKey, conflict.Kind);

        c.SetActionKey("m", "gear", c.SuggestKey()!);
        Assert.Empty(c.FindConflicts());
    }

    [Theory]
    [InlineData("lalt+f4")]
    [InlineData("lctrl+lalt+f4")]
    [InlineData("lctrl+f4")]
    [InlineData("lctrl+lalt+delete")]
    [InlineData("lalt+tab")]
    [InlineData("lctrl+lshift+esc")]
    [InlineData("lctrl+lalt+f9")]
    [InlineData("lalt+np5")]
    [InlineData("lshift+np2")]
    public void Dangerous_combos_are_flagged(string combo) => Assert.NotNull(KeyPlanner.DangerReason(combo));

    [Fact]
    public void Dangerous_combos_are_never_offered()
    {
        var all = KeyPlanner.Candidates().ToList();
        Assert.NotEmpty(all);
        Assert.All(all, c => Assert.Null(KeyPlanner.DangerReason(c)));
        Assert.DoesNotContain("lalt+f4", all);
        Assert.Contains("lalt+f3", all);
    }

    [Fact]
    public void Suggested_keys_walk_through_the_free_combos()
    {
        var used = new HashSet<string>();
        var first = KeyPlanner.NextFreeKey(used);
        var second = KeyPlanner.NextFreeKey(used, first);
        Assert.Equal("lctrl+f1", first);
        Assert.Equal("lctrl+f2", second);
        used.Add(KeyPlanner.Canonical("lctrl+f2"));
        Assert.Equal("lctrl+f3", KeyPlanner.NextFreeKey(used, first));
    }
}
