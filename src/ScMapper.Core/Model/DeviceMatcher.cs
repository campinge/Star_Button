namespace ScMapper.Core.Model;

public sealed record MatchResult(
    IReadOnlyDictionary<Guid, DeviceInfo> Matched,
    IReadOnlyList<DeviceInfo> Unmatched,
    IReadOnlyList<StickRecord> Missing);

/// <summary>
/// Pairs connected devices with saved sticks. Without serial numbers this is best effort:
/// instance GUID, then interface path, then a unique VID/PID. Anything left is reported so
/// the user can map it by hand (the "remap" flow).
/// </summary>
public static class DeviceMatcher
{
    public static MatchResult Match(IEnumerable<StickRecord> saved, IReadOnlyList<DeviceInfo> devices)
    {
        var matched = new Dictionary<Guid, DeviceInfo>();
        var freeDevices = devices.ToList();
        var freeSticks = saved.ToList();

        void Pair(StickRecord s, DeviceInfo d)
        {
            matched[s.Id] = d;
            freeDevices.Remove(d);
            freeSticks.Remove(s);
        }

        foreach (var s in freeSticks.ToList())
        {
            var d = s.InstanceGuid is { } g ? freeDevices.FirstOrDefault(x => x.InstanceGuid == g) : null;
            if (d != null) Pair(s, d);
        }
        foreach (var s in freeSticks.ToList())
        {
            if (string.IsNullOrEmpty(s.InterfacePath)) continue;
            var d = freeDevices.FirstOrDefault(x =>
                string.Equals(x.InterfacePath, s.InterfacePath, StringComparison.OrdinalIgnoreCase));
            if (d != null) Pair(s, d);
        }
        foreach (var s in freeSticks.ToList())
        {
            var sameModelSticks = freeSticks.Count(x => x.Vid == s.Vid && x.Pid == s.Pid);
            var candidates = freeDevices.Where(x => x.Vid == s.Vid && x.Pid == s.Pid).ToList();
            if (sameModelSticks == 1 && candidates.Count == 1) Pair(s, candidates[0]);
        }
        return new MatchResult(matched, freeDevices, freeSticks);
    }

    /// <summary>Copies identity hints from the live device onto the saved record.</summary>
    public static void Refresh(StickRecord s, DeviceInfo d)
    {
        s.InstanceGuid = d.InstanceGuid;
        s.InterfacePath = d.InterfacePath;
        s.ProductName = d.ProductName;
        s.Vid = d.Vid; s.Pid = d.Pid;
        s.Buttons = d.Buttons; s.Axes = d.Axes; s.Hats = d.Hats;
        s.LastSeenUtc = DateTime.UtcNow;
    }
}
