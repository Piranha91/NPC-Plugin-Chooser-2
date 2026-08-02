# Problem: `Include As New` outfit-side records never reach the game in SkyPatcher mode

Status: **implemented 2026-08-02, pending in-game verification** (gates: Dorthe clothed in
RSC clothes; Kayd has `race=`; a non-RSC child unchanged). Diagnosis below is preserved
as-measured on 2026-08-01 (specimen: Dorthe, `Skyrim.esm|013477`, RS Children Overhaul,
live `NPC Test` profile). The implementation follows §7 (all eight requirements):
directives are emitted post-remap at the asset stage; the recipient's effective outfit
(and sleep outfit) root the duplication traversal; `outfitDefault=`/`outfitSleep=` are
emitted only when the batch minted a copy of the outfit the actor wears; assets a
duplicated record references that the mod itself ships are relocated to
`meshes|textures\NPC2\<mod>\...` with record paths and NIF-internal texture slots
rewritten to match; and an end-of-run orphan check (`Patcher.LogOrphanedDuplicates` — a
neutral run-log note, deliberately NOT a colored warning: unreferenced records are inert
in game, and WARNINGs are reserved for in-game-visible issues per the user's 2026-08-02
standard) keeps any future delivery gap from being invisible. Donor outfit links the
written record will not carry are excluded from discovery roots, so donor-only outfit
chains (RSC's native `0RCOClothesO*`) are no longer minted at all.
Golden references for the four Include-As-New combos
predate the fix; the tests assert the fixed behavior and tolerate exactly those deviations
until the references are regenerated.

---

## 1. The requirement `Include As New` exists to satisfy

RS Children Overhaul (RSC) is not confined to NPC records. It edits **shared, non-NPC
records that the whole game references**:

1. **The child Race records.** It overrides vanilla `NordRaceChild`, `BretonRaceChild`,
   `ImperialRaceChild`, `RedguardRaceChild` (and `BretonRaceChildVampire`). A single
   vanilla Race record can only hold one mod's version, and whichever wins the conflict
   applies to *every* child in the game — not just the ones assigned to RSC.
2. **The child clothing ArmorAddons**, reached through the NPC's Default Outfit. RSC
   adjusts these so they stay compatible with its race edits; the AA has to agree with the
   race it is being fitted to. An RSC-adjusted AA on a non-RSC child is as wrong as the
   reverse.

So **`Include` (plain override) is semantically wrong here.** Carrying RSC's edits in as
overrides of the vanilla records makes RSC's race *the* race for all children, which is
exactly what breaks a load order that also runs Children of the Pariah, Children of the
Hist, or the Beyond Reach patch.

**`Include As New` is the correct mechanism**: duplicate the overridden record under a new
FormID and repoint only the NPCs assigned to that mod. RSC children get RSC's race and
RSC-fitted clothes; every other child keeps the vanilla record and whatever their own
replacer did to it. The README's trade-off still applies — each duplicate is a snapshot,
so contributions other mods made to the original record are lost.

That mechanism produces `NordRaceChild_RSChildren.esp` and its three siblings, and the
parallel set for the outfit-side ArmorAddons.

---

## 2. The two delivery channels for a duplicated record

`Include As New` has exactly one way to make a duplicate take effect: **something the NPC
points at must be repointed to the copy.** `RecordHandler.DuplicateInOverrideRecords`
duplicates the chain and then repoints the patched record in one step —
[RecordHandler.cs:1324-1327](../BackEnd/RecordHandler.cs#L1324-L1327),
`mergedInRecords.And(rootRecord).RemapLinks(remappedOverrideMap)`. Parent records that
are not themselves overridden (e.g. the vanilla Outfit above an overridden Armor) are
duplicated too and added to the same map at
[RecordHandler.cs:1482](../BackEnd/RecordHandler.cs#L1482).

That gives two distinct channels:

| channel | fields | works in record mode | works in SkyPatcher mode |
|---|---|---|---|
| **Appearance links on the patched record** | Race, WornArmor, HeadParts, HeadTexture, HairColor | yes | **yes** |
| **Outfit-rooted chain** (`DefaultOutfit` → Armor → ArmorAddon) | DefaultOutfit | yes* | **no** |

\* record mode's patched record is an override of the *winning* NPC record, whose
`DefaultOutfit` is always populated, so the remap lands even with Include Outfits off.
See §4.4 for the caveat.

**The distinction that matters:** repointing `DefaultOutfit` at a duplicate is *not*
outfit forwarding. Dorthe still wears `ChildOutfit01` — she wears NPC2's private copy of
`ChildOutfit01`, wired to the RSC-fitted ArmorAddons. "Include Outfits" answers a
different question ("whose outfit — the donor's or the recipient's?"). The current code
conflates the two, and that is the root of the bug.

---

## 3. Measured state of the current output

Configuration: `PatchingMode = CreateAndPatch`, `UseSkyPatcherMode = true`,
`DefaultRecordOverrideHandlingMode = Ignore`. RS Children Overhaul is the only one of 317
mod entries with a non-default mode: `ModRecordOverrideHandlingMode = IncludeAsNew`,
`IncludeOutfits = false`, `MergeInDependencyRecords = true`. Both its plugins
(`RSChildren.esp`, `RSkyrimChildren.esm`) are disabled in the profile, so everything must
be merged.

What the run produced:

- **Surrogate** `0035E1:NPC.esp` `Dorthe_Template` —
  `Race: 0035BC:NPC.esp`, **`DefaultOutfit: Null`**, `WornArmor: Null`,
  factions/items/packages/perks all empty.
- **ini directive** —
  `filterByNPCs=Skyrim.esm|13477:copyVisualStyle=NPC.esp|35E1,height=1,race=NPC.esp|35BC,weight=10`
- **`outfitDefault=` directives in the entire ini: 0.**
- **The duplicated outfit chain exists in full**:
  `ChildOutfit01_Skyrim` → `ClothesChild01_RSkyrimChildren.esm` →
  `ChildTorso01AA_RSkyrimChildren.esm` (plus the `03` variants). Nothing in the output
  references `ChildOutfit01_Skyrim`.
- **The duplicated races exist**: `NordRaceChild_RSChildren.esp`,
  `BretonRaceChild_RSChildren.esp`, `ImperialRaceChild_RSChildren.esp`,
  `RedguardRaceChild_RSChildren.esp`, `BretonRaceChildVampire_RSChildren.esp`.
- **`NPC Output\meshes\clothes` contains only `ranaline`** — no `childrenclothes`.

So override discovery is **not** the failure. It found the edits and built the copies. The
copies are orphaned.

---

## 4. Why the outfit-side chain is orphaned

### 4.1 The surrogate's `DefaultOutfit` is nulled before the override pass runs

`Include Outfits` is off for RSC, so `includeOutfit == false`, so
`CreateSkyPatcherNpc(..., appearanceOnly: true, includeOutfit: false)`
([Patcher.cs:1257-1259](../BackEnd/Patcher.cs#L1257-L1259)) calls
`StripNonAppearanceData`, whose first line is
[SkyPatcherInterface.cs:235](../BackEnd/SkyPatcherInterface.cs#L235):

```csharp
if (!includeOutfit) npc.DefaultOutfit.SetToNull();
```

The record-override pass runs afterwards
([Patcher.cs:1344](../BackEnd/Patcher.cs#L1344)) and traverses the **donor**
([Patcher.cs:1551](../BackEnd/Patcher.cs#L1551) →
[RecordHandler.cs:1307](../BackEnd/RecordHandler.cs#L1307)), so discovery is unaffected —
but `rootRecord.RemapLinks` has a null link to rewrite. The duplicate is minted and
immediately stranded.

This is the conflation from §2: a *content* choice ("don't take the donor's outfit") was
used to delete the *plumbing* the duplicate needs.

### 4.2 Even with the link intact, SkyPatcher never reads it

Per SkyPatcher's own source, recorded at
[Patcher.cs:2788](../BackEnd/Patcher.cs#L2788): `copyVisualStyle` assigns
`curobj->faceNPC = bo` plus height/weight/tintLayers/bodyTintColor/headRelatedData and the
head parts. **It does not copy the outfit.** The only channel to the game is the
`outfitDefault=` directive, and that is emitted solely under
[Patcher.cs:2808-2811](../BackEnd/Patcher.cs#L2808-L2811):

```csharp
if (includeOutfit)
{
    _skyPatcherInterface.SetOutfit(npcFormKey, patchNpc.DefaultOutfit.FormKey);
}
```

So in SkyPatcher mode the surrogate's `DefaultOutfit` field is **inert by construction**.
Restoring it (undoing 4.1) removes the orphaning but changes nothing in game. This is the
structural reason SkyPatcher mode cannot deliver an outfit-rooted duplicate today, and
record mode can.

### 4.3 The directive is emitted before the remap

Independently of the above, `ApplySkyPatcherDirectives` runs *immediately before* the
override switch in both branches:

| branch | directives | override switch |
|---|---|---|
| Create-and-Patch | [Patcher.cs:1340](../BackEnd/Patcher.cs#L1340) | [Patcher.cs:1344](../BackEnd/Patcher.cs#L1344) |
| Create | [Patcher.cs:1678](../BackEnd/Patcher.cs#L1678) | [Patcher.cs:1682](../BackEnd/Patcher.cs#L1682) |

So even with `Include Outfits` turned **on**, `outfitDefault=` would carry
`Skyrim.esm|6D92E` — the pre-remap, original outfit — and the duplicated chain would stay
orphaned exactly as it is now. Turning on Include Outfits is therefore not a workaround;
it is a second bug stacked on the first. (Currently latent: 0 `outfitDefault` lines in
this run.)

### 4.4 Record mode works, but partly by coincidence

Discovery traverses the **donor's** links while the remap is applied to the **patched
record**, whose `DefaultOutfit` in record mode is the **recipient's**. The remap map is
keyed by the donor's outfit FormKey. When donor and recipient name different outfits — an
appearance mod that also restyles the NPC — the key does not match the link and the same
orphaning occurs in record mode. For Dorthe they coincide (both `06D92E:Skyrim.esm`, donor
and vanilla verified), which is why record mode happens to work for this specimen.

### 4.5 The other root links are delivered on three different schedules

The outfit is not the only NPC link a duplicated chain can hang from. Surveying how each
root actually reaches the game today (verified 2026-08-01):

- **HeadParts / FaceTexture / HairColor** — carried on the surrogate record itself and
  read by `copyVisualStyle`. The override switch's `RemapLinks` rewrites them in place,
  and the surrogate is written to disk after the switch. Correct by construction.
- **WornArmor (`skin=`)** — emitted by `ApplyCoreAppearance` in the asset stage
  ([Patcher.cs:1900](../BackEnd/Patcher.cs#L1900)), *after* the override switch, from the
  surrogate's post-remap link. Also correct by construction — and the precedent the fix
  should follow: link-valued directives emitted once links are final.
- **Race (`race=`)** — emitted at [Patcher.cs:1340](../BackEnd/Patcher.cs#L1340) *before*
  the switch, yet the measured ini carries the post-remap `race=NPC.esp|35BC`. The route:
  `_currentDuplicateInMappings` ([RecordHandler.cs:21](../BackEnd/RecordHandler.cs#L21))
  is batch-scoped and shared across NPCs; the Include-As-New pass registers every
  duplicate in it ([RecordHandler.cs:1396](../BackEnd/RecordHandler.cs#L1396)); and the
  merge-in walker at [Patcher.cs:1310](../BackEnd/Patcher.cs#L1310) runs on the surrogate
  *before* directive emission, remapping its links with that same map. So a duplicate
  minted while processing an **earlier** RSC child remaps **this** child's Race in time
  for `ShouldChangeRace` to see a delta. **Confirmed on the 2026-08-01 output (2026-08-02):**
  the first-processed RSC child, Kayd (`Skyrim.esm|13292`, surrogate `NPC.esp|358E`,
  minted immediately before the duplicate block `358F–35CD`), has **no `race=` directive**
  (`NPC.ini` line 6776) while every later child carries `race=NPC.esp|35BC`/`35BB`. His
  surrogate's Race *is* remapped in-plugin (`0035B9:NPC.esp`) — the switch fixed the
  record after the directive had already been skipped, so at runtime he gets RSC's face
  but keeps the load-order race. Race delivery is cross-NPC coincidence and is
  processing-order-dependent: whichever NPC of a batch is processed first loses.

So the four roots that work are split across three mechanisms, one of which is an
accident. The fix should collapse all of them into one deliberately-ordered delivery
step (§7.8).

---

## 5. Why the result is visibly corrupted rather than merely unchanged

RSC's compatibility fix is a **matched pair**, and the run delivered exactly one half:

| record | vanilla | RSC | delivered? |
|---|---|---|---|
| `NordRaceChild` & siblings | `ArmorRace: Null` | `ArmorRace: DefaultRace (000019)` | **yes** — as `NPC.esp\|35BC`, pushed by `race=` |
| `ChildTorso01AA` (`0006D92D`) | `Race: ArgonianRace` + 5 vanilla child races | `Race: DefaultRace` + those 5 + 3 `RSkyrimChildren.esm` races | **no** — duplicate orphaned |

Dorthe's race now resolves armor through `DefaultRace`. The `ChildTorso01AA` she actually
wears is still vanilla, which serves `ArgonianRace` and the five vanilla child races and
**not** `DefaultRace`. Her child clothing has no valid armature.

Note the failure is *created* by half-succeeding. Under `Ignore` the race is never
duplicated either, no `race=` directive is emitted, and she renders as a plain vanilla
child — wrong appearance, but not broken. The corruption is specific to "the race half of
`Include As New` landed and the outfit half did not."

---

## 6. Record-level isolation is undone at the asset level

Not addressed by any fix that only moves FormIDs around, and it must be solved for
`Include As New` to mean what §1 says it means.

`RSChildren.bsa` ships `torso01_0.nif`, `torso01_1.nif`, `torso03_0.nif`, `torso03_1.nif`
— replacements for the vanilla child clothing meshes. The duplicated ARMA
`ChildTorso01AA_RSkyrimChildren.esm` names the **same vanilla-relative paths** its source
did (`Meshes\Clothes\ChildrenClothes\F\Torso01_1.nif`), because duplicating a record does
not rewrite its model paths.

`AssetHandler` copies to
[AssetHandler.cs:555](../BackEnd/AssetHandler.cs#L555):

```csharp
string destPath = Path.Combine(outputBasePath, relativePath);
```

There is a destination-override parameter
([AssetHandler.cs:512/517/558](../BackEnd/AssetHandler.cs#L512)) but only FaceGen uses it
([AssetHandler.cs:1236/1250/1267](../BackEnd/AssetHandler.cs#L1236)), where the
destination is per-NPC-FormID and therefore naturally unique.

So the moment the outfit chain is delivered and its assets are collected
([Patcher.cs:1571](../BackEnd/Patcher.cs#L1571)), RSC's child clothing meshes and textures
land in `NPC Output` at the vanilla paths as loose files — **re-skinning every child in
the game**, including the non-RSC ones the duplication was meant to protect. Same failure
mode as `Include`, one layer down.

(This also explains the empty `meshes\clothes` today: the orphaned chain's assets were
never delivered. Whether asset collection ran and failed, or never ran, is unverified —
`RecordProvenance.csv` and `AssetProvenance.csv` in the Debug folder are both stale, from
July 12 and July 27.)

---

## 7. What a fix has to satisfy

1. **Separate "which outfit" from "which copy of it."** Repointing `DefaultOutfit` at a
   private duplicate must not be gated on `Include Outfits`, which answers an unrelated
   question. `StripNonAppearanceData` needs to distinguish "the surrogate should have no
   outfit opinion" from "the surrogate must carry the outfit link so `Include As New` can
   repoint it."
2. **Pick the right source outfit.** With `Include Outfits` off, the outfit to duplicate
   is the one the actor will actually wear — the recipient's effective outfit, which
   `OutfitDisplayResolver.ResolveForDisplay` already computes (chain-resolved,
   patch-mode-aware, SkyPatcher/SPID layers included) — not the donor's. Using the donor's
   would silently change what the NPC wears whenever they differ (§4.4).
3. **Give SkyPatcher mode a delivery channel.** `outfitDefault=` is the only one. That
   means emitting it for an NPC whose `Include Outfits` is *off*, whenever `Include As
   New` minted a private copy of that NPC's own outfit — and emitting it **after** the
   remap (§4.3), not before.
4. **Respect the runtime outfit contest.** `outfitDefault=` is a bare pointer poke with no
   reconciliation. Other SkyPatcher/SPID configs can also set these NPCs' outfits — the
   disabled `(OUTDATED) Immersive Outfits - RS Children Edition SPID` is in this very
   modlist — and `ForwardedOutfitDistributor` / `PublishForwardedOutfitsToDistributors`
   (currently `true`) already model republishing through the distributors.
5. **Handle inventory-templated NPCs.** An NPC that takes its whole inventory from a
   template never wears a record-level outfit; the patcher already detects and reports
   this ([Patcher.cs:1096-1105](../BackEnd/Patcher.cs#L1096-L1105),
   `ReportInertOutfitNpcs`). A duplicate delivered to such an NPC is dead on arrival and
   should say so rather than silently do nothing.
6. **Isolate the assets too** (§6), or the record-level isolation is cosmetic. Likely
   requires a per-mod destination prefix plus rewriting the duplicated ARMA's model paths
   — which breaks the "a duplicate is a verbatim snapshot" simplicity and needs a decision.
7. **Do not regress record mode**, which delivers the outfit-side chain today, and do not
   double-apply when both channels are live.
8. **Generalize across every root field, not just DefaultOutfit.** The duplicated chain's
   interior is already type-generic — traversal, duplication, and internal remapping
   handle any record type at any depth, so a future "RSC-but-for-headparts" mod needs no
   new discovery code. The only per-field surface is the **root**: the NPC link fields a
   chain can hang from, and that set is closed because it is the NPC record schema —
   Race, WornArmor, HeadParts, FaceTexture, HairColor, DefaultOutfit, SleepingOutfit. A
   new mod cannot invent a new root; it can only override different record types *beneath*
   these roots. So the fix is **one delivery pass over the enumerated roots**, run after
   the override switch when links are final (§4.5's `skin=` schedule), driven by the
   duplicate map (`RecordHandler.TryGetDuplicatedFormKey`): for each root, resolve the
   effective value — the donor's post-remap link when the content flag says "donor's",
   the recipient's effective value otherwise (§7.2) — look it up in the map, and if
   mapped, deliver through that root's channel: set the surrogate field for
   copyVisualStyle-carried roots, emit the directive (`race=` / `skin=` /
   `outfitDefault=`) for the rest. The per-field flags, the §4.3 emission-timing bug, and
   the §4.5 cross-NPC race accident all collapse into that one pass. The channel table is
   fixed by SkyPatcher's directive vocabulary — verify each root's directive against
   SkyPatcher's source (npc.cpp), never extend it by guesswork.

## 8. Open questions

- Should the outfit-side duplicate be delivered unconditionally for every `Include As New`
  NPC with outfit-rooted overrides, or only when the chain actually changes something the
  NPC would otherwise get wrong? Unconditional means many more `outfitDefault=` lines and
  more exposure to (4).
- Asset isolation: per-mod path prefix for every duplicated record's assets, or only when
  a collision with a differing vanilla/other-mod file is detected? The latter is cheaper
  and less invasive but needs a content comparison.
- Are there other shared-record classes reachable only through fields the surrogate does
  not carry? The audit so far covers Race/WornArmor/HeadParts/HeadTexture/HairColor
  (carried) and DefaultOutfit (stripped). `SleepingOutfit` is now unconditionally nulled
  and is not a rendering input, but the enumeration should be deliberate rather than
  incidental.
- Should `Include As New` warn when it mints a duplicate that nothing in the output
  references? That condition is cheap to detect after the remap and would have surfaced
  this immediately.
- ~~Confirm the §4.5 prediction~~ **Confirmed** (see §4.5): Kayd lacks `race=` in the
  current output. The §7.8 delivery pass is a bug fix for race too, not just outfit.
- `SleepingOutfit` is unconditionally nulled and (verify against SkyPatcher source)
  likely has no directive channel. Decide deliberately whether it is in or out of the
  §7.8 root enumeration rather than leaving it incidental.

## 9. Verification assets

Re-derive any of the above with:

- `tools\espdump\bin\Release\net10.0\espdump.exe "<plugin>" [FormKey ...] | --formids`
- Duplicated-record inventory: grep `NPC.esp` for EditorIDs suffixed
  `_RSkyrimChildren.esm`, `_RSChildren.esp`, `_Skyrim.esm`.
- Enable **Log Record Provenance** and **Log Asset Provenance** in Settings > Logging
  before the next run; both CSVs currently on disk predate this configuration.
