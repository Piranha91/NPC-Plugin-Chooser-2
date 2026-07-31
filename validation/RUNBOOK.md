# In-game validation runbook — 2026-07-28 fixes

Specimens were found by scanning the actual `NPC Test` profile (vanilla + DLC + the 22 wig-bearing
mods in `ModSettings`), not chosen by guesswork. What the scan found is as important as what it
didn't — see **Coverage reality** at the bottom before planning screenshots.

Each test is one staged `Settings.json`, written by `run.py` over the live file (your own is
snapshotted first — see below). All three inherit your live settings verbatim (mods folder, scan
caches, mugshot config) and change only the NPC selection, the handling modes under test, and the
output folder — so runs cannot overwrite each other or your normal output.

## How to run one

Works in PowerShell or Command Prompt. (In Command Prompt, `cd` across drives needs `cd /d` — or
just type `S:` first. PowerShell's `cd` switches drives on its own.)

```powershell
cd "S:\Dev\NPC Plugin Chooser 2\NPC Plugin Chooser 2"
python validation\run.py A skypatcher
```

Then launch NPC2 **through MO2 on the `NPC Test` profile** and hit Run.

The first call snapshots your live `Settings.json` to `validation\Settings.live.json` and derives
every variant from that snapshot, so repeated runs never compound. Put it back any time with:

```powershell
python validation\run.py restore
```

Valid combinations — each writes to its own output folder, so nothing is overwritten:

| | |
|---|---|
| `run.py A skypatcher` / `A record` | §3 flatten seam, both output modes |
| `run.py B skypatcher` / `B record` | §4 skin-carried wig, both output modes |
| `run.py C record` | §1/§2 — the real test |
| `run.py C skypatcher` | §1/§2 negative control (must report nothing) |

> Launch through MO2, not the exe directly. A direct launch sees the raw Steam load order, and the
> conflict-winning record is what decides the recipient in Create-and-Patch.

## Objective check (no before-run needed)

```powershell
dotnet test "Tests\NPC Plugin Chooser 2.Tests.csproj" --filter "FullyQualifiedName~ValidationChecker"
```
It sweeps every run folder you produced and prints PASS/FAIL per specimen, per mode, reading the
output plugin and (in SkyPatcher mode) the .ini. Tests for runs you haven't done skip themselves
with a note telling you which `run.py` line to use.

> A/B are self-contained: they check the specimen's own wig and hair in the output. C is not —
> it checks that the *condition* it reports on is present, but not that NPC2 said anything. A green
> C plus the log lines under Check 1 is the full pass.

## Spawn scripts

`python validation\make_bats.py` writes **one spawn script per run**, into that run's output folder.
MO2 maps those into Data, where the console finds them — so whichever run's output mod is enabled,
that run's script is the one in scope. Re-run the script after any new patch run.

In console: **`bat a`**, **`bat b`**, **`bat c`** — always the same three names, whichever run is
loaded. The files are `a.txt` / `b.txt` / `c.txt`: `bat` names the script, not the extension.

Each spawns every specimen for that test and ends with `tai`. Test A's spawns are ordered so each
twin lands immediately before its terminus. Every file carries a header naming the NPCs and what to
expect, so you can work from the pile without coming back to this document.

> Two practical notes:
> - They spawn at your feet, so stand somewhere open or you get a heap.
> - `tai` with nothing selected toggles AI for **all** actors. It freezes what you just spawned, but
>   it is a toggle, not a set — running another bat in the same session turns AI back **on**. Type
>   `tai` again to re-freeze.

FormIDs are computed from `loadorder.txt` + `plugins.txt`, honouring the ESL flag, so the prefixes
are the real in-game ones (`Skyrim.esm`→`00`, `Dawnguard.esm`→`02`, `Dragonborn.esm`→`04` in this
profile). If you enable or disable a non-ESL plugin that sorts before Dragonborn, re-run the script.

## If you want before/after screenshots

```powershell
git stash push -- BackEnd/HeadPartWigConverter.cs BackEnd/WigForwarder.cs `
                  BackEnd/Patcher.cs BackEnd/OutfitDistribution/OutfitDisplayResolver.cs
# close NPC2, rebuild, `python validation\run.py B skypatcher`, Run  -> this is "before"
git stash pop
# close NPC2, rebuild, run the same line again              -> this is "after"
```

`run.py` names the output folder per (test, mode), so the second pass would overwrite the first.
Rename it in between:

```powershell
Rename-Item "S:\Skyrim NPC Selection\mods\NPC Output - B-skypatcher" "NPC Output - B-skypatcher BEFORE"
```

(The checker only looks at the un-renamed folders, so the "BEFORE" copy is ignored by it and kept
purely for your screenshots.)

---

# Test A — §3, the flatten seam  (`run.py A skypatcher` / `A record`)

**Modes:** `TemplateHandlingMode = GiveEachNpcOwnCopy`, `DefaultWigHandlingMode = ForwardToOutfit`,
SkyPatcher mode (unchanged from live).

`GiveEachNpcOwnCopy` flattens the terminus's appearance onto the NPC's own record. The wig pass runs
**before** that flatten, and used to read the DONOR. Every specimen below is Traits-templated to a
terminus that carries a **different HighPoly wig**, so "which record did the wig pass read" shows up
as a visibly different hairstyle.

| NPC | FormID | Donor's wig (wrong) | Terminus's wig (correct) |
|---|---|---|---|
| Legate Rikke | `0D0573` Skyrim.esm | `WigAA_HairFemaleNord12` | `WigAA_HairFemaleNord03` |
| Arniel's Shade | `06A152` Skyrim.esm | `WigAA_HairMaleNord01` | `WigAA_HairMaleElder3` |
| Miraak (MQ02) | `017938` Dragonborn.esm | `WigAA_HairMaleNord01` | `WigAA_HairMaleElder1` |
| Vigilant 02NordM01 | `10C471` Skyrim.esm | `WigAA_HairMaleNord03` | `WigAA_HairMaleRedguard2` |

The body armor moves with it (`HighPoly_BodyArmor_*` follows the same WornArmor), so the skin is
wrong too, not just the hair.

**In game:** `coc` anywhere, then place a twin next to its terminus:

```
player.placeatme 0D0573 1 ; Legate Rikke, the templated twin
player.placeatme 0132A1 1 ; the real Legate Rikke, her chain terminus
```

After the fix the two should share a hairstyle and face, because the twin now inherits them.

> **Both must be SELECTED in NPC2 for this to mean anything.** HPNO's ESP is not active in this
> profile — NPC2 bakes its appearance into the output instead — so an unselected terminus renders
> with whatever appearance ESP wins the load order (vanilla, Bijin, …), and comparing a flattened
> twin against that compares two different mods rather than before-and-after. `run.py` selects
> both halves of every pair for exactly this reason. This bit the first attempt at the shot.

**Headgear confounds the hair half.** Three of the four pairs wear something on the head, so pick
the pair by what you want to see:

| Pair | Twin / terminus | Good for |
|---|---|---|
| Arniel's Shade `06A152` / Arniel Gane `01C19D` | same outfit, **no headgear** | hair + face (the Shade carries a ghost shader, so judge shape not colour) |
| Legate Rikke `0D0573` / `0132A1` | twin has an officer's **helmet**, terminus does not | face; expect the helmet difference — the outfit is Inventory-governed and correctly stays the twin's own |
| Miraak MQ02 `017938` / Miraak `017F7D` | both wear **DLC2MiraakMaskNew** | body/skin only — the mask hides the face |
| Vigilant `10C471` / `10C454` | both wear a **hood** | face only |

Cleanest of all: skip the console and compare the two in NPC2's own 3D preview with the attire
toggle **off**. No equipment, no shaders, same camera.

**Expected after:** twin and terminus match.
**Expected before:** the twin wears the donor's hair and body instead.

### The wig does not always land in the same place — this is correct

Measured 2026-07-28 across both modes. Which route the wig takes depends on the NPC and the mode,
and the checker accepts all three because the test is about which RECORD was read, not where the
wig ends up:

| Route | When | Looks like |
|---|---|---|
| minted outfit ARMO | SkyPatcher mode, or record mode on an NPC without the Inventory flag | `NPC2WigArmor_HighPoly_WigAA_*` in the outfit |
| minted **head parts** | record mode + an **Inventory**-templated NPC | `NPC2Wig_HighPoly_WigAA_*_M_Hair` in the head parts |
| left on the skin | ForwardToSkin | `HighPoly_WigAA_*` still in the WornArmor armature |

The middle row is `Patcher.cs`'s pre-existing `outfitFieldInert` branch: a forwarded outfit could
never reach an Inventory-templated NPC (that is §1's whole subject), so it redirects to
ConvertToHeadParts instead. Of A's four specimens only Legate Rikke lacks the Inventory flag, which
is why she is the only one that takes the outfit route in record mode.

That route is the **better** evidence for §3, incidentally: it runs the wig through the converter,
so a correct `NPC2Wig_..._HairMaleElder3_...` proves the converter read the terminus's WornArmor
*and* took the terminus's sex, not just that the forwarder picked the right skin.

> Note: `Arniel's Shade` is also Inventory-templated, so it doubles as a §1 specimen — its log line
> should appear in Test C's summary too if you enable Include Outfit.

---

# Test B — §4, skin-carried wig vs. head-part hair  (`run.py B skypatcher` / `B record`)

**Modes:** `DefaultWigHandlingMode = ForwardToSkin`. None of these NPCs are Traits-templated, so
this isolates §4 from §3 completely.

These are the 13 NPCs in HPNO that carry a skin wig **and still have a real, modeled Hair head
part**. Both meshes render and clash. Before the fix the forwarder never removed the hair here,
because nothing was transferred.

| NPC | FormID | Clashing hair | Skin wig |
|---|---|---|---|
| Forsworn Briarheart | `044310` Skyrim.esm | `HairFemaleNord19` | `WigAA_HairMaleNord01` |
| Pit Fan (female) | `0558F3` Skyrim.esm | `HairMaleNord10` | `WigAA_HairFemaleNord01` |
| Velehk Sain | `075C7F` Skyrim.esm | `MaleDremoraHair01` | `WigAA_HairMaleDremora01` |
| Dremora Kynval | `016F69` Skyrim.esm | `MaleDremoraHair01` | `WigAA_HairMaleDremora01` |

The first two are the clearest shots — both are cross-sex mismatches, so the clash is unmistakable
(a female hairstyle on a male Briarheart; a male hairstyle on a female Pit Fan).

**In game:** `player.placeatme 044310 1`, `player.placeatme 0558F3 1`.

**Expected after:** one hairstyle — the wig. The record carries `NPC2_HairBald` instead of the
clashing part.
**Expected before:** two overlapping hairstyles, or Z-fighting where they intersect.

**Watch for the regression this could cause:** the NPC must not end up bald. If the wig disappears
too, the slot gate or the bald back-fill is wrong. The checker asserts both halves.

---

# Test C — §1/§2, inert Include Outfit  (`run.py C record`, control `C skypatcher`)

**Modes:** `UseSkyPatcherMode = false` (**required** — SkyPatcher mode is exempt by design, its
`outfitDefault=` directive reaches the actor whatever the record says), `PatchingMode =
CreateAndPatch`, HPNO `IncludeOutfits = true`, wig/antler handling off.

| NPC | FormID | Own outfit (dead write) | Inventory template → what's actually worn |
|---|---|---|---|
| DLC2EncCultist06NordM | `034FC5` Dragonborn.esm | `WarlockOutfitLeveled` | `030CD5` → `DLC2HermaeusMoraCultistOutfit` |
| DLC2EncCultist06DarkElfF | `034FC3` Dragonborn.esm | `WarlockOutfitLeveled` | `030CD5` → `DLC2HermaeusMoraCultistOutfit` |
| DLC1VQ03VampireDriverDead | `006E5C` Dawnguard.esm | `DLC1vampireOutfitHigh` | `00887B` (Rogen) → `DLC1VampireClotheOnly` |

**There is no before/after to photograph** — the write was always dead, so this fix changes nothing
in game. What it changes is that NPC2 now *says so* instead of silently doing nothing, and that the
preview stops depicting an outfit the game will never put on.

**But there IS a mode-vs-mode shot**, and it proves the premise the whole fix rests on. Confirmed
from the two outputs on 2026-07-28:

```
C-skypatcher\SKSE\Plugins\SkyPatcher\npc\NPC Plugin Chooser\NPC.ini
  filterByNPCs=Dragonborn.esm|34FC5:...,outfitDefault=Skyrim.esm|44CD5,...
C-record  ->  no ini at all
```

The checker reads both halves of that automatically now — record mode: the outfit was written AND
the recipient is Inventory-templated (so the write cannot reach the actor); SkyPatcher mode: the
`outfitDefault=` directive is present (so nothing is inert). What it still cannot see is whether
NPC2 *reported* it, which is Check 1 below and stays a human read.

Same NPC, same Include Outfit setting, different clothes:

- load the **C-record** output, `player.placeatme 034FC5 1` -> **cultist robes**
  (`DLC2HermaeusMoraCultistOutfit`, from the inventory template). The record write is dead.
- load the **C-skypatcher** output, same command -> **warlock robes** (`WarlockOutfitLeveled`,
  what Include Outfit asked for). The runtime directive reaches the actor and bypasses the flag.

That is the clearest possible demonstration of both halves at once: that the record field really is
inert, and that the SkyPatcher exemption is real rather than an assumption baked into the code.

**Check 1 — the run log.** Search it for:
- per-NPC: `Include Outfit: <name> takes its inventory from template 030CD5:Dragonborn.esm`
- end of run: `3 NPC(s) had 'Include Outfit' enabled but take their whole inventory`

**Check 2 — the UI, before you Run.** Open `DLC2EncCultist06NordM` in the NPCs tab with the outfit
render toggle on:
- the tile/preview should show the **cultist robes**, not warlock robes
- a notice banner should read *"'Include Outfit' is set, but this NPC takes its inventory from
  template 'DLC2MasterCultist' — the outfit written to its own record is never worn in game."*

Before the fix the preview showed `WarlockOutfitLeveled` and said nothing.

**Check 3 — mugshots re-stale.** Only these NPCs' tiles should re-render, not the library. If you
see a mass re-render, the identity stamp changed more broadly than intended.

> This is the one thing that could have leaked into your *live* (SkyPatcher) config. The wig
> identity stamp originally followed the inventory template chain unconditionally, while the
> display exempted SkyPatcher mode — so the stamp and the depiction would have disagreed, and
> Inventory-templated tiles would have re-rendered for no reason. Both now share the exemption,
> pinned from both sides by `WigIdentityStamp_RecordMode_FollowsTheInventoryTemplate` and
> `WigIdentityStamp_SkyPatcherMode_UsesTheNpcsOwnOutfit`. Net effect on your live settings: none.

---

# Coverage reality — what the scan could NOT find

Worth knowing before you spend time hunting for a shot that doesn't exist.

**§3's double-hair manifestation: 0 specimens, across all 22 wig-bearing mods.** The converter's
hair-removal bug needs a terminus with a *modeled* Hair head part. Every HPNO terminus is bald
(`HighPoly_HairBald`, modeless) because that is exactly how HPNO ships skin wigs. So the
double-hair failure is real in code and pinned by `Route8_ConvertToHeadParts_Flattened_ReadsTheTerminus`,
but it is **not reachable in this load order**. Test A exercises the *wrong-skin* half instead,
which has 55 specimens.

**§3's sex/race/weight divergence: essentially absent.** Of 101 HPNO donor≠terminus pairs, exactly
one differs in race (`10C47A`) and none in sex or weight bucket. The unit tests
(`Apply_Terminus_MintsForTheTerminusSex`, `..._UsesTheTerminusWeightVariant`) are the only coverage
those will get; don't expect an in-game shot.

**§1/§2 in vanilla: 0 specimens.** Of 2,490 Inventory-templated NPCs in Skyrim.esm, 540 have no
outfit of their own and 1,504 name the same outfit as their template — none differ. The dead field
is only observable where a *mod* writes a different outfit onto one, which is why Test C's specimens
all come from HPNO/Modpocalypse overrides of DLC records. This also means the limitation was
low-impact in practice, which is consistent with choosing report-only over distribution.

**`ConvertToHeadParts` is not exercised by any test here.** Your live config is `ForwardToOutfit`,
and the converter needs a modeled donor hair to harvest partitions from — which, per the above, the
flattened specimens don't have. If you want it covered in game, the place to do it is a mod with
outfit-carried wigs (Evangeline, GLENMORIL, VIGILANT, Vilja all have `DetectedWigArmors`), not HPNO.

## Deferred items (no test — by design)

The three entries now in `docs/KnownLimitations.md` are deliberately untested:
`IsWigArmature` scope-key divergence is invisible under your `ManualWigBlockScope = AllNpcs`; the
five-way duplicated WNAM walk is a refactor with no behavioural surface; and
`StripWigsFromForwardedOutfit`'s raw-donor read only diverges for Inventory-templated NPCs with a
forwarded wig — which, per the scan above, has no specimen in this load order either.
