using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace ScMapper.Core.Sc;

public sealed record ScInstall(string Root, string Channel)
{
    public string Dir => Path.Combine(Root, Channel);
    public string P4kPath => Path.Combine(Dir, "Data.p4k");
    public string UserClientDir => Path.Combine(Dir, "USER", "Client", "0");
    public string ActionMapsPath => Path.Combine(UserClientDir, "Profiles", "default", "actionmaps.xml");
    public string MappingsDir => Path.Combine(UserClientDir, "Controls", "Mappings");
    public string Label => $"{Channel} - {Root}";
    public override string ToString() => Label;

    private static readonly Dictionary<string, (DateTime Stamp, string? Version)> VersionCache = new();

    /// <summary>The build version from build_manifest.id; re-read only when the file changed.</summary>
    public string? ReadVersion()
    {
        var path = Path.Combine(Dir, "build_manifest.id");
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (VersionCache)
                if (VersionCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Version;
            using var s = File.OpenRead(path);
            using var doc = JsonDocument.Parse(s);
            var version = doc.RootElement.GetProperty("Data").GetProperty("Version").GetString();
            lock (VersionCache) VersionCache[path] = (stamp, version);
            return version;
        }
        catch { return null; }
    }

}

public static class ScLocator
{
    private static readonly string[] Channels = { "LIVE", "PTU", "EPTU", "HOTFIX", "TECH-PREVIEW" };

    public static IReadOnlyList<ScInstall> FindAll(string? explicitRoot = null)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitRoot)) roots.Add(explicitRoot);
        else roots.AddRange(CandidateRoots());

        var list = new List<ScInstall>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var ch in Channels)
            {
                var inst = new ScInstall(root, ch);
                if (File.Exists(inst.P4kPath)) list.Add(inst);
            }
        }
        return list;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var found = new List<string>();
        foreach (var (hive, sub) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sk = key.OpenSubKey(name);
                    var display = sk?.GetValue("DisplayName") as string ?? "";
                    if (!display.Contains("Star Citizen", StringComparison.OrdinalIgnoreCase)
                        && !display.StartsWith("RSI", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var v in new[] { "InstallLocation", "DisplayIcon" })
                    {
                        if (sk!.GetValue(v) is string p && p.Length > 0)
                            found.Add(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                ? Path.GetDirectoryName(p)! : p.Trim('"'));
                    }
                }
            }
            catch { /* registry access is best effort */ }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            foreach (var rel in new[]
                     {
                         @"Program Files\Roberts Space Industries\StarCitizen",
                         @"Roberts Space Industries\StarCitizen", @"Games\StarCitizen", "StarCitizen",
                     })
                found.Add(Path.Combine(drive.RootDirectory.FullName, rel));
        }
        return found.Where(Directory.Exists);
    }

    private static long _checkedAt;
    private static bool _running;

    /// <summary>
    /// Whether the game process exists. The UI asks several times a second, so the answer is reused for a second;
    /// anything that is about to write the game's files asks with <paramref name="fresh"/>.
    /// </summary>
    public static bool IsGameRunning(bool fresh = false)
    {
        long now = Environment.TickCount64;
        if (!fresh && now - Interlocked.Read(ref _checkedAt) < 1000) return _running;
        var procs = Process.GetProcessesByName("StarCitizen");
        try { _running = procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
        Interlocked.Exchange(ref _checkedAt, now);
        return _running;
    }
}
