"""Equivalence across many runs: separates variant differences from run-to-run nondeterminism.

  python Tests/Benchmark/manifest_stability.py <results-dir> [--scenario cold] [--json out.json]

Every manifest is flattened into atomic facts, e.g. ("modSettings", "Base Game", "NpcFormKeys", "0B28A5:Skyrim.esm")
or ("npcCatalogue", "000D67:...", "DisplayName", "Vilja"). For each variant:
  stable   = facts present in every run of that variant
  unstable = facts present in some but not all runs (run-to-run nondeterminism)
The variants are equivalent when their stable sets agree and each variant's unstable facts are unstable (or
absent) in the other too. Facts in faceGenCache.loose outside facegeom/facetint are excluded: the legacy cache
holding every mesh/texture is the intended, separately reported difference (see compare_manifests.py).
"""
import argparse
import collections
import glob
import json
import os

FACEGEN_ROOTS = ("meshes/actors/character/facegendata/facegeom/", "textures/actors/character/facegendata/facetint/")


def facts(manifest):
    out = set()
    for folder, paths in manifest["faceGenCache"]["loose"].items():
        for p in paths:
            if p.lower().startswith(FACEGEN_ROOTS):
                out.add(("loose", folder, "", p))
    for entry, paths in manifest["faceGenCache"]["bsa"].items():
        out.add(("bsaEntry", entry, "", ""))
        for p in paths:
            out.add(("bsa", entry, "", p))
    for section in ("modSettings", "inMemory"):
        for m in manifest[section]:
            name = m["DisplayName"]
            for field, value in m.items():
                if isinstance(value, list):
                    if field in ("CorrespondingFolderPaths", "CorrespondingModKeys", "LockedFolderPaths",
                                 "MugShotFolderPaths"):
                        out.add((section, name, field, json.dumps(value)))  # order matters: whole list
                    else:
                        for v in value:
                            out.add((section, name, field, json.dumps(v, sort_keys=True)))
                elif isinstance(value, dict):
                    for k, v in value.items():
                        out.add((section, name, field, k + "=" + json.dumps(v, sort_keys=True)))
                else:
                    out.add((section, name, field, json.dumps(value)))
    for n in manifest["npcCatalogue"]:
        for field, value in n.items():
            out.add(("npcCatalogue", n["FormKey"], field, json.dumps(value)))
    for k, v in manifest["caches"]["CachedNonAppearanceMods"].items():
        out.add(("CachedNonAppearanceMods", k, "", v))
    out.add(("CachedMissingMasterMods", "", "", json.dumps(manifest["caches"]["CachedMissingMasterMods"], sort_keys=True)))
    for d in manifest["dialogs"]:
        out.add(("dialog", "", "", d))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results", nargs="+")
    ap.add_argument("--scenario", default="cold")
    ap.add_argument("--json")
    args = ap.parse_args()

    runs = collections.defaultdict(list)
    for d in args.results:
        for f in sorted(glob.glob(os.path.join(d, f"manifest-{args.scenario}-*.json"))):
            # manifest-<scenario>-<variant>-<run>.json; the scenario itself may contain '-'.
            variant = os.path.basename(f)[len(f"manifest-{args.scenario}-"):].split("-")[0]
            runs[variant].append((f, facts(json.load(open(f, encoding="utf-8")))))

    summary = {}
    stable, unstable = {}, {}
    for variant, items in runs.items():
        sets = [s for _, s in items]
        union = set().union(*sets)
        inter = set.intersection(*sets)
        stable[variant], unstable[variant] = inter, union - inter
        summary[variant] = {"runs": [f for f, _ in items], "facts": len(inter), "unstableFacts": len(union - inter)}

    report = {"scenario": args.scenario, "variants": summary}
    if {"legacy", "optimized"} <= set(runs):
        l_st, o_st = stable["legacy"], stable["optimized"]
        l_all, o_all = l_st | unstable["legacy"], o_st | unstable["optimized"]
        # A stable fact of one variant that the other never produced in any run is a real difference.
        real_only_legacy = sorted(l_st - o_all)
        real_only_optimized = sorted(o_st - l_all)
        # Stable in one, intermittent in the other: consistent with nondeterminism, reported for transparency.
        intermittent = sorted((l_st ^ o_st) - set(real_only_legacy) - set(real_only_optimized))
        report["realDifferences"] = {"stableInLegacyNeverInOptimized": real_only_legacy[:50],
                                     "stableInOptimizedNeverInLegacy": real_only_optimized[:50],
                                     "counts": [len(real_only_legacy), len(real_only_optimized)]}
        report["stableInOneIntermittentInOther"] = {"count": len(intermittent), "examples": intermittent[:30]}
        report["unstableExamples"] = {v: sorted(unstable[v])[:30] for v in unstable}
        unstable_entities = collections.Counter((f[0], f[1]) for v in unstable for f in unstable[v])
        report["unstableEntities"] = [f"{s} :: {e} ({n} facts)" for (s, e), n in unstable_entities.most_common(40)]
        report["equivalent"] = not real_only_legacy and not real_only_optimized
    text = json.dumps(report, indent=2, default=list)
    if args.json:
        open(args.json, "w", encoding="utf-8").write(text)
    print(text)


if __name__ == "__main__":
    main()
