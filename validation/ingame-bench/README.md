# NPC2 in-game bench

An end-to-end check of NPC2 in the real game. For a fixed set of 43 adult NPCs and the appearance mods of the frozen
corpus (`corpus/CORPUS.md`), it photographs
each NPC with the mod running alone (the answer key), then builds NPC2's output for twelve different settings
combinations ("arms"), launches the game with only that output, and compares what the game shows and reports with
the answer key. It was run in full for the 2.2.7 release on 2026-11-27 (results below).

This folder is a copy of the suite as it ran. The code lived outside the repository until then; the only changes
made in the copy are that every machine path now comes from `harness/bench_paths.py` (see "Machine paths").

## What it proves, and what it does not

It shows, for each subject and arm, whether the NPC in the game carries the head parts the mod gives it (read from
the running game by name), and whether the face, crown, neck and antler regions of its portrait match the mod's own
portrait within the measured repeat noise, both in its default outfit and after the outfit is swapped for a plain
one. It also covers: a save and reload (arms A4 and A6), a forwarded armature override on a race change (the
ArmaRace fixture), and whether each arm's output loads at all.

It does not prove:

- anything below the collarbone (no frame shows it, by rule);
- that a face "looks right" to a person: pictures are compared in pairs against the mod alone, never judged on who
  the NPC looks like;
- cells marked READ: these are outcomes reported, not predicted (contests with a runtime distributor such as SPID,
  Recast Only on a race change, and designed limits). Each READ cell carries its written reason;
- behaviour with mods, game versions or NPCs outside the corpus, or with an existing save;
- the NPC2 user interface: arms are built headless by a test (`Tests/Integration/Lab/BenchOutputBuilder.cs`).

## Capture rules (bind every run)

- Never photograph an NPC whose outfit was removed or whose body slot is empty. The outfit swap goes from one full
  outfit to another; a subject whose body slot reads empty is logged UNASSESSED and gets no picture.
- Every frame is framed from about the collarbone up by camera placement (the camera stands 40 units in front of the
  head bone, at head height). Never crop a wider shot.
- No child NPC is a subject, in any launch, frame or render. `bench_def.S` holds adults only.

## Layout

| Path | What it is |
|---|---|
| `harness/bench_def.py` | the single definition of subjects, mods, reference launches and arms |
| `harness/bench_run.py` | orchestration: install, profiles, headless NPC2 build, game launch, tiers and holds |
| `harness/bench_driver.py` | the in-game driver for one launch (spawn, telemetry, portraits, outfit swap, reload) |
| `harness/bench_score.py` | the scorer (offline): references, arms, the known-answer controls, the summary |
| `harness/bench_corpus.py` | corpus custody: freeze and verify |
| `harness/bench_paths.py` | every machine path, with an environment override each |
| `harness/backdrop.py`, `harness/backdrop_measure.py` | the opt-in backdrop probe (see below) |
| `harness/armarace_probe.py`, `armarace_cases.json`, `make_driver.py` | the fixture probe of 2026-11-24, kept as provenance (see "Known limits") |
| `harness/fov_fit.py` | offline field-of-view fit used to set the camera distance |
| `harness/profile_template/` | MO2 list files the profiles start from |
| `fixtures/ArmaRace-v2/` | the synthetic fixture mods the bench installs (its own README, recipe, SHA256SUMS) |
| `fixtures/ArmaRace/` | version 1 of the fixture (verified in game 2026-11-24), kept for its record |
| `corpus/CORPUS.md`, `corpus/corpus.json` | the corpus table: every mod folder with version, Nexus ID, archive and plugins; every NPC |
| `corpus/MANIFEST.json` | SHA-256, size and time of each of the 20553 corpus files (the check behind `verify`) |
| `corpus/npc2-settings-snapshot.json` | NPC2's own analysis of the corpus mods, frozen: every arm's `Settings.json` is derived from it |

Not committed (see `.gitignore`), but present beside the harness on the machine of the run: the fixture NPC's
FaceGen (`.nif`, `.dds`; Bethesda's own files, copied out of the game's archives, local use only) and the game
shortcut `harness/SKSE-NPCSelection.lnk` (machine-specific). To recreate them, see "Prerequisites".

In `corpus/CORPUS.md` and `corpus/corpus.json` one archive path was shortened to `<Downloads>/skse64_2_02_06.7z` to
leave a user name out; otherwise these files are byte copies of the frozen records.

## Prerequisites

- Windows, Skyrim Special Edition, Mod Organizer 2. The game version is not recorded in the corpus (not confirmed).
- An MO2 instance (default `S:\Skyrim NPC Selection`) with a profile named `Blank` (its INI files are copied into
  each bench profile `N2B-<launch>`), the QASmoke test cell (vanilla), and every mod folder named in
  `corpus/CORPUS.md` under `mods\`, with exactly those folder names and files. `bench_corpus.py verify` decides
  whether an instance matches. The 11 GB of corpus mods are not in the repository; on the machine of the run they
  are kept at `S:\NPC2 Bench Corpus\mods`, and High Poly NPC Overhaul and Nordic Faces keep only the subjects'
  FaceGen there (column "FaceGen pruned").
- For the two RaceMenu reference launches only: a mod folder `RaceMenu` in the instance. It is not part of the frozen
  corpus and its version is not recorded (not confirmed).
- The two FaceGen files of the fixture under `fixtures/ArmaRace-v2/mods/NPC2Bench - ArmaRace ModA/`
  (`meshes/.../facegeom/Skyrim.esm/00090738.nif`, `textures/.../facetint/Skyrim.esm/00090738.dds`): Christer's
  vanilla FaceGen from `Skyrim - Meshes0.bsa` and `Skyrim - Textures0.bsa`. Their SHA-256 are in that folder's
  `SHA256SUMS.txt`.
- A game shortcut (default `harness/SKSE-NPCSelection.lnk`): a Windows shortcut to the instance's
  `ModOrganizer.exe` with the argument `moshortcut://:SKSE`.
- NPC2 itself: this repository, built in Debug together with its test project
  (`dotnet build "Tests/NPC Plugin Chooser 2.Tests.csproj" -c Debug`). Arms are built with
  `dotnet test ... --no-build --filter FullyQualifiedName~BenchOutputBuilder`, so the build must be current.
- Python 3.12 with PIL and numpy; scipy for `backdrop_measure.py` only. The run used Python 3.12.6, Pillow 12.0.0,
  numpy 2.4.2, scipy 1.17.1.
- External code, not in this repository (from the houseCARL tooling on the machine of the run):

| Piece | Default location | Version used |
|---|---|---|
| `dbharness.py`, the DevBench bridge library (talks to the DevBench SKSE plugin in the game) | `S:\Dev\houseCARL\.claude\skills\devbench-in-game-testing\scripts` | file of 2026-11-26 07:10 (houseCARL commit `de767811` of 2026-11-25), SHA-256 `81858aa5b8d3...` |
| `wmi_launch.py`, starts MO2 outside the calling shell's job object | `S:\Dev\houseCARL\_claude\runs\2026-09-20-occlusion-transfer-test\driver` | file of 2026-10-29 16:10, SHA-256 `25228219f80d...` |
| `idle_lock.py`, the machine queue each game launch holds (`hold`, game-lock class) | `S:\Dev\houseCARL\_claude\tools\idle_lock.py` | changed after the run (file of 2026-11-27 15:56, SHA-256 `02f75227092d...`); the exact version the run used is not recorded |
| DevBench (SKSE plugin) | corpus mod folder `DevBench 1.39.0` | 1.39.0 |

## Machine paths

`harness/bench_paths.py` holds every path the code uses. Edit the defaults there, or set the variable:

| Variable | Default | Used for |
|---|---|---|
| `N2B_INSTANCE` | `S:\Skyrim NPC Selection` | the MO2 instance (`bench_def.INSTANCE`, the driver's `LAB`) |
| `N2B_NPC2_REPO` | `S:\Dev\NPC Plugin Chooser 2\NPC Plugin Chooser 2` | headless builds; `LIVE_SETTINGS` for `freeze` |
| `N2B_CORPUS` | `S:\NPC2 Bench Corpus` | `mods\`, `MANIFEST.json`, the settings snapshot, `corpus.json`, `CORPUS.md` |
| `N2B_RUN_ROOT` | `S:\Dev\houseCARL\_claude\runs\2026-11-25-npc2bench` | the run folder (`build\`, `live\`, `scores\`, `traces\`) |
| `N2B_CAPTURE_ROOT` | `C:\NPC2 Bench Captures` | frames, under `<root>\<run folder name>\<launch>\shots`; `.` puts them in the launch folder |
| `N2B_FIXTURE_SRC` | `S:\Dev\NPC Plugin Chooser 2\bench-fixtures\ArmaRace-v2\mods` | what `install` copies |
| `N2B_IDLE_LOCK` | `S:\Dev\houseCARL\_claude\tools\idle_lock.py` | the machine queue |
| `N2B_DEVBENCH_SCRIPTS` | `S:\Dev\houseCARL\.claude\skills\devbench-in-game-testing\scripts` | `dbharness.py` |
| `N2B_LAUNCH_DRIVER_DIR` | `S:\Dev\houseCARL\_claude\runs\2026-09-20-occlusion-transfer-test\driver` | `wmi_launch.py` |
| `N2B_SKSE_LNK` | `harness\SKSE-NPCSelection.lnk` | the game shortcut |
| `N2B_ARMARACE_RUN` | `S:\Dev\houseCARL\_claude\runs\2026-11-24-npc2bench-armarace` | `armarace_probe.py` only |
| `N2B_HOUSECARL_HARNESS` | `S:\Dev\houseCARL\_claude\harness` | `make_driver.py` only |
| `N2B_PYTHON` | the interpreter of the run | not used by the code (documentation) |

The repository copy of the fixture is `fixtures/ArmaRace-v2/mods`; set `N2B_FIXTURE_SRC` to it on another machine.
The default still names the folder the run installed from (same files, by SHA-256).

Other variables the harness reads (unchanged): `N2B_EXTRA_MODS`, `N2B_DRIVER_OPTIONS`, `N2B_SETTINGS_OVERRIDES`,
`N2B_BUILD_ROOT`, `N2B_HOLD_MAX_MIN`, `N2B_MIN_FREE_GB`, `N2B_TAKE_FIRST`, `N2B_EXEMPT`, `N2B_EXEMPT_TREES`; see the
docstrings of `bench_run.py` and `bench_score.py`.

## Reproducing a run

All commands run from `harness/` with the Python above (`py` below). MO2 must be closed. Each game launch takes the
machine queue's game lock through `bench_run.py hold`, which also keeps two launches from running at once. The
harness refuses a launch with under 8 GB free where it writes (frames about 3.4 GB, NPC2 output about 2.7 GB).

1. Point the paths at your machine (`bench_paths.py` or the variables), and pick a fresh run folder:
   `set N2B_RUN_ROOT=<new folder>` (PowerShell: `$env:N2B_RUN_ROOT = "<new folder>"`).
2. Install the fixture and check the instance has every tool, base and environment mod:
   `py bench_run.py install`
3. Check the corpus. It must read `verified 20553 files under <instance>\mods: intact`:
   `py bench_corpus.py verify` (add `--full` to hash every file; `--corpus` checks the corpus copy itself).
   `go` runs this check again before every launch and stops on any changed byte.
4. Check that no reference subject is supplied by two active mods: `py bench_run.py check`
5. References, one hold each (a failed launch is retried once inside the hold):

   ```
   py bench_run.py hold "reference V"  tier list:V
   py bench_run.py hold "reference R1" tier list:R1
   py bench_run.py hold "reference R2" tier list:R2
   py bench_run.py hold "reference R3" tier list:R3
   py bench_run.py hold "reference R4" tier list:R4
   ```

   Repeat launches (they measure the repeat noise the thresholds are scaled by):

   ```
   py bench_run.py hold "repeat V"  go V  --only Irileth,Anoriath,Auri,Lydia,Christer,Ulfric,Thorgar,Aela --tag repeat
   py bench_run.py hold "repeat R1" go R1 --only Irileth,Anoriath,Auri,Christer,Serana --tag repeat
   py bench_run.py hold "repeat R2" go R2 --only Ulfric,Urag --tag repeat
   py bench_run.py hold "repeat R4" go R4 --only Lydia,Vilja --tag repeat
   ```

   RaceMenu readings (the answer for a tinted converted wig), with `N2B_EXTRA_MODS=RaceMenu` set for these two only:

   ```
   py bench_run.py hold "R2 with RaceMenu" go R2 --only Briarheart,Velehk --tag racemenu
   py bench_run.py hold "R1 with RaceMenu" go R1 --only Auri --tag racemenu
   ```

   Then write the references table (`scores\references.json`): `py bench_score.py refs`
6. Arms, one hold each. `tier list:<arm>` writes the profile and the staged `Settings.json`, builds the arm with
   NPC2, launches it (one retry inside the hold), drops the output and scores it:

   ```
   py bench_run.py hold "arm A1" tier list:A1
   ...
   py bench_run.py hold "arm A12" tier list:A12
   ```

   An arm is rescored offline with `py bench_score.py arm A1` (writes `scores\A1\results.json`, `report.md`, sheets).
7. One table over every scored arm: `py bench_score.py summary` (writes `scores\SUMMARY.md`).
8. The known-answer controls: with `N2B_RUN_ROOT` unset, `py bench_score.py controls` must end
   `all controls behave: True`. They score stored launches of the first run folder
   (`2026-11-25-npc2bench`) with known answers injected, so they need that folder; they write
   `scores\controls\` there.

These are the commands of the 2.2.7 run (its chain scripts are `traces\phase5_refs.sh` and `traces\phase5_arms.sh`
in the run folder), with the purpose strings shortened.

## Reference launches

Generated from `bench_def.REFS` and `bench_def.MOD`. Every launch also enables the tools (SKSE, Address Library,
DevBench 1.39.0, powerofthree's Tweaks, SkyPatcher, Recast 1.6.3 and two small ones) and the base mods: the
Unofficial Patch, RaceCompatibility, High Poly Head, Song of the Green (adds Auri), Vilja in Skyrim (adds Vilja) and
the fixture's base mod. Faster HDT-SMP is added to every launch that loads an asset needing it (the FoxGlove Auri).

| Launch | Appearance mods active alone | Subjects photographed |
|---|---|---|
| V | (none: vanilla) | 43: every subject |
| R1 | The Ordinary Women SSE, Sons of Nirn - Whiterun, Seranaholic, FoxGlove - Auri Visual Overhaul (Classic Red), Vilja Re-Imagined SSE, NPC2Bench - ArmaRace ModA | 17: Irileth, Faralda, Carlotta, Saadia, Sybille, Danica, Anoriath, Elrindir, Belethor, Nazeem, Farkas, Eorlund, Auri, Serana, Vilja, Christer, Thorgar |
| R2 | High Poly NPC Overhaul (All Vanilla NPCs) | 13: Ulfric, PitFan, Cultist, Briarheart, Velehk, ArnielGane, ArnielShade, Urag, Rikke, RikkeTwin, Kharjo, Derkeethus, Shahvee |
| R3 | Nordic Faces (FaceGen with FaceTint), Bijin Warmaidens | 6: Ysolda, Hadvar, Olava, Delphine, Adelaisa, Aela |
| R4 | UBE Lydia, Shields Sister Npc Replacer (SkyPatcher), SOGS Vilja Visual Overhaul, Children of the Green | 7: Lydia, Aela, Vilja, Dervenin, Beleval, Nivenor, Anuriel |

## Arms

Generated from `bench_def.ARMS`. "Load order": Vanilla is the base set above; Patched adds AI Overhaul; Contested
adds SPID, the DX Druid Armor mods and an SPID outfit for Auri as well (a runtime distributor that competes with
NPC2 for the outfit). Antler modes are given for Sons of Nirn, Children of the Green and FoxGlove in that order. High
Poly NPC Overhaul is registered four times, once per wig mode, and its four subject groups rotate through the modes
from arm to arm (`hpno_group_mode`). Arms A1 to A8 photograph the 42 subjects other than Aela.

| Arm | Output | Recast mode | Patching | Load order | Auri wig | Wig tint | Antlers | Template NPCs | Overrides | Include Outfits | Subjects | Other |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| A1 | ESP | | Create | Vanilla | ForwardToSkin | Auto | Remove, ForwardToSkin, None | InheritFromTemplate | Include | off | 42 | Vilja from Vilja Re-Imagined |
| A2 | ESP | | CreateAndPatch | Patched | ConvertToHeadParts | Auto | Remove, ForwardToSkin, Remove | GiveEachNpcOwnCopy | IncludeAsNew | on | 42 | Vilja from SOGS |
| A3 | SkyPatcher | | Create | Vanilla | ForwardToOutfit | Auto | None, Remove, ForwardToSkin | GiveEachNpcOwnCopy | Ignore | on | 42 | Vilja from Vilja Re-Imagined |
| A4 | SkyPatcher | | CreateAndPatch | Patched | ForwardToSkin | Auto | Remove, ForwardToOutfit, Remove | InheritFromTemplate | Include | off | 42 | save and reload: Irileth, Belethor, Urag, Auri; Vilja from SOGS |
| A5 | Recast | Hybrid | Create | Vanilla | None | Auto | ForwardToSkin, None, ForwardToOutfit | InheritFromTemplate | IncludeAsNew | on | 42 | Vilja from Vilja Re-Imagined |
| A6 | Recast | Hybrid | CreateAndPatch | Patched | ConvertToHeadParts | Always | Remove, ForwardToSkin, Remove | GiveEachNpcOwnCopy | Ignore | on | 42 | save and reload: Irileth, Belethor, Urag, Auri; Vilja from SOGS |
| A7 | Recast | RecastOnly | Create | Vanilla | ForwardToOutfit | Auto | None, Remove, Remove | GiveEachNpcOwnCopy | Include | on | 42 | Vilja from Vilja Re-Imagined |
| A8 | Recast | RecastOnly | CreateAndPatch | Patched | ConvertToHeadParts | Never | ForwardToSkin, Remove, ForwardToSkin | InheritFromTemplate | IncludeAsNew | off | 42 | Vilja from SOGS |
| A9 | ESP | | CreateAndPatch | Contested | ForwardToOutfit | Auto | ForwardToOutfit (all three) | InheritFromTemplate | Include | on | 8 | every wig ForwardToOutfit |
| A10 | ESP | | CreateAndPatch | Contested | ForwardToOutfit | Auto | ForwardToOutfit (all three) | InheritFromTemplate | Include | on | 7 | every wig ForwardToOutfit; publishing forwarded outfits to distributors off |
| A11 | SkyPatcher | | CreateAndPatch | Contested | ForwardToOutfit | Auto | ForwardToOutfit (all three) | InheritFromTemplate | Include | on | 8 | every wig ForwardToOutfit; third-party distributor Shields Sister (SkyPatcher) active |
| A12 | ESP | | CreateAndPatch | Patched | ForwardToSkin | Auto | Remove, ForwardToSkin, Remove | GiveEachNpcOwnCopy | IncludeAsNew | on | 12 | split output (by gender, at most 8 NPCs per plugin, no auto ESL); Vilja from SOGS |

The small arms photograph Auri, Ulfric, Urag, Anoriath, Dervenin, Christer and Thorgar, and A9 and A11 also Aela.
Thorgar is an observer: never selected, he tells Include from Include As New on the fixture's armature.

## The 2.2.7 result

Run folder `S:\Dev\houseCARL\_claude\runs\2026-11-27-npc2bench-final` (frames under
`C:\NPC2 Bench Captures\2026-11-27-npc2bench-final\`), 2026-11-27, method digest `3c19fb2cd712`. NPC2 was the
Debug build of the tree later committed as `5a914a1` (whole test suite 3379 passed, 0 failed). Every arm loaded as
built with no workaround, no build excluded a subject, and every arm counts its first launch except A9 and A10,
which were rerun after a corpus fix (the SPID outfit for Auri pointed at armour ids of a plugin the corpus did not
enable, so the game stripped her and the driver rightly took no picture).

From `scores\SUMMARY.md` there:

| Arm | PASS | FAIL | UNASSESSED | READ |
|---|---|---|---|---|
| A1 | 51 | 0 | 0 | 6 |
| A2 | 50 | 0 | 0 | 7 |
| A3 | 49 | 0 | 0 | 8 |
| A4 | 55 | 0 | 0 | 6 |
| A5 | 47 | 0 | 0 | 10 |
| A6 | 54 | 0 | 0 | 7 |
| A7 | 48 | 0 | 0 | 9 |
| A8 | 48 | 0 | 0 | 9 |
| A9 | 8 | 0 | 0 | 5 |
| A10 | 8 | 0 | 0 | 4 |
| A11 | 8 | 0 | 0 | 5 |
| A12 | 15 | 0 | 0 | 0 |
| all | 441 | 0 | 0 | 76 |

The 31 known-answer controls behave (`bench_score.py controls`, 2026-11-27 14:49 to 14:52, run folder
`2026-11-25-npc2bench`, `scores\controls\CONTROLS.md`). Per-arm detail: `scores\<arm>\report.md` and
`results.json`; the references: `scores\references.json`; the work log of the run:
`S:\Dev\NPC Plugin Chooser 2\NPC2-bench-battle-plan.md` (outside the repository).

## Backdrop probe (opt-in, not part of the method)

`N2B_DRIVER_OPTIONS={"backdrop": true}` makes the driver also shoot each subject in front of a flat fog colour
(Papyrus `Cell.SetFogColor` and `SetFogPlanes` on QASmoke, restored and checked after each subject); measured offline
with `py backdrop_measure.py <launch folder>`. Run once, 2026-11-27, `go R1 --only Irileth,Auri,Anoriath,Eorlund
--tag backdrop` in the first run folder (`live\R1-backdrop-20261127-150342`). Findings:

- The fog route works: everything behind the subject draws as one flat colour, and the fog is put back each time.
- Against the keyed silhouette, the current mask misses 6.9% to 16.9%, but almost all of it is speckle inside the
  body: at most 1.0% lies more than 8 pixels from what it did find. No whole feature is lost on these four.
- The backdrop changes how the subject is drawn (darker before a brighter backdrop, cause not traced), so backdrop
  frames cannot be compared with today's references.
- Using it would be a method change: one brightness-matched keyed colour, references re-shot, thresholds and controls
  re-qualified. By the owner's ruling it does not gate 2.2.7.

## Known limits

- The corpus mods cannot be shipped: a reader has to install the exact archives named in `corpus/CORPUS.md` and
  pass `verify`. RaceMenu (two reference launches) and the game version are outside the frozen corpus.
- The controls read stored launches of the first run folder, which is not in the repository.
- The fixture's FaceGen and the game shortcut are not committed (see Layout).
- A subject in `bench_score.NO_PICTURE` (ArnielShade, a translucent animated ghost) is judged by telemetry only.
- A converted wig that should be tinted is verified only where a RaceMenu reading of its reference exists; otherwise
  its cell says the tint was not verified.
- Telemetry cannot tell an equipped helmet that is not drawn; the armature case rests on the picture (bald crown).
- The studio wall hides about a tenth of a dark subject's silhouette from the mask (see the backdrop probe).
- A distributor that replaces the whole outfit (SPID) takes a wig forwarded into the outfit with it; the contested
  arms report this as READ.
- `armarace_probe.py` and `make_driver.py` are kept as the record of the fixture probe of 2026-11-24. `make_driver.py`
  regenerates `armarace_probe.py` from a houseCARL driver into the houseCARL harness folder; do not run it.
- Item codes in comments (H5, N14 and similar) refer to the work log named above.
