using System.Text.Json;
using ScMapper.Core.Model;

namespace ScMapper.Core.Storage;

public sealed class AppData
{
    public List<StickRecord> Sticks { get; set; } = new();
    /// <summary>Stick layouts (kind Stick). Older versions also kept the keyboard/gamepad layers here.</summary>
    public List<ControllerProfile> Profiles { get; set; } = new();
    public List<Setup> Setups { get; set; } = new();
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
                    Data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(DataFile)) ?? new AppData();
            }
            catch
            {
                // Keep a copy of an unreadable file instead of silently overwriting it.
                File.Copy(DataFile, DataFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true);
                Data = new AppData();
            }
        }
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

    public static string ExportProfile(ControllerProfile p) => JsonSerializer.Serialize(p, Json);

    public ControllerProfile ImportProfile(string json)
    {
        var p = JsonSerializer.Deserialize<ControllerProfile>(json) ?? throw new InvalidDataException("Empty profile");
        p.Id = Guid.NewGuid();
        if (Data.Profiles.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) p.Name += " (imported)";
        Data.Profiles.Add(p);
        Save();
        return p;
    }
}
