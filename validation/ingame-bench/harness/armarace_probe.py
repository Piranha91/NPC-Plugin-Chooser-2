"""ArmaRace fixture probe (2026-11-24): COPIED from harness/npc2recast/driver.py (completed against the real game
2026-10-29) with only these changes: subjects from armarace_cases.json (vanilla NPCs), no save/load passes, no body
view (every portrait is framed from about the collarbone up: FACE_D/FACE_Z), worn head-slot telemetry, a
required-plugin check, and a recording failure is logged instead of aborting (stills are the evidence here).
Original docstring follows.

NPC2 Recast output in game (Recast plan Phase 8, 2026-10-29). PLAN: _claude/runs/2026-10-29-npc2-recast-ingame/PLAN.md.
Inherits harness/faceswap/driver.py (completed against the real game 2026-10-28): same launch (wmi_launch), harness,
recording, teardown and restore proof, studio portraits. New here: the arms and subjects of npc2recast/cases.json, the
donors spawned beside their targets as same-launch references, and the worn-body guard (§0 of the plan).

usage: python driver.py run --arm Hybrid|Only|Measure [--load SAVE] [--smoke] [--watch] [--out DIR]
Always runs the arm's whole sequence:
  new game: spawn -> pass new -> save -> load (same process) -> pass reload -> end
  --load:   load SAVE from the main menu (fresh process) -> pass fresh -> end
  --smoke:  the new-game sequence on cases.json spawn.smoke (and their donors) only
Every step is a line in <out>/log.jsonl; portraits land in <out>/shots; SKSE logs are copied to <out>/logs at teardown.

Worn-body guard (Piranha, 2026-10-29): no picture is taken of a subject whose body slot (GetWornForm 0x4) is empty;
its portraits are logged UNASSESSED and only telemetry is kept. Outfits are never removed."""
import sys, os, json, time, shutil, glob, math, traceback, hashlib

HERE = os.path.dirname(os.path.abspath(__file__))
ARGS = sys.argv[1:]
def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default
ARM = arg("--arm", "ArmaRace")
LOADSAVE = arg("--load")
SMOKE = "--smoke" in ARGS
WATCH = True if "--watch" in ARGS else None   # None: DEVBENCH_WATCH decides; default minimized (wmi_launch)
import bench_paths as P
RUN = P.ARMARACE_RUN
OUT = arg("--out") or os.path.join(RUN, "live",
                                   f"{ARM}{'-load' if LOADSAVE else ''}{'-smoke' if SMOKE else ''}-{time.strftime('%Y%m%d-%H%M%S')}")
os.makedirs(OUT, exist_ok=True)
os.environ.setdefault("HOUSECARL_RUNLOG_DIR", os.path.join(OUT, "runlog"))
SCRIPTS = P.DEVBENCH_SCRIPTS
sys.path.insert(0, SCRIPTS)
sys.path.insert(0, P.LAUNCH_DRIVER_DIR)
import dbharness as db
import wmi_launch as WL

C = json.load(open(os.path.join(HERE, "armarace_cases.json"), encoding="utf-8"))
SPEC = C["arms"][ARM]
LAB = P.INSTANCE
PROFILE = SPEC["profile"]
LNK = os.path.join(RUN, "SKSE-NPCSelection.lnk")
CELL = C["cells"]["A"]
SNAP = os.path.join(OUT, "profile-before")
SHOTS = os.path.join(OUT, "shots")
LISTS = ("plugins.txt", "loadorder.txt", "modlist.txt")
SKSE_LOGS = os.path.join(os.path.expanduser("~"), "Documents", "My Games", "Skyrim Special Edition", "SKSE")
BODY = (2400, 2016, 6976)      # QASmoke's small east room: out of the studio view (2026-10-25 station AD)
NOTICE_WAIT = 10.0             # the previous native capture's "ScreenShot created" notice must clear (P12)
FACE_D, FACE_Z = 40.0, 126.0   # measured 2026-11-24: at D=68 the frame is ~75 u tall (mid-chest up), not the 49 u computed from FOV 65; D=40 gives ~44 u, head to collar
HDPT_TYPES = {0: "Misc", 1: "Face", 2: "Eyes", 3: "Hair", 4: "FacialHair", 5: "Scar", 6: "Brows"}
LOG = open(os.path.join(OUT, "log.jsonl"), "a", encoding="utf-8", buffering=1)
BASE = {}      # subject name -> runtime base FormID
REF = {}       # subject name -> runtime ref FormID
STATE = {"freecam": False}


def subjects():
    """The four vanilla subjects of the 2x2 (armarace_cases.json)."""
    return {n: (int(s["form"], 16), s["plugin"]) for n, s in C["subjects"].items()}


def log(**row):
    row["t"] = round(time.time(), 3)
    LOG.write(json.dumps(row, default=str) + "\n")
    print(json.dumps(row, default=str)[:300], flush=True)


def sha(p):
    return hashlib.sha256(open(p, "rb").read()).hexdigest() if os.path.exists(p) else None


def snapshot():
    os.makedirs(SNAP, exist_ok=True)
    for f in LISTS:
        shutil.copy2(os.path.join(LAB, "profiles", PROFILE, f), os.path.join(SNAP, f))
    shutil.copy2(os.path.join(LAB, "ModOrganizer.ini"), os.path.join(SNAP, "ModOrganizer.ini"))


def proof():
    r = {"running": db._exe_running(*db.MO2_EXES), "selected": db.selected_profile(LAB)}
    r["mo2_ini_identical"] = open(os.path.join(LAB, "ModOrganizer.ini"), "rb").read() == open(os.path.join(SNAP, "ModOrganizer.ini"), "rb").read()
    restored = []
    for f in LISTS:
        cur, ref = os.path.join(LAB, "profiles", PROFILE, f), os.path.join(SNAP, f)
        if open(cur, "rb").read() != open(ref, "rb").read() and not r["running"]:
            shutil.copy2(cur, os.path.join(OUT, f"{PROFILE}.{f}.as-left.{int(time.time())}"))
            shutil.copy2(ref, cur)
            restored.append(f)
    r["lists_restored"] = restored
    log(step="proof", **r)
    return r


def close_owned_mo2(pid):
    import subprocess
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue; if ($p) {{ $p.CloseMainWindow() | Out-Null }}"],
                   capture_output=True)
    t0 = time.time()
    while time.time() - t0 < 60 and pid in db._pids_of("ModOrganizer.exe"):
        time.sleep(3)
    return pid not in db._pids_of("ModOrganizer.exe")


def versions():
    mods = [l[1:].strip() for l in open(os.path.join(LAB, "profiles", PROFILE, "modlist.txt"), encoding="utf-8") if l.startswith("+")]
    out = {"profile": PROFILE, "mods": mods, "files": {}}
    for m in mods:
        if m.startswith(("NPC2Bench", "DevBench")):
            for p in glob.glob(os.path.join(LAB, "mods", m, "**", "*"), recursive=True):
                if os.path.isfile(p) and p.lower().endswith((".dll", ".toml", ".ini", ".esp")):
                    out["files"][os.path.relpath(p, os.path.join(LAB, "mods"))] = sha(p)
    return out


# ------------------------------------------------------------------ readings
def fid(v):
    return db._formid_of(v) if not db.failed(v) else None


def base_reads(b):
    if not b:
        return {"error": "no base"}
    r = {"formId": b}
    for fn in ("GetRace", "GetVoiceType", "GetSkin", "GetHairColor", "GetFaceTextureSet", "GetTemplate"):
        v = db.ret("ActorBase", fn, b)
        r[fn] = v if db.failed(v) else fid(v)
    r["GetOutfit"] = fid(db.ret("ActorBase", "GetOutfit", b, [False]))
    for fn in ("GetSex", "GetHeight", "GetWeight", "GetNumHeadParts"):
        r[fn] = db.ret("ActorBase", fn, b)
    parts = []
    n = r["GetNumHeadParts"] if isinstance(r["GetNumHeadParts"], int) else 0
    for i in range(n):
        hp = fid(db.ret("ActorBase", "GetNthHeadPart", b, [i]))
        parts.append({"i": i, "formId": hp, "name": db.ret("HeadPart", "GetPartName", hp) if hp else None,
                      "type": HDPT_TYPES.get(db.ret("HeadPart", "GetType", hp), None) if hp else None})
    r["headParts"] = parts
    return r


def tele_one(ref):
    return {"ref": ref, "loaded": db.is_3d_loaded(ref), "pos": db.pos(ref),
            "actorRace": fid(db.ret("Actor", "GetRace", ref)),
            "angleZ": db.ret("ObjectReference", "GetAngleZ", ref),
            "wornBody": fid(db.ret("Actor", "GetWornForm", ref, [0x4])),
            "wornHair": fid(db.ret("Actor", "GetWornForm", ref, [0x2])),
            "wornCirclet": fid(db.ret("Actor", "GetWornForm", ref, [0x1000])),
            "base": base_reads(fid(db.ret("Actor", "GetActorBase", ref))),
            "leveledBase": base_reads(fid(db.ret("Actor", "GetLeveledActorBase", ref)))}


def tele(tag):
    rows = {n: tele_one(r) for n, r in REF.items()}
    log(step="tele", tag=tag, refs=rows)
    return rows


# ------------------------------------------------------------------ camera and capture
def drive(x, y, z, yaw_deg, pitch_deg=0.0):
    if not STATE["freecam"]:
        r = db.post("camera", {"action": "freecam", "on": True})
        log(step="freecam", res=r)
        if isinstance(r, dict) and r.get("_httpError") == 409:
            log(step="freecam_foreign", tfc=db.post("console", {"action": "exec", "command": "tfc"}))
            time.sleep(1.5)
            r = db.post("camera", {"action": "freecam", "on": True})
            log(step="freecam", res=r, retry=True)
            if isinstance(r, dict) and r.get("_httpError"):
                raise RuntimeError(f"free camera refused after tfc: {r}")
        STATE["freecam"] = True
        time.sleep(1.0)
    r = db.post("camera", {"action": "drive", "x": x, "y": y, "z": z, "yaw": math.radians(yaw_deg), "pitch": math.radians(pitch_deg)})
    time.sleep(1.2)
    return {"drive": r, "get": db.post("camera", {"action": "get"})}


def shot(cid):
    time.sleep(NOTICE_WAIT)
    r = db.shot(cid)
    path = os.path.join(SHOTS, "n2r", "default", r["file"]) if isinstance(r, dict) and r.get("file") else None
    blank = None
    if path and os.path.exists(path):
        try:
            from PIL import Image, ImageStat
            blank = ImageStat.Stat(Image.open(path).convert("L").resize((96, 54))).stddev[0] < 4.0
        except Exception as e:
            blank = repr(e)
    log(step="shot", id=cid, res=r, path=path, blank=blank)
    return path


def place(ref, x, y, z):
    db.ret("ObjectReference", "SetPosition", ref, [float(x), float(y), float(z)], timeout_ms=20000)
    db.ret("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0], timeout_ms=20000)


def park_xy(i, n):
    x0, x1 = CELL["park_x"]
    return (x0 + (x1 - x0) * i / max(n - 1, 1), CELL["park_y"])


PLACE_TOL = 60.0   # studio placement must land within this many units (x/y) before any picture
# Presence: the face frame's centre must differ from the empty-studio frame by at least this mean grey level (0-255,
# 192x108 downscale, crop x 67-125 / y 22-97). Measured 2026-10-29 on existing frames: empty vs empty 0.49 and 0.81,
# a framed NPC (faceswap R run) vs empty 14.45. A readback said "in the studio, 3D loaded" for subjects that were
# not drawn (smoke 2): position alone does not establish that the subject is in the picture.
PRESENCE_MIN = 5.0


def at_studio(ref):
    sx, sy, _ = CELL["studio"]
    p = db.pos(ref)
    return p, bool(p) and abs(p[0] - sx) < PLACE_TOL and abs(p[1] - sy) < PLACE_TOL and db.is_3d_loaded(ref)


def presence(path):
    if not path or not STATE.get("empty") or not os.path.exists(path):
        return None
    from PIL import Image, ImageChops, ImageStat
    def crop(p):
        return Image.open(p).convert("L").resize((192, 108)).crop((67, 22, 125, 97))
    return round(ImageStat.Stat(ImageChops.difference(crop(path), crop(STATE["empty"]))).mean[0], 2)


def reattach(ref):
    """Disable/Enable so the actor's 3D is rebuilt where it now stands. QASmoke's rooms cull an actor whose 3D still
    belongs to the room PlaceAtMe put it in (smoke 2: position and 3D-loaded both read back fine, nothing drawn)."""
    db.ret("ObjectReference", "Disable", ref, [False], timeout_ms=20000)
    time.sleep(1.0)
    db.ret("ObjectReference", "Enable", ref, [False], timeout_ms=20000)
    time.sleep(3.0)


def empty_studio(tag):
    """The studio with nobody in it, from the face pose: the presence reference for this pass."""
    sx, sy, sz = CELL["studio"]
    cam = drive(sx, sy + FACE_D, sz + FACE_Z, 180.0)
    STATE["empty"] = shot(f"{tag}-empty-face")
    log(step="empty_studio", tag=tag, camera=cam, path=STATE["empty"])
    for pname, dyaw, dpitch in (("yaw5", 5.0, 0.0), ("yaw10", 10.0, 0.0), ("pitch5", 0.0, 5.0)):
        c = drive(sx, sy + FACE_D, sz + FACE_Z, 180.0 + dyaw, dpitch)
        log(step="fov_probe", name=pname, yawDeg=180.0 + dyaw, pitchDeg=dpitch, camera=c, path=shot(f"{tag}-fov-{pname}"))
    drive(sx, sy + FACE_D, sz + FACE_Z, 180.0)


def place_in_studio(ref):
    """SetPosition into the studio, verified. QASmoke is finicky about where actors end up (Piranha, 2026-10-29: PlaceAtMe
    there often lands NPCs in another room), so a miss is retried once by MoveTo the player body first, then refused."""
    sx, sy, sz = CELL["studio"]
    place(ref, sx, sy, sz); time.sleep(3.0)
    reattach(ref)
    p, ok = at_studio(ref)
    tries = [{"how": "SetPosition, Disable/Enable", "pos": p, "ok": ok}]
    if not ok:
        db.ret("ObjectReference", "MoveTo", ref, [{"form": db.PLAYER}, 0.0, 0.0, 0.0, False], timeout_ms=20000)
        time.sleep(2.0)
        place(ref, sx, sy, sz); time.sleep(3.0)
        reattach(ref)
        p, ok = at_studio(ref)
        tries.append({"how": "MoveTo player, SetPosition, Disable/Enable", "pos": p, "ok": ok})
    return p, ok, tries


FACING_TOL = 20.0     # degrees off the camera bearing before a re-aim is tried
VIEW_BEARING = {"face": 0.0, "face34": 45.0}   # camera bearing from the subject, clockwise from +Y (heading 0)


def heading(ref):
    """The reference's heading in degrees (ObjectReference.GetAngleZ: 0 = facing +Y, clockwise), or None."""
    v = db.ret("ObjectReference", "GetAngleZ", ref)
    return float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else None


def wrap180(a):
    return (a + 180.0) % 360.0 - 180.0


def aspect(view, hdg):
    """Which side of the subject the camera sees: degrees clockwise from its front, and a word for it."""
    if hdg is None:
        return {"deg": None, "side": "unknown"}
    a = wrap180(VIEW_BEARING[view] - hdg)
    side = "front" if abs(a) <= 25 else "three-quarter" if abs(a) <= 65 else "profile" if abs(a) <= 115 else \
        "rear three-quarter" if abs(a) <= 155 else "back"
    return {"deg": round(a, 1), "side": side}


HEAD, EYE, HMAG = "NPC Head [Head]", "NPCEyeBone", "NPC Head MagicNode [Hmag]"   # skeleton.nif, read 2026-11-24
FREEZE = C.get("freeze", {})     # subject -> "papyrus" (Actor.EnableAI(false)) | "console" ("<ref>".tai)


def nodepos(ref, node):
    p = [db.ret("NetImmerse", "GetNodeWorldPosition" + a, None, [{"form": ref}, node, False]) for a in "XYZ"]
    return p if all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in p) else None


def headread(name, ref, stage):
    """One row: body heading, head direction (world yaw of head->eye and head->magic-node), AI flag, and where the
    player stands relative to the subject's front (GetHeadingAngle: degrees, + = to its right)."""
    row = {"name": name, "stage": stage, "freeze": FREEZE.get(name), "heading": heading(ref), "pos": db.pos(ref),
           "aiEnabled": db.ret("Actor", "IsAIEnabled", ref), "playerPos": db.pos(db.PLAYER),
           "playerFromFront": db.ret("ObjectReference", "GetHeadingAngle", ref, [{"form": db.PLAYER}])}
    if row["pos"] and row["playerPos"]:
        row["playerDist"] = round(math.dist(row["pos"][:2], row["playerPos"][:2]), 1)
    h, e, m = nodepos(ref, HEAD), nodepos(ref, EYE), nodepos(ref, HMAG)
    row["nodes"] = {"head": h, "eye": e, "hmag": m}
    for key, n in (("eye", e), ("hmag", m)):
        if h and n and abs(n[0] - h[0]) + abs(n[1] - h[1]) > 0.05:
            yaw = math.degrees(math.atan2(n[0] - h[0], n[1] - h[1]))
            row["headYaw_" + key] = round(yaw, 1)
            if row["heading"] is not None:
                row["headVsBody_" + key] = round(wrap180(yaw - row["heading"]), 1)
    log(step="headread", **row)
    return row


def freeze(name, ref):
    how = FREEZE.get(name)
    if not how:
        return
    before = db.ret("Actor", "IsAIEnabled", ref)
    if how == "papyrus":
        res = db.ret("Actor", "EnableAI", ref, [False], timeout_ms=20000)
    else:
        res = db.post("console", {"action": "exec", "command": '"%s".tai' % ref[2:]})
    time.sleep(1.0)
    log(step="freeze", name=name, how=how, res=res, aiBefore=before, aiAfter=db.ret("Actor", "IsAIEnabled", ref))
    headread(name, ref, "frozen")


def tracktest():
    """Does a subject's head follow the player? One frozen and one unfrozen subject side by side at heading 0, the
    player moved to their right and then their left, under the global `tai` and then with it off."""
    release_cam()
    sx, sy, sz = CELL["studio"]
    pair = {"Orgnar": (sx + 60.0, sy), "Hod": (sx - 60.0, sy)}
    for n, (x, y) in pair.items():
        ref = REF[n]
        place(ref, x, y, sz); time.sleep(2.0)
        reattach(ref)
        db.ret("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0], timeout_ms=20000)
    time.sleep(2.0)
    for n in pair:
        headread(n, REF[n], "track-setup")
    spots = {"right": [(sx + 220.0, sy + 150.0), (CELL["park_x"][1], CELL["park_y"])],
             "left": [(sx - 220.0, sy + 150.0), (CELL["park_x"][0], CELL["park_y"])]}
    for ai_label, off in (("globalTaiOn", True), ("globalTaiOff", False)):
        ai_travel(off)
        for side, cands in spots.items():
            landed = None
            for (x, y) in cands:
                try:
                    landed = db.move_within_cell(x, y, sz)
                    break
                except Exception as e:
                    log(step="track_move_refused", side=side, x=x, y=y, error=repr(e)[:300])
            log(step="track_player", ai=ai_label, side=side, landed=landed)
            time.sleep(6.0)
            for n in pair:
                headread(n, REF[n], f"track-{ai_label}-{side}")


def shotat(name, tag):
    """Face, face 3/4 and clothed body of one subject in the studio — only when its body slot is worn and it verifiably
    stands in the studio."""
    ref = REF[name]
    worn = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    if not worn:
        log(step="portrait", tag=tag, name=name, paths={"UNASSESSED": "body slot empty: no picture taken (plan §0)"})
        return {"UNASSESSED": "body slot empty"}
    # The studio must be empty before this subject goes in (smoke 3: the first subject stayed drawn there and every
    # later one was placed inside it; a presence score cannot tell one subject from two).
    sx, sy, sz = CELL["studio"]
    drive(sx, sy + FACE_D, sz + FACE_Z, 180.0)
    vacant = shot(f"{tag}-{name}-vacant")
    occupied = presence(vacant)
    if occupied is not None and occupied >= PRESENCE_MIN:
        log(step="portrait", tag=tag, name=name, vacant=vacant, occupied=occupied,
            paths={"UNASSESSED": f"studio not empty before placement (presence {occupied})"})
        return {"UNASSESSED": "studio occupied"}
    before = db.pos(ref)
    p, ok, tries = place_in_studio(ref)
    if not ok:
        log(step="portrait", tag=tag, name=name, placement=tries, paths={"UNASSESSED": "not in the studio after placement"})
        return {"UNASSESSED": "placement"}
    sx, sy, _ = CELL["studio"]
    z = p[2]
    hdg0 = heading(ref)
    hdg1 = None
    if hdg0 is None or abs(wrap180(hdg0)) > FACING_TOL:      # re-aim once, then read back
        db.ret("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0], timeout_ms=20000)
        time.sleep(2.0)
        hdg1 = heading(ref)
    try:
        insp = db.post("inspect", {"kind": "refs", "formId": ref})
    except Exception as e:
        insp = repr(e)
    out = {"wornBody": worn, "heading": {"afterPlacement": hdg0, "afterReaim": hdg1}, "inspect": str(insp)[:600]}
    headread(name, ref, "studio")
    cams = {}
    for view, (x, y, zz, yaw) in {"face": (sx, sy + FACE_D, z + FACE_Z, 180.0),
                                  "face34": (sx + FACE_D / math.sqrt(2), sy + FACE_D / math.sqrt(2), z + FACE_Z, 225.0)}.items():
        cams[view] = drive(x, y, zz, yaw)
        hs = heading(ref)
        out[view + "-predicted"] = {"headingAtShot": hs, **aspect(view, hs)}     # written before the picture exists
        log(step="aspect", name=name, view=view, **out[view + "-predicted"])
        headread(name, ref, "atShot-" + view)
        _, still = at_studio(ref)
        out[view] = shot(f"{tag}-{name}-{view}") if still else "UNASSESSED: subject left the studio"
        if view == "face":
            seen = presence(out["face"])
            if seen is not None and seen < PRESENCE_MIN:     # one more re-attach, then refuse
                reattach(ref)
                cams["face-retry"] = drive(x, y, zz, yaw)
                out["face"] = shot(f"{tag}-{name}-face-retry")
                seen = presence(out["face"])
            out["presence"] = seen
            if seen is not None and seen < PRESENCE_MIN:
                out["UNASSESSED"] = f"subject not drawn in the studio (presence {seen} < {PRESENCE_MIN})"
                break
    if before:
        place(ref, *before)
    # Out of the studio for good this pass: a subject moved back by coordinates kept being drawn in the studio, and the
    # next one was placed inside it (Piranha, smoke 3). Disabled actors are not drawn; do_pass re-enables them all.
    db.ret("ObjectReference", "Disable", ref, [False], timeout_ms=20000)
    time.sleep(1.0)
    out["disabledAfter"] = db.ret("ObjectReference", "IsDisabled", ref)
    log(step="portrait", tag=tag, name=name, placement=tries, cameras=cams, paths=out)
    return out


# ------------------------------------------------------------------ ops
def resolve_bases():
    pairs = subjects()
    pairs["@FarmClothesOutfit02"] = (0x02D75E, "Skyrim.esm")   # faceswap cases.json "vanilla"
    BASE.update(db.runtime_formids(pairs))
    log(step="bases", bases=BASE)


def dress(name, ref):
    """Clothe a subject spawned with nothing on its body slot (the FaceSwapLab targets have no outfit, measured in the
    2026-10-29 smoke), right after it spawns. Its outfit telemetry is logged first, so a case that is ABOUT outfit
    delivery (C7) keeps its reading. Nothing is ever removed. Actor.SetOutfit(Outfit, Bool) is vanilla native."""
    worn = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    base = fid(db.ret("Actor", "GetActorBase", ref))
    row = {"name": name, "wornBodyBefore": worn, "outfitBefore": fid(db.ret("ActorBase", "GetOutfit", base, [False])) if base else None}
    if not worn:
        row["setOutfit"] = db.ret("Actor", "SetOutfit", ref, [{"form": BASE["@FarmClothesOutfit02"]}, False], timeout_ms=20000)
        time.sleep(2.0)
        row["wornBodyAfter"] = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    log(step="dress", **row)
    return row


def spawn():
    names = [n for n in BASE if not n.startswith("@")]
    for i, n in enumerate(names):
        x, y = park_xy(i, len(names)); z = CELL["studio"][2]
        try:
            REF[n] = db.spawn(BASE[n], x, y, z)
            headread(n, REF[n], "spawned")
            freeze(n, REF[n])
            dress(n, REF[n])
            db.ret("ObjectReference", "SetAngle", REF[n], [0.0, 0.0, 0.0])
        except Exception as e:
            log(step="spawn_refused", name=n, error=repr(e))
    for n, r in REF.items():
        headread(n, r, "parked")
    log(step="spawn", refs=REF)
    return REF


def ai_travel(off):
    """Capture policy (2026-10-28): `tai` with nothing selected stops NPC travel before portraits. A toggle: tracked,
    restored before a save."""
    if STATE.get("ai_off", False) == off:
        return
    log(step="tai", off=off, res=db.post("console", {"action": "exec", "command": "tai"}))
    STATE["ai_off"] = off
    time.sleep(1.0)


def hud(off):
    """Console `tm` (toggle menus) hides the crosshair and compass that native captures burn in (Piranha, 2026-10-29).
    A toggle: tracked, and restored before a save or teardown."""
    if STATE.get("hud_off", False) == off:
        return
    log(step="tm", off=off, res=db.post("console", {"action": "exec", "command": "tm"}))
    STATE["hud_off"] = off
    time.sleep(1.0)


def do_pass(tag):
    db.video_mark(f"n2r: pass {tag}")
    t = tele(tag)
    ai_travel(True)
    hud(True)
    try:
        empty_studio(tag)
        pics = {n: shotat(n, tag) for n in REF}
    finally:
        hud(False)
        for n, ref in REF.items():          # back on for the save and the next pass
            db.ret("ObjectReference", "Enable", ref, [False], timeout_ms=20000)
        time.sleep(3.0)
        log(step="reenabled", disabled={n: db.ret("ObjectReference", "IsDisabled", r) for n, r in REF.items()})
    return {"tele": t, "pics": pics}


def release_cam():
    if STATE["freecam"]:
        log(step="freecam_off", res=db.post("camera", {"action": "freecam", "on": False}))
        STATE["freecam"] = False
        time.sleep(1.0)


def save(name):
    release_cam()
    ai_travel(False)
    r = db.post("game", {"action": "save", "name": name})
    time.sleep(10.0)
    found = glob.glob(os.path.join(LAB, "profiles", PROFILE, "saves", f"{name}*"))
    log(step="save", name=name, res=r, files=found)
    if not found:
        raise RuntimeError(f"save {name} produced no file")
    return found


def load(name):
    release_cam()
    r = db.post("game", {"action": "load", "name": name})
    time.sleep(3.0)
    confirm_load_prompt(limit=20)
    time.sleep(5.0)
    c = db.wait_loaded()
    time.sleep(8.0)
    db.clear_modals(6)
    STATE["freecam"] = False
    STATE["ai_off"] = False     # the save was made with AI on
    log(step="load", name=name, res=r, loaded=c)
    return c


def confirm_load_prompt(limit=90):
    """A load can stop at Skyrim's "Creations are currently loaded ... continue loading this save?" box, which
    wait_loaded never answers (Hybrid fresh load, 2026-10-29: it sat at the main menu until Piranha noticed). Answer
    that one box with the button labelled Yes, by label; any other box is left for clear_modals and logged."""
    t0 = time.time()
    while time.time() - t0 < limit:
        m = db.post("menu", {"action": "list"})
        if isinstance(m, dict) and m.get("messageBoxOpen"):
            box = db.post("menu", {"action": "describe"})
            body = (box or {}).get("bodyText", "") if isinstance(box, dict) else ""
            buttons = (box or {}).get("buttons", []) if isinstance(box, dict) else []
            if "loading this save" in body and "Yes" in buttons:
                r = db.post("menu", {"action": "accept", "index": buttons.index("Yes")})
                log(step="load_prompt", body=body, buttons=buttons, res=r)
                return True
            log(step="load_prompt_other", body=body, buttons=buttons)
            return False
        if isinstance(m, dict) and "Main Menu" not in (m.get("openMenus") or []):
            return None          # past the menu: no prompt this time
        time.sleep(2.0)
    log(step="load_prompt_timeout", seconds=limit)
    return None


def refs_from_save():
    """After a save load, the spawned refs keep the runtime FormIDs the new game logged (persist=True)."""
    prev = sorted(glob.glob(os.path.join(os.path.dirname(OUT), f"{ARM}-2*", "log.jsonl")))
    for p in reversed(prev):
        for line in open(p, encoding="utf-8"):
            row = json.loads(line)
            if row.get("step") == "spawn":
                REF.update(row["refs"])
        if REF:
            log(step="refs_from_save", source=p, refs=REF)
            return
    raise RuntimeError("no earlier new-game launch of this arm logged its spawned refs")


def sequence():
    return [("spawn", None), ("pass", "new"), ("tracktest", None)]


def run_sequence():
    for op, a in sequence():
        if op == "spawn": spawn()
        elif op == "pass": do_pass(a)
        elif op == "save": save(a)
        elif op == "load": load(a)
        elif op == "tracktest": tracktest()
        log(step="auto", op=op, arg=a, ok=True)


def copy_logs():
    dst = os.path.join(OUT, "logs"); os.makedirs(dst, exist_ok=True)
    for pat in ("Recast.log", "SkyPatcher*.log", "skse64.log", "DevBench*.log", "po3_Tweaks.log", "crash-*.log"):
        for p in glob.glob(os.path.join(SKSE_LOGS, pat)):
            if os.path.getmtime(p) < STATE.get("t_launch", 0):
                continue
            shutil.copy2(p, dst)
    log(step="logs", files=sorted(os.listdir(dst)))


# ------------------------------------------------------------------ lifecycle
def run():
    busy = db._exe_running(*db.MO2_EXES)
    log(step="prelaunch", arm=ARM, load=LOADSAVE, smoke=SMOKE, busy=busy, out=OUT)
    assert not busy, f"refusing: {busy} running"
    log(step="versions", **versions())
    snapshot()
    log(step="select", res=db.select_profile(LAB, PROFILE))
    pre = db.popup_preflight(LAB, PROFILE)
    log(step="popup_preflight", ok=pre["ok"], missing=pre["missing"], warnings=pre["warnings"])
    assert pre["ok"], "profile not quiet"
    db.configure(port=8920, shot_dir=SHOTS, recording="n2r")
    STATE["t_launch"] = time.time()
    mo2_pid, video = None, False
    try:
        WL.launch_outside_job(LNK, watch=WATCH)
        mo2_pid = int(WL.RECORDS[-1]["created"]["pid"])
        log(step="launch", record=WL.RECORDS[-1])
        h = db.wait_ready(limit=420)
        log(step="ready", seconds=round(time.time() - STATE["t_launch"], 1), health=h)
        db.health_check(expect_exe="SkyrimSE")
        assert db.loaded() is None, "main menu expected"
        try:
            v = db.video_start(OUT, label=f"n2r-{ARM}", hook_limit=6, allow_display=False)
            video = True
            log(step="video_start", mode=v.get("mode"), v=v)
        except Exception as e:      # diagnostic recording only: the native stills are this probe's evidence
            log(step="video_start_failed", error=repr(e))
            db.video_mark = lambda label: log(step="video_mark_skipped", label=label)   # marks need the recorder
        if LOADSAVE:
            refs_from_save()
            db.video_mark(f"n2r: load {LOADSAVE} from the main menu")
            r = db.post("game", {"action": "load", "name": LOADSAVE})
            time.sleep(3.0)
            confirm_load_prompt()
            time.sleep(5.0)
            log(step="menu_load", res=r, loaded=db.wait_loaded())
            time.sleep(8.0)
            db.clear_modals(6)
        else:
            db.video_mark(f"n2r: coc {CELL['cell']} (new game)")
            log(step="new_game", cell=db.new_game_at_cell(CELL["cell"]))
        ok, hours = db.unpause()
        log(step="unpause", ok=ok, hours=hours)
        for key in ("fDefaultWorldFOV:Display", "fDefault1stPersonFOV:Display"):
            try:
                log(step="getini", key=key, res=db.post("console", {"action": "exec", "command": f'getini "{key}"', "capture": True}))
            except Exception as e:
                log(step="getini_error", key=key, error=repr(e))
        log(step="protect", res=db.protect_body(log=None, note="npc2recast: the body is not the subject"))
        log(step="body", res=db.move_within_cell(*BODY))
        lp = db.loaded_plugins()
        log(step="loaded_plugins", plugins=lp)
        missing = [p for p in C["required_plugins"] if p.lower() not in [x.lower() for x in lp]]
        assert not missing, f"fixture plugins not loaded: {missing}"
        if not LOADSAVE:
            resolve_bases()
        run_sequence()
        log(step="sequence_done")
    except Exception as e:
        log(step="abort", error=repr(e), tb=traceback.format_exc())
    finally:
        if video:
            try:
                db.video_mark("n2r: end")
                log(step="video_stop", res=db.video_stop())
            except Exception as e:
                log(step="video_stop_error", error=repr(e))
        try:
            log(step="teardown", res=db.teardown())
        except Exception as e:
            log(step="teardown_error", error=repr(e))
        if mo2_pid:
            log(step="mo2_closed", ok=close_owned_mo2(mo2_pid), pid=mo2_pid)
        try:
            log(step="unselect", res=db.select_profile(LAB, restore=True))
        except Exception as e:
            log(step="unselect_error", error=repr(e))
        proof()
        copy_logs()
        try:
            r = db.obs_close()
            json.dump(r, open(os.path.join(OUT, "obs_close.json"), "w"), indent=1, default=str)
            log(step="obs_close", res=r)
        except Exception as e:
            log(step="obs_close_error", error=repr(e))
        log(step="done")


if __name__ == "__main__":
    if ARGS[:1] == ["run"]:
        assert ARM in C["arms"], f"--arm must be one of {list(C['arms'])}"
        run()
