#!/usr/bin/env python
"""
SC Stick Mapper - P0 Spike / POC.

Liest nur (nichts wird in Spieldateien geschrieben):
  1. Findet Star-Citizen-Installationen (LIVE/PTU/...) und liest die Build-Info.
  2. Liest actionmaps.xml (Geraete, Rebinds, Mappings-Ordner).
  3. Oeffnet Data.p4k, extrahiert NUR defaultProfile.xml und global.ini
     (ZIP64-Verzeichnis, optional AES + Zstd) und wandelt CryXmlB in XML um.
  4. Analysiert die Standardaktionen (Gruppen, Aktivierungsmodi, Attribute).
  5. Listet angeschlossene HID-Geraete (VID/PID/Seriennummer-Hinweis) per PnP.

Aufruf:  poc\\.venv\\Scripts\\python.exe poc\\sc_probe.py [--sc-root PFAD] [--channel LIVE] [--out poc\\out]
Ergebnis: <out>\\report.md, <out>\\report.json, <out>\\defaultProfile.xml, <out>\\global.ini
"""
import argparse
import collections
import json
import os
import re
import string
import struct
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zlib

try:
    import zstandard
except ImportError:
    zstandard = None
try:
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
except ImportError:
    Cipher = None

# Oeffentlich bekannter p4k-Schluessel (unp4k / scdatatools), IV = 0.
P4K_KEY = bytes([0x5E, 0x7A, 0x20, 0x02, 0x30, 0x2E, 0xEB, 0x1A,
                 0x3B, 0xB6, 0x17, 0xC3, 0x0F, 0xDE, 0x1E, 0x47])

CHANNELS = ["LIVE", "PTU", "EPTU", "HOTFIX", "TECH-PREVIEW"]
TARGETS = {
    "default_profile": "libs/config/defaultprofile.xml",
    "global_ini": "localization/english/global.ini",
}

report = {"steps": [], "warnings": []}


def log(msg):
    print(msg, flush=True)


def warn(msg):
    report["warnings"].append(msg)
    log("  ! " + msg)


# --------------------------------------------------------------- 1. Install
def candidate_roots():
    roots = []
    try:
        import winreg
        for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
            for sub in (r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                        r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"):
                try:
                    key = winreg.OpenKey(hive, sub)
                except OSError:
                    continue
                for i in range(winreg.QueryInfoKey(key)[0]):
                    try:
                        sk = winreg.OpenKey(key, winreg.EnumKey(key, i))
                        name = winreg.QueryValueEx(sk, "DisplayName")[0]
                        if "star citizen" in name.lower() or name.lower().startswith("rsi"):
                            for val in ("InstallLocation", "DisplayIcon"):
                                try:
                                    p = winreg.QueryValueEx(sk, val)[0]
                                    roots.append(os.path.dirname(p) if p.lower().endswith(".exe") else p)
                                except OSError:
                                    pass
                    except OSError:
                        pass
    except ImportError:
        pass
    drives = [f"{c}:\\" for c in string.ascii_uppercase if os.path.exists(f"{c}:\\")]
    for d in drives:
        for rel in (r"Program Files\Roberts Space Industries\StarCitizen",
                    r"Roberts Space Industries\StarCitizen",
                    r"Games\StarCitizen", r"StarCitizen"):
            roots.append(os.path.join(d, rel))
    seen, out = set(), []
    for r in roots:
        k = os.path.normcase(os.path.normpath(r))
        if k not in seen and os.path.isdir(r):
            seen.add(k)
            out.append(os.path.normpath(r))
    return out


def find_installs(explicit_root=None):
    roots = [explicit_root] if explicit_root else candidate_roots()
    installs = []
    for root in roots:
        for ch in CHANNELS:
            d = os.path.join(root, ch)
            p4k = os.path.join(d, "Data.p4k")
            if os.path.isfile(p4k):
                installs.append({"root": root, "channel": ch, "dir": d, "p4k": p4k})
    return installs


def read_build(inst):
    path = os.path.join(inst["dir"], "build_manifest.id")
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)["Data"]
        return {k: data.get(k) for k in ("Branch", "Version", "BuildId", "RequestedP4ChangeNum", "BuildDateStamp")}
    except Exception as e:  # noqa: BLE001
        warn(f"build_manifest.id nicht lesbar: {e}")
        return {}


def user_paths(inst):
    base = None
    for name in ("USER", "user"):
        cand = os.path.join(inst["dir"], name, "Client", "0")
        if os.path.isdir(cand):
            base = cand
            break
    if not base:
        return None
    return {
        "base": base,
        "profiles": os.path.join(base, "Profiles"),
        "actionmaps": os.path.join(base, "Profiles", "default", "actionmaps.xml"),
        "mappings": os.path.join(base, "Controls", "Mappings"),
    }


# ------------------------------------------------------ 2. actionmaps.xml
def parse_actionmaps(path):
    res = {"path": path, "exists": os.path.isfile(path)}
    if not res["exists"]:
        return res
    res["size"] = os.path.getsize(path)
    root = ET.parse(path).getroot()
    prof = root.find("ActionProfiles")
    res["profileName"] = prof.get("profileName") if prof is not None else None
    res["options"] = []
    res["rebinds"] = []
    res["unbound"] = 0
    res["other_children"] = []
    if prof is None:
        return res
    for el in prof:
        if el.tag == "options":
            res["options"].append({k: el.get(k) for k in ("type", "instance", "Product")})
        elif el.tag == "actionmap":
            for act in el.findall("action"):
                for rb in act.findall("rebind"):
                    inp = rb.get("input", "")
                    res["rebinds"].append({"map": el.get("name"), "action": act.get("name"),
                                           "input": inp, "activationMode": rb.get("activationMode")})
                    if re.fullmatch(r"\w+?_\s*", inp) and inp.strip().endswith("_") is False and " " in inp:
                        res["unbound"] += 1
        elif el.tag not in ("modifiers",):
            res["other_children"].append(el.tag)
    res["unbound"] = sum(1 for r in res["rebinds"] if r["input"].endswith(" "))
    return res


def list_mappings(folder):
    if not folder or not os.path.isdir(folder):
        return []
    return sorted(f for f in os.listdir(folder) if f.lower().endswith(".xml"))


# ------------------------------------------------------------- 3. p4k
class P4kEntry:
    __slots__ = ("name", "method", "flags", "csize", "usize", "offset")

    def __init__(self, name, method, flags, csize, usize, offset):
        self.name, self.method, self.flags = name, method, flags
        self.csize, self.usize, self.offset = csize, usize, offset

    def as_dict(self):
        return {s: getattr(self, s) for s in self.__slots__}


def p4k_central_directory(f):
    f.seek(0, os.SEEK_END)
    size = f.tell()
    tail_len = min(size, 1 << 17)
    f.seek(size - tail_len)
    tail = f.read(tail_len)
    i = tail.rfind(b"PK\x05\x06")
    if i < 0:
        raise ValueError("EOCD nicht gefunden - kein ZIP/p4k?")
    (_, disk, cd_disk, n_disk, n_total, cd_size, cd_off, _clen) = struct.unpack_from("<4s4H2LH", tail, i)
    info = {"eocd_entries": n_total, "eocd_cd_size": cd_size, "eocd_cd_offset": cd_off}
    loc = tail.rfind(b"PK\x06\x07", 0, i)
    if loc >= 0:
        (_, _d, z64_off, _n) = struct.unpack_from("<4sLQL", tail, loc)
        f.seek(z64_off)
        z = f.read(56)
        if z[:4] == b"PK\x06\x06":
            (_, _sz, _vm, _vn, _d1, _d2, n_disk64, n_total64, cd_size, cd_off) = struct.unpack_from("<4sQHHLLQQQQ", z)
            info.update(zip64=True, entries=n_total64, cd_size=cd_size, cd_offset=cd_off)
            return info
    info.update(zip64=False, entries=n_total, cd_size=cd_size, cd_offset=cd_off)
    return info


def p4k_find(f, cd, wanted):
    """Streamt das Zentralverzeichnis, merkt nur Treffer fuer 'wanted' (key->suffix)."""
    f.seek(cd["cd_offset"])
    data = f.read(cd["cd_size"])
    found = {k: [] for k in wanted}
    near = []
    pos, n = 0, 0
    while pos + 46 <= len(data) and n < cd["entries"] + 10:
        if data[pos:pos + 4] != b"PK\x01\x02":
            warn(f"Zentralverzeichnis: unerwartete Signatur bei Eintrag {n} (Offset {pos})")
            break
        (_s, _vm, _vn, flags, method, _t, _d, _crc, csize, usize, nlen, elen, clen,
         _dn, _ia, _ea, rel) = struct.unpack_from("<4s4H2HLLLHHHHHLL", data, pos)
        name = data[pos + 46:pos + 46 + nlen].decode("utf-8", "replace")
        extra = data[pos + 46 + nlen:pos + 46 + nlen + elen]
        if 0xFFFFFFFF in (csize, usize, rel):
            e = 0
            while e + 4 <= len(extra):
                hid, hsz = struct.unpack_from("<HH", extra, e)
                if hid == 1:
                    p = e + 4
                    if usize == 0xFFFFFFFF:
                        usize = struct.unpack_from("<Q", extra, p)[0]; p += 8
                    if csize == 0xFFFFFFFF:
                        csize = struct.unpack_from("<Q", extra, p)[0]; p += 8
                    if rel == 0xFFFFFFFF:
                        rel = struct.unpack_from("<Q", extra, p)[0]; p += 8
                    break
                e += 4 + hsz
        norm = name.replace("\\", "/").lower()
        for key, suffix in wanted.items():
            if norm.endswith(suffix):
                found[key].append(P4kEntry(name, method, flags, csize, usize, rel))
        if "defaultprofile" in norm or norm.endswith("global.ini"):
            near.append(name)
        pos += 46 + nlen + elen + clen
        n += 1
    return found, near, n


def aes_decrypt(buf):
    if Cipher is None:
        raise RuntimeError("Paket 'cryptography' fehlt")
    buf = buf[: len(buf) // 16 * 16]
    d = Cipher(algorithms.AES(P4K_KEY), modes.CBC(b"\x00" * 16)).decryptor()
    return d.update(buf) + d.finalize()


def decompress(buf, method):
    if method == 0:
        return buf
    if method == 8:
        return zlib.decompressobj(-15).decompress(buf)
    if method in (93, 100):
        if zstandard is None:
            raise RuntimeError("Paket 'zstandard' fehlt")
        return zstandard.ZstdDecompressor().decompressobj().decompress(buf)
    raise ValueError(f"Kompressionsmethode {method} unbekannt")


def plausible(data):
    if data.startswith(b"CryXmlB"):
        return True
    head = data[:2000].lstrip(b"\xef\xbb\xbf \r\n\t")
    if head.startswith(b"<?xml") or head.startswith(b"<"):
        return True
    if not head:
        return False
    printable = sum(1 for b in head if b in b"\r\n\t" or 32 <= b < 127 or b >= 128)
    return printable / len(head) > 0.95


def p4k_read(f, e):
    f.seek(e.offset)
    h = f.read(30)
    if h[:4] not in (b"PK\x03\x04", b"PK\x03\x14"):
        raise ValueError(f"lokaler Header ungueltig bei {e.offset}: {h[:4]!r}")
    nlen, elen = struct.unpack_from("<HH", h, 26)
    f.seek(e.offset + 30 + nlen + elen)
    raw = f.read(e.csize)
    # Strategien: laut Flag zuerst, dann die jeweils andere Variante.
    tried = []
    order = [bool(e.flags & 1), not bool(e.flags & 1)]
    for decrypt in order:
        try:
            buf = aes_decrypt(raw) if decrypt else raw
            out = decompress(buf, e.method)
            tried.append(f"decrypt={decrypt}: {len(out)} B, plausibel={plausible(out)}")
            if plausible(out):
                return out, {"decrypt": decrypt, "tried": tried}
        except Exception as ex:  # noqa: BLE001
            tried.append(f"decrypt={decrypt}: Fehler {type(ex).__name__}: {ex}")
    raise ValueError("Entpacken fehlgeschlagen: " + " | ".join(tried))


# ------------------------------------------------------- CryXmlB -> XML
def cryxml_to_element(buf):
    if not buf.startswith(b"CryXmlB"):
        raise ValueError("kein CryXmlB")
    (xml_size, n_off, n_cnt, a_off, a_cnt, c_off, c_cnt, s_off, s_cnt) = struct.unpack_from("<9I", buf, 8)
    for off in (n_off, a_off, c_off, s_off):
        if off > len(buf):
            raise ValueError("CryXmlB: Tabellen-Offset ausserhalb der Datei (Format geaendert?)")

    def string(rel):
        start = s_off + rel
        end = buf.index(b"\x00", start)
        return buf[start:end].decode("utf-8", "replace")

    nodes = [struct.unpack_from("<IIHHIII", buf, n_off + 28 * i) for i in range(n_cnt)]
    attrs = [struct.unpack_from("<II", buf, a_off + 8 * i) for i in range(a_cnt)]
    childs = struct.unpack_from(f"<{c_cnt}I", buf, c_off) if c_cnt else ()
    built = {}

    def safe_tag(s):
        return re.sub(r"[^\w.\-:]", "_", s) or "_"

    def build(idx):
        name_off, _type, acount, ccount, _parent, first_attr, first_child = nodes[idx][:7]
        el = ET.Element(safe_tag(string(name_off)))
        for k, v in attrs[first_attr:first_attr + acount]:
            el.set(safe_tag(string(k)), string(v))
        for ci in childs[first_child:first_child + ccount]:
            el.append(build(ci))
        return el

    sys.setrecursionlimit(max(sys.getrecursionlimit(), 10000))
    return build(0)


def to_element(data):
    if data.startswith(b"CryXmlB"):
        return cryxml_to_element(data)
    return ET.fromstring(data)


# ------------------------------------------------ 4. Aktionen analysieren
def analyze_profile(root):
    res = {"root_tag": root.tag, "tag_counts": collections.Counter(el.tag for el in root.iter())}
    res["tag_counts"] = dict(res["tag_counts"].most_common(15))
    actionmaps = [el for el in root.iter("actionmap")]
    res["actionmap_count"] = len(actionmaps)
    action_attr = collections.Counter()
    rebind_attr = collections.Counter()
    modes = collections.Counter()
    input_prefixes = collections.Counter()
    groups = []
    n_actions = 0
    for am in actionmaps:
        acts = list(am.iter("action"))
        n_actions += len(acts)
        groups.append({"name": am.get("name"), "attrs": dict(am.attrib), "actions": len(acts)})
        for a in acts:
            action_attr.update(a.attrib.keys())
            for k, v in a.attrib.items():
                if "activation" in k.lower() or k.lower().startswith("on"):
                    modes[f"{k}={v}"] += 1
            for rb in a.iter("rebind"):
                rebind_attr.update(rb.attrib.keys())
                inp = rb.get("input", "")
                input_prefixes[re.sub(r"[\d]+_.*$", "N_", inp) if "_" in inp else inp] += 1
                for k, v in rb.attrib.items():
                    if "activation" in k.lower():
                        modes[f"rebind.{k}={v}"] += 1
    res["action_count"] = n_actions
    res["action_attributes"] = dict(action_attr.most_common())
    res["rebind_attributes"] = dict(rebind_attr.most_common())
    res["activation_related"] = dict(modes.most_common(40))
    res["input_prefixes"] = dict(input_prefixes.most_common(15))
    # In diesem Format stehen Standardbelegungen als Attribute direkt an <action>.
    bound = {dev: 0 for dev in ("keyboard", "gamepad", "joystick", "mouse")}
    for a in root.iter("action"):
        for dev in bound:
            if a.get(dev, "").strip():
                bound[dev] += 1
    res["default_bound_by_device"] = bound
    res["groups"] = groups
    first = next((a for am in actionmaps for a in am.iter("action")), None)
    res["sample_action"] = ET.tostring(first, encoding="unicode")[:600] if first is not None else None
    return res


def load_ini(text):
    d = {}
    for line in text.splitlines():
        if "=" in line and not line.startswith(";"):
            k, v = line.split("=", 1)
            d[k.strip().lstrip("\ufeff")] = v.strip()
    return d


# ------------------------------------------------------- 5. HID / PnP
def probe_hid():
    ps = ("[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'HID\\VID_*' -and "
          "($_.FriendlyName -match 'game|joystick|stick|throttle|VKB|controller' -or $_.Class -eq 'HIDClass') } | "
          "Select-Object FriendlyName,InstanceId,Status | ConvertTo-Json -Compress")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True,
                             text=True, encoding="utf-8", timeout=60).stdout.strip()
        data = json.loads(out) if out else []
        if isinstance(data, dict):
            data = [data]
    except Exception as e:  # noqa: BLE001
        warn(f"PnP-Abfrage fehlgeschlagen: {e}")
        return []
    res = []
    for d in data:
        m = re.search(r"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", d["InstanceId"], re.I)
        if not m:
            continue
        tail = d["InstanceId"].split("\\")[-1]
        # Instanz-ID ohne '&' => meist echte Seriennummer, sonst Windows-generiert (Port-abhaengig).
        res.append({"name": d["FriendlyName"], "vid": m.group(1), "pid": m.group(2),
                    "instance_tail": tail, "has_serial_hint": "&" not in tail})
    return res


# ------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sc-root", help=r"z.B. C:\Program Files\Roberts Space Industries\StarCitizen")
    ap.add_argument("--channel", default=None, help="LIVE/PTU/... (Standard: erster Fund, LIVE bevorzugt)")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
    ap.add_argument("--skip-p4k", action="store_true")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    log("[1] Installationen suchen")
    installs = find_installs(args.sc_root)
    report["installs"] = installs
    for i in installs:
        log(f"  - {i['channel']}: {i['dir']}")
    if not installs:
        warn("Keine Installation gefunden - mit --sc-root angeben.")
        finish(args.out)
        return 1
    inst = next((i for i in installs if i["channel"] == (args.channel or "LIVE")), installs[0])
    report["selected"] = inst
    report["build"] = read_build(inst)
    log(f"  Build: {report['build']}")

    log("[2] Benutzerdaten (actionmaps.xml, Mappings)")
    up = user_paths(inst)
    report["user_paths"] = up
    if up:
        report["actionmaps"] = parse_actionmaps(up["actionmaps"])
        report["mappings_files"] = list_mappings(up["mappings"])
        am = report["actionmaps"]
        log(f"  actionmaps.xml: {am.get('exists')}  Optionen: {len(am.get('options', []))}  "
            f"Rebinds: {len(am.get('rebinds', []))}  Mappings-Dateien: {len(report['mappings_files'])}")
        if am.get("exists") and not am.get("rebinds"):
            warn("actionmaps.xml enthaelt keine Rebinds (nur Geraeteliste) - Standard unveraendert oder leere Datei.")
    else:
        warn("USER-Ordner nicht gefunden (SC noch nie gestartet?)")

    log("[3] HID-Geraete (PnP)")
    report["hid"] = probe_hid()
    for d in report["hid"]:
        log(f"  - {d['name']}  VID {d['vid']} PID {d['pid']}  Seriennummer? {d['has_serial_hint']}")

    if not args.skip_p4k:
        log("[4] Data.p4k lesen (nur Zentralverzeichnis + 2 Dateien)")
        t0 = time.time()
        with open(inst["p4k"], "rb") as f:
            cd = p4k_central_directory(f)
            report["p4k"] = {"size": os.path.getsize(inst["p4k"]), **cd}
            log(f"  {cd['entries']:,} Eintraege, Zentralverzeichnis {cd['cd_size']/1e6:.0f} MB, zip64={cd['zip64']}")
            found, near, scanned = p4k_find(f, cd, TARGETS)
            report["p4k"]["scanned"] = scanned
            report["p4k"]["near_matches"] = near[:30]
            log(f"  durchsucht: {scanned:,} Eintraege in {time.time()-t0:.1f}s")
            ini = None
            for key, entries in found.items():
                if not entries:
                    warn(f"{key}: nicht im Archiv gefunden")
                    continue
                e = entries[0]
                log(f"  {key}: {e.name}  (method {e.method}, flags {e.flags:#x}, {e.csize:,} B gepackt)")
                try:
                    data, meta = p4k_read(f, e)
                except Exception as ex:  # noqa: BLE001
                    warn(f"{key}: {ex}")
                    continue
                report.setdefault("extracted", {})[key] = {"entry": e.as_dict(), "bytes": len(data),
                                                            "cryxml": data.startswith(b"CryXmlB"), **meta}
                if key == "default_profile":
                    raw_path = os.path.join(args.out, "defaultProfile.raw")
                    with open(raw_path, "wb") as o:
                        o.write(data)
                    try:
                        root = to_element(data)
                        ET.indent(root)
                        ET.ElementTree(root).write(os.path.join(args.out, "defaultProfile.xml"),
                                                   encoding="utf-8", xml_declaration=True)
                        report["profile"] = analyze_profile(root)
                        p = report["profile"]
                        log(f"  -> {p['actionmap_count']} Actionmaps, {p['action_count']} Aktionen")
                    except Exception as ex:  # noqa: BLE001
                        warn(f"defaultProfile: Umwandlung/Analyse fehlgeschlagen: {ex}")
                else:
                    with open(os.path.join(args.out, "global.ini"), "wb") as o:
                        o.write(data)
                    ini = load_ini(data.decode("utf-8", "replace"))
                    log(f"  -> {len(ini):,} Lokalisierungsschluessel")
            report["localization_keys"] = len(ini) if ini else 0
    finish(args.out)
    return 0


def finish(out):
    with open(os.path.join(out, "report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False, default=str)
    L = ["# SC-Probe Bericht", ""]
    b = report.get("build", {})
    L.append(f"- Installation: `{report.get('selected', {}).get('dir')}`")
    L.append(f"- Build: {b.get('Version')} ({b.get('Branch')})")
    am = report.get("actionmaps", {})
    L.append(f"- actionmaps.xml: vorhanden={am.get('exists')}, Rebinds={len(am.get('rebinds', []))}, "
             f"Joystick-Optionen={len([o for o in am.get('options', []) if o['type']=='joystick'])}")
    L.append(f"- Mappings-Dateien: {report.get('mappings_files')}")
    p = report.get("p4k")
    if p:
        L.append(f"- p4k: {p['entries']:,} Eintraege, zip64={p['zip64']}")
    ex = report.get("extracted", {})
    for k, v in ex.items():
        L.append(f"- extrahiert {k}: {v['bytes']:,} B, CryXmlB={v['cryxml']}, decrypt={v.get('decrypt')}")
    pr = report.get("profile")
    if pr:
        L += ["", "## Standardprofil", f"- Wurzel `{pr['root_tag']}`, {pr['actionmap_count']} Actionmaps, "
              f"{pr['action_count']} Aktionen",
              f"- Action-Attribute: {pr['action_attributes']}",
              f"- Standardbelegungen je Geraet: {pr['default_bound_by_device']}",
              f"- Aktivierungsbezogen: {pr['activation_related']}",
              "", "### Beispielaktion", "```xml", pr.get("sample_action") or "", "```",
              "", "### Gruppen (Actionmaps)"]
        L += [f"- `{g['name']}`: {g['actions']} Aktionen" for g in pr["groups"]]
    if report.get("hid"):
        L += ["", "## HID-Geraete"]
        L += [f"- {d['name']} VID {d['vid']} PID {d['pid']} Instanz=`{d['instance_tail']}` Serien-Hinweis={d['has_serial_hint']}" for d in report["hid"]]
    if report["warnings"]:
        L += ["", "## Warnungen"] + [f"- {w}" for w in report["warnings"]]
    with open(os.path.join(out, "report.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    log(f"\nBericht: {os.path.join(out, 'report.md')}")


if __name__ == "__main__":
    sys.exit(main())

