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
        Assert.Equal("lctrl+f4", free);
    }

    [Fact]
    public void Released_game_action_without_key_gets_a_free_key_and_fires_on_release_only()
    {
        var hub = new FakeHub();
        var d = Dev();
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(TmpStore(), hub, output);
        c.Engine.TapMilliseconds = 1;
        c.Reconcile();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;

        c.AddAction(prof, "button4", new ActionSpec(ButtonTrigger.Released, "spaceship_movement", "v_retract_landing_system", null));

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
        c.Reconcile();
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
        c.Reconcile();
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
}
