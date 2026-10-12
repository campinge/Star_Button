using System.Xml.Linq;
using ScMapper.Core.Model;

namespace ScMapper.Core.Sc;

public sealed record SeenRebind(string Map, string Action, string Input, string? ActivationMode, string? MultiTap)
{
    /// <summary>Joystick instance number for "jsN_..." inputs, else null.</summary>
    public int? JsInstance
    {
        get
        {
            var m = ActionCatalog.JsInput.Match(Input);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }

    /// <summary>Input without device prefix: "button5", "hat1_up", "x".</summary>
    public string? JsInputName
    {
        get
        {
            var m = ActionCatalog.JsInput.Match(Input);
            return m.Success ? m.Groups[2].Value : null;
        }
    }

    public bool IsUnbound => Input.EndsWith(' ');
}

/// <summary>
/// Reads and merges Star Citizen's actionmaps.xml. Unknown nodes/attributes are preserved;
/// only joystick rebinds managed by this tool and joystick &lt;options&gt; entries are touched.
/// </summary>
public sealed class ActionMapsFile
{
    public XDocument Doc { get; }
    public string? Path { get; }

    private ActionMapsFile(XDocument doc, string? path) { Doc = doc; Path = path; }

    /// <summary>The file started with a UTF-8 byte order mark; written back the same way.</summary>
    private bool _bom;

    public static ActionMapsFile Load(string path)
    {
        var file = new ActionMapsFile(XDocument.Load(path, LoadOptions.PreserveWhitespace), path);
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[3];
            file._bom = fs.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch { /* the document is loaded; the byte order mark is cosmetic */ }
        return file;
    }

    public static ActionMapsFile LoadOrEmpty(string path) =>
        File.Exists(path) ? Load(path) : CreateEmpty();

    public static ActionMapsFile CreateEmpty()
    {
        var doc = new XDocument(new XElement("ActionMaps",
            new XElement("ActionProfiles",
                new XAttribute("version", "1"), new XAttribute("optionsVersion", "2"),
                new XAttribute("rebindVersion", "2"), new XAttribute("profileName", "default"),
                new XElement("modifiers"))));
        return new ActionMapsFile(doc, null);
    }

    public static ActionMapsFile Parse(string xml) => new(XDocument.Parse(xml, LoadOptions.PreserveWhitespace), null);

    private XElement Profile => ProfileOrNull
                                ?? throw new InvalidDataException("actionmaps.xml has no <ActionProfiles>");

    /// <summary>Reads treat a file without the profile node as "holds nothing"; only writes insist on it.</summary>
    private XElement? ProfileOrNull => Doc.Root?.Element("ActionProfiles");

    public IEnumerable<SeenRebind> Rebinds()
    {
        foreach (var am in ProfileOrNull?.Elements("actionmap") ?? Enumerable.Empty<XElement>())
            foreach (var a in am.Elements("action"))
                foreach (var r in a.Elements("rebind"))
                    yield return new SeenRebind((string?)am.Attribute("name") ?? "", (string?)a.Attribute("name") ?? "",
                        (string?)r.Attribute("input") ?? "", (string?)r.Attribute("activationMode"),
                        (string?)r.Attribute("multiTap"));
    }

    public IEnumerable<(int Instance, string? Product)> JoystickOptions() =>
        (ProfileOrNull?.Elements("options") ?? Enumerable.Empty<XElement>()).Where(o => (string?)o.Attribute("type") == "joystick")
            .Select(o => (int.TryParse((string?)o.Attribute("instance"), out var i) ? i : 0, (string?)o.Attribute("Product")));

    /// <summary>"Name  {PPPPVVVV-0000-0000-0000-504944564944}"</summary>
    public static string ProductString(string name, int vid, int pid) =>
        $"{name}  {{{pid:X4}{vid:X4}-0000-0000-0000-504944564944}}";

    private static readonly System.Text.RegularExpressions.Regex ProductIds =
        new(@"\{([0-9A-Fa-f]{4})([0-9A-Fa-f]{4})-0000-0000-0000-504944564944\}\s*$");

    /// <summary>Vendor and product id out of a Product string ("Name  {PPPPVVVV-0000-...}"); null for anything else.</summary>
    public static (int Vid, int Pid)? ParseProductIds(string? product)
    {
        if (product == null) return null;
        var m = ProductIds.Match(product);
        if (!m.Success) return null;
        return (Convert.ToInt32(m.Groups[2].Value, 16), Convert.ToInt32(m.Groups[1].Value, 16));
    }

    public void SetJoystickOption(int instance, string product)
    {
        var existing = Profile.Elements("options").FirstOrDefault(o =>
            (string?)o.Attribute("type") == "joystick" && (string?)o.Attribute("instance") == instance.ToString());
        if (existing != null) { existing.SetAttributeValue("Product", product); return; }

        var el = new XElement("options", new XAttribute("type", "joystick"),
            new XAttribute("instance", instance), new XAttribute("Product", product));
        var last = Profile.Elements("options").LastOrDefault();
        if (last != null) last.AddAfterSelf(el);
        else Profile.AddFirst(el);
    }

    /// <summary>Removes the rebinds of the given actions whose input matches the predicate (unbound markers stay).</summary>
    public int RemoveRebinds(IEnumerable<string> actionKeys, Func<string, bool> inputMatches, bool includeUnboundMarkers = false)
    {
        var keys = new HashSet<string>(actionKeys);
        int removed = 0;
        foreach (var am in Profile.Elements("actionmap").ToList())
        {
            foreach (var a in am.Elements("action").ToList())
            {
                var key = $"{(string?)am.Attribute("name")}/{(string?)a.Attribute("name")}";
                if (!keys.Contains(key)) continue;
                foreach (var r in a.Elements("rebind").ToList())
                {
                    var input = (string?)r.Attribute("input") ?? "";
                    if ((includeUnboundMarkers || !input.EndsWith(' ')) && inputMatches(input)) { r.Remove(); removed++; }
                }
                if (!a.HasElements) a.Remove();
            }
            if (!am.HasElements) am.Remove();
        }
        return removed;
    }

    public void AddRebind(string map, string action, string input, string? activationMode = null)
    {
        var am = Profile.Elements("actionmap").FirstOrDefault(e => (string?)e.Attribute("name") == map);
        if (am == null)
        {
            am = new XElement("actionmap", new XAttribute("name", map));
            var mods = Profile.Element("modifiers");
            if (mods != null) mods.AddBeforeSelf(am); else Profile.Add(am);
        }
        var a = am.Elements("action").FirstOrDefault(e => (string?)e.Attribute("name") == action);
        if (a == null)
        {
            a = new XElement("action", new XAttribute("name", action));
            am.Add(a);
        }
        var r = new XElement("rebind", new XAttribute("input", input));
        if (activationMode == "double_tap") r.SetAttributeValue("multiTap", "2");
        else if (!string.IsNullOrEmpty(activationMode) && activationMode != "press")
            r.SetAttributeValue("activationMode", activationMode);
        a.Add(r);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        // Same shape as the file that was read: the game writes no declaration unless the file had one.
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(_bom),
            OmitXmlDeclaration = Doc.Declaration == null,
            Indent = false,
            NewLineHandling = System.Xml.NewLineHandling.None,
        };
        using (var w = System.Xml.XmlWriter.Create(tmp, settings))
            Doc.Save(w);
        File.Move(tmp, path, overwrite: true);
    }
}
