"""Logical directory/path counts of the loose FaceGen enumeration each implementation performs.

  python Tests/Benchmark/count_enumeration.py [--mods "S:\\Skyrim NPC Selection\\mods"]

Recursive Directory.EnumerateFiles hides how many directories it lists, so this derives it from the on-disk
tree (it is not a timing and not physical I/O):
  legacy    - lists every directory below <mod>\\Textures and <mod>\\Meshes; yields every *.dds / *.nif there.
  optimized - lists <mod>\\Textures|Meshes, then actors, character and facegendata one level at a time (each a
              single filtered listing of its parent), then every directory below facetint / facegeom; yields
              *.dds / *.nif only there.
"""
import argparse
import json
import os


def child(parent, name):
    try:
        for e in os.scandir(parent):
            if e.is_dir() and e.name.lower() == name:
                return e.path
    except OSError:
        pass
    return None


def walk_count(root, ext):
    dirs = files = 0
    for dp, dn, fn in os.walk(root):
        dirs += 1
        files += sum(1 for f in fn if f.lower().endswith(ext))
    return dirs, files


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mods", default=r"S:\Skyrim NPC Selection\mods")
    args = ap.parse_args()
    legacy = {"directoriesListed": 0, "pathsYielded": 0}
    optimized = {"directoriesListed": 0, "pathsYielded": 0}
    mods = 0
    for m in sorted(os.listdir(args.mods)):
        mod = os.path.join(args.mods, m)
        if not os.path.isdir(mod):
            continue
        mods += 1
        for top, leaf, ext in (("textures", "facetint", ".dds"), ("meshes", "facegeom", ".nif")):
            top_dir = child(mod, top)
            if not top_dir:
                continue
            d, f = walk_count(top_dir, ext)
            legacy["directoriesListed"] += d
            legacy["pathsYielded"] += f
            cur = top_dir
            for seg in ("actors", "character", "facegendata", leaf):
                optimized["directoriesListed"] += 1  # filtered listing of `cur` looking for `seg`
                cur = child(cur, seg)
                if not cur:
                    break
            if cur:
                d, f = walk_count(cur, ext)
                optimized["directoriesListed"] += d
                optimized["pathsYielded"] += f
    print(json.dumps({"modFolders": mods, "legacy": legacy, "optimized": optimized}, indent=2))


if __name__ == "__main__":
    main()
