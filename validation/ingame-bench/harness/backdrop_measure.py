"""Backdrop probe, offline: does a uniform fog backdrop find more of the subject than the empty-studio difference?

  python backdrop_measure.py <launch folder>            reads <launch>\\log.jsonl (backdrop_subject rows), writes
                                                        <launch>\\backdrop-measure.json and backdrop-<name>.jpg
  python backdrop_measure.py --selftest <scratch dir>   synthetic frames with a known answer

Per subject and variant (bench_driver.backdrop_* took the frames; backdrop.py says why the variants are what they are):
  (a) the subject mask by the CURRENT method (bench_score: any channel differs from the frame's own vacant frame by
      more than MASK_T, after the scorer's 4x box downscale) on the NORMAL face frame, and its enclosed-hole share:
      pixels inside the silhouette's outline that the mask leaves out, over the filled silhouette.
  (b) on the BACKDROP face frame, the mask by colour key: any channel differs from the backdrop colour (the median of
      that variant's vacant frame) by more than the key threshold (MASK_T, or the vacant frame's own 99.9th percentile
      spread if that is larger). That keyed silhouette is taken as the truth, and the miss rate is the share of it the
      normal frame's current-method mask leaves out. The subject is frozen, so the pose is the same; hair under
      physics can still sway by a pixel or two between the frames. So the miss rate is given twice: raw, and with
      keyed pixels within EDGE_TOL (downscaled) pixels of the normal mask not counted as missed. The restore check
      frame (the normal method's face frame taken again after the fog was put back) gives the motion actually seen:
      `repeat` is its distance to the normal face frame over the subject.
  (c) backdrop uniformity on the vacant frame: per-channel spread, the share of pixels off the backdrop colour by
      more than the key threshold, and the edge energy (mean gradient) against the normal vacant frame's: a wall or
      floor still drawn through the fog shows as structure.
  (d) did the fog change the SUBJECT: mean |grey difference| between the normal and the backdrop face frame over the
      pixels that are subject in both (normal mask and keyed mask), per bench_score.REGIONS region, against the repeat
      noise floor 1.5 (bench_score THRESHOLDS NOISE_FLOOR).
  (e) a sheet per subject for a reader: normal and each variant on top; below, the normal mask and each keyed mask,
      white = both, red = keyed but missed by the normal mask, blue = normal mask only."""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import numpy as np
from PIL import Image
from bench_score import REGIONS, THRESHOLDS, rgb        # the scorer's regions, threshold and 4x box downscale

MASK_T = THRESHOLDS["MASK_T"]
NOISE = THRESHOLDS["NOISE_FLOOR"]
EDGE_TOL = 2               # downscaled pixels (8 full-resolution pixels at 3840x2160)
TILE = (480, 270)


def region_slice(shape, region):
    h, w = shape[:2]
    x0, y0, x1, y1 = REGIONS[region]
    return slice(int(y0 * h), int(y1 * h)), slice(int(x0 * w), int(x1 * w))


def current_mask(f, v):
    return np.abs(f - v).max(axis=2) > MASK_T


def holes_share(mask):
    from scipy.ndimage import binary_fill_holes
    filled = binary_fill_holes(mask)
    n = int(filled.sum())
    return (round(float((filled & ~mask).sum()) / n, 4) if n else None), filled


def backdrop_colour(bv):
    flat = bv.reshape(-1, 3)
    med = np.median(flat, axis=0)
    dist = np.abs(bv - med).max(axis=2)
    return med, dist


def edge_energy(a):
    g = a.mean(axis=2)
    return float(np.abs(np.diff(g, axis=0)).mean() + np.abs(np.diff(g, axis=1)).mean())


def grey_mad(a, b, sel):
    return round(float(np.abs(a.mean(axis=2) - b.mean(axis=2))[sel].mean()), 3) if sel.any() else None


def measure_variant(N, NV, Mn, B, BV, R=None):
    from scipy.ndimage import binary_dilation
    med, dist = backdrop_colour(BV)
    key_t = max(MASK_T, float(np.percentile(dist, 99.9)))
    K = np.abs(B - med).max(axis=2) > key_t
    nk = int(K.sum())
    near = binary_dilation(Mn, iterations=EDGE_TOL)
    miss = K & ~Mn
    r = {"backdropRgb": [round(float(x), 1) for x in med], "keyThreshold": round(key_t, 2),
         "keyed": {"pixels": nk, "share": round(nk / K.size, 4), "holeShare": holes_share(K)[0]},
         "missRate": round(float(miss.sum()) / nk, 4) if nk else None,
         "missRateBeyondEdgeTol": round(float((miss & ~near).sum()) / nk, 4) if nk else None,
         "normalOnlyShare": round(float((Mn & ~K).sum()) / max(1, int(Mn.sum())), 4),
         "currentMethodOnBackdrop": None}
    Mb = current_mask(B, BV)
    r["currentMethodOnBackdrop"] = {"share": round(float(Mb.sum()) / Mb.size, 4),
                                    "missOfKeyed": round(float((K & ~Mb).sum()) / nk, 4) if nk else None}
    r["uniformity"] = {"stdRgb": [round(float(x), 2) for x in BV.reshape(-1, 3).std(axis=0)],
                       "offKeyShare": round(float((dist > MASK_T).mean()), 5),
                       "p99Spread": round(float(np.percentile(dist, 99)), 2),
                       "edgeEnergy": round(edge_energy(BV), 3), "edgeEnergyNormalVacant": round(edge_energy(NV), 3)}
    r["uniformity"]["structureVisible"] = bool(r["uniformity"]["offKeyShare"] > 0.001 or
                                               r["uniformity"]["edgeEnergy"] > 0.25 * r["uniformity"]["edgeEnergyNormalVacant"])
    both = Mn & K
    r["subjectChange"] = {}
    for reg in REGIONS:
        ys, xs = region_slice(N.shape, reg)
        d = grey_mad(N[ys, xs], B[ys, xs], both[ys, xs])
        r["subjectChange"][reg] = {"d": d, "overNoise": None if d is None else round(d / NOISE, 2)}
    return r, K


def overlay(Mn, K):
    img = np.zeros(Mn.shape + (3,), np.uint8)
    img[Mn & K] = (255, 255, 255)
    img[K & ~Mn] = (255, 40, 40)
    img[Mn & ~K] = (60, 110, 255)
    return Image.fromarray(img)


def tile(a):
    im = a if isinstance(a, Image.Image) else Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))
    return im.resize(TILE, Image.BILINEAR)


def sheet(path, top, bottom, labels):
    from PIL import ImageDraw
    cols = max(len(top), len(bottom))
    S = Image.new("RGB", (TILE[0] * cols, TILE[1] * 2 + 20), (32, 32, 32))
    d = ImageDraw.Draw(S)
    for row, items in ((0, top), (1, bottom)):
        for i, a in enumerate(items):
            S.paste(tile(a), (i * TILE[0], 20 + row * TILE[1]))
    for i, t in enumerate(labels):
        d.text((i * TILE[0] + 4, 4), t, fill=(230, 230, 230))
    S.save(path, "JPEG", quality=88)


def ok_path(p):
    return bool(p) and os.path.exists(p)


def measure_subject(row, outdir):
    name = row["name"]
    res = {"name": name, "variants": {}}
    N_p, NV_p = row.get("normalFace"), row.get("normalVacant")
    if not ok_path(N_p) or not ok_path(NV_p):
        res["skipped"] = "normal face or its vacant frame missing"
        return res
    N, NV = rgb(N_p), rgb(NV_p)
    Mn = current_mask(N, NV)
    hs, _ = holes_share(Mn)
    res["normal"] = {"maskShare": round(float(Mn.mean()), 4), "holeShare": hs}
    rep = [r for r in (row.get("restoreFaces") or []) if r.get("ok") and ok_path(r.get("face"))]
    if rep:
        R = rgb(rep[-1]["face"])
        res["repeat"] = {"subjectMad": grey_mad(N, R, Mn | current_mask(R, NV)), "frame": rep[-1]["face"]}
    top, bottom, labels = [N], [overlay(Mn, Mn)], ["normal"]
    for vid, v in (row.get("variants") or {}).items():
        if not ok_path(v.get("face")) or not ok_path(v.get("vacant")):
            res["variants"][vid] = {"skipped": v.get("UNASSESSED") or v.get("refused") or "backdrop face or vacant missing"}
            continue
        B, BV = rgb(v["face"]), rgb(v["vacant"])
        if B.shape != N.shape or BV.shape != N.shape:
            res["variants"][vid] = {"skipped": "frame sizes differ"}
            continue
        r, K = measure_variant(N, NV, Mn, B, BV)
        r["planes"] = [v.get("near"), v.get("far")]
        if ok_path(v.get("faceLate")):
            r["fogLateDiff"] = round(float(np.abs(rgb(v["faceLate"]) - B).mean()), 3)
        res["variants"][vid] = r
        top.append(B)
        bottom.append(overlay(Mn, K))
        labels.append(f"{vid} miss {r['missRate']} / {r['missRateBeyondEdgeTol']}")
    jpg = os.path.join(outdir, f"backdrop-{name}.jpg")
    sheet(jpg, top, bottom, labels)
    res["sheet"] = jpg
    return res


def table(results):
    lines = [f"{'subject':<12}{'variant':<13}{'hole%':>7}{'miss%':>7}{'miss>tol%':>10}{'bgStd':>8}{'struct':>7}"
             f"{'face d':>8}{'crown d':>8}{'repeat':>8}"]
    for s in results:
        if s.get("skipped"):
            lines.append(f"{s['name']:<12}SKIPPED: {s['skipped']}")
            continue
        for vid, r in s["variants"].items():
            if r.get("skipped"):
                lines.append(f"{s['name']:<12}{vid:<13}skipped: {r['skipped']}")
                continue
            sc = r["subjectChange"]
            f = lambda x: "-" if x is None else f"{x:.2f}"
            lines.append(f"{s['name']:<12}{vid:<13}{100 * (s['normal']['holeShare'] or 0):>7.2f}{100 * r['missRate']:>7.2f}"
                         f"{100 * r['missRateBeyondEdgeTol']:>10.2f}{max(r['uniformity']['stdRgb']):>8.2f}"
                         f"{'yes' if r['uniformity']['structureVisible'] else 'no':>7}{f(sc['face']['d']):>8}"
                         f"{f(sc['crown']['d']):>8}{f((s.get('repeat') or {}).get('subjectMad')):>8}")
    return "\n".join(lines)


def run(launch):
    rows = [json.loads(l) for l in open(os.path.join(launch, "log.jsonl"), encoding="utf-8")]
    setup = next((r for r in rows if r.get("step") == "backdrop_setup"), None)
    subs = [r for r in rows if r.get("step") == "backdrop_subject"]
    results = [measure_subject(r, launch) for r in subs]
    out = {"launch": launch, "setup": {k: (setup or {}).get(k) for k in ("ready", "refused", "sceneCell", "effective", "restoreCandidates")},
           "noiseFloor": NOISE, "maskT": MASK_T, "edgeTol": EDGE_TOL, "subjects": results}
    json.dump(out, open(os.path.join(launch, "backdrop-measure.json"), "w"), indent=1)
    if setup and not setup.get("ready"):
        print("backdrop setup refused:", setup.get("refused"))
    print(table(results))
    print("written:", os.path.join(launch, "backdrop-measure.json"))
    return out


def selftest(d):
    """A known answer: a textured dark wall, a subject with a dark fringe whose tone lies within MASK_T of the wall,
    a flat grey backdrop. The current method must miss about the fringe; the key must find all of it."""
    os.makedirs(d, exist_ok=True)
    rng = np.random.default_rng(7)
    H, W = 540, 960                                   # saved at 4x so rgb()'s box downscale gives 135 x 240
    yy, xx = np.mgrid[0:H, 0:W]
    wall = (30 + 12 * np.sin(xx / 9.0) * np.cos(yy / 13.0) + rng.normal(0, 2, (H, W)))[..., None].repeat(3, 2)
    body = ((xx - W / 2) / 120.0) ** 2 + ((yy - H * 0.55) / 170.0) ** 2 < 1
    fringe = (((xx - W / 2) / 150.0) ** 2 + ((yy - H * 0.35) / 120.0) ** 2 < 1) & ~body
    subj = np.zeros((H, W, 3)) + (180, 140, 120)
    def put(bg, motion=0):
        a = bg.copy()
        b = np.roll(body, motion, axis=1)
        f = np.roll(fringe, motion, axis=1)
        a[b] = subj[b]
        a[f] = wall[f] + 6                            # dark hair: within MASK_T of the wall
        return a
    grey = np.full((H, W, 3), 128.0) + rng.normal(0, 1.0, (H, W, 3))
    paths = {}
    for k, a in (("N", put(wall)), ("NV", wall), ("B", put(grey, motion=2)), ("BV", grey), ("R", put(wall))):
        paths[k] = os.path.join(d, f"{k}.png")
        Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).save(paths[k])
    row = {"step": "backdrop_subject", "name": "Synthetic", "normalFace": paths["N"], "normalVacant": paths["NV"],
           "restoreFaces": [{"ok": True, "face": paths["R"]}],
           "variants": {"grey": {"face": paths["B"], "vacant": paths["BV"], "near": 100.0, "far": 160.0}}}
    launch = os.path.join(d, "launch")
    os.makedirs(launch, exist_ok=True)
    with open(os.path.join(launch, "log.jsonl"), "w", encoding="utf-8") as f:
        f.write(json.dumps({"step": "backdrop_setup", "ready": True, "refused": []}) + "\n" + json.dumps(row) + "\n")
    out = run(launch)
    r = out["subjects"][0]["variants"]["grey"]
    truth = float(fringe.sum()) / float((fringe | body).sum())
    print(f"selftest: fringe share of the silhouette {truth:.4f}; measured miss {r['missRate']}, "
          f"beyond edge tolerance {r['missRateBeyondEdgeTol']}")
    assert abs(r["missRateBeyondEdgeTol"] - truth) < 0.05, "miss rate does not match the known answer"
    assert not r["uniformity"]["structureVisible"], "flat backdrop read as structured"
    return out


if __name__ == "__main__":
    a = sys.argv[1:]
    if a[:1] == ["--selftest"]:
        selftest(a[1])
    elif a:
        run(a[0])
    else:
        sys.exit(__doc__)
