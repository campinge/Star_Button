using ScMapper.Core.Model;

namespace ScMapper.Core.Sc;

public sealed record DeployResult(int Added, int Removed, string? BackupPath, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ManagedKeys)
{
    /// <summary>The merged file as it would be (or was) written.</summary>
    public ActionMapsFile? Result { get; init; }
    public IReadOnlyList<string> KbmKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GamepadKeys { get; init; } = Array.Empty<string>();
}

/// <summary>Action keys ("map/action") written by earlier deploys, per device class.</summary>
public sealed record ManagedKeys(IEnumerable<string> Joystick, IEnumerable<string> Kbm, IEnumerable<string> Gamepad)
{
    public static readonly ManagedKeys None = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

public static class Deployer
{
    /// <summary>
    /// Resolves logical stick/profile bindings into jsN inputs and merges them, together with the
    /// keyboard/mouse and gamepad layers, into actionmaps.xml.
    /// </summary>
    /// <param name="previouslyManaged">Joystick action keys written by an earlier deploy; their js rebinds are replaced.</param>
    public static DeployResult Apply(string actionMapsPath, string backupDir,
        IEnumerable<StickRecord> sticks, IEnumerable<ControllerProfile> profiles,
        IEnumerable<string> previouslyManaged, bool dryRun = false, ManagedKeys? managed = null,
        IReadOnlyList<(string Map, string Action, string Input)>? unboundDefaults = null)
    {
        var warnings = new List<string>();
        var profileList = profiles.ToList();
        var profileById = profileList.ToDictionary(p => p.Id);
        var file = ActionMapsFile.LoadOrEmpty(actionMapsPath);
        managed ??= new ManagedKeys(previouslyManaged, Array.Empty<string>(), Array.Empty<string>());

        var wanted = new List<(string Map, string Action, string Input, string? Mode)>();
        var usedInstances = new Dictionary<int, string>();
        foreach (var s in sticks)
        {
            if (s.ProfileId is null || !profileById.TryGetValue(s.ProfileId.Value, out var prof)) continue;
            if (prof.IsSystem || prof.Binds.Count == 0) continue;
            if (s.ScInstance is null)
            {
                warnings.Add($"Stick '{s.Name}' has {prof.Binds.Count} binding(s) but no SC instance number - skipped.");
                continue;
            }
            int inst = s.ScInstance.Value;
            if (usedInstances.TryGetValue(inst, out var other))
            {
                warnings.Add($"Sticks '{other}' and '{s.Name}' both use js{inst}.");
                continue;
            }
            usedInstances[inst] = s.Name;
            foreach (var b in prof.Binds)
                wanted.Add((b.Map, b.Action, $"js{inst}_{b.Input}", b.Mode));
        }

        // Same physical input bound twice within one actionmap is almost always a mistake.
        foreach (var g in wanted.Where(w => !w.Input.EndsWith(' ')).GroupBy(w => (w.Map, w.Input)).Where(g => g.Count() > 1))
            warnings.Add($"{g.Key.Input} is bound to {g.Count()} actions in '{g.Key.Map}': " +
                         string.Join(", ", g.Select(x => x.Action)));

        var kbm = Layer(profileList, ProfileKind.KeyboardMouse, ScInputCodes.IsKbm, "Keyboard/mouse", warnings);
        var pad = Layer(profileList, ProfileKind.Gamepad, ScInputCodes.IsGamepad, "Gamepad", warnings);

        var keys = wanted.Select(w => $"{w.Map}/{w.Action}").Distinct().ToList();
        var kbmKeys = kbm.Select(w => $"{w.Map}/{w.Action}").Distinct().ToList();
        var padKeys = pad.Select(w => $"{w.Map}/{w.Action}").Distinct().ToList();
        string? backup = null;

        // Game defaults to switch off ("Empty" profile): one unbound marker per default, except where this
        // profile binds that very device itself.
        var cancel = new List<(string Map, string Action, string Input)>();
        if (unboundDefaults != null)
        {
            static string Device(string input)
            {
                int i = input.IndexOf('_');
                return i > 0 ? input[..i] : input;
            }
            var taken = wanted.Select(w => (w.Map, w.Action, Device(w.Input)))
                .Concat(kbm.Concat(pad).Select(w => (w.Map, w.Action, Device(w.Input)))).ToHashSet();
            foreach (var u in unboundDefaults)
                if (!taken.Contains((u.Map, u.Action, Device(u.Input)))) cancel.Add(u);
            foreach (var u in cancel)
            {
                var key = $"{u.Map}/{u.Action}";
                var list = ScInputCodes.IsGamepad(u.Input) ? padKeys : ScInputCodes.IsKbm(u.Input) ? kbmKeys : keys;
                if (!list.Contains(key)) list.Add(key);
            }
            // Whatever else the file binds (set in the game) goes too: this profile is the whole truth.
            foreach (var r in file.Rebinds().Where(r => !r.IsUnbound))
            {
                var key = $"{r.Map}/{r.Action}";
                var list = ScInputCodes.IsGamepad(r.Input) ? padKeys : ScInputCodes.IsKbm(r.Input) ? kbmKeys : keys;
                if (!list.Contains(key)) list.Add(key);
            }
        }

        Func<string, bool> isJs = i => ActionCatalog.JsInput.IsMatch(i);
        // Keys this app wrote before are replaced as a whole - their unbound markers included, otherwise a
        // marker of an earlier profile would outlive a switch to another one.
        int removed = file.RemoveRebinds(managed.Joystick.Concat(keys), isJs, includeUnboundMarkers: true);
        removed += file.RemoveRebinds(managed.Kbm.Concat(kbmKeys), ScInputCodes.IsKbm, includeUnboundMarkers: true);
        removed += file.RemoveRebinds(managed.Gamepad.Concat(padKeys), ScInputCodes.IsGamepad, includeUnboundMarkers: true);
        // An explicit "unbound" marker of the same device class would fight a real binding: drop it.
        file.RemoveRebinds(keys, isJs, includeUnboundMarkers: true);
        file.RemoveRebinds(kbmKeys, ScInputCodes.IsKbm, includeUnboundMarkers: true);
        file.RemoveRebinds(padKeys, ScInputCodes.IsGamepad, includeUnboundMarkers: true);
        foreach (var w in wanted.Concat(kbm).Concat(pad)) file.AddRebind(w.Map, w.Action, w.Input, w.Mode);
        foreach (var u in cancel) file.AddRebind(u.Map, u.Action, u.Input);

        foreach (var s in sticks.Where(s => s.ScInstance is not null))
        {
            var product = ActionMapsFile.ProductString(s.ProductName, s.Vid, s.Pid);
            var existing = file.JoystickOptions().FirstOrDefault(o => o.Instance == s.ScInstance);
            var guidPart = product[product.IndexOf('{')..];
            if (existing.Product == null || !existing.Product.EndsWith(guidPart, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Product != null)
                    warnings.Add($"js{s.ScInstance} was '{existing.Product.Trim()}' in actionmaps.xml, now '{s.ProductName}'.");
                file.SetJoystickOption(s.ScInstance!.Value, product);
            }
        }

        if (!dryRun)
        {
            if (File.Exists(actionMapsPath))
            {
                Directory.CreateDirectory(backupDir);
                backup = Path.Combine(backupDir, $"actionmaps_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
                File.Copy(actionMapsPath, backup, overwrite: false);
            }
            file.Save(actionMapsPath);
        }
        return new DeployResult(wanted.Count + kbm.Count + pad.Count + cancel.Count, removed, backup, warnings, keys)
        {
            KbmKeys = kbmKeys,
            GamepadKeys = padKeys,
            Result = file,
        };
    }

    private static List<(string Map, string Action, string Input, string? Mode)> Layer(
        List<ControllerProfile> profiles, ProfileKind kind, Func<string, bool> valid, string label, List<string> warnings)
    {
        var list = new List<(string, string, string, string?)>();
        var prof = profiles.FirstOrDefault(p => p.Kind == kind);
        if (prof == null) return list;
        foreach (var b in prof.Binds)
        {
            if (!valid(b.Input))
            {
                warnings.Add($"{label}: '{b.Input}' for {b.Action} is not a valid input code - skipped.");
                continue;
            }
            list.Add((b.Map, b.Action, b.Input, b.Mode));
        }
        foreach (var g in list.Where(w => !w.Item3.EndsWith(' ')).GroupBy(w => (w.Item1, w.Item3)).Where(g => g.Count() > 1))
            warnings.Add($"{label}: {g.Key.Item2} is bound to {g.Count()} actions in '{g.Key.Item1}': " +
                         string.Join(", ", g.Select(x => x.Item2)));
        return list;
    }
}
