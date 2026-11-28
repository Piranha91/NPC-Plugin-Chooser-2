"""NPC2 in-game pre-release check: orchestration. One launch = one MO2 profile, (for an arm) one headless NPC2 build,
one game run, one score.

  python bench_run.py install                 copy the ArmaRace fixture mods into the instance (once)
  python bench_run.py check                   reference groups: no subject supplied by two active mods
  python bench_run.py profile  <launch>       write profile N2B-<launch> (MO2 must be closed)
  python bench_run.py build    <arm>          profile + staged Settings.json + headless NPC2 build + manifest
  python bench_run.py launch   <launch> [--only A,B] [--tag T]    run the game for a prepared launch
  python bench_run.py go       <launch> [--only A,B] [--tag T]    verify corpus, (build,) launch, score
  python bench_run.py tier     full|refs|quick [--from <launch>]  several launches in order; stops a launch after
                                                                  MAX_TRIES failed tries and carries on with the next
  python bench_run.py d1check  [A4,A8]        offline: the form-version scan (H6) over each arm's kept output plugins

Frames go under bench_def.CAPTURE_ROOT_DEFAULT, or the folder named in N2B_CAPTURE_ROOT ("." = the launch folder).

Launch names: V R1 R2 R3 R4 (references) and A1..A12 (arms); see bench_def.py.
Run with Python 3.12 with PIL and numpy (bench_paths.PYTHON names the interpreter the 2.2.7 run used)."""
import glob, json, os, shutil, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bench_def as B
import bench_paths as P

PY = sys.executable
BASE_PROFILE = "Blank"                       # INI files only; the lists are written here
TEMPLATE = os.path.join(HERE, "profile_template")
FIXTURE_SRC = P.FIXTURE_SRC     # H5 (2026-11-27): v2, Christer and Thorgar
PLUGIN_EXT = (".esp", ".esm", ".esl")
MIN_FREE_DISK_GB = 8                          # per launch: ~3.4 GB captures + ~2.7 GB NPC2 output, with margin
MAX_TRIES = 2                                # game launches per launch name inside one `tier` run
IDLE_LOCK = P.IDLE_LOCK
HOLD_MAX_MIN = int(os.environ.get("N2B_HOLD_MAX_MIN", "110"))                           # a 42-subject arm is about 45 minutes; the hold stops the tree at this

# The queue's idle rule waits for 16 GB of free memory by default. N2B_MIN_FREE_GB sets that floor for THIS bench's
# holds only; N2B_EXEMPT names executables (";"-separated full paths) that do not count as a
# competing process for this bench's holds. Both are idle_lock's own per-call options; nothing shared is changed.
def idle_opts():
    # Default 4 GB (Piranha, 2026-11-25): no memory block is reserved for this bench. NPC2 adapts to memory pressure
    # and only runs slower unless RAM is severely short (under about 2 GB), and the game needs a few GB.
    o = ["--min-free-gb", os.environ.get("N2B_MIN_FREE_GB", "4")]
    if os.environ.get("N2B_TAKE_FIRST"):         # a short diagnostic goes ahead of this bench's own queued holds
        o += ["--take-first"]
    for e in filter(None, os.environ.get("N2B_EXEMPT", "").split(";")):
        o += ["--exempt", e]
    # N2B_EXEMPT_TREES: ";"-separated folders; every probe executable found under them when the hold starts is exempt
    # (the owner of those trees agreed that their probes may run alongside a game launch).
    for tree in filter(None, os.environ.get("N2B_EXEMPT_TREES", "").split(";")):
        for root, _, files in os.walk(tree):
            for fn in files:
                low = fn.lower()
                if low.endswith(".exe") and ("probe" in low or low == "conflictdiff.exe"):
                    o += ["--exempt", os.path.join(root, fn)]
    return o
# merged into every launch.json (e.g. {"notice_wait": 1.0}); N2B_DRIVER_OPTIONS (JSON) sets it for one command,
# e.g. {"t4": true} for the placement probe of 2026-11-26
DRIVER_OPTIONS = json.loads(os.environ.get("N2B_DRIVER_OPTIONS") or "{}")
TIERS = {
    "refs": ["V", "R1", "R2", "R3", "R4"],
    "quick": ["A2"],
    "full": ["A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9", "A10", "A11", "A12"],
}


def say(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def mo2_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ModOrganizer.exe"], capture_output=True, text=True).stdout
    return "ModOrganizer.exe" in out


def profile_name(launch):
    return f"N2B-{launch}"


def build_dir(launch):
    d = os.path.join(B.RUN_ROOT, "build", launch)
    os.makedirs(d, exist_ok=True)
    return d


def install():
    for m in os.listdir(FIXTURE_SRC):
        dst = os.path.join(B.MODS_DIR, m)
        if not os.path.isdir(dst):
            shutil.copytree(os.path.join(FIXTURE_SRC, m), dst)
            say("installed", m)
    for m in B.TOOLS + B.BASE_MODS + B.PATCHED + B.CONTEST:
        assert os.path.isdir(os.path.join(B.MODS_DIR, m)), f"mod folder missing from the instance: {m}"
    say("instance has every tool, base and environment mod")


# ------------------------------------------------------------------ reference groups
def check_ref_groups():
    """Every reference subject must be supplied by exactly one active plugin mod; a FaceGen-only mod that also
    covers it must sit below that mod. Loose files outside FaceGen must not collide between the group's mods."""
    d, _ = B.snapshot()
    ok = True
    for name, ref in B.REFS.items():
        keys = ref["mods"]
        entries = {k: B.mod_entry(d, B.MOD[k]) for k in keys}
        for subj in ref["subjects"]:
            f = B.fk(subj)
            own = B.S[subj][2]
            if name == "R4" and subj == "Vilja":
                own = "ViljaSOGS"
            if name == "R4" and subj == "Aela":
                own = "SSSP"
            holders = [k for k, e in entries.items() if f in e.get("NpcFormKeys", [])]
            others = [k for k in holders if k != own]
            bad = [k for k in others if not (entries[k].get("IsFaceGenOnlyEntry") and keys.index(k) < keys.index(own))] if own in keys else others
            if own in keys and own not in holders and not B.S[subj][3].startswith("guest") and subj != "Thorgar":
                say(f"  {name}: {subj} is NOT in its own mod {own}"); ok = False
            if bad:
                say(f"  {name}: {subj} ({own}) is also supplied by {bad}"); ok = False
        seen = {}
        for k in keys:
            for folder in entries[k]["CorrespondingFolderPaths"]:
                for root, _, files in os.walk(folder):
                    rel_root = os.path.relpath(root, folder).replace("\\", "/").lower()
                    if "facegendata" in rel_root or rel_root == ".":
                        continue
                    for fn in files:
                        rel = rel_root + "/" + fn.lower()
                        if rel in seen and seen[rel] != k:
                            say(f"  {name}: loose file collision {rel}: {seen[rel]} vs {k}"); ok = False
                        seen[rel] = k
        say(f"{name}: mods {keys}, {len(ref['subjects'])} subjects")
    return ok


# ------------------------------------------------------------------ profiles
def plugin_order(mods):
    """Plugins found in the enabled mod folders, masters first; a plugin with a master nobody supplies is dropped."""
    base = [l.strip() for l in open(os.path.join(TEMPLATE, "loadorder.txt"), encoding="utf-8") if l.strip() and not l.startswith("#")]
    base = [p for p in base if not p.lower().startswith("npc2bench")]
    have = {p.lower() for p in base}
    found = []                                   # (file name, path), lowest-priority mod first; a later mod overrides
    for m in mods:
        for fn in sorted(os.listdir(os.path.join(B.MODS_DIR, m))):
            if fn.lower().endswith(PLUGIN_EXT):
                found = [x for x in found if x[0].lower() != fn.lower()] + [(fn, os.path.join(B.MODS_DIR, m, fn))]
    found.sort(key=lambda x: 0 if x[0].lower().endswith((".esm", ".esl")) else 1)      # stable
    order, dropped, pending = [], [], list(found)
    progress = True
    while pending and progress:
        progress = False
        for item in list(pending):
            ms = [x.lower() for x in B.masters(item[1])]
            if all(x in have for x in ms):
                order.append(item[0]); have.add(item[0].lower()); pending.remove(item); progress = True
    for fn, path in pending:
        dropped.append({"plugin": fn, "missing": [x for x in B.masters(path) if x.lower() not in have]})
    # NPC2's output loads LAST, as NPC2 built it (manifest.loadOrder ends before it). The master passes above placed
    # it as soon as its masters were in, which put it BEFORE "AI Overhaul - USSEP Patch.esp" and the Scripted Addon
    # (their master sorts after them, so they resolve one pass later): in A2's game their vanilla NPC records won
    # over the output (Saadia, ArnielGane drew vanilla heads). Found 2026-11-25; arms run before the fix: A2.
    outdir = os.path.join(B.MODS_DIR, B.OUTPUT_PREFIX).lower()
    mine = {fn for fn, path in found if path.lower().startswith(outdir)}
    order = [x for x in order if x not in mine] + [x for x in order if x in mine]
    return base, order, dropped


def write_lists(launch, mods, plugins, base):
    dst = os.path.join(B.INSTANCE, "profiles", profile_name(launch))
    with open(os.path.join(dst, "modlist.txt"), "w", encoding="utf-8", newline="\r\n") as f:
        f.write("# This file was automatically generated by Mod Organizer.\n" + "\n".join(f"+{m}" for m in reversed(mods)) + "\n")
    cc = [p for p in base if p.lower().startswith("cc") or p.lower().startswith("_resourcepack")]
    with open(os.path.join(dst, "plugins.txt"), "w", encoding="utf-8", newline="\r\n") as f:
        f.write("# This file was automatically generated by Mod Organizer.\n" + "\n".join("*" + p for p in cc + plugins) + "\n")
    with open(os.path.join(dst, "loadorder.txt"), "w", encoding="utf-8", newline="\r\n") as f:
        f.write("# This file was automatically generated by Mod Organizer.\n" + "\n".join(base + plugins) + "\n")


INI_REQUIRED = [("General", "bAlwaysActive", "1"),          # the launch leaves the window minimised; without this the game stops ticking
                ("Interface", "bShowTutorials", "0"),       # dbharness.popup_preflight refuses a profile that can raise a tutorial box
                ("Camera", "bDisableAutoVanityMode", "1")]  # no idle camera orbit under a long capture


def quiet_ini(path):
    """Set INI_REQUIRED in the profile's own skyrim.ini, keeping every other line as it is."""
    lines = open(path, encoding="utf-8-sig", errors="replace").read().splitlines() if os.path.exists(path) else []
    for section, key, value in INI_REQUIRED:
        out, here, done, seen = [], False, False, False
        for l in lines:
            t = l.strip()
            if t.startswith("["):
                if here and not done:
                    out.append(f"{key}={value}"); done = True
                here = t.lower() == f"[{section.lower()}]"
                seen = seen or here
            elif here and t.lower().replace(" ", "").startswith(key.lower() + "="):
                if done:
                    continue
                l, done = f"{key}={value}", True
            out.append(l)
        if here and not done:
            out.append(f"{key}={value}")
        if not seen:
            out += [f"[{section}]", f"{key}={value}"]
        lines = out
    with open(path, "w", encoding="utf-8", newline=chr(13) + chr(10)) as f:
        f.write(chr(10).join(lines) + chr(10))


def make_profile(launch, with_output=False, mods=None, extra_mods=()):
    assert not mo2_running(), "close MO2 first: it rewrites profile files from memory"
    mods, out = (list(mods), None) if mods is not None else B.launch_mods(launch)
    # N2B_EXTRA_MODS (";"-separated mod folders), for ONE diagnostic command: mods added to the profile for the BUILD
    # as well as the launch. Added 2026-11-26 so NPC2's RaceMenu detection (wig tint mode Auto) can be exercised: the
    # headless builder looks for skee64.dll among the profile's enabled mods. Not the launch as defined: tag it.
    extra_mods = list(extra_mods) + [m for m in os.environ.get("N2B_EXTRA_MODS", "").split(";") if m]
    mods = mods + [m for m in extra_mods if m not in mods]
    for m in mods:
        assert os.path.isdir(os.path.join(B.MODS_DIR, m)), f"mod folder missing: {m}"
    if out and with_output:
        mods = mods + [out]
    src, dst = os.path.join(B.INSTANCE, "profiles", BASE_PROFILE), os.path.join(B.INSTANCE, "profiles", profile_name(launch))
    os.makedirs(os.path.join(dst, "saves"), exist_ok=True)
    for fn in os.listdir(src):
        p = os.path.join(src, fn)
        if os.path.isfile(p) and fn.lower().endswith(".ini"):
            shutil.copy2(p, os.path.join(dst, fn))
    quiet_ini(os.path.join(dst, "skyrim.ini"))
    base, plugins, dropped = plugin_order(mods)
    write_lists(launch, mods, plugins, base)
    info = {"launch": launch, "profile": profile_name(launch), "mods": mods, "plugins": plugins, "dropped": dropped,
            "output": out if with_output else None}
    json.dump(info, open(os.path.join(build_dir(launch), "profile.json"), "w"), indent=1)
    say(f"profile {profile_name(launch)}: {len(mods)} mods, {len(plugins)} plugins" + (f", DROPPED {dropped}" if dropped else ""))
    return info


# ------------------------------------------------------------------ build
def save_failure(log_text):
    """NPC2's own diagnosis when it could not write the output plugin: (message, [subject names it blames])."""
    if "FATAL SAVE ERROR" not in log_text:
        return None
    i = log_text.find("Diagnosing missing-master save failure")
    diag = log_text[i:i + 6000] if i >= 0 else log_text[log_text.find("FATAL SAVE ERROR"):][:1500]
    blamed = []
    for name, (form, plugin, _, _) in B.S.items():
        if f"({form}:{plugin})".lower() in diag.lower():
            blamed.append(name)
    return {"diagnosis": diag[:4000], "blamed": blamed}


def build_once(arm, exclude):
    settings, src = B.stage_settings(arm, exclude)
    bd = build_dir(arm)
    spath, mpath, specpath = os.path.join(bd, "Settings.json"), os.path.join(bd, "manifest.json"), os.path.join(bd, "spec.json")
    json.dump(settings, open(spath, "w", encoding="utf-8"), indent=1)
    json.dump({"instance": B.INSTANCE, "profile": profile_name(arm), "settingsPath": spath, "manifestPath": mpath},
              open(specpath, "w", encoding="utf-8"), indent=1)
    if os.path.exists(mpath):
        os.remove(mpath)
    env = dict(os.environ, NPC2_BENCH_SPEC=specpath)
    t0 = time.time()
    r = subprocess.run(["dotnet", "test", os.path.join(B.NPC2, "Tests", "NPC Plugin Chooser 2.Tests.csproj"), "-c", "Debug",
                        "--no-build", "--filter", "FullyQualifiedName~BenchOutputBuilder",
                        "--logger", "console;verbosity=normal"], capture_output=True, text=True, env=env, cwd=B.NPC2)
    open(os.path.join(bd, "dotnet-test.txt"), "w", encoding="utf-8").write(r.stdout + "\n--- stderr\n" + r.stderr)
    ok = r.returncode == 0 and os.path.exists(mpath)
    say(f"build {arm}: {'ran' if ok else 'FAILED'} in {time.time() - t0:.0f}s (settings from {os.path.basename(src)}"
        f"{', without ' + ','.join(exclude) if exclude else ''})")
    if not ok:
        say("\n".join([l for l in r.stdout.splitlines() if l.strip()][-25:]))
        return None, None
    m = json.load(open(mpath, encoding="utf-8"))
    log_text = open(os.path.splitext(mpath)[0] + ".log.txt", encoding="utf-8", errors="replace").read()
    return m, save_failure(log_text)


def build(arm):
    """Build the arm. If NPC2 cannot write its plugin, that is recorded as the arm's build failure (a FAIL for the
    subjects NPC2 itself blames) and the arm is rebuilt once without them, so the other subjects are still tested."""
    assert arm in B.ARMS, arm
    info = make_profile(arm, with_output=False)
    bd = build_dir(arm)
    state = {"arm": arm, "excluded": [], "failures": []}
    if os.environ.get("N2B_SETTINGS_OVERRIDES"):       # a diagnostic build, not the arm as defined: say so in build.json
        state["settingsOverrides"] = json.loads(os.environ["N2B_SETTINGS_OVERRIDES"])
    if os.environ.get("N2B_EXTRA_MODS"):
        state["extraMods"] = os.environ["N2B_EXTRA_MODS"]
    # What RaceMenu does in the profile NPC2 builds against (2026-11-26): wig tint mode Auto follows it, and the
    # scorer predicts the tint from this entry (bench_score.wig_tint). Read from the mod folders, not from NPC2's log.
    try:
        state["raceMenu"] = B.racemenu_state(info["mods"])
    except Exception as e:
        state["raceMenuError"] = repr(e)[:300]
    m, fail = build_once(arm, [])
    tries = 0
    while m is not None and fail and tries < 2:
        tries += 1
        state["failures"].append({"withSubjects": "all" if not state["excluded"] else f"without {state['excluded']}", **fail})
        shutil.copy2(os.path.join(bd, "manifest.log.txt"), os.path.join(bd, f"manifest.log.savefail-{tries}.txt"))
        say(f"  NPC2 could not write the plugin; it blames {fail['blamed'] or 'nobody it names'}")
        if not fail["blamed"]:
            break
        state["excluded"] = sorted(set(state["excluded"]) | set(fail["blamed"]))
        m, fail = build_once(arm, state["excluded"])
    state["unusable"] = bool(m is None or fail or not m["plugins"])
    json.dump(state, open(os.path.join(bd, "build.json"), "w"), indent=1)
    if m is None:
        return None
    say(f"  selected {m['selected']}, patched {len(m['patchedTargets'])}, rejected {len(m['invalidSelections'])}, "
        f"files {m['fileCount']}, plugins {m['plugins']}" + (f", EXCLUDED {state['excluded']}" if state["excluded"] else ""))
    if state["unusable"]:
        say(f"  build {arm} is unusable: no output plugin")
        return None
    make_profile(arm, with_output=True)
    return m


# ------------------------------------------------------------------ W1: a form version stamped onto older model data
# NPC2 finding, 2026-11-25 (A4: SkyPatcher + Create-and-Patch + Include). The Include delta copies the load order's
# winning record and applies the mod's property differences to it; FormVersion is one of those properties. A mod that
# only re-saved a vanilla record (High Poly NPC Overhaul - Resources.esp, ARMA 000CDD87 NakedTorsoWerewolfBeast) so
# makes NPC2 write Skyrim.esm's record (form version 39, MO2T/MO3T without the 12-byte header) stamped 44. The game then
# never finishes loading. Shown three ways on a one-NPC output (Ulfric): as built does not load; with that record
# removed it loads; with nothing changed but the stamp set back to 39 it loads (RUN\variants-A4-Ulfric*.json).
# The scan below is the static form of that finding (the report's D1).
# H6 (2026-11-26): NPC2 item N1 fixed the cause (confirmed in game 2026-11-26 01:31: the A4 output loads as built
# and the scan is empty), so the scan stays as a REGRESSION CHECK and the workaround is gone. Until then a hit was
# followed by one as-built launch and, if that did not load, by setting the stamps back to the masters' ("W1") so
# the rest of the arm could be read; build.json and launch.json of the first run still say so ("workarounds").
# Now a hit is the arm's result: d1_gate() writes it into build.json ("formVersionMismatch"), go() launches
# nothing, and the scorer fails every cell of the arm on it (bench_score.d1_failure). Nothing is altered.
MODEL_DATA = (b"MODT", b"MO2T", b"MO3T", b"MO4T", b"MO5T", b"DMDT")


def game_data_dir():
    for line in open(os.path.join(B.INSTANCE, "ModOrganizer.ini"), encoding="utf-8", errors="replace"):
        if line.startswith("gamePath="):
            v = line.split("=", 1)[1].strip()
            v = v[len("@ByteArray("):-1] if v.startswith("@ByteArray(") else v
            return os.path.join(v.replace("\\\\", "\\"), "Data")
    raise RuntimeError("gamePath not found in ModOrganizer.ini")


def plugin_records(path, want=None):
    """{(local form id, defining file name lower): (type, form version, header offset, [(sig, bytes)])}.
    `want`: only these keys get their subrecords parsed (a master file has ~900k records)."""
    import struct, zlib
    buf = open(path, "rb").read()
    hsz = struct.unpack_from("<I", buf, 4)[0]
    masters = [m.lower() for m in B.masters(path)]
    own, out = os.path.basename(path).lower(), {}
    stack = [(24 + hsz, len(buf))]
    while stack:
        o, end = stack.pop()
        while o < end:
            sz = struct.unpack_from("<I", buf, o + 4)[0]
            if buf[o:o + 4] == b"GRUP":
                stack.append((o + 24, o + sz))
                o += sz
                continue
            flags, fid = struct.unpack_from("<II", buf, o + 8)
            key = (fid & 0xFFFFFF, masters[fid >> 24] if (fid >> 24) < len(masters) else own)
            if want is None or key in want:
                d = buf[o + 24:o + 24 + sz]
                if flags & 0x00040000:
                    d = zlib.decompress(d[4:])
                subs, q, big = [], 0, None
                while q + 6 <= len(d):
                    sig, n = d[q:q + 4], struct.unpack_from("<H", d, q + 4)[0]
                    if sig == b"XXXX":
                        big = struct.unpack_from("<I", d, q + 6)[0]
                        q += 6 + n
                        continue
                    n, big = (big, None) if big is not None else (n, None)
                    subs.append((sig, d[q + 6:q + 6 + n]))
                    q += 6 + n
                out[key] = (buf[o:o + 4].decode("latin1"), struct.unpack_from("<H", buf, o + 20)[0], o, subs)
            o += 24 + sz
    return out


def headerless(d):
    """Model texture-hash data in the old layout: bare 12-byte hashes, no 12-byte count header in front."""
    import struct
    return len(d) >= 12 and len(d) % 12 == 0 and len(d) != 12 + 12 * struct.unpack_from("<I", d, 4)[0]


def stale_stamps(plugin, dirs):
    """Override records in `plugin` that carry their master's old-layout model data, byte for byte, under a newer
    form version than the master's record has."""
    own = os.path.basename(plugin).lower()
    md = lambda subs: [(s, d) for s, d in subs if s in MODEL_DATA]
    cand = {k: v for k, v in plugin_records(plugin).items() if k[1] != own and any(headerless(d) for _, d in md(v[3]))}
    hits = []
    for master in sorted({k[1] for k in cand}):
        mp = next((os.path.join(d, fn) for d in dirs if os.path.isdir(d) for fn in os.listdir(d) if fn.lower() == master), None)
        if not mp:
            continue
        src = plugin_records(mp, want={k for k in cand if k[1] == master})
        for k in sorted(k for k in cand if k[1] == master and k in src):
            t, ver, off, subs = cand[k]
            if ver > src[k][1] and md(subs) == md(src[k][3]):
                edid = next((d.rstrip(b"\0").decode("latin1") for s, d in subs if s == b"EDID"), "")
                hits.append({"record": f"{k[0]:06X}:{k[1]}", "type": t, "editorId": edid, "writtenVersion": ver,
                             "masterVersion": src[k][1], "offset": off})
    return hits


def scan_dirs(mods):
    """Where a plugin's masters are looked for: the game's Data folder, then these mod folders."""
    return [game_data_dir()] + [os.path.join(B.MODS_DIR, x) for x in mods]


def d1_scan(arm, m):
    """The static finding for one built arm: {plugin file: [hits]} (only plugins with hits)."""
    moddir = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - {arm}")
    info = json.load(open(os.path.join(build_dir(arm), "profile.json")))
    dirs = scan_dirs(info["mods"])
    found = {}
    for fn in m["plugins"]:
        hits = stale_stamps(os.path.join(moddir, fn), dirs)
        if hits:
            found[fn] = hits
    return found


def d1_gate(arm, m):
    """After a build, before the arm's launches (H6). Writes the static finding into build.json and returns it.
    A hit stops the arm (d1_blocks): nothing is launched and nothing is altered. A scan that could not run is
    written down (formVersionMismatchError) and does not stop the arm, as before: the launch itself then shows
    whether the output loads."""
    bj = os.path.join(build_dir(arm), "build.json")
    state = json.load(open(bj))
    try:
        found = d1_scan(arm, m)
    except Exception as e:
        state["formVersionMismatchError"] = repr(e)[:300]
        json.dump(state, open(bj, "w"), indent=1)
        say(f"  {arm}: form-version scan failed: {e!r}")
        return None
    state.pop("formVersionMismatchError", None)
    state["formVersionMismatch"] = found
    json.dump(state, open(bj, "w"), indent=1)
    if found:
        n = sum(len(v) for v in found.values())
        say(f"  {arm}: D1 REGRESSION: {n} override record(s) carry a master's old-layout model data under a newer form version: "
            + ", ".join(h["record"] for v in found.values() for h in v)[:300])
    return found


def d1_blocks(arm):
    """Why this arm's build must not be launched (H6), or None: the static scan has a hit (the game does not finish
    loading such a plugin: 2026-11-25, A4)."""
    bj = os.path.join(build_dir(arm), "build.json")
    state = json.load(open(bj)) if os.path.exists(bj) else {}
    if state.get("workarounds"):
        return None                                  # a build of the first run, altered then so it could be read
    found = state.get("formVersionMismatch")
    if found:
        return "D1 regression, " + ", ".join(h["record"] for v in found.values() for h in v)[:200]
    return None


def d1check(arms=None):
    """Offline: the static scan over the plugins each arm's last build left in build\\<arm>\\output-kept (drop_output
    keeps them), against the masters that build's profile enabled. Prints one line per plugin; True when none hits."""
    clean = True
    for arm in arms or list(B.ARMS):
        keep, pj = os.path.join(build_dir(arm), "output-kept"), os.path.join(build_dir(arm), "profile.json")
        plugins = sorted(fn for fn in (os.listdir(keep) if os.path.isdir(keep) else []) if fn.lower().endswith(PLUGIN_EXT))
        if not plugins or not os.path.exists(pj):
            say(f"{arm}: no kept plugin or no profile.json: not scanned"); clean = False
            continue
        dirs = scan_dirs(json.load(open(pj))["mods"])
        for fn in plugins:
            hits = stale_stamps(os.path.join(keep, fn), dirs)
            clean = clean and not hits
            say(f"{arm}: {fn}: " + (f"{len(hits)} HIT(S): " + ", ".join(h["record"] for h in hits) if hits else "no hit"))
    return clean


# ------------------------------------------------------------------ launch
KEPT_WITH_LAUNCH = ("build.json", "manifest.json", "manifest.log.txt", "profile.json")


def spec_for(launch, only=None, tag=None, extra=None):
    """(the launch's spec, its launch folder). Writes nothing."""
    subs = B.launch_subjects(launch) if launch in B.LAUNCHES else []
    bj = os.path.join(build_dir(launch), "build.json")
    excluded = json.load(open(bj)).get("excluded", []) if os.path.exists(bj) else []
    subs = [s for s in subs if s["name"] not in excluded]       # not in the build: nothing of NPC2's to look at
    if only:
        subs = [s for s in subs if s["name"] in only]
    info = json.load(open(os.path.join(build_dir(launch), "profile.json")))
    stamp = time.strftime("%Y%m%d-%H%M%S")
    out = os.path.join(B.RUN_ROOT, "live", f"{launch}{'-' + tag if tag else ''}-{stamp}")
    spec = {"launch": launch, "kind": "ref" if launch in B.REFS else "arm", "profile": profile_name(launch), "out": out,
            "subjects": subs, "required_plugins": [p for p in info["plugins"]],
            "swap_outfit": {"form": B.SWAP_OUTFIT[0], "plugin": B.SWAP_OUTFIT[1]},
            "reload": [n for n in (B.ARMS[launch]["reload"] if launch in B.ARMS else []) if not only or n in only]}
    spec["smp"] = B.SMP in info["mods"]          # the scorer refuses physics subjects read without it
    spec["workarounds"] = [w["id"] for w in (json.load(open(bj)).get("workarounds", []) if os.path.exists(bj) else [])]
    assert spec["smp"] or not B.launch_has_smp(launch), f"{launch} loads an SMP-dependent asset but its profile has no {B.SMP}"
    spec["shots"] = B.shots_dir(out)             # H7: the frames go under the capture root, not into the launch folder
    spec.update(extra or {})
    spec.update(DRIVER_OPTIONS)
    return spec, out


def launch_spec(launch, only=None, tag=None, extra=None):
    """Write the launch folder and its launch.json. The state of the build the launch is made from is copied into
    <launch folder>\\build (2026-11-26): build\\<arm>\\build.json and manifest.json are overwritten by the arm's next
    build, and a diagnostic launch (N2B_SETTINGS_OVERRIDES, N2B_EXTRA_MODS) could then no longer be scored against
    its own settings. The scorer reads these copies first (bench_score.launch_build)."""
    spec, out = spec_for(launch, only, tag, extra)
    os.makedirs(out, exist_ok=True)
    os.makedirs(spec["shots"], exist_ok=True)
    kept = os.path.join(out, "build")
    for fn in KEPT_WITH_LAUNCH:
        src = os.path.join(build_dir(launch), fn)
        if os.path.exists(src) and (launch in B.ARMS or fn == "profile.json"):
            os.makedirs(kept, exist_ok=True)
            shutil.copy2(src, os.path.join(kept, fn))
    path = os.path.join(out, "launch.json")
    json.dump(spec, open(path, "w"), indent=1)
    return path, out


def run_game(launch, only=None, tag=None, extra=None):
    path, out = launch_spec(launch, only, tag, extra)
    say(f"launch {launch}: {out}")
    if os.environ.get("IDLE_LOCK_SLOT"):       # already inside a hold (bench_run batch): the slot is ours
        r = subprocess.run([PY, os.path.join(HERE, "bench_driver.py"), path], capture_output=True, text=True)
        return finish_game(out, r)
    # The game is a competing process for every other houseCARL session's heavy work, so each launch takes the
    # machine slot through the shared queue (tools/idle_lock.py), one launch per hold so other sessions interleave.
    lock = os.path.join(B.RUN_ROOT, "GAME-LOCK-npc2bench.json")
    r = subprocess.run([PY, IDLE_LOCK, "hold", "--lock", lock, "--purpose", f"NPC2 bench {launch}: one game launch",
                        "--max-min", str(HOLD_MAX_MIN), "--wait-timeout-min", "240", "--slot-wait-min", "720"] + idle_opts() +
                       ["--", PY, os.path.join(HERE, "bench_driver.py"), path], capture_output=True, text=True)
    return finish_game(out, r)


def finish_game(out, r):
    open(os.path.join(out, "driver-stdout.txt"), "w", encoding="utf-8").write(r.stdout + "\n--- stderr\n" + r.stderr)
    steps = [json.loads(l).get("step") for l in open(os.path.join(out, "log.jsonl"), encoding="utf-8")] if os.path.exists(os.path.join(out, "log.jsonl")) else []
    done, aborted = "sequence_done" in steps, "abort" in steps
    say(f"  rc={r.returncode} done={done} aborted={aborted} steps={len(steps)}")
    return out, done and not aborted


def log_rows(out, step):
    p = os.path.join(out or "", "log.jsonl")
    return [r for r in (json.loads(l) for l in open(p, encoding="utf-8")) if r.get("step") == step] if os.path.exists(p) else []


def probe(name, mods):
    """Does a new game load with exactly these mods enabled? One short launch, no subjects."""
    make_profile(name, mods=mods)
    out, _ = run_game(name, tag="probe", extra={"probe": True, "load_limit": 150})
    rows = log_rows(out, "probe_result")
    res = {"name": name, "mods": mods, "out": out, "loaded": bool(rows and rows[0]["loaded"]), "seconds": rows[0]["seconds"] if rows else None,
           "plugins": rows[0].get("plugins") if rows else None, "process": [r.get("process") for r in log_rows(out, "no_cell")]}
    say(f"probe {name}: loaded={res['loaded']} in {res['seconds']}s with {len(mods)} mods")
    return res


def hold(purpose, inner_args):
    """Run `bench_run.py <inner_args>` as ONE hold of the machine slot."""
    lock = os.path.join(B.RUN_ROOT, "GAME-LOCK-npc2bench.json")
    r = subprocess.run([PY, IDLE_LOCK, "hold", "--lock", lock, "--purpose", purpose, "--max-min", str(HOLD_MAX_MIN),
                        "--wait-timeout-min", "240", "--slot-wait-min", "720"] + idle_opts() + ["--", PY, os.path.abspath(__file__)] + inner_args,
                       capture_output=True, text=True)
    open(os.path.join(B.RUN_ROOT, f"hold-{time.strftime('%Y%m%d-%H%M%S')}.txt"), "w", encoding="utf-8").write(r.stdout + "\n--- stderr\n" + r.stderr)
    return r.returncode


SMOKE = ["Irileth", "Anoriath", "Auri", "Lydia", "Christer", "Ulfric"]
REPEATS = {"V": ["Irileth", "Anoriath", "Auri", "Lydia", "Christer", "Ulfric", "Thorgar", "Aela"],
           "R1": ["Irileth", "Anoriath", "Auri", "Christer", "Serana"], "R2": ["Ulfric", "Urag"], "R4": ["Lydia", "Vilja"]}
FACEGEN_CONTROL = "Irileth"


def h1():
    """First hold: which mods stop a new game from loading, then a smoke of arm A1 on whatever loads."""
    res = {"probes": [], "bisect": [], "smoke": None}
    status = os.path.join(B.RUN_ROOT, "h1-status.json")
    T = list(B.TOOLS)
    sets = [("P5-vanilla", T + B.BASE_MODS)]
    last_ok = None
    for name, mods in sets:
        r = probe(name, mods)
        res["probes"].append(r); json.dump(res, open(status, "w"), indent=1)
        if r["loaded"]:
            last_ok = name
    if res["probes"][-1]["loaded"]:
        # the base load order is fine: does NPC2's A1 output load, and if not, without which subject does it?
        groups = [[], ["Lydia"], ["Vilja"], ["Auri"], ["Christer"], [n for n in B.S if B.S[n][2] == "HPNO"], ["Serana"]]
        for ex in groups:
            make_profile("A1", with_output=False)
            m, fail = build_once("A1", ex)
            if m is None or fail or not m["plugins"]:
                res["bisect"].append({"exclude": ex, "built": False}); continue
            make_profile("A1", with_output=True)
            out, _ = run_game("A1", tag="probe", extra={"probe": True, "load_limit": 150})
            rows = log_rows(out, "probe_result")
            ok = bool(rows and rows[0]["loaded"])
            res["bisect"].append({"exclude": ex, "built": True, "loaded": ok, "out": out, "process": [r.get("process") for r in log_rows(out, "no_cell")]})
            say(f"A1 without {ex or 'nobody'}: loaded={ok}")
            json.dump(res, open(status, "w"), indent=1)
            if ok:
                json.dump({"arm": "A1", "excluded": ex, "failures": [], "unusable": False, "note": "h1 bisect: excluded so the game loads"},
                          open(os.path.join(build_dir("A1"), "build.json"), "w"), indent=1)
                out, sok = run_game("A1", only=[n for n in SMOKE if n not in ex], tag="smoke2", extra={"notice_test": True})
                res["smoke"] = {"out": out, "ok": sok, "excluded": ex}
                json.dump(res, open(status, "w"), indent=1)
                # the same subjects with no appearance mod: real mod/vanilla pairs to set the scorer's regions on
                make_profile("V")
                vout, vok = run_game("V", only=SMOKE, tag="smoke2")
                res["vanilla_smoke"] = {"out": vout, "ok": vok}
                break
    res["finished"] = True
    json.dump(res, open(status, "w"), indent=1)
    say("h1 finished")


def refs_batch():
    """One hold: the five reference launches, a repeat of a few subjects of each for the noise floor, and the
    known-answer build for the scorer's controls (arm A1 with one subject's FaceGen head mesh deleted)."""
    res = {"tier": tier("refs"), "repeats": {}, "facegen": None}
    status = os.path.join(B.RUN_ROOT, "refs-batch-status.json")
    for launch, names in REPEATS.items():
        names = [n for n in names if n in [x["name"] for x in B.launch_subjects(launch)]]
        make_profile(launch)
        out, ok = run_game(launch, only=names, tag="repeat")
        res["repeats"][launch] = {"out": out, "ok": ok, "subjects": names}
        json.dump(res, open(status, "w"), indent=1)
    m = build("A1")
    if m:
        form, plugin = B.S[FACEGEN_CONTROL][0], B.S[FACEGEN_CONTROL][1]
        nif = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - A1", "meshes", "actors", "character", "facegendata", "facegeom",
                           plugin, form.zfill(8) + ".nif")
        had = os.path.exists(nif)
        if had:
            os.remove(nif)                 # NPC2Bench's own output folder; the next build of A1 writes it again
            out, ok = run_game("A1", only=[FACEGEN_CONTROL, "Ulfric", "Anoriath"], tag="ctlfacegen")
            res["facegen"] = {"out": out, "ok": ok, "deleted": nif}
        else:
            res["facegen"] = {"error": "FaceGen mesh not found in the A1 output", "path": nif}
        drop_output("A1")
    res["finished"] = True
    json.dump(res, open(status, "w"), indent=1)
    say("refs batch finished")


SMP_LAUNCHES = ["R1", "A1", "A2", "A12", "A3"]
SMP_SUBJECTS = ["Auri", "Irileth"]


def smp_batch():
    """Launches with Faster HDT-SMP enabled, outside the scored tier: does physics hair still work on NPC2's output?
    Auri's FoxGlove wig is an SMP mesh (22a_1.nif names 22a.xml). R1 is the mod alone; the arms carry the wig as an
    outfit item (A1), as a head part (A2, Convert To Head Parts), on the skin (A12) and under SkyPatcher (A3).
    Irileth has no SMP mesh: the subject that must NOT change.
    Read, declared before the launches (2026-11-25): (1) hdtSMP64.log of each launch names the wig's physics file
    without an error; (2) Auri's frame with SMP differs from her frame without it in the same load order by at
    least DIFF_MIN_NOISE x the repeat noise while Irileth's stays within SAME_MAX_NOISE x; (3) the three frames of
    a burst agree with each other more closely than (2), i.e. the hair has settled rather than being frozen in its
    rest pose or thrashing; (4) Auri on NPC2's output against Auri with the mod alone, both with SMP, is a reader's
    comparison on the sheet: physics hair never lands in exactly the same place twice."""
    res, status = {}, os.path.join(B.RUN_ROOT, "smp-status.json")
    for launch in SMP_LAUNCHES:
        try:
            if launch in B.ARMS:
                if build(launch) is None:
                    res[launch] = {"error": "build unusable"}; continue
                make_profile(launch, with_output=True, extra_mods=[B.SMP])
            else:
                make_profile(launch, extra_mods=[B.SMP])
            out, ok = run_game(launch, only=SMP_SUBJECTS, tag="smp", extra={"settle": 6, "burst": True})
            res[launch] = {"out": out, "ok": ok}
        except Exception as e:
            res[launch] = {"error": repr(e)[:400]}
        finally:
            if launch in B.ARMS:
                drop_output(launch)
            else:
                make_profile(launch)        # the reference profile goes back to what the scored launches use
        json.dump(res, open(status, "w"), indent=1)
    res["finished"] = True
    json.dump(res, open(status, "w"), indent=1)
    say("smp batch finished")


RECAPTURE = [("V", ["Vilja"]), ("R1", ["Vilja"]), ("R3", ["Adelaisa"]), ("R4", ["Vilja"])]


def recapture():
    """Reference readings the first reference batch missed (the subject ran before its freeze took)."""
    res = {}
    for launch, names in RECAPTURE:
        out, ok = go(launch, only=names)
        res[launch] = {"out": out, "ok": ok, "done": sorted(done_subjects(out))}
        json.dump(res, open(os.path.join(B.RUN_ROOT, "recapture-status.json"), "w"), indent=1)
    res["finished"] = True
    json.dump(res, open(os.path.join(B.RUN_ROOT, "recapture-status.json"), "w"), indent=1)


def waitfor(path, seconds):
    """Block until a status file says finished (or `seconds` pass). Prints one line of progress either way."""
    t0 = time.time()
    while time.time() - t0 < seconds:
        try:
            d = json.load(open(path))
            if d.get("finished"):
                print("FINISHED", path); return 0
        except Exception:
            pass
        time.sleep(15)
    live = sorted(glob.glob(os.path.join(B.RUN_ROOT, "live", "*")), key=os.path.getmtime)
    last = live[-1] if live else None
    n = sum(1 for l in open(os.path.join(last, "log.jsonl"), encoding="utf-8") if '"step": "subject"' in l) if last and os.path.exists(os.path.join(last, "log.jsonl")) else 0
    print(f"STILL RUNNING after {int(time.time() - t0)} s; newest launch folder {os.path.basename(last) if last else None}, {n} subject rows so far")
    return 2


def existing_parent(path):
    """The nearest folder that exists at or above `path` (disk_usage needs one; the capture root is made on demand)."""
    path = os.path.abspath(path)
    while not os.path.isdir(path) and os.path.dirname(path) != path:
        path = os.path.dirname(path)
    return path


def go(launch, only=None, tag=None, rebuild=True):
    import bench_corpus
    bad = bench_corpus.verify(quiet=True)
    if bad:
        say(f"CORPUS CHANGED, refusing to run: {bad[:5]}")
        return None, False
    # Disk guard. A full arm writes about 3.4 GB of screenshots (3840x2160 PNG, ~17 MB each) under RUN_ROOT\live and
    # NPC2 writes about 2.7 GB into the instance's mods folder. On 2026-11-25 the run drive was down to 2.7 GB free
    # with a full arm still to come (42 GB of captures by then); finished launch folders were moved to another drive
    # with a junction left at each old path. Refuse to start rather than fill a shared drive mid-launch.
    # H7 (2026-11-26): the frames go under the capture root (bench_def.shots_dir), so that is the drive to guard.
    shots_at = B.RUN_ROOT if B.capture_root() == "." else B.capture_root()
    for what, d in (("captures", shots_at), ("NPC2 output", B.MODS_DIR)):
        free = shutil.disk_usage(existing_parent(d)).free / 2**30
        if free < MIN_FREE_DISK_GB:
            say(f"DISK: {free:.1f} GB free for {what} at {d}, need {MIN_FREE_DISK_GB}; not launching {launch}. "
                f"Free space there, or name another capture root in N2B_CAPTURE_ROOT.")
            return None, False
    if launch in B.ARMS:
        if rebuild or not os.path.exists(os.path.join(build_dir(launch), "manifest.json")):
            m = build(launch)
            if m is None:
                return None, False
            d1_gate(launch, m)
        blocked = d1_blocks(launch)          # H6: also on a retry that does not rebuild
        if blocked:
            say(f"  {launch}: NOT LAUNCHED ({blocked}). The arm fails on its build; see build.json")
            return None, False
    else:
        make_profile(launch)
    out, ok = run_game(launch, only, tag)
    if not tag:
        # Every untagged run of a launch counts: the scorer takes each subject's first usable reading across them,
        # so a second try only has to cover what the first one missed.
        lp = os.path.join(B.RUN_ROOT, "live", f"{launch}.latest")
        outs = json.load(open(lp)) if os.path.exists(lp) and open(lp).read().strip().startswith("[") else []
        json.dump(outs + [out], open(lp, "w"))
    return out, ok


def done_subjects(out):
    """Subjects with a usable default-state reading in one output folder."""
    names, p = set(), os.path.join(out or "", "log.jsonl")
    if os.path.exists(p):
        for line in open(p, encoding="utf-8"):
            try:
                r = json.loads(line)
            except Exception:
                continue
            if r.get("step") == "subject" and r.get("state") == "default" and not r.get("UNASSESSED"):
                names.add(r["name"])
    return names


def stopped_at(out):
    """The subject a launch was working on when it aborted (begun, no reading), or None."""
    p = os.path.join(out or "", "log.jsonl")
    if not os.path.exists(p):
        return None
    begun, read, aborted = None, set(), False
    for line in open(p, encoding="utf-8"):
        try:
            r = json.loads(line)
        except Exception:
            continue
        if r.get("step") == "begin":
            begun = r.get("name")
        elif r.get("step") == "subject" and not r.get("UNASSESSED"):
            read.add(r.get("name"))
        elif r.get("step") == "abort":
            aborted = True
    return begun if aborted and begun and begun not in read else None


def drop_output(launch):
    """An arm's NPC2 output is about 2.7 GB and is rebuilt from the corpus on demand: remove it once photographed."""
    d = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - {launch}")
    if launch in B.ARMS and os.path.isdir(d):
        # keep what a later diagnosis needs and is small: the plugins and every distributor/config file
        keep = os.path.join(build_dir(launch), "output-kept")
        shutil.rmtree(keep, ignore_errors=True)
        for root, _, files in os.walk(d):
            rel = os.path.relpath(root, d)
            if rel.split(os.sep)[0].lower() in ("meshes", "textures"):
                continue
            for fn in files:
                src = os.path.join(root, fn)
                if os.path.getsize(src) < 20 * 1024 * 1024:
                    os.makedirs(os.path.join(keep, rel), exist_ok=True)
                    shutil.copy2(src, os.path.join(keep, rel, fn))
        shutil.rmtree(d, ignore_errors=True)


def tier(name, start=None):
    if name.startswith("list:"):                 # an explicit batch, e.g. list:A1,A2 (one hold's worth)
        order = name[5:].split(",")
        name = "batch-" + "-".join(order)
    else:
        order = TIERS[name]
    if start:
        order = order[order.index(start):]
    results = {}
    status_path = os.path.join(B.RUN_ROOT, f"tier-{name}-status.json")
    for launch in order:
        # The pause is also honoured between launches: one in flight finishes, the next does not start.
        if os.path.exists(PAUSE_FILE):
            say(f"PAUSED before {launch}: the group stops here, not run: {order[order.index(launch):]}")
            results["paused-before"] = launch
            break
        tries = 0
        ok, out, outs, only = False, None, [], None
        while tries < MAX_TRIES and not ok and not (tries and os.path.exists(PAUSE_FILE)):
            tries += 1
            out, ok = go(launch, only=only, rebuild=(tries == 1))
            outs.append(out)
            if not ok:
                say(f"{launch}: try {tries} failed")
                done = set().union(*[done_subjects(o) for o in outs if o])
                only = [s["name"] for s in B.launch_subjects(launch) if s["name"] not in done] or None
                # A subject the game died on goes LAST in the retry: the others get photographed first, and the
                # second death at the same subject is the repeat that makes it a finding (A2, 2026-11-25: both
                # tries died spawning Auri, the retry at once, and twelve other subjects were never reached).
                bad = [b for b in (stopped_at(o) for o in outs if o) if b]
                if only and bad:
                    only = [n for n in only if n not in bad] + [n for n in only if n in bad]
                    say(f"{launch}: the game stopped at {bad}; retry order puts them last")
                time.sleep(20)
        drop_output(launch)
        scored = None
        try:
            if launch in B.ARMS and os.path.exists(os.path.join(build_dir(launch), "manifest.json")):
                import bench_score
                scored = bench_score.score_arm(launch)["counts"]
        except Exception as e:
            scored = "score error: " + repr(e)[:300]
        results[launch] = {"ok": ok, "outs": outs, "tries": tries, "scored": scored}
        say(f"{launch}: ok={ok} tries={tries} scored={scored}")
        json.dump(results, open(status_path, "w"), indent=1)
    say("tier", name, "finished:", {k: (v["ok"] if isinstance(v, dict) else v) for k, v in results.items()})
    json.dump({"finished": True, "results": results}, open(status_path, "w"), indent=1)
    return results


def strip_subrecord(path, formid, sig, dst):
    """Write to `dst` a copy of plugin `path` without the `sig` subrecords of record `formid` (as stored in the file,
    e.g. 0x00013746). Nothing else changes: the record's size and the sizes of the groups around it shrink by the
    bytes removed. Returns the removed subrecords' data (hex). Refuses a compressed record."""
    import struct
    d = bytearray(open(path, "rb").read())
    sig = sig.encode()
    found = []

    def walk(o, end, groups):
        while o < end:
            t, size = bytes(d[o:o + 4]), struct.unpack_from("<I", d, o + 4)[0]
            if t == b"GRUP":
                r = walk(o + 24, o + size, groups + [o])
                if r:
                    return r
                o += size
            else:
                if struct.unpack_from("<I", d, o + 12)[0] == formid and t != b"TES4":
                    return o, size, groups
                o += 24 + size
        return None

    hit = walk(0, len(d), [])
    assert hit, f"record {formid:08X} not in {path}"
    o, size, groups = hit
    assert not struct.unpack_from("<I", d, o + 8)[0] & 0x00040000, "record is compressed"
    p, end, big, cuts = o + 24, o + 24 + size, None, []
    while p < end:
        s, n = bytes(d[p:p + 4]), struct.unpack_from("<H", d, p + 4)[0]
        if s == b"XXXX":
            big = struct.unpack_from("<I", d, p + 6)[0]; p += 6 + n; continue
        n = big if big is not None else n
        big = None
        if s == sig:
            cuts.append((p, 6 + n)); found.append(bytes(d[p + 6:p + 6 + n]).hex())
        p += 6 + n
    assert p == end, "subrecords do not end at the record's end"
    gone = sum(n for _, n in cuts)
    for p, n in reversed(cuts):
        del d[p:p + n]
    struct.pack_into("<I", d, o + 4, size - gone)
    for g in groups:
        struct.pack_into("<I", d, g + 4, struct.unpack_from("<I", d, g + 4)[0] - gone)
    open(dst, "wb").write(d)
    return found


def striptest(arm, formid, sig, ready_limit=150):
    """A counter-test for "this arm's output stops the game from loading" (A4, 2026-11-25: two launches never became
    ready; the only self-link among all loaded races was NordRace ATKR -> NordRace, added by N2B_A4.esp).
    One build, two short launches in the same profile and code: the plugin as NPC2 built it, then the same plugin
    with only that subrecord removed. Known answers: as built must NOT become ready within `ready_limit` s (it took
    under 30 s in every launch that loaded today) and the stripped one must. Any other outcome is reported as such."""
    res, status = {"arm": arm, "formid": formid, "sig": sig, "readyLimit": ready_limit}, os.path.join(B.RUN_ROOT, f"striptest-{arm}-{sig}.json")
    if build(arm) is None:
        res["error"] = "build unusable"
        json.dump(res, open(status, "w"), indent=1)
        return res
    moddir = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - {arm}")
    plugin = os.path.join(moddir, json.load(open(os.path.join(build_dir(arm), "profile.json")))["plugins"][-1])
    keep = os.path.join(build_dir(arm), "striptest")
    os.makedirs(keep, exist_ok=True)
    orig = os.path.join(keep, os.path.basename(plugin) + ".as-built")
    shutil.copy2(plugin, orig)

    def one(tag):
        out, _ = run_game(arm, tag=tag, extra={"probe": True, "ready_limit": ready_limit, "load_limit": 150})
        ready, probe = log_rows(out, "ready"), log_rows(out, "probe_result")
        return {"out": out, "ready": bool(ready), "readySeconds": ready[0].get("seconds") if ready else None,
                "newGameLoaded": bool(probe and probe[0].get("loaded")), "abort": [r.get("error", "")[:200] for r in log_rows(out, "abort")]}
    try:
        res["asBuilt"] = one("ctl-asbuilt")
        json.dump(res, open(status, "w"), indent=1)
        stripped = os.path.join(keep, os.path.basename(plugin) + f".no-{sig}")
        res["removed"] = strip_subrecord(orig, int(formid, 16), sig, stripped)
        res["bytes"] = {"asBuilt": os.path.getsize(orig), "stripped": os.path.getsize(stripped)}
        shutil.copy2(stripped, plugin)
        res["stripped"] = one(f"ctl-no{sig}")
    except Exception as e:
        res["error"] = repr(e)[:400]
    finally:
        shutil.copy2(orig, plugin)             # output-kept must hold what NPC2 built, not the test variant
        drop_output(arm)
    a, s = res.get("asBuilt", {}), res.get("stripped", {})
    res["verdict"] = ("the subrecord is the cause: as built never ready, stripped ready" if a and s and not a["ready"] and s["ready"] else
                      "NOT the cause alone: both failed to become ready" if a and s and not a["ready"] and not s["ready"] else
                      "the failure did not repeat: as built became ready" if a and a["ready"] else "incomplete")
    res["finished"] = True
    json.dump(res, open(status, "w"), indent=1)
    say(f"striptest {arm} {sig}: {res['verdict']}")
    return res


def plugin_items(path):
    """([top-level group labels in file order], {label: [formids of the records directly in it]})."""
    import struct
    d = open(path, "rb").read()
    o = 24 + struct.unpack_from("<I", d, 4)[0]
    labels, recs = [], {}
    while o < len(d):
        size, label = struct.unpack_from("<I", d, o + 4)[0], d[o + 8:o + 12].decode("latin1")
        labels.append(label); recs[label] = []
        p, end = o + 24, o + size
        while p < end:
            t, sz = bytes(d[p:p + 4]), struct.unpack_from("<I", d, p + 4)[0]
            if t == b"GRUP":
                p += sz
            else:
                recs[label].append(struct.unpack_from("<I", d, p + 12)[0]); p += 24 + sz
        o += size
    return labels, recs


def drop_items(path, dst, groups=(), records=()):
    """Copy plugin `path` to `dst` without the top-level groups labelled in `groups` and without the records whose
    formid is in `records` (records directly inside a top-level group). Group sizes follow. Nothing else changes."""
    import struct
    d = open(path, "rb").read()
    o = 24 + struct.unpack_from("<I", d, 4)[0]
    out = bytearray(d[:o])
    while o < len(d):
        size, label = struct.unpack_from("<I", d, o + 4)[0], d[o + 8:o + 12].decode("latin1")
        if label not in groups:
            g = bytearray(d[o:o + 24])
            p, end = o + 24, o + size
            while p < end:
                t, sz = bytes(d[p:p + 4]), struct.unpack_from("<I", d, p + 4)[0]
                n = sz if t == b"GRUP" else 24 + sz
                if t == b"GRUP" or struct.unpack_from("<I", d, p + 12)[0] not in records:
                    g += d[p:p + n]
                p += n
            struct.pack_into("<I", g, 4, len(g))
            out += g
        o += size
    open(dst, "wb").write(out)


def loadbisect(arm, ready_limit=100, max_launches=20):
    """Which part of this arm's output plugin stops the game from loading? (A4, 2026-11-25: four launches as built
    never became ready; removing NordRace's self-pointing ATKR alone did not help.) One build, then short launches
    of variants of the plugin with whole groups, then single records, REMOVED: a variant that becomes ready says the
    cause is in what was removed. Halving assumes ONE culprit; when neither half's removal helps, it stops and says
    so. A removed new record leaves dangling links in what remains: that can only make a variant fail, never load,
    so a variant that loads is sound evidence and one that fails is weaker. Every launch is logged with its answer."""
    res = {"arm": arm, "readyLimit": ready_limit, "launches": []}
    status = os.path.join(B.RUN_ROOT, f"loadbisect-{arm}.json")
    save = lambda: json.dump(res, open(status, "w"), indent=1)
    if build(arm) is None:
        res["error"] = "build unusable"; save(); return res
    moddir = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - {arm}")
    plugin = os.path.join(moddir, json.load(open(os.path.join(build_dir(arm), "profile.json")))["plugins"][-1])
    keep = os.path.join(build_dir(arm), "loadbisect")
    os.makedirs(keep, exist_ok=True)
    orig = os.path.join(keep, os.path.basename(plugin) + ".as-built")
    shutil.copy2(plugin, orig)
    labels, recs = plugin_items(orig)
    res["groups"] = {l: len(recs[l]) for l in labels}

    def loads(tag, groups=(), records=()):
        if len(res["launches"]) >= max_launches:
            raise RuntimeError("launch limit reached")
        var = os.path.join(keep, f"{len(res['launches']):02d}-{tag}.esp")
        drop_items(orig, var, set(groups), set(records))
        shutil.copy2(var, plugin)
        out, _ = run_game(arm, tag="bis", extra={"probe": True, "ready_limit": ready_limit, "load_limit": 150})
        ready = log_rows(out, "ready")
        row = {"tag": tag, "removedGroups": sorted(groups), "removedRecords": ["%08X" % r for r in records], "variant": var,
               "bytes": os.path.getsize(var), "out": out, "ready": bool(ready), "readySeconds": ready[0].get("seconds") if ready else None}
        res["launches"].append(row); save()
        say(f"loadbisect {arm}: removed {tag} -> {'READY' if row['ready'] else 'not ready'}")
        return row["ready"]

    def find(items, test, name):
        """Smallest part of `items` whose removal lets the game load, by halving; None when neither half does."""
        while len(items) > 1:
            a, b = items[:len(items) // 2], items[len(items) // 2:]
            if test(a):
                items = a
            elif test(b):
                items = b
            else:
                res[f"{name}Ambiguous"] = [str(i) for i in items]
                return None
        return items[0]
    try:
        if not loads("all-groups", groups=labels):
            res["verdict"] = "even the plugin with every group removed does not load: the cause is not a record in it"
        else:
            g = find(labels, lambda part: loads("groups-" + "+".join(part), groups=part), "groups")
            res["culpritGroup"] = g
            if g is None:
                res["verdict"] = "no single group's removal lets it load (see groupsAmbiguous): more than one cause, or an interaction"
            else:
                ids = recs[g]
                r = find(ids, lambda part: loads(f"{g}-{len(part)}rec-{part[0]:08X}", records=part), "records") if len(ids) > 1 else ids[0]
                res["culpritRecord"] = None if r is None else "%08X" % r
                res["verdict"] = (f"removing the one {g} record {r:08X} lets the game load" if r is not None else
                                  f"the cause is in group {g}, but no single record's removal lets it load (see recordsAmbiguous)")
    except Exception as e:
        res["error"] = repr(e)[:400]
    finally:
        shutil.copy2(orig, plugin)             # output-kept must hold what NPC2 built, not a variant
        drop_output(arm)
    res["finished"] = True
    save()
    say(f"loadbisect {arm}: {res.get('verdict') or res.get('error')}")
    return res


def npcbisect(arm, first=None, ready_limit=100, max_launches=14):
    """Which NPC selections make this arm's output stop the game from loading? (Piranha's method, 2026-11-25, in
    place of removing records from the plugin: every variant here is a plugin NPC2 itself built for a SUBSET of the
    arm's NPCs, so nothing dangles and the answer is a selection a user could make.)
    Known before it starts: the full selection does not load (A4: five launches). `first` is a suspect to try alone
    and to leave out, before any halving. Then halving on "still does not load": keep half the NPCs; a half that
    still fails is halved again. It ends at one NPC, or says that neither half fails alone (the failure needs NPCs
    from both). A build NPC2 could not write counts as no answer and is logged as such."""
    res = {"arm": arm, "readyLimit": ready_limit, "launches": []}
    status = os.path.join(B.RUN_ROOT, f"npcbisect-{arm}.json")
    save = lambda: json.dump(res, open(status, "w"), indent=1)
    if build(arm) is None:
        res["error"] = "build unusable"; save(); return res
    base_excl = json.load(open(os.path.join(build_dir(arm), "build.json")))["excluded"]
    everyone = list(B.ARMS[arm]["subjects"])
    names = [n for n in everyone if n not in base_excl]
    res["subjects"], res["excludedByBuild"] = names, base_excl
    keepdir = os.path.join(build_dir(arm), "npcbisect")
    os.makedirs(keepdir, exist_ok=True)
    moddir = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - {arm}")

    def fails(keep, tag):
        """True: built and the game did not become ready. False: became ready. None: NPC2 wrote no usable plugin."""
        if len(res["launches"]) >= max_launches:
            raise RuntimeError("launch limit reached")
        row = {"tag": tag, "kept": list(keep)}
        make_profile(arm, with_output=False)
        m, fail = build_once(arm, sorted(set(everyone) - set(keep)))
        if m is None or fail or not m["plugins"]:
            row.update(built=False, why=(fail or {}).get("diagnosis", "no manifest or no plugin")[:300], notReady=None)
        else:
            info = make_profile(arm, with_output=True)
            var = os.path.join(keepdir, f"{len(res['launches']):02d}-{tag}-{m['plugins'][-1]}")
            shutil.copy2(os.path.join(moddir, m["plugins"][-1]), var)
            out, _ = run_game(arm, tag="npc", extra={"probe": True, "ready_limit": ready_limit, "load_limit": 150})
            ready = log_rows(out, "ready")
            row.update(built=True, variant=var, bytes=os.path.getsize(var), out=out, notReady=not ready,
                       readySeconds=ready[0].get("seconds") if ready else None)
        res["launches"].append(row); save()
        say(f"npcbisect {arm}: only {tag} ({len(keep)} NPCs) -> " + ("no build" if row["notReady"] is None else "NOT ready" if row["notReady"] else "ready"))
        return row["notReady"]
    try:
        items = names
        if first:
            first = [n for n in first if n in names]
            rest = [n for n in names if n not in first]
            alone, without = fails(first, "+".join(first)), fails(rest, "all-but-" + "+".join(first))
            res["first"] = {"names": first, "aloneFails": alone, "restFails": without}
            if alone and without is False:
                items = first
            elif without:
                items = rest                      # the rest fails on its own too (whatever `first` does): narrow it
                if alone:
                    res["note"] = f"{first} alone fails AND the selection without them fails: more than one cause"
            elif alone is False and without is False:
                items = None
                res["verdict"] = f"neither {first} alone nor the selection without them fails: the failure needs both"
        while items and len(items) > 1:
            a, b = items[:len(items) // 2], items[len(items) // 2:]
            if fails(a, f"{len(a)}-from-{a[0]}"):
                items = a
            elif fails(b, f"{len(b)}-from-{b[0]}"):
                items = b
            else:
                res["ambiguous"] = items
                res["verdict"] = f"these {len(items)} NPCs together fail, neither half alone does: the failure needs NPCs from both halves"
                items = None
        if items:
            res["minimal"] = items
            res["verdict"] = f"selecting only {items} still stops the game from loading"
    except Exception as e:
        res["error"] = repr(e)[:400]
    finally:
        shutil.rmtree(moddir, ignore_errors=True)        # a variant must not become the arm's kept output
        try:
            if build(arm) is not None:                   # put build\<arm> back to the full selection, as NPC2 built it
                drop_output(arm)
        except Exception as e:
            res["restoreError"] = repr(e)[:300]
    res["finished"] = True
    save()
    say(f"npcbisect {arm}: {res.get('verdict') or res.get('error')}")
    return res


def variants(arm, keep, specs, ready_limit=100):
    """After npcbisect: which OVERRIDE records of a small failing selection stop the game from loading?
    Builds `arm` for only the NPCs in `keep`, then launches that plugin as built and once per spec with records
    removed. A spec is "tag=G:LABEL[+LABEL]" (whole top-level groups) or "tag=R:FORMID[+FORMID]" (formids as stored
    in the file, hex). Remove only overrides of other plugins' records: that leaves nothing dangling, so both
    answers are sound. A variant that becomes ready says the cause is among what it lacks.
    "tag=V:FORMID:NN" changes nothing but that record's form version (the u16 at byte 20 of its header) to NN.
    A spec "@name" names the status file (variants-<arm>-<kept>-<name>.json) so a second run keeps the first."""
    name = "".join("-" + x[1:] for x in specs if x.startswith("@"))
    specs = [x for x in specs if not x.startswith("@")]
    res = {"arm": arm, "kept": keep, "readyLimit": ready_limit, "launches": []}
    status = os.path.join(B.RUN_ROOT, f"variants-{arm}-{'+'.join(keep)}{name}.json")
    save = lambda: json.dump(res, open(status, "w"), indent=1)
    moddir = os.path.join(B.MODS_DIR, f"{B.OUTPUT_PREFIX} - {arm}")
    keepdir = os.path.join(build_dir(arm), "variants")
    os.makedirs(keepdir, exist_ok=True)
    try:
        make_profile(arm, with_output=False)
        m, fail = build_once(arm, sorted(set(B.ARMS[arm]["subjects"]) - set(keep)))
        assert m is not None and not fail and m["plugins"], "NPC2 wrote no usable plugin for this selection"
        make_profile(arm, with_output=True)
        plugin = os.path.join(moddir, m["plugins"][-1])
        orig = os.path.join(keepdir, "as-built-" + "+".join(keep) + "-" + m["plugins"][-1])
        shutil.copy2(plugin, orig)
        for spec in ["as-built="] + list(specs):
            tag, what = spec.split("=", 1)
            groups = set(what[2:].split("+")) if what.startswith("G:") else set()
            records = {int(x, 16) for x in what[2:].split("+")} if what.startswith("R:") else set()
            var = os.path.join(keepdir, f"{tag}-{m['plugins'][-1]}")
            drop_items(orig, var, groups, records)
            if what.startswith("V:"):
                import struct
                fidv, ver = int(what.split(":")[1], 16), int(what.split(":")[2])
                d = bytearray(open(var, "rb").read())
                o, hits = 24 + struct.unpack_from("<I", d, 4)[0], []
                while o < len(d):
                    gsize, q = struct.unpack_from("<I", d, o + 4)[0], o + 24
                    while q < o + gsize:
                        sz = struct.unpack_from("<I", d, q + 4)[0]
                        if bytes(d[q:q + 4]) != b"GRUP" and struct.unpack_from("<I", d, q + 12)[0] == fidv:
                            hits.append(struct.unpack_from("<H", d, q + 20)[0])
                            struct.pack_into("<H", d, q + 20, ver)
                        q += sz if bytes(d[q:q + 4]) == b"GRUP" else 24 + sz
                    o += gsize
                assert len(hits) == 1, f"record {fidv:08X} found {len(hits)} times"
                open(var, "wb").write(d)
                what += f" (was {hits[0]})"
            shutil.copy2(var, plugin)
            out, _ = run_game(arm, tag="var", extra={"probe": True, "ready_limit": ready_limit, "load_limit": 150})
            ready = log_rows(out, "ready")
            res["launches"].append({"tag": tag, "removed": what, "variant": var, "bytes": os.path.getsize(var), "out": out,
                                    "ready": bool(ready), "readySeconds": ready[0].get("seconds") if ready else None})
            save()
            say(f"variants {arm}: {tag} -> {'READY' if ready else 'not ready'}")
    except Exception as e:
        res["error"] = repr(e)[:400]
    finally:
        shutil.rmtree(moddir, ignore_errors=True)
        try:
            if build(arm) is not None:
                drop_output(arm)
        except Exception as e:
            res["restoreError"] = repr(e)[:300]
    res["finished"] = True
    save()
    return res


PAUSE_FILE = os.path.join(B.RUN_ROOT, "PAUSE-NPC2BENCH")


if __name__ == "__main__":
    a = sys.argv[1:]
    # A pause for this bench only (the shared PAUSE-MACHINE would stop every session). Checked when a command starts,
    # so a hold that reaches its turn while paused releases the machine at once and launches nothing.
    if os.path.exists(PAUSE_FILE) and a[:1] in (["tier"], ["go"], ["launch"], ["recapture"], ["refsbatch"], ["smpbatch"], ["h1"], ["striptest"], ["loadbisect"], ["npcbisect"], ["variants"]):
        say(f"PAUSED ({PAUSE_FILE} exists): {' '.join(a)} not run")
        sys.exit(75)
    def opt(name):
        return a[a.index(name) + 1] if name in a else None
    only = opt("--only").split(",") if opt("--only") else None
    if a[:1] == ["install"]:
        install()
    elif a[:1] == ["check"]:
        sys.exit(0 if check_ref_groups() else 1)
    elif a[:1] == ["profile"]:
        print(json.dumps(make_profile(a[1]), indent=1))
    elif a[:1] == ["build"]:
        sys.exit(0 if build(a[1]) else 1)
    elif a[:1] == ["launch"]:
        sys.exit(0 if run_game(a[1], only, opt("--tag"))[1] else 1)
    elif a[:1] == ["go"]:
        ok = go(a[1], only, opt("--tag"))[1]
        drop_output(a[1])
        sys.exit(0 if ok else 1)
    elif a[:1] == ["tier"]:
        tier(a[1], opt("--from"))
    elif a[:1] == ["batches"]:
        # python bench_run.py batches A1,A2 A3,A4 ...   each group is one hold of the machine slot, in order
        bs = os.path.join(B.RUN_ROOT, "batches-status.json")
        done = []
        json.dump({"finished": False, "groups": a[1:], "done": done}, open(bs, "w"), indent=1)
        for group in a[1:]:
            rc = hold(f"NPC2 bench: launches {group}", ["recapture"] if group == "recapture" else ["tier", "list:" + group])
            say(f"hold for {group} ended rc={rc}")
            done.append({"group": group, "rc": rc})
            json.dump({"finished": False, "groups": a[1:], "done": done}, open(bs, "w"), indent=1)
        json.dump({"finished": True, "groups": a[1:], "done": done}, open(bs, "w"), indent=1)
    elif a[:1] == ["striptest"]:
        striptest(a[1], a[2], a[3])           # striptest A4 00013746 ATKR
    elif a[:1] == ["variants"]:
        variants(a[1], a[2].split(","), a[3:])        # variants A4 Ulfric noCLFM=G:CLFM noWolf=R:000CDD87
    elif a[:1] == ["npcbisect"]:
        npcbisect(a[1], a[2].split(",") if len(a) > 2 else None)      # npcbisect A4 Vilja
    elif a[:1] == ["loadbisect"]:
        loadbisect(a[1])
    elif a[:1] == ["seq"]:
        # python bench_run.py seq "go R1 --only Auri" "go A1 --only Auri" ...   run in turn (inside one hold when
        # called through `hold`); each is its own process, so each meets the pause gate and loads the current code
        rcs = []
        for line in a[1:]:
            rc = subprocess.run([PY, os.path.abspath(__file__)] + line.split()).returncode
            rcs.append(rc)
            say(f"seq: {line} -> rc={rc}")
        sys.exit(0 if not any(rcs) else 1)
    elif a[:1] == ["h1"]:
        h1()
    elif a[:1] == ["refsbatch"]:
        refs_batch()
    elif a[:1] == ["smpbatch"]:
        smp_batch()
    elif a[:1] == ["recapture"]:
        recapture()
    elif a[:1] == ["d1check"]:
        sys.exit(0 if d1check(a[1].split(",") if len(a) > 1 else None) else 1)      # offline; d1check [A4,A8]
    elif a[:1] == ["waitfor"]:
        sys.exit(waitfor(a[1], int(a[2]) if len(a) > 2 else 540))
    elif a[:1] == ["hold"]:
        sys.exit(hold(a[1], a[2:]))
    else:
        sys.exit(__doc__)
