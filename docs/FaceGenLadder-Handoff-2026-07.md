# FaceGen forwarding ladder — session handoff (2026-07-27)

Written for a fresh reader with no context from the session that produced it. Nothing here is
committed; everything is unstaged working-tree changes plus machine-local artifacts.

**Read section 7 before trusting anything in sections 1–5.** Several confident-sounding causal
claims made during this session turned out to be wrong, and they are listed there explicitly so
they are not inherited as fact.

---

## 1. The original question

A patch run emitted:

> WARNING: For Casival (2AD373:3DNPC.esp), a FaceGen texture (.dds) was found in 'Interesting NPCs
> PLUS' but a mesh (.nif) was not. This may result in the 'brown face' bug.

The question asked was: when an appearance mod ships only one half of an NPC's FaceGen, does the
patcher forward the other half from the mod that originally added the NPC?

**Answer: no.** `AssetHandler.ScheduleCopyNpcAssets` derived both FaceGen paths from the donor's
FormKey and looked them up **only inside the selected mod** (`FindAssetSource` is ModSetting-scoped).
A miss was a silent no-op. What actually happened varied by mode:

- **Record mode** — the output overrides the NPC at its own FormKey, so the FaceGen path is
  unchanged and the game silently falls through to whatever wins that path in the load order.
  Usually the origin mod, but not guaranteed; NPC2 neither controlled nor checked it.
- **Face swap** (shared/guest appearance) — the destination is the *target's* path, so a missing
  half could never be filled by fallthrough. Donor tint on target geometry.
- **SkyPatcher** — the surrogate gets a brand-new FormKey in the output plugin, so nothing in the
  load order can fall through to its path at all.

## 2. What was designed

A five-row "ladder" deciding where each half comes from, measured at the **subject** — the end of
the donor's Traits template chain, because a templated NPC's own head data is inert and the engine
renders the terminus's face.

| Row | Mod ships (at subject's path) | Mesh | Tint |
|---|---|---|---|
| 1 | nif + dds | mod | mod |
| 2 | nif only | mod | origin, else winner, else none + warn |
| 3 | dds only, mod **has** a record | origin if head-part-compatible; else winner if compatible; else **abort** | mod |
| 4 | dds only, mod has **no** record | origin record + origin mesh | mod |
| 5 | neither | as row 4 | origin, else winner, else none + warn |

Supporting definitions:

- **Origin** — the mod that first defines the subject NPC. Resolved by **ModSetting, not ModKey**:
  assets for one plugin routinely live in a sibling's archive (Casival is filed under `3DNPC.esp`
  but stored in `3DNPC0.bsa`, owned by `3DNPC0.esp`), and a ModSetting groups a mod's plugins.
- **Winner** — the conflict-winning file at the subject's path in the live data folder, excluding
  NPC2's own prior output (otherwise a bad result validates itself on a re-run).
- **Compatible** — every geometry-bearing head part the final patched record resolves to has a
  baked shape of that EditorID in the mesh. This is the engine's own tint reconciliation; failing
  it is the dark-face bug. Only the **mesh** is gated on this — a tint is a flat texture with no
  shape names, so it has no such constraint.
- **Abort** — decided before any record is written, so nothing needs rolling back.

**Template flattening — SkyPatcher ONLY. Record mode deliberately does not flatten.**
This asymmetry is easy to miss and drives several observed behaviours, so state it plainly:

| Mode | Donor is Traits-templated | Outcome |
|---|---|---|
| SkyPatcher | `CreateSkyPatcherNpc` resolves the terminus, copies its appearance onto the surrogate, **clears Traits** (`SkyPatcherInterface.cs`) | surrogate owns its face; the user's selection is live |
| Record | `SyncTemplateInheritance` **mirrors** the donor's Traits state onto the output | output still inherits; the user's selection is **inert** |

Record mode only de-standalones in the opposite case: `Patcher.cs` clears Traits when the target is
templated and the **donor is not**. Choose a templated donor and it stays templated.

Rationale for flattening in SkyPatcher: proven in game that the engine resolves a templated NPC's
face to the **terminus's own FaceGen path**. Left inherited, the result is load-order-dependent
*and* not per-NPC — every NPC inheriting from one terminus resolves to a single shared path, so two
NPCs given different mods would be forced to look identical.

**That argument applies just as forcefully to record mode, and was not acted on there** — see §8
"Should record mode flatten too?". The `destinationOwnedByAnotherNpc` deferral in
`ScheduleCopyNpcAssets` exists purely to work around the shared-path problem that flattening would
dissolve.

## 3. What was built

All unstaged. Roughly: 6 new files, 8 modified.

**New**
- `BackEnd/FaceGenLadder.cs` — the ladder as a pure function (`Classify`). No I/O; presence,
  compatibility and chain status all arrive as inputs. This is the file to read first.
- `BackEnd/FaceGenLadderDiag.cs` — opt-in CSV collector (`LogFaceGenLadder.txt` trigger). 29
  columns per NPC including `LegacyAction`, so one run yields the before/after diff.
- `BackEnd/PatchVerifyRunner.cs` — headless verification harness (`PatchVerify.json` trigger).
- `Tests/Unit/FaceGenLadderTests.cs`, `Tests/Unit/AppearanceTerminusTests.cs`
- `Docs/Investigate-Aisha-Disembodied-Earrings.md` — parked, unrelated.

**Modified**
- `BackEnd/Patcher.cs` — `ComputeFaceGenDecisionAsync` (lazy probing: skips origin/winner disk
  probes for the ~96 % of NPCs whose mod ships both halves), early abort, skipped-NPC run summary.
- `BackEnd/AssetHandler.cs` — decision-driven FaceGen scheduling; read-only probes
  (`GetAssetPresence`, `FindOriginModSetting`, `WinningAssetExists`, `MaterializeAssetAsync`);
  `explicitSourceAbsolutePath` on `RequestAssetCopyAsync` so winner copies reuse the whole existing
  dedup/claim/post-process pipeline.
- `BackEnd/Auxilliary.cs` — `TryResolveAppearanceTerminus`, separating *untemplated* /
  *resolved* / *levelled terminus* / *unfollowable*, with an optional hop trace.
- `BackEnd/SkyPatcherInterface.cs` — template flattening.
- `BackEnd/ContextualPerformanceTracer.cs` — crash fix (see §5).
- `BackEnd/CharacterViewerHost/NpcMeshResolver.cs`, `NpcResolutionContext.cs`,
  `InternalMugshotGenerator.cs` — origin scope for the renderer (see §5).
- `Tests/Integration/NpcChooserHarness.cs` — `Lazy<FaceGenConsistencyAnalyzer>` registration.

## 4. How it was tested, and what came back

**Deliberate sequencing:** the harness was built *first*, as a pure classifier that reported the
row without changing behaviour. That produced a baseline over the real 8154-NPC load order before
any behaviour changed, and it is what caught three of the four bugs in §5.

Measured distribution (this load order):

| Row | Count |
|---|---|
| 1 — both halves | 7881 |
| 2 — nif only | 228 |
| **3 — dds only + record** | **0** |
| 4 — dds only, no record | 35 |
| 5 — neither | 6 |
| aborts | 4 |

**Row 3 has zero instances.** The most intricate branch — the compatibility gate and winner
fallback — is entirely unexercised on real data. It is implemented and unit-tested but has never
run against a real NPC. Treat it as unproven.

268 NPCs source differently from the pre-ladder behaviour. Confirmed concretely: Casival's origin
mesh is now extracted from `3DNPC0.bsa` into the output, where previously only the tint was copied.

**In-game pass.** 27 NPCs spawned via generated console bats across rows 1/2/4/5 and the aborts.
Comparison pages (machine-local, not in repo):
`C:\Users\Piranha\Downloads\NPC2 Screenshots\Comparison.html` and `Comparison-NoPatch.html`.
Result: everything rendered correctly and matched in-game **except Adrianne Avenicci** (§6).

## 5. Bugs found and fixed during verification

1. **`ContextualPerformanceTracer.Tracer.Dispose` crashed the app.** It indexed a dictionary that
   `Reset()` had cleared, throwing from a `finally`. Triggered by running a patch inside another
   traced scope. Fixed defensively — a diagnostic must never take the app down.

2. **The template chain walk started from the wrong record.** It re-resolved the donor's *FormKey*
   through the link cache, so the walk began at the load order's **winning override**, which can
   disagree with the record the user selected about whether the NPC inherits. The output carries
   the *donor's* inheritance, not the winner's. This misreported 18 healthy NPCs as unfollowable;
   with the abort wired up they would have stopped being patched. Now walks from the donor record.
   Aborts went 22 → 4.

3. **The mugshot gate was mod-scoped** (`NpcMeshResolver.FaceGenExists`), so it refused to render
   every row-2/4/5 NPC — exactly the population the ladder serves. 37 render failures.

4. **The renderer's asset chain could not reach the origin mod**, which is what actually caused
   headless bodies. Two important details for anyone touching this again:
   - FaceGen loading uses `TryLocateInScopedBsa` **exclusively**; the broad `TryLocateInBsa` is
     never called (verified: 0 calls in a `BsaContentsDiag` run). So broadening the *gate* alone
     makes things worse — it promises what the loader cannot deliver, and the renderer draws a
     headless body instead of skipping cleanly.
   - The loader's chain is **`BuildResolutionScopes`** (fed to `OffscreenRenderRequest.AdditionalScopes`),
     **not `BuildDiagnosticScopes`** — that name is literal and only the gate/diagnostics use it.
     Changing it alone does nothing.

   Fix: an origin tier in `BuildResolutionScopes`, between vanilla and the selected mod. Verified —
   all missing-asset counts cleared, heads render, and Kaidan's unrelated-looking artefacts (hair
   occluded, face hue off, tint apparently missing) were the same five missing textures and cleared
   with it.

## 6. Open problems

### 6a. Adrianne Avenicci — UNRESOLVED, and the leading theory was disproven

**Symptom:** in game, missing eye and hair textures. The only in-game rendering defect observed
across the whole 27-NPC pass.

`Adrianne Avenicci (013BB9:Skyrim.esm)`, mod **Bijin AIO De-Standalone**, ladder row 2
(mod ships the mesh, tint from origin).

**What was tried and is wrong.** The session changed row 2's tint preference from origin-first to
winner-first, reasoning that `Bijin AIO De-Standalone` ships no tint and leans on its sibling
`Bijin NPCs` (349 KB vanilla tint was being copied over a 5.5 MB Bijin one). A re-run showed the
symptom **unchanged**, and the reasoning does not hold:

- Hair, brows and eyes are **separate HeadPart records with their own TextureSets**. They are not
  regions of the face tint. No choice about which `.dds` gets copied to the facetint path could
  affect them.
- Origin-first is the correct shape anyway: a mod shipping one half signals it expects the origin's
  counterpart. Winner-first makes the result depend on whatever else is installed.

**That change has been reverted** (row 2 and row 5 tint ordering, plus tests). Current behaviour is
origin-first, as originally designed.

**Where to actually look.** The failure is in **non-FaceGen asset forwarding** — the TextureSets
referenced by the NPC's hair/brow/eye HeadPart records, pulled in by
`GetAssetPathsReferencedByPlugin` / `PostProcessCopiedFile`, not by the ladder at all. Two specific
candidates:
1. **Base-game-overwrite protection.** `ShouldSkipAsBaseGameOverwrite` skips any asset landing on a
   vanilla path unless the mod has `OverwriteBaseGameAssets` ticked. FaceGen is exempt; head-part
   textures are **not**. Check whether Bijin AIO De-Standalone has it enabled.
2. **FaceGen-only selections.** Adrianne's row-2 entry may have `HasPluginRecord=False`, in which
   case no plugin records are traversed for asset discovery and only the FaceGen pair is copied.

**The right tool is `AssetProvenance.csv`** (`LogAssetProvenance.txt` trigger, or the Settings >
Logging checkbox). It records every copied file, its source, and — critically — a
`SkippedBaseGameOverwrite` marker. It was never enabled during this session. Enable it, re-run,
and filter to `00013BB9` and Bijin paths. That answers the question directly instead of by theory.

### 6b. Orc "Adventurer" mugshots disagree with the game — UNRESOLVED

Two orc Adventurers (`083279`, `0C176B`, both Skyrim.esm, High Poly NPC Overhaul 2.0, row 5).
Mugshots show yellow warpaint on the nose; in game, red warpaint on the brows. Both pairs are
internally identical, so it is systematic, not a mix-up.

Notable: these renders report **no missing assets at all**, so it is *not* a resolution failure —
a different mechanism from §5.4. Also, the ladder copies **nothing** for them
(`nif=WinnerInPlace, dds=WinnerInPlace`), so in game is purely the load order. That means the
renderer and the game are reading different files for a reason not yet established. Unaffected by
the origin-scope fix.

### 6c. Aisha's disembodied earrings

Parked deliberately. See `Docs/Investigate-Aisha-Disembodied-Earrings.md`.

## 7. Corrections — claims made during this session that turned out to be WRONG

Listed because they were stated with confidence and would mislead if inherited.

| Claim | Reality |
|---|---|
| The 18 unfollowable NPCs were levelled-list termini | No — their chains end in ordinary NPCs, one clean hop. The walk started from the wrong record (§5.2). |
| Headless mugshots were post-patch BSA cache poisoning (the `67feb41` class) | No — reproduced with **no patch run at all**. A genuine scope gap (§5.4). |
| The render path falls through to `TryLocateInBsa`, so broadening the gate is safe | No — that method is called **zero** times. Broadening the gate alone caused the headless bodies. |
| Adding the origin to `BuildDiagnosticScopes` fixes the loader | No — the loader uses `BuildResolutionScopes`. |
| Row 2's tint should prefer the winner; that explains Adrianne | No — disproven by re-run, and hair/brow/eye textures are not part of the face tint at all (§6a). Reverted. |

Pattern worth noting: each of these was a plausible mechanism asserted before the decisive
measurement existed. The measurements were cheap in every case. Prefer the log.

## 8. What remains

**Verification gaps**
- **SkyPatcher flattening has never been exercised in game.** All runs were record mode. Run
  `PatchVerify.json` with `"ModeOverride": "SkyPatcher"` against a templated donor.
- **Row 3 has zero real instances**, so the compatibility gate and winner fallback are unproven
  outside unit tests.
- Face-swap (shared/guest appearance) mode was never verified in game either.

**Open design decision — should record mode flatten templated NPCs too?**

Currently no: record mode mirrors the donor's inheritance (§2), so selecting a mod for a
Traits-templated NPC is **inert** — the game renders the terminus's face instead. The NPC menu
already warns about this, and §6b is a worked example (the orc Adventurers show Lawless's face, not
the High Poly one selected for them).

The case for flattening record mode as well:
- It is the same argument that decided SkyPatcher. Inheritance cannot give two NPCs sharing a
  terminus different appearances; flattening can.
- In record mode the output overrides the NPC at its **own** FormKey, so a flattened NPC gets its
  own FaceGen at its own path. That dissolves the shared-terminus problem outright and would make
  the `destinationOwnedByAnotherNpc` deferral unnecessary.
- It makes a user's selection mean what they expect — "the mod I picked is what I see".
- **395 NPCs** have `ChainStatus=Resolved` in this load order, so the population is not niche.

The case against:
- Diverges further from vanilla record structure than mirroring does.
- Duplicates FaceGen per NPC rather than sharing one terminus copy (the same cost accepted for
  SkyPatcher).
- Changes what existing saved selections produce, so it is a behaviour change for current users.

This was never explicitly decided — flattening was scoped to SkyPatcher during design and record
mode simply kept its existing mirroring. Decide it deliberately rather than by omission.

**Planned but not done**
- **OutputValidator alignment.** The 4 deliberately-aborted NPCs will surface in Validate Output as
  selection mismatches with no indication the skip was intentional. Also
  `OutputValidator.CheckFaceGen`'s silent early-return at the `!subjectExists && sourcePath == null`
  branch should probably become a real finding now that "no FaceGen anywhere" is a defined condition.
- **Menu exclusion for zero-head-part NPCs.** The 4 aborts are VIGILANT monsters (headless, or
  wearing non-removable helmets) that are offered because their custom races carry `ActorTypeNPC`
  — which the mod author sets correctly, for dialogue/faction/targeting. The better discriminator
  is `HeadParts.Count == 0`: they have no face by construction. Would need a carve-out for
  Traits-templated NPCs, which legitimately have an empty list of their own. **Not implemented —
  this changes menu inclusion and therefore saved selections, so it needs a decision.**
- `ChangesHeadDataThatNeedsFaceGen` in `Patcher.cs` is now **dead code** — referenced only by its
  own tests. Its job is row 3's. Left in place rather than deleted unasked.

## 9. Reproducing the harness

Both triggers live next to the exe and must be run **through MO2** so the virtual file system is
visible (a direct launch sees the raw Steam load order).

- **`PatchVerify.json`** (template at `PatchVerify.json.template`) — runs a full patch against live
  settings into a throwaway output mod, then writes `_PatchVerify/FaceGenLadder.csv`,
  `VerifyManifest.html` (each NPC beside its reference mugshot), and `verify_*.txt` console spawn
  bats with correct runtime FormIDs. `SamplePerRow` of 0 means "all of that row".
- **`RenderHarness.json`** — renders listed mugshots with **no patch run**, which is the control for
  any "is this post-patch state?" question.
- **`LogBsaDiag.txt`** (empty file) — per-asset BSA hit/miss. Note: it existed as
  `LogBsaDiag.txt.txt` for an unknown period and therefore never fired; check the name.
- **`LogAssetProvenance.txt`** — needed for §6a. Never enabled this session.

Delete triggers after use; `PatchVerify` regenerates its own spawn bats but the patch output mod
accumulates otherwise.

Generator for the comparison pages is machine-local at
`…/scratchpad/CompareShots/` (a small standalone .NET project); it reads the CSV and the render
directory, so it can be re-run after any new render pass.

## 10. State of the tree

Nothing committed. `dotnet build` clean; **1763 tests passing**, including 33 for the ladder and 8
for terminus resolution. The row-2/row-5 tint revert is included in that count.
