namespace ScMapper.Core.Sc;

/// <summary>Names and retention of the actionmaps.xml backups ("actionmaps_yyyyMMdd_HHmmss[_n].xml").</summary>
public static class BackupFiles
{
    /// <summary>How many backups are kept; older ones are removed after a new one is made.</summary>
    public const int Keep = 50;

    /// <summary>A path in <paramref name="dir"/> that does not exist yet, so two backups in one second never collide.</summary>
    public static string NewPath(string dir, DateTime? now = null)
    {
        Directory.CreateDirectory(dir);
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(dir, $"actionmaps_{stamp}.xml");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"actionmaps_{stamp}_{i}.xml");
        return path;
    }

    /// <summary>The time a backup was made, from its file name; null when the name has another shape.</summary>
    public static DateTime? StampOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "actionmaps_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length < prefix.Length + 15) return null;
        return DateTime.TryParseExact(name.Substring(prefix.Length, 15), "yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
    }

    /// <summary>Removes all but the newest <paramref name="keep"/> backups. Never throws.</summary>
    public static void Prune(string dir, int keep = Keep)
    {
        try
        {
            foreach (var old in Directory.GetFiles(dir, "actionmaps_*.xml").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
            {
                try { File.Delete(old); } catch { /* in use: next time */ }
            }
        }
        catch { /* retention is housekeeping, never a reason to fail a deploy */ }
    }
}
