using ScMapper.Core.Model;
using ScMapper.Core.Sc;

namespace ScMapper.Core;

/// <summary>How a stick's js number compares with what Star Citizen wrote into actionmaps.xml at its last run.</summary>
public enum JsStatus
{
    /// <summary>The stick has no js number (a gamepad, say).</summary>
    NoNumber,
    /// <summary>No readable file: nothing to compare with.</summary>
    NoGameInfo,
    /// <summary>The game's file lists this stick's model at this number.</summary>
    Matches,
    /// <summary>Matches, but several sticks share the model, so which one is which is a guess.</summary>
    MatchesAmbiguous,
    /// <summary>The game's file lists another device (or nothing) at this number.</summary>
    DiffersFromGame,
}

/// <summary>One stick's js number and how far it can be trusted.</summary>
public sealed record JsStickState(StickRecord Stick, int? Number, JsStatus Status, string? GameProduct, int? WindowsNumber)
{
    /// <summary>Windows' device order would give another number than the one in use.</summary>
    public bool WindowsDiffers => Number != null && WindowsNumber != null && WindowsNumber != Number;
}

/// <summary>One js number: what the game's file lists there and which sticks Star Button maps to it.</summary>
public sealed record JsSlot(int Number, string? GameProduct, IReadOnlyList<StickRecord> Sticks,
    IReadOnlyList<DeviceInfo>? Waiting = null)
{
    public bool IsDoubled => Sticks.Count > 1;
    /// <summary>Connected devices Windows would put on this number that have no profile yet.</summary>
    public IReadOnlyList<DeviceInfo> WaitingDevices => Waiting ?? Array.Empty<DeviceInfo>();
}

/// <summary>A stick whose bindings are in the game's file under <see cref="InFile"/> but which is <see cref="Now"/>.</summary>
public sealed record JsDriftItem(StickRecord Stick, int InFile, int Now, int Binds);

/// <summary>A stick that changes number.</summary>
public sealed record JsMove(StickRecord Stick, int? From, int To, int Binds, bool Ambiguous = false);

public sealed partial class AppController
{
    /// <summary>Slots shown even when empty, so a stick can be moved to a free number.</summary>
    public const int MinJsSlots = 8;
    public const int MaxJsSlots = 16;

    private static string? CleanProduct(string? product)
    {
        var p = product?.Trim();
        return string.IsNullOrEmpty(p) ? null : System.Text.RegularExpressions.Regex.Replace(p, @"\s+", " ");
    }

    /// <summary>The product name without the "{GUID}" tail, for display.</summary>
    public static string? ProductDisplayName(string? product)
    {
        var p = CleanProduct(product);
        if (p == null) return null;
        int i = p.IndexOf('{');
        return i > 0 ? p[..i].Trim() : p;
    }

    /// <summary>The js numbers 1..n (at least eight) with the game's entry and the sticks mapped to each.</summary>
    public IReadOnlyList<JsSlot> JsSlots(ActionMapsFile? file = null)
    {
        file ??= TryLoadActionMaps();
        var options = file?.JoystickOptions().Where(o => o.Instance > 0)
            .GroupBy(o => o.Instance).ToDictionary(g => g.Key, g => g.First().Product)
            ?? new Dictionary<int, string?>();
        List<StickRecord> sticks;
        lock (_gate) sticks = DeploySticks();
        // A device without a profile still has a number in the game: it must not look free.
        var waiting = UnassignedDevices;
        int max = Math.Max(MinJsSlots, Math.Max(
            Math.Max(sticks.Select(s => s.ScInstance ?? 0).DefaultIfEmpty(0).Max(),
                waiting.Select(d => d.DefaultScInstance).DefaultIfEmpty(0).Max()),
            options.Keys.DefaultIfEmpty(0).Max()));
        max = Math.Min(max, MaxJsSlots);
        var list = new List<JsSlot>();
        for (int n = 1; n <= max; n++)
            list.Add(new JsSlot(n, CleanProduct(options.GetValueOrDefault(n)),
                sticks.Where(s => s.ScInstance == n).ToList(),
                waiting.Where(d => d.DefaultScInstance == n).ToList()));
        return list;
    }

    private ActionMapsFile? TryLoadActionMaps()
    {
        try { return LoadActionMaps(); }
        catch { return null; }   // unreadable or half-written: treat as "no information"
    }

    /// <summary>Compares a stick's number with the game's file.</summary>
    public JsStickState JsState(StickRecord s, ActionMapsFile? file = null)
    {
        file ??= TryLoadActionMaps();
        var dev = DeviceFor(s);
        int? windows = dev is { HasJsNumber: true } ? dev.DefaultScInstance : null;
        if (s.ScInstance is not { } n) return new JsStickState(s, null, JsStatus.NoNumber, null, windows);
        if (file == null) return new JsStickState(s, n, JsStatus.NoGameInfo, null, windows);

        var entry = file.JoystickOptions().FirstOrDefault(o => o.Instance == n);
        var product = CleanProduct(entry.Product);
        var ids = ActionMapsFile.ParseProductIds(product);
        if (ids is { } i && i.Vid == s.Vid && i.Pid == s.Pid)
        {
            int sameModel;
            lock (_gate) sameModel = DeploySticks().Count(o => o.Vid == s.Vid && o.Pid == s.Pid);
            return new JsStickState(s, n, sameModel > 1 ? JsStatus.MatchesAmbiguous : JsStatus.Matches, product, windows);
        }
        return new JsStickState(s, n, JsStatus.DiffersFromGame, product, windows);
    }

    private int BindCount(StickRecord s) => Store.Profile(s.ProfileId)?.Binds.Count ?? 0;

    /// <summary>What swapping two numbers would do. A number nobody holds simply makes the other stick move.</summary>
    public IReadOnlyList<JsMove> PreviewJsSwap(int a, int b)
    {
        if (a == b) return Array.Empty<JsMove>();
        List<StickRecord> sticks;
        lock (_gate) sticks = DeploySticks();
        var moves = new List<JsMove>();
        foreach (var s in sticks.Where(s => s.ScInstance == a)) moves.Add(new JsMove(s, a, b, BindCount(s)));
        foreach (var s in sticks.Where(s => s.ScInstance == b)) moves.Add(new JsMove(s, b, a, BindCount(s)));
        return moves;
    }

    /// <summary>
    /// Gives the sticks on number <paramref name="a"/> number <paramref name="b"/> and the other way round. The
    /// profiles stay as they are; only the jsN prefix of what a deploy writes changes. Both ends become
    /// hand-set, so the automatic order does not move them back.
    /// </summary>
    public IReadOnlyList<JsMove> SwapJsNumbers(int a, int b)
    {
        var moves = PreviewJsSwap(a, b);
        if (moves.Count == 0) return moves;
        lock (_gate)
            foreach (var m in moves)
            {
                m.Stick.ScInstance = m.To;
                m.Stick.ScInstanceManual = true;
            }
        Touch();
        return moves;
    }

    /// <summary>
    /// Pairs the connected sticks with the numbers in Star Citizen's file by model. A stick keeps its number when
    /// the file lists its model there; the rest are paired in Windows' device order. Nothing is changed.
    /// </summary>
    public IReadOnlyList<JsMove> PlanJsFromGame(ActionMapsFile? file = null)
    {
        file ??= TryLoadActionMaps();
        if (file == null) return Array.Empty<JsMove>();
        var entries = file.JoystickOptions().Where(o => o.Instance > 0)
            .Select(o => (o.Instance, Ids: ActionMapsFile.ParseProductIds(o.Product)))
            .Where(o => o.Ids != null).Select(o => (o.Instance, Ids: o.Ids!.Value)).ToList();
        List<(StickRecord Stick, DeviceInfo Dev)> connected;
        lock (_gate)
            connected = Store.Data.Sticks.Select(s => (Stick: s, Dev: DeviceFor(s)))
                .Where(x => x.Dev is { HasJsNumber: true }).Select(x => (Stick: x.Stick, Dev: x.Dev!))
                .OrderBy(x => x.Dev.EnumIndex).ToList();

        var moves = new List<JsMove>();
        foreach (var model in connected.GroupBy(x => (x.Stick.Vid, x.Stick.Pid)))
        {
            var free = entries.Where(e => e.Ids.Vid == model.Key.Vid && e.Ids.Pid == model.Key.Pid)
                .Select(e => e.Instance).OrderBy(i => i).ToList();
            var open = new List<(StickRecord Stick, DeviceInfo Dev)>();
            foreach (var x in model)
            {
                // Already on one of this model's numbers: keep it, so a hand-made correction survives a re-read.
                if (x.Stick.ScInstance is { } cur && free.Remove(cur))
                    moves.Add(new JsMove(x.Stick, cur, cur, BindCount(x.Stick), model.Count() > 1));
                else open.Add(x);
            }
            foreach (var x in open)
            {
                if (free.Count == 0) break;
                int to = free[0];
                free.RemoveAt(0);
                moves.Add(new JsMove(x.Stick, x.Stick.ScInstance, to, BindCount(x.Stick), model.Count() > 1));
            }
        }
        return moves.OrderBy(m => m.To).ToList();
    }

    /// <summary>Applies a plan from <see cref="PlanJsFromGame"/>. Returns how many sticks changed number.</summary>
    public int ApplyJsMoves(IEnumerable<JsMove> moves)
    {
        int changed = 0;
        lock (_gate)
            foreach (var m in moves)
            {
                if (m.Stick.ScInstance != m.To) changed++;
                m.Stick.ScInstance = m.To;
                m.Stick.ScInstanceManual = true;
            }
        Touch();
        return changed;
    }

    /// <summary>Gives up all hand-set numbers: every connected stick follows Windows' device order again.</summary>
    public void FollowWindowsOrder()
    {
        lock (_gate)
            foreach (var s in Store.Data.Sticks)
            {
                s.ScInstanceManual = false;
                var d = DeviceFor(s);
                if (d != null) s.ScInstance = d.HasJsNumber ? d.DefaultScInstance : null;
            }
        Touch();
    }

    /// <summary>
    /// Sticks whose bindings sit under another number in Star Citizen's file than the stick has now: the profile
    /// says "button5 → fire", the file has it as js1_button5, but the stick is js4 today. Read off the bindings
    /// themselves, so nothing has to be remembered from an earlier deploy.
    /// </summary>
    public IReadOnlyList<JsDriftItem> JsDrift(ActionMapsFile? file = null)
    {
        file ??= TryLoadActionMaps();
        if (file == null) return Array.Empty<JsDriftItem>();
        var inputs = new Dictionary<string, HashSet<string>>();
        foreach (var r in file.Rebinds())
        {
            var key = $"{r.Map}/{r.Action}";
            if (!inputs.TryGetValue(key, out var set)) inputs[key] = set = new HashSet<string>();
            set.Add(r.Input);
        }
        List<StickRecord> sticks;
        lock (_gate) sticks = Store.Data.Sticks.Where(s => IsConnected(s) && s.ScInstance != null).ToList();
        var list = new List<JsDriftItem>();
        foreach (var s in sticks)
        {
            var prof = Store.Profile(s.ProfileId);
            if (prof == null || prof.Binds.Count == 0) continue;
            int now = s.ScInstance!.Value, agree = 0;
            var elsewhere = new Dictionary<int, int>();
            foreach (var b in prof.Binds)
            {
                if (b.IsUnbound || !inputs.TryGetValue($"{b.Map}/{b.Action}", out var present)) continue;
                if (present.Contains($"js{now}_{b.Input}")) { agree++; continue; }
                foreach (var other in present)
                {
                    var (n, tail) = SplitJs(other);
                    if (n != null && n != now && tail == b.Input)
                        elsewhere[n.Value] = elsewhere.GetValueOrDefault(n.Value) + 1;
                }
            }
            if (elsewhere.Count == 0) continue;
            var top = elsewhere.OrderByDescending(kv => kv.Value).First();
            if (top.Value > agree) list.Add(new JsDriftItem(s, top.Key, now, top.Value));
        }
        return list;
    }

    private static (int? N, string Tail) SplitJs(string input)
    {
        var m = System.Text.RegularExpressions.Regex.Match(input, @"^js(\d{1,4})_(.+)$");
        return m.Success ? ((int?)int.Parse(m.Groups[1].Value), m.Groups[2].Value) : ((int?)null, input);
    }

}
