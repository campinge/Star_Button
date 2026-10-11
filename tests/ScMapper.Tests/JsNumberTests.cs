using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;
using ScMapper.Core.Sc;
using ScMapper.Core.Storage;
using Xunit;

namespace ScMapper.Tests;

public class JsNumberTests
{
    // What Star Citizen wrote: three sticks of one model and two VKB sticks.
    private const string GameFile = """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <options type="joystick" instance="1" Product="Generic USB Joystick  {E0010810-0000-0000-0000-504944564944}"/>
 <options type="joystick" instance="2" Product=" VKB-Sim Gladiator NXT L    {0201231D-0000-0000-0000-504944564944}"/>
 <options type="joystick" instance="3" Product="Generic USB Joystick  {E0010810-0000-0000-0000-504944564944}"/>
 <options type="joystick" instance="4" Product=" VKBsim Gladiator NXT R    {0200231D-0000-0000-0000-504944564944}"/>
 <options type="joystick" instance="5"/>
</ActionProfiles></ActionMaps>
""";

    private static string Tmp(string name) =>
        Path.Combine(Path.GetTempPath(), "starbutton-tests", Guid.NewGuid().ToString("N"), name);

    private static ActionMapsFile File_()
    {
        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, GameFile);
        return ActionMapsFile.Load(path);
    }

    private static DeviceInfo Dev(int idx, int vid, int pid, string name) =>
        new(Guid.NewGuid(), name, name, vid, pid, $"path{idx}", 12, 2, 0, idx);

    private static (AppController C, FakeHub Hub) Make(params DeviceInfo[] devices)
    {
        var hub = new FakeHub();
        hub.List.AddRange(devices);
        var c = new AppController(new AppStore(Tmp("store")), hub, new FakeOutput());
        c.Reconcile(); c.RegisterNewDevices();
        return (c, hub);
    }

    private static StickRecord Stick(AppController c, DeviceInfo d) =>
        c.Store.Data.Sticks.First(s => c.DeviceFor(s)?.InstanceGuid == d.InstanceGuid);

    [Fact]
    public void Product_ids_are_read_from_the_game_string()
    {
        Assert.Equal((0x231D, 0x0201), ActionMapsFile.ParseProductIds(" VKB-Sim Gladiator NXT L    {0201231D-0000-0000-0000-504944564944}"));
        Assert.Equal((0x0810, 0xE001), ActionMapsFile.ParseProductIds("Generic USB Joystick  {E0010810-0000-0000-0000-504944564944}"));
        Assert.Null(ActionMapsFile.ParseProductIds("Tastatur  {6F1D2B61-D5A0-11CF-BFC7-444553540000}"));
        Assert.Null(ActionMapsFile.ParseProductIds(null));
        // The strings the app writes read back.
        Assert.Equal((0x231D, 0x0201), ActionMapsFile.ParseProductIds(ActionMapsFile.ProductString("X", 0x231D, 0x0201)));
    }

    [Fact]
    public void Swap_exchanges_the_numbers_of_two_sticks_and_keeps_the_profiles()
    {
        var a = Dev(0, 0x231D, 0x0201, "VKB-Sim Gladiator NXT L");
        var b = Dev(1, 0x231D, 0x0200, "VKBsim Gladiator NXT R");
        var (c, _) = Make(a, b);
        using var _c = c;
        var left = Stick(c, a);
        var right = Stick(c, b);
        var leftProfile = left.ProfileId;
        c.SetBind(c.Store.Profile(left.ProfileId)!, "m", "fire", "button1");
        Assert.Equal(1, left.ScInstance);
        Assert.Equal(2, right.ScInstance);

        var moves = c.SwapJsNumbers(1, 2);

        Assert.Equal(2, moves.Count);
        Assert.Equal(2, left.ScInstance);
        Assert.Equal(1, right.ScInstance);
        Assert.True(left.ScInstanceManual && right.ScInstanceManual);
        Assert.Equal(leftProfile, left.ProfileId);
        Assert.Equal("button1", c.Store.Profile(left.ProfileId)!.Binds.Single().Input);   // profile untouched

        // A later device refresh must not undo a hand-set number.
        c.Reconcile(); c.RegisterNewDevices();
        Assert.Equal(2, left.ScInstance);
        Assert.Equal(1, right.ScInstance);
    }

    [Fact]
    public void Swap_with_a_free_number_moves_the_stick_and_equal_numbers_do_nothing()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var (c, _) = Make(a);
        using var _c = c;
        var s = Stick(c, a);

        Assert.Empty(c.SwapJsNumbers(3, 3));
        Assert.Empty(c.SwapJsNumbers(5, 6));           // nobody there
        Assert.Equal(1, s.ScInstance);

        var moves = c.SwapJsNumbers(1, 6);
        Assert.Single(moves);
        Assert.Equal(6, s.ScInstance);
    }

    [Fact]
    public void Preview_does_not_change_anything()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var b = Dev(1, 2, 2, "Beta");
        var (c, _) = Make(a, b);
        using var _c = c;
        var preview = c.PreviewJsSwap(1, 2);
        Assert.Equal(2, preview.Count);
        Assert.Equal(1, Stick(c, a).ScInstance);
        Assert.False(Stick(c, a).ScInstanceManual);
    }

    [Fact]
    public void Numbers_are_taken_from_the_game_file_by_model()
    {
        // Windows lists the sticks differently from the game's last run.
        var vklLeft = Dev(0, 0x231D, 0x0201, "VKB-Sim Gladiator NXT L");
        var genA = Dev(1, 0x0810, 0xE001, "Generic USB Joystick");
        var genB = Dev(2, 0x0810, 0xE001, "Generic USB Joystick");
        var (c, _) = Make(vklLeft, genA, genB);
        using var _c = c;
        Assert.Equal(1, Stick(c, vklLeft).ScInstance);
        Assert.Equal(2, Stick(c, genA).ScInstance);
        Assert.Equal(3, Stick(c, genB).ScInstance);

        var plan = c.PlanJsFromGame(File_());

        Assert.Equal(3, plan.Count);
        Assert.Equal(2, plan.Single(m => m.Stick == Stick(c, vklLeft)).To);      // unique model: certain
        Assert.False(plan.Single(m => m.Stick == Stick(c, vklLeft)).Ambiguous);
        // The two generics: genB already sits on a generic number (3) and keeps it, genA takes the other (1).
        Assert.Equal(3, plan.Single(m => m.Stick == Stick(c, genB)).To);
        Assert.Equal(1, plan.Single(m => m.Stick == Stick(c, genA)).To);
        Assert.All(plan.Where(m => m.Stick != Stick(c, vklLeft)), m => Assert.True(m.Ambiguous));

        Assert.Equal(2, c.ApplyJsMoves(plan.Where(m => m.From != m.To)));
        Assert.Equal(2, Stick(c, vklLeft).ScInstance);
        Assert.Equal(1, Stick(c, genA).ScInstance);
        Assert.True(Stick(c, genA).ScInstanceManual);
    }

    [Fact]
    public void Reading_twice_is_stable_and_a_manual_twin_swap_survives()
    {
        var genA = Dev(0, 0x0810, 0xE001, "Generic USB Joystick");
        var genB = Dev(1, 0x0810, 0xE001, "Generic USB Joystick");
        var (c, _) = Make(genA, genB);
        using var _c = c;
        var file = File_();
        c.ApplyJsMoves(c.PlanJsFromGame(file));            // genA=1, genB=3
        Assert.Equal(1, Stick(c, genA).ScInstance);
        Assert.Equal(3, Stick(c, genB).ScInstance);

        c.SwapJsNumbers(1, 3);                              // the user knows it is the other way round
        c.ApplyJsMoves(c.PlanJsFromGame(file));             // reading again keeps that choice
        Assert.Equal(3, Stick(c, genA).ScInstance);
        Assert.Equal(1, Stick(c, genB).ScInstance);
    }

    [Fact]
    public void State_compares_with_the_game_file()
    {
        var left = Dev(0, 0x231D, 0x0201, "VKB-Sim Gladiator NXT L");
        var right = Dev(1, 0x231D, 0x0200, "VKBsim Gladiator NXT R");
        var gen = Dev(2, 0x0810, 0xE001, "Generic USB Joystick");
        var (c, _) = Make(left, right, gen);
        using var _c = c;
        var file = File_();
        // Windows now: L=js1, R=js2, Generic=js3. The game's file says L=2, R=4, Generic=1/3.
        Assert.Equal(JsStatus.DiffersFromGame, c.JsState(Stick(c, left), file).Status);   // js1 is a generic in the file
        Assert.Equal(JsStatus.DiffersFromGame, c.JsState(Stick(c, right), file).Status);  // js2 is the left stick
        Assert.Equal(JsStatus.Matches, c.JsState(Stick(c, gen), file).Status);             // js3 is a generic

        c.SwapJsNumbers(1, 2);   // L=js2 now
        var st = c.JsState(Stick(c, left), file);
        Assert.Equal(JsStatus.Matches, st.Status);
        Assert.True(st.WindowsDiffers);                                                    // Windows would say js1
        Assert.Equal(1, st.WindowsNumber);
    }

    [Fact]
    public void Drift_is_read_from_the_bindings_in_the_game_file()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var b = Dev(1, 2, 2, "Beta");
        var (c, _) = Make(a, b);
        using var _c = c;
        var alpha = Stick(c, a);   // js1
        var beta = Stick(c, b);    // js2
        c.SetBind(c.Store.Profile(alpha.ProfileId)!, "m", "fire", "button5");
        c.SetBind(c.Store.Profile(beta.ProfileId)!, "m", "pitch", "y");

        var path = Tmp("actionmaps.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
<ActionMaps><ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
 <actionmap name="m">
  <action name="fire"><rebind input="js1_button5"/></action>
  <action name="pitch"><rebind input="js2_y"/></action>
 </actionmap>
</ActionProfiles></ActionMaps>
""");
        // Deployed as js1/js2 and still the same: no drift.
        Assert.Empty(c.JsDrift(ActionMapsFile.Load(path)));

        // Windows lists them the other way round now: the file holds Alpha's bindings under js1, Alpha is js2.
        c.SwapJsNumbers(1, 2);
        var drift = c.JsDrift(ActionMapsFile.Load(path));
        Assert.Equal(2, drift.Count);
        var d = drift.Single(x => x.Stick == alpha);
        Assert.Equal(1, d.InFile);
        Assert.Equal(2, d.Now);
        Assert.Equal(1, d.Binds);
    }

    [Fact]
    public void Follow_windows_order_drops_manual_numbers()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var b = Dev(1, 2, 2, "Beta");
        var (c, _) = Make(a, b);
        using var _c = c;
        c.SwapJsNumbers(1, 2);
        Assert.Equal(2, Stick(c, a).ScInstance);

        c.FollowWindowsOrder();

        Assert.Equal(1, Stick(c, a).ScInstance);
        Assert.Equal(2, Stick(c, b).ScInstance);
        Assert.False(Stick(c, a).ScInstanceManual);
    }

    [Fact]
    public void An_unplugged_stick_is_inactive_even_with_a_hand_set_number()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var b = Dev(1, 2, 2, "Beta");
        var (c, hub) = Make(a, b);
        using var _c = c;
        var alpha = Stick(c, a);
        alpha.ScInstance = 3;
        alpha.ScInstanceManual = true;
        Assert.Contains(alpha, c.JsSlots(File_()).SelectMany(s => s.Sticks));

        hub.List.Remove(a);
        c.Reconcile(); c.RegisterNewDevices();

        Assert.False(c.IsConnected(alpha));
        Assert.DoesNotContain(alpha, c.JsSlots(File_()).SelectMany(s => s.Sticks));
    }

    [Fact]
    public void A_stick_is_labelled_with_its_profile_name()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var (c, _) = Make(a);
        using var _c = c;
        var stick = Stick(c, a);
        Assert.Equal("Alpha", c.StickLabel(stick));

        c.RenameProfile(c.Store.Profile(stick.ProfileId)!, "Power und Comms");

        Assert.Equal("Power und Comms", c.StickLabel(stick));
        Assert.Equal("Alpha", stick.Name);
    }

    [Fact]
    public void Slots_list_the_game_entries_and_flag_a_doubled_number()
    {
        var a = Dev(0, 1, 1, "Alpha");
        var b = Dev(1, 2, 2, "Beta");
        var (c, _) = Make(a, b);
        using var _c = c;
        Stick(c, a).ScInstance = 2;
        Stick(c, a).ScInstanceManual = true;

        var slots = c.JsSlots(File_());
        Assert.Equal(8, slots.Count);
        Assert.True(slots[1].IsDoubled);                       // js2: both sticks
        Assert.Empty(slots[0].Sticks);                          // js1 is free now
        Assert.Contains("VKB-Sim Gladiator NXT L", slots[1].GameProduct);
        Assert.Null(slots[4].GameProduct);                      // empty entry in the file
    }
}
