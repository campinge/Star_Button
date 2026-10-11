using ScMapper.Core;
using ScMapper.Core.Model;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

public class ProfileFitTests
{
    private static DeviceInfo Dev(int vid, int pid, string name, int buttons, int hats = 1, params int[] axisSlots) =>
        new(Guid.NewGuid(), name, name, vid, pid, $"path-{name}", buttons, axisSlots.Length, hats, 0) { AxisSlots = axisSlots };

    private static ControllerProfile Profile(params string[] inputs)
    {
        var p = new ControllerProfile { Name = "Power und Comms" };
        foreach (var i in inputs) p.Binds.Add(new ScBind { Map = "m", Action = "a" + i, Input = i });
        return p;
    }

    private static readonly ProfileDevice Nxt = new(0x231D, 0x0200, "VKBsim Gladiator NXT R", 120, 4, 8);
    private static readonly ProfileDevice Generic = new(0x0810, 0xE001, "Generic USB Joystick", 12, 0, 2);

    [Fact]
    public void A_profile_using_a_button_the_device_lacks_is_flagged()
    {
        var fit = ProfileFitCheck.Check(Profile("button3", "button57"), Nxt, Dev(0x0810, 0xE001, "Generic USB Joystick", 12));

        Assert.Equal(FitLevel.MissingInputs, fit.Level);
        Assert.Equal(new[] { "button57" }, fit.Missing);
        Assert.True(fit.NeedsConfirmation);
    }

    [Fact]
    public void The_same_profile_fits_the_big_stick_it_was_made_for()
    {
        var fit = ProfileFitCheck.Check(Profile("button3", "button57"), Nxt, Dev(0x231D, 0x0200, "VKBsim Gladiator NXT R", 120));

        Assert.Equal(FitLevel.Fits, fit.Level);
        Assert.False(fit.NeedsConfirmation);
    }

    [Fact]
    public void Another_model_with_enough_inputs_asks_for_confirmation_only()
    {
        var fit = ProfileFitCheck.Check(Profile("button3"), Generic, Dev(0x231D, 0x0200, "VKBsim Gladiator NXT R", 120));

        Assert.Equal(FitLevel.DifferentModel, fit.Level);
        Assert.Empty(fit.Missing);
    }

    [Fact]
    public void A_changed_button_count_of_the_same_model_is_no_reason_to_warn()
    {
        var fit = ProfileFitCheck.Check(Profile("button3"), Nxt, Dev(0x231D, 0x0200, "VKBsim Gladiator NXT R", 64));

        Assert.Equal(FitLevel.Fits, fit.Level);
    }

    [Fact]
    public void An_empty_profile_fits_any_device()
    {
        var fit = ProfileFitCheck.Check(new ControllerProfile(), Generic, Dev(0x231D, 0x0200, "VKB", 120));

        Assert.Equal(FitLevel.Fits, fit.Level);
    }

    [Fact]
    public void Modifiers_do_not_hide_a_missing_button()
    {
        var fit = ProfileFitCheck.Check(Profile("lshift+button60"), null, Dev(0x0810, 0xE001, "Generic", 12));

        Assert.Equal(FitLevel.MissingInputs, fit.Level);
        Assert.Equal(new[] { "button60" }, fit.Missing);
    }

    [Fact]
    public void Rules_and_actions_count_as_used_inputs_too()
    {
        var p = new ControllerProfile();
        p.Rules["button40"] = new ButtonRule { OnPress = new KeyAction { Keys = "a" } };
        p.Actions.Add(new ButtonAction { Input = "hat2_up", Keys = "b" });

        var fit = ProfileFitCheck.Check(p, null, Dev(0x0810, 0xE001, "Generic", 12, hats: 1));

        Assert.Equal(new[] { "button40", "hat2_up" }, fit.Missing.OrderBy(x => x));
    }

    [Fact]
    public void An_axis_is_only_missing_when_the_device_lists_its_axes()
    {
        var p = Profile("rotz");

        Assert.Equal(FitLevel.MissingInputs, ProfileFitCheck.Check(p, null, Dev(1, 1, "A", 12, 0, 0, 1)).Level);
        Assert.Equal(FitLevel.Fits, ProfileFitCheck.Check(p, null, Dev(1, 1, "A", 12, 0, 0, 1, 5)).Level);
        Assert.Equal(FitLevel.Fits, ProfileFitCheck.Check(p, null, Dev(1, 1, "A", 12, 0)).Level);   // unknown
    }

    [Fact]
    public void The_controller_remembers_the_device_a_profile_was_used_on()
    {
        var dev = Dev(0x0810, 0xE001, "Generic USB Joystick", 12);
        var hub = new FakeHub();
        hub.List.Add(dev);
        var dir = Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N"));
        using var c = new AppController(new AppStore(dir), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var profile = c.Store.Profile(c.Store.Data.Sticks.Single().ProfileId)!;
        profile.Binds.Add(new ScBind { Map = "m", Action = "a", Input = "button30" });

        var origin = c.OriginOf(profile);

        Assert.Equal(new ProfileDevice(0x0810, 0xE001, "Generic USB Joystick", 12, 0, 1), origin);
        Assert.Equal(FitLevel.Fits, c.CheckFit(profile, dev with { Buttons = 32 }).Level);
        Assert.Equal(FitLevel.MissingInputs, c.CheckFit(profile, dev).Level);
    }
}
