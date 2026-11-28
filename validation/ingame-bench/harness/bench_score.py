"""NPC2 in-game pre-release check: the scorer. Offline; reads only what the driver logged and photographed.

  python bench_score.py refs                 index the reference launches, measure repeat noise and mod-vs-vanilla
                                             separation, write references.json (the qualification table)
  python bench_score.py arm <A?> [--out DIR] [--only A,B]
                                             score one arm against the references: results.json, report.md, sheets
                                             (--only: just those subjects, written beside the arm's stored results)
  python bench_score.py controls             the known-answer controls (see CONTROLS below)
  python bench_score.py summary              one table over every scored arm

Method, declared before any arm was judged (frozen in THRESHOLDS; digest printed in every report):

  Telemetry is the primary record. A subject whose NPC2 selection was ACCEPTED must carry the mod's head parts, by
  part name, as the same NPC does with the mod running alone (reference launch); a REJECTED selection must carry the
  vanilla NPC's. Wig and antler modes change that expectation in declared ways (expect_headparts()).

  Pictures are compared in pairs, never judged on who the NPC looks like. Each frame is cut into regions fixed by the
  camera geometry (the camera stands 40 units in front of the head bone at head height, so the head is in the same
  place in every frame). The SUBJECT is found in each frame by subtracting that frame's own vacant frame (the empty
  studio from the same camera pose, taken right after the subject is removed): a pixel belongs to the subject where
  any colour channel differs by more than MASK_T. A region distance is the mean absolute greyscale difference over
  the pixels that are subject in EITHER frame, after a 4x box downscale; background pixels are not compared at all.
  (Smoke of 2026-11-25: a camera that followed a head 1.3 units lower after a change of boots moved the wall behind
  it enough to read 11.8 in a region where nothing about the subject had changed.)
  For "is this the mod's appearance or the original's", the test frame's FACE distance to the mod
  reference and to the vanilla reference are compared: mod-like, vanilla-like, or neither.
  A subject only gets an image verdict on that question if its mod reference is itself clearly different from its
  vanilla reference (separation at least SEP_MIN times the repeat noise); otherwise the picture cannot tell and the
  cell rests on telemetry alone, and says so.

  "Hair kept through an outfit change" is the CROWN region of the subject's swap-state frame against a frame that
  shows the hair in question (swap_reading()): its own default-state frame in the same launch, or, when the default
  outfit includes base-game headgear that leaves with the outfit (a mask, a helmet: measured 2026-11-25, crown
  change 35 to 109 with the hair untouched), the mod reference's swap-state frame. Kept: at most SAME_MAX_NOISE x
  noise (measured for kept hair: 0.4 to 1.1). Lost: at least DIFF_MIN_NOISE x noise (measured: 12.9 to 109).
  Subjects in NO_PICTURE are never judged by picture. Nothing below the collar is in any frame.

  A frame pair is only compared when the head faced the same way in both (head yaw within POSE_YAW_TOL) and both
  have their vacant frame; otherwise the image part is UNASSESSED and the pair goes on the sheet for a reader.

Verdicts: PASS (telemetry and picture agree with the expectation), FAIL (either contradicts it), UNASSESSED (a
reading is missing, a guard refused it, or nothing available can decide), READ (a cell whose outcome is reported,
not predicted: contests, Recast Only on a race change, and the designed limits below).

  What NPC2 is designed to do, where it differs from "looks and reads exactly like the mod alone" (method v4,
  2026-11-26, after the first full run; each rule is a function below whose comment names the cells it was
  measured on, and each has a known-answer control K-*):
    - SkyPatcher, like Recast, draws the donor's face and leaves the NPC's own head-part list: the list is read, not
      judged; the picture decides.
    - An arm that delivers no outfit (Include Outfits off, or Recast Only) leaves the NPC in the vanilla outfit: the
      regions that outfit covers (neck; face and crown too when either frame wears base-game headgear) are not
      compared with the mod reference. A face that reads vanilla-like still fails.
    - "Antlers gone" has a measured threshold of its own (ANTLER_GONE_MIN, grey levels): removed antlers move the
      region above the hairline by 2.2 to 5.2, antlers left on by at most 0.8. Since 2026-11-27 (H2d) that region
      is the ANTLER ZONE (antler_zone): the silhouette the mod reference adds over the vanilla reference above and
      beside the head, so antlers low at the sides of the head (Beleval) are read where they are.
    - "Hair lost with the outfit" is only predicted where the wig really travels on a delivered outfit; the wig
      mode is the one NPC2 APPLIED to the NPC (a converted wig is read off the head-part list), and "lost" means
      the hair is gone: the crown silhouettes no longer overlap. The same hair in another colour is kept
      (whether that colour is right is the tint prediction's question: v5 below).
    - A wig forwarded into an outfit whose headgear claims the same slot shows one of the two (warned, not
      prevented, by the user's decision of 2026-11-26): READ where the headgear is the one that went.
    - A reading whose head bone stood off the studio mark is refused where it is loaded (H1c); over a launch's
      folders a usable reading facing forward is taken before a turned one, and either before a refused one.
    - A rejected NPC that takes its face from a patched template is expected to look like that template's mod,
      in its own outfit AND after the outfit change (v5, N11: under every back end; SkyPatcher and Recast reach
      such an NPC through an entry NPC2 writes for it since 2026-11-26).
    - Include As New gives the NPC a private copy of its race: race is compared by origin.
    - Include Outfits on a template-dressed NPC replaces the template's outfit by design (the user's decision of
      2026-11-26): READ.
    - A NO_PICTURE subject is a designed limit: READ, unless its telemetry fails.

  Method v5 (2026-11-26, after NPC2's new wig tint and the N11 fix):
    - A wig converted to head parts is PREDICTED tinted or untinted (wig_tint): tint mode Always tints, Never does
      not, Auto tints only when RaceMenu was in the profile NPC2 built against with its hair-slot tint on; the
      NPC's own entry (NpcWigHairTintModes) beats the mod's mode, which beats the default. No arm as defined loads
      RaceMenu, so under Auto a converted wig is untinted.
        untinted: the wig must match the mod reference in colour. The CROWN of a converted wig is now compared
                  with the mod reference in the default state (v4 left it out under every wig mode), and a wig
                  that is there in another colour after the outfit change fails.
        tinted:   the answer is the mod alone WITH RaceMenu, which tints the worn wig the same way (measured on
                  the Briarheart after the outfit change and on Auri: a tinted converted wig reads 2.3 to 3.3
                  against it, an untinted one 11.8 to 34.9). Where a reading of
                  the subject in a tagged RaceMenu launch of its reference exists (racemenu_ref_rows), the cell is
                  compared with that reading in full. Where none exists the face and crown are not compared by
                  distance and a colour difference after the outfit change is accepted: the tint is then NOT
                  verified, and the cell says so.
    - A build whose static form-version scan has a hit (D1, fixed by N1) fails every cell of its arm (H6).
    - The manifest and build state of a launch are read from the launch folder first (launch_build).

  N2B_BUILD_ROOT (environment, optional): read the arms' manifest.json and build.json from this folder instead of
  <run>\\build, for scoring stored launches against the build they were made from after an arm has been rebuilt.
  A launch folder that carries its own build state (<launch>\\build, kept by bench_run since 2026-11-26, or a
  hand-kept copy under <run>\\traces\\launch-build\\<launch folder name>) is scored against that instead."""
import glob, hashlib, json, math, os, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bench_def as B

LIVE = os.path.join(B.RUN_ROOT, "live")
SCORES = os.path.join(B.RUN_ROOT, "scores")
REFS_JSON = os.path.join(SCORES, "references.json")
# 2026-11-26: the arm builds under <run>\build are rebuilt with the fixed NPC2 while the first run's launches are
# still being rescored cell by cell. A stored launch must be scored against the manifest of the build it was made
# from (who was rejected, who was left out), so the folder can be named from outside. Default unchanged.
BUILD = os.environ.get("N2B_BUILD_ROOT") or os.path.join(B.RUN_ROOT, "build")
FIRST_RUN = os.path.join(B.RUN_ROOT, "first-run-snapshot-2026-11-25")     # builds and scores of the first full run


def build_file(arm, name, root=None):
    return os.path.join(root or BUILD, arm, name)


# Hand-kept build state for launches made before bench_run kept it with the launch folder (2026-11-26): one folder
# per launch folder name, holding build.json (and manifest.json where it was saved) and a SOURCE.txt that says
# where the copy comes from. <run>\live is evidence and is not written to.
SIDE_BUILD = os.path.join(B.RUN_ROOT, "traces", "launch-build")


def launch_build(arm, out=None, build_root=None):
    """{"manifest": path, "build": path}: the manifest and the build state of the build a launch was made from.
    <run>\\build\\<arm> holds only the arm's LAST build, and a diagnostic build (N2B_SETTINGS_OVERRIDES,
    N2B_EXTRA_MODS) differs from the arm as defined, so for one launch folder each file is taken from the first
    place that has it: the launch folder's own copy (<out>\\build, written by bench_run.launch_spec), a hand-kept
    copy (SIDE_BUILD), then the arm's build folder (`build_root`, N2B_BUILD_ROOT, <run>\\build), as before."""
    found = {}
    for key, name in (("manifest", "manifest.json"), ("build", "build.json")):
        places = ([os.path.join(out, "build", name), os.path.join(SIDE_BUILD, os.path.basename(out), name)] if out else []) + [build_file(arm, name, build_root)]
        found[key] = next((p for p in places if os.path.exists(p)), places[-1])
    return found

# Regions as fractions of the frame (x0, y0, x1, y1). Geometry: the frame is 37.7 units tall and 67.1 wide at the
# subject; the head bone sits 1 unit below the centre row.
REGIONS = {
    "face": (0.41, 0.36, 0.59, 0.70),
    "crown": (0.34, 0.10, 0.66, 0.38),
    "above": (0.26, 0.00, 0.74, 0.22),
    "neck": (0.42, 0.72, 0.58, 0.98),
    "subject": (0.26, 0.00, 0.74, 1.00),
}
# 2026-11-27 (H2d): where "antlers gone" looks. The band is the subject column above the face region (the top of the
# head and beside it, down to the brow); within it the check reads only the ANTLER ZONE, the pixels the mod
# reference's subject covers and the vanilla reference's does not (antler_zone). Not one of REGIONS: it is read by
# that check alone and is not a fixed rectangle.
ANTLER_BAND = (REGIONS["subject"][0], 0.00, REGIONS["subject"][2], REGIONS["face"][1])
THRESHOLDS = {
    "SEP_MIN": 3.0,          # mod-vs-vanilla face separation must be at least this many times the repeat noise
    "RATIO_MOD": 0.35,       # d_mod / (d_mod + d_van) at or below this: the mod's face
    "RATIO_VAN": 0.65,       # at or above this: the vanilla face
    "SAME_MAX_NOISE": 3.0,   # "same as reference" (crown, neck, above): distance at most this many times repeat noise
    "DIFF_MIN_NOISE": 6.0,   # "clearly different": at least this many times repeat noise
    "NOISE_FLOOR": 1.5,      # grey levels; used until `refs` has measured repeats (then the measured value replaces it)
    "POSE_YAW_TOL": 3.0,
    "MASK_T": 14.0,          # a pixel is subject where any channel differs from the vacant frame by more than this
    "MASK_MIN": 0.005,       # subject pixels (either frame) below this fraction of a region: nothing there, distance 0
    # H2i (2026-11-26): "hair lost" means the hair is GONE, read from the crown SILHOUETTE (overlap of the two subject
    # masks, compare()["iou"]), not from grey levels alone. Measured on the 23 C5 cases and the first run's 115 swap
    # readings (138 in all). Clear grey difference (at least 9.0), 30 cases: 26 with the hair gone or another
    # hairstyle overlap 0.469 to 0.765 (the highest: the cultist's High Poly wig against his vanilla hair), 4 with
    # the same hair in another colour (a tinted converted wig) 0.833 to 0.859, none between. Same on grey levels
    # (at most 4.5), 108 cases: overlap 0.812 to 0.995, so those are decided on grey levels first.
    "SIL_KEPT_MIN": 0.82,    # crown silhouettes overlapping at least this much: the same hair shape, whatever its colour
    "SIL_LOST_MAX": 0.78,    # at most this much (with a clear grey difference): the hair is gone, or another shape
    "MARK_TOL": 60.0,        # H1c: a head bone further than this from the studio mark in X or Y: the reading is refused
    # 2026-11-26: "antlers gone" has a threshold of its own, in grey levels (not a multiple of the noise). The check
    # used SAME_MAX_NOISE x noise (4.5), which was never measured for it and sat INSIDE the range of removed antlers.
    # Distance of `above` to the mod reference in the stored first run and the launches of 2026-11-26:
    #   antlers NOT removed (any antler subject in an arm that keeps them, and Dervenin and Beleval where Remove
    #   was asked but their antlers are not detected): 0.048 to 0.799 (Anoriath 0.084 to 0.355, Elrindir 0.135 to
    #   0.432, Beleval 0.048 to 0.369, Auri 0.693 to 0.763, Dervenin 0.393 to 0.799, the largest, in A5)
    #   antlers removed: 2.172 to 5.237 (Auri 2.172 in A7, 2.344 in A2, 3.654 in A4-N2, 3.751 in A12; Anoriath
    #   3.303 to 3.352 in A2, A4, A6; Elrindir 5.212 to 5.237 in A2, A4, A6), and 16.0 for Auri in A6 (tint as well)
    # 1.5 lies between: 1.9 times the largest unremoved reading and 0.69 of the smallest removed one.
    # 2026-11-27 (H2d): the region above was `above` (the top 22% of the frame), which missed Beleval's two horns:
    # they sit at the sides of her mohawk at 0.25 to 0.33 of the frame height, so her removed horns read 0.703 to
    # 0.857 there. The check now reads the ANTLER ZONE (antler_zone, ANTLER_BAND), the same threshold kept. Mean
    # distance to the mod reference over the zone, the same 48 first-run and 2026-11-26 readings plus the three
    # H2d launches (RUN\traces\scorer-antler-region\cases.txt):
    #   antlers on (36): 0.052 to 0.943 (Auri in A8 the largest, her wig is in her zone; Dervenin 0.30 to 0.45,
    #   Beleval 0.05 to 0.35, Anoriath 0.11 to 0.37, Elrindir 0.14 to 0.41)
    #   antlers removed (17): 2.234 to 43.06 (Beleval 2.23 to 2.38 and Dervenin 43.0 in the H2d launches; Auri 4.07
    #   to 5.30, and 20.6 in A6 with the tint; Anoriath 10.4; Elrindir 21.4)
    # 1.5 still lies between: 1.59 times the largest reading with the antlers on, 0.67 of the smallest removed one.
    # The band's lower edge (the face region's top) was chosen on these same readings (0.30 separated better, 0.38
    # worse): there is no independent sample yet.
    "ANTLER_GONE_MIN": 1.5,
}
MARK = (-2400.0, 1600.0)     # the studio mark (X, Y); on it a head bone reads about (-2400.0, 1598.4)


NO_PICTURE = {"ArnielShade": "ghost: translucent and animated, its own frames differ by 13 to 18 with nothing changed"}
BASE_PLUGINS = {"skyrim.esm", "update.esm", "dawnguard.esm", "hearthfires.esm", "dragonborn.esm",
                "unofficial skyrim special edition patch.esp"}
HEAD_SLOTS = (30, 31, 41, 42, 43)


def digest():
    # v3 (2026-11-25): a subject whose mod carries SMP physics is read only from launches with Faster HDT-SMP loaded
    # v4 (2026-11-26): expectations follow what NPC2 is designed to do (H2a-c, H2e-h, H8, N5: see the module docstring)
    # v5 (2026-11-26): the wig tint is predicted (new Auto, RaceMenu, per-NPC entry), a converted wig's crown is
    #                  compared, an inheriting NPC's swap is predicted (N11), a D1 hit fails the arm (H6)
    # v5, 2026-11-27: "antlers gone" reads the antler zone (antler_zone, ANTLER_BAND) instead of `above` (H2d)
    return hashlib.sha256(json.dumps({"r": REGIONS, "t": THRESHOLDS, "n": sorted(NO_PICTURE), "smp": sorted(B.NEEDS_SMP), "v": 5,
                                      "antlerZone": {"band": ANTLER_BAND, "zone": "mod reference subject and not vanilla reference subject"}},
                                     sort_keys=True).encode()).hexdigest()[:12]


# ------------------------------------------------------------------ reading launches
def outs_of(launch):
    """Every completed output folder of a launch that is not a tagged side run, oldest first."""
    p = os.path.join(LIVE, f"{launch}.latest")
    if os.path.exists(p):
        txt = open(p).read().strip()
        return json.loads(txt) if txt.startswith("[") else [txt]
    return []


NO_SMP = "read without Faster HDT-SMP in a launch that loads this subject's physics mesh: not a reading of the subject"


def has_smp(out):
    """Was Faster HDT-SMP loaded in this launch? launch.json says so ("smp") since 2026-11-25 15:15 PST. Before that
    only the launches tagged "smp" had it (bench_run.smp_batch). Its plugin is no evidence (FSMPM's .esp is dropped
    for a missing master while the DLL loads), nor is a copied hdtSMP64.log (the SKSE log folder keeps old logs)."""
    try:
        spec = json.load(open(os.path.join(out, "launch.json"), encoding="utf-8"))
    except Exception:
        spec = {}
    return bool(spec.get("smp")) or "-smp-" in os.path.basename(out)


def rows_of(out):
    rows = {}
    p = os.path.join(out, "log.jsonl")
    if not os.path.exists(p):
        return rows
    launch = os.path.basename(out).split("-")[0]
    no_smp = B.launch_has_smp(launch) and not has_smp(out)
    for line in open(p, encoding="utf-8"):
        try:
            r = json.loads(line)
        except Exception:
            continue
        if r.get("step") == "subject":
            if no_smp and r.get("name") in B.S and B.needs_smp(r["name"]) and not r.get("UNASSESSED"):
                r["UNASSESSED"] = NO_SMP
            if not r.get("UNASSESSED") and off_mark(r):
                r["UNASSESSED"] = off_mark(r)
            key = (r["name"], r.get("state", "default"))
            if key not in rows or (rows[key].get("UNASSESSED") and not r.get("UNASSESSED")):
                rows[key] = r
        elif r.get("step") == "removed" and (r.get("vacant") or r.get("vacants")):
            v = dict(r.get("vacants") or {})
            if not v and r.get("vacant"):
                v["default-face"] = r["vacant"]          # launches before 2026-11-25 06:30: one vacant frame per subject
            rows.setdefault(("@vacant", r["name"]), {"vacants": {}})["vacants"].update(v)
    for (name, state), r in rows.items():
        if name != "@vacant":
            vac = (rows.get(("@vacant", name)) or {}).get("vacants") or {}
            r["vacants"] = {view: vac.get(f"{state}-{view}") for view in ("face", "face34")}
    return rows


def off_mark(r):
    """H1c (2026-11-26). A reading is only usable if the DRAWN subject stood on the studio mark. Returns the refusal
    (with the measured numbers) for a reading whose head bone is more than MARK_TOL from the mark in X or Y, else
    None; a reading with no pose recorded is not refused (as before). Applied where readings are loaded, references
    and arms alike, so that a later usable reading replaces a refused one (merge_rows).
    Why: the first run's driver judged "in the studio" by the position the game stores, which its own fallback had
    moved while the drawn body stayed behind. Measured over the 611 stored readings: Vilja's head bone stood +165
    to +205 in X and -34 to -205 in Y in all 11 launches (V, R1, R4, A1-A8), Adelaisa's (+107, -288) in R3 and
    (+163, -203) in A2; every other reading is within 0.0 in X and 1.7 in Y."""
    head = (r.get("pose") or {}).get("head")
    if not head:
        return None
    dx, dy = head[0] - MARK[0], head[1] - MARK[1]
    if abs(dx) <= THRESHOLDS["MARK_TOL"] and abs(dy) <= THRESHOLDS["MARK_TOL"]:
        return None
    return (f"off the studio mark: the head bone stood at ({head[0]:.1f}, {head[1]:.1f}), {dx:+.1f} in X and {dy:+.1f} in Y from the mark "
            f"({MARK[0]}, {MARK[1]}); more than {THRESHOLDS['MARK_TOL']:.0f} is not a reading of the subject in the studio")


def turned(r):
    """Degrees the head is turned from facing forward, when more than POSE_YAW_TOL; else 0. None: no pose recorded."""
    yaw = ((r or {}).get("pose") or {}).get("yaw")
    if yaw is None:
        return None
    yaw = (yaw + 180) % 360 - 180
    return yaw if abs(yaw) > THRESHOLDS["POSE_YAW_TOL"] else 0


def reading_rank(r):
    """H1c. How good a reading is when several output folders of one launch hold the same subject and state
    (lower is better): 0 usable and facing forward, 1 usable but turned, 2 UNASSESSED. A reading with no pose
    recorded ranks 0, as it did before. Yaw is NOT a ground for refusing a reading: Urag (-178.3 deg), Elrindir
    (90.0), Eorlund (32.8), Hadvar (-25.7), Lydia (-46.8) and Delphine (136.5) stand turned by the same angle in
    every launch of the first run and compare cleanly pair by pair (same_pose guards the pair). The rank only lets
    a reading RECAPTURED facing forward (the driver restrains the actor since 2026-11-26) replace a turned one."""
    return 2 if r.get("UNASSESSED") else 1 if turned(r) else 0


def merge_rows(outs):
    """One reading per (name, state) over a launch's output folders: the best-ranked (reading_rank), the earliest
    folder on a tie. Until 2026-11-26 the rule was "the first, unless it is UNASSESSED and a later one is not",
    which is this rule with ranks 0 and 1 merged. A reading that replaces an earlier, worse one says so."""
    rows = {}
    for o in outs:
        for k, v in rows_of(o).items():
            if k not in rows:
                rows[k] = v
            elif k[0] != "@vacant" and reading_rank(v) < reading_rank(rows[k]):
                rows[k] = {**v, "supersedes": rows[k].get("UNASSESSED") or f"a reading turned {turned(rows[k])} deg"}
    return rows


def launch_rows(launch, out=None):
    return merge_rows([out] if out else outs_of(launch))


# ------------------------------------------------------------------ pictures
_IMG = {}


def rgb(path):
    import numpy as np
    from PIL import Image
    if path not in _IMG:
        im = Image.open(path).convert("RGB")
        im = im.resize((im.width // 4, im.height // 4), Image.BOX)
        _IMG[path] = np.asarray(im, dtype=np.float32)
    return _IMG[path]


def crop(a, region):
    h, w = a.shape[:2]
    x0, y0, x1, y1 = REGIONS[region]
    return a[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)]


def usable(row, view="face"):
    f, v = frame(row, view), ((row or {}).get("vacants") or {}).get(view)
    return bool(f and v and os.path.exists(f) and os.path.exists(v))


def subject(row, view, region):
    """(greyscale crop, subject mask crop) of one frame: the subject is where the frame differs from its vacant frame."""
    f, v = rgb(frame(row, view)), rgb(row["vacants"][view])
    if f.shape != v.shape:
        return None, None
    mask = abs(f - v).max(axis=2) > THRESHOLDS["MASK_T"]
    return crop(f.mean(axis=2), region), crop(mask, region)


def compare(r1, r2, region, view="face"):
    """{"d": mean |difference| over pixels that are subject in either frame, "iou": overlap of the two silhouettes,
    "cover": fraction of the region that is subject in either}. None when a frame or its vacant frame is missing."""
    if not usable(r1, view) or not usable(r2, view):
        return None
    g1, m1 = subject(r1, view, region)
    g2, m2 = subject(r2, view, region)
    if g1 is None or g2 is None or g1.shape != g2.shape:
        return None
    union = m1 | m2
    n = int(union.sum())
    cover = n / union.size
    if cover < THRESHOLDS["MASK_MIN"]:
        return {"d": 0.0, "iou": 1.0, "cover": round(cover, 4)}
    return {"d": float(abs(g1 - g2)[union].mean()), "iou": round(float((m1 & m2).sum()) / n, 3), "cover": round(cover, 4)}


def dist(r1, r2, region, view="face"):
    c = compare(r1, r2, region, view)
    return None if c is None else c["d"]


def same_pose(r1, r2, view="face"):
    """Both rows can be compared: the head faced the same way in both and each has its vacant frame. The camera is
    placed from the head bone, so the head sits at the same place in both frames whatever the NPC's height."""
    p1, p2 = (r1 or {}).get("pose") or {}, (r2 or {}).get("pose") or {}
    if p1.get("yaw") is None or p2.get("yaw") is None:
        return False, "pose not recorded"
    if not usable(r1, view) or not usable(r2, view):
        return False, "a frame or its vacant frame is missing"
    dyaw = abs((p1["yaw"] - p2["yaw"] + 180) % 360 - 180)
    return dyaw <= THRESHOLDS["POSE_YAW_TOL"], f"head yaw differs {dyaw:.1f} deg"


def pose_reason(row, ref, why):
    """Why a pair was refused on yaw, in words that say which of the two is the odd one (H1c). A reference that
    replaced an earlier turned reading of its launch was recaptured after the driver fix of 2026-11-26; a first-run
    reading of the same subject then stands turned against it and waits for its arm to be re-run."""
    t, r = turned(row), turned(ref)
    if t and r == 0:
        if (ref or {}).get("supersedes"):
            return f"reference recaptured facing forward; this reading predates the driver fix (it stands turned {t:.1f} deg)"
        return f"{why}: this reading stands turned {t:.1f} deg, the reference faces forward"
    if r and t == 0:
        return f"{why}: the reference stands turned {r:.1f} deg, this reading faces forward"
    return why


def frame(row, view="face"):
    return ((row or {}).get("frames") or {}).get(view)


# ------------------------------------------------------------------ telemetry
def eff_base(row):
    t = (row or {}).get("tele") or {}
    return t.get("leveledBase") or t.get("base") or {}


def part_names(row, types=None):
    out = set()
    for hp in eff_base(row).get("headParts") or []:
        n = hp.get("partName") or hp.get("editorId") or hp.get("name")
        if n and (types is None or hp.get("type") in types):
            out.add(n)
    return out


def form_name(d):
    d = d or {}
    return d.get("editorId") or d.get("name") or d.get("formId")


def skin_models(row):
    return sorted({(a.get("model") or "").lower() for a in ((eff_base(row).get("skin") or {}).get("addons") or []) if a.get("model")})


def worn_models(row):
    out = set()
    for w in ((row or {}).get("tele") or {}).get("worn") or []:
        for a in w.get("addons") or []:
            if a.get("model"):
                out.add(a["model"].lower())
    return sorted(out)


def race_name(row):
    return form_name(((row or {}).get("tele") or {}).get("actorRace"))


# ------------------------------------------------------------------ references
def ref_launch_for(name, mod_key):
    """Which reference launch holds this subject under this mod key."""
    for ln, ref in B.REFS.items():
        if ln != "V" and mod_key in ref["mods"] and name in ref["subjects"]:
            return ln
    return None


def build_refs():
    os.makedirs(SCORES, exist_ok=True)
    table = {"digest": digest(), "built": time.strftime("%Y-%m-%d %H:%M:%S"), "launches": {}, "subjects": {}}
    rows = {ln: launch_rows(ln) for ln in B.REFS}
    for ln in B.REFS:
        table["launches"][ln] = {"outs": outs_of(ln), "rows": len([k for k in rows[ln] if k[0] != "@vacant"])}
    # repeat noise: any reference launch re-run with a tag "repeat" (bench_run go R1 --only ... --tag repeat)
    noise = []
    for d in sorted(glob.glob(os.path.join(LIVE, "*-repeat-*"))):
        ln = os.path.basename(d).split("-")[0]
        for (name, state), r in rows_of(d).items():
            if name == "@vacant" or (name, state) not in rows.get(ln, {}):
                continue
            base = rows[ln][(name, state)]
            if r.get("UNASSESSED") or base.get("UNASSESSED"):
                continue
            ok, _ = same_pose(base, r)
            if ok:
                for view in ("face", "face34"):
                    dd = {reg: dist(base, r, reg, view) for reg in REGIONS}
                    if dd["face"] is not None:
                        noise.append({"launch": ln, "name": name, "state": state, "view": view, **dd})
    table["repeats"] = noise
    for reg in REGIONS:
        vals = sorted(n[reg] for n in noise if n[reg] is not None)
        table.setdefault("noise", {})[reg] = round(max(vals[int(len(vals) * 0.9)] if vals else 0.0, THRESHOLDS["NOISE_FLOOR"]), 3) if vals else THRESHOLDS["NOISE_FLOOR"]
    table["noiseMeasured"] = bool(noise)
    for name in B.S:
        mk = B.S[name][2]
        keys = [mk] if mk else []
        if name == "Vilja":
            keys = ["ViljaRe", "ViljaSOGS"]
        if name == "Aela":
            keys = ["BW", "SSSP"]
        entry = {"vanilla": {"launch": "V", "present": ("V" in rows and (name, "default") in rows["V"])}, "mods": {}}
        van = rows["V"].get((name, "default"))
        for k in keys:
            ln = ref_launch_for(name, k)
            mrow = rows.get(ln, {}).get((name, "default")) if ln else None
            e = {"launch": ln, "present": bool(mrow), "unassessed": (mrow or {}).get("UNASSESSED")}
            if van and van.get("UNASSESSED"):
                e["vanillaUnassessed"] = van["UNASSESSED"]
            # a refused reading (off the mark, no physics) measures nothing: no separation, no qualification (H1c)
            if mrow and van and frame(mrow) and frame(van) and not mrow.get("UNASSESSED") and not van.get("UNASSESSED"):
                ok, why = same_pose(mrow, van)
                e["poseVsVanilla"] = "same" if ok else why
                e["sep"] = {reg: (round(dist(mrow, van, reg), 3) if ok else None) for reg in REGIONS}
                e["headPartsDifferFromVanilla"] = part_names(mrow) != part_names(van)
                sep, nz = e["sep"]["face"], table["noise"]["face"]
                e["qualifies"] = bool(ok and sep is not None and sep >= THRESHOLDS["SEP_MIN"] * nz and name not in NO_PICTURE)
            entry["mods"][k] = e
        table["subjects"][name] = entry
    json.dump(table, open(REFS_JSON, "w"), indent=1)
    return table


def load_refs():
    return json.load(open(REFS_JSON)) if os.path.exists(REFS_JSON) else build_refs()


# ------------------------------------------------------------------ expectations
def antler_names(facts):
    names = set()
    for hp in (facts or {}).get("headParts") or []:
        if hp.get("antler") and hp.get("editorId"):
            names.add(hp["editorId"])
    return names


def expectation(arm, name, manifest):
    """What this cell should show. Returned as plain facts the report prints next to the reading."""
    a = B.ARMS[arm]
    f, p, mk, tg = B.S[name]
    target = B.fk(name)
    rejected = next((s for s in manifest.get("invalidSelections", []) if target.lower() in s.lower() or f"[{target}]" in s), None)
    e = {"selected": mk is not None, "rejected": rejected, "modes": B.effective_modes(arm, name) if mk else None,
         "backend": a["backend"], "recast": a["recast"] if a["backend"] == "Recast" else None, "kind": "mod", "notes": []}
    sm = B.subject_mod(arm, name)
    e["mod"] = sm[0] if sm else None
    e["refKey"] = (a["vilja"] if name == "Vilja" else mk)
    donor = next((t[6:] for t in tg.split() if t.startswith("guest:")), None)
    e["refSubject"] = donor or name
    if name == "Thorgar":
        # Mod A's armature edit takes Nords off the helmet. Only Include writes that edit onto the shared record.
        # H5/N7 (2026-11-27): the fixture mod is staged with IncludeAllOverrides (bench_def.stage_settings). Read from
        # NPC2 (Patcher.cs 1462-1643 and 1937-2011): Include collects every non-NPC override of Mod A's plugin
        # (RecordHandler.GetAllOverriddenDependencyRecords) and writes it as an override of the SAME record, so the
        # shared armature 000800:NPC2Bench_ArmaRace_Base.esp leaves Nord for every NPC, Thorgar too: helmet gone.
        # Include As New (DuplicateAllOverrideRecordsAsNew, RecordHandler.cs 1304-1370) writes NEW records only and
        # leaves the shared armature as Base has it; Ignore writes no override: helmet shows (vanilla).
        e["kind"] = "mod" if a["overrides"] == "Include" else "vanilla"
        e["refKey"], e["refSubject"] = "ModA", "Thorgar"
        e["notes"].append(f"observer: helmet {'gone (as with Mod A active)' if a['overrides'] == 'Include' else 'shows (as in vanilla)'} under {a['overrides']}")
        return e
    if rejected:
        e["kind"] = "vanilla"
        e["notes"].append("screening rejected the selection: the NPC must be unchanged")
        return e
    if name == "Christer":
        if a["backend"] == "Recast" and a["recast"] == "RecastOnly":
            e["kind"] = "read"; e["notes"].append("Recast Only cannot change race: read, not predicted")
        elif a["overrides"] == "Ignore":
            e["kind"] = "helmet_gone"; e["notes"].append("Ignore: the race arrives, the armature edit does not, so the helmet must NOT render (the control that proves the check sees this failure)")
        elif a["overrides"] == "IncludeAsNew":
            # H5/N7 (2026-11-27): with IncludeAllOverrides the armature edit is duplicated as a NEW record
            # (DuplicateAllOverrideRecordsAsNew), and only the NPC record's own links are remapped to it. Christer
            # reaches the armature through Base's outfit and armor, which Mod A does not override; whether the
            # outfit-root bridge (Patcher.cs 1693-1781, Create and Patch or surrogates only) repoints that chain at
            # the copy depends on Include Outfits, the back end and the depth walk. Not derived with confidence.
            e["kind"] = "read"; e["notes"].append("Include As New with include-all-overrides: whether Christer's outfit chain reaches the private copy of the armature is not derived; read, not predicted")
        else:
            e["notes"].append(f"{a['overrides']} with include-all-overrides: the armature edit is written onto the shared record, the helmet renders")
    if a["order"] == "Contested" and name in ("Auri", "Aela"):
        e["kind"] = "read"; e["notes"].append("contested by a third-party distributor: read, not predicted")
    if a["backend"] == "Recast" and a["recast"] == "RecastOnly" and name in ("Lydia", "Vilja"):
        e["kind"] = "read"; e["notes"].append("custom race under Recast Only: whatever screening decides; read")
    return e


def effective_wig_mode(arm, name, row):
    """H2j (2026-11-26). The wig mode NPC2 actually applied to THIS NPC. The arm asks one mode per mod; NPC2 turns
    ForwardToOutfit into ConvertToHeadParts for an NPC whose outfit field the game never reads (dressed by a
    template with no SkyPatcher ini, or any NPC under Recast Only: Settings.GetEffectiveWigModeForNpc; the build log
    says "converting the wig to head parts instead"). The scorer sees it in the game without the log: the reading
    carries an NPC2Wig_* Hair head part. Measured: A2 Cultist (ESP, template-dressed; predicted "lost", read kept
    0.485), A7 Urag, Rikke, Auri, RikkeTwin and A8 ArnielGane (Recast Only)."""
    m = B.effective_modes(arm, name)["wig"]
    if m == "ForwardToOutfit" and any(n.lower().startswith("npc2wig") for n in part_names(row, {"Hair"})):
        return "ConvertToHeadParts"
    return m


def hair_kept_expected(arm, name, row=None):
    """Does the subject's wig survive a runtime change to an unrelated outfit? (proposal: wig and antler mode table)
    `row`: the subject's default-state reading, for the mode NPC2 applied to this NPC (effective_wig_mode)."""
    m = B.effective_modes(arm, name)
    mk = B.S[name][2]
    if "wig" not in B.tags(name):
        return None
    wig = effective_wig_mode(arm, name, row) if row else m["wig"]
    if wig == "None":
        return mk == "HPNO"            # a skin-carried wig stays, an outfit-carried wig goes
    return wig in ("ForwardToSkin", "ConvertToHeadParts")


def headgear(row):
    """Base-game pieces worn on the head slots (a mask, a helmet, a hood): they leave with the outfit and take the
    crown region with them, whatever happens to the hair. A mod's wig or antlers are not base-game forms."""
    return [w.get("name") or w.get("formId") for w in ((row or {}).get("tele") or {}).get("worn") or []
            if w.get("firstSlot") in HEAD_SLOTS and (w.get("plugin") or "").lower() in BASE_PLUGINS]


def swap_reading(name, row, srow, ref_srow, ref_keeps, nz, prefer_ref=False, ref_label="the mod reference"):
    """What the crown says about the hair after the outfit change. Returns {"hair": "kept" | "lost" | None, ...}.
    ref_keeps: True when the mod reference's swap frame shows the wig (a wig the mod itself carries on the skin).
    prefer_ref (v5, N11): compare with the reference's swap frame even when the default outfit has no headgear;
    for an NPC that inherits its look the question is "as the mod alone shows it after the same change", and its
    own default frame cannot answer that (bald before and bald after reads "kept").
    ref_label: what `ref_srow` is (the mod reference, or the mod reference with RaceMenu for a tinted wig)."""
    T = THRESHOLDS
    r = {"headgear": headgear(row)}
    if name in NO_PICTURE:
        return {**r, "hair": None, "why": "no picture verdict: " + NO_PICTURE[name]}
    ref_ok = ref_srow is not None and ref_keeps is not None and not headgear(ref_srow)
    if not r["headgear"] and not (prefer_ref and ref_ok):
        other, r["against"], same_means = row, "its own default-state frame", "kept"
    elif ref_ok:
        other, r["against"], same_means = ref_srow, f"{ref_label} after the same outfit change", ("kept" if ref_keeps else "lost")
        r["againstRef"] = True
    else:
        return {**r, "hair": None, "why": f"default outfit includes {r['headgear']} and there is no reference frame to compare with"}
    ok, why = same_pose(other, srow)
    cc = compare(other, srow, "crown") if ok else None
    if not cc:
        return {**r, "hair": None, "why": "frames not comparable: " + why}
    r.update(crown=round(cc["d"], 3), overlap=cc["iou"], above=round(dist(other, srow, "above") or 0.0, 3))
    opposite = "lost" if same_means == "kept" else "kept"
    same, colour = hair_same(cc, nz)
    r["hair"] = None if same is None else same_means if same else opposite
    if colour:
        r["colour"] = colour
    if r["hair"] is None:
        r["why"] = f"crown change {r['crown']} with silhouette overlap {r['overlap']} decides neither way"
    return r


def hair_same(cc, nz):
    """H2i. Do two crown crops show the same hair? (True | False | None, "differs" when only its colour does).
      same on grey levels (at most SAME_MAX_NOISE x noise)                         -> the same hair
      otherwise, silhouettes overlap at least SIL_KEPT_MIN                         -> the same hair; its colour
                                                                                      differs if the grey distance
                                                                                      is at least DIFF_MIN_NOISE x noise
      silhouettes overlap at most SIL_LOST_MAX and the grey distance is that large -> not the same hair (gone)
      anything else                                                                -> undecided
    Before this rule a clear grey difference alone meant "lost". Measured: A2 and A6 Briarheart, wig converted to
    head parts and recoloured by the tint mode, read 11.09 and 11.02 against the black wig of the mod reference
    with the same shape (overlap 0.853, 0.859) and failed as "hair lost"; where his hair really is gone (A8, the
    Include As New baldness) the overlap is 0.471."""
    T = THRESHOLDS
    near, far = T["SAME_MAX_NOISE"] * nz["crown"], T["DIFF_MIN_NOISE"] * nz["crown"]
    if cc["d"] <= near:
        return True, None
    if cc["iou"] >= T["SIL_KEPT_MIN"]:
        return True, ("differs" if cc["d"] >= far else None)
    if cc["iou"] <= T["SIL_LOST_MAX"] and cc["d"] >= far:
        return False, None
    return None, None


# ------------------------------------------------------------------ wig tint (method v5, 2026-11-26)
# Until 2026-11-26 NPC2's tint mode Auto recoloured a converted wig unless its baked tint was white, and the scorer
# excused a colour difference under Auto and Always (colour_expected, wig_tinted: H2g, H2i). Auto now follows
# RaceMenu (the user's decision of 2026-11-26; NPC2 Models/WigHairTintMode.cs, BackEnd/RaceMenuDetector.cs), so the
# tint is a prediction with two outcomes and a reference for each. Measured on the launches of 2026-11-26 (arm A2,
# both wigs converted; crown after the outfit change for the Briarheart, face in the default state for Auri):
#                                                against the mod alone       against the mod alone with RaceMenu
#   untinted (Auto without RaceMenu, Never,      Briarheart 1.60 to 1.70     Briarheart 11.82 to 11.90
#             Auto with a per-NPC Never)         Auri 4.30 to 4.71           Auri 15.69 to 15.91
#   tinted (Auto with RaceMenu, Always,          Briarheart 11.12 to 11.25   Briarheart 2.34 to 2.39
#           the old Auto on High Poly's wig)     Auri 15.19 to 15.26         Auri 2.78 to 2.85
# ("same" is at most 4.5, "clearly different" at least 9.0). The mod alone with and without RaceMenu differ by
# 10.69 to 10.79 (Briarheart) and 14.95 (Auri); two RaceMenu launches of the Briarheart differ by 0.42.
# RaceMenu does not change every wig: Velehk reads 0.25 with against without it. So "tinted" cannot be demanded as
# "differs from the mod alone"; it is judged against the RaceMenu reading, and left unverified where there is none.
def racemenu_at_build(build_state):
    """What RaceMenu did in the profile NPC2 BUILT against, which is what tint mode Auto follows. From build.json:
    "raceMenu" (bench_run.build records it since 2026-11-26), else the mod folders named in "extraMods" read now,
    else none: bench_def's launch lists enable no RaceMenu (K-tint control). A converted wig is out of RaceMenu's
    reach in the game, so RaceMenu in the launch alone (the tint lab's "-rm" launches) changes nothing about it."""
    bs = build_state or {}
    if bs.get("raceMenu") is not None:
        return {**bs["raceMenu"], "from": "build.json: read from the build's profile when it was built"}
    extra = [m for m in (bs.get("extraMods") or "").split(";") if m]
    if not extra:
        return {"installed": False, "tintsHairSlots": False, "from": "no mod was added to the arm's profile for this build, and the arms as defined enable no RaceMenu"}
    return {**B.racemenu_state(extra), "from": f"the mod folders of extraMods {extra}, read at scoring time (the build did not record it)"}


def launch_racemenu(out):
    """What RaceMenu did in the profile a launch RAN with, from the profile the driver keeps in the launch folder
    (<out>\\profile-before\\modlist.txt: one "+<mod folder>" line per enabled mod, highest priority first). None when
    the launch folder holds no such list."""
    p = os.path.join(out, "profile-before", "modlist.txt")
    if not os.path.exists(p):
        return None
    mods = [l[1:].strip() for l in open(p, encoding="utf-8", errors="replace").read().splitlines() if l.startswith("+")]
    return B.racemenu_state(list(reversed(mods)))


_RM_REFS = {}


def racemenu_ref_outs(ln):
    """Launch folders that show reference `ln` (the mod alone) WITH RaceMenu tinting hair-slot wigs: tagged
    launches of that reference whose own kept profile says so. Nothing is listed by hand. 2026-11-26: R2
    (R2-racemenu-*, R2-tintlab-rm-*: Briarheart, Ulfric, Urag, Velehk) and R1 (R1-racemenu-*: Auri)."""
    outs = []
    for d in sorted(glob.glob(os.path.join(LIVE, f"{ln}-*"))):
        try:
            spec = json.load(open(os.path.join(d, "launch.json"), encoding="utf-8"))
        except Exception:
            continue
        st = launch_racemenu(d) if spec.get("kind") == "ref" and spec.get("launch") == ln else None
        if st and st["tintsHairSlots"]:
            outs.append(d)
    return outs


def racemenu_ref_rows(ln):
    """One reading per (name, state) over racemenu_ref_outs(ln), chosen as a launch's readings are (merge_rows)."""
    if ln not in _RM_REFS:
        _RM_REFS[ln] = merge_rows(racemenu_ref_outs(ln)) if ln else {}
    return _RM_REFS[ln]


def converted_wig_of(arm, name, e, row, rows):
    """The subject whose wig, converted to head parts, this NPC shows: itself (the mode NPC2 applied to it, set
    in e["modes"] by score_arm), or the patched template it takes its look from (inherited_look). None: no
    converted wig, so no tint question."""
    if e["kind"] != "mod":
        return None
    t = e.get("inherits")
    if t:
        trow = rows.get((t, "default"))
        mode = effective_wig_mode(arm, t, trow) if trow and not trow.get("UNASSESSED") else B.effective_modes(arm, t)["wig"]
        return t if mode == "ConvertToHeadParts" else None
    return name if (e["modes"] or {}).get("wig") == "ConvertToHeadParts" else None


def wig_tint(arm, wig_subject, build_state, rm):
    """Does NPC2 tint this subject's converted wig? {"mode", "from", "tinted", "why", "raceMenu"}: Always tints,
    Never does not, Auto tints when RaceMenu was in the build's profile with its hair-slot tint on and a mask
    that holds slot 31 or 41 (`rm`: racemenu_at_build). The mode is the build's (bench_def.tint_mode: the NPC's
    own entry, then the mod's mode, then the default, each as the build's settingsOverrides changed it).
    Limit: RaceMenu's slot test on the wig's own armor and addon is taken as passed. Both wig families of the
    corpus pass it (build log of 2026-11-26: Auri "armor slots 31, 38, addon slots 31, 41", Briarheart "armor
    slots 30-33, 37, addon slot 31"); a wig that claims neither hair slot would be predicted tinted wrongly."""
    mode, src = B.tint_mode(arm, wig_subject, (build_state or {}).get("settingsOverrides"))
    if mode == "Always":
        tinted, why = True, "tint mode On (Always)"
    elif mode == "Never":
        tinted, why = False, "tint mode Off (Never)"
    elif mode == "Auto":
        tinted = bool(rm.get("tintsHairSlots"))
        why = "tint mode Auto, and RaceMenu was " + ("in the build's profile with its hair-slot tint on" if tinted else
                                                     "not in the build's profile" if not rm.get("installed") else
                                                     "in the build's profile with its hair-slot tint off (or a mask without slot 31 or 41)")
    else:
        tinted, why = None, f"tint mode {mode!r} is not one the scorer knows"
    return {"mode": mode, "from": src, "tinted": tinted, "why": why, "of": wig_subject,
            "raceMenu": {k: rm.get(k) for k in ("installed", "mod", "tintEnabled", "slotMask", "tintsHairSlots", "from") if k in rm}}


def colour_verdict(tint, rd):
    """The swap cell where the hair is there but its colour differs from the reference frame (hair_same "differs").
    (verdict, reason):
      a converted wig predicted UNTINTED, against the mod reference          -> FAIL: it was recoloured
      predicted TINTED, against the mod reference WITH RaceMenu              -> FAIL: not the colour RaceMenu gives
      predicted TINTED, against the plain mod reference (no RaceMenu reading) -> PASS, tint not verified
      no converted wig, or no prediction                                     -> UNASSESSED, for a reader (as in v4)"""
    what = f"hair kept (silhouette overlap {rd['overlap']}) but its colour differs from {rd['against']} (crown {rd['crown']})"
    if not tint or tint["tinted"] is None or not rd.get("againstRef"):
        return "UNASSESSED", what + " and nothing predicts a tint here: for a reader"
    if not tint["tinted"]:
        return "FAIL", what + f": the converted wig is recoloured where none is expected ({tint['why']})"
    if rd.get("racemenuRef"):
        return "FAIL", what + f": a tinted wig is expected ({tint['why']}) and it does not have the colour RaceMenu gives the worn wig"
    return "PASS", what + f": expected, the converted wig is tinted ({tint['why']}); NOT verified, there is no reading of the mod alone with RaceMenu to compare with"


def antler_kept_expected(arm, name):
    """Do the subject's antlers survive the outfit change? None: it has none, or they are removed in this arm."""
    if "antler" not in B.tags(name):
        return None
    m = B.effective_modes(arm, name)["antler"]
    if m == "Remove":
        return None
    if m == "None":
        return B.S[name][2] != "Fox"       # FoxGlove carries the antlers on the outfit; the others as head parts
    return m == "ForwardToSkin"


# ------------------------------------------------------------------ what NPC2 is designed to do (method v4, 2026-11-26)
# The first full run (2026-11-25) left 56 cells failing where the scorer expected what NPC2 is not designed to do.
# Each rule below is one small function or constant, so a known-answer control (K-* in controls()) can show that it
# bites: put the old rule back and that control fails. scores\A1..A12 hold the verdicts from before these rules.

# H2a. Backends under which the NPC's OWN head-part list is read but not judged. Recast draws the face at 3D build.
# SkyPatcher's copyVisualStyle (npc.cpp 1583-1606, commit 48d1779) points the target's face NPC at the donor and calls
# ChangeHeadPart for each donor part: it never removes a part the donor lacks and keeps one part per type, so the
# actor base ends with a list that is neither the mod's nor the vanilla one while the drawn face is the donor's.
# Measured: 27 cells in A3, A4, A11 failed on the list alone with a picture that matched the mod (A3 Irileth: one
# scar part of two; A3 Auri: the vanilla antler part still listed). The picture still decides those cells.
OWN_LIST_NOT_JUDGED = ("Recast", "SkyPatcher")


def designed_limit(name):
    """H8. A NO_PICTURE subject (the ghost ArnielShade: translucent and animated, its own frames differ by 13 to 18
    with nothing changed) is a limit of the method, known before the launch, not a reading that went missing. Its
    cells are READ with this reason: never UNASSESSED or FAIL on picture grounds. Its records are still judged: a
    telemetry failure is a FAIL, and a missing reading or reference is still UNASSESSED.
    First run: her swap cell was UNASSESSED in A1, A2, A3, A6, A7 and her default cell PASS on records alone."""
    return ("designed limit, no picture verdict: " + NO_PICTURE[name]) if name in NO_PICTURE else None


def race_matches(arm, test_race, ref_race):
    """H2h. Race compared by ORIGIN. Under override mode Include As New NPC2 gives the NPC a private copy of its
    race, defined by the output plugin and named after the original plus "_" and a plugin file name
    (NordRace_Skyrim.esm, NordRace_SOGS.esm). The test race matches when it IS the reference race, or, in an
    Include As New arm, when it is defined by the arm's output (plugin N2B_*) and its editor ID is the reference's
    followed by "_". Returns (ok, note). Measured 2026-11-25: A2 Vilja (NordRace_SOGS.esm in N2B_A2.esp against
    NordRace) and every High Poly subject of A2, A5, A12 (NordRace_Skyrim.esm, BretonRace_Skyrim.esm,
    OrcRace_Skyrim.esm). A copy in an arm that is not Include As New, or a copy of another race, still fails."""
    t, r = test_race or {}, ref_race or {}
    tn, rn = form_name(t), form_name(r)
    if tn == rn:
        return True, None
    if (B.ARMS[arm]["overrides"] == "IncludeAsNew" and (t.get("plugin") or "").startswith("N2B_")
            and r.get("editorId") and (t.get("editorId") or "").startswith(r["editorId"] + "_")):
        return True, f"race {tn} is the output's private copy of {rn} (Include As New)"
    return False, None


def base_worn(row):
    """The base-game pieces the NPC wears, as (plugin, local id): the outfit as the game dressed it. A mod's wig or
    antlers and NPC2's minted wig armor are not base-game forms and are left out."""
    return sorted({((w.get("plugin") or "").lower(), w.get("localId") or w.get("formId")) for w in ((row or {}).get("tele") or {}).get("worn") or []
                   if (w.get("plugin") or "").lower() in BASE_PLUGINS})


def outfit_covers(row, mod):
    """Regions a difference of outfit reaches in a collarbone-up frame: the neck (the collar) always; the face and
    the crown as well when either frame wears base-game headgear (a hood shades the face and hides the hair)."""
    return ["neck"] + (["face", "crown"] if headgear(row) or headgear(mod) else [])


def delivers_no_outfit(arm):
    """Does this arm leave the NPC in the outfit the game already gives it? Include Outfits off; or Recast Only,
    which "has no way to deliver an outfit at all" (NPC2 Models/Settings.cs, OutfitFieldIsInert), whatever the
    Include Outfits setting says (A7 has it on and every subject still wears the vanilla outfit)."""
    a = B.ARMS[arm]
    return (not a["outfits"]) or (a["backend"] == "Recast" and a["recast"] == "RecastOnly")


def kept_vanilla_outfit(arm, row, mod, van):
    """H2b. The arm delivers no outfit and the readings show it: the test wears the vanilla reference's base-game
    pieces and the mod reference wears others. Then the regions the outfit covers are not compared with the mod
    reference (no other reference is invented). Returns {"covered": [...], "note": ...} or None.
    Measured 2026-11-25: Farkas A7 (Wolf Armor against the mod's Steel Armor: neck 38.8), Auri A7 and A8 (Hide
    Armor against Tavern Clothes: neck 23.2), Sybille and Danica in A4, A7, A8 (the vanilla hooded robes against the
    mod's hoodless ones: face 21.9 to 28.3, crown 29.9 to 40.7, neck 33.1 to 34.4, and a face ratio of 0.59 to 0.62,
    "neither", with the mod's face plainly under the hood). The same NPCs unpatched read a ratio near 1: that still
    fails (see the face-class rule in score_arm)."""
    if not row or not mod or not van or not delivers_no_outfit(arm):
        return None
    tw = base_worn(row)
    if tw != base_worn(van) or tw == base_worn(mod):
        return None
    covered = outfit_covers(row, mod)
    return {"covered": covered, "note": f"the arm delivers no outfit: the NPC wears the vanilla outfit, the mod reference another; "
                                        f"{', '.join(covered)} not compared with the mod reference"}


def writes_ini(arm):
    """Does this arm write a SkyPatcher ini? SkyPatcher output, and Recast Hybrid. Read from the arm's settings."""
    a = B.ARMS[arm]
    return a["backend"] == "SkyPatcher" or (a["backend"] == "Recast" and a["recast"] == "Hybrid")


def body_piece(row):
    """The piece(s) worn on the body slot (32), as (plugin, local id): which outfit this is."""
    return sorted(((w.get("plugin") or "").lower(), w.get("localId") or w.get("formId")) for w in ((row or {}).get("tele") or {}).get("worn") or []
                  if w.get("firstSlot") == 32)


def outfit_replaced_by_design(arm, row, mod, facts):
    """N5 (the user's decision of 2026-11-26: "Include Outfit is an opt-in feature. If a user wants the outfit, they
    want the outfit."). Where an ini is written and Include Outfits is on, the outfit named on the mod's NPC record
    is delivered by directive and REPLACES what the NPC's template dresses it in. The mod alone never shows that
    outfit (the game dresses the NPC from the template), so the mod reference is no answer for the picture: READ.
    Conditions, all from the arm's settings, the build manifest and the readings, none from an arm or NPC name:
    the arm writes an ini with Include Outfits on; the manifest says the mod's NPC is templated; and the body piece
    the NPC wears is not the mod reference's. Returns the reason or None.
    Measured: the Dragonborn cultist in A3 and A6 (and A5 once its floating heads are fixed): Black Mage Robes and
    no mask where the mod reference wears Cultist Robes and the mask; face 58.0, neck 38.3 against the reference.
    Not this rule: an NPC whose body piece is unchanged and who lost only headgear (RikkeTwin A3: see N6)."""
    a = B.ARMS[arm]
    if not (row and mod and writes_ini(arm) and a["outfits"] and (facts or {}).get("templated")):
        return None
    if not body_piece(row) or not body_piece(mod) or body_piece(row) == body_piece(mod):
        return None
    return "outfit replaced by design: Include Outfit on a template-dressed NPC"


def wig_headgear_conflict(modes, row, mod, van):
    """N6 (the user's decision of 2026-11-26: warn, do not prevent). The wig mode applied to this NPC is
    ForwardToOutfit, the mod or the vanilla reference reading wears base-game headgear that this reading no longer
    wears, and this reading wears the output's wig piece instead: the forwarded wig armor and the outfit's headgear
    claim the same slot and the game shows one of the two. The default cell is READ with the reason; where the
    headgear won (it is still worn) nothing differs from the reference and the cell is scored as usual.
    Measured: A3 RikkeTwin (report D5: wig 000898:N2B_A3.esp on slot 31, the Imperial Officer's Helmet of slots 31
    and 42 gone; face ratio 0.359, face 12.7, neck 12.1) and Briarheart in the A5 launch of 2026-11-26 (the wig
    shows, the Forsworn Headdress is dropped; face 36.96, neck 20.94). Returns the reason or None.
    The body piece must be the reference's: where the whole outfit was replaced the headgear went with it, which
    is N5's case or a failure, not this one (the A6 cultist matched this rule too until that was required)."""
    if (modes or {}).get("wig") != "ForwardToOutfit" or not output_headpieces(row):
        return None
    refs_with_gear = [r for r in (mod, van) if r and set(headgear(r)) - set(headgear(row))]
    if not refs_with_gear or not any(body_piece(row) and body_piece(row) == body_piece(r) for r in refs_with_gear):
        return None
    return "wig forwarded into an outfit with headgear on the same slot: one of the two shows (warned by NPC2, by the user's decision of 2026-11-26)"


def region_cover(row, region, view="face"):
    """Fraction of a region that is subject in ONE frame (its own mask, not the union of two). None without frames."""
    if not usable(row, view):
        return None
    _, m = subject(row, view, region)
    return None if m is None else float(m.mean())


def antlers_show(mod, van):
    """H2c. How much more of `above` the mod reference's subject covers than the vanilla reference's: RECORDED in
    the "antlers gone" check for a reader, no longer part of the verdict. Returns {"shows", "mod", "vanilla",
    "added"}; "shows" is added >= MASK_MIN.
    History, 2026-11-26. This was first a gate: the picture was only asked about the antlers when "shows" was true,
    because Anoriath's removed antlers moved `above` by 3.3 where the check wanted more than 4.5. The gate was
    removed the same day, for two reasons. (1) The check got a measured threshold of its own (ANTLER_GONE_MIN),
    under which Anoriath's 3.3 passes and the gate changes no stored verdict. (2) The gate hid a real failure:
    antlers LEFT ON such a subject under SkyPatcher or Recast (where the head-part list is not judged either) read
    about 0.3 and were waved through unasked. It also never did what it was meant to for Auri: the cover measures
    "the mod adds silhouette up there", not "the antlers are what it adds" (Anoriath -0.0086, Beleval +0.0212,
    Dervenin +0.0260, Auri +0.0482, which is her wig, Elrindir +0.0524; subjects without antlers reach +0.108 on
    hair alone; repeat launches differ by at most 0.0004)."""
    cm, cv = region_cover(mod, "above"), region_cover(van, "above")
    if cm is None or cv is None:
        return {"shows": True, "mod": cm, "vanilla": cv, "added": None}
    return {"shows": (cm - cv) >= THRESHOLDS["MASK_MIN"], "mod": round(cm, 4), "vanilla": round(cv, 4), "added": round(cm - cv, 4)}


def full_subject(row, view="face"):
    """(greyscale frame, subject mask) of one whole frame, as subject() cuts them per region. (None, None) on a
    size mismatch."""
    f, v = rgb(frame(row, view)), rgb(row["vacants"][view])
    if f.shape != v.shape:
        return None, None
    return f.mean(axis=2), abs(f - v).max(axis=2) > THRESHOLDS["MASK_T"]


def antler_zone(test, mod, van, view="face"):
    """H2d (2026-11-27). Where "antlers gone" looks, found from the two REFERENCE frames of the subject, not drawn:
    the ANTLER ZONE is every pixel of ANTLER_BAND (the subject column above the face region) that is subject in the
    mod reference and NOT subject in the vanilla reference, i.e. the silhouette the mod adds above and beside the
    head. The reading is the mean absolute greyscale difference between the test frame and the mod reference over
    that zone (the zone does not depend on the test frame, so a launch that looks like the mod reads its noise).
    Returns {"d", "zonePixels", "zoneFraction"} or {"refused": reason}.
    Why: the check read `above` (the top 22% of the frame, over the union of both subjects). Dervenin's horns stand
    up into it (33.7 removed); Beleval's two horns sit at the sides of her mohawk at 0.25 to 0.33 of the frame
    height, below it, and her mohawk filled what she has there, so removing them read 0.70 to 0.86 (frames show
    them gone, RUN\\traces\\H2d-antlers-A3.jpg). In the zone her removed horns read 2.2 to 2.4, with the antlers on
    at most 0.35. Over every antler subject (cases in THRESHOLDS at ANTLER_GONE_MIN) the same threshold separates.
    Limits: the zone also holds whatever ELSE the mod adds up there (a mohawk, a wig: Auri's is most of hers), so a
    change of that hair (tint) reads as change too, as it did in `above` (Auri in A8, antlers on, 0.94; A6, removed
    and tinted, 20.6). Antlers drawn entirely inside the vanilla subject's silhouette would not be in the zone; a
    zone smaller than MASK_MIN of the band is refused, never read as "matches"."""
    for r, what in ((test, "test"), (mod, "mod reference"), (van, "vanilla reference")):
        if not usable(r, view):
            return {"refused": f"no usable {what} frame to locate the antlers"}
    ok, why = same_pose(mod, van, view)
    if not ok:
        return {"refused": f"the mod and vanilla references cannot be laid over each other to locate the antlers ({why})"}
    gt, _ = full_subject(test, view)
    gm, mm = full_subject(mod, view)
    _, mv = full_subject(van, view)
    if gt is None or gm is None or mv is None or not (gt.shape == gm.shape == mv.shape):
        return {"refused": "frame sizes differ: the antler zone cannot be located"}
    h, w = gm.shape
    x0, y0, x1, y1 = ANTLER_BAND
    band = (slice(int(y0 * h), int(y1 * h)), slice(int(x0 * w), int(x1 * w)))
    zone = mm[band] & ~mv[band]
    frac = float(zone.mean())
    if frac < THRESHOLDS["MASK_MIN"]:
        return {"refused": f"the mod reference adds no silhouette above or beside the head ({frac:.4f} of the band): the antlers cannot be located", "zoneFraction": round(frac, 4)}
    return {"d": round(float(abs(gt[band] - gm[band])[zone].mean()), 3), "zonePixels": int(zone.sum()), "zoneFraction": round(frac, 4)}


def actor_base(row):
    return ((row or {}).get("tele") or {}).get("base") or {}


def template_candidates(name, rows):
    """Subjects of this launch that `name` takes her face from, read from the game. Her actor base names a template;
    that template is either another subject's actor base (the unmodded game, the mod alone: RikkeTwin names
    0x000132A1, Rikke) or the face donor another subject points at: SkyPatcher and Recast point the patched NPC at
    a donor in the output plugin and the game reports that donor for whoever inherits from the patched NPC (A4, A5,
    A8: RikkeTwin and Rikke read the same 0xFE04...., as do ArnielShade and ArnielGane)."""
    t = actor_base(rows.get((name, "default"))).get("template")
    if not t:
        return []
    return [n for (n, st), r in rows.items() if st == "default" and n not in (name, "@vacant") and n in B.S
            and t in (actor_base(r).get("formId"), actor_base(r).get("template"))]


def inherited_look(arm, name, e, rows, ref_rows, manifest):
    """H2f. A REJECTED selection normally means "unchanged". An NPC that takes her face from a template the arm DID
    patch is not unchanged: she shows the template's new face in her own outfit. Changes the expectation `e` in
    place: kind "mod", no wig or antler mode of her own (her selection was rejected), and the reference is
      - her OWN mod reference, when the template's subject carries the same mod and she inherits from that subject
        in the reference launch too: that frame is the template's mod face in her own outfit;
      - otherwise the template subject's mod reference (then a difference of outfit will show, and the note says so).
    Measured 2026-11-25: RikkeTwin in A4, A5, A8 (rejected: "can't apply an appearance through a template chain")
    read mod-like (face ratio 0.151, 0.022, 0.143) where vanilla was expected. Against Rikke's own reference she
    differs by her helmet and armour (face 11.7 to 13.2, crown 32.5 to 66.9, neck 10.4 to 12.3); against her own
    High Poly reference A5 matches in every region (face 0.28, crown 0.37, neck 0.15).
    The crown is compared (no wig mode of her own): in A4 and A8, where Rikke's wig is converted to head parts, it
    differs from her reference by 31.8 and 37.9 with the face matching (2.4, 2.3): hair shows through her helmet."""
    if not e.get("rejected") or e["kind"] != "vanilla" or name == "Thorgar":
        return
    for t in template_candidates(name, rows):
        te = expectation(arm, t, manifest)
        if te["kind"] != "mod" or te["rejected"] or not te["refKey"] or t not in B.ARMS[arm]["subjects"]:
            continue
        own_key = B.S[name][2]
        ln = ref_launch_for(name, own_key) if own_key else None
        own, tref = (ref_rows.get(ln, {}).get((n, "default")) for n in (name, t)) if ln else (None, None)
        same = bool(own_key == te["refKey"] and own and tref and actor_base(own).get("template") and
                    actor_base(own).get("template") == actor_base(tref).get("formId"))
        e.update(kind="mod", inherits=t, modes=None)
        if not same:
            e["refKey"], e["refSubject"] = te["refKey"], te["refSubject"]
        e["notes"] = [f"screening rejected the selection, but the NPC takes its face from {t}, which this arm patched: expected to look like "
                      + (f"its own {own_key} reference (the template's mod face in its own outfit)" if same else f"{t}'s mod reference (its own outfit will differ)")]
        return


def wig_parts(row):
    """Head parts on the NPC that NPC2 minted from a wig (ConvertToHeadParts names them NPC2Wig_*)."""
    return sorted(n for n in part_names(row) if n.lower().startswith("npc2wig"))


def crown_compared(wig_mode, kind, antler_gone):
    """Is the crown region of the default state compared with the reference by distance?
    v4: only for an NPC with no wig mode. v5: also for an accepted NPC whose wig NPC2 CONVERTED to head parts: the
    same hair mesh in the same place as the mod's worn wig, in the predicted colour. Measured against the mod
    reference: untinted converted wigs 0.20 to 0.26 (High Poly: Urag, Rikke, Arniel Gane) and 3.9 (Auri, physics
    hair), under headgear 0.34 to 0.53 (Briarheart, cultist); tinted where no tint is predicted 12.6 to 34.9.
    Still left out: where the antlers were removed from it, under ForwardToSkin and ForwardToOutfit (as in v4, not
    measured for this), and for a rejected NPC with a wig mode (compared with the vanilla reference as in v4)."""
    if antler_gone:
        return False
    return wig_mode == "None" or (wig_mode == "ConvertToHeadParts" and kind == "mod")


def inheritor_of(e):
    """N11. The patched template a rejected NPC takes its look from (set by inherited_look), or None. Such an NPC's
    outfit swap is PREDICTED since v5 (score_arm); before, every rejected NPC's swap was READ."""
    return e.get("inherits")


def tint_unverified(tint, racemenu_ref):
    """H2g as it stands in v5. A converted wig predicted TINTED (wig_tint) is compared with the mod alone with
    RaceMenu where that reading exists (`racemenu_ref`); the cell is then scored in full against it. Where it does
    not, the regions that hold hair, FACE (the fringe and the hair beside the face) and CROWN, are not compared by
    distance with the untinted mod reference, and the cell says the tint is not verified; the mod-or-vanilla face
    ratio still decides. Returns the reason the two regions are left out, or None.
    Measured: A6 Auri (tint Always), face 14.881 against the limit 9.0 with the same face and a darker red wig
    (build log: hair tint (1,1,1) -> (0.5176, 0.4157, 0.3529)); against the RaceMenu reading of 2026-11-26 the same
    wig reads 2.78 to 2.85. The old Auto's tint on High Poly's wigs moved the crown by 12.6 to 25.7 (Ulfric, pit
    fan, Rikke, Arniel Gane, Urag in the first run); v4 never looked, because it left the crown out under every
    wig mode. The same wigs untinted read 0.20 to 0.26 (A8, tint Never; A4 and A8 of 2026-11-26)."""
    if not tint or tint["tinted"] is False or racemenu_ref:
        return None
    if tint["tinted"] is None:
        return f"converted wig: {tint['why']}, so its colour is not predicted"
    return f"converted wig expected tinted ({tint['why']}) and there is no reading of the mod alone with RaceMenu: the tint is NOT verified"


def output_headpieces(row):
    """Head-slot pieces the NPC wears that are defined by the arm's own output plugin (N2B_*): a wig NPC2 forwarded
    to the outfit is worn as one (measured: A5 Velehk wears 0008B9:N2B_A5.esp on slot 31 until the outfit changes)."""
    return [w.get("localId") or w.get("formId") for w in ((row or {}).get("tele") or {}).get("worn") or []
            if w.get("firstSlot") in HEAD_SLOTS and (w.get("plugin") or "").startswith("N2B_")]


def real_hair(row):
    """Names of hair head parts that have a mesh (High Poly's bald placeholder under a wig has none)."""
    return sorted(hp.get("partName") or hp.get("editorId") or "?" for hp in eff_base(row).get("headParts") or []
                  if hp.get("type") == "Hair" and hp.get("model"))


def lost_prediction_void(arm, row):
    """H2e (1). "The wig leaves with the outfit" is only a prediction where the arm delivers an outfit for it to
    travel on. Recast Only delivers none (NPC2 Models/Settings.cs, OutfitFieldIsInert: "always true for Recast
    alone, which has no way to deliver an outfit at all"). Returns the reason there is no prediction, or None.
    Measured 2026-11-25, all asked ForwardToOutfit: A7 Urag (crown 0.464), Rikke (0.392), Auri (4.472) and A8
    ArnielGane (0.442) read "hair kept" where "lost" was predicted. Not excused: Recast HYBRID, which writes an ini
    and does deliver the outfit (A5 Velehk wears the forwarded wig piece until the outfit changes; A5 Briarheart
    reads "lost", 27.0, and passes on it).
    Since H2j the four cells above no longer reach this rule: their readings show the wig converted to head parts,
    so "kept" is predicted for them. It remains for a Recast Only reading that shows no conversion."""
    a = B.ARMS[arm]
    if a["backend"] == "Recast" and a["recast"] == "RecastOnly":
        return "Recast Only delivers no outfit, so no wig can leave with one"
    return None


def lost_unseen_over_real_hair(rd, row, srow, mod):
    """H2e (2). The prediction "lost" held on the RECORDS (the output's wig piece is worn before the outfit change
    and not after it) while the crown reads "kept", for an NPC that has real hair under the wig in the mod reference:
    the picture cannot show the loss, so the cell is READ with both readings, not FAIL. Returns the reason or None.
    Measured: A5 Velehk. The Dremora race does not accept High Poly's bald placeholder, so the game draws
    MaleDremoraHair01 (hair and horns) under the wig; the wig piece 0008B9:N2B_A5.esp left with the outfit and the
    crown moved 3.096 (the "same" limit is 4.5). Only this contradiction is excused: where the crown shows the loss
    the cell still passes on it (A2 and A6 pit fan 34.1 and 34.2, A3 and A5 Auri 30.6 and 30.8: all have real hair
    and read lost), and a wig piece still worn after the change, or never worn, still fails."""
    if rd.get("hair") != "kept" or not output_headpieces(row) or output_headpieces(srow) or not real_hair(mod):
        return None
    return (f"the forwarded wig left with the outfit on the records (worn before the change, not after) but the crown moved only "
            f"{rd.get('crown')}: {', '.join(real_hair(mod))} is drawn under the wig, so the picture cannot show the loss")


# ------------------------------------------------------------------ scoring one arm
def d1_failure(build_state):
    """H6. The reason every cell of an arm fails on its build, or None. bench_run's static scan (stale_stamps:
    override records written with a newer form version than the old-layout model data they carry) found a hit in
    the arm's output plugin: the game does not finish loading such a plugin (2026-11-25, A4, one record). NPC2 item
    N1 removed the cause, so a hit is a regression, nothing is launched (bench_run.d1_blocks), and no cell of the
    arm can pass. Not applied to a build of the first run that carries "workarounds": its stamps were set back so
    the cells could be read, and the summary row says "LOAD FAIL as built" for it."""
    found = (build_state or {}).get("formVersionMismatch") or {}
    if not found or (build_state or {}).get("workarounds"):
        return None
    hits = [h for v in found.values() for h in v]
    return (f"BUILD: D1 regression. The static scan found {len(hits)} override record(s) in the output plugin stamped with a newer form "
            f"version than the model data they carry ({', '.join(str(h.get('record')) + ' ' + str(h.get('editorId') or '') for h in hits)[:300]}): "
            "the game does not finish loading such a plugin, so the arm was not launched")


MODELLED_OVERRIDES = ("NpcWigHairTintModes", "DefaultWigHairTintMode")


def unmodelled_overrides(build_state):
    """Keys of a diagnostic build's settingsOverrides that the scorer's expectations do not follow. Only the wig
    tint keys are modelled (bench_def.tint_mode); for any other key the cells are scored as the arm is DEFINED, and
    each cell says so."""
    return sorted(k for k in ((build_state or {}).get("settingsOverrides") or {})
                  if k not in MODELLED_OVERRIDES and not k.endswith(":ModWigHairTintMode"))


def score_arm(arm, out=None, rows=None, label=None, only=None, build_root=None, build_state=None):
    """Score one arm. `out`: one output folder instead of the arm's recorded launches. `rows` + `label`: readings
    supplied by the caller (the known-answer controls), scored exactly as a launch's readings would be.
    `only`: score just these subjects (battle plan R1: a rescore names its cells). The result then goes to
    scores\\<arm>-only\\<names>\\ and never replaces the arm's stored results.
    `build_root`: read the arm's manifest from this folder (default BUILD; see N2B_BUILD_ROOT).
    `build_state`: with `rows`, the build state those readings are to be scored against (a control's known answer);
    without it injected readings are scored as the arm as defined, with nobody left out."""
    refs = load_refs()
    built = launch_build(arm, out, build_root)
    manifest = json.load(open(built["manifest"], encoding="utf-8"))
    injected = rows is not None
    rows = rows if injected else launch_rows(arm, out)
    ref_rows = {ln: launch_rows(ln) for ln in B.REFS}
    nz = refs["noise"]
    T = THRESHOLDS
    cells = []
    names = [s["name"] for s in B.launch_subjects(arm)] if not (out or injected) else sorted({k[0] for k in rows if k[0] != "@vacant"})
    # H5 (2026-11-27): a stored folder or injected rows can hold readings of a subject the bench no longer defines
    # (the first run's fixture NPCs Orgnar and Hod). They are skipped and named in the result ("skippedNotInBench").
    skipped = sorted(n for n in names if n not in B.S)
    names = [n for n in names if n in B.S]
    if skipped:
        print(f"score_arm {arm}: skipped readings of subjects the bench no longer defines: {skipped}")
    if only:
        unknown = [n for n in only if n not in names]
        assert not unknown, f"--only names subjects this {'folder' if out else 'arm'} does not hold: {unknown}"
        names = [n for n in names if n in only]
    bj = built["build"]
    if injected:
        build_state = build_state or {"excluded": [], "failures": []}
    else:
        build_state = json.load(open(bj)) if os.path.exists(bj) else {"excluded": [], "failures": []}
    d1 = d1_failure(build_state)                         # H6
    rm_build = racemenu_at_build(build_state)            # what tint mode Auto followed in this build
    rm_launch = launch_racemenu(out) if out else None    # what the game itself had (worn wigs only)
    unmodelled = unmodelled_overrides(build_state)
    for name in names:
        row = rows.get((name, "default"))
        e = expectation(arm, name, manifest)
        inherited_look(arm, name, e, rows, ref_rows, manifest)
        c = {"arm": arm, "name": name, "state": "default", "expect": e, "checks": [], "verdict": None, "frames": {}}
        cells.append(c)
        if unmodelled:
            e["notes"].append(f"this build carried settings overrides the scorer does not model ({', '.join(unmodelled)}): scored as the arm is defined")
        if d1:
            c["verdict"], c["why"] = "FAIL", d1
            continue
        if name in build_state.get("excluded", []):
            c["verdict"] = "FAIL"
            c["why"] = ("BUILD: with this subject selected NPC2 could not write its output plugin, so the whole output "
                        "was unusable. The arm was rebuilt without it. NPC2's diagnosis: "
                        + " ".join((build_state["failures"][0].get("diagnosis") or "").split())[:420])
            continue
        if not row:
            c["verdict"], c["why"] = "UNASSESSED", "no reading: the launch did not reach this subject"
            continue
        if row.get("UNASSESSED"):
            c["verdict"], c["why"] = "UNASSESSED", row["UNASSESSED"]
            continue
        if e["modes"] and effective_wig_mode(arm, name, row) != e["modes"]["wig"]:      # H2j: the mode NPC2 applied to this NPC
            e["modes"] = {**e["modes"], "wigByArm": e["modes"]["wig"], "wig": effective_wig_mode(arm, name, row)}
            e["notes"].append(f"wig mode {e['modes']['wigByArm']} asked, {e['modes']['wig']} applied: the reading carries a converted wig "
                              "(NPC2 converts where the outfit cannot reach the NPC)")
        ln = ref_launch_for(e["refSubject"], e["refKey"]) if e["refKey"] else None
        mod = ref_rows.get(ln, {}).get((e["refSubject"], "default")) if ln else None
        van = ref_rows["V"].get((name, "default"))
        # a reference reading that was refused where it was loaded (off the mark, no physics) is no reference (H1c)
        refused = {k: r["UNASSESSED"] for k, r in (("mod", mod), ("vanilla", van)) if r and r.get("UNASSESSED")}
        mod, van = (None if "mod" in refused else mod), (None if "vanilla" in refused else van)
        # ---- wig tint (v5): is this NPC's converted wig predicted tinted, and which reading answers for its colour.
        # `mod` stays the reference for the RECORDS; `pmod` is the picture's: the mod alone, or, for a wig predicted
        # tinted, the mod alone with RaceMenu where a reading of it exists (racemenu_ref_rows).
        wig_of = converted_wig_of(arm, name, e, row, rows)
        tint = wig_tint(arm, wig_of, build_state, rm_build) if wig_of else None
        pmod, rm_ref, rm_rows = mod, None, {}
        if tint:
            e["tint"] = tint
            if tint["tinted"] and mod is not None:
                rm_rows = racemenu_ref_rows(ln)
                cand = rm_rows.get((e["refSubject"], "default"))
                if cand and not cand.get("UNASSESSED") and usable(cand):
                    pmod, rm_ref = cand, launch_of(cand)
                    tint["reference"] = f"the mod alone with RaceMenu ({rm_ref})"
        unverified = tint_unverified(tint, rm_ref)
        c["frames"] = {"test": frame(row), "mod": frame(pmod), "vanilla": frame(van), "test34": frame(row, "face34"),
                       "mod34": frame(pmod, "face34"), "refLaunch": ln, **({"racemenuRef": rm_ref} if rm_ref else {})}
        fails, unass, notes = [], [], list(e["notes"])
        if tint:
            notes.append(f"converted wig{'' if wig_of == name else ' of ' + wig_of}: {'tinted' if tint['tinted'] else 'not tinted' if tint['tinted'] is False else 'tint not predicted'} "
                         f"({tint['why']}; mode from {tint['from']})" + (f"; compared with {tint['reference']}" if rm_ref else ""))
        if rm_launch is not None and "wig" in B.tags(name) and bool(rm_launch.get("tintsHairSlots")) != bool(rm_build.get("tintsHairSlots")):
            notes.append("RaceMenu " + ("tinted hair-slot wigs in the game of this launch but was not in the profile NPC2 built against"
                                        if rm_launch.get("tintsHairSlots") else "was in the profile NPC2 built against but not in the game of this launch")
                         + ": a wig that is still WORN is tinted by the game, a converted one by the build")
        judged = None                    # is the NPC's own head-part list judged in this cell (set by the telemetry block)
        antler_left = None               # antler head parts still on the record under antler Remove (set there too)
        want = {"mod": mod, "vanilla": van}.get(e["kind"], mod)
        # ---- telemetry
        if e["kind"] in ("mod", "vanilla", "helmet_gone") and want:
            tp, rp = part_names(row), part_names(want)
            modes = e["modes"] or {}
            facts = (manifest.get("sourceFacts") or {}).get(B.fk(name)) or {}
            judged = True
            if e["backend"] in OWN_LIST_NOT_JUDGED and e["kind"] != "vanilla":
                judged = False          # the face is drawn from the donor; the actor base keeps its own head-part list
            if modes.get("antler") == "Remove" and e["kind"] != "vanilla":
                gone = antler_names(facts)
                left = antler_left = tp & gone
                c["checks"].append({"check": "antler head parts removed", "expected": sorted(gone), "stillPresent": sorted(left), "ok": not left,
                                    # H2d: antlers that are EXTRA PARTS of another head part are designated by hand
                                    # (bench_def.MANUAL_ANTLERS). They are not on the NPC's own list, before or after:
                                    # only the picture ("antlers gone") can show that they went.
                                    **({"designatedExtraParts": B.MANUAL_ANTLERS[name], "judgedBy": "the picture only"} if name in B.MANUAL_ANTLERS else {})})
                if left and judged:
                    fails.append(f"antler head part still present: {sorted(left)}")
                rp = rp - gone
            if modes.get("wig") in ("ForwardToSkin", "ForwardToOutfit", "ConvertToHeadParts") and e["kind"] != "vanilla":
                tp_c, rp_c = part_names(row, {"Face", "Eyes", "Brows", "Scar", "FacialHair"}), part_names(want, {"Face", "Eyes", "Brows", "Scar", "FacialHair"})
                if modes.get("antler") == "Remove":
                    rp_c = rp_c - antler_names(facts)
                ok = tp_c == rp_c
                c["checks"].append({"check": "head parts other than hair match the reference (a wig mode may replace hair)", "ok": ok,
                                    "missing": sorted(rp_c - tp_c), "extra": sorted(tp_c - rp_c)})
                if modes["wig"] == "ConvertToHeadParts":
                    conv = sorted(n for n in tp if n.lower().startswith("npc2wig"))
                    c["checks"].append({"check": "wig converted to a head part (NPC2Wig_*)", "found": conv, "ok": bool(conv)})
                    if not conv and judged:
                        fails.append("ConvertToHeadParts: no NPC2Wig_* head part on the NPC")
            else:
                ok = tp == rp
                c["checks"].append({"check": f"head parts match the {e['kind'] if e['kind'] != 'helmet_gone' else 'mod'} reference", "ok": ok,
                                    "missing": sorted(rp - tp), "extra": sorted(tp - rp), "judged": judged})
            if not ok and judged:
                fails.append("head parts differ from the expected reference")
            elif not ok:
                notes.append(f"head parts on the actor base differ from the reference (not judged under {e['backend']})")
            tr, rr = race_name(row), race_name(want)
            race_ok, race_note = race_matches(arm, (row.get("tele") or {}).get("actorRace"), (want.get("tele") or {}).get("actorRace"))
            c["checks"].append({"check": "race", "test": tr, "reference": rr, "ok": race_ok, **({"byOrigin": race_note} if race_note else {})})
            if race_note:
                notes.append(race_note)
            if not race_ok:
                if e["backend"] == "Recast" and e["recast"] == "RecastOnly":
                    notes.append(f"race {tr} vs reference {rr}: Recast Only does not change race")
                else:
                    fails.append(f"race is {tr}, reference has {rr}")
        elif not want and e["kind"] != "read":
            wk = "vanilla" if e["kind"] == "vanilla" else "mod"
            unass.append(f"the {wk} reference reading is refused: {refused[wk]}" if wk in refused else "no reference reading for this subject")
        # ---- pictures
        img = {}
        design = None                               # reason this cell's picture is not judged by design (N5, N6, H8)
        n_fails, n_unass = len(fails), len(unass)   # what follows is the picture's; the records' verdicts stand before
        if e["kind"] == "mod":
            design = (outfit_replaced_by_design(arm, row, mod, (manifest.get("sourceFacts") or {}).get(B.fk(name)))
                      or wig_headgear_conflict(e["modes"], row, mod, van))
            if design:
                c["checks"].append({"check": design, "test": base_worn(row), "reference": base_worn(mod), "ok": True})
        tf, mf, vf = frame(row), frame(pmod), frame(van)
        pm, why_m = same_pose(row, pmod) if pmod else (False, "the mod reference reading is refused" if "mod" in refused else "no mod reference")
        pv, why_v = same_pose(row, van) if van else (False, "the vanilla reference reading is refused" if "vanilla" in refused else "no vanilla reference")
        why_m, why_v = (why_m if pm else pose_reason(row, pmod, why_m)), (why_v if pv else pose_reason(row, van, why_v))
        if tf and mf and pm:
            img["d_mod"] = {reg: round(dist(row, pmod, reg), 3) for reg in REGIONS}
            if rm_ref:
                img["d_mod_against"] = tint["reference"]
        if tf and vf and pv:
            img["d_van"] = {reg: round(dist(row, van, reg), 3) for reg in REGIONS}
        qual = ((refs["subjects"].get(e["refSubject"], {}).get("mods") or {}).get(e["refKey"]) or {}).get("qualifies") if name == e["refSubject"] else None
        if name in NO_PICTURE:
            qual = False
            notes.append("no picture verdict: " + NO_PICTURE[name])
        img["qualifies"] = qual
        if "d_mod" in img and "d_van" in img and (img["d_mod"]["face"] + img["d_van"]["face"]) > 0:
            ratio = img["d_mod"]["face"] / (img["d_mod"]["face"] + img["d_van"]["face"])
            img["ratio"] = round(ratio, 3)
            img["class"] = "mod" if ratio <= T["RATIO_MOD"] else "vanilla" if ratio >= T["RATIO_VAN"] else "neither"
        # the outfit the arm is designed to leave on the NPC, where it is not the mod reference's (H2b)
        oc = kept_vanilla_outfit(arm, row, mod, van) if e["kind"] == "mod" else None
        covered = set(oc["covered"]) if oc else set()
        if oc:
            c["checks"].append({"check": "outfit differs from the mod reference by design", "covered": sorted(covered), "ok": True,
                                "test": base_worn(row), "reference": base_worn(mod)})
            notes.append(oc["note"])
        if e["kind"] in ("mod", "vanilla"):
            ref_d = img.get("d_mod" if e["kind"] == "mod" else "d_van")
            if ref_d is None:
                unass.append("picture not comparable: " + (why_m if e["kind"] == "mod" else why_v))
            else:
                if qual and "class" in img and img["class"] != e["kind"]:
                    if "face" in covered and img["class"] == "neither":
                        # headgear of the kept outfit shades the face: the mod's face under the vanilla hood reads
                        # "neither" (0.59 to 0.62, six cells). Only that is let through: a vanilla-like face fails.
                        notes.append(f"face ratio {img['ratio']} reads neither-like under the kept outfit's headgear (not vanilla-like)")
                    else:
                        fails.append(f"face is {img['class']}-like (ratio {img['ratio']}), expected {e['kind']}")
                elif not qual:
                    notes.append("the picture cannot tell this mod from vanilla for this subject: telemetry decides")
                if {"face", "crown", "neck"} <= covered and not (qual and "class" in img) and not judged:
                    unass.append("the kept outfit covers every compared region, the picture cannot tell this mod from vanilla and the "
                                 "head-part list is not judged here: nothing decides")
                modes = e["modes"] or {}
                antler_gone = modes.get("antler") == "Remove" and e["kind"] == "mod" and "antler" in B.tags(name)
                # The crown is compared with the reference for an NPC without a wig mode and, since v5, for a wig
                # CONVERTED to head parts: the same mesh in the same place, tinted or not as predicted. Left out as
                # in v4: under ForwardToSkin and ForwardToOutfit, and where the antlers were removed from it.
                wig_mode = modes.get("wig") or "None"
                if unverified and e["kind"] == "mod":
                    notes.append(f"{unverified}; face ({ref_d['face']}) and crown ({ref_d['crown']}) not compared by distance")
                for reg in ("face", "crown", "neck"):
                    if name in NO_PICTURE:
                        break
                    if reg == "crown" and not crown_compared(wig_mode, e["kind"], antler_gone):
                        continue
                    if reg in covered:
                        continue
                    if reg in ("face", "crown") and e["kind"] == "mod" and unverified:
                        continue
                    lim = T["DIFF_MIN_NOISE"] * nz[reg]
                    if ref_d[reg] > lim:
                        fails.append(f"{reg} differs from the {'mod reference with RaceMenu' if rm_ref and e['kind'] == 'mod' else e['kind'] + ' reference'} ({ref_d[reg]} > {round(lim, 2)})"
                                     + (f" [converted wig, predicted untinted: {tint['why']}]" if reg == "crown" and tint and tint["tinted"] is False else ""))
                if antler_gone:
                    lim = T["ANTLER_GONE_MIN"]      # measured for this check (see THRESHOLDS); always asked of the picture
                    az = antler_zone(row, pmod, van)    # H2d: the zone the mod adds above and beside the head
                    if "refused" in az:
                        c["checks"].append({"check": "antlers gone: the antler zone differs from the mod reference", "ok": None, "refused": az["refused"],
                                            "aboveD": ref_d["above"], "coverAbove": antlers_show(mod, van)})
                        unass.append(f"antler Remove: {az['refused']}")
                    else:
                        c["checks"].append({"check": "antlers gone: the antler zone differs from the mod reference", "d": az["d"], "min": round(lim, 2),
                                            "ok": az["d"] > lim, "zone": {k: az[k] for k in ("zonePixels", "zoneFraction")},
                                            "aboveD": ref_d["above"], "coverAbove": antlers_show(mod, van)})
                        if az["d"] <= lim:
                            fails.append(f"antler Remove: the antler zone (what the mod adds above and beside the head) still matches the mod reference ({az['d']} <= {round(lim, 2)})")
        elif e["kind"] == "helmet_gone":
            ref_d = img.get("d_mod")
            if ref_d is None:
                unass.append("picture not comparable: " + why_m)
            else:
                lim = T["DIFF_MIN_NOISE"] * nz["crown"]
                c["checks"].append({"check": "helmet not rendered: crown differs from the reference with the helmet", "d": ref_d["crown"], "min": round(lim, 2), "ok": ref_d["crown"] > lim})
                if ref_d["crown"] <= lim:
                    fails.append("override Ignore: the helmet still renders (crown matches the reference)")
        # a cell whose picture is not judged BY DESIGN: what the picture block objected to becomes a note, and the
        # cell is READ with the reason unless the records fail (H8; N5 and N6 set `design` above the picture block)
        design = design or designed_limit(name)
        if design:
            dropped = fails[n_fails:] + unass[n_unass:]
            del fails[n_fails:], unass[n_unass:]
            if dropped:
                notes.append("picture not judged (it reads: " + "; ".join(dropped) + ")")
        c["image"] = img
        c["notes"] = notes
        if e["kind"] == "read":
            c["verdict"], c["why"] = "READ", "; ".join(notes) or "reported, not predicted"
        elif fails:
            c["verdict"], c["why"] = "FAIL", "; ".join(fails)
        elif unass:
            c["verdict"], c["why"] = "UNASSESSED", "; ".join(unass)
        elif design:
            c["verdict"], c["why"] = "READ", "; ".join([design] + [n for n in notes if not n.startswith("no picture verdict")])
        else:
            c["verdict"], c["why"] = "PASS", "; ".join(notes)
        # ---- outfit swap: hair and antlers through an outfit change, measured inside this launch
        if B.swaps(name) and name in [s["name"] for s in B.launch_subjects(arm)]:
            srow = rows.get((name, "swap"))
            s = {"arm": arm, "name": name, "state": "swap", "expect": {"hairKept": hair_kept_expected(arm, name, row), "modes": e["modes"]},
                 "checks": [], "frames": {"test": frame(srow), "default": tf}, "verdict": None}
            cells.append(s)
            if not srow or srow.get("UNASSESSED") or not frame(srow) or not tf:
                s["verdict"], s["why"] = "UNASSESSED", (srow or {}).get("UNASSESSED") or "no swap reading"
                continue
            inherits = inheritor_of(e)
            ref_keeps = (mk_of(name) == "HPNO") if "wig" in B.tags(name) else None
            if inherits:
                # N11 (v5). A rejected NPC that takes its look from a patched template has no wig mode of its own:
                # after the outfit change it must show what the mod alone shows after the same change, the
                # template's hair kept. Judged against the mod reference's swap frame, with or without headgear.
                # Under plugin records the game's own inheritance delivers it; under SkyPatcher and Recast NPC2
                # writes the NPC an entry of its own (Patcher.MirrorAppearanceOntoInheritingNpcs, 2026-11-26).
                # Measured on RikkeTwin against her High Poly reference after the swap: 0.20 to 0.27 with the
                # entry (A4, A8, wig converted or on the skin); 45.8, bald, without it where the wig is on the
                # skin (A4-N11skin, A5 of the first run); 13.6 with Rikke's converted wig in the old Auto tint.
                s["expect"].update(hairKept=ref_keeps, inherits=inherits)
            exp = s["expect"]["hairKept"]
            if tint:
                s["expect"]["tint"] = tint
            ref_srow = ref_rows.get(ln, {}).get((e["refSubject"], "swap")) if ln else None
            if ref_srow is not None and ref_srow.get("UNASSESSED"):
                ref_srow = None
            ref_label = "the mod reference"
            rm_srow = rm_rows.get((e["refSubject"], "swap")) if rm_ref else None
            if rm_srow and not rm_srow.get("UNASSESSED") and usable(rm_srow):      # a wig predicted tinted: the mod alone with RaceMenu
                ref_srow, ref_label = rm_srow, "the mod reference with RaceMenu"
            rd = swap_reading(name, row, srow, ref_srow, ref_keeps, nz, prefer_ref=bool(inherits), ref_label=ref_label)
            if rd.get("againstRef") and rm_srow is not None and ref_srow is rm_srow:
                rd["racemenuRef"] = launch_of(rm_srow)
            s["image"] = rd
            s["frames"]["refSwap"] = frame(ref_srow)
            ak = antler_kept_expected(arm, name)
            confounded = ak is not None and exp is not None and ak != exp
            void = lost_prediction_void(arm, row) if exp is False else None
            if void:
                s["expect"]["noPrediction"] = void
            predicted = f"an NPC that takes its look from {inherits} shows what the mod alone shows after the same change" if inherits else "the mode predicts"
            if exp is None or (rejected_or_read(e) and not inherits):
                s["verdict"], s["why"] = "READ", f"no prediction for this subject's swap in this arm (crown reads: hair {rd.get('hair')})"
            elif designed_limit(name):
                s["verdict"], s["why"] = "READ", designed_limit(name)
            elif void:
                s["verdict"], s["why"] = "READ", f"no prediction: {void} (crown reads: hair {rd.get('hair')}, {rd.get('crown')})"
            elif rd["hair"] is None:
                s["verdict"], s["why"] = "UNASSESSED", rd.get("why", "no reading")
            elif inherits and not rd.get("againstRef"):
                s["verdict"], s["why"] = "UNASSESSED", (f"{predicted}, and there is no reference frame after the same change to compare with "
                                                        f"(crown reads: hair {rd['hair']}, {rd['crown']} against {rd['against']})")
            elif exp and rd["hair"] == "kept" and rd.get("colour"):
                s["verdict"], s["why"] = colour_verdict(tint, rd)      # the hair is there in another colour: the tint prediction decides
            elif (rd["hair"] == "kept") == exp:
                s["verdict"], s["why"] = "PASS", (f"hair {rd['hair']} through the outfit change (crown {rd['crown']} against {rd['against']}), as {predicted}"
                                                  + (f"; a tinted wig is expected ({tint['why']}) and this frame matches the UNTINTED reference: not verified, there is no "
                                                     "reading of the mod alone with RaceMenu to tell a tint that does not show from one that is missing"
                                                     if tint and tint["tinted"] and rd.get("againstRef") and not rd.get("racemenuRef") and rd["hair"] == "kept" else ""))
            elif confounded:
                s["verdict"], s["why"] = "UNASSESSED", (f"crown reads hair {rd['hair']} ({rd['crown']}) where the mode predicts {'kept' if exp else 'lost'}, but the antlers "
                                                        f"are predicted {'kept' if ak else 'lost'} and sit in the same region: for a reader")
            elif exp is False and lost_unseen_over_real_hair(rd, row, srow, mod):
                s["verdict"], s["why"] = "READ", lost_unseen_over_real_hair(rd, row, srow, mod)
            else:
                s["verdict"], s["why"] = "FAIL", (f"hair {rd['hair']} through the outfit change (crown {rd['crown']} against {rd['against']}); "
                                                  + (f"{predicted}: the hair {'kept' if exp else 'lost'}" if inherits else f"the mode predicts {'kept' if exp else 'lost'}"))
    # ---- reload rows
    for (name, state), r in rows.items():
        if state != "reload" or (only and name not in only):
            continue
        base = rows.get((name, "default"))
        c = {"arm": arm, "name": name, "state": "reload", "checks": [], "frames": {"test": frame(r), "default": frame(base)}, "expect": {"sameAsBeforeReload": True}}
        cells.append(c)
        if r.get("UNASSESSED") or not frame(r) or not frame(base):
            c["verdict"], c["why"] = "UNASSESSED", r.get("UNASSESSED") or "no frame"
            continue
        ok_pose, why = same_pose(base, r)
        hp_ok = part_names(r) == part_names(base)
        d = dist(base, r, "face") if ok_pose else None
        c["image"] = {"faceVsBeforeReload": round(d, 3) if d is not None else None, "pose": "same" if ok_pose else why}
        if not hp_ok:
            c["verdict"], c["why"] = "FAIL", "head parts changed across the save and reload"
        elif d is None:
            c["verdict"], c["why"] = "UNASSESSED", "pose changed across the reload: " + why
        elif d > T["DIFF_MIN_NOISE"] * nz["face"]:
            c["verdict"], c["why"] = "FAIL", f"face changed across the save and reload ({round(d, 2)})"
        else:
            c["verdict"], c["why"] = "PASS", "same face and head parts after the reload"
    od = os.path.join(SCORES, "controls", label) if injected else os.path.join(SCORES, arm if not out else os.path.basename(out))
    if only and not injected:
        od = os.path.join(SCORES, (arm if not out else os.path.basename(out)) + "-only", "+".join(sorted(only))[:100])
    os.makedirs(od, exist_ok=True)
    result = {"arm": arm, "scored": time.strftime("%Y-%m-%d %H:%M:%S"), "digest": digest(), "noise": nz, "noiseMeasured": refs.get("noiseMeasured"),
              "outs": [out] if out else outs_of(arm), "manifest": {k: manifest[k] for k in ("backend", "recastMode", "patchingMode", "patchedTargets", "invalidSelections", "plugins")},
              "build": build_state, "buildRoot": build_root or BUILD, "builtFrom": None if injected else built,
              "raceMenu": {"build": rm_build, "launch": rm_launch}, "unmodelledOverrides": unmodelled,
              "counts": {v: sum(1 for c in cells if c["verdict"] == v) for v in ("PASS", "FAIL", "UNASSESSED", "READ")}, "cells": cells,
              **({"skippedNotInBench": skipped} if skipped else {})}
    json.dump(result, open(os.path.join(od, "results.json"), "w"), indent=1)
    write_report(result, od)
    sheets(result, od)
    return result


def mk_of(name):
    return B.S[name][2]


def launch_of(row):
    """The launch folder a reading was taken in, from the path of its frame (<launch folder>\\shots\\...)."""
    parts = (frame(row) or "").replace("/", "\\").split("\\")
    return parts[parts.index("shots") - 1] if "shots" in parts and parts.index("shots") > 0 else None


def rejected_or_read(e):
    return bool(e.get("rejected")) or e.get("kind") == "read"


# ------------------------------------------------------------------ known-answer controls
def repeat_rows():
    """{(launch, name, state): row} from the tagged repeat launches: readings independent of the references."""
    out = {}
    for d in sorted(glob.glob(os.path.join(LIVE, "*-repeat-*"))):
        ln = os.path.basename(d).split("-")[0]
        for (name, state), r in rows_of(d).items():
            if name != "@vacant" and not r.get("UNASSESSED"):
                out[(ln, name, state)] = r
    return out


def controls(arm="A1"):
    """Known answers through the real scorer (score_arm), before any arm is judged:
      C1 perfect output    each subject's reading is a REPEAT launch of what the arm should show (the mod alone, or
                           the unmodded game for a rejected selection). Must PASS. Christer under override Ignore is
                           fed the reading WITH the helmet: must FAIL.
      C2 nothing applied   each subject's reading is a repeat of the unmodded game. Accepted selections must FAIL,
                           rejected ones PASS.
      C3 records right, picture wrong   the unmodded picture with the mod's telemetry (what a missing or mismatched
                           FaceGen file looks like to the records). Must FAIL wherever the picture can tell.
      C4 FaceGen deleted   a real launch of the arm with one subject's FaceGen head mesh deleted from NPC2's output.
                           That subject must FAIL; the untouched subjects in the same launch must PASS.
      C5 outfit change     crown change across the outfit swap inside the reference launches: an outfit-carried wig
                           (FoxGlove Auri) must read LOST, everything else KEPT.
    Only subjects with a repeat reading take part in C1-C3; the count is reported."""
    refs = build_refs()
    nz, T = refs["noise"], THRESHOLDS
    manifest = json.load(open(build_file(arm, "manifest.json"), encoding="utf-8"))
    ref_rows = {ln: launch_rows(ln) for ln in B.REFS}
    rep = repeat_rows()
    c1, c2, c3, want1, want2, want3 = {}, {}, {}, {}, {}, {}
    for sub in B.launch_subjects(arm):
        name = sub["name"]
        e = expectation(arm, name, manifest)
        ln = ref_launch_for(e["refSubject"], e["refKey"]) if e["refKey"] else None
        mod_rep, van_rep = rep.get((ln, e["refSubject"], "default")) if ln else None, rep.get(("V", name, "default"))
        qual = ((refs["subjects"].get(name, {}).get("mods") or {}).get(e["refKey"]) or {}).get("qualifies")
        if e["kind"] == "mod":
            if mod_rep and name == e["refSubject"]:
                c1[(name, "default")], want1[name] = mod_rep, "PASS"
            if van_rep:
                c2[(name, "default")], want2[name] = van_rep, "FAIL"
                mref = ref_rows.get(ln, {}).get((name, "default")) if ln else None
                if mref and qual and e["backend"] != "Recast":
                    c3[(name, "default")], want3[name] = {**van_rep, "tele": mref["tele"]}, "FAIL"
        elif e["kind"] == "vanilla" and van_rep:
            c1[(name, "default")], want1[name] = van_rep, "PASS"
            c2[(name, "default")], want2[name] = van_rep, "PASS"
        elif e["kind"] == "helmet_gone":
            if mod_rep:
                c1[(name, "default")], want1[name] = mod_rep, "FAIL"
            if van_rep:
                c2[(name, "default")], want2[name] = van_rep, "FAIL"
    report = {"digest": digest(), "noise": nz, "arm": arm, "controls": []}

    def judge(label, title, rows, want):
        res = score_arm(arm, rows=rows, label=label) if rows else {"cells": []}
        got = {c["name"]: c for c in res["cells"] if c["state"] == "default"}
        items = [{"name": n, "want": w, "got": got.get(n, {}).get("verdict"), "why": (got.get(n, {}).get("why") or "")[:240],
                  "ratio": (got.get(n, {}).get("image") or {}).get("ratio")} for n, w in want.items()]
        ok = bool(items) and all(i["want"] == i["got"] for i in items)
        report["controls"].append({"id": label, "title": title, "ok": ok, "n": len(items), "items": items})

    judge("C1-perfect-output", "repeat of the expected appearance", c1, want1)
    judge("C2-nothing-applied", "repeat of the unmodded game", c2, want2)
    judge("C3-records-right-picture-wrong", "unmodded picture with the mod's telemetry", c3, want3)
    # C4: the launch with a FaceGen mesh deleted
    st = os.path.join(B.RUN_ROOT, "refs-batch-status.json")
    fg = (json.load(open(st)).get("facegen") or {}) if os.path.exists(st) else {}
    items = []
    if fg.get("out"):
        res = score_arm(arm, out=fg["out"])
        deleted = os.path.splitext(os.path.basename(fg["deleted"]))[0].lower()
        for c in res["cells"]:
            if c["state"] != "default":
                continue
            broken = B.S[c["name"]][0].lower().zfill(8) == deleted
            items.append({"name": c["name"], "want": "FAIL" if broken else "PASS", "got": c["verdict"], "why": (c.get("why") or "")[:240],
                          "ratio": (c.get("image") or {}).get("ratio"), "d_mod_face": ((c.get("image") or {}).get("d_mod") or {}).get("face")})
    report["controls"].append({"id": "C4-facegen-deleted", "title": "real launch, one FaceGen head mesh deleted from the output",
                               "ok": bool(items) and all(i["want"] == i["got"] for i in items), "n": len(items), "items": items})
    # C5: the outfit swap, through swap_reading(), on launches whose answer is known
    items = []
    def wig_worn(r):
        return any("wig" in (w.get("name") or "").lower() for w in ((r or {}).get("tele") or {}).get("worn") or [])
    for ln, rows in ref_rows.items():
        for (name, state), srow in rows.items():
            if state != "swap" or name == "@vacant" or srow.get("UNASSESSED") or name in NO_PICTURE:
                continue
            base = rows.get((name, "default"))
            if headgear(base):
                # (b) headgear subjects go against another launch's swap frame: a repeat is the same hair (kept),
                #     the unmodded game against the mod is different hair (lost)
                if ln != "V" and ("V", name, "swap") in {("V",) + k for k in ref_rows["V"]} and not ref_rows["V"][(name, "swap")].get("UNASSESSED"):
                    rd = swap_reading(name, base, ref_rows["V"][(name, "swap")], srow, True, nz)
                    items.append({"name": f"{ln}/{name} vs unmodded", "want": "lost", "got": rd.get("hair") or rd.get("why"), "crown": rd.get("crown"), "overlap": rd.get("overlap")})
                continue
            want = "lost" if wig_worn(base) and not wig_worn(srow) else "kept"
            rd = swap_reading(name, base, srow, None, None, nz)
            items.append({"name": f"{ln}/{name}", "want": want, "got": rd.get("hair") or rd.get("why"), "crown": rd.get("crown"), "overlap": rd.get("overlap")})
    report["controls"].append({"id": "C5-outfit-change", "title": "hair through the outfit swap in the reference launches (a worn wig that leaves with the outfit = lost; base-game headgear subjects against another launch)",
                               "ok": bool(items) and all(i["want"] == i["got"] for i in items), "n": len(items), "items": items})
    report["controls"] += k_controls(rep)
    report["allOk"] = all(c["ok"] for c in report["controls"])
    od = os.path.join(SCORES, "controls")
    os.makedirs(od, exist_ok=True)
    json.dump(report, open(os.path.join(od, "controls.json"), "w"), indent=1)
    L = ["# NPC2 bench: known-answer controls", "", f"Method digest `{report['digest']}`. Noise: {nz}. All behave: **{report['allOk']}**", ""]
    for c in report["controls"]:
        L += [f"## {c['id']}: {c['title']}: {'OK' if c['ok'] else 'NOT OK'} ({c['n']} cells)", "", "| Cell | Expected | Got | Detail |", "|---|---|---|---|"]
        for i in c["items"]:
            L.append(f"| {i['name']} | {i['want']} | {i['got']} | {' '.join(str(i.get(k, '')) for k in ('ratio', 'crown', 'overlap', 'd_mod_face') if i.get(k) is not None)} {i.get('why', '')} |")
        L.append("")
    open(os.path.join(od, "CONTROLS.md"), "w", encoding="utf-8").write("\n".join(L) + "\n")
    return report


# ------------------------------------------------------------------ known-answer controls for the v4 rules
def stored_rows(arm):
    """An arm's readings from the launches the FIRST full run scored (their folders are listed in the snapshot's
    results.json and stay in <run>\\live), so a later relaunch of the arm cannot change what a control is fed.
    Without the snapshot: the arm's current launches."""
    outs = first_outs(arm)
    return merge_rows(outs) if outs else launch_rows(arm)


def first_outs(arm):
    """The output folders of an arm that the first full run scored (from the snapshot of its results)."""
    p = os.path.join(FIRST_RUN, "scores", arm, "results.json")
    return (json.load(open(p)).get("outs") or []) if os.path.exists(p) else []


def pick(rows, *names):
    """Those subjects' default and swap readings out of a launch's rows."""
    return {k: v for k, v in rows.items() if k[0] in names and k[1] in ("default", "swap")}


def feed(label, arm, rows, want, build=None):
    """One known-answer feed: `rows` scored as that arm's readings by the real score_arm, against the manifest of
    the first run's build (the build the stored launches were made from). want: {(name, state): verdict}.
    `build`: the build state the readings are scored against (settingsOverrides, raceMenu, formVersionMismatch);
    without it, the arm as defined."""
    fb = os.path.join(FIRST_RUN, "build")
    res = score_arm(arm, rows=rows, label=label, build_root=fb if os.path.exists(build_file(arm, "manifest.json", fb)) else None,
                    build_state={"excluded": [], "failures": [], **build} if build else None)
    got = {(c["name"], c["state"]): c for c in res["cells"]}
    return [{"name": f"{label.split('.')[-1]}: {arm} {n} [{st}]", "want": w, "got": got.get((n, st), {}).get("verdict"),
             "why": (got.get((n, st), {}).get("why") or ("no such reading was fed (launch folder gone?)" if not rows else ""))[:240]} for (n, st), w in want.items()]


# Launch folders of 2026-11-26 the v5 controls are fed from (they stay in <run>\live; a control whose folder is gone
# reports NOT OK rather than passing on nothing).
V5 = {"tint-auto": "A2-tintnew-auto-20261126-164740",            # new Auto, no RaceMenu: both wigs untinted
      "tint-auto-rm": "A2-tintnew-auto-rm-20261126-173906",      # new Auto, RaceMenu in the build's profile: both tinted
      "tint-npcoff-rm": "A2-tintnew-npcoff-rm-20261126-180334",  # as above with a per-NPC Never for Auri
      "tint-on": "A2-tintlab-on-20261126-105913",                # Always (old build; Always is unchanged): both tinted
      "tint-off": "A2-tintlab-off-20261126-104109",              # Never (old build; Never is unchanged): both untinted
      "n11-a4": "A4-N11built-20261126-193610",                   # SkyPatcher, the twin's entry written by NPC2
      "n11-a8": "A8-N11built-20261126-204120",                   # Recast Only, the same
      "n11-skin": "A4-N11skin-20261126-111420"}                  # SkyPatcher before the fix, wig on the skin: bald after the swap
RM_ON = {"raceMenu": {"installed": True, "mod": "RaceMenu", "tintEnabled": True, "slotMask": 2050, "tintsHairSlots": True}}


def live_rows(key, *names):
    """Those subjects' default and swap readings from one named launch folder of V5 ({} when it is gone)."""
    d = os.path.join(LIVE, V5[key])
    return pick(rows_of(d), *names) if os.path.isdir(d) else {}


def k_controls(rep):
    """One control per v4 rule. Each is fed stored readings (a launch of the first run, a repeat launch, or a
    reference launch) and names the verdict the rule must give; each was seen to FAIL with the old rule put back
    (2026-11-26, table in the hand-over report). A second feed in most controls is the guard: the case the rule must
    NOT excuse."""
    out = []

    def control(cid, title, items):
        out.append({"id": cid, "title": title, "ok": bool(items) and all(i["want"] == i["got"] for i in items), "n": len(items), "items": items})

    a3 = stored_rows("A3")
    # K-H2a: SkyPatcher leaves its own list on the actor base (the A3 launch's telemetry). With the mod's picture
    # (a repeat of the mod reference) the cell must pass; with the unmodded picture it must still fail.
    tele = a3[("Irileth", "default")]["tele"]
    control("K-H2a-skypatcher-own-head-parts", "A3 (SkyPatcher): the launch's own head-part list with the mod's picture passes, with the unmodded picture fails",
            feed("K-H2a.mod-picture", "A3", {("Irileth", "default"): {**rep[("R1", "Irileth", "default")], "tele": tele}}, {("Irileth", "default"): "PASS"})
            + feed("K-H2a.vanilla-picture", "A3", {("Irileth", "default"): {**rep[("V", "Irileth", "default")], "tele": tele}}, {("Irileth", "default"): "FAIL"}))
    a7, van = stored_rows("A7"), launch_rows("V")
    # K-H2b: Recast Only delivers no outfit. The A7 launch's Farkas (vanilla armour, the mod's face) and Sybille (the
    # mod's face under the vanilla hood) must pass. Guard: the unmodded Sybille herself (no repeat launch holds her,
    # so the vanilla reference reading is fed) wears the same hood and must still fail, on the face.
    control("K-H2b-vanilla-outfit-kept", "A7 (Recast Only): the mod's face in the vanilla outfit passes; the unmodded NPC in that outfit fails",
            feed("K-H2b.stored-launch", "A7", pick(a7, "Farkas", "Sybille"), {("Farkas", "default"): "PASS", ("Sybille", "default"): "PASS"})
            + feed("K-H2b.unmodded", "A7", {("Sybille", "default"): van[("Sybille", "default")]}, {("Sybille", "default"): "FAIL"}))
    a2, r1 = stored_rows("A2"), launch_rows("R1")
    # K-H2c: the measured "antlers gone" threshold (ANTLER_GONE_MIN). Launch readings with the antlers removed must
    # pass: A2 Anoriath (`above` moved 3.307), A2 Auri (2.344), A7 Auri (2.172). Readings with the antlers still on
    # must fail, each fed to a Recast arm that asks Remove, where only the picture can object: Elrindir and Anoriath
    # as the mod alone shows them (the R1 reference reading, a repeat of it), and Auri as the A8 launch shows her
    # (antlers kept there, 0.763) fed as an A7 reading. With the old limit 4.5 put back the three PASS items fail.
    only_d = lambda rows, *names: {k: v for k, v in pick(rows, *names).items() if k[1] == "default"}
    control("K-H2c-antlers-gone-threshold", "antler Remove: removed antlers pass (A2 Anoriath, A2 Auri, A7 Auri); antlers left on fail (Elrindir, Anoriath, Auri)",
            feed("K-H2c.removed-A2", "A2", only_d(a2, "Anoriath", "Auri"), {("Anoriath", "default"): "PASS", ("Auri", "default"): "PASS"})
            + feed("K-H2c.removed-A7", "A7", only_d(a7, "Auri"), {("Auri", "default"): "PASS"})
            + feed("K-H2c.left-on-A6", "A6", {("Elrindir", "default"): r1[("Elrindir", "default")], ("Anoriath", "default"): rep[("R1", "Anoriath", "default")]},
                   {("Elrindir", "default"): "FAIL", ("Anoriath", "default"): "FAIL"})
            + feed("K-H2c.kept-A8-as-A7", "A7", only_d(stored_rows("A8"), "Auri"), {("Auri", "default"): "FAIL"}))
    a5 = stored_rows("A5")
    # K-H2e: A5 Velehk: the forwarded wig piece left with the outfit (records) and the crown barely moved over his
    # real hair: READ, not FAIL. Guard: A3 Urag, bald under the forwarded wig, still passes on "lost" (the rule must
    # not take a working prediction away), and so does A5 Briarheart under Recast Hybrid.
    control("K-H2e-wig-to-outfit-prediction", "wig forwarded to the outfit: A5 Velehk (real hair under it) is READ; A3 Urag and A5 Briarheart still pass on 'lost'",
            feed("K-H2e.real-hair", "A5", pick(a5, "Velehk", "Briarheart"), {("Velehk", "swap"): "READ", ("Briarheart", "swap"): "PASS"})
            + feed("K-H2e.delivered", "A3", pick(a3, "Urag"), {("Urag", "swap"): "PASS"}))
    # K-H2j: ForwardToOutfit asked, the reading carries the wig as a head part: "kept" is predicted and read.
    # A2 Cultist (ESP, dressed by a template) and A7 Urag (Recast Only). The guard is K-H2e's A3 Urag: no converted
    # hair part on the reading, so "lost" stays predicted there.
    control("K-H2j-effective-wig-mode", "ForwardToOutfit asked but the wig is on the NPC as a head part: 'kept' is predicted (A2 Cultist, A7 Urag pass)",
            feed("K-H2j.template-dressed", "A2", pick(a2, "Cultist"), {("Cultist", "swap"): "PASS"})
            + feed("K-H2j.recast-only", "A7", pick(a7, "Urag"), {("Urag", "swap"): "PASS"}))
    # K-H2f: RikkeTwin is rejected in A5 and takes her face from Rikke, whom A5 patched: the A5 launch's reading
    # (the mod's Rikke face under her helmet) must pass. Guard: the unmodded twin (the vanilla reference reading,
    # whose template is Rikke's own actor base) beside the patched Rikke must fail: she no longer "must be unchanged".
    control("K-H2f-rejected-but-inherits", "A5: rejected RikkeTwin with the patched template's face passes; with the unmodded face fails",
            feed("K-H2f.stored-launch", "A5", {k: v for k, v in pick(a5, "RikkeTwin", "Rikke").items() if k[1] == "default"}, {("RikkeTwin", "default"): "PASS"})
            + feed("K-H2f.unmodded-twin", "A5", {("RikkeTwin", "default"): van[("RikkeTwin", "default")], ("Rikke", "default"): a5[("Rikke", "default")]},
                   {("RikkeTwin", "default"): "FAIL"}))
    a6 = stored_rows("A6")
    # K-H2g: A6 tints Auri's converted wig; the A6 launch's reading must pass. Guard: a repeat of the unmodded Auri
    # as an A6 reading carries no converted wig, is compared in full and must fail.
    # v5: the A6 reading now passes by comparison with the mod alone with RaceMenu (R1-racemenu-*), not by leaving
    # the face out. The case without such a reading is K-tint-unverified below.
    control("K-H2g-wig-tint-always", "A6 (tint Always): Auri with the recoloured converted wig passes; the unmodded Auri fails",
            feed("K-H2g.stored-launch", "A6", {("Auri", "default"): a6[("Auri", "default")]}, {("Auri", "default"): "PASS"})
            + feed("K-H2g.unmodded", "A6", {("Auri", "default"): rep[("V", "Auri", "default")]}, {("Auri", "default"): "FAIL"}))
    # K-H2h: no stored A2 reading with a copied race passes as a whole cell (the first run's Include As New arms
    # draw floating heads, D2: the neck fails; Vilja's reading is off the mark), so the verdict cannot show the race
    # rule alone. The rule is tested directly on races read in stored launches (A2 Ulfric's copy of NordRace against
    # the High Poly reference's NordRace), and the race check inside the scored A2 Ulfric cell is read.
    def race_of(r):
        return (r.get("tele") or {}).get("actorRace")
    a2v, r4v = race_of(a2[("Ulfric", "default")]), race_of(launch_rows("R2")[("Ulfric", "default")])
    cell = next(c for c in score_arm("A2", rows={("Ulfric", "default"): a2[("Ulfric", "default")]}, label="K-H2h.cell",
                                     build_root=os.path.join(FIRST_RUN, "build") if os.path.isdir(os.path.join(FIRST_RUN, "build")) else None)["cells"]
                if c["name"] == "Ulfric" and c["state"] == "default")
    said = lambda ok: "race matches" if ok else "race differs"
    items = [{"name": f"race_matches A2: {form_name(a2v)} [{a2v.get('plugin')}] against {form_name(r4v)}", "want": "race matches", "got": said(race_matches("A2", a2v, r4v)[0])},
             {"name": f"race_matches A2: the same copy against {form_name(race_of(r1[('Irileth', 'default')]))} (another race)", "want": "race differs",
              "got": said(race_matches("A2", a2v, race_of(r1[("Irileth", "default")]))[0])},
             {"name": "race_matches A4 (Include, not Include As New): the same copy against NordRace", "want": "race differs", "got": said(race_matches("A4", a2v, r4v)[0])},
             {"name": "race check inside the scored cell: A2 Ulfric [default]", "want": "race matches",
              "got": next((said(k["ok"]) for k in cell["checks"] if k["check"] == "race"), "no race check: " + str(cell.get("verdict"))), "why": (cell.get("why") or "")[:200]}]
    control("K-H2h-race-by-origin", "Include As New: the output's private copy of the reference race matches; another race, or a copy outside Include As New, does not", items)
    a1 = stored_rows("A1")
    # K-H8: the ghost. The A1 launch's readings (records right) are READ in both states. Guard: with the unmodded
    # game's records (the vanilla reference reading) in A1, where the head-part list is judged, she must FAIL.
    control("K-H8-ghost-designed-limit", "ArnielShade: READ as a designed limit with the right records; FAIL with the unmodded records",
            feed("K-H8.stored-launch", "A1", pick(a1, "ArnielShade"), {("ArnielShade", "default"): "READ", ("ArnielShade", "swap"): "READ"})
            + feed("K-H8.unmodded-records", "A1", {("ArnielShade", "default"): van[("ArnielShade", "default")]}, {("ArnielShade", "default"): "FAIL"}))
    # K-N5: the cultist. A3 (SkyPatcher) and A6 (Recast Hybrid) write an ini with Include Outfits on: READ. Guards:
    # in A7 (Recast Only, no ini) he keeps the mask and passes; in A3 Briarheart, also templated, keeps his outfit and passes.
    only_default = lambda rows, *names: {k: v for k, v in pick(rows, *names).items() if k[1] == "default"}
    control("K-N5-templated-outfit-replaced", "cultist: READ where an ini delivers the mod's outfit (A3, A6); PASS where none does (A7); a templated NPC whose outfit is unchanged passes (A3 Briarheart)",
            feed("K-N5.skypatcher", "A3", only_default(a3, "Cultist", "Briarheart"), {("Cultist", "default"): "READ", ("Briarheart", "default"): "PASS"})
            + feed("K-N5.recast-hybrid", "A6", only_default(a6, "Cultist"), {("Cultist", "default"): "READ"})
            + feed("K-N5.recast-only", "A7", only_default(a7, "Cultist"), {("Cultist", "default"): "PASS"}))
    # K-H2i: Briarheart after the outfit change. The same hair in another colour is not "lost": hair_same() on the
    # first run's A2 and A6 frames (wig tinted) against the black wig of the mod reference must say "same hair,
    # colour differs", and on A8 (the Include As New baldness) "not the same hair". As cells: A6 (tint Always)
    # passes, A8 fails. v5: the A2 cell left this control. Its wig was tinted by the OLD Auto; under the new Auto
    # no tint is predicted there and the cell fails on the COLOUR (K-tint-untinted-is-compared), not as "hair lost".
    nz5, r2 = load_refs()["noise"], launch_rows("R2")
    def same_hair(arm_rows):
        cc = compare(r2[("Briarheart", "swap")], arm_rows[("Briarheart", "swap")], "crown")
        same, colour = hair_same(cc, nz5)
        return ("not the same hair" if same is False else "undecided" if same is None else "same hair" + (", colour differs" if colour else "")) + f" (crown {cc['d']:.2f}, overlap {cc['iou']})"
    control("K-H2i-lost-means-gone", "Briarheart swap: the same hair in another colour is the same hair (A2, A6 of the first run); hair really gone is not (A8)",
            [{"name": f"hair_same: first run {arm} Briarheart after the swap against the mod reference", "want": want, "got": same_hair(rws).split(" (")[0], "why": same_hair(rws)}
             for arm, rws, want in (("A2", a2, "same hair, colour differs"), ("A6", a6, "same hair, colour differs"), ("A8", stored_rows("A8"), "not the same hair"))]
            + feed("K-H2i.tint-always", "A6", pick(a6, "Briarheart"), {("Briarheart", "swap"): "PASS"})
            + feed("K-H2i.hair-gone", "A8", pick(stored_rows("A8"), "Briarheart"), {("Briarheart", "swap"): "FAIL"}))
    # K-N6: A3 RikkeTwin wears the forwarded wig where her references wear the officer's helmet: READ. Guard: A3
    # Urag, wig forwarded into an outfit without headgear, is scored as usual and passes.
    control("K-N6-wig-and-headgear-on-one-slot", "A3: RikkeTwin (forwarded wig, helmet gone) is READ; Urag (forwarded wig, no headgear in the outfit) passes",
            feed("K-N6.stored-launch", "A3", only_default(a3, "RikkeTwin", "Urag"), {("RikkeTwin", "default"): "READ", ("Urag", "default"): "PASS"}))
    # K-H1c (1): a reading whose head bone stood off the studio mark is refused where it is loaded. The A1 launch's
    # Vilja (head +179 in X, -90 in Y) must be UNASSESSED; Irileth of the same launch, on the mark, passes.
    control("K-H1c-off-the-mark", "A1: Vilja's reading, head bone off the studio mark, is UNASSESSED; Irileth, on the mark, passes",
            feed("K-H1c.stored-launch", "A1", only_default(a1, "Vilja", "Irileth"), {("Vilja", "default"): "UNASSESSED", ("Irileth", "default"): "PASS"}))
    # K-H1c (2): over the folders of one launch a reading facing forward is chosen before a turned one, whichever
    # comes first. No launch of the first run holds both for one subject, so merge_rows is given two stored folders
    # directly: Farkas turned 103.1 deg (an A2 folder) and facing forward (an A1 folder, an A3 folder for the tie).
    def holder(arm, want_turned):
        return next((o for o in first_outs(arm) for r in [rows_of(o).get(("Farkas", "default"))]
                     if r and not r.get("UNASSESSED") and bool(turned(r)) == want_turned), None)
    tf, ff, ff2 = holder("A2", True), holder("A1", False), holder("A3", False)
    facing = lambda outs: "forward" if turned(merge_rows(outs)[("Farkas", "default")]) == 0 else "turned"
    control("K-H1c-forward-reading-preferred", "merge_rows over two folders: the forward reading is chosen in either order; on a tie the earliest folder",
            [{"name": "Farkas: [turned folder, forward folder]", "want": "forward", "got": facing([tf, ff])},
             {"name": "Farkas: [forward folder, turned folder]", "want": "forward", "got": facing([ff, tf])},
             {"name": "Farkas: [forward folder of A1, forward folder of A3] -> the first", "want": os.path.basename(ff),
              "got": next((os.path.basename(o) for o in (ff, ff2) if os.path.basename(o) in (frame(merge_rows([ff, ff2])[("Farkas", "default")]) or "")), None)}]
            if tf and ff and ff2 else [])
    # K-H5-fixture (2026-11-27): the ArmaRace fixture on Christer (Mod A's NPC) and Thorgar (the observer), staged
    # with include-all-overrides (decision Q4, N7). Offline half: the staged settings give IncludeAllOverrides to the
    # fixture mod and to no other mod in every arm, and the expectation per override mode is the table written out
    # HERE (not read from expectation()). Picture half, both ways: Thorgar's reference reading with the helmet gone
    # (R1, Mod A active) passes as an Include arm's reading and fails as an Include As New arm's; his vanilla reading
    # (V, helmet shown) fails under Include; Christer's R1 reading (helmet shown) fails under Ignore.
    want_kind = {"Include": ("mod", "mod"), "IncludeAsNew": ("read", "vanilla"), "Ignore": ("helmet_gone", "vanilla")}
    items = []
    for arm in B.ARMS:
        st = B.stage_settings(arm)[0]
        fix = [m for m in st["ModSettings"] if m["DisplayName"] == B.FIXTURE_MODA]
        others = sorted(m["DisplayName"] for m in st["ModSettings"] if m.get("IncludeAllOverrides") and m["DisplayName"] != B.FIXTURE_MODA)
        if "Christer" in B.ARMS[arm]["subjects"]:
            items.append({"name": f"stage_settings({arm}): fixture mod IncludeAllOverrides, other mods with it", "want": "True, none",
                          "got": f"{bool(fix and fix[0].get('IncludeAllOverrides'))}, {', '.join(others) or 'none'}"})
        a = B.ARMS[arm]
        wc, wt = want_kind[a["overrides"]]
        if a["backend"] == "Recast" and a["recast"] == "RecastOnly":
            wc = "read"
        mf = {"invalidSelections": []}
        for nm, w in (("Christer", wc), ("Thorgar", wt)):
            if nm in a["subjects"]:
                items.append({"name": f"expectation({arm} {a['overrides']}, {nm})", "want": w, "got": expectation(arm, nm, mf)["kind"]})
    r1v, vv = launch_rows("R1"), launch_rows("V")
    th_d, ch_d = ("Thorgar", "default"), ("Christer", "default")
    items += (feed("K-H5.observer-helmet-gone-under-Include", "A1", {th_d: r1v[th_d]} if th_d in r1v else {}, {th_d: "PASS"})
              + feed("K-H5.observer-helmet-shown-under-Include", "A1", {th_d: vv[th_d]} if th_d in vv else {}, {th_d: "FAIL"})
              + feed("K-H5.observer-helmet-gone-under-IncludeAsNew", "A2", {th_d: r1v[th_d]} if th_d in r1v else {}, {th_d: "FAIL"})
              + feed("K-H5.target-helmet-shown-under-Ignore", "A3", {ch_d: r1v[ch_d]} if ch_d in r1v else {}, {ch_d: "FAIL"}))
    control("K-H5-fixture-include-all", "ArmaRace fixture (Christer, Thorgar) with include-all-overrides: staged for the fixture mod only; expectation per mode; Thorgar helmet gone passes under Include and fails under Include As New, shown fails under Include; Christer with the helmet fails under Ignore", items)
    out += k_controls_v5(rep, control=control, a2=a2, a6=a6, a5=a5, a3_tele=tele)
    return out


def k_controls_v5(rep, control, a2, a6, a5, a3_tele):
    """The controls of method v5 (2026-11-26). The tint is seen where the hair shows: for the Briarheart after the
    outfit change (his headdress hides it before), for Auri in her default state. Each tint control is fed real
    launch readings and a BUILD STATE (what the scorer would read from build.json), and bites both ways: the same
    readings pass against the build that made them and fail against the other one."""
    ctl = control
    B_, A_ = ("Briarheart", "swap"), ("Auri", "default")
    both = lambda v: {B_: v, A_: v}
    auto, auto_rm, npcoff = (live_rows(k, "Briarheart", "Auri") for k in ("tint-auto", "tint-auto-rm", "tint-npcoff-rm"))
    on, off = live_rows("tint-on", "Briarheart", "Auri"), live_rows("tint-off", "Briarheart", "Auri")
    # K-tint-auto: tint mode Auto follows RaceMenu in the BUILD's profile.
    ctl("K-tint-auto-follows-racemenu", "A2, tint Auto: untinted wigs pass without RaceMenu in the build and fail with it; tinted wigs pass with it and fail without",
        feed("K-tintA.untinted-no-racemenu", "A2", auto, both("PASS"))
        + feed("K-tintA.tinted-with-racemenu", "A2", auto_rm, both("PASS"), build=RM_ON)
        + feed("K-tintA.tinted-no-racemenu", "A2", auto_rm, both("FAIL"))
        + feed("K-tintA.untinted-with-racemenu", "A2", auto, both("FAIL"), build=RM_ON))
    # K-tint-per-npc: the NPC's own entry beats the mod's mode. Launch: RaceMenu, Auto, Never kept for Auri.
    npc_off = {**RM_ON, "settingsOverrides": {"NpcWigHairTintModes": {B.fk("Auri"): "Never"}}}
    ctl("K-tint-per-npc-entry", "A2, RaceMenu, Never kept for Auri: Briarheart tinted and Auri untinted pass; without the entry Auri fails; with it a tinted Auri fails",
        feed("K-tintN.own-build", "A2", npcoff, both("PASS"), build=npc_off)
        + feed("K-tintN.entry-dropped", "A2", npcoff, {B_: "PASS", A_: "FAIL"}, build=RM_ON)
        + feed("K-tintN.tinted-under-the-entry", "A2", auto_rm, {B_: "PASS", A_: "FAIL"}, build=npc_off))
    # K-tint-on-off: Always tints without RaceMenu, Never does not tint with it (readings of the tint lab).
    always = {"settingsOverrides": {"DefaultWigHairTintMode": "Always", "mod:Fox:ModWigHairTintMode": "Always"}}
    never = {"settingsOverrides": {"DefaultWigHairTintMode": "Never", "mod:Fox:ModWigHairTintMode": "Never"}}
    ctl("K-tint-on-and-off", "A2: tinted wigs pass under Always with no RaceMenu, untinted ones fail; untinted wigs pass under Never with RaceMenu, tinted ones fail",
        feed("K-tintM.always-tinted", "A2", on, both("PASS"), build=always)
        + feed("K-tintM.always-untinted", "A2", off, both("FAIL"), build=always)
        + feed("K-tintM.never-untinted", "A2", off, both("PASS"), build={**RM_ON, **never})
        + feed("K-tintM.never-tinted", "A2", on, both("FAIL"), build={**RM_ON, **never}))
    # K-tint-untinted-is-compared: where no tint is predicted, a converted wig must match the mod reference. The
    # first run's wigs were tinted by the OLD Auto: Rikke in A4 (crown 13.6 against the reference, never looked at
    # by v4) and the Briarheart in A2 after the swap (11.1, excused by v4) now fail as the arm is defined. Guards:
    # Rikke and Urag in A8 of the first run (tint Never, crowns 0.22 and 0.23) pass.
    a4, a8 = stored_rows("A4"), stored_rows("A8")
    only_default = lambda rows, *names: {k: v for k, v in pick(rows, *names).items() if k[1] == "default"}
    ctl("K-tint-untinted-is-compared", "no tint predicted: a wig tinted by the old Auto fails (first run: A4 Rikke's crown, A2 Briarheart after the swap); the same wig untinted passes (A8 Rikke, Urag)",
        feed("K-tintU.old-auto-crown", "A4", only_default(a4, "Rikke"), {("Rikke", "default"): "FAIL"})
        + feed("K-tintU.old-auto-swap", "A2", pick(a2, "Briarheart"), {B_: "FAIL"})
        + feed("K-tintU.never", "A8", only_default(a8, "Rikke", "Urag"), {("Rikke", "default"): "PASS", ("Urag", "default"): "PASS"}))
    # K-tint-unverified: a wig predicted tinted with NO reading of the mod alone with RaceMenu (Rikke has none) is
    # not compared in face and crown and passes with "NOT verified" in its reason. Its limit, stated as an item: the
    # same wig UNTINTED under Always (A8's Rikke as an Always reading) passes too, because nothing can tell.
    unv = (feed("K-tintV.tinted", "A4", only_default(a4, "Rikke"), {("Rikke", "default"): "PASS"}, build=always)
           + feed("K-tintV.untinted-limit", "A8", only_default(a8, "Rikke"), {("Rikke", "default"): "PASS"}, build=always))
    unv.append({"name": "both reasons say the tint is NOT verified", "want": "said", "got": "said" if all("NOT verified" in i["why"] for i in unv) else "not said"})
    ctl("K-tint-unverified-without-racemenu-reading", "tint predicted, no RaceMenu reading of the subject (Rikke): passes, says NOT verified; an untinted wig then passes too (the limit)", unv)
    # K-tint-inputs: where the prediction comes from.
    defined = sorted({m for arm in B.ARMS for m in B.launch_mods(arm)[0]} | {m for ref in B.REFS for m in B.launch_mods(ref)[0]})
    lab = os.path.join(SIDE_BUILD, V5["tint-npcoff-rm"], "build.json")
    kept = json.load(open(lab)) if os.path.exists(lab) else {}
    ctl("K-tint-inputs", "tint inputs: no launch as defined enables RaceMenu; the kept build state of the per-NPC launch names RaceMenu and the entry; mode precedence",
        [{"name": f"RaceMenu among the {len(defined)} mods of the launches as defined", "want": "not installed", "got": "installed" if B.racemenu_state(defined)["installed"] else "not installed"},
         {"name": "racemenu_at_build(no extraMods)", "want": "no tint", "got": "tint" if racemenu_at_build({}).get("tintsHairSlots") else "no tint"},
         {"name": f"racemenu_at_build({V5['tint-npcoff-rm']}: kept build.json, extraMods {kept.get('extraMods')!r})", "want": "tint",
          "got": "tint" if racemenu_at_build(kept).get("tintsHairSlots") else "no tint"},
         {"name": "tint_mode(A2, Auri) with that build's overrides", "want": "Never", "got": B.tint_mode("A2", "Auri", kept.get("settingsOverrides"))[0]},
         {"name": "tint_mode(A2, Briarheart) with that build's overrides", "want": "Auto", "got": B.tint_mode("A2", "Briarheart", kept.get("settingsOverrides"))[0]},
         {"name": "tint_mode(A6, Auri) as defined", "want": "Always", "got": B.tint_mode("A6", "Auri")[0]},
         {"name": "tint_mode(A8, Rikke) as defined", "want": "Never", "got": B.tint_mode("A8", "Rikke")[0]},
         {"name": "tint_mode(A6, Auri): default Never does not beat FoxGlove's own mode", "want": "Always", "got": B.tint_mode("A6", "Auri", {"DefaultWigHairTintMode": "Never"})[0]},
         {"name": "RaceMenu launches of the references are found from their own kept profiles (R1, R2)", "want": "R1 yes, R2 yes",
          "got": ", ".join(f"{ln} {'yes' if racemenu_ref_outs(ln) else 'no'}" for ln in ("R1", "R2")),
          "why": "; ".join(os.path.basename(o) for ln in ("R1", "R2") for o in racemenu_ref_outs(ln))},
         {"name": "a tagged reference launch WITHOUT RaceMenu (R2-tintlab-20261126-102334) is not taken for one", "want": "not taken",
          "got": "taken" if any(os.path.basename(o) == "R2-tintlab-20261126-102334" for o in racemenu_ref_outs("R2")) else "not taken"}])
    # K-N11: a rejected NPC that takes its look from a patched template. With NPC2's entry for her (A4 SkyPatcher,
    # A8 Recast Only, launches of 2026-11-26) the twin passes in her helmet and after the swap. Before the fix:
    # A4 with the wig on the skin, bald after the swap (45.8): FAIL; A5 of the first run (Recast Hybrid), the same:
    # FAIL; A4 and A8 of the first run, converted wig through the helmet (crown 31.8, 37.9): FAIL in the DEFAULT cell.
    twin_d, twin_s = ("RikkeTwin", "default"), ("RikkeTwin", "swap")
    ctl("K-N11-inheriting-npc", "RikkeTwin (rejected, inherits from Rikke): with NPC2's entry she passes before and after the swap (A4, A8); bald after the swap fails (A4 skin wig, A5); hair through the helmet fails (first run A4, A8)",
        feed("K-N11.skypatcher-fixed", "A4", live_rows("n11-a4", "Rikke", "RikkeTwin"), {twin_d: "PASS", twin_s: "PASS"})
        + feed("K-N11.recast-fixed", "A8", live_rows("n11-a8", "Rikke", "RikkeTwin"), {twin_d: "PASS", twin_s: "PASS"})
        + feed("K-N11.skin-wig-before-fix", "A4", live_rows("n11-skin", "Rikke", "RikkeTwin"), {twin_d: "PASS", twin_s: "FAIL"})
        + feed("K-N11.recast-hybrid-before-fix", "A5", pick(a5, "Rikke", "RikkeTwin"), {twin_d: "PASS", twin_s: "FAIL"})
        + feed("K-N11.through-helmet-A4", "A4", pick(a4, "Rikke", "RikkeTwin"), {twin_d: "FAIL"})
        + feed("K-N11.through-helmet-A8", "A8", pick(a8, "Rikke", "RikkeTwin"), {twin_d: "FAIL", twin_s: "PASS"}))
    # K-H6: a D1 hit in the build fails every cell of the arm, whatever the readings say; and the detector itself
    # still finds the first run's record and nothing in the same plugin once the stamp is right.
    import bench_run as R
    hit = {"formVersionMismatch": {"N2B_A3.esp": [{"record": "0CDD87:skyrim.esm", "type": "ARMA", "editorId": "NakedTorsoWerewolfBeast", "writtenVersion": 44, "masterVersion": 39}]}}
    good = {("Irileth", "default"): {**rep[("R1", "Irileth", "default")], "tele": a3_tele}}
    fb4 = os.path.join(FIRST_RUN, "build", "A4")
    def scan(sub):
        p, pj = os.path.join(fb4, sub, "N2B_A4.esp"), os.path.join(fb4, "profile.json")
        if not (os.path.exists(p) and os.path.exists(pj)):
            return "plugin or profile.json missing from the first-run snapshot"
        return ", ".join(h["record"] for h in R.stale_stamps(p, R.scan_dirs(json.load(open(pj))["mods"]))) or "no hit"
    ctl("K-H6-d1-regression", "a form-version hit in the build fails the arm's cells (the same readings pass without it); the scan finds the first run's record and nothing once its stamp is right",
        feed("K-H6.clean-build", "A3", good, {("Irileth", "default"): "PASS"})
        + feed("K-H6.hit-in-build", "A3", good, {("Irileth", "default"): "FAIL"}, build=hit)
        + [{"name": "stale_stamps: first run A4 as built (as-built\\N2B_A4.esp)", "want": "0CDD87:skyrim.esm", "got": scan("as-built")},
           {"name": "stale_stamps: the same plugin with the stamp set back (output-kept\\N2B_A4.esp)", "want": "no hit", "got": scan("output-kept")},
           {"name": "bench_run no longer alters a plugin (w1_apply gone) and blocks the launch on a hit (d1_blocks)", "want": "gone, blocks",
            "got": ("gone" if not hasattr(R, "w1_apply") else "still there") + ", " + ("blocks" if hasattr(R, "d1_blocks") else "does not block")}])
    # K-H7: where the frames go.
    run_drive, env0 = os.path.splitdrive(B.RUN_ROOT)[0].upper(), os.environ.pop("N2B_CAPTURE_ROOT", None)
    try:
        out_x = os.path.join(LIVE, "V-H7check-20000101-000000")
        default_shots = B.shots_dir(out_x)
        spec, spec_out = R.spec_for("V", only=["Irileth"], tag="H7check")
        spec_want = B.shots_dir(spec_out)
        os.environ["N2B_CAPTURE_ROOT"] = r"Q:\elsewhere"
        named = B.shots_dir(out_x)
        os.environ["N2B_CAPTURE_ROOT"] = "."
        inside = B.shots_dir(out_x)
    finally:
        os.environ.pop("N2B_CAPTURE_ROOT", None)
        if env0 is not None:
            os.environ["N2B_CAPTURE_ROOT"] = env0
    drv = open(os.path.join(HERE, "bench_driver.py"), encoding="utf-8").read()
    ctl("K-H7-capture-root", "frames go under a capture root off the run folder's drive by default; N2B_CAPTURE_ROOT names another; \".\" keeps them in the launch folder; the spec carries the folder and the driver uses it",
        [{"name": f"default frames folder for a launch ({default_shots})", "want": f"not on {run_drive}", "got": f"not on {run_drive}" if os.path.splitdrive(default_shots)[0].upper() != run_drive else f"on {run_drive}"},
         {"name": "default layout: <root>\\<run folder>\\<launch folder>\\shots", "want": os.path.join(B.CAPTURE_ROOT_DEFAULT, os.path.basename(B.RUN_ROOT), "V-H7check-20000101-000000", "shots"), "got": default_shots},
         {"name": "N2B_CAPTURE_ROOT=Q:\\elsewhere", "want": os.path.join(r"Q:\elsewhere", os.path.basename(B.RUN_ROOT), "V-H7check-20000101-000000", "shots"), "got": named},
         {"name": "N2B_CAPTURE_ROOT=. (the layout up to 2026-11-26)", "want": os.path.join(out_x, "shots"), "got": inside},
         {"name": "bench_run.spec_for(V): launch.json carries the frames folder", "want": spec_want, "got": spec.get("shots")},
         {"name": "bench_driver.py takes its frames folder from the spec", "want": "yes", "got": "yes" if 'SHOTS = SPEC.get("shots") or' in drv else "no"}])
    # K-H2d: the antlers designated by hand. Offline half: the staged settings carry the designations in NPC2's
    # shape, each names a head part of the mod's plugin that has a model and hangs as an EXTRA PART off a head part
    # of that NPC's mod record, and the first run's readings (antlers still on) fail the "antlers gone" check. The
    # passing half needs a launch (see the hand-over): then add those readings here as PASS.
    # The known answer is written out HERE, not read from bench_def.MANUAL_ANTLERS: an emptied table must fail.
    designated = {"Dervenin": ["000CotG_DerveninHLExtra01"], "Beleval": ["000CotG_BelevalHLExtra01", "000CotG_BelevalHLExtra02"]}
    items = []
    for arm in ("A3", "A7", "A8"):                       # the arms whose antler mode for Children of the Green is Remove
        staged = {d["EditorId"]: d["Sources"] for d in B.stage_settings(arm)[0]["ManualAntlerHeadParts"]}
        items += [{"name": f"stage_settings({arm}) designates {eid} on {name} (antler mode {B.effective_modes(arm, name)['antler']})", "want": B.fk(name),
                   "got": next((s["NpcFormKey"] for s in staged.get(eid, []) if s["ModName"] == B.MOD["CotG"]), None)}
                  for name, eids in designated.items() for eid in eids]
    items.append({"name": "stage_settings(A12), which holds neither subject, designates nothing", "want": 0, "got": len(B.stage_settings("A12")[0]["ManualAntlerHeadParts"])})
    items += antler_extra_part_items(R, designated)
    items += feed("K-H2d.antlers-still-on", "A3", only_default(stored_rows("A3"), "Dervenin", "Beleval"), {("Dervenin", "default"): "FAIL", ("Beleval", "default"): "FAIL"})
    ctl("K-H2d-antlers-designated-by-hand", "Dervenin and Beleval: their antler extra parts are designated in the staged settings and exist in the mod as extra parts with a mesh; readings with the antlers on fail", items)
    # K-H2d-antler-zone (2026-11-27): the "antlers gone" check reads where the antlers are (antler_zone). Antlers
    # on TOP of the head (Dervenin: horns standing up from the hood) and at the SIDES of the head (Beleval: two horns
    # low beside her mohawk, under the old `above` region). Removed: the three H2d launches (designated by hand;
    # frames checked by eye, RUN\traces\H2d-antlers-A3.jpg) must read "gone" and PASS. On: the first run's launches
    # of the same arms (Remove asked, the parts not detected, the antlers drawn) must read "still there" and FAIL.
    # Each item asserts the antler check's own answer, not only the verdict. With the old region (`above`) put back
    # Beleval's three removed readings (0.70 to 0.86) read "still there": RUN\traces\scorer-antler-region\bite.py.
    items = []
    for arm, folder in H2D_LAUNCHES.items():
        d = os.path.join(LIVE, folder)
        rows = only_default(rows_of(d), "Dervenin", "Beleval") if os.path.isdir(d) else {}
        items += antler_items(f"K-H2d-zone.removed-{arm}", arm, rows, {"Dervenin": "gone", "Beleval": "gone"})
        items += antler_items(f"K-H2d-zone.on-{arm}", arm, only_default(stored_rows(arm), "Dervenin", "Beleval"), {"Dervenin": "on", "Beleval": "on"})
    ctl("K-H2d-antler-zone", "antlers gone is read where the antlers are: removed antlers on top of the head (Dervenin) and at its sides (Beleval) pass in A3, A7, A8; the same antlers left on fail", items)
    return []


# The H2d launches of 2026-11-26 (Dervenin and Beleval, antlers designated by hand, Remove). They stay in <run>\live;
# a control whose folder is gone reports NOT OK rather than passing on nothing.
H2D_LAUNCHES = {"A3": "A3-H2d-20261126-225822", "A7": "A7-H2d-20261126-230837", "A8": "A8-H2d-20261126-231856"}


def antler_items(label, arm, rows, want):
    """Known-answer items for the "antlers gone" check: `rows` scored as `arm` readings (feed's path, the first
    run's build), and for each subject in `want` ("gone" or "on") two items: the check's own answer and the cell's
    verdict (gone: PASS, on: FAIL). A subject with no reading or no such check reports what it got."""
    fb = os.path.join(FIRST_RUN, "build")
    res = score_arm(arm, rows=rows, label=label, build_root=fb if os.path.exists(build_file(arm, "manifest.json", fb)) else None) if rows else {"cells": []}
    got = {c["name"]: c for c in res["cells"] if c["state"] == "default"}
    items = []
    for n, w in want.items():
        c = got.get(n) or {}
        chk = next((x for x in c.get("checks", []) if x["check"].startswith("antlers gone")), None)
        ans = None if chk is None else ("refused: " + chk["refused"]) if chk.get("refused") else "gone" if chk["ok"] else "on"
        tag = f"{label.split('.')[-1]}: {arm} {n}"
        items.append({"name": f"{tag} antler check (zone d {chk.get('d') if chk else None}, old above {chk.get('aboveD') if chk else None})",
                      "want": w, "got": ans if c else "no reading fed (launch folder gone?)"})
        items.append({"name": f"{tag} verdict", "want": "PASS" if w == "gone" else "FAIL", "got": c.get("verdict"), "why": (c.get("why") or "")[:240]})
    return items


def antler_extra_part_items(R, designated):
    """K-H2d, read from the corpus plugin: for each designated EditorID, the head part exists in
    "Children of the Green.esp", has a model, and is an extra part (HNAM) of a head part that the subject's NPC
    record in that plugin lists (PNAM). A typo or a mod update that renames a part fails here, before any build."""
    import struct
    d, _ = B.snapshot()
    entry = B.mod_entry(d, B.MOD["CotG"])
    path = next((os.path.join(f, fn) for f in entry["CorrespondingFolderPaths"] if os.path.isdir(f) for fn in os.listdir(f)
                 if fn.lower() == "children of the green.esp"), None)
    if not path:
        return [{"name": "Children of the Green.esp in the corpus mod folders", "want": "found", "got": "not found"}]
    masters, own = [m.lower() for m in B.masters(path)], os.path.basename(path).lower()
    recs = R.plugin_records(path)
    link = lambda b: (struct.unpack("<I", b[:4])[0] & 0xFFFFFF, (masters + [own])[min(struct.unpack("<I", b[:4])[0] >> 24, len(masters))])
    subs = lambda rec, sig: [x for s, x in rec[3] if s == sig]
    edid = lambda rec: (subs(rec, b"EDID")[0].rstrip(b"\0").decode("latin1") if subs(rec, b"EDID") else "")
    by_edid = {edid(v).lower(): (k, v) for k, v in recs.items() if v[0] == "HDPT"}
    items = []
    for name, eids in designated.items():
        form, plugin = B.S[name][0], B.S[name][1]
        npc = recs.get((int(form, 16), plugin.lower()))
        own_parts = {link(x) for x in subs(npc, b"PNAM")} if npc else set()
        for eid in eids:
            k, rec = by_edid.get(eid.lower(), (None, None))
            parents = sorted(edid(v) for kk, v in recs.items() if v[0] == "HDPT" and kk in own_parts and k in {link(x) for x in subs(v, b"HNAM")}) if rec else []
            got = ("no such head part" if not rec else "no model" if not subs(rec, b"MODL") else
                   "not an extra part of a head part of this NPC" if not parents else "extra part with a mesh")
            items.append({"name": f"{eid} in {os.path.basename(path)} for {name}", "want": "extra part with a mesh", "got": got,
                          "why": (subs(rec, b"MODL")[0].rstrip(b"\0").decode("latin1") + " under " + ", ".join(parents)) if rec and subs(rec, b"MODL") else ""})
    return items


# ------------------------------------------------------------------ report and sheets
def write_report(res, od):
    L = [f"# NPC2 bench: arm {res['arm']}", "",
         f"Scored {res['scored']}. Method digest `{res['digest']}`. Repeat noise (grey levels): {res['noise']}"
         f"{'' if res['noiseMeasured'] else ' (floor only: no repeat launch measured)'}.",
         f"NPC2 build: {res['manifest']['backend']} / {res['manifest']['patchingMode']}"
         f"{' / ' + res['manifest']['recastMode'] if res['manifest']['backend'] == 'Recast' else ''}, "
         f"{len(res['manifest']['patchedTargets'])} patched, {len(res['manifest']['invalidSelections'])} rejected by screening.", "",
         f"**{res['counts']}**", "", "| NPC | State | Verdict | Why | Face d(mod) | d(vanilla) | Ratio | Picture can tell? |", "|---|---|---|---|---|---|---|---|"]
    for c in res["cells"]:
        img = c.get("image") or {}
        L.append(f"| {c['name']} | {c['state']} | {c['verdict']} | {(c.get('why') or '')[:260]} | {(img.get('d_mod') or {}).get('face', '')} | "
                 f"{(img.get('d_van') or {}).get('face', '')} | {img.get('ratio', '')} | {img.get('qualifies', '') if c['state'] == 'default' else ''} |")
    open(os.path.join(od, "report.md"), "w", encoding="utf-8").write("\n".join(L) + "\n")


def sheets(res, od):
    """Contact sheets a reader can check: every FAIL, UNASSESSED and READ cell, and every fifth PASS. Each strip is
    test | mod reference | vanilla reference (default state) or default | after swap. All frames are collarbone-up."""
    from PIL import Image, ImageDraw
    picks = [c for c in res["cells"] if c["verdict"] in ("FAIL", "UNASSESSED", "READ")]
    passes = [c for c in res["cells"] if c["verdict"] == "PASS"]
    picks += passes[::5]
    W, H = 480, 270
    strips = []
    for c in picks:
        f = c.get("frames") or {}
        order = [("test", f.get("test")), ("mod ref", f.get("mod")), ("vanilla ref", f.get("vanilla"))] if c["state"] == "default" else \
                [("before", f.get("default")), (c["state"], f.get("test"))]
        strip = Image.new("RGB", (W * 3, H + 34), (24, 24, 24))
        d = ImageDraw.Draw(strip)
        d.text((6, 4), f"{res['arm']} {c['name']} [{c['state']}] {c['verdict']}: {(c.get('why') or '')[:150]}", fill=(255, 255, 255))
        for i, (label, p) in enumerate(order):
            if p and os.path.exists(p):
                strip.paste(Image.open(p).convert("RGB").resize((W, H)), (i * W, 34))
            d.text((i * W + 6, 20), label + ("" if p and os.path.exists(p) else " (no frame)"), fill=(200, 200, 120))
        strips.append((c, strip))
    paths = []
    for i in range(0, len(strips), 6):
        chunk = strips[i:i + 6]
        sheet = Image.new("RGB", (W * 3, (H + 34) * len(chunk)), (0, 0, 0))
        for j, (_, s) in enumerate(chunk):
            sheet.paste(s, (0, j * (H + 34)))
        p = os.path.join(od, f"sheet-{i // 6 + 1:02d}.jpg")
        sheet.save(p, quality=88)
        paths.append({"path": p, "cells": [f"{c['name']}/{c['state']}/{c['verdict']}" for c, _ in chunk]})
    json.dump(paths, open(os.path.join(od, "sheets.json"), "w"), indent=1)
    return paths


def summary():
    L = ["# NPC2 bench: summary", "", f"Method digest `{digest()}`.", "", "| Arm | PASS | FAIL | UNASSESSED | READ | Failing or unassessed cells |", "|---|---|---|---|---|---|"]
    for arm in B.ARMS:
        p = os.path.join(SCORES, arm, "results.json")
        if not os.path.exists(p):
            L.append(f"| {arm} | not scored | | | | |")
            continue
        r = json.load(open(p))
        bad = [f"{c['name']}/{c['state']} {c['verdict']}" for c in r["cells"] if c["verdict"] in ("FAIL", "UNASSESSED")]
        bs = r.get("build") or {}
        if bs.get("workarounds"):        # the cells were read from a plugin the harness altered: say so on the row
            n = sum(len(v) for v in (bs.get("formVersionMismatch") or {}).values())
            bad.insert(0, f"**LOAD FAIL as built** (game never loads: {n} record(s) stamped with a newer form version than "
                          f"their data; cells read with workaround {', '.join(w['id'] for w in bs['workarounds'])})")
        L.append(f"| {arm} | {r['counts']['PASS']} | {r['counts']['FAIL']} | {r['counts']['UNASSESSED']} | {r['counts']['READ']} | {'; '.join(bad)[:900]} |")
    out = os.path.join(SCORES, "SUMMARY.md")
    open(out, "w", encoding="utf-8").write("\n".join(L) + "\n")
    print("\n".join(L))
    return out


if __name__ == "__main__":
    a = sys.argv[1:]
    if a[:1] == ["refs"]:
        t = build_refs()
        print(json.dumps({"noise": t["noise"], "measured": t["noiseMeasured"], "launches": t["launches"]}, indent=1))
        for n, e in t["subjects"].items():
            for k, m in e["mods"].items():
                print(f"{n:12} {k:10} ref {m['launch']} present={m['present']} sepFace={(m.get('sep') or {}).get('face')} qualifies={m.get('qualifies')} hpDiffer={m.get('headPartsDifferFromVanilla')} {m.get('unassessed') or ''}")
    elif a[:1] == ["arm"]:
        r = score_arm(a[1], a[a.index("--out") + 1] if "--out" in a else None,
                      only=a[a.index("--only") + 1].split(",") if "--only" in a else None)
        print(a[1], r["counts"])
        for c in r["cells"]:
            if c["verdict"] != "PASS":
                print(f"  {c['name']:12} {c['state']:8} {c['verdict']:10} {(c.get('why') or '')[:200]}")
    elif a[:1] == ["controls"]:
        r = controls()
        for c in r["controls"]:
            print(c["id"], "OK" if c["ok"] else "NOT OK", c["n"])
            for i in c["items"]:
                if i["want"] != i["got"]:
                    print("   ", i)
        print("all controls behave:", r["allOk"])
    elif a[:1] == ["summary"]:
        summary()
    else:
        sys.exit(__doc__)
