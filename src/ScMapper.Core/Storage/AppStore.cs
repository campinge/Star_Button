using System.Text.Json;
using ScMapper.Core.Model;

namespace ScMapper.Core.Storage;

public sealed class AppData
{
    public List<StickRecord> Sticks { get; set; } = new();
    /// <summary>Stick layouts (kind Stick). Older versions also kept the keyboard/gamepad layers here.</summary>
    public List<ControllerProfile> Profiles { get; set; } = new();
    public List<Setup> Setups { get; set; } = new();
    /// <summary>Profile id → the device it was last used on (see <see cref="ProfileDevice"/>).</summary>
    public Dictionary<Guid, ProfileDevice> ProfileDevices { get; set; } = new();
    public Guid? ActiveSetupId { get; set; }
    /// <summary>Action keys ("map/action") written to actionmaps.xml by the last deploy.</summary>
    public List<string> ManagedKeys { get; set; } = new();
    public List<string> ManagedKbmKeys { get; set; } = new();
    public List<string> ManagedGamepadKeys { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

/// <summary>JSON persistence in %APPDATA%\StarButton. Sticks and profiles are stored together but independent.</summary>
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string Dir { get; }
    public string DataFile => Path.Combine(Dir, "data.json");
    public string BackupDir => Path.Combine(Dir, "backups");
    public string CacheDir => Path.Combine(Dir, "cache");
    public AppData Data { get; private set; } = new();

    public AppStore(string? dir = null)
    {
        Dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StarButton");
        Directory.CreateDirectory(Dir);
        Load();
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(DataFile))
                    Data = Normalize(JsonSerializer.Deserialize<AppData>(File.ReadAllText(DataFile)) ?? new AppData());
            }
            catch
            {
                // Keep a copy of an unreadable file instead of silently overwriting it.
                try { File.Copy(DataFile, DataFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
                Data = new AppData();
            }
        }
    }

    /// <summary>
    /// Valid JSON can still have the wrong shape (a hand edit, another tool, an older version): nulls where lists
    /// are expected, repeated ids. Everything the rest of the app takes for granted is made true here, so a
    /// damaged file degrades to "that part is empty" and never to an app that cannot start.
    /// </summary>
    public static AppData Normalize(AppData d)
    {
        d.Sticks ??= new();
        d.Profiles ??= new();
        d.Setups ??= new();
        d.ProfileDevices ??= new();
        d.ManagedKeys ??= new();
        d.ManagedKbmKeys ??= new();
        d.ManagedGamepadKeys ??= new();
        d.Settings ??= new();
        d.Settings.Channel ??= "LIVE";
        d.Settings.Theme ??= "kartell";

        d.Sticks.RemoveAll(s => s == null);
        d.Profiles.RemoveAll(p => p == null);
        d.Setups.RemoveAll(s => s == null);

        void Fix(ControllerProfile p, ProfileKind? kind = null, string fallbackName = "Profile")
        {
            if (kind != null) p.Kind = kind.Value;
            p.Name ??= fallbackName;
            p.Rules ??= new();
            p.Binds ??= new();
            p.Actions ??= new();
            p.Binds.RemoveAll(b => b == null);
            p.Actions.RemoveAll(a => a == null);
            foreach (var b in p.Binds) { b.Map ??= ""; b.Action ??= ""; b.Input ??= ""; }
            foreach (var a in p.Actions) a.Input ??= "";
            foreach (var k in p.Rules.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) p.Rules.Remove(k);
        }

        // Ids are identities: a repeated one would make every ToDictionary / lookup in the app ambiguous.
        var seen = new HashSet<Guid>();
        foreach (var p in d.Profiles)
        {
            Fix(p);
            if (!seen.Add(p.Id)) p.Id = Guid.NewGuid();
        }
        seen.Clear();
        foreach (var s in d.Sticks)
        {
            s.Name ??= "";
            s.ProductName ??= "";
            if (!seen.Add(s.Id)) s.Id = Guid.NewGuid();
        }
        seen.Clear();
        foreach (var s in d.Setups)
        {
            s.Name ??= "Profile";
            s.Keyboard ??= new ControllerProfile { Name = "Keyboard & Mouse" };
            s.Gamepad ??= new ControllerProfile { Name = "Gamepad" };
            Fix(s.Keyboard, ProfileKind.KeyboardMouse, "Keyboard & Mouse");
            Fix(s.Gamepad, ProfileKind.Gamepad, "Gamepad");
            s.StickLayouts ??= new();
            s.IgnoredConflicts ??= new();
            if (!string.IsNullOrEmpty(s.SavedState))
            {
                try { using var _ = JsonDocument.Parse(s.SavedState); }
                catch (JsonException) { s.SavedState = null; }
            }
            if (!seen.Add(s.Id)) s.Id = Guid.NewGuid();
        }
        return d;
    }

    public void Save()
    {
        lock (_gate)
        {
            var tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, DataFile, overwrite: true);
        }
    }

    public ControllerProfile? Profile(Guid? id) => id is null ? null : Data.Profiles.FirstOrDefault(p => p.Id == id);

}
