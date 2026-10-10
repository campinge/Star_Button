using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScMapper.Core.Sc;

/// <summary>Converts CryEngine binary XML ("CryXmlB") to XDocument. Plain XML passes through.</summary>
public static partial class CryXml
{
    public static XDocument Parse(byte[] data)
    {
        if (data.Length >= 7 && Encoding.ASCII.GetString(data, 0, 7) == "CryXmlB") return FromBinary(data);
        using var ms = new MemoryStream(data);
        return XDocument.Load(ms);
    }

    private static XDocument FromBinary(byte[] b)
    {
        // Header: 8 byte signature, then 9 x uint32 (size, node off/cnt, attr off/cnt, child off/cnt, string off/size)
        uint nodeOff = U32(b, 12), nodeCnt = U32(b, 16);
        uint attrOff = U32(b, 20), attrCnt = U32(b, 24);
        uint childOff = U32(b, 28), childCnt = U32(b, 32);
        uint strOff = U32(b, 36);
        if (nodeOff > b.Length || attrOff > b.Length || childOff > b.Length || strOff > b.Length)
            throw new InvalidDataException("CryXmlB: table offsets out of range (format changed?)");

        string Str(uint rel)
        {
            int start = (int)(strOff + rel), end = Array.IndexOf(b, (byte)0, start);
            return Encoding.UTF8.GetString(b, start, end - start);
        }

        // Node (28 bytes): nameOff, content, attrCount(u16), childCount(u16), parent, firstAttr, firstChild, reserved
        XElement Build(uint idx, int depth)
        {
            if (depth > 200) throw new InvalidDataException("CryXmlB: nesting too deep");
            int p = (int)(nodeOff + 28 * idx);
            uint nameOff = U32(b, p);
            ushort acount = BitConverter.ToUInt16(b, p + 8);
            ushort ccount = BitConverter.ToUInt16(b, p + 10);
            uint firstAttr = U32(b, p + 16), firstChild = U32(b, p + 20);
            var el = new XElement(SafeName(Str(nameOff)));
            for (uint i = 0; i < acount; i++)
            {
                int ap = (int)(attrOff + 8 * (firstAttr + i));
                el.SetAttributeValue(SafeName(Str(U32(b, ap))), Str(U32(b, ap + 4)));
            }
            for (uint i = 0; i < ccount; i++)
            {
                uint child = U32(b, (int)(childOff + 4 * (firstChild + i)));
                if (child >= nodeCnt) throw new InvalidDataException("CryXmlB: bad child index");
                el.Add(Build(child, depth + 1));
            }
            return el;
        }

        _ = attrCnt; _ = childCnt;
        return new XDocument(Build(0, 0));
    }

    private static uint U32(byte[] b, int p) => BitConverter.ToUInt32(b, p);

    private static string SafeName(string s)
    {
        s = InvalidNameChars().Replace(s, "_");
        return s.Length == 0 || char.IsDigit(s[0]) ? "_" + s : s;
    }

    [GeneratedRegex(@"[^\w.\-]")]
    private static partial Regex InvalidNameChars();
}
