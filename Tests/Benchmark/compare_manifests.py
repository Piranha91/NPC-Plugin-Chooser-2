"""Compare two discovery manifests written by DiscoveryBenchmark (NPC2_BENCH_MANIFEST=1).

  python Tests/Benchmark/compare_manifests.py <legacy-manifest.json> <optimized-manifest.json> [--json out.json]

Sections compared:
  faceGenCache.loose  - loose paths per source folder. The legacy cache also held every non-FaceGen mesh and
                        texture; those are reported separately as 'non-FaceGen paths no longer cached' (an
                        expected, intended difference) and checked to be outside facegeom/facetint.
  faceGenCache.bsa    - archive FaceGen paths per entry (must be identical).
  modSettings         - persisted entries: folders/plugins (precedence order), NPC FormKeys, FaceGen-only keys,
                        plugin ownership, flags, analysis snapshots (must be identical).
  inMemory            - every in-memory entry incl. mugshot-only ones (must be identical).
  npcCatalogue        - the NPC list (must be identical).
  caches, dialogs     - non-appearance / missing-master caches, warning text (must be identical).
Exit code 0 when every must-be-identical section matches.
"""
import json
import sys

FACEGEN_ROOTS = ("meshes/actors/character/facegendata/facegeom", "textures/actors/character/facegendata/facetint")


def is_facegen_tree(rel_path: str) -> bool:
    p = rel_path.lower()
    return any(p.startswith(root + "/") for root in FACEGEN_ROOTS)


def facegen_ish_legacy(rel_path: str) -> bool:
    """What the legacy consumers would accept: the FaceGenScanner 'Contains' rule with matching extension."""
    p = rel_path.lower()
    return (p.endswith(".nif") and FACEGEN_ROOTS[0] in p) or (p.endswith(".dds") and FACEGEN_ROOTS[1] in p)


def by_key(items, key="DisplayName"):
    return {i[key]: i for i in items}


def diff_keyed(a: dict, b: dict, limit=20):
    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    changed = sorted(k for k in set(a) & set(b) if a[k] != b[k])
    return {"onlyLegacy": only_a[:limit], "onlyOptimized": only_b[:limit], "changed": changed[:limit],
            "counts": {"onlyLegacy": len(only_a), "onlyOptimized": len(only_b), "changed": len(changed),
                       "legacy": len(a), "optimized": len(b)}}


def main():
    legacy = json.load(open(sys.argv[1], encoding="utf-8"))
    optimized = json.load(open(sys.argv[2], encoding="utf-8"))
    report = {"legacy": sys.argv[1], "optimized": sys.argv[2], "sections": {}}
    ok = True

    # --- loose ---
    lo, op = legacy["faceGenCache"]["loose"], optimized["faceGenCache"]["loose"]
    loose = {"foldersLegacy": len(lo), "foldersOptimized": len(op), "pathsLegacy": sum(map(len, lo.values())),
             "pathsOptimized": sum(map(len, op.values()))}
    missing_facegen, extra, dropped_non_fg, dropped_but_facegen_ish = [], [], 0, []
    for folder in sorted(set(lo) | set(op)):
        a, b = set(lo.get(folder, [])), set(op.get(folder, []))
        for p in sorted(a - b):
            if is_facegen_tree(p):
                missing_facegen.append(f"{folder} :: {p}")
            else:
                dropped_non_fg += 1
                if facegen_ish_legacy(p):
                    dropped_but_facegen_ish.append(f"{folder} :: {p}")
        extra += [f"{folder} :: {p}" for p in sorted(b - a)]
    # Folders the legacy cache listed only because of non-FaceGen files disappear from the optimized keys.
    folders_only_legacy = sorted(set(lo) - set(op))
    folders_only_legacy_with_facegen = [f for f in folders_only_legacy if any(is_facegen_tree(p) for p in lo[f])]
    loose.update({
        "faceGenPathsMissingFromOptimized": len(missing_facegen), "examplesMissing": missing_facegen[:20],
        "pathsOnlyInOptimized": len(extra), "examplesExtra": extra[:20],
        "nonFaceGenPathsNoLongerCached": dropped_non_fg,
        "droppedPathsLegacyConsumersWouldAccept": dropped_but_facegen_ish,
        "foldersOnlyInLegacyKeys": len(folders_only_legacy),
        "foldersOnlyInLegacyKeysThatHadFaceGen": folders_only_legacy_with_facegen,
    })
    loose_ok = not missing_facegen and not extra and not folders_only_legacy_with_facegen
    loose["identicalFaceGenSubset"] = loose_ok
    report["sections"]["faceGenCache.loose"] = loose
    ok &= loose_ok

    # --- bsa ---
    bl, bo = legacy["faceGenCache"]["bsa"], optimized["faceGenCache"]["bsa"]
    bsa = diff_keyed(bl, bo)
    bsa["paths"] = {"legacy": sum(map(len, bl.values())), "optimized": sum(map(len, bo.values()))}
    bsa["identical"] = bl == bo
    report["sections"]["faceGenCache.bsa"] = bsa
    ok &= bl == bo

    for section in ("modSettings", "inMemory"):
        a, b = by_key(legacy[section]), by_key(optimized[section])
        d = diff_keyed(a, b)
        d["identical"] = legacy[section] == optimized[section]
        if d["changed"]:
            k = d["changed"][0]
            d["firstChangedFields"] = sorted(f for f in set(a[k]) | set(b[k]) if a[k].get(f) != b[k].get(f))
            details = {}
            for k in d["changed"]:
                per_field = {}
                for f in sorted(set(a[k]) | set(b[k])):
                    va, vb = a[k].get(f), b[k].get(f)
                    if va == vb:
                        continue
                    if isinstance(va, dict) and isinstance(vb, dict):
                        va, vb = list(va), list(vb)
                    if isinstance(va, list) and isinstance(vb, list):
                        sa = {json.dumps(x, sort_keys=True) for x in va}
                        sb = {json.dumps(x, sort_keys=True) for x in vb}
                        per_field[f] = {"onlyLegacy": sorted(sa - sb)[:10], "onlyOptimized": sorted(sb - sa)[:10],
                                        "sameMembersDifferentOrder": sa == sb}
                    else:
                        per_field[f] = {"legacy": va, "optimized": vb}
                details[k] = per_field
            d["details"] = details
        report["sections"][section] = d
        ok &= d["identical"]

    a, b = by_key(legacy["npcCatalogue"], "FormKey"), by_key(optimized["npcCatalogue"], "FormKey")
    d = diff_keyed(a, b)
    d["identical"] = legacy["npcCatalogue"] == optimized["npcCatalogue"]
    report["sections"]["npcCatalogue"] = d
    ok &= d["identical"]

    for section in ("caches", "dialogs"):
        same = legacy[section] == optimized[section]
        report["sections"][section] = {"identical": same}
        ok &= same

    report["allMustMatchSectionsIdentical"] = ok
    text = json.dumps(report, indent=2)
    if "--json" in sys.argv:
        open(sys.argv[sys.argv.index("--json") + 1], "w", encoding="utf-8").write(text)
    print(text)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
