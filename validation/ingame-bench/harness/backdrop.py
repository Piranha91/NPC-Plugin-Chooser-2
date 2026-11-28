"""Backdrop probe (2026-11-27): the studio cell's fog as it is in the plugins, and the backdrop variants.

The bench finds a subject as the pixels that differ from a frame of the empty studio. QASmoke's dark stone wall hides
about a tenth of a subject's silhouette. The probe asks whether the cell's own fog can turn everything behind the
subject into one flat colour: Cell.SetFogPlanes / SetFogColor / SetFogPower (papyrus-reference vanilla\\Cell.md,
"interior, non-sky-lit cells only"). Papyrus has no getter for any of them (vanilla and SKSE Cell.md list setters
only), so the values to restore come from the plugins: the winning CELL record's XCLL lighting subrecord and, for
the fields it inherits, its lighting template (LGTM DATA). That is what this module reads. Nothing here talks to the
game; bench_driver.py does that, and only when the launch spec carries a `backdrop` entry.

  python backdrop.py [Skyrim.esm path ...]     offline: print what the plugins say about QASmoke's lighting

XCLL layout (SSE, 92 bytes; UESP Skyrim Mod:Mod File Format/CELL, checked against the bytes read here):
   0 ambient rgba        4 directional rgba    8 fog near colour rgba   12 fog near   16 fog far
  20 dir. rotation XY   24 dir. rotation Z    28 directional fade      32 fog clip distance   36 fog power
  40 directional ambient colours (32 bytes)   72 fog far colour rgba   76 fog max   80 light fade begin
  84 light fade end     88 inherit flags (bit 2 fog colour, 3 fog near, 4 fog far, 8 fog power, 9 fog max)
LGTM DATA starts with the same fields up to fog power at the same offsets (UESP Skyrim Mod:Mod File Format/LGTM)."""
import os, struct, sys, zlib

CELL_EDID = "QASmoke"
INHERIT = {"fogColor": 1 << 2, "fogNear": 1 << 3, "fogFar": 1 << 4, "fogPower": 1 << 8, "fogMax": 1 << 9}

# The variants the probe photographs (bench_driver.backdrop_* reads these unless the spec names its own).
# near: beyond the subject. The face camera stands 40 units from the head bone; the back of the head, Auri's
#   antlers and a quiver on the back stand within about 80 units of it (the coordinator's figure; the probe's
#   measure step (d) shows whether the subject was touched). 100 leaves a margin.
# far: in front of every background pixel. The floor is the nearest background: the bottom edge of the frame
#   looks down by VFOV/2 = 25.26 degrees from a camera 126 units above the floor (head bone 125 + CAM_DZ 1), so the
#   nearest floor in frame is ~267 units away (logged per subject as `floorDistance`, computed from the camera's
#   height over the studio floor). The wall is farther than the floor in every frame the bench has taken (the
#   floor shows between the wall and the subject's shoulders). 160 sits well inside both; the sweep repeats grey at
#   240 (just inside the floor) once per launch to show what a far plane near the floor does.
# colours: mid grey; magenta (255, 0, 255), the saturated colour least likely on an NPC - no skin, hair, eye or
#   warpaint tone in the corpus is near it, and it is opposite the greens and browns of Bosmer and CotG antlers, so
#   a colour key cannot eat into a subject; and a light neutral.
VARIANTS = [
    {"id": "grey", "rgb": [128, 128, 128], "near": 100.0, "far": 160.0},
    {"id": "magenta", "rgb": [255, 0, 255], "near": 100.0, "far": 160.0},
    {"id": "light", "rgb": [210, 210, 210], "near": 100.0, "far": 160.0},
]
SWEEP = {"id": "grey-far240", "rgb": [128, 128, 128], "near": 100.0, "far": 240.0}     # first subject only


def _records(buf, start, end, want_cell):
    """Yield (sig, flags, formid, data) of CELL records (and LGTM records) under the top-level CELL and LGTM groups.
    Cell children groups (persistent/temporary references, types 6, 8, 9, 10) are skipped by size."""
    o = start
    while o + 24 <= end:
        sig = buf[o:o + 4]
        sz = struct.unpack_from("<I", buf, o + 4)[0]
        if sig == b"GRUP":
            gtype = struct.unpack_from("<i", buf, o + 12)[0]
            if gtype in (0, 2, 3):           # top, interior block, interior sub-block
                yield from _records(buf, o + 24, o + sz, want_cell)
            o += sz
            continue
        flags, fid = struct.unpack_from("<II", buf, o + 8)
        if sig in (b"CELL", b"LGTM"):
            d = buf[o + 24:o + 24 + sz]
            if flags & 0x00040000:
                d = zlib.decompress(d[4:])
            yield sig, flags, fid, d
        o += 24 + sz


def _subs(d):
    out, q, big = {}, 0, None
    while q + 6 <= len(d):
        sig, n = d[q:q + 4].decode("latin1"), struct.unpack_from("<H", d, q + 4)[0]
        if sig == "XXXX":
            big = struct.unpack_from("<I", d, q + 6)[0]
            q += 6 + n
            continue
        if big is not None:
            n, big = big, None
        out.setdefault(sig, d[q + 6:q + 6 + n])
        q += 6 + n
    return out


def _masters(buf):
    hsz = struct.unpack_from("<I", buf, 4)[0]
    d, out, q = buf[24:24 + hsz], [], 0
    while q + 6 <= len(d):
        sig, n = d[q:q + 4], struct.unpack_from("<H", d, q + 4)[0]
        if sig == b"MAST":
            out.append(d[q + 6:q + 6 + n].rstrip(b"\0").decode("cp1252").lower())
        q += 6 + n
    return out


def _top_groups(buf):
    """(label, start, end) of the CELL and LGTM top groups."""
    hsz = struct.unpack_from("<I", buf, 4)[0]
    o, out = 24 + hsz, []
    while o + 24 <= len(buf):
        sz = struct.unpack_from("<I", buf, o + 4)[0]
        if buf[o:o + 4] == b"GRUP" and buf[o + 8:o + 12] in (b"CELL", b"LGTM"):
            out.append((buf[o + 8:o + 12].decode(), o + 24, o + sz))
        o += sz if buf[o:o + 4] == b"GRUP" else 24 + sz
    return out


def _rgb(d, off):
    return list(d[off:off + 3])


def xcll(d):
    f = lambda off: round(struct.unpack_from("<f", d, off)[0], 4)
    r = {"fogNearColor": _rgb(d, 8), "fogNear": f(12), "fogFar": f(16), "fogClip": f(32), "fogPower": f(36)}
    if len(d) >= 92:
        r.update(fogFarColor=_rgb(d, 72), fogMax=f(76), inherit=struct.unpack_from("<I", d, 88)[0])
    return r


def scan(paths, edid=CELL_EDID):
    """Read the cell's lighting from every plugin in `paths` (load order, lowest first). The last plugin that has the
    record wins, as in the game. Returns {"winner", "records": [per plugin], "template", "effective"}."""
    seen, lgtm = [], {}
    names = [os.path.basename(p).lower() for p in paths]
    for path in paths:
        buf = open(path, "rb").read()
        masters = _masters(buf)
        own = os.path.basename(path).lower()
        def key(fid):
            i = fid >> 24
            return (masters[i] if i < len(masters) else own, fid & 0xFFFFFF)
        for label, s, e in _top_groups(buf):
            for sig, flags, fid, d in _records(buf, s, e, True):
                subs = _subs(d)
                if sig == b"LGTM":
                    lgtm[key(fid)] = {"plugin": own, "editorId": subs.get("EDID", b"").rstrip(b"\0").decode("cp1252"),
                                      "data": xcll(subs["DATA"]) if "DATA" in subs else None}
                    continue
                if subs.get("EDID", b"").rstrip(b"\0").decode("cp1252").lower() != edid.lower():
                    continue
                rec = {"plugin": own, "formId": "%s:%06X" % (key(fid)[0], key(fid)[1]),
                       "dataFlags": struct.unpack_from("<H", subs["DATA"], 0)[0] if "DATA" in subs and len(subs["DATA"]) >= 2 else None,
                       "xcll": xcll(subs["XCLL"]) if "XCLL" in subs else None,
                       "ltmp": None}
                if "LTMP" in subs:
                    t = struct.unpack_from("<I", subs["LTMP"], 0)[0]
                    rec["ltmp"] = list(key(t)) if t else None
                seen.append(rec)
    out = {"plugins": names, "records": seen, "winner": seen[-1] if seen else None, "template": None, "effective": None}
    w = out["winner"]
    if w and w["xcll"]:
        eff = {k: w["xcll"].get(k) for k in ("fogNearColor", "fogFarColor", "fogNear", "fogFar", "fogPower", "fogMax")}
        eff["from"] = {k: "cell XCLL" for k in eff}
        tmpl = lgtm.get(tuple(w["ltmp"])) if w["ltmp"] else None
        if not w["ltmp"]:
            # No LTMP: the cell is taken to use Skyrim.esm's DefaultLightingTemplate (030E2), which is what the CK
            # shows for a cell without a template. That is an ASSUMPTION about the engine, marked as such.
            tmpl = next((dict(t, assumed="no LTMP on the cell: DefaultLightingTemplate assumed") for t in lgtm.values()
                         if t["editorId"].lower() == "defaultlightingtemplate"), None)
        out["template"] = tmpl
        inh = w["xcll"].get("inherit") or 0
        for field, bit in INHERIT.items():
            if inh & bit:
                keys = ["fogNearColor", "fogFarColor"] if field == "fogColor" else [field]
                for k in keys:
                    if tmpl and tmpl["data"] and k in tmpl["data"]:
                        eff[k], eff["from"][k] = tmpl["data"][k], f"template {tmpl['editorId']} (inherited)"
                    else:
                        eff["from"][k] = "cell XCLL, flagged inherited but no template value read"
        # DATA flag 0x100: "use sky lighting" - the Cell.md setters say non-sky-lit cells only
        eff["skyLit"] = bool((w["dataFlags"] or 0) & 0x100)
        eff["interior"] = bool((w["dataFlags"] or 0) & 0x1)
        out["effective"] = eff
        out["restoreCandidates"] = restore_candidates(out)
    return out


def restore_candidates(s):
    """The fog values to put back, most likely first: the values the cell draws with (inherited fields from the
    template), then the cell's own XCLL numbers. Which one the setters' fields correspond to is not documented; the
    driver restores the first, compares a frame of the empty studio with the one taken before any change, and tries
    the next when they differ."""
    out, w, e = [], s.get("winner"), s.get("effective")
    if e:
        out.append({"source": "effective (template-resolved)", "nearColor": e["fogNearColor"], "farColor": e["fogFarColor"],
                    "near": e["fogNear"], "far": e["fogFar"], "power": e["fogPower"]})
    if w and w.get("xcll"):
        x = w["xcll"]
        c = {"source": "cell XCLL as stored", "nearColor": x["fogNearColor"], "farColor": x.get("fogFarColor") or x["fogNearColor"],
             "near": x["fogNear"], "far": x["fogFar"], "power": x["fogPower"]}
        if not out or any(c[k] != out[0][k] for k in ("nearColor", "farColor", "near", "far", "power")):
            out.append(c)
    return out


def game_data_dir(instance):
    for line in open(os.path.join(instance, "ModOrganizer.ini"), encoding="utf-8", errors="replace"):
        if line.startswith("gamePath="):
            v = line.split("=", 1)[1].strip()
            v = v[len("@ByteArray("):-1] if v.startswith("@ByteArray(") else v
            return os.path.join(v.replace("\\\\", "\\"), "Data")
    raise RuntimeError("gamePath not found in ModOrganizer.ini")


def plugin_paths(instance, profile, names):
    """Files for plugin `names` (load order) as MO2 would serve them to this profile: the enabled mod highest in
    modlist.txt (top line = highest priority) that holds the file, else the game's Data folder. Unfound names are
    returned separately."""
    mods = [l[1:].strip() for l in open(os.path.join(instance, "profiles", profile, "modlist.txt"), encoding="utf-8-sig")
            if l.startswith("+")]
    data = game_data_dir(instance)
    paths, missing = [], []
    for n in names:
        hit = None
        for m in mods:
            p = os.path.join(instance, "mods", m, n)
            if os.path.isfile(p):
                hit = p
                break
        if not hit and os.path.isfile(os.path.join(data, n)):
            hit = os.path.join(data, n)
        (paths.append(hit) if hit else missing.append(n))
    return paths, missing


if __name__ == "__main__":
    import json
    args = sys.argv[1:]
    if not args:
        import bench_paths
        d = game_data_dir(bench_paths.INSTANCE)
        args = [os.path.join(d, n) for n in ("Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm")]
    print(json.dumps(scan(args), indent=1))
