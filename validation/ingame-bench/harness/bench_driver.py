"""NPC2 in-game pre-release check: the game driver. One launch, described by a launch.json written by bench_run.py.

usage: python bench_driver.py <launch.json>

Derived from harness/npc2bench/armarace_probe.py (run against the real game 2026-11-24), which was itself copied from
harness/npc2recast/driver.py (2026-10-29). What changed for the bench:
  * subjects are handled ONE AT A TIME: spawn, freeze that actor's AI, read telemetry, portraits, (outfit swap,
    telemetry, portraits,) disable, delete, and a frame of the vacated studio from the same pose;
  * the camera is placed from the subject's HEAD BONES, not its heading: it stands 40 units in front of wherever the
    head actually faces, at head-bone height, so a frozen subject that comes back turned is still photographed from
    the front (2026-11-24: heading read 0 while the subject stood with its back to a heading-placed camera);
  * telemetry names things: head parts, skin armatures and worn pieces by editor ID / model path, because records
    merged into NPC2's output get new FormIDs;
  * an optional same-process save and reload pass on a few subjects.

Rules that are not negotiable (Piranha, 2026-10-29 and 2026-11-24):
  * every picture is framed from about the collarbone up BY CAMERA PLACEMENT (never a crop of a wider shot);
  * no outfit is ever removed, the swap goes from one full outfit to another, and a subject whose body slot is empty
    is logged UNASSESSED and is not photographed;
  * no child NPC is a subject (bench_def.S holds adults only).

Every step is a line in <out>/log.jsonl; one `subject` row per subject and state carries its telemetry and frames."""
import sys, os, json, time, shutil, glob, math, traceback, hashlib

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = json.load(open(sys.argv[1], encoding="utf-8"))
OUT = SPEC["out"]
os.makedirs(OUT, exist_ok=True)
os.environ.setdefault("HOUSECARL_RUNLOG_DIR", os.path.join(OUT, "runlog"))
import bench_paths as P
SCRIPTS = P.DEVBENCH_SCRIPTS
sys.path.insert(0, SCRIPTS)
sys.path.insert(0, P.LAUNCH_DRIVER_DIR)
import dbharness as db
import wmi_launch as WL

LAB = P.INSTANCE
PROFILE = SPEC["profile"]
LNK = P.SKSE_LNK
CELL = {"cell": "QASmoke", "studio": (-2400.0, 1600.0, 6976.0), "park": (-2350.0, 1900.0)}
BODY = (2400, 2016, 6976)      # QASmoke's small east room: out of the studio view (2026-10-25 station AD)
SNAP = os.path.join(OUT, "profile-before")
SHOTS = SPEC.get("shots") or os.path.join(OUT, "shots")      # H7 (2026-11-26): bench_run names the folder (bench_def.shots_dir)
os.makedirs(SHOTS, exist_ok=True)
LISTS = ("plugins.txt", "loadorder.txt", "modlist.txt")
SKSE_LOGS = os.path.join(os.path.expanduser("~"), "Documents", "My Games", "Skyrim Special Edition", "SKSE")
NOTICE_WAIT = float(SPEC.get("notice_wait", 1.0))    # settle time before a capture. Measured 2026-11-25 with menus
                                                     # toggled off: a frame taken 1 s after a capture equals one taken 10 s
                                                     # after (region distance 0.33-0.37 either way), so no notice shows
FACE_D = 40.0                  # camera distance from the head bone (2026-11-24: frames helmet top to collar at FOV 80)
CAM_DZ = 1.0                   # camera height above the head bone (head bone = feet + 125 on a standard adult)
VFOV = 50.524                  # vertical field of view at fDefaultWorldFOV 80, 16:9 (measured 50.53 on 2026-11-24)
HDPT_TYPES = {0: "Misc", 1: "Face", 2: "Eyes", 3: "Hair", 4: "FacialHair", 5: "Scar", 6: "Brows"}
HEAD, EYE, HMAG = "NPC Head [Head]", "NPCEyeBone", "NPC Head MagicNode [Hmag]"   # skeleton.nif, read 2026-11-24
PLACE_TOL = 60.0
PRESENCE_MIN = 5.0             # measured 2026-10-29: empty vs empty 0.49 and 0.81, a framed NPC vs empty 14.45
LOG = open(os.path.join(OUT, "log.jsonl"), "a", encoding="utf-8", buffering=1)
BASE = {}                      # subject name -> runtime base FormID
STATE = {"freecam": False}
DESC = {}                      # formId -> names (cache for the whole launch)
ARMOR = {}                     # (formId, female) -> armor reading


def log(**row):
    row["t"] = round(time.time(), 3)
    LOG.write(json.dumps(row, default=str) + "\n")
    print(json.dumps(row, default=str)[:260], flush=True)


# ------------------------------------------------------------------ waiting (H9, 2026-11-27)
# The fixed sleeps of the subject sequence were replaced by polling for the condition each one waited for (table:
# RUN\traces\h9\waits.md). Every poll has an upper bound longer than the sleep it replaced; at the bound the code
# goes on exactly as it did after the old sleep, and a `poll_bound` row says so: a timeout never passes for success.
# Each finished wait logs a `wait` row with its seconds, so the time per step can be read from log.jsonl.
POLL_GAP = 0.1
RENDER_SETTLE = 0.5            # after the re-attached actor reads loaded and still: see reattach()
CAM_FRAMES = 3                 # frames the game must draw after the camera reads back where it was driven


def wait_for(what, cond, bound, gap=POLL_GAP, **ctx):
    """Poll cond() until it returns something truthy or `bound` seconds pass. Returns (value, seconds, bound_hit)."""
    t0 = time.time()
    v, err = None, None
    while True:
        try:
            v, err = cond(), None
        except Exception as e:
            v, err = None, repr(e)[:200]
        if v:
            secs = round(time.time() - t0, 2)
            log(step="wait", what=what, seconds=secs, **ctx)
            return v, secs, False
        if time.time() - t0 >= bound:
            secs = round(time.time() - t0, 2)
            log(step="poll_bound", what=what, bound=bound, seconds=secs, last=str(v)[:200], error=err, **ctx)
            return v, secs, True
        time.sleep(gap)


def frame_no():
    """The game's frame counter (/api/health `frame`, a read that does not wait for the main thread), or None."""
    try:
        f = db.health().get("frame")
        return f if isinstance(f, int) and not isinstance(f, bool) else None
    except Exception:
        return None


def frames_after(start, n):
    """A condition: the frame counter is at least n frames past `start` (False when either reading is missing)."""
    def cond():
        f = frame_no()
        return start is not None and f is not None and f - start >= n
    return cond


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
            shutil.copy2(cur, os.path.join(OUT, f"{PROFILE}.{f}.as-left"))
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


# ------------------------------------------------------------------ readings
def fid(v):
    return db._formid_of(v) if not db.failed(v) else None


def scalar(v):
    return v if isinstance(v, (int, float, str, bool)) or v is None else str(v)[:160]


def describe(f):
    """Names for a form: DevBench `forms describe` (plugin, localId, editorId where it has them) plus Form.GetName."""
    if not f:
        return None
    if f in DESC:
        return DESC[f]
    d = {"formId": f}
    try:
        r = db.post("forms", {"action": "describe", "formId": f})
        if isinstance(r, dict) and not r.get("_httpError"):
            src = r.get("form") if isinstance(r.get("form"), dict) else r
            for k in ("editorId", "plugin", "localId", "name", "type", "formType", "model", "fullName"):
                if k in src and isinstance(src[k], (int, float, str, bool)):
                    d[k] = src[k]
        else:
            d["describeError"] = str(r)[:120]
    except Exception as e:
        d["describeError"] = repr(e)[:120]
    if "name" not in d:
        n = db.ret("Form", "GetName", f)
        d["name"] = n if isinstance(n, str) else None
    DESC[f] = d
    return d


def armor_read(f, female):
    """An Armor by name, with the model path of each of its armatures: the wig-on-skin and helmet signatures."""
    if not f:
        return None
    key = (f, bool(female))
    if key in ARMOR:
        return ARMOR[key]
    r = dict(describe(f))
    r["slotMask"] = scalar(db.ret("Armor", "GetSlotMask", f))
    n = db.ret("Armor", "GetNumArmorAddons", f)
    addons = []
    for i in range(n if isinstance(n, int) and not isinstance(n, bool) else 0):
        aa = fid(db.ret("Armor", "GetNthArmorAddon", f, [i]))
        if not aa:
            continue
        a = dict(describe(aa))
        a["model"] = scalar(db.ret("ArmorAddon", "GetModelPath", aa, [False, bool(female)]))
        a["slotMask"] = scalar(db.ret("ArmorAddon", "GetSlotMask", aa))
        addons.append(a)
    r["addons"] = addons
    ARMOR[key] = r
    return r


def base_reads(b):
    if not b:
        return {"error": "no base"}
    r = {"formId": b}
    sex = db.ret("ActorBase", "GetSex", b)
    female = sex == 1
    r["sex"] = scalar(sex)
    r["race"] = describe(fid(db.ret("ActorBase", "GetRace", b)))
    r["height"] = scalar(db.ret("ActorBase", "GetHeight", b))
    r["weight"] = scalar(db.ret("ActorBase", "GetWeight", b))
    r["template"] = fid(db.ret("ActorBase", "GetTemplate", b))
    hc = fid(db.ret("ActorBase", "GetHairColor", b))
    r["hairColor"] = {"formId": hc, "rgb": scalar(db.ret("ColorForm", "GetColor", hc)) if hc else None}
    r["skin"] = armor_read(fid(db.ret("ActorBase", "GetSkin", b)), female)
    of = fid(db.ret("ActorBase", "GetOutfit", b, [False]))
    r["outfit"] = dict(describe(of)) if of else None
    if of:
        n = db.ret("Outfit", "GetNumParts", of)
        r["outfit"]["parts"] = [describe(fid(db.ret("Outfit", "GetNthPart", of, [i])))
                                for i in range(n if isinstance(n, int) and not isinstance(n, bool) else 0)]
    n = db.ret("ActorBase", "GetNumHeadParts", b)
    parts = []
    for i in range(n if isinstance(n, int) and not isinstance(n, bool) else 0):
        hp = fid(db.ret("ActorBase", "GetNthHeadPart", b, [i]))
        d = dict(describe(hp) or {})
        if hp:
            d["partName"] = scalar(db.ret("HeadPart", "GetPartName", hp))
            d["type"] = HDPT_TYPES.get(db.ret("HeadPart", "GetType", hp))
        parts.append(d)
    r["headParts"] = parts
    return r


def tele_one(ref):
    base = fid(db.ret("Actor", "GetActorBase", ref))
    lbase = fid(db.ret("Actor", "GetLeveledActorBase", ref))
    r = {"ref": ref, "loaded": db.is_3d_loaded(ref), "pos": db.pos(ref),
         "actorRace": describe(fid(db.ret("Actor", "GetRace", ref))),
         "aiEnabled": scalar(db.ret("Actor", "IsAIEnabled", ref)),
         "scale": scalar(db.ret("ObjectReference", "GetScale", ref)),
         "base": base_reads(base)}
    r["leveledBase"] = base_reads(lbase) if lbase and lbase != base else None
    female = (r["leveledBase"] or r["base"]).get("sex") == 1
    worn, seen = [], set()
    for bit in range(32):
        w = fid(db.ret("Actor", "GetWornForm", ref, [1 << bit]))
        if w and w not in seen:
            seen.add(w)
            a = dict(armor_read(w, female) or {})
            a["firstSlot"] = 30 + bit
            worn.append(a)
    r["worn"] = worn
    r["wornBody"] = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    return r


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
    # H9: was a fixed 1.2 s. Wait until the camera reads back the driven pose, then until the game has drawn
    # CAM_FRAMES frames from it. The capture's own settle (NOTICE_WAIT, in shot()) still follows.
    got = {}
    def at_pose():
        g = db.post("camera", {"action": "get"})
        got["g"] = g
        try:
            if math.dist((float(g["camX"]), float(g["camY"]), float(g["camZ"])), (x, y, z)) > 0.5:
                return False
            if g.get("camAngles") == "freeCameraState" and (
                    abs(wrap180(math.degrees(float(g["camYaw"])) - yaw_deg)) > 0.1 or abs(math.degrees(float(g["camPitch"])) - pitch_deg) > 0.1):
                return False
        except Exception:
            return False
        return True
    _, _, hit = wait_for("camera at pose", at_pose, 3.0)
    if not hit:
        wait_for("camera frames drawn", frames_after(frame_no(), CAM_FRAMES), 3.0)
    return {"want": {"x": x, "y": y, "z": z, "yawDeg": yaw_deg, "pitchDeg": pitch_deg}, "drive": r, "get": db.post("camera", {"action": "get"})}


def shot(cid):
    time.sleep(NOTICE_WAIT)
    r = db.shot(cid)
    path = os.path.join(SHOTS, "n2b", "default", r["file"]) if isinstance(r, dict) and r.get("file") else None
    blank = None
    if path and os.path.exists(path):
        try:
            from PIL import Image, ImageStat
            blank = ImageStat.Stat(Image.open(path).convert("L").resize((96, 54))).stddev[0] < 4.0
        except Exception as e:
            blank = repr(e)
    log(step="shot", id=cid, file=(r or {}).get("file") if isinstance(r, dict) else str(r)[:200], path=path, blank=blank)
    return path


def presence(path, empty):
    if not path or not empty or not os.path.exists(path) or not os.path.exists(empty):
        return None
    from PIL import Image, ImageChops, ImageStat
    def crop(p):
        return Image.open(p).convert("L").resize((192, 108)).crop((67, 22, 125, 97))
    return round(ImageStat.Stat(ImageChops.difference(crop(path), crop(empty))).mean[0], 2)


def place(ref, x, y, z):
    db.ret("ObjectReference", "SetPosition", ref, [float(x), float(y), float(z)], timeout_ms=20000)
    db.ret("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0], timeout_ms=20000)


def at_studio(ref):
    """(stored position, is the DRAWN body on the studio mark?). Judged from the head bone, not from the position the
    game stores (H1a, 2026-11-26). The stored position said "on the mark" for Vilja in every launch of the first run
    while her head bone, and so her drawn body, stood 183 to 262 units away: a SetPosition on an actor whose AI is
    off moves the stored position and leaves the body where it was. On the mark a standard adult's head bone reads
    about (-2400.0, 1598.4): well inside PLACE_TOL."""
    sx, sy, _ = CELL["studio"]
    p = db.pos(ref)
    h = nodepos(ref, HEAD)
    return p, bool(h) and abs(h[0] - sx) < PLACE_TOL and abs(h[1] - sy) < PLACE_TOL and db.is_3d_loaded(ref)


def reattach(ref):
    """Disable/Enable so the actor's 3D is rebuilt where it now stands. QASmoke's rooms cull an actor whose 3D still
    belongs to the room PlaceAtMe put it in (October smoke 2: position and 3D-loaded both read fine, nothing drawn)."""
    db.ret("ObjectReference", "Disable", ref, [False], timeout_ms=20000)
    # H9: was a fixed 1.0 s. The 3D is rebuilt only if it was let go: wait until the actor reads disabled and unloaded.
    wait_for("disabled for re-attach", lambda: unloaded(ref), 3.0, ref=ref)
    db.ret("ObjectReference", "Enable", ref, [False], timeout_ms=20000)
    # Hold it again at once. 2026-11-25 reference launches: Vilja (three launches) and Adelaisa (one of two) were 170 to
    # 250 units from the studio 3 s after Enable, i.e. they ran: an actor that is re-enabled can act before its
    # AI-off flag is honoured again. EnableAI(false) alone does not stop that; SetRestrained does (hold(), T4).
    hold(ref)
    # H9: was a fixed 3.0 s. Wait until the 3D is loaded again and the head bone stands on the mark and has stopped
    # moving (two readings in a row agree). If it never does (an actor that ran), the bound passes and the placement
    # check that follows judges it, as before. Then RENDER_SETTLE: "loaded" can read true before the rebuilt body and
    # face are drawn, and nothing the bridge reports says when they are.
    wait_for("re-attached, on the mark and still", still_on_mark(ref), 6.0, ref=ref)
    time.sleep(RENDER_SETTLE)


def unloaded(ref):
    return db.ret("ObjectReference", "IsDisabled", ref) is True and db.is_3d_loaded(ref) is False


def still_on_mark(ref):
    """A condition: 3D loaded, head bone within PLACE_TOL of the mark, head and eye bones within 0.1 unit of the
    previous reading."""
    sx, sy, _ = CELL["studio"]
    last = {}
    def cond():
        if db.is_3d_loaded(ref) is not True:
            last.clear()
            return False
        h, e = nodepos(ref, HEAD), nodepos(ref, EYE)
        if not h or not e:
            last.clear()
            return False
        prev, last["p"] = last.get("p"), (h, e)
        on = abs(h[0] - sx) < PLACE_TOL and abs(h[1] - sy) < PLACE_TOL
        return bool(on and prev and math.dist(prev[0], h) < 0.1 and math.dist(prev[1], e) < 0.1)
    return cond


def hold(ref):
    """Keep the actor where it is put and facing where it is turned: SetRestrained(true), then AI off.
    T4, 2026-11-26 (live\\V-T4-20261126-020556): six strategies, a fresh actor each, three subjects. Only
    SetRestrained(true) before the move and again after Enable left all three on the mark (1.5 units) and at yaw 0.
    With EnableAI(false) alone Vilja stood 213 units off, Adelaisa 299, and Delphine came back turned 136.5 degrees;
    SetDontMove fixed the turn but not the running; moving the actor while disabled fixed Adelaisa but not Vilja;
    SetAngle after Enable changed the stored heading (0) and left the drawn body turned."""
    db.ret("Actor", "SetRestrained", ref, [True], timeout_ms=20000)
    db.ret("Actor", "EnableAI", ref, [False], timeout_ms=20000)


def place_in_studio(ref):
    sx, sy, sz = CELL["studio"]
    place(ref, sx, sy, sz)
    # H9: was a fixed 2.0 s. Wait until the stored position reads the mark and the stored heading reads 0.
    def placed():
        p, a = db.pos(ref), db.ret("ObjectReference", "GetAngleZ", ref)
        return bool(p) and math.dist(p[:2], (sx, sy)) < 1.0 and isinstance(a, (int, float)) and not isinstance(a, bool) \
            and abs(wrap180(float(a))) < 0.5
    wait_for("stored position on the mark", placed, 4.0, ref=ref)
    reattach(ref)
    p, ok = at_studio(ref)
    tries = [{"how": "SetPosition, Disable/Enable", "pos": p, "head": nodepos(ref, HEAD), "ok": ok,
              "ai": scalar(db.ret("Actor", "IsAIEnabled", ref))}]
    # Removed 2026-11-26 (H1a): a second try "frozen, SetPosition again". It set the stored position back on the mark
    # with the AI off, which made the old check pass while the drawn body stayed where it had run to.
    if not ok:
        db.ret("ObjectReference", "MoveTo", ref, [{"form": db.PLAYER}, 0.0, 0.0, 0.0, False], timeout_ms=20000)
        time.sleep(2.0)
        place(ref, sx, sy, sz); time.sleep(3.0)
        reattach(ref)
        p, ok = at_studio(ref)
        tries.append({"how": "MoveTo player, SetPosition, Disable/Enable", "pos": p, "head": nodepos(ref, HEAD), "ok": ok})
    return p, ok, tries


def nodepos(ref, node):
    p = [db.ret("NetImmerse", "GetNodeWorldPosition" + a, None, [{"form": ref}, node, False]) for a in "XYZ"]
    return p if all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in p) else None


def wrap180(a):
    return (a + 180.0) % 360.0 - 180.0


def head_pose(ref):
    """Where the head is and which way it faces (world yaw, 0 = +Y, clockwise), from the head bones."""
    h, e, m = nodepos(ref, HEAD), nodepos(ref, EYE), nodepos(ref, HMAG)
    r = {"head": h, "eye": e, "hmag": m, "yaw": None, "yawSource": None}
    for key, n in (("eye", e), ("hmag", m)):
        if h and n and abs(n[0] - h[0]) + abs(n[1] - h[1]) > 0.05:
            r["yaw"] = round(math.degrees(math.atan2(n[0] - h[0], n[1] - h[1])), 2)
            r["yawSource"] = key
            break
    v = db.ret("ObjectReference", "GetAngleZ", ref)
    r["heading"] = float(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else None
    return r


def cam_for(pose, bearing_off):
    """Camera 40 units from the head bone on the bearing the head faces (+ offset), at head height, looking back."""
    hx, hy, hz = pose["head"]
    b = pose["yaw"] + bearing_off
    return (hx + FACE_D * math.sin(math.radians(b)), hy + FACE_D * math.cos(math.radians(b)), hz + CAM_DZ, b + 180.0)


def framing(pose, cam):
    """The collarbone-up rule as geometry: where the frame's bottom edge cuts the subject, relative to the head bone.
    Returns (ok, facts). Refused when the camera is not where it was sent, is pitched, or would see below ~20 units
    under the head bone (the collar on a standard adult; the chest starts below that)."""
    g = cam.get("get") if isinstance(cam, dict) else None
    want = cam["want"]
    facts = {"bottomBelowHead": round(FACE_D * math.tan(math.radians(VFOV / 2)) - CAM_DZ, 2), "distance": FACE_D}
    if isinstance(g, dict):
        try:
            # camera get: camX/camY/camZ, camYaw/camPitch in radians (devbench-screenshots camera-and-capture.md);
            # the angles are the driven ones only while camAngles == "freeCameraState" (api.md, since 1.25.0)
            gx, gy, gz = (float(g[k]) for k in ("camX", "camY", "camZ"))
            facts["posError"] = round(math.dist((gx, gy, gz), (want["x"], want["y"], want["z"])), 2)
            facts["camAngles"] = g.get("camAngles")
            if g.get("camAngles") == "freeCameraState":
                facts["pitchDeg"] = round(math.degrees(float(g["camPitch"])), 2)
                facts["yawErrorDeg"] = round(abs(wrap180(math.degrees(float(g["camYaw"])) - want["yawDeg"])), 2)
        except Exception as e:
            facts["readbackError"] = repr(e)[:120]
    ok = facts["bottomBelowHead"] <= 20.0 and facts.get("posError", 99.0) <= 4.0 and abs(facts.get("pitchDeg", 0.0)) <= 2.0 \
        and facts.get("yawErrorDeg", 0.0) <= 3.0
    return ok, facts


def portraits(name, ref, state, tag):
    """Face and three-quarter frames of a subject standing in the studio. Returns a dict for the subject row."""
    out = {"frames": {}, "cameras": {}, "framing": {}}
    worn = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    if not worn:
        out["UNASSESSED"] = "body slot empty: no picture taken"
        return out
    # physics-driven hair needs a few seconds to fall into place after a move: the whole launch (SPEC) or this subject
    settle = float(SPEC.get("settle") or next((s.get("settle") or 0 for s in SPEC["subjects"] if s["name"] == name), 0))
    if settle:
        time.sleep(settle)
        out["settle"] = settle
    pose = head_pose(ref)
    out["pose"] = pose
    if not pose["head"]:
        out["UNASSESSED"] = "head bone not readable: camera cannot be placed"
        return out
    if pose["yaw"] is None:
        pose["yaw"], pose["yawSource"] = (pose["heading"] or 0.0), "heading (no eye bone)"
    for view, off in (("face", 0.0), ("face34", 45.0)):
        x, y, z, yaw = cam_for(pose, off)
        cam = drive(x, y, z, yaw)
        ok, facts = framing(pose, cam)
        out["cameras"][view], out["framing"][view] = cam["want"], facts
        if not ok:
            out["frames"][view] = None
            out["UNASSESSED"] = f"framing check refused the {view} capture: {facts}"
            continue
        _, still = at_studio(ref)
        if not still:
            out["UNASSESSED"] = "subject left the studio"
            break
        out["frames"][view] = shot(f"{tag}-{name}-{state}-{view}")
        if SPEC.get("burst") and view == "face":
            # two more frames from the same camera, 3 s apart: is anything on the subject moving or still settling?
            out["burst"] = []
            for i in (1, 2):
                time.sleep(3.0)
                out["burst"].append(shot(f"{tag}-{name}-{state}-face-burst{i}"))
    return out


# ------------------------------------------------------------------ ops
def hud(off):
    if STATE.get("hud_off", False) == off:
        return
    log(step="tm", off=off, res=db.post("console", {"action": "exec", "command": "tm"}))
    STATE["hud_off"] = off
    time.sleep(1.0)


def ai_travel(off):
    if STATE.get("ai_off", False) == off:
        return
    log(step="tai", off=off, res=db.post("console", {"action": "exec", "command": "tai"}))
    STATE["ai_off"] = off
    time.sleep(1.0)


def release_cam():
    if STATE["freecam"]:
        log(step="freecam_off", res=db.post("camera", {"action": "freecam", "on": False}))
        STATE["freecam"] = False
        time.sleep(1.0)


def empty_studio(tag):
    sx, sy, sz = CELL["studio"]
    drive(sx, sy + FACE_D, sz + 125.0 + CAM_DZ, 180.0)
    STATE["empty"] = shot(f"{tag}-empty")
    log(step="empty_studio", path=STATE["empty"])


def freeze(ref):
    before = db.ret("Actor", "IsAIEnabled", ref)
    hold(ref)                      # restrained BEFORE the move as well as after Enable: both are needed (T4)
    # H9: was a fixed 0.5 s. Wait until the AI reads off.
    wait_for("AI off", lambda: db.ret("Actor", "IsAIEnabled", ref) is False, 2.0, ref=ref)
    return {"aiBefore": scalar(before), "aiAfter": scalar(db.ret("Actor", "IsAIEnabled", ref))}


def swap_slots():
    """Slot bits the swap outfit's armors claim (first bit of each), read once per launch."""
    if "swap_bits" not in STATE:
        bits = set()
        of = BASE["@swap"]
        n = db.ret("Outfit", "GetNumParts", of)
        for i in range(n if isinstance(n, int) and not isinstance(n, bool) else 0):
            m = db.ret("Armor", "GetSlotMask", fid(db.ret("Outfit", "GetNthPart", of, [i])))
            if isinstance(m, int) and not isinstance(m, bool) and m > 0:
                bits.add(m & -m)
        STATE["swap_bits"] = bits
    return STATE["swap_bits"]


def swap_outfit(ref, worn_before=None):
    """One full outfit to another (never a removal). Returns what changed on the body slot."""
    before = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    # H9: the slots to watch while the change is applied: those worn before (from the default telemetry) and those
    # the swap outfit's armors claim.
    bits = sorted({0x4} | {1 << (w["firstSlot"] - 30) for w in (worn_before or []) if isinstance(w.get("firstSlot"), int)}
                  | swap_slots())
    def worn_now():
        return tuple(fid(db.ret("Actor", "GetWornForm", ref, [b])) for b in bits)
    first = worn_now()
    r = {"bodyBefore": before, "set": scalar(db.ret("Actor", "SetOutfit", ref, [{"form": BASE["@swap"]}, False], timeout_ms=20000))}
    # H9: was a fixed 3.0 s. Wait until what the watched slots hold has changed and two readings in a row agree (the
    # change is applied, not half-way). A swap that changes none of them waits out the bound and goes on as before.
    last = {}
    def applied():
        w = worn_now()
        prev, last["w"] = last.get("w"), w
        return w != first and w == prev
    wait_for("outfit change applied", applied, 6.0, ref=ref, slots=[hex(b) for b in bits])
    after = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
    def outfit_is_swap():
        """Does the actor now carry the swap outfit? SetOutfit changes the outfit of the actor's base."""
        for getter in ("GetActorBase", "GetLeveledActorBase"):
            b = fid(db.ret("Actor", getter, ref))
            if b and fid(db.ret("ActorBase", "GetOutfit", b, [False])) == BASE["@swap"]:
                return True
        return False

    if after == before and not outfit_is_swap():      # a frozen actor may not process the change: let it run for a
        db.ret("Actor", "EnableAI", ref, [True], timeout_ms=20000)       # moment (still restrained), then freeze again
        time.sleep(4.0)
        db.ret("Actor", "EnableAI", ref, [False], timeout_ms=20000)
        time.sleep(1.0)
        after = fid(db.ret("Actor", "GetWornForm", ref, [0x4]))
        r["neededAi"] = True
    r["bodyAfter"] = after
    r["outfitIsSwap"] = outfit_is_swap()
    # H4, 2026-11-26. "Changed" used to mean "a different form on the body slot". Dervenin's own outfit (02D75F) and
    # the swap outfit (02D75E) hold the SAME clothes and boots; his adds a hat. The swap worked in every launch (the
    # hat left, the base outfit read 02D75E) and was reported as failed because the body form was the same. The
    # swap is judged by the outfit the actor now carries; a body slot must still be filled (never photograph without).
    r["changed"] = bool(after) and (after != before or r["outfitIsSwap"])
    return r


def remove_subject(ref, cams, tag, name):
    """Disable and delete the subject, then photograph the vacated studio from EVERY camera pose its frames used
    (cams: {"<state>-<view>": pose}). The scorer subtracts each frame's own vacant frame to find the subject, so a
    camera that follows a taller or shorter head does not turn the shifted background into a difference; the first
    one is also the proof the next subject starts in an empty room."""
    db.ret("ObjectReference", "Disable", ref, [False], timeout_ms=20000)
    # H9: was a fixed 1.0 s. The vacant frames must not show the subject: wait until it reads disabled and unloaded.
    wait_for("disabled for removal", lambda: unloaded(ref), 3.0, ref=ref)
    r = {"disabled": scalar(db.ret("ObjectReference", "IsDisabled", ref)), "vacants": {}}
    db.ret("ObjectReference", "Delete", ref, timeout_ms=20000)
    for key, w in (cams or {}).items():
        drive(w["x"], w["y"], w["z"], w["yawDeg"])
        r["vacants"][key] = shot(f"{tag}-{name}-{key}-vacant")
    r["vacant"] = r["vacants"].get("default-face") or next(iter(r["vacants"].values()), None)
    return r


def spawn(base_form, x, y, z):
    """dbharness.spawn (settle 3.0, then 1.0 after the move) with its two sleeps replaced by polling (H9).
    Same calls in the same order, same fallback for a PlaceAtMe that returns no formId, same final assertion."""
    before = set(db.nearby("ACHR"))
    v = db.ret("ObjectReference", "PlaceAtMe", db.PLAYER, [{"form": base_form}, 1, True, False], timeout_ms=20000)
    f = fid(v)
    if f:
        # was 3.0 s: the clone's 3D is loaded and it wears something on the body (its outfit is equipped). A subject
        # with an empty body slot waits out the bound and is then judged UNASSESSED as before.
        wait_for("spawned: loaded and dressed", lambda: db.is_3d_loaded(f) is True and bool(fid(db.ret("Actor", "GetWornForm", f, [0x4]))),
                 6.0, ref=f)
    else:
        # no formId (a scripted reference): the new ACHR is found by diffing the loaded ones, as dbharness does
        got, _, _ = wait_for("spawned: new actor listed", lambda: [g for g in db.nearby("ACHR") if g not in before], 6.0)
        after = db.nearby("ACHR")
        new = [g for g in after if g not in before]
        if not new:
            db._fail(f"no new ACHR appeared; PlaceAtMe returned {v}", "spawn")
        same = [g for g in new if (after[g].get("base") or {}).get("formId", "").lower() == base_form.lower()]
        f = (same or new)[0]
    db.ret("ObjectReference", "SetPosition", f, [float(x), float(y), float(z)], timeout_ms=20000)
    # was 1.0 s: loaded at the park position
    def parked():
        p = db.pos(f)
        return db.is_3d_loaded(f) is True and bool(p) and math.dist(p[:2], (x, y)) < 25.0
    wait_for("spawned: moved to the park", parked, 3.0, ref=f)
    if db.is_3d_loaded(f) is not True:
        db._fail(f"spawned {f} but Is3DLoaded is not True", "spawn")
    return f


# ------------------------------------------------------------------ backdrop probe (opt-in, 2026-11-27)
# Only when the launch spec carries "backdrop" (bench_run: N2B_DRIVER_OPTIONS='{"backdrop": true}'). Without it none
# of this runs and the driver behaves exactly as before. Per subject, AFTER its normal default-state frames: the cell's
# fog is set so that everything beyond the subject draws as one colour, the face frame is taken again from the same
# camera pose, the fog is put back and checked, and once the subject is gone each variant's vacant frame is taken.
# Calls (papyrus-reference vanilla\Cell.md and skse\Cell.md): Cell.IsInterior(), Cell.SetFogColor(int x6),
# Cell.SetFogPlanes(float near, float far), Cell.SetFogPower(float), ObjectReference.GetParentCell(). The cell is
# passed as `self` {"form": <formId>} (DevBench api.md "papyrus", self is {"form": "0x14 | editorId"}); its formId
# comes from `inspect scene` -> cell.formId, the read dbharness.move_within_cell already makes.
BD = {}
RESTORE_MAX = 2.0              # mean |L| difference (192x108, whole frame) for "the fog is back": empty vs empty read
                               # 0.49 and 0.81 on the presence crop (PRESENCE_MIN, 2026-10-29)
FOG_LATE_FRAMES = 30           # the first variant is photographed again this many frames later: was the fog all drawn?


def frame_diff(p, q):
    if not p or not q or not os.path.exists(p) or not os.path.exists(q):
        return None
    from PIL import Image, ImageChops, ImageStat
    a, b = (Image.open(x).convert("L").resize((192, 108)) for x in (p, q))
    return round(ImageStat.Stat(ImageChops.difference(a, b)).mean[0], 3)


def bd_call(fn, args):
    v = db.ret("Cell", fn, BD["cell"], args, timeout_ms=20000)
    return {"call": f"Cell.{fn}{tuple(args)}", "failed": bool(db.failed(v)), "ret": scalar(v)}


def backdrop_setup():
    """Once per launch, after the first frame of the empty studio. Finds the cell, checks the bridge takes it as a
    Cell, and reads the fog values to restore from the plugins (Papyrus has no fog getter). Sets BD["ready"]."""
    import backdrop as BK
    cfg = SPEC["backdrop"] if isinstance(SPEC["backdrop"], dict) else {}
    r = {"variants": cfg.get("variants") or BK.VARIANTS, "sweep": cfg.get("sweep", BK.SWEEP)}
    try:
        cb = db.scene().get("cell") or {}
        r["sceneCell"] = {k: cb.get(k) for k in ("formId", "editorId", "interior", "name") if k in cb}
        cell = cb.get("formId")
        par = db.ret("ObjectReference", "GetParentCell", db.PLAYER)
        r["parentCell"] = scalar(par) if db.failed(par) else fid(par)
        same = bool(cell) and isinstance(r["parentCell"], str) and int(str(cell), 16) == int(r["parentCell"], 16)
        BD["cell"] = cell
        iv = db.ret("Cell", "IsInterior", cell) if cell else None
        r["isInterior"] = scalar(iv)
        names = db.loaded_plugins()
        paths, missing = BK.plugin_paths(LAB, PROFILE, names)
        rec = BK.scan(paths)
        r["plugins"] = {"found": len(paths), "missing": missing}
        r["records"] = rec["records"]
        r["template"] = rec["template"]
        r["effective"] = rec["effective"]
        r["restoreCandidates"] = rec["restoreCandidates"]
        win = rec["winner"] or {}
        r["formIdMatchesRecord"] = bool(cell) and win.get("formId", "").lower() == "skyrim.esm:%06x" % (int(str(cell), 16) & 0xFFFFFF) \
            and int(str(cell), 16) >> 24 == 0
        why = [w for w, bad in (("no cell formId from inspect scene", not cell),
                                ("GetParentCell(player) is not the scene cell", not same),
                                (f"the scene cell is not {CELL['cell']}", cb.get("editorId") != CELL["cell"]),
                                ("Cell.IsInterior did not return true through the bridge", iv is not True),
                                ("no fog values to restore were read from the plugins", not rec["restoreCandidates"]),
                                ("the plugins' record is not this cell's formId", not r["formIdMatchesRecord"]),
                                ("the cell uses sky lighting (the setters work on non-sky-lit cells only)",
                                 bool((rec["effective"] or {}).get("skyLit"))))
               if bad]
        r["refused"] = why
    except Exception as e:
        r["refused"] = ["setup error: " + repr(e)[:300]]
    BD.update(r, ready=not r["refused"], restore=0, broken=False)
    log(step="backdrop_setup", ready=BD["ready"], **r)
    return BD["ready"]


def set_fog(label, rgb_near, rgb_far, near, far, power=None):
    calls = [bd_call("SetFogColor", [int(c) for c in list(rgb_near) + list(rgb_far)]),
             bd_call("SetFogPlanes", [float(near), float(far)])]
    if power is not None:
        calls.append(bd_call("SetFogPower", [float(power)]))
    # Nothing the bridge reports says when new fog is drawn: wait until the game has drawn CAM_FRAMES frames after the
    # calls returned (the camera wait's rule). The first variant is photographed again later to show whether that was
    # enough (FOG_LATE_FRAMES).
    wait_for("fog drawn", frames_after(frame_no(), CAM_FRAMES), 3.0, fog=label)
    log(step="backdrop_fog", fog=label, nearColor=list(rgb_near), farColor=list(rgb_far), near=near, far=far, power=power, calls=calls)
    return not any(c["failed"] for c in calls)


def fog_variant(v):
    return set_fog(v["id"], v["rgb"], v["rgb"], v["near"], v["far"], v.get("power"))


def fog_restore():
    c = BD["restoreCandidates"][BD["restore"]]
    # SetFogPower is put back only if a variant changed it (none does by default)
    power = c["power"] if any(v.get("power") is not None for v in BD["variants"] + [BD.get("sweep") or {}]) else None
    return set_fog("restore:" + c["source"], c["nearColor"], c["farColor"], c["near"], c["far"], power)


def bd_geometry(cam):
    """The distances the fog planes rest on, for this camera. The floor is the nearest background in a face frame:
    the frame's bottom edge looks down by VFOV/2 from the camera's height over the studio floor."""
    h = cam["z"] - CELL["studio"][2]
    floor = h / math.tan(math.radians(VFOV / 2))
    return {"camToHeadBone": FACE_D, "camAboveFloor": round(h, 2), "floorDistanceAtBottomEdge": round(floor, 1),
            "subjectDepthAssumed": 80.0}


def bd_guarded_shot(name, ref, pose, cam_want, cid):
    """The capture guards of portraits(), in the same order, in front of every backdrop frame of a subject: body slot
    filled, framing check on the camera as driven, subject still on the mark."""
    if not fid(db.ret("Actor", "GetWornForm", ref, [0x4])):
        return None, "body slot empty: no picture taken"
    cam = drive(cam_want["x"], cam_want["y"], cam_want["z"], cam_want["yawDeg"], cam_want.get("pitchDeg", 0.0))
    ok, facts = framing(pose, cam)
    if not ok:
        return None, f"framing check refused: {facts}"
    _, still = at_studio(ref)
    if not still:
        return None, "subject left the studio"
    return shot(cid), None


def backdrop_faces(name, ref, pics, tag):
    """The subject is frozen on the mark and its normal frames are taken. Each variant: fog on, the face frame again
    from the same camera pose; then the fog back and a check frame against the normal face frame."""
    cam_want, pose = (pics.get("cameras") or {}).get("face"), pics.get("pose")
    out = {"name": name, "camera": cam_want, "normalFace": (pics.get("frames") or {}).get("face"), "variants": {}}
    if not cam_want or not pose or not out["normalFace"]:
        out["skipped"] = "no normal face frame"
        log(step="backdrop_faces", **out)
        return out
    geo = bd_geometry(cam_want)
    out["geometry"] = geo
    vs = list(BD["variants"]) + ([BD["sweep"]] if BD.get("sweep") and not BD.get("swept") else [])
    try:
        for i, v in enumerate(vs):
            vr = {"rgb": v["rgb"], "near": v["near"], "far": v["far"], "power": v.get("power")}
            if v["near"] <= geo["subjectDepthAssumed"] or v["far"] >= geo["floorDistanceAtBottomEdge"] or v["far"] <= v["near"]:
                vr["refused"] = "fog planes outside (subject depth, floor distance)"
                out["variants"][v["id"]] = vr
                continue
            vr["fogSet"] = fog_variant(v)
            vr["face"], why = bd_guarded_shot(name, ref, pose, cam_want, f"{tag}-{name}-bd-{v['id']}-face")
            if why:
                vr["UNASSESSED"] = why
                out["variants"][v["id"]] = vr
                break
            if i == 0:
                wait_for("fog late frame", frames_after(frame_no(), FOG_LATE_FRAMES), 5.0, fog=v["id"])
                vr["faceLate"], _ = bd_guarded_shot(name, ref, pose, cam_want, f"{tag}-{name}-bd-{v['id']}-face-late")
            out["variants"][v["id"]] = vr
            if v is BD.get("sweep"):
                BD["swept"] = True
    finally:
        out["restore"] = []
        # put the fog back; check it on the face frame (the subject is frozen, so it should equal the normal one up to
        # repeat noise); if it does not, try the next set of values to restore
        while True:
            c = BD["restoreCandidates"][BD["restore"]]
            rr = {"source": c["source"], "set": fog_restore()}
            rr["face"], why = bd_guarded_shot(name, ref, pose, cam_want, f"{tag}-{name}-bd-restored{BD['restore']}-face")
            rr["diffToNormal"] = frame_diff(rr["face"], out["normalFace"])
            rr["ok"] = rr["diffToNormal"] is not None and rr["diffToNormal"] <= RESTORE_MAX
            if why:
                rr["UNASSESSED"] = why
            out["restore"].append(rr)
            if rr["ok"] or why or BD["restore"] + 1 >= len(BD["restoreCandidates"]):
                break
            BD["restore"] += 1
        if not out["restore"][-1]["ok"]:
            BD["broken"] = True              # no further subject gets a backdrop; the frames after this one are suspect
            out["restoreFailed"] = "the fog could not be shown to be back: later frames of this launch may be under changed fog"
        log(step="backdrop_faces", **out)
    return out


def backdrop_vacants(name, bd, tag, removed):
    """The subject is gone (remove_subject took the normal vacant frames). Each variant photographed: its fog, its
    vacant frame from the face camera; then the fog back and a frame of the empty studio against the first one."""
    out = {"name": name, "normalFace": bd.get("normalFace"), "normalVacant": (removed or {}).get("vacants", {}).get("default-face"),
           "camera": bd.get("camera"), "geometry": bd.get("geometry"), "variants": {}, "restoreFaces": bd.get("restore")}
    w = bd.get("camera")
    try:
        for vid, vr in bd.get("variants", {}).items():
            vo = dict(vr)
            if vr.get("face") and w:
                v = next(x for x in BD["variants"] + [BD.get("sweep") or {}] if x.get("id") == vid)
                vo["fogSetVacant"] = fog_variant(v)
                drive(w["x"], w["y"], w["z"], w["yawDeg"], w.get("pitchDeg", 0.0))
                vo["vacant"] = shot(f"{tag}-{name}-bd-{vid}-face-vacant")
            out["variants"][vid] = vo
    finally:
        fog_restore()
        sx, sy, sz = CELL["studio"]
        drive(sx, sy + FACE_D, sz + 125.0 + CAM_DZ, 180.0)        # empty_studio()'s pose
        p = shot(f"{tag}-{name}-bd-restored-empty")
        out["restoredEmpty"] = {"path": p, "first": STATE.get("empty"), "diff": frame_diff(p, STATE.get("empty"))}
        out["restoredEmpty"]["ok"] = out["restoredEmpty"]["diff"] is not None and out["restoredEmpty"]["diff"] <= RESTORE_MAX
        if not out["restoredEmpty"]["ok"]:
            BD["broken"] = True
        log(step="backdrop_subject", **out)


def process(sub, tag):
    """One subject, start to finish. Always logs a `subject` row per state; never leaves the subject in the studio."""
    name = sub["name"]
    db.video_mark(f"n2b: {name}")
    px, py = CELL["park"]
    ref, cams, bd = None, {}, None
    row = {"name": name, "state": "default", "launch": SPEC["launch"]}
    try:
        ref = spawn(BASE[name], px, py, CELL["studio"][2])
        row["ref"] = ref
        row["freeze"] = freeze(ref)
        row["tele"] = tele_one(ref)
        if not row["tele"]["wornBody"]:
            row["UNASSESSED"] = "body slot empty: no picture taken"
            log(step="subject", **row)
            return
        p, ok, tries = place_in_studio(ref)
        row["placement"] = tries
        if not ok:
            row["UNASSESSED"] = "not in the studio after placement"
            log(step="subject", **row)
            return
        pics = portraits(name, ref, "default", tag)
        face = pics["frames"].get("face")
        pose = pics.get("pose") or {}
        if face and STATE.get("empty") and pose.get("yaw") is not None and abs(wrap180(pose["yaw"])) < 3.0:
            seen = presence(face, STATE["empty"])
            if seen is not None and seen < PRESENCE_MIN:      # one more re-attach, then the frames stand as they are
                reattach(ref)
                row["presenceFirst"] = seen
                pics = portraits(name, ref, "default", tag + "r")
        row.update(pics)
        cams.update({f"default-{v}": w for v, w in (pics.get("cameras") or {}).items() if (pics.get("frames") or {}).get(v)})
        log(step="subject", **row)
        if SPEC.get("backdrop") and BD.get("ready") and not BD.get("broken") and not row.get("UNASSESSED") \
                and (pics.get("frames") or {}).get("face"):
            try:                                 # a probe error must not cost the subject its swap state
                bd = backdrop_faces(name, ref, pics, tag)
            except Exception as e:
                BD["broken"] = True              # the fog may be left changed: no further backdrop this launch
                log(step="backdrop_error", name=name, error=repr(e)[:300], tb=traceback.format_exc()[-1200:])
        if sub.get("swap"):
            srow = {"name": name, "state": "swap", "launch": SPEC["launch"], "ref": ref}
            srow["swap"] = swap_outfit(ref, row["tele"].get("worn"))
            srow["tele"] = tele_one(ref)
            if not srow["swap"]["changed"]:
                srow["UNASSESSED"] = "the outfit swap did not change the body slot"
            elif not srow["tele"]["wornBody"]:
                srow["UNASSESSED"] = "body slot empty after the swap: no picture taken"
            else:
                srow.update(portraits(name, ref, "swap", tag))
                cams.update({f"swap-{v}": w for v, w in (srow.get("cameras") or {}).items() if (srow.get("frames") or {}).get(v)})
            log(step="subject", **srow)
    except Exception as e:
        # The default row is logged as soon as its frames exist, so an error after that belongs to the swap state:
        # it must not overwrite a good default reading with a second, failed row of the same state.
        err = {"name": name, "launch": SPEC["launch"], "ref": ref, "state": "swap" if "frames" in row else "default",
               "UNASSESSED": "driver error: " + repr(e)[:300], "tb": traceback.format_exc()[-1200:]}
        if err["state"] == "default":
            err = {**row, **err}
        log(step="subject", **err)
        db.health_check(expect_exe="SkyrimSE")      # raises if the game is gone: the launch aborts
    finally:
        if ref:
            removed = None
            try:
                removed = remove_subject(ref, cams, tag, name)
                log(step="removed", name=name, **removed)
            except Exception as e:
                log(step="remove_error", name=name, error=repr(e)[:300])
            if bd:
                try:
                    backdrop_vacants(name, bd, tag, removed)
                except Exception as e:
                    log(step="backdrop_error", name=name, error=repr(e)[:300], tb=traceback.format_exc()[-1200:])


def t4_probe(sub):
    """T4 (battle plan): what keeps a subject ON the mark and FACING 0 through the Disable/Enable that re-attaches its
    3D? No picture is taken. Each strategy gets a freshly spawned actor and logs, 1, 3 and 6 s after it finished, the
    stored position, the head bone's position, the head's yaw and the heading. Every engine call's return is logged
    too, so a function the bridge refuses shows as such and is not mistaken for a strategy that did nothing.
    Known before it runs (first run, 2026-11-25): with S0 Vilja ended 183 to 262 units off the mark in every launch,
    and Farkas, Ysolda and Delphine ended turned by 103, 241 and 137 degrees in some launches."""
    name = sub["name"]
    px, py = CELL["park"]
    sx, sy, sz = CELL["studio"]

    def call(cls, fn, ref, args=None):
        v = db.ret(cls, fn, ref, args, timeout_ms=20000) if args is not None else db.ret(cls, fn, ref, timeout_ms=20000)
        return {"call": f"{cls}.{fn}{tuple(args) if args is not None else ()}", "failed": bool(db.failed(v)), "ret": scalar(v)}

    def s0(ref, calls):
        place(ref, sx, sy, sz); time.sleep(2.0)
        calls += [call("ObjectReference", "Disable", ref, [False])]; time.sleep(1.0)
        calls += [call("ObjectReference", "Enable", ref, [False]), call("Actor", "EnableAI", ref, [False])]

    def held(setter):
        def run(ref, calls):
            calls += [call("Actor", setter, ref, [True])]
            place(ref, sx, sy, sz); time.sleep(2.0)
            calls += [call("ObjectReference", "Disable", ref, [False])]; time.sleep(1.0)
            calls += [call("ObjectReference", "Enable", ref, [False]), call("Actor", setter, ref, [True]),
                      call("Actor", "EnableAI", ref, [False])]
        return run

    def s3(ref, calls):                      # move it while it is disabled: there is no body to leave behind
        calls += [call("ObjectReference", "Disable", ref, [False])]; time.sleep(1.0)
        place(ref, sx, sy, sz); time.sleep(1.0)
        calls += [call("ObjectReference", "Enable", ref, [False]), call("Actor", "EnableAI", ref, [False])]

    def s4(ref, calls):
        s3(ref, calls)
        calls += [call("Actor", "SetDontMove", ref, [True])]

    def s5(ref, calls):                      # S3, then turn it back to 0 once it is enabled and frozen
        s3(ref, calls)
        time.sleep(1.0)
        calls += [call("ObjectReference", "SetAngle", ref, [0.0, 0.0, 0.0])]

    strategies = [("S0 as the driver does today: SetPosition, Disable/Enable, EnableAI(false)", s0),
                  ("S1 SetDontMove(true) before the move and again after Enable", held("SetDontMove")),
                  ("S2 SetRestrained(true) before the move and again after Enable", held("SetRestrained")),
                  ("S3 Disable first, SetPosition while disabled, Enable, EnableAI(false)", s3),
                  ("S4 S3, then SetDontMove(true)", s4),
                  ("S5 S3, then SetAngle(0,0,0) after Enable", s5)]
    for label, fn in strategies:
        ref = None
        try:
            ref = db.spawn(BASE[name], px, py, sz)
            calls = []
            # AI off only, as freeze() did when this probe ran (freeze() now restrains as well, which is S2's answer)
            fr = {"aiBefore": scalar(db.ret("Actor", "IsAIEnabled", ref)),
                  "set": scalar(db.ret("Actor", "EnableAI", ref, [False], timeout_ms=20000))}
            time.sleep(0.5)
            fn(ref, calls)
            log(step="t4_calls", name=name, strategy=label, freeze=fr, calls=calls)
            waited = 0.0
            for w in (1.0, 2.0, 3.0):
                time.sleep(w); waited += w
                pose = head_pose(ref)
                h = pose["head"]
                log(step="t4", name=name, strategy=label, after=waited, stored=db.pos(ref), head=h,
                    offMark=round(math.dist((h[0], h[1]), (sx, sy)), 1) if h else None,
                    yaw=pose["yaw"], heading=pose["heading"], loaded=db.is_3d_loaded(ref),
                    ai=scalar(db.ret("Actor", "IsAIEnabled", ref)))
        except Exception as e:
            log(step="t4_error", name=name, strategy=label, error=repr(e)[:300])
            db.health_check(expect_exe="SkyrimSE")
        finally:
            if ref:
                db.ret("ObjectReference", "Disable", ref, [False], timeout_ms=20000)
                time.sleep(0.5)
                db.ret("ObjectReference", "Delete", ref, timeout_ms=20000)
                time.sleep(0.5)


def reload_pass(names):
    """Same-process save and reload on a few subjects: the runtime backends must still show the appearance after it."""
    release_cam()
    hud(False)
    refs = {}
    px, py = CELL["park"]
    for i, n in enumerate(names):
        try:
            refs[n] = db.spawn(BASE[n], px - 60.0 * i, py, CELL["studio"][2])
            freeze(refs[n])
        except Exception as e:
            log(step="reload_spawn_refused", name=n, error=repr(e)[:300])
    ai_travel(False)
    save = f"n2b_{SPEC['launch']}"
    r = db.post("game", {"action": "save", "name": save})
    time.sleep(10.0)
    found = glob.glob(os.path.join(LAB, "profiles", PROFILE, "saves", f"{save}*"))
    log(step="save", name=save, res=r, files=found)
    if not found:
        raise RuntimeError(f"save {save} produced no file")
    r = db.post("game", {"action": "load", "name": save})
    time.sleep(8.0)
    c = db.wait_loaded()
    time.sleep(8.0)
    db.clear_modals(6)
    STATE.update(freecam=False, ai_off=False, hud_off=False)
    log(step="load", name=save, res=r, loaded=c)
    ai_travel(True)
    hud(True)
    for n, ref in refs.items():
        row = {"name": n, "state": "reload", "launch": SPEC["launch"], "ref": ref}
        cams = {}
        try:
            row["freeze"] = freeze(ref)
            row["tele"] = tele_one(ref)
            if not row["tele"]["wornBody"]:
                row["UNASSESSED"] = "body slot empty: no picture taken"
            else:
                p, ok, tries = place_in_studio(ref)
                row["placement"] = tries
                if ok:
                    row.update(portraits(n, ref, "reload", "reload"))
                    cams = {f"reload-{v}": w for v, w in (row.get("cameras") or {}).items() if (row.get("frames") or {}).get(v)}
                else:
                    row["UNASSESSED"] = "not in the studio after placement"
        except Exception as e:
            row["UNASSESSED"] = "driver error: " + repr(e)[:300]
        log(step="subject", **row)
        try:
            log(step="removed", name=n, state="reload", **remove_subject(ref, cams, "reload", n))
        except Exception as e:
            log(step="remove_error", name=n, error=repr(e)[:300])


def probes():
    """Call shapes this harness has not used before (DevBench 1.39.0): logged raw, nothing depends on them."""
    for tool, args in (("forms", {"action": "describe", "formId": "0x13BB8"}),
                       ("forms", {"action": "headParts", "formId": "0x13BB8"})):
        try:
            log(step="probe", tool=tool, args=args, res=str(db.post(tool, args))[:1500])
        except Exception as e:
            log(step="probe_error", tool=tool, args=args, error=repr(e)[:300])


def copy_logs():
    dst = os.path.join(OUT, "logs"); os.makedirs(dst, exist_ok=True)
    for pat in ("Recast.log", "SkyPatcher*.log", "skse64.log", "DevBench*.log", "po3_*.log", "crash-*.log", "hdtSMP64*.log"):
        for p in glob.glob(os.path.join(SKSE_LOGS, pat)):
            if os.path.getmtime(p) < STATE.get("t_launch", 0):
                continue
            shutil.copy2(p, dst)
    log(step="logs", files=sorted(os.listdir(dst)))


# ------------------------------------------------------------------ lifecycle
def new_game(limit):
    """A new game in the studio cell by `coc` from the main menu (dbharness.new_game_at_cell with a bounded wait).
    When no cell loads, what the game process looks like is logged: a frozen main thread reads Responding=False."""
    assert db.loaded() is None, "main menu expected"
    t0 = time.time()
    db.post("console", {"action": "exec", "command": f"coc {CELL['cell']}"})
    c = db.wait_loaded(limit=limit)
    secs = round(time.time() - t0, 1)
    if not c:
        import subprocess
        ps = "Get-Process -Name SkyrimSE -ErrorAction SilentlyContinue | Select-Object Id,Responding,MainWindowTitle,@{n='MemGB';e={[math]::Round($_.WorkingSet64/1GB,2)}},@{n='Threads';e={$_.Threads.Count}} | ConvertTo-Json -Compress"
        r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True)
        log(step="no_cell", seconds=secs, process=r.stdout.strip()[:600])
        return None, secs
    time.sleep(12)
    db.clear_modals(6)
    return c, secs


def notice_test():
    """Does the 'ScreenShot created' notice of one capture show in the next, with menus toggled off? Three frames of
    the empty studio: after the full wait, 1 second after that, and after the full wait again. If the middle frame
    equals the others, the wait between captures can be dropped."""
    global NOTICE_WAIT
    from PIL import Image, ImageChops, ImageStat
    keep = NOTICE_WAIT
    try:
        a = shot("notice-a")
        NOTICE_WAIT = 1.0
        b = shot("notice-b")
        NOTICE_WAIT = keep
        c = shot("notice-c")
        def d(p, q):
            return round(ImageStat.Stat(ImageChops.difference(Image.open(p).convert("L"), Image.open(q).convert("L"))).mean[0], 4)
        def dmax(p, q):
            return ImageChops.difference(Image.open(p).convert("L"), Image.open(q).convert("L")).getextrema()[1]
        log(step="notice_test", a=a, b=b, c=c, mean_ab=d(a, b), mean_bc=d(b, c), mean_ac=d(a, c), max_ab=dmax(a, b), max_bc=dmax(b, c), max_ac=dmax(a, c))
    except Exception as e:
        log(step="notice_test_error", error=repr(e)[:300])
    finally:
        NOTICE_WAIT = keep


def output_last():
    """NPC2's output plugin must be the LAST active plugin, as NPC2 built it. 2026-11-25: bench_run placed it before
    two AI Overhaul patches in A2 and their vanilla NPC records won in the game. bench_run is fixed; this is the check
    at the point of use (and the repair, for a tier process that loaded the old rule). Logs what it found and did."""
    outdir = os.path.join(LAB, "mods", "NPC2Bench Output - " + SPEC["launch"])
    mine = {f.lower() for f in os.listdir(outdir) if f.lower().endswith((".esp", ".esm", ".esl"))} if os.path.isdir(outdir) else set()
    res = {"output": sorted(mine), "moved": False}
    for fn in ("plugins.txt", "loadorder.txt"):
        path = os.path.join(LAB, "profiles", PROFILE, fn)
        lines = [l for l in open(path, encoding="utf-8").read().splitlines() if l.strip()]
        name = lambda l: l.lstrip("*").strip().lower()
        idx = [i for i, l in enumerate(lines) if name(l) in mine]
        after = [l for l in lines[idx[-1] + 1:] if l.startswith("*") or fn == "loadorder.txt"] if idx else []
        after = [l for l in after if not l.startswith("#")]
        if fn == "plugins.txt":
            res["activeAfterOutput"] = [l.lstrip("*") for l in after]
        if idx and after and (fn == "loadorder.txt" and res["moved"] or fn == "plugins.txt"):
            lines = [l for l in lines if name(l) not in mine] + [l for l in lines if name(l) in mine]
            with open(path, "w", encoding="utf-8", newline=chr(13) + chr(10)) as f:
                f.write(chr(10).join(lines) + chr(10))
            res["moved"] = True
    log(step="output_last", **res)


def run():
    busy = db._exe_running(*db.MO2_EXES)
    log(step="prelaunch", launch=SPEC["launch"], profile=PROFILE, busy=busy, out=OUT, subjects=[s["name"] for s in SPEC["subjects"]])
    assert not busy, f"refusing: {busy} running"
    if SPEC.get("kind") == "arm":
        output_last()
    snapshot()
    log(step="select", res=db.select_profile(LAB, PROFILE))
    pre = db.popup_preflight(LAB, PROFILE)
    log(step="popup_preflight", ok=pre["ok"], missing=pre["missing"], warnings=pre["warnings"])
    assert pre["ok"], "profile not quiet"
    db.configure(port=8920, shot_dir=SHOTS, recording="n2b")
    db.video_mark = lambda label: None          # no recorder: native stills and telemetry are this check's evidence
    STATE["t_launch"] = time.time()
    mo2_pid = None
    try:
        WL.launch_outside_job(LNK, watch=None)
        mo2_pid = int(WL.RECORDS[-1]["created"]["pid"])
        log(step="launch", record=WL.RECORDS[-1])
        # Ready took 14 to 30 s in every launch that loaded on 2026-11-25; the five that never loaded (A4) each
        # spent the old 600 s here. 240 s is still eight times the slowest good start.
        h = db.wait_ready(limit=float(SPEC.get("ready_limit", 240)))
        log(step="ready", seconds=round(time.time() - STATE["t_launch"], 1), health=h)
        db.health_check(expect_exe="SkyrimSE")
        assert db.loaded() is None, "main menu expected"
        cell, secs = new_game(float(SPEC.get("load_limit", 240)))
        log(step="new_game", cell=cell, seconds=secs)
        if SPEC.get("probe"):
            lp = db.loaded_plugins() if cell else []
            log(step="probe_result", loaded=bool(cell), cell=cell, seconds=secs, plugins=len(lp))
            log(step="sequence_done")
            return
        if not cell:
            raise RuntimeError("coc never produced a loaded cell")
        ok, hours = db.unpause()
        log(step="unpause", ok=ok, hours=hours)
        log(step="getini", res=db.post("console", {"action": "exec", "command": 'getini "fDefaultWorldFOV:Display"', "capture": True}))
        log(step="protect", res=db.protect_body(log=None, note="npc2bench: the body is not the subject"))
        log(step="body", res=db.move_within_cell(*BODY))
        lp = db.loaded_plugins()
        log(step="loaded_plugins", n=len(lp), plugins=lp)
        missing = [p for p in SPEC["required_plugins"] if p.lower() not in [x.lower() for x in lp]]
        assert not missing, f"profile plugins not loaded: {missing}"
        pairs = {s["name"]: (int(s["form"], 16), s["plugin"]) for s in SPEC["subjects"]}
        pairs["@swap"] = (int(SPEC["swap_outfit"]["form"], 16), SPEC["swap_outfit"]["plugin"])
        BASE.update(db.runtime_formids(pairs))
        log(step="bases", bases=BASE)
        probes()
        ai_travel(True)
        hud(True)
        empty_studio("new")
        if SPEC.get("backdrop"):
            backdrop_setup()
        if SPEC.get("notice_test"):
            notice_test()
        if SPEC.get("t4"):                         # placement probe only: no subject is photographed
            for sub in SPEC["subjects"]:
                if BASE.get(sub["name"]):
                    t4_probe(sub)
            hud(False)
            log(step="sequence_done")
            return
        for i, sub in enumerate(SPEC["subjects"]):
            if sub["name"] not in BASE or not BASE[sub["name"]]:
                log(step="subject", name=sub["name"], state="default", launch=SPEC["launch"], UNASSESSED="base form did not resolve in this load order")
                continue
            log(step="begin", name=sub["name"], index=i, of=len(SPEC["subjects"]))
            process(sub, "new")
        if SPEC.get("reload"):
            reload_pass([n for n in SPEC["reload"] if BASE.get(n)])
        hud(False)
        log(step="sequence_done")
    except Exception as e:
        log(step="abort", error=repr(e), tb=traceback.format_exc())
    finally:
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
        log(step="done")


if __name__ == "__main__":
    run()
