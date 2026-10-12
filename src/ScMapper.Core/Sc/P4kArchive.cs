using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ScMapper.Core.Sc;

public sealed record P4kEntry(string Name, ushort Method, ushort Flags, long CompressedSize, long Size, long Offset);

/// <summary>
/// Minimal read-only reader for Star Citizen's Data.p4k (ZIP64 with Zstd, optional AES-CBC).
/// Only the central directory is streamed; individual entries are extracted on demand.
/// </summary>
public sealed class P4kArchive : IDisposable
{
    // Publicly known p4k key (unp4k, scdatatools); IV is zero.
    private static readonly byte[] Key =
        { 0x5E, 0x7A, 0x20, 0x02, 0x30, 0x2E, 0xEB, 0x1A, 0x3B, 0xB6, 0x17, 0xC3, 0x0F, 0xDE, 0x1E, 0x47 };

    private readonly FileStream _file;
    private long _cdOffset, _cdSize, _entries;

    private P4kArchive(FileStream file) => _file = file;

    public static P4kArchive Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var a = new P4kArchive(fs);
        a.ReadDirectoryInfo();
        return a;
    }

    private void ReadDirectoryInfo()
    {
        long size = _file.Length;
        int tailLen = (int)Math.Min(size, 1 << 17);
        var tail = new byte[tailLen];
        _file.Seek(size - tailLen, SeekOrigin.Begin);
        _file.ReadExactly(tail);
        int eocd = LastIndexOf(tail, 0x50, 0x4B, 0x05, 0x06, tail.Length - 4);
        if (eocd < 0) throw new InvalidDataException("End of central directory not found - not a p4k/zip?");
        _entries = BitConverter.ToUInt16(tail, eocd + 10);
        _cdSize = BitConverter.ToUInt32(tail, eocd + 12);
        _cdOffset = BitConverter.ToUInt32(tail, eocd + 16);

        int loc = LastIndexOf(tail, 0x50, 0x4B, 0x06, 0x07, eocd - 4);
        if (loc >= 0)
        {
            long z64 = BitConverter.ToInt64(tail, loc + 8);
            var z = new byte[56];
            _file.Seek(z64, SeekOrigin.Begin);
            _file.ReadExactly(z);
            if (z[0] == 0x50 && z[1] == 0x4B && z[2] == 0x06 && z[3] == 0x06)
            {
                _entries = BitConverter.ToInt64(z, 32);
                _cdSize = BitConverter.ToInt64(z, 40);
                _cdOffset = BitConverter.ToInt64(z, 48);
            }
        }
    }

    private static int LastIndexOf(byte[] b, byte b0, byte b1, byte b2, byte b3, int from)
    {
        for (int i = Math.Min(from, b.Length - 4); i >= 0; i--)
            if (b[i] == b0 && b[i + 1] == b1 && b[i + 2] == b2 && b[i + 3] == b3) return i;
        return -1;
    }

    /// <summary>Scan the central directory for entries whose normalized name ends with one of the suffixes.</summary>
    public Dictionary<string, P4kEntry> Find(IEnumerable<string> suffixes)
    {
        var wanted = suffixes.Select(s => s.Replace('\\', '/').ToLowerInvariant()).ToArray();
        var result = new Dictionary<string, P4kEntry>(StringComparer.OrdinalIgnoreCase);
        _file.Seek(_cdOffset, SeekOrigin.Begin);
        using var br = new BinaryReader(new BufferedStream(_file, 1 << 22), Encoding.UTF8, leaveOpen: true);
        var hdr = new byte[46];
        for (long i = 0; i < _entries; i++)
        {
            if (br.BaseStream.Read(hdr, 0, 46) < 46) break;
            if (hdr[0] != 0x50 || hdr[1] != 0x4B || hdr[2] != 1 || hdr[3] != 2)
                throw new InvalidDataException($"Central directory corrupt at entry {i}");
            ushort flags = BitConverter.ToUInt16(hdr, 8);
            ushort method = BitConverter.ToUInt16(hdr, 10);
            long csize = BitConverter.ToUInt32(hdr, 20);
            long usize = BitConverter.ToUInt32(hdr, 24);
            int nlen = BitConverter.ToUInt16(hdr, 28);
            int elen = BitConverter.ToUInt16(hdr, 30);
            int clen = BitConverter.ToUInt16(hdr, 32);
            long rel = BitConverter.ToUInt32(hdr, 42);

            var nameBytes = br.ReadBytes(nlen);
            var extra = br.ReadBytes(elen);
            if (clen > 0) br.ReadBytes(clen);

            if (usize == 0xFFFFFFFF || csize == 0xFFFFFFFF || rel == 0xFFFFFFFF)
                ParseZip64Extra(extra, ref usize, ref csize, ref rel);

            string norm = Encoding.UTF8.GetString(nameBytes).Replace('\\', '/').ToLowerInvariant();
            foreach (var w in wanted)
            {
                if (norm.EndsWith(w, StringComparison.Ordinal) && !result.ContainsKey(w))
                {
                    result[w] = new P4kEntry(Encoding.UTF8.GetString(nameBytes), method, flags, csize, usize, rel);
                    break;
                }
            }
        }
        return result;
    }

    private static void ParseZip64Extra(byte[] extra, ref long usize, ref long csize, ref long rel)
    {
        int e = 0;
        while (e + 4 <= extra.Length)
        {
            ushort id = BitConverter.ToUInt16(extra, e);
            ushort sz = BitConverter.ToUInt16(extra, e + 2);
            if (id == 1)
            {
                int p = e + 4;
                if (usize == 0xFFFFFFFF) { usize = BitConverter.ToInt64(extra, p); p += 8; }
                if (csize == 0xFFFFFFFF) { csize = BitConverter.ToInt64(extra, p); p += 8; }
                if (rel == 0xFFFFFFFF) { rel = BitConverter.ToInt64(extra, p); }
                return;
            }
            e += 4 + sz;
        }
    }

    public byte[] Read(P4kEntry e)
    {
        var h = new byte[30];
        _file.Seek(e.Offset, SeekOrigin.Begin);
        _file.ReadExactly(h);
        bool sig = h[0] == 0x50 && h[1] == 0x4B && ((h[2] == 3 && h[3] == 4) || (h[2] == 3 && h[3] == 0x14));
        if (!sig) throw new InvalidDataException($"Bad local header for {e.Name}");
        int nlen = BitConverter.ToUInt16(h, 26), elen = BitConverter.ToUInt16(h, 28);
        _file.Seek(e.Offset + 30 + nlen + elen, SeekOrigin.Begin);
        var raw = new byte[e.CompressedSize];
        _file.ReadExactly(raw);

        bool flagged = (e.Flags & 1) != 0;
        Exception? last = null;
        foreach (bool decrypt in new[] { flagged, !flagged })
        {
            try
            {
                var buf = decrypt ? Decrypt(raw) : raw;
                var data = Decompress(buf, e.Method);
                if (LooksValid(data)) return data;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidDataException($"Could not unpack {e.Name}", last);
    }

    private static byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        int n = data.Length / 16 * 16;
        return aes.CreateDecryptor().TransformFinalBlock(data, 0, n);
    }

    private static byte[] Decompress(byte[] data, ushort method)
    {
        switch (method)
        {
            case 0: return data;
            case 8:
            {
                using var ms = new MemoryStream(data);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var o = new MemoryStream();
                ds.CopyTo(o);
                return o.ToArray();
            }
            case 93:
            case 100:
            {
                using var ms = new MemoryStream(data);
                using var ds = new ZstdSharp.DecompressionStream(ms);
                using var o = new MemoryStream();
                ds.CopyTo(o);
                return o.ToArray();
            }
            default: throw new NotSupportedException($"Compression method {method}");
        }
    }

    private static bool LooksValid(byte[] d)
    {
        if (d.Length == 0) return false;
        if (d.Length >= 7 && Encoding.ASCII.GetString(d, 0, 7) == "CryXmlB") return true;
        int n = Math.Min(d.Length, 2000), ok = 0;
        for (int i = 0; i < n; i++)
            if (d[i] == 9 || d[i] == 10 || d[i] == 13 || (d[i] >= 32 && d[i] < 127) || d[i] >= 128) ok++;
        return ok / (double)n > 0.95;
    }

    public void Dispose() => _file.Dispose();
}
