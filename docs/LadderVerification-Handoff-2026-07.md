# Verifying the two unexercised FaceGen-ladder paths

**Purpose:** close the last two "proven only by unit tests" gaps in `BackEnd/FaceGenLadder.cs`
(recorded as `docs/KnownLimitations.md` #5). Nothing here is a bug hunt — the expected result of
every step is "it works". The point is to stop describing these paths as unproven.

Delete this file once both boxes are ticked and move anything learned into KnownLimitations.

- [ ] **Row 3** — the mod ships a face *tint* but no face *mesh*, and edits the NPC record
- [ ] **Face swap** — a shared/guest appearance, where the donor's face lands at the target's path

They are independent; do them in either order. Face swap is much quicker, so it is first below.

---

# Part A — Face swap (30 minutes, no special mods)

## What is unproven

`FaceGenDestinationMode.FaceSwap` is the case where you tell NPC2 "make NPC **A** look like NPC
**B**". The face files are read from B's paths and written to **A's**, renamed on the way. Record
mode and SkyPatcher mode have both been verified in game many times; this third destination has not.

The specific risk is a rename bug: FaceGen is reconciled by baked shape names against the head
parts of the record sitting at that path, so writing B's mesh to A's path while A's record says
something else is the classic dark-face setup. If it were broken, it would be *obviously* broken.

## Procedure

1. Launch NPC2 **through MO2**. Any profile works — this needs no particular mod.
2. In the **NPCs** tab, pick a target NPC with an obvious, memorable face. A named vanilla NPC of a
   different sex and race from the donor is ideal, because it makes a partial failure visible: pick
   e.g. **Lydia** (`000A2C94:Skyrim.esm`).
3. Right-click → the share/guest-appearance option → choose a clearly different NPC as the donor
   (e.g. an Orc or Redguard male). The mugshot should immediately show the donor's face.
4. Run the patch in **Create and Patch** mode.
5. Confirm on disk before going in game — this catches the rename bug without launching Skyrim:

   ```
   <output>\meshes\actors\character\facegendata\facegeom\Skyrim.esm\000A2C94.nif
   <output>\textures\actors\character\facegendata\facetint\Skyrim.esm\000A2C94.dds
   ```

   Both must exist at the **target's** FormID (`000A2C94`), NOT the donor's. If they are at the
   donor's ID, the destination is wrong and nothing else matters.
6. `Validate Output`. A face-swapped NPC should produce **no Asset findings**. (It may produce a
   Record finding if something else in your load order overrides Lydia — that is a different thing.)
7. In game: `coc whiterun`, `player.placeatme 000A2C94 1`.

## Pass / fail

- **Pass:** the spawned NPC has the donor's face, correctly tinted, with no seam at the neck.
- **Fail (dark face):** head renders flat grey/black or an obviously different skin tone from the
  body. That means shape-name reconciliation failed — capture the output `.nif` and the run log.
- **Fail (wrong face):** the target's own face renders. The write went to the wrong path; check
  step 5.

---

# Part B — Row 3 (the real work)

## What is unproven, precisely

Row 3 is: **the selected mod ships a face tint (`.dds`) but no face mesh (`.nif`), and it also has
a record for that NPC.** A tint alone cannot render — it needs geometry — so the ladder borrows a
mesh, and because the engine reconciles a mesh's baked shape names against the head parts of the
record that ships, a borrowed mesh that does not fit produces the dark-face bug. Row 3 is the only
row that *gates* its borrowed mesh on that compatibility check.

It has **three** outcomes, and they are separate things to prove
(`FaceGenLadder.ResolveDdsOnlyWithRecord`):

| Leg | When | Expected |
|---|---|---|
| **B1 — origin mesh** | the mod that originally added the NPC has a mesh, and it fits | mesh from origin, tint from your mod |
| **B2 — winner mesh** | the origin's mesh is missing or does not fit, but some other installed mod's does | mesh copied from that mod, tint from your mod |
| **B3 — abort** | no mesh anywhere fits | NPC deliberately left unpatched, named in the run summary |

B1 is the common, healthy shape of a tint-only mod. **B3 is the one that matters most** — it is the
guard that stops NPC2 shipping a face it knows will be broken — and it is also the easiest to reach.

## Candidates

Found 2026-07-29 by scanning 20,707 mod folders across all four MO2 installs for a loose
`facetint\<plugin>\<id>.dds` with no matching `facegeom\<plugin>\<id>.nif`. Restricted to mods that
ship **no BSA**, so the missing mesh is genuinely missing rather than packed.

| Modlist | Mod | Plugin | NPCs | Verified on disk |
|---|---|---|---|---|
| Tempus Maledictum 1_11 | **Teldryn Serious** | `TSR_TeldrynSerious.esp` | 68 | yes — 78 tints, 10 meshes, no BSA |
| Tempus Maledictum 1_11 | Darkend | `Darkend.esp` | 39 | not spot-checked |
| Tempus Maledictum 1_11 | Hearthfire Extended | `HearthFireExtended.esp` | 27 | not spot-checked |
| Tempus Maledictum 1_11 | Strongholds - Mor Khazgur | `Strongholds - Mor Khazgur.esp` | 22 | not spot-checked |
| Tempus Maledictum 1_11 | There Is No Umbra - Ch III | `FloatingSwordFollower.esp` | 12 | not spot-checked |
| Tempus Maledictum 1_11 | Boethiah's Calling - Alt Questline | `BoethiahCalling_AlternativeQuest.esp` | 10 | not spot-checked |
| Tempus Maledictum 1_11 | Solitude Weaver's Lane | `Socalista Solitude.esp` | 10 | not spot-checked |
| LoreRim | Katana - Journey in the Shadows | `katana.esp` | 1 (`001724E6`) | not spot-checked |

**Start with Teldryn Serious.** It is the only one confirmed file-by-file, it has the largest
population, and 68 NPCs across one plugin is very likely to contain more than one leg.

Verified Teldryn Serious FormIDs with a tint and no mesh:
`0000F29F`, `0000F2A2`, `00018290`, `00018294`, `00038560`, `0005D2B1`.

### Two caveats — read before spending time

1. **A tint-without-mesh file does not by itself mean row 3.** The ladder needs the mod to also have
   a *plugin record* for that NPC; without one it is row 4 (a FaceGen-only mod), which is already
   well exercised. Step 1 below settles this from a log instead of by reasoning.
2. **Which leg you land on depends on the ORIGIN, not on the mod you select.** If the mod is itself
   the NPC's origin — likely for a quest mod's own new NPCs — then there is no origin mesh to borrow
   and you will land on B2 or B3, never B1. That is fine: B3 is the leg most worth proving. Do not
   force B1; find it if it appears.

## Step 1 — confirm the classification from a log (do this first)

Do not go in game to find out which row you hit. The ladder already writes its own verdict.

1. Point NPC2 at the Tempus Maledictum 1_11 mods folder and let it scan.
2. Create an empty file named **`LogFaceGenLadder.txt`** next to `NPC Plugin Chooser 2.exe`.
3. Select **Teldryn Serious** as the appearance mod for a handful of its NPCs (the FormIDs above).
4. Run the patch.
5. Open **`FaceGenLadder.csv`** next to the exe. The columns that answer everything:

   | Column | What to look for |
   |---|---|
   | `Row` | `3` — this is the whole point. `4` means the mod has no record for that NPC |
   | `PlannedAction` | `nif=Origin` → leg B1 · `nif=Winner`/`WinnerInPlace` → B2 · `Abort` → B3 |
   | `Aborted`, `AbortReason` | the B3 evidence, in the ladder's own words |
   | `Explanation` | the sentence the user would have seen in the run log |
   | `ChainStatus` | should be `NotTemplated` for most; a templated one drags in template handling and muddies the test |

6. **Delete `LogFaceGenLadder.txt` afterwards** — it re-triggers on every launch.

If no row is `3`, try the next mod in the table. If none of them produce a `3`, that is itself a
finding worth recording: it would mean row 3 needs a plugin shape that does not occur in these
loadouts, and KnownLimitations #5 should say so rather than continuing to list candidates.

## Step 2 — verify the leg you landed on

### If you got B3 (abort) — the most valuable outcome

No in-game step is needed, and that is the point: the guard's whole job is that nothing ships.

1. In the run log, find the forced end-of-run summary: *"N NPC(s) were left unpatched because their
   face could not be assembled…"* with one line per NPC.
2. Confirm the output contains **no** FaceGen for those FormIDs:
   `<output>\meshes\actors\character\facegendata\facegeom\<plugin>\<id>.nif` must be absent.
3. Confirm the output plugin has **no record** for them either — the abort happens before anything
   is written, so an aborted NPC should appear nowhere in the output.
4. `Validate Output`. The aborted NPCs will show as unpatched. That is expected and correct; the run
   log is where the intent is stated.
5. In game, spawn one anyway: it should look exactly as it does with NPC2's output disabled. That is
   the success condition — the mod was not applied, and nothing was broken by not applying it.

### If you got B1 or B2 (a mesh was borrowed)

This is the dark-face-risk path, so it must be checked in game.

1. Note from the CSV which NPCs took a borrowed mesh.
2. In the output, confirm both halves landed:
   `facegeom\<plugin>\<id>.nif` **and** `facetint\<plugin>\<id>.dds`.
3. `Validate Output`. Expect an **Info** row reading *"'<mod>' ships no face mesh for this NPC, so
   the deployed mesh was forwarded from elsewhere"*, naming the provider. A **FaceGen** finding
   (head-part mismatch) here is a real failure — that is the dark-face detector firing.
4. In game: `player.placeatme <id> 1` for two or three of them.

**Pass:** the face renders normally, with the skin tone the mod's tint intends and no seam at the
neck. Compare against the same NPC with NPC2's output disabled — the face should look *better* or
at least deliberate, not grey.

**Fail (dark face):** flat grey/black head, or head and body tones obviously disagreeing. Capture
the CSV row, the output `.nif`, and the `Validate Output` report. That would mean the compatibility
gate passed something it should not have, which is exactly the bug this row exists to prevent.

## Step 3 — record the result

Whichever way it goes, edit `docs/KnownLimitations.md` #5:

- legs proven → strike them from the entry, and if all three are proven, delete the entry
- a leg still unreachable → say which, and why, so nobody re-runs this hunt
- a failure → it becomes its own entry with the specimen attached

---

## Notes on tooling

- **Everything must be launched through MO2.** A direct launch reads the raw Steam load order, so
  the conflict winner — which decides the B2 mesh — is wrong.
- `PatchVerifyRunner` (`PatchVerify.json` next to the exe) can drive a headless patch run and emits
  the same `FaceGenLadder.csv` plus spawn `.bat` files with correct runtime FormIDs. It is worth it
  if you end up iterating; for a single pass the manual route above is fewer moving parts.
  `SamplePerRow: { "Row3": 0 }` means "all of row 3". Delete the trigger file afterwards.
- The census CSV behind the candidate table was machine-local scratch and is not in the repo. The
  scan is cheap to redo: walk each mod folder, set-difference the `.dds` basenames under
  `textures\actors\character\facegendata\facetint\<plugin>\` against the `.nif` basenames under
  `meshes\actors\character\facegendata\facegeom\<plugin>\`, and drop any mod that ships a `.bsa`.
