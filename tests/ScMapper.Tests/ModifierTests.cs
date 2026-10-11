using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

/// <summary>Keyboard modifier + stick button: Star Citizen writes it as "js4_lshift+button1".</summary>
public class ModifierTests
{
    private static string Tmp(string name) =>
        Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N"), name);

    private static void WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 100 && !cond(); i++) Thread.Sleep(20);
    }

    [Theory]
    [InlineData("button1", "", "button1")]
    [InlineData("lshift+button1", "lshift", "button1")]
    [InlineData("lctrl+lalt+hat1_up", "lctrl,lalt", "hat1_up")]
    [InlineData(" ", "", " ")]
    [InlineData("x", "", "x")]
    public void Split_separates_keyboard_modifiers_from_the_stick_input(string input, string mods, string bare)
    {
        var (m, i) = ScInputCodes.SplitJsInput(input);
        Assert.Equal(mods, string.Join(",", m));
        Assert.Equal(bare, i);
    }

    [Fact]
    public void Compose_puts_modifiers_first_and_ignores_unknown_or_empty()
    {
        Assert.Equal("lshift+button1", ScInputCodes.ComposeJsInput(new string?[] { "LShift", null, "banana" }, "button1"));
        Assert.Equal("button1", ScInputCodes.ComposeJsInput(new string?[] { null }, "button1"));
    }

    [Fact]
    public void Deploy_writes_the_modifier_in_front_of_the_button_and_reads_it_back()
    {
        var path = Tmp("actionmaps.xml");
        var p = new ControllerProfile { Name = "Flight" };
        p.Binds.Add(new ScBind { Map = "spaceship_movement", Action = "v_pitch_up", Input = "lshift+button1" });
        var stick = new StickRecord { Name = "Right", Vid = 0x231D, Pid = 0x0200, ProductName = "VKBsim Gladiator NXT R", ScInstance = 4, ProfileId = p.Id };

        Deployer.Apply(path, Path.Combine(Path.GetDirectoryName(path)!, "bk"), new[] { stick }, new[] { p }, Array.Empty<string>());

        var r = ActionMapsFile.Load(path).Rebinds().Single(x => x.Action == "v_pitch_up");
        Assert.Equal("js4_lshift+button1", r.Input);
        Assert.Equal(4, r.JsInstance);
        Assert.Equal("lshift+button1", r.JsInputName);
    }

    [Fact]
    public void Import_keeps_the_keyboard_modifier()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Exactly what Star Citizen wrote for "Left Shift + stick 4 button 1".
        File.WriteAllText(path, """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <actionmap name="spaceship_movement"><action name="v_pitch_up"><rebind input="js4_lshift+button1"/></action></actionmap>
</ActionProfiles></ActionMaps>
""");
        // The stick must be plugged in: an unplugged one does not own its number.
        var hub = new FakeHub();
        hub.List.Add(new DeviceInfo(Guid.NewGuid(), "Right", "Right", 1, 1, "path3", 12, 2, 0, 3));   // js4
        using var c = new AppController(new AppStore(Tmp("store")), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks.Single().ProfileId)!;

        var (imported, skipped) = c.ImportFromActionMaps(ActionMapsFile.Load(path));
        Assert.Equal(1, imported);
        Assert.Equal(0, skipped);
        Assert.Contains(prof.Binds, b => b.Action == "v_pitch_up" && b.Input == "lshift+button1");
    }

    [Fact]
    public void Inspector_rows_show_modified_bindings_on_their_button_and_edit_the_modifier()
    {
        using var c = new AppController(new AppStore(Tmp("store")), new FakeHub(), new FakeOutput());
        var p = new ControllerProfile { Name = "P" };
        var spec = new ActionSpec(ButtonTrigger.Pressed, "spaceship_movement", "v_pitch_up", null, "lshift");

        c.AddAction(p, "button1", spec);
        var bind = Assert.Single(p.Binds);
        Assert.Equal("lshift+button1", bind.Input);
        var row = Assert.Single(c.ActionsOf(p, "button1"));
        Assert.False(row.ViaApp);
        Assert.Equal("lshift", row.KeyModifier);
        Assert.Empty(c.ActionsOf(p, "button2"));

        // Removing the modifier turns it back into a plain binding of the same action.
        c.UpdateAction(p, "button1", row, row.ToSpec() with { KeyModifier = null });
        Assert.Equal("button1", Assert.Single(p.Binds).Input);
    }

    [Fact]
    public void A_button_can_be_set_as_modifier_and_is_listed_separately()
    {
        using var c = new AppController(new AppStore(Tmp("store")), new FakeHub(), new FakeOutput());
        var p = new ControllerProfile { Name = "P" };

        c.SetKeyModifierButton(p, "button5", "lshift");
        Assert.Equal("lshift", c.KeyModifierOf(p, "button5"));
        Assert.Empty(c.ActionsOf(p, "button5"));
        Assert.Equal(new[] { "button5" }, c.OtherLayerButtons(p, "button6", "lshift"));

        c.SetKeyModifierButton(p, "button5", "lctrl");   // replaces, never stacks
        var a = Assert.Single(p.Actions);
        Assert.Equal(ButtonTrigger.Held, a.Trigger);
        Assert.Equal("lctrl", a.Keys);

        c.SetKeyModifierButton(p, "button5", null);
        Assert.Empty(p.Actions);
    }

    [Fact]
    public void Modifier_button_holds_the_key_and_app_taps_do_not_turn_into_shift_plus_key()
    {
        var hub = new FakeHub();
        var d = new DeviceInfo(Guid.NewGuid(), "Alpha", "Alpha", 1, 1, "path0", 12, 2, 0, 0);
        hub.List.Add(d);
        var output = new FakeOutput();
        using var c = new AppController(new AppStore(Tmp("store")), hub, output);
        c.Engine.TapMilliseconds = 1;
        c.Reconcile(); c.RegisterNewDevices();
        var prof = c.Store.Profile(c.Store.Data.Sticks[0].ProfileId)!;
        c.SetKeyModifierButton(prof, "button1", "lshift");
        c.AddAction(prof, "button2", new ActionSpec(ButtonTrigger.Pressed, null, null, "o"));

        hub.Raise(d.InstanceGuid, "button1", true);
        WaitFor(() => output.Snapshot().Length >= 1);
        Assert.Equal(new[] { "down:2A" }, output.Snapshot());

        output.Log.Clear();
        hub.Raise(d.InstanceGuid, "button2", true);
        WaitFor(() => output.Snapshot().Length >= 4);
        // shift is lifted for the tap and put back afterwards
        Assert.Equal(new[] { "up:2A", "down:18", "up:18", "down:2A" }, output.Snapshot());

        output.Log.Clear();
        hub.Raise(d.InstanceGuid, "button1", false);
        WaitFor(() => output.Snapshot().Length >= 1);
        Assert.Equal(new[] { "up:2A" }, output.Snapshot());
    }
}
