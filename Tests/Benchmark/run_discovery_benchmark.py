"""Discovery benchmark driver: legacy vs targeted FaceGen discovery against a real MO2 instance.

Runs Tests/Integration/Benchmark/DiscoveryBenchmark.cs once per *fresh test-host process*, alternating the
two variants in a counterbalanced (ABBA) order, and aggregates the per-run JSON results.

Isolation: the Release test output is copied into <bench-root>/bin, which becomes AppContext.BaseDirectory of
every run, so Settings.json, RaceEvalCache.json, ModIssuesCache.json and the analysis logs the app writes land
there - never in the app's own bin/ folder or the MO2 instance. The MO2 instance's mods and game files are
read-only inputs; the 'refresh-mod' scenario mutates only a disposable fixture subset under <bench-root>.

Usage (from the repo root):
  python Tests/Benchmark/run_discovery_benchmark.py --scenarios cold,warm,refresh-all --runs 5
  python Tests/Benchmark/run_discovery_benchmark.py --pilot            # 1 run per variant, with manifests
  python Tests/Benchmark/run_discovery_benchmark.py --no-build ...     # reuse the existing Release build

Build/restore time is never measured: the build happens once, before any run, and each run's timing is taken
inside the test around the population call only.
"""
import argparse
import json
import os
import shutil
import statistics
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TEST_PROJECT = REPO / "Tests" / "NPC Plugin Chooser 2.Tests.csproj"
TEST_OUTPUT = REPO / "Tests" / "bin" / "Release" / "net10.0-windows10.0.19041.0"
TEST_DLL = "NPC Plugin Chooser 2.Tests.dll"
DEFAULT_SETTINGS = REPO / "bin" / "Debug" / "net10.0-windows10.0.19041.0" / "Settings.json"
CACHE_FILES = ["RaceEvalCache.json", "ModIssuesCache.json"]
VARIANTS = ["legacy", "optimized"]


def run(cmd, env=None, cwd=None, log=None):
    started = time.perf_counter()
    with open(log, "w", encoding="utf-8") if log else open(os.devnull, "w") as fh:
        proc = subprocess.run(cmd, env=env, cwd=cwd, stdout=fh, stderr=subprocess.STDOUT)
    return proc.returncode, time.perf_counter() - started


def stage_bin(bench_root: Path) -> Path:
    bin_dir = bench_root / "bin"
    if bin_dir.exists():
        shutil.rmtree(bin_dir)
    # Report folders other tests leave in the output are not needed (and some exceed MAX_PATH).
    shutil.copytree(TEST_OUTPUT, bin_dir, ignore=shutil.ignore_patterns(
        "TemplateMatrixReport", "MemoryReports", "Rejected NPCs", "RenderLogs", "LoadingErrors",
        "BenchmarkResults", "TestResults", "NPC Output*"))
    for name in CACHE_FILES + ["Settings.json"]:
        (bin_dir / name).unlink(missing_ok=True)
    for d in ["Rejected NPCs", "LoadingErrors", "BenchmarkResults"]:
        shutil.rmtree(bin_dir / d, ignore_errors=True)
    return bin_dir


def prepare_state(bin_dir: Path, settings_src: Path, seed_dir: Path | None):
    """Reset every app-written cache in the isolated bin, then stage the scenario's settings/caches."""
    for name in CACHE_FILES:
        (bin_dir / name).unlink(missing_ok=True)
    for d in ["Rejected NPCs", "LoadingErrors"]:
        shutil.rmtree(bin_dir / d, ignore_errors=True)
    if seed_dir is None:
        shutil.copy2(settings_src, bin_dir / "Settings.json")
    else:
        shutil.copy2(seed_dir / "Settings.json", bin_dir / "Settings.json")
        for name in CACHE_FILES:
            if (seed_dir / name).exists():
                shutil.copy2(seed_dir / name, bin_dir / name)


def bench_once(bin_dir, out_dir, variant, scenario, label, extra_env, manifest=False, save_settings=None):
    env = dict(os.environ)
    env.update({
        "NPC2_DISCOVERY_BENCH": "1",
        "NPC2_BENCH_VARIANT": variant,
        "NPC2_BENCH_SCENARIO": scenario,
        "NPC2_BENCH_OUT": str(out_dir),
        "NPC2_BENCH_RUN": label,
        "NPC2_BENCH_MANIFEST": "1" if manifest else "0",
    })
    env.update(extra_env)
    env.pop("NPC2_BENCH_SAVE_SETTINGS", None)
    if save_settings:
        env["NPC2_BENCH_SAVE_SETTINGS"] = str(save_settings)
    log = out_dir / f"log-{scenario}-{variant}-{label}.txt"
    code, host_s = run(["dotnet", "test", TEST_DLL, "--filter", "FullyQualifiedName~DiscoveryBenchmark",
                        "--logger", "console;verbosity=normal"], env=env, cwd=bin_dir, log=log)
    result_path = out_dir / f"result-{scenario}-{variant}-{label}.json"
    if code != 0 or not result_path.exists():
        raise SystemExit(f"benchmark run failed ({scenario}/{variant}/{label}); see {log}")
    data = json.loads(result_path.read_text(encoding="utf-8"))
    data["hostProcessSeconds"] = host_s
    result_path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    print(f"  {scenario:12s} {variant:9s} {label:8s} wall={data['measured']['wallMs']/1000:7.2f}s "
          f"cpu={data['measured']['cpuMs']/1000:7.2f}s alloc={data['measured']['allocatedBytes']/1e9:6.2f}GB "
          f"(host {host_s:.1f}s)", flush=True)
    return data


def make_seed(bin_dir, out_dir, settings_src, seed_root, variant):
    """Cold run whose populated Settings.json + caches become the 'persisted caches populated' state."""
    seed_dir = seed_root / f"seed-{variant}"
    seed_dir.mkdir(parents=True, exist_ok=True)
    prepare_state(bin_dir, settings_src, None)
    bench_once(bin_dir, out_dir, variant, "cold", "seed", {}, manifest=True,
               save_settings=seed_dir / "Settings.json")
    for name in CACHE_FILES:
        if (bin_dir / name).exists():
            shutil.copy2(bin_dir / name, seed_dir / name)
    return seed_dir


def summarize(results):
    def stats(values):
        values = sorted(values)
        q = statistics.quantiles(values, n=4) if len(values) >= 2 else [values[0]] * 3
        return {"n": len(values), "median": statistics.median(values), "min": values[0], "max": values[-1],
                "q1": q[0], "q3": q[2], "raw": values}

    out = {}
    for (scenario, variant), runs in sorted(results.items()):
        m = [r["measured"] for r in runs]
        entry = {
            "wallMs": stats([x["wallMs"] for x in m]),
            "cpuMs": stats([x["cpuMs"] for x in m]),
            "allocatedBytes": stats([x["allocatedBytes"] for x in m]),
            "peakWorkingSetBytes": stats([x["peakWorkingSetBytes"] for x in m]),
            "phases": {p: stats([x["phases"][p]["ms"] for x in m]) for p in m[0]["phases"]},
            "scanWorkSteps": {p: stats([x["scanWorkSteps"][p]["summedWorkerMs"] for x in m])
                              for p in m[0].get("scanWorkSteps", {})},
            "counters": {c: stats([x["counters"][c] for x in m]) for c in m[0]["counters"]},
        }
        out[f"{scenario}/{variant}"] = entry
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bench-root", default=str(REPO / "Tests" / "bin" / "DiscoveryBench"))
    ap.add_argument("--settings", default=str(DEFAULT_SETTINGS),
                    help="Source Settings.json (copied, never modified). Default: the Debug app's settings.")
    ap.add_argument("--scenarios", default="cold,warm")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--pilot", action="store_true")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--seed-variant", default="legacy",
                    help="Which variant's cold run seeds the persisted-cache scenarios (same seed for both).")
    ap.add_argument("--fixture-root", default=None, help="Disposable fixture mods folder for refresh-mod.")
    ap.add_argument("--refresh-mod", default=None, help="Display name refreshed by refresh-mod.")
    ap.add_argument("--mutation-file", default=None, help="Fixture mutation applied before refresh-mod.")
    ap.add_argument("--fixture-template", default=None,
                    help="Pristine copy of the fixture subset; restored into --fixture-root before each run.")
    args = ap.parse_args()

    bench_root = Path(args.bench_root)
    out_dir = bench_root / time.strftime("results-%Y%m%d-%H%M%S")
    out_dir.mkdir(parents=True, exist_ok=True)
    settings_src = Path(args.settings)

    if not args.no_build:
        print("Building Release (not timed)...", flush=True)
        code, secs = run(["dotnet", "build", str(TEST_PROJECT), "-c", "Release", "-nologo", "-v", "q"],
                         log=out_dir / "build.log")
        if code != 0:
            raise SystemExit(f"build failed; see {out_dir / 'build.log'}")
        print(f"  build took {secs:.0f}s (excluded)")
    bin_dir = stage_bin(bench_root)

    scenarios = [s.strip() for s in args.scenarios.split(",") if s.strip()]
    runs = 1 if args.pilot else args.runs
    meta = {"repo": str(REPO), "settingsSource": str(settings_src), "scenarios": scenarios, "runs": runs,
            "pilot": args.pilot, "python": sys.version, "started": time.strftime("%Y-%m-%dT%H:%M:%S")}
    try:
        meta["gitHead"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip()
        meta["gitStatus"] = subprocess.check_output(["git", "status", "--porcelain"], cwd=REPO, text=True)
    except Exception as ex:  # noqa: BLE001
        meta["gitError"] = str(ex)

    seed_dir = None
    if any(s in ("warm", "refresh-all") for s in scenarios):
        print("Seeding persisted caches...", flush=True)
        seed_dirs = {v: make_seed(bin_dir, out_dir, settings_src, out_dir, v) for v in VARIANTS}
        seed_dir = seed_dirs[args.seed_variant]
        meta["seedDirs"] = {k: str(v) for k, v in seed_dirs.items()}

    results = {}
    for scenario in scenarios:
        extra = {}
        if scenario == "refresh-mod":
            if not (args.fixture_root and args.refresh_mod and args.fixture_template):
                raise SystemExit("refresh-mod needs --fixture-root, --fixture-template and --refresh-mod")
            extra = {"NPC2_BENCH_MODS_FOLDER": str(Path(args.fixture_root).resolve()),
                     "NPC2_BENCH_REFRESH_MOD": args.refresh_mod}
            if args.mutation_file:
                extra["NPC2_BENCH_MUTATE"] = str(Path(args.mutation_file).resolve())
        print(f"Scenario {scenario}: {runs} run(s) per variant", flush=True)
        for i in range(runs):
            order = VARIANTS if i % 2 == 0 else list(reversed(VARIANTS))
            for variant in order:
                if scenario == "refresh-mod":
                    shutil.rmtree(args.fixture_root, ignore_errors=True)
                    shutil.copytree(args.fixture_template, args.fixture_root)
                prepare_state(bin_dir, settings_src, seed_dir if scenario in ("warm", "refresh-all") else None)
                label = f"r{i + 1}"
                # Every run writes a manifest (after its measured region), so equivalence can be judged
                # against the run-to-run variation of each variant, not a single pair.
                data = bench_once(bin_dir, out_dir, variant, scenario, label, extra, manifest=True)
                results.setdefault((scenario, variant), []).append(data)

    summary = summarize(results)
    (out_dir / "summary.json").write_text(json.dumps({"meta": meta, "summary": summary}, indent=2),
                                          encoding="utf-8")
    print(f"Results in {out_dir}")


if __name__ == "__main__":
    main()
