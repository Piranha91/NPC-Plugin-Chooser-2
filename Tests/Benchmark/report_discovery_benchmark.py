"""Turns DiscoveryBenchmark result files into comparison tables (Markdown) and a flat CSV of raw values.

  python Tests/Benchmark/report_discovery_benchmark.py <results-dir> [<results-dir> ...] --out <prefix>

For every scenario and metric: legacy median, optimized median, absolute and percentage change, min-max range,
and a two-sided exact permutation test of the difference in medians (all C(n+m, n) relabelings, so it is exact
for these small samples). 'clear' means p < 0.05 AND the ranges do not overlap; otherwise the change is reported
as 'within noise'. Counters are logical work counts (deterministic per variant) and are shown without a test.
"""
import argparse
import csv
import glob
import itertools
import json
import os
import statistics

TIME_METRICS = [
    ("wallMs", "Total population wall time", "ms"),
    ("cpuMs", "Process CPU time", "ms"),
    ("allocatedBytes", "Managed allocations (all threads)", "MB"),
    ("peakWorkingSetBytes", "Peak working set (process)", "MB"),
]
PHASES = [
    ("FaceGenCacheTotal", "FaceGen path cache (total)"),
    ("PreliminaryCandidates", "  preliminary entry discovery"),
    ("LooseFaceGenDiscovery", "  loose FaceGen discovery"),
    ("ArchiveAssociation", "  archive association (plugin -> BSA)"),
    ("ArchiveFaceGenIndexing", "  archive FaceGen indexing"),
    ("ModFolderScan", "Mod-folder scan"),
    ("Analysis", "Analysis (RefreshNpcLists etc.)"),
]
COUNTERS = [
    "LooseFoldersScanned", "LooseDuplicateFolderReferences", "LooseRootsEnumerated", "LooseCandidatePaths",
    "LoosePathsRetained", "PreliminaryCandidates", "TemporaryViewModelsConstructed", "ArchivesConsidered",
    "ArchivesProbed", "ArchivesProbeAbsent", "ArchivesProbePresent", "ArchivesProbeInconclusive",
    "ArchivesFullyIndexed", "ArchiveFoldersRead", "ArchiveFileEntriesEnumerated", "ArchivePathsRetained",
    "ArchiveListingCacheHits", "PluginDirectoryListings", "PluginLoads",
]


def load(dirs):
    runs = {}
    for d in dirs:
        for f in sorted(glob.glob(os.path.join(d, "result-*.json"))):
            r = json.load(open(f, encoding="utf-8"))
            if r["run"] == "seed":
                continue
            scenario = r["scenario"]
            if r.get("mutations") is not None and scenario == "refresh-mod":
                scenario = "refresh-mod (changed)"
            elif scenario == "refresh-mod":
                scenario = "refresh-mod (unchanged)"
            runs.setdefault(scenario, {}).setdefault(r["variant"], []).append(r)
    return runs


def perm_p(a, b):
    """Exact two-sided permutation p-value for |median(a) - median(b)|."""
    observed = abs(statistics.median(a) - statistics.median(b))
    pooled = a + b
    n = len(a)
    count = total = 0
    for idx in itertools.combinations(range(len(pooled)), n):
        s = set(idx)
        x = [pooled[i] for i in idx]
        y = [pooled[i] for i in range(len(pooled)) if i not in s]
        total += 1
        if abs(statistics.median(x) - statistics.median(y)) >= observed - 1e-12:
            count += 1
    return count / total


def fmt(v, unit):
    if unit == "MB":
        return f"{v / 1e6:,.1f}"
    return f"{v:,.0f}"


def row(label, a, b, unit, test=True):
    ma, mb = statistics.median(a), statistics.median(b)
    diff = mb - ma
    pct = (diff / ma * 100) if ma else float("nan")
    rng = f"{fmt(min(a), unit)}–{fmt(max(a), unit)} / {fmt(min(b), unit)}–{fmt(max(b), unit)}"
    if test and len(a) > 1 and len(b) > 1:
        p = perm_p(a, b)
        overlap = not (max(a) < min(b) or max(b) < min(a))
        verdict = "clear" if p < 0.05 and not overlap else "within noise"
        ptxt = f"{p:.3f}"
    else:
        verdict, ptxt = "", ""
    return (f"| {label} | {fmt(ma, unit)} | {fmt(mb, unit)} | {'+' if diff >= 0 else '−'}{fmt(abs(diff), unit)} "
            f"| {pct:+.1f}% | {rng} | {ptxt} | {verdict} |")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dirs", nargs="+")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    runs = load(args.dirs)

    md = []
    flat = []
    for scenario in ["cold", "warm", "refresh-all", "refresh-mod (changed)", "refresh-mod (unchanged)"]:
        if scenario not in runs or not {"legacy", "optimized"} <= set(runs[scenario]):
            continue
        L, O = runs[scenario]["legacy"], runs[scenario]["optimized"]
        md.append(f"\n### {scenario} — n = {len(L)} legacy / {len(O)} optimized\n")
        md.append("| Metric | Legacy median | Optimized median | Δ | Δ % | range (legacy / optimized) | p | verdict |")
        md.append("|---|---:|---:|---:|---:|---|---:|---|")
        for key, label, unit in TIME_METRICS:
            md.append(row(f"{label} ({unit})", [r["measured"][key] for r in L], [r["measured"][key] for r in O], unit))
        for key, label in PHASES:
            a = [r["measured"]["phases"][key]["ms"] for r in L]
            b = [r["measured"]["phases"][key]["ms"] for r in O]
            if max(a + b) > 0:
                md.append(row(f"{label} (ms)", a, b, "ms"))
        md.append("\n| Logical counter | Legacy | Optimized |\n|---|---:|---:|")
        for c in COUNTERS:
            a = sorted({r["measured"]["counters"][c] for r in L})
            b = sorted({r["measured"]["counters"][c] for r in O})
            if any(a + b):
                md.append(f"| {c} | {'/'.join(f'{x:,}' for x in a)} | {'/'.join(f'{x:,}' for x in b)} |")
        for variant, items in (("legacy", L), ("optimized", O)):
            for r in items:
                m = r["measured"]
                flat.append({"scenario": scenario, "variant": variant, "run": r["run"], "wallMs": m["wallMs"],
                             "cpuMs": m["cpuMs"], "allocatedBytes": m["allocatedBytes"],
                             "peakWorkingSetBytes": m["peakWorkingSetBytes"],
                             **{f"phase.{k}": m["phases"][k]["ms"] for k, _ in PHASES},
                             **{f"counter.{c}": m["counters"][c] for c in COUNTERS}})

    with open(args.out + ".md", "w", encoding="utf-8") as fh:
        fh.write("\n".join(md) + "\n")
    with open(args.out + ".csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(flat[0].keys()))
        w.writeheader()
        w.writerows(flat)
    print("\n".join(md))


if __name__ == "__main__":
    main()
