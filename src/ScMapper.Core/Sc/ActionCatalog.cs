using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScMapper.Core.Sc;

public sealed class ActionDef
{
    public string Map { get; set; } = "";
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ActivationMode { get; set; }
    public bool OnPress { get; set; }
    public bool OnRelease { get; set; }
    public bool OnHold { get; set; }
    /// <summary>The default profile declares a joystick slot for this action (it can be bound to a stick).</summary>
    public bool Joystick { get; set; }
    public bool Keyboard { get; set; }
    public bool Mouse { get; set; }
    public bool Gamepad { get; set; }
    public string DefaultKeyboard { get; set; } = "";
    public string DefaultJoystick { get; set; } = "";
    public string DefaultGamepad { get; set; } = "";
    public string DefaultMouse { get; set; } = "";
    public string Key => $"{Map}/{Name}";
}

public sealed class ActionGroup
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Category { get; set; } = "";
    public List<ActionDef> Actions { get; set; } = new();
}

public sealed class ActionCatalog
{
    public string BuildVersion { get; set; } = "";
    public List<ActionGroup> Groups { get; set; } = new();
    /// <summary>Activation modes seen in the default profile, most frequent first.</summary>
    public List<string> ActivationModes { get; set; } = new();

    public IEnumerable<ActionDef> AllActions => Groups.SelectMany(g => g.Actions);

    /// <summary>All gamepad input tokens that appear in the defaults (e.g. "shoulderl", "dpad_up").</summary>
    public IReadOnlyList<string> GamepadTokens() => AllActions
        .SelectMany(a => a.DefaultGamepad.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct().OrderBy(t => t).ToList();

    public ActionDef? Find(string map, string action) =>
        Groups.FirstOrDefault(g => g.Name == map)?.Actions.FirstOrDefault(a => a.Name == action);

    // ---- parsing -------------------------------------------------------------------

    public static ActionCatalog FromDefaultProfile(XDocument profile, IReadOnlyDictionary<string, string> loc, string version)
    {
        string L(string? v, string fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            if (v[0] != '@') return v;
            return loc.TryGetValue(v[1..], out var t) ? t : fallback;
        }

        var cat = new ActionCatalog { BuildVersion = version };
        var modes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var am in profile.Root!.Descendants("actionmap"))
        {
            var name = (string?)am.Attribute("name") ?? "";
            if (name.Length == 0) continue;
            var group = new ActionGroup
            {
                Name = name,
                Label = Pretty(L((string?)am.Attribute("UILabel"), Humanize(name))),
                Category = L((string?)am.Attribute("UICategory"), CategoryFallback(name, loc)),
            };
            foreach (var a in am.Elements("action"))
            {
                var an = (string?)a.Attribute("name") ?? "";
                if (an.Length == 0) continue;
                string? mode = Attr(a, "activationMode");
                if (mode != null) modes[mode] = modes.GetValueOrDefault(mode) + 1;
                group.Actions.Add(new ActionDef
                {
                    Map = name,
                    Name = an,
                    Label = L((string?)a.Attribute("UILabel"), Humanize(an)),
                    Description = L((string?)a.Attribute("UIDescription"), ""),
                    ActivationMode = mode,
                    OnPress = Attr(a, "onPress") == "1",
                    OnRelease = Attr(a, "onRelease") == "1",
                    OnHold = Attr(a, "onHold") == "1",
                    Joystick = a.Attribute("joystick") != null,
                    Keyboard = a.Attribute("keyboard") != null,
                    Mouse = a.Attribute("mouse") != null,
                    Gamepad = a.Attribute("gamepad") != null,
                    DefaultKeyboard = (string?)a.Attribute("keyboard") ?? "",
                    DefaultJoystick = ((string?)a.Attribute("joystick") ?? "").Trim(),
                    DefaultGamepad = ((string?)a.Attribute("gamepad") ?? "").Trim(),
                    DefaultMouse = ((string?)a.Attribute("mouse") ?? "").Trim(),
                });
            }
            if (group.Actions.Count > 0) cat.Groups.Add(group);
        }
        cat.ActivationModes = modes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        return cat;
    }

    /// <summary>Groups without a UICategory are filed by the prefix of their internal name.</summary>
    private static string CategoryFallback(string map, IReadOnlyDictionary<string, string> loc)
    {
        string T(string key, string dflt) => loc.TryGetValue(key, out var v) ? v : dflt;
        var m = map.ToLowerInvariant();
        if (m.StartsWith("spaceship") || m.StartsWith("turret")) return T("ui_CCSpaceFlight", "FLIGHT");
        if (m.StartsWith("vehicle")) return T("ui_CCVehicle", "Vehicle");
        if (m.StartsWith("eva")) return T("ui_CCEVA", "E.V.A.");
        if (m.StartsWith("player") || m.StartsWith("fps") || m.StartsWith("prone")) return T("ui_CCFPS", "ON FOOT");
        if (m.StartsWith("seat")) return T("ui_CCSeatGeneral", "Seats");
        return "Other";
    }

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string Pretty(string s) => s.Length > 0 && s.All(c => !char.IsLower(c) || !char.IsLetter(c))
        ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())
        : s;

    private static string Humanize(string internalName) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(internalName.Replace('_', ' '));

    /// <summary>Parses global.ini ("key=value", keys may end in ",P").</summary>
    public static Dictionary<string, string> ParseIni(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0 || line[0] == ';') continue;
            var key = line[..eq].Trim().TrimStart('﻿');
            if (key.EndsWith(",P", StringComparison.Ordinal)) key = key[..^2];
            d[key] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    // ---- loading with cache ----------------------------------------------------------

    public static ActionCatalog LoadOrBuild(ScInstall install, string cacheDir, bool forceRefresh, Action<string>? progress = null)
    {
        var version = install.ReadVersion() ?? "unknown";
        var cacheFile = Path.Combine(cacheDir, $"catalog-v4-{install.Channel}-{version}.json");
        if (!forceRefresh && File.Exists(cacheFile))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<ActionCatalog>(File.ReadAllText(cacheFile));
                if (cached is { Groups.Count: > 0 }) return cached;
            }
            catch { /* rebuild */ }
        }

        progress?.Invoke("Reading Data.p4k directory...");
        using var p4k = P4kArchive.Open(install.P4kPath);
        var found = p4k.Find(new[] { "libs/config/defaultprofile.xml", "localization/english/global.ini" });
        if (!found.TryGetValue("libs/config/defaultprofile.xml", out var profEntry))
            throw new FileNotFoundException("defaultProfile.xml not found in Data.p4k");

        progress?.Invoke("Extracting defaultProfile.xml...");
        var profile = CryXml.Parse(p4k.Read(profEntry));
        var loc = new Dictionary<string, string>();
        if (found.TryGetValue("localization/english/global.ini", out var iniEntry))
        {
            progress?.Invoke("Extracting localization...");
            loc = ParseIni(System.Text.Encoding.UTF8.GetString(p4k.Read(iniEntry)));
        }

        var catalog = FromDefaultProfile(profile, loc, version);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(catalog));
        return catalog;
    }

    internal static readonly Regex JsInput = new(@"^js(\d+)_(.+)$", RegexOptions.Compiled);
}
