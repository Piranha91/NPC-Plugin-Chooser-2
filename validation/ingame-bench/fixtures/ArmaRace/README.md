# ArmaRace fixture — override handling, armature race

Synthetic mod pair for the NPC2 in-game pre-release check. Built 2026-11-24 with houseCARL 2.0.4
(`housecarl_copy`, `housecarl_apply`, `housecarl_place`) against a vanilla-only scratch MO2 profile,
checked with `housecarl_check` (0 dangling refs, 0 missing masters) and read back with `espdump`.

**Verified in the game on 2026-11-24** (third launch; see "In-game check"). The mods themselves work as
intended. What NPC2 does with them has not been run yet.

## What it tests

A replacer changes an NPC's race *and* edits an armature the NPC's outfit uses so that it fits the new
race instead of the old one. If NPC2 forwards the race but not the armature override, the armor piece
stops rendering on that NPC. The piece is a helmet, so the failure shows above the collarbone and never
exposes the body.

## The two mods

`mods/NPC2Bench - ArmaRace Base` — plays the load order. Active in every launch.

| Record | FormKey | Content |
|---|---|---|
| ArmorAddon `N2B_IronHelmetAA_NordOnly` | `000800:NPC2Bench_ArmaRace_Base.esp` | Clone of `IronHelmetAA`. Race = NordRace, no additional races (vanilla lists DefaultRace + 21) |
| Armor `N2B_IronHelmet` | `000801` | Clone of `ArmorIronHelmet`, armature = the addon above only |
| Outfit `N2B_HelmetOutfit` | `000802` | Bar keeper clothes + shoes + the helmet |
| NPC Orgnar | `013479:Skyrim.esm` (override) | DefaultOutfit = `N2B_HelmetOutfit` |
| NPC Hod | `01347D:Skyrim.esm` (override) | DefaultOutfit = `N2B_HelmetOutfit` (control: Mod A never touches him) |

`mods/NPC2Bench - ArmaRace ModA` — the appearance mod. Active only in the reference launch; in a test
launch NPC2 reads it as a mod folder and its plugin is off.

| Record | FormKey | Content |
|---|---|---|
| Race `N2B_NordCloneRace` | `000800:NPC2Bench_ArmaRace_ModA.esp` | Clone of NordRace (Update.esm's version). ArmorRace = DefaultRace |
| NPC Orgnar | `013479:Skyrim.esm` (override) | Race = `N2B_NordCloneRace`; outfit unchanged |
| ArmorAddon `N2B_IronHelmetAA_NordOnly` | `000800:NPC2Bench_ArmaRace_Base.esp` (override) | Race = `N2B_NordCloneRace`, additional races = DefaultRace. Nord is out |
| FaceGen | `00013479.nif` / `.dds` | Orgnar's vanilla FaceGen, copied out of the game's BSAs so the mod ships a face |

Masters: Base → Skyrim.esm, Update.esm. Mod A → Skyrim.esm, Update.esm, NPC2Bench_ArmaRace_Base.esp.

### Armor race substitutes; it does not add (measured)

When a race has an ArmorRace, the engine checks an armature's race list against **that** race, in place of the
actor's own race. Launch 2 showed it: Orgnar was in the clone race, the armature named only the clone race, and
the helmet did not render. Adding the clone's armor race (DefaultRace) to the armature fixed it in launch 3.

So the design is:

- The clone's ArmorRace is DefaultRace. Vanilla clothing and skin armatures name DefaultRace as their primary
  race (checked: `BarKeeperTorso01AA`, `BarKeeperShoes01AA`, `NakedTorso`, `NakedHands`, `NakedFeet`), so the clone
  keeps its body and clothes in every case.
- Base's helmet armature names NordRace only, so a clone-race actor cannot wear it.
- Mod A's edit puts the clone race and DefaultRace on that armature and takes Nord off.

Consequence for scoring: under Include, the picture shows whether the armature override was forwarded, but the
helmet renders because of the DefaultRace entry. A wrongly remapped link to the cloned race would not show in
the picture; check that link in the output record.

## Expected

| Launch | Orgnar's helmet | Hod's helmet | Status |
|---|---|---|---|
| Base only | Shows | Shows | Not run; Sigurd in the probe is the same case and shows |
| Reference: Base + Mod A | Shows | Gone (Mod A took Nord off the armature) | **Seen in game, launch 3** |
| NPC2, overrides Ignore | **Gone** — race forwarded, armature edit not | Shows | Alvor in the probe is the same case: gone, seen in game |
| NPC2, overrides Include | Shows | Gone, as in the reference | Prediction |
| NPC2, overrides Include As New | Shows | Shows (Orgnar gets a private copy) | Prediction |

The Ignore row is the control that proves the check can see this failure. Hod separates Include from
Include As New.

Telemetry cannot see this failure through worn forms: all four probe subjects read back the helmet as equipped
in the hair and circlet slots, including the three it did not render on. A helmet that is equipped but not
drawn still hides the hair, so the tell is a bald crown.

## In-game check (2026-11-24)

`probe/` adds a third plugin so one launch covers all four combinations of actor race and helmet armature.

| Subject | Actor race | Helmet armature fits | Launch 2 (Mod A as first built) | Launch 3 (Mod A fixed) |
|---|---|---|---|---|
| Orgnar | clone | Mod A's edit | **no helmet** (edit named the clone race only) | helmet shows |
| Hod | Nord | Mod A's edit | no helmet | no helmet |
| Alvor | clone | Nord only | no helmet | no helmet |
| Sigurd | Nord | Nord only | helmet shows | helmet shows |

- Launch 1 stopped at the first portrait: the shell's default Python had no PIL. Use a
  Python 3.12 that has PIL and numpy.
- Orgnar's face renders normally under the clone race.
- Alvor stood facing away in every frame; his helmet state is clear from behind, his face was not checked.
- Run from the Skyrim NPC Selection instance (DevBench 1.39.0 copied in for the run, then removed again along
  with the mods, the profile and everything the launches added to the overwrite folder).
- Evidence: `S:\Dev\houseCARL\_claude\runs\2026-11-24-npc2bench-armarace\` (RESULT.md, logs, frames, recordings).
- Driver: `S:\Dev\houseCARL\_claude\harness\npc2bench\armarace_probe.py`, derived from the October Recast driver
  by `make_driver.py`.

### Framing

Camera 40 units from the subject, 126 above the feet, level: the frame runs from the top of the helmet horns to
the collar. A first attempt at 68 units, computed from an assumed field of view of 65, framed from mid-chest up.
Measure the frame, or use the harness's `shotplan.py --from-profile`, rather than computing from an assumed FOV.

## Rebuilding

`recipe/` holds the stdio client (`hc.py`) and the call files, in order: `calls_base1.json`, `calls_base2.json`,
enable the base plugin in the profile, `calls_moda.json`, `calls_moda_fix.json`, `calls_place.json`, then enable
Mod A and run `calls_probe.json`. The profile needs base masters in `plugins.txt` and a `Skyrim.ini` with the
archive list. `probe/install_lab.py` installs into the Conflict Lab; it was not used for the 2026-11-24 run.

`SHA256SUMS.txt` lists every file as built. The FaceGen files are Bethesda assets: local use only.
