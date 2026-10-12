using System.Text.RegularExpressions;
using ScMapper.Core.Sc;

namespace ScMapper.Core.Model;

/// <summary>
/// The device a stick profile was last used on. Kept next to the profiles (not inside them), so refreshing it
/// never counts as an unsaved change of a setup.
/// </summary>
public sealed record ProfileDevice(int Vid, int Pid, string ProductName, int Buttons, int Axes, int Hats)
{
    public static ProfileDevice Of(StickRecord s) => new(s.Vid, s.Pid, s.ProductName.Trim(), s.Buttons, s.Axes, s.Hats);

    public bool SameModel(int vid, int pid) => Vid == vid && Pid == pid;
}

public enum FitLevel
{
    Fits,
    /// <summary>Works technically, but the profile was made for another stick model.</summary>
    DifferentModel,
    /// <summary>The profile uses buttons, hats or axes the device does not have.</summary>
    MissingInputs,
}

/// <summary>How well a profile suits a device. Never forbids anything: the user may accept a mismatch.</summary>
public sealed record ProfileFit(FitLevel Level, IReadOnlyList<string> Missing, ProfileDevice? Origin)
{
    public bool NeedsConfirmation => Level != FitLevel.Fits;
}

public static class ProfileFitCheck
{
    private static readonly Regex Button = new(@"^button(\d{1,6})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Hat = new(@"^hat(\d{1,6})(_.+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every physical input the profile does something with ("button5", "lshift+hat1_up", "x").</summary>
    public static IReadOnlyList<string> UsedInputs(ControllerProfile p) =>
        p.Binds.Select(b => b.Input)
            .Concat(p.Rules.Keys)
            .Concat(p.Actions.Select(a => a.Input))
            .Select(i => i.Trim())
            .Where(i => i.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Missing inputs weigh more than a different model. Only the model decides "different": VKB sticks report
    /// another button count after a configuration change, which is no reason to warn on its own.
    /// A profile without any input fits everything.
    /// </summary>
    public static ProfileFit Check(ControllerProfile p, ProfileDevice? origin, DeviceInfo d)
    {
        var used = UsedInputs(p);
        var missing = new List<string>();
        foreach (var raw in used)
        {
            var (_, input) = ScInputCodes.SplitJsInput(raw);
            if (IsMissing(input, d) && !missing.Contains(input, StringComparer.OrdinalIgnoreCase)) missing.Add(input);
        }
        if (missing.Count > 0) return new ProfileFit(FitLevel.MissingInputs, missing, origin);
        if (used.Count > 0 && origin != null && !origin.SameModel(d.Vid, d.Pid))
            return new ProfileFit(FitLevel.DifferentModel, Array.Empty<string>(), origin);
        return new ProfileFit(FitLevel.Fits, Array.Empty<string>(), origin);
    }

    private static bool IsMissing(string input, DeviceInfo d)
    {
        var m = Button.Match(input);
        if (m.Success) return int.Parse(m.Groups[1].Value) > d.Buttons;
        m = Hat.Match(input);
        if (m.Success) return int.Parse(m.Groups[1].Value) > d.Hats;
        int axis = Array.FindIndex(AppController.AxisInputNames, n => n.Equals(input, StringComparison.OrdinalIgnoreCase));
        // A device that does not list its axis slots is "unknown", not "has none".
        return axis >= 0 && d.AxisSlots.Count > 0 && !d.AxisSlots.Contains(axis);
    }
}
