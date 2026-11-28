"""Fit the camera's field of view from position-pinned rotations (devbench-screenshots, "the projection rule"):
with the position identical there is no parallax, so a patch at normalized offset n0 moves to n1 with
atan(n1*t) = atan(n0*t) - delta, t = tan(fov/2). Each patch is fitted separately; they must agree.

usage: python fov_fit.py <shots dir> [tag]     frames: <tag>-empty-face (yaw 180), -fov-yaw5, -fov-yaw10, -fov-pitch5
"""
import math, os, sys
import numpy as np
from PIL import Image
from numpy.lib.stride_tricks import sliding_window_view

D = sys.argv[1]
TAG = sys.argv[2] if len(sys.argv) > 2 else "new"
P = 96          # patch size at half resolution


def load(name):
    im = Image.open(os.path.join(D, f"{TAG}-{name}.png")).convert("L")
    W, H = im.size
    return np.asarray(im.resize((W // 2, H // 2), Image.BILINEAR), dtype=np.float32), (W, H)


def match(a, b, cx, cy, axis):
    """Where the PxP patch of `a` centred (cx, cy) lands in `b`, searching along `axis` (0 = x, 1 = y) with +-4 px of
    slack on the other one. Returns (shift in half-res px with sub-pixel refinement, normalized score, margin)."""
    h, w = a.shape
    patch = a[cy - P // 2:cy + P // 2, cx - P // 2:cx + P // 2]
    patch = patch - patch.mean()
    pn = np.sqrt((patch ** 2).sum()) + 1e-6
    best = None
    for off in range(-4, 5):
        if axis == 0:
            y0 = cy - P // 2 + off
            strip = b[y0:y0 + P, :]
            win = sliding_window_view(strip, P, axis=1)            # (P, w-P+1, P) -> windows along x
            win = np.moveaxis(win, 1, 0)                            # (n, P, P) with [row, col]
        else:
            x0 = cx - P // 2 + off
            strip = b[:, x0:x0 + P]
            win = sliding_window_view(strip, P, axis=0)            # (h-P+1, P, P) -> [n, col, row]
            win = np.moveaxis(win, 2, 1)
        wm = win - win.mean(axis=(1, 2), keepdims=True)
        score = (wm * patch).sum(axis=(1, 2)) / (np.sqrt((wm ** 2).sum(axis=(1, 2))) * pn + 1e-6)
        i = int(score.argmax())
        if best is None or score[i] > best[1]:
            sub = 0.0
            if 0 < i < len(score) - 1:
                l, c, r = score[i - 1], score[i], score[i + 1]
                den = l - 2 * c + r
                sub = 0.5 * (l - r) / den if abs(den) > 1e-9 else 0.0
            second = np.partition(score, -2)[-2] if len(score) > 1 else 0
            far = np.delete(score, range(max(0, i - 6), min(len(score), i + 7)))
            best = (i + sub, float(score[i]), float(score[i] - (far.max() if len(far) else 0)))
    start = (cx if axis == 0 else cy) - P // 2
    return best[0] - start, best[1], best[2]


def solve_t(n0, n1, delta):
    """t with atan(n1*t) - atan(n0*t) = delta (radians, signed)."""
    f = lambda t: math.atan(n1 * t) - math.atan(n0 * t) - delta
    lo, hi = 0.05, 5.0
    if f(lo) * f(hi) > 0:
        return None
    for _ in range(80):
        mid = (lo + hi) / 2
        if f(lo) * f(mid) <= 0:
            hi = mid
        else:
            lo = mid
    return (lo + hi) / 2


def fit(base, other, deg, axis):
    a, (W, H) = load(base)
    b, _ = load(other)
    h, w = a.shape
    half = (w if axis == 0 else h) / 2.0
    rows = []
    centres = ([(int(w * fx), h // 2) for fx in (0.30, 0.38, 0.46, 0.54, 0.62, 0.70)] if axis == 0 else
               [(w // 2, int(h * fy)) for fy in (0.30, 0.40, 0.50, 0.60, 0.70)])
    for cx, cy in centres:
        shift, score, margin = match(a, b, cx, cy, axis)
        c = cx if axis == 0 else cy
        n0, n1 = (c - half) / half, (c + shift - half) / half
        t = None
        for sign in (1, -1):                       # the direction is read off the data, then reported
            t = solve_t(n0, n1, sign * math.radians(deg))
            if t:
                break
        rows.append({"centre": (cx * 2, cy * 2), "shift_px_fullres": round(shift * 2, 2), "ncc": round(score, 3),
                     "margin": round(margin, 3), "fov_deg": round(2 * math.degrees(math.atan(t)), 3) if t else None})
    return (W, H), rows


if __name__ == "__main__":
    for other, deg, axis, label in (("fov-yaw5", 5.0, 0, "horizontal"), ("fov-yaw10", 10.0, 0, "horizontal"),
                                    ("fov-pitch5", 5.0, 1, "vertical")):
        try:
            size, rows = fit("empty-face", other, deg, axis)
        except Exception as e:
            print(other, "ERROR", repr(e)); continue
        good = [r["fov_deg"] for r in rows if r["fov_deg"] and r["ncc"] > 0.6 and r["margin"] > 0.05]
        print(f"\n{label} from {other} ({deg} deg), frame {size[0]}x{size[1]}")
        for r in rows:
            print("  ", r)
        if good:
            print(f"   -> {label} FOV median {np.median(good):.3f} deg over {len(good)} patches, spread {max(good) - min(good):.3f}")
    print("\nrule (setting 80, 16:9 base): horizontal 80.000, vertical 50.524;  setting 65: 65.000 / 39.420")
