# ArmaRace fixture, version 2 — override handling, armature race

Synthetic mod pair for the NPC2 in-game pre-release check. Version 2 of `..\ArmaRace\` (battle plan item H5): the
same records on two other vanilla NPCs. Version 1 used Orgnar (`013479:Skyrim.esm`) and Hod (`01347D`); in the
bench's Patched and Contested profiles `AI Overhaul.esp` loads after the fixture and carries its own record of both,
with their vanilla outfits, so the helmet outfit never reached the game there. Version 2 uses Christer
(`dunMistwatchChrister`, `090738:Skyrim.esm`) in Orgnar's place and Thorgar (`0AE777:Skyrim.esm`) in Hod's.

Built 2026-11-27 03:21 PST with houseCARL 2.0.4 (`2.0.4+bbb63cb6`) through `recipe\hc.py`, against a scratch MO2
instance outside the bench instance (`recipe\scratch-instance\`, profile `H5Scratch`: Skyrim.esm and Update.esm
only). Checked with `housecarl_check` (errors: 0 dangling refs, 0 missing masters) and read back with the bench's
plugin reader (`bench_run.plugin_records`).

**Not yet run in the game.** Version 1's mechanism was verified in game on 2026-11-24 (see `..\ArmaRace\README.md`);
version 2 changes only which NPC records carry it.

## The two mods

`mods/NPC2Bench - ArmaRace Base` — plays the load order. Active in every launch.

| Record | FormKey | Content |
|---|---|---|
| ArmorAddon `N2B_IronHelmetAA_NordOnly` | `000800:NPC2Bench_ArmaRace_Base.esp` | Clone of `IronHelmetAA`. Race = NordRace, no additional races |
| Armor `N2B_IronHelmet` | `000801` | Clone of `ArmorIronHelmet`, armature = the addon above only |
| Outfit `N2B_HelmetOutfit` | `000802` | Bar keeper clothes + shoes + the helmet |
| NPC Christer | `090738:Skyrim.esm` (override) | DefaultOutfit = `N2B_HelmetOutfit` |
| NPC Thorgar | `0AE777:Skyrim.esm` (override) | DefaultOutfit = `N2B_HelmetOutfit` (control: Mod A never touches him) |

`mods/NPC2Bench - ArmaRace ModA` — the appearance mod. Active only in the reference launch; in a test launch NPC2
reads it as a mod folder and its plugin is off.

| Record | FormKey | Content |
|---|---|---|
| Race `N2B_NordCloneRace` | `000800:NPC2Bench_ArmaRace_ModA.esp` | Clone of NordRace (Update.esm's version). ArmorRace = DefaultRace |
| NPC Christer | `090738:Skyrim.esm` (override) | Race = `N2B_NordCloneRace`; outfit unchanged |
| ArmorAddon `N2B_IronHelmetAA_NordOnly` | `000800:NPC2Bench_ArmaRace_Base.esp` (override) | Race = `N2B_NordCloneRace`, additional races = DefaultRace. Nord is out |
| FaceGen | `00090738.nif` / `.dds` | Christer's vanilla FaceGen, placed out of `Skyrim - Meshes0.bsa` and `Skyrim - Textures0.bsa` by `housecarl_place` |

Masters: Base → Skyrim.esm, Update.esm. Mod A → Skyrim.esm, Update.esm, NPC2Bench_ArmaRace_Base.esp. The plugin
FormIDs of every new record are the same as in version 1, so nothing that names them changes.

## Read back (2026-11-27)

- The helmet armature, armor and outfit in Base and the race and armature override in Mod A are subrecord for
  subrecord identical to version 1.
- Base's two NPC records differ from Skyrim.esm's in DefaultOutfit and in what houseCARL's re-serialisation always
  changes (FULL written as a string because the plugin is not localized; one float of TextureLighting a last bit
  off; a -0.0 face morph written as 0.0). Version 1's Orgnar and Hod records differ from Skyrim.esm in the same way.
- Mod A's Christer differs from Base's only in Race.
- No record of Orgnar or Hod.
- The same scratch instance and version 1's call files rebuild version 1 byte for byte (all four SHA-256 of
  `..\ArmaRace\SHA256SUMS.txt`), so the scratch instance stands in for the one of 2026-11-24.

## Why these two NPCs (read from the plugins, 2026-11-27)

No unique male NordRace NPC of Skyrim.esm is free of `High Poly NPC Overhaul - Skyrim Special Edition.esp` (3558 NPC
records; only the ten NordMalePreset records are), so "no plugin of any bench profile touches it" cannot be met. That
plugin is active only in profile N2B-R2, which photographs no fixture NPC, and not in any arm. Christer and Thorgar
are carried by no other plugin of any N2B-* profile, and no plugin of those profiles names Christer from any
record; `unofficial skyrim special edition patch.esp` overrides Thorgar's placed reference in Whistling Mine
(`0AE778`, its location data) and names him in one dialogue condition, without a record of the NPC. The bench
spawns a new actor from the NPC record, so neither reaches it. Neither is a quest alias in Skyrim.esm, Update.esm or the DLC masters. Full
tables: `S:\Dev\houseCARL\_claude\runs\2026-11-25-npc2bench\traces\h5\`.

## Expected

As version 1, with Christer for Orgnar and Thorgar for Hod.

| Launch | Christer's helmet | Thorgar's helmet |
|---|---|---|
| Base only | Shows | Shows |
| Reference: Base + Mod A | Shows | Gone (Mod A took Nord off the armature) |
| NPC2, overrides Ignore | **Gone**: race forwarded, armature edit not | Shows |
| NPC2, overrides Include | Shows | Gone, as in the reference |
| NPC2, overrides Include As New | Shows | Shows (Christer gets a private copy) |

## Rebuilding

Copy `recipe\scratch-instance\` to a working folder (its `ModOrganizer.ini` names the game folder
`C:\Games\Steam\steamapps\common\Skyrim Special Edition`), create an empty `mods\` folder beside its
`ModOrganizer.ini` (houseCARL refuses an instance without one), set `HC_INSTANCE` to that folder (unset, `hc.py`
writes into `X:\Skyrim Conflict Lab`), and run from a copy of
`recipe\` with Python 3.12, `python hc.py run <calls> <out>` (set `HC_EXE` to houseCARL's `housecarl-mcp.exe` if it
is not under your profile's `.claude\skills\housecarl\server\`):
`calls_base1.json`, `calls_base2.json`, then enable the base plugin in the scratch profile (`+houseCARL -
NPC2Bench_ArmaRace_Base` in `modlist.txt`, `*NPC2Bench_ArmaRace_Base.esp` in `plugins.txt`, the plugin name appended
to `loadorder.txt`), then `calls_moda.json`, `calls_moda_fix.json`, `calls_place.json`. houseCARL writes the two mod
folders as `mods\houseCARL - NPC2Bench_ArmaRace_Base` and `mods\houseCARL - NPC2Bench_ArmaRace_ModA` of the scratch
instance; they are renamed here. `hc.py` keeps houseCARL's data folder (`hcdata`) beside itself. MO2 and the game are
not involved. Version 1's probe plugin was not rebuilt and is not part of version 2.

`SHA256SUMS.txt` lists every file as built. The FaceGen files are Bethesda assets: local use only.
