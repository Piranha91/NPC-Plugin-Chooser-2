# Targeted FaceGen discovery — implementation, equivalence and benchmark (2026-10)

**Status:** implemented, uncommitted on top of `72814c9` ("Add same-name grouping option to the NPC list").
Raw data, tables and equivalence reports: [`docs/benchmarks/discovery-2026-10/`](benchmarks/discovery-2026-10/).

## Summary

Two discovery optimizations, measured against the unchanged implementation (kept as an internal oracle) on the full
`S:\Skyrim NPC Selection` instance:

1. **Loose FaceGen is discovered by walking only `meshes/actors/character/facegendata/facegeom` and
   `textures/actors/character/facegendata/facetint`**, instead of every `*.nif` under `Meshes` and every `*.dds` under
   `Textures`.
2. **Preliminary discovery uses plain data and cheap archive metadata:** the full scan describes each mod folder /
   synthetic entry as a `DiscoveryCandidate` record instead of constructing a temporary `VM_ModSetting`; each BSA's
   header and folder table are read to prove whether it contains FaceGen folders, and Mutagen's reader is asked for
   the file lists of only those folders. The new-folder scan rejects FaceGen-less folders before building a view model.

| Full instance (median of 6 fresh processes) | Cold caches | Warm caches | Refresh All |
|---|---:|---:|---:|
| FaceGen path cache phase | 2,067 → **424 ms (−79.5%)** | 2,013 → **464 ms (−77.0%)** | 1,577 → **364 ms (−76.9%)** |
| Total population (catalogue usable) | 47.9 → 46.5 s (−2.8%) | 12.6 → 10.9 s (−13.5%) | 46.6 → 44.3 s (−5.1%) |
| Peak working set | 3,333 → **2,879 MB (−13.6%)** | 2,183 → **1,827 MB (−16.3%)** | 4,411 → **3,759 MB (−14.8%)** |
| Managed allocations | −1.23 GB (−1.7%) | −1.00 GB (−7.6%) | −1.93 GB (−2.7%) |

The discovery phases improve clearly (every run of the optimized variant beats every legacy run). Total population
improves too, but in the cold case discovery is only ~4% of the work: ~34 s is the mod-folder scan's record analysis,
which these changes do not touch (see *Where the remaining time goes*). Discovery results and the populated
catalogue are equivalent; the only differences found are pre-existing run-to-run nondeterminism, present in the legacy
variant as well.

## Design

### Loose FaceGen (`BackEnd/FaceGenDiscovery.cs` — `DiscoverLooseFaceGen`)

* Resolves `actors/character/facegendata/facegeom|facetint` one level at a time below `Meshes`/`Textures`,
  case-insensitively, keeping on-disk casing, then enumerates recursively below each FaceGen root with the same
  `Directory.EnumerateFiles(…, "*.nif"|"*.dds", AllDirectories)` call the original used.
* Output strings are byte-identical to the original cache (relative, forward slashes, `Meshes`/`Textures` first
  segment, on-disk casing after it), keyed by the consumer's own folder string.
* **Textures are enumerated before meshes**, as before. This is observable: when a mod's facetint and facegeom plugin
  folders differ only in case (`Glenmoril.esm`/`glenmoril.esm`, `EMCompViljaSkyrim.esp`/`emcompviljaskyrim.esp` in
  this instance), `FaceGenScanner` keeps the first spelling it meets and that spelling is persisted in FaceGen-only
  FormKeys. The first pilot had the order reversed; the manifest comparison caught it (2 mods differed).
* The same physical folder referenced several times (different spelling, trailing separator) is enumerated once.
* Nested layouts the original kept (backup folders inside a plugin folder, files directly in `facegeom`) are still kept
  — the walk is recursive below the FaceGen root and the consumers decide what they mean.

**Consumers traced.** The loose cache has exactly three readers: `FaceGenScanner.CreateFaceGenScanResultFromCache`
(FaceGen-only NPC keys, "has FaceGen" prechecks — filters to facegeom/facetint itself), `VM_ModSetting.FaceGenExists`
(exact-path lookups), and the single-mod refresh's type check `allFaceGenLooseFiles.Any()`. The last one is the only
consumer that depended on non-FaceGen files: with the broad cache it meant "does any folder ship *any* mesh or
texture?", which keeps a plugin-less mod alive as a FaceGen-only entry on refresh. It now gets its own early-exiting
probe, `FaceGenDiscovery.AnyLooseMeshOrTexture`, over the same folders, so its behaviour is unchanged. (That this
check is broader than the initial scan's strict FaceGen precheck looks like a latent inconsistency; it is preserved,
not fixed, here.)

**Deliberate narrowing.** The original cache let `FaceGenScanner`'s substring rule accept files in a lookalike sibling
(`facegeom_old/...`) or in a data tree nested inside `Meshes` (`Meshes/x/meshes/actors/.../facegeom/...`). The game loads
neither, and the targeted walk does not read them. No mod in the reference instance has either layout (the manifest
check `droppedPathsLegacyConsumersWouldAccept` is empty); `LookalikeAndRelocatedTrees_AreNotFaceGen` pins the choice.

### Preliminary discovery and archives

* **Plain-data candidates.** `BuildFullScanDiscoveryCandidates` yields `(DisplayName, FolderPaths, ModKeys,
  IsAutoGenerated)` per mod directory plus Base Game / Creation Club — exactly the fields the temporary VMs were built
  to supply (the folder-VM constructor names the entry after the folder and takes the plugin list as given).
  Archive association still goes through `BsaHandler.GetBsaPathsForPluginsInDirs`, so ownership, plugin association,
  Data-folder handling for the synthetic entries and the per-entry keying of the result are unchanged.
* **Folder-metadata probe (`ProbeArchiveFolders`).** Mutagen 0.54's public `IArchiveReader` offers only
  `TryGetFolder(exactName)` and `Files` (which materializes every entry: a record, two `Lazy<>`s and a path string per
  file). Its internal `BsaReader` does read folder names lazily without the file-name table, but that surface is not
  public. Rather than reflect into Mutagen internals, the probe reads the documented SE/LE directory layout itself —
  header, folder records, and per-folder name blocks; never the file-name table or data — and returns
  `Absent` / `Present(folders)` / `Inconclusive`. It decodes names as Mutagen does (Windows-1252 for v105, UTF-7 for
  v104) so they can be passed straight to `TryGetFolder`.
* **"Definitely absent" vs "unreadable".** `Absent` is only returned when the folder table reconciles exactly with the
  header (folder count, total file count, total folder-name length, every name NUL-terminated, no duplicate names) for
  a version-104/105 archive with folder and file names. Anything else — other versions, missing name tables,
  truncation, inconsistent totals, duplicates, I/O errors, not a BSA — is `Inconclusive` and falls back to the original
  full enumeration, which also reproduces its errors (`Failed to read BSA archive: …`). A folder the probe listed but
  Mutagen cannot address also falls back. All 336 archives in the instance probe conclusively (146 absent, 189 present
  of the 335 considered).
* **Detail on demand.** For `Present` archives the reader comes from `BsaHandler.OpenBsaArchiveReaders(…, false)`,
  exactly as before — an uncached reader, or the shared cached one (no reference taken), so the reference-counted
  lifetime the renderer relies on is untouched — and only the FaceGen folders' files are materialized.
* **Listing cache.** Per-archive FaceGen listings are kept in-process, keyed by full path and validated by file size +
  last-write time — the same rule `ModStateSnapshot` uses for archives. Nothing new is persisted to disk.
* **Scope.** This answers FaceGen questions only. `BsaHandler`'s full per-archive asset index used by rendering and
  patching (`PopulateBsaContentPathsAsync`, `EnsureDataFolderArchivesIndexed`, the CharacterViewer adapter) is
  untouched and still indexes every archive it did before, including resource-only plugins' archives.
* **No deferred work.** Nothing moved to a later lookup: absent archives never needed FaceGen file lists, and present
  archives get theirs during discovery as before. The full asset index was already on demand and is unchanged, so no
  "first lookup" cost was introduced to measure.

### Plugins

Plugin relevance checks already run on Mutagen's lazy binary overlays (`PluginProvider`) and short-circuit on
`mod.Npcs`; a plugin without an `NPC_` group costs one overlay open. A header parser would duplicate that, so the
overlays were kept. The plugin-side change is that discovery no longer needs view models: plugin directory listings
(4,587 cold) and overlay opens (~26.3k cold) are the same in both variants (counters below).

### What was not changed

Rendering, merge policy and appearance-selection logic. `ContextualPerformanceTracer` was not used for the benchmark:
it allocates a `Guid` + node per call and is called per NPC inside `RefreshNpcLists`. The new `DiscoveryMetrics` is
off by default (one static read per call site), counts locally and publishes once per folder/archive.

## Environment and workload

* **Hardware:** AMD Ryzen 7 9800X3D (8C/16T), 64 GB RAM, Samsung 990 PRO NVMe (mods on `S:`, game on `C:`), Windows 11
  Pro 23H2 (10.0.22631.6199).
* **Runtime/build:** .NET 10.0.9 (SDK 10.0.301), Release, workstation GC, no debugger, Mutagen 0.54.0. Build/restore
  excluded (built once before any run).
* **Target:** `S:\Skyrim NPC Selection` (MO2 2.5.2), profile `NPC Test`. Data folder from `ModOrganizer.ini` `gamePath`
  (`C:\Games\Steam\steamapps\common\Skyrim Special Edition\Data`), verified per run; 97 active plugins resolved through
  `modlist.txt` priority, 0 unresolved. Auto-detection was **not** used: this machine's `%LOCALAPPDATA%` `plugins.txt`
  is empty, so it would silently have produced a vanilla-only load order.
* **Input counts:** 717 mod folders; 162,385 loose `*.nif`/`*.dds` under Meshes/Textures, of which 84,353 FaceGen in 578
  folders; 335 archives associated with entries' plugins (243 BSAs in mod folders, 75 GB, plus Data), 40,126 archive
  FaceGen paths; 1,416 in-memory / 619 persisted mod entries; NPC catalogue 12,509 (cold) / 12,518 (warm).
* **Settings:** copy of the Debug app's `Settings.json` (read-only source), with FaceFinder, FaceFinder caching,
  portrait-creator fallback, mugshot auto-update, live tiles, NPC descriptions, FaceGen analysis and all logging
  forced off for both variants. Mugshots folder `S:\Skyrim Mugshots` (read).
* **Isolation:** every run's `AppContext.BaseDirectory` is `Tests/bin/DiscoveryBench/bin`, so Settings.json,
  RaceEvalCache.json, ModIssuesCache.json and analysis logs are written only there. The instance is read-only; the
  refresh fixture is a copy under the bench root.
* **Cache state:** application caches as defined per scenario. The OS file cache was **not** flushed (no reboot):
  every run is a fresh *process* against a warm OS cache, so these are not cold-disk numbers.

| Scenario | Application cache state | Measured region |
|---|---|---|
| cold | no mod entries, no non-appearance/missing-master caches, no race/mod-issue cache files | `PopulateModSettingsAsync` + `SaveModSettingsToModel` + `NpcSelectionBar.InitializeAsync` |
| warm | persisted state from one cold seeding run (the legacy seed, used for both variants) | same |
| refresh-all | warm start (not measured), then | `RefreshAllModSettingsAsync` (includes ~1.1 s of fixed `Task.Delay`) |
| refresh-mod (changed / unchanged) | cold start of a 3-mod fixture copy, controlled change applied (or not) | `RefreshSingleModSettingAsync` on the target |

## Correctness

### Manifest equivalence on the real instance

Every measured run (plus the seeding runs) wrote a normalized manifest after its measured region: both discovery
dictionaries, every persisted and in-memory entry (folders and plugins in precedence order; NPC FormKeys, FaceGen-only
keys, plugin ownership, flags, analysis snapshots), the NPC catalogue, the non-appearance / missing-master caches and
any warning dialog text. `manifest_stability.py` flattens them into ~516k facts and separates facts stable across all
runs of a variant from facts that vary between runs of the *same* variant.

| Scenario | Runs (L/O) | Stable facts L / O | Real differences (stable in one, never in the other) |
|---|---|---|---|
| cold | 7 / 7 | 516,116 / 516,121 | **0 / 0** |
| warm | 6 / 6 | 516,219 / 516,219 | **0 / 0** |
| refresh-all | 6 / 6 | 516,126 / 516,138 | **0 / 0** |
| refresh-mod changed / unchanged | 5 / 5 each | pairwise | every pair identical in every section |

* **Loose cache:** for every folder, the optimized cache equals the legacy cache's facegeom/facetint subset exactly
  (strings, casing). The legacy cache additionally held 78,032 non-FaceGen paths (and 52 folder keys that had only
  such files); none of them is accepted by a legacy consumer.
* **Archive cache:** identical — 719 entries, 40,126 paths.
* **Refresh after a controlled change:** the target's own FaceGen-free archive is replaced by a FaceGen archive and a
  loose FaceGen pair is added; both variants go from 3 to 18 FaceGen-only NPCs, i.e. the cached listing was invalidated.
  Unchanged: 3 → 3, served from the listing cache (optimized).

**Pre-existing nondeterminism (explains every unstable fact).** A few NPCs are accepted in some runs and rejected in
others — `0B28A5:Skyrim.esm` (under Base Game, Twisted's High Poly NPC Overhauls, or USSEP) and
`000D67:EMCompViljaSkyrim.esp` under Vilja Re-Imagined SSE. It happens between two legacy runs as well. Cause:
`Auxilliary`'s race-validity cache stores `Valid` verdicts but never `InvalidNotInLoadOrder`, and a mod whose
own plugins supply a race caches it as valid. Whether a mod that lacks that race's plugin is analysed before or after
such a mod (parallel analysis order) decides whether its NPC passes — e.g. *"Discarded Vilja … because its race
(000D68:EMCompViljaSkyrim.esp) is not in the current load order"* appears only in some runs. It is analysis-stage
behaviour outside this change; it is reported here, not fixed.

`refresh-all`'s `modSettingsPersisted` read immediately after `RefreshAllModSettingsAsync` returns is 0 or 619 depending
on timing (the method does not save; an asynchronous save lands shortly after). The manifests, taken moments later,
hold 619 entries in all 12 runs.

### Tests

| Suite | Result |
|---|---|
| Baseline, unmodified `72814c9` (Release) | 2,689 tests: 2,685 passed, **4 failed** — golden combos 1–3 (`facetint/skyrim.esm/0001347a.dds` hash mismatch) and `RecordProvenanceDiagTests.RemoveOutputRecord_DropsTheRow` |
| After the change | 2,721 tests: 2,718 passed, **3 failed** — the same golden combos 1–3 with identical messages; `RemoveOutputRecord_DropsTheRow` passed (intermittent, unrelated) |
| New focused tests | 31 — `FaceGenDiscoveryLooseTests` (13), `FaceGenDiscoveryArchiveTests` (18), all passing |

Golden references were not regenerated. **What actually ran against real data:** golden combos 4–12 and
`SourceOracleTests` exercise the patcher with the instance's USSEP / AI Overhaul / appearance mod folders; most other
integration tests resolve an auto-detected vanilla+CC environment from the real Data folder, without the instance's
mods. The golden/oracle tests build their settings directly and do not run discovery, so they guard downstream patching
only. Opt-in tests that ran as no-ops in the suite: the discovery benchmark (only run via its driver), FaceFinder
live probes, memory and cache-ratio diagnostics, `validation\run.py`-dependent checks (14 SKIP lines in the
after-suite; listed in `benchmarks/discovery-2026-10/tests/`). The real-instance validation of discovery is the
benchmark and its manifests above, which fail rather than skip when the instance cannot be resolved.

## Results

Full tables with ranges and permutation p-values: [`comparison.md`](benchmarks/discovery-2026-10/comparison.md); raw
per-run values: [`comparison.csv`](benchmarks/discovery-2026-10/comparison.csv) and `raw/`. Verdict rule: *clear*
when the exact two-sided permutation test gives p < 0.05 **and** the two ranges do not overlap. With 6 runs per side the
smallest attainable p is 0.013.

### Cold (n = 6 / 6)

| Metric | Legacy | Optimized | Δ | Δ % | Verdict |
|---|---:|---:|---:|---:|---|
| Total population wall | 47,875 ms | 46,512 ms | −1,363 ms | −2.8% | p 0.013, ranges overlap (45.1–47.5 vs 47.4–48.7 s) |
| Process CPU | 40,438 ms | 39,445 ms | −992 ms | −2.5% | within noise (CPU spread 30–46 s) |
| Managed allocations | 71,935 MB | 70,701 MB | −1,234 MB | −1.7% | clear |
| Peak working set | 3,333 MB | 2,879 MB | −454 MB | −13.6% | clear |
| FaceGen cache phase | 2,067 ms | 424 ms | −1,643 ms | −79.5% | clear |
| · entry discovery (temp VMs → records) | 1,072 ms | 17 ms | −1,055 ms | −98.4% | clear |
| · loose FaceGen | 356 ms | 180 ms | −176 ms | −49.4% | clear |
| · archive association | 236 ms | 190 ms | −45 ms | −19.1% | within noise |
| · archive FaceGen indexing | 371 ms | 31 ms | −341 ms | −91.7% | clear |
| Mod-folder scan | 34,219 ms | 34,045 ms | −174 ms | −0.5% | within noise |
| Analysis | 7,695 ms | 7,738 ms | +43 ms | +0.6% | within noise |

### Warm (n = 6 / 6)

| Metric | Legacy | Optimized | Δ | Δ % | Verdict |
|---|---:|---:|---:|---:|---|
| Total population wall | 12,627 ms | 10,921 ms | −1,706 ms | −13.5% | p 0.013; one optimized run (12.46 s) overlaps the legacy range |
| Process CPU | 9,188 ms | 8,289 ms | −898 ms | −9.8% | within noise (p 0.019, overlap) |
| Managed allocations | 13,137 MB | 12,135 MB | −1,002 MB | −7.6% | clear |
| Peak working set | 2,183 MB | 1,827 MB | −356 MB | −16.3% | clear |
| FaceGen cache phase | 2,013 ms | 464 ms | −1,549 ms | −77.0% | clear |
| Mod-folder scan | 144 ms | 215 ms | +71 ms | +49% | within noise by the rule (p 0.013, overlap) — see limitations |
| Analysis | 6,471 ms | 6,234 ms | −237 ms | −3.7% | within noise |

### Refresh All after a warm start (n = 6 / 6)

| Metric | Legacy | Optimized | Δ | Δ % | Verdict |
|---|---:|---:|---:|---:|---|
| Refresh wall | 46,638 ms | 44,273 ms | −2,365 ms | −5.1% | p 0.013, ranges overlap by 0.1 s |
| Peak working set | 4,411 MB | 3,759 MB | −653 MB | −14.8% | clear |
| FaceGen cache phase | 1,577 ms | 364 ms | −1,213 ms | −76.9% | clear |
| · archive FaceGen indexing | 253 ms | 1 ms | −252 ms | −99.7% | clear (all 335 listings served from the in-process cache, re-validated by size + mtime) |

### Single-mod refresh on the fixture (n = 5 / 5)

Sub-millisecond phases on one entry; the whole refresh is ~0.2 s and indistinguishable between variants (all within
noise). The optimized unchanged refresh spends ~1 ms on the listing-cache hit vs ~0.4 ms for legacy's full read of the
37-entry archive — likely first-call JIT/stat cost; irrelevant at this size.

### Logical work counters (identical across runs of a variant unless shown as a range)

| Counter (cold, full instance) | Legacy | Optimized |
|---|---:|---:|
| Loose directories listed (derived from the tree, see `enumeration-volume.json`) | 14,998 | 6,835 |
| Loose candidate paths enumerated | 162,385 | 84,353 |
| Loose paths retained in the cache | 162,385 | 84,353 |
| Temporary `VM_ModSetting`s built for discovery | 719 | 0 |
| Archives considered | 335 | 335 |
| Archives probed (folder metadata) / absent / present / inconclusive | — | 335 / 146 / 189 / 0 |
| Archives fully indexed (every entry) | 335 | 0 (fallbacks: 0) |
| Archive folders opened for file lists | — | 391 |
| Archive file entries materialized | 567,541 | 40,126 |
| Archive FaceGen paths retained | 40,126 | 40,126 |
| Plugin directory listings | 4,587 | 4,587 |
| Plugin overlays opened | 26,292–26,306 | 26,287–26,301 |

These are logical counts (directories listed, names yielded, entries materialized), not physical disk I/O; enumerated
file names were not measured as bytes.

## Where the remaining time goes (not changed here)

Opt-in worker-time buckets inside the cold mod-folder scan (summed across ~16 concurrent folder tasks, so they include
lock waits and exceed wall time) put the dominant cost in record analysis, not discovery: the appearance-plugin check
(`PluginModifiesAppearanceAsync`, which looks up each overridden NPC in a master with a linear
`listing.Mod?.Npcs.FirstOrDefault(...)`) ≈ 180–190 s summed over 504 folders, and `CheckForInjectedRecords` ≈ 170–180 s
over 172 folders, versus ~4 s for the scan's temporary VMs. `PluginProvider.LoadPlugins` also creates overlays while
holding its global lock. Those are the next places to look for startup time; they are outside this change's scope.

## Limitations and regressions

* **Cold startup barely moves** (−2.8%, overlapping ranges) because discovery is ~4% of it on this workload; the
  measurable gains are the discovery phase itself, memory, and warm start.
* **This workload is FaceGen-heavy:** 84k of the 162k loose candidate paths are FaceGen, so targeted loose discovery
  halves rather than eliminates enumeration. Instances with large texture/mesh overhauls benefit more; an instance whose
  mods ship almost only FaceGen would see little from part 1.
* **Warm mod-folder scan +71 ms** (p 0.013, overlapping ranges). The scan code is identical in both variants and does
  not read the FaceGen cache on that path. It is not explained; one possibility (not verified — GC is not attributed
  per phase) is collections shifting into the scan once the discovery phase allocates less. The scenario total is
  still 1.7 s faster.
* **Present archives are read twice at the directory level** (probe, then Mutagen's own folder table on
  `TryGetFolder`). Measured indexing is still −88% to −92%; from a cold disk the second read hits the OS cache.
* **Listing-cache invalidation is size + last-write time**, like `ModStateSnapshot`: an archive replaced by a
  same-size file with an identical timestamp would be served stale for the session.
* **Corrupt archives:** an archive with a valid folder table but a corrupt file-name table is now reported FaceGen-free
  instead of failing the population (the original read every name). Any inconsistency the probe *can* see falls back.
* **The handwritten probe** covers BSA v104/v105 only (the Skyrim formats); other versions always use the full reader.
* **Not measured:** cold-disk behaviour (no cache flush/reboot), progress-dialog marshalling (benchmark runs with no
  splash reporter; with one, the per-candidate `Dispatcher.Invoke` remains in both variants), and BA2 archives.
* **Instrumentation in production code:** `DiscoveryMetrics` call sites and the legacy implementation remain in
  `VM_Mods` as an oracle behind `FaceGenDiscovery.UseLegacyImplementation` (never set by the app). Remove both once the
  oracle is no longer wanted.

## Reproduction

From the repository root, with no NPC2 instance holding the Release output:

```sh
python Tests/Benchmark/run_discovery_benchmark.py --scenarios cold,warm,refresh-all --runs 6
python Tests/Benchmark/make_refresh_fixture.py
python Tests/Benchmark/run_discovery_benchmark.py --no-build --runs 5 --scenarios refresh-mod --fixture-template Tests/bin/DiscoveryBench/fixture-template --fixture-root Tests/bin/DiscoveryBench/fixture --refresh-mod "Men of Glenmoril Refined - NPC Overhaul" --mutation-file Tests/bin/DiscoveryBench/fixture-mutation.tsv
python Tests/Benchmark/run_discovery_benchmark.py --no-build --runs 5 --scenarios refresh-mod --fixture-template Tests/bin/DiscoveryBench/fixture-template --fixture-root Tests/bin/DiscoveryBench/fixture --refresh-mod "Men of Glenmoril Refined - NPC Overhaul"
python Tests/Benchmark/report_discovery_benchmark.py <results-dir> [<results-dir> ...] --out docs/benchmarks/discovery-2026-10/comparison
python Tests/Benchmark/manifest_stability.py <results-dir> --scenario cold
python Tests/Benchmark/compare_manifests.py <manifest-legacy.json> <manifest-optimized.json>
python Tests/Benchmark/count_enumeration.py
dotnet test "Tests/NPC Plugin Chooser 2.Tests.csproj" -c Release --filter "FullyQualifiedName~FaceGenDiscovery"
```

Results reported here: `Tests/bin/DiscoveryBench/results-20261025-235629` (cold/warm/refresh-all + seeds),
`results-20261026-002557` (refresh-mod changed), `results-20261026-002904` (refresh-mod unchanged). Each run's ~40 MB
manifest stays there (git-ignored); the small per-run result files are copied under `docs/benchmarks/discovery-2026-10/raw/`.

## Acknowledgement

These two optimizations were prompted by examining how NPC Appearance Manager approaches discovery (scanning FaceGen
directories directly; cheap presence checks before detailed analysis). They were implemented in NPC2's own architecture
and dependencies without NAM's source code. This was not a clean-room process.
