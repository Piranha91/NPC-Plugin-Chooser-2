# Known limitations

Behaviours that are understood, deliberate for now, and not defects to be re-diagnosed. Each entry
says what happens, where it lives, and what a fix would have to decide — so the next person can act
on it rather than rediscover it.

Anything in here has been verified against the code, not inferred. Line references are a starting
point, not a contract.

Last verified: 2026-07-29.

---

# Deliberate limitations

## 1. Manual wig designations are scoped by a different NPC in the preview than in the patcher

`Settings.IsWigArmature` takes an `npcFormKey` used only to evaluate `ManualWigBlockScope`. The
renderer passes the **terminus's** key (`NpcMeshResolver.ComputeWigHideHeadShapeNames`, whose
`npcGetter` came through `ResolveAppearanceNpcKey`), while `HeadPartWigConverter` and `WigForwarder`
pass the **donor's**. So under `SpecificNpc` scope a designation made in the 3D preview can be
stored against one key and read against another.

Invisible under the default `ManualWigBlockScope = AllNpcs`, where the key is ignored entirely.

**Which key is canonical is now settled — the DONOR's — and no migration is needed.** Traced
2026-07-30: the UI stores a designation under the key `VM_InternalMugshotPreview.PopulateWigSelector`
was called with, which is the preview's loaded `formKey`, i.e. the appearance donor (the same call
site notes that `targetNpcFormKey` "differs from formKey for guest appearances"), and
`GetWigArmatureCandidates` enumerates the offered rows from that same key. The patcher then reads
back under `donorNpc.FormKey`. So the write side and the patcher already agree; the ONLY divergent
reader is `NpcMeshResolver.ComputeWigHideHeadShapeNames`, whose `npcGetter` arrives via
`ResolveAppearanceNpcKey` and is therefore the terminus for a templated NPC.

*The fix* is therefore one-sided and cheap: thread the original donor key into
`ComputeWigHideHeadShapeNames` and pass it to `IsWigArmature` instead of `npcGetter.FormKey`. Nothing
stored changes, so the migration the previous version of this entry warned about does not apply — it
assumed the write side might be the terminus, and it is not.

## 2. RESOLVED — the effective-WNAM-wig walk is now one function

Was: the same walk (iterate a WornArmor's `Armature`, resolve each ARMA, keep the ones
`Settings.IsWigArmature` accepts) written out five times, across three different record-resolution
mechanisms, with three of the five applying a race filter the other two did not.

Consolidated 2026-07-30 into `WigDetector.EffectiveWnamWigArmatures(wnam, resolveArma,
isEffectiveWig, extraFilter)`. The differences that were real stay as parameters: each caller injects
its own resolver (mod plugins first / render scopes / deployed load order), its own scope key, and its
own optional narrowing. Two properties are load-bearing and pinned by
`Tests/Unit/WigDetectorWnamWalkTests.cs`: the walk does **not** deduplicate (two callers act only when
there is exactly ONE effective wig ARMA, so collapsing a doubled armature entry would turn a declined
conversion into an applied one), and `extraFilter` runs **before** the wig test (so a manual
designation cannot resurrect an armature the NPC's race is not served by).

One deliberate behaviour change came with it: `OutputValidator.WigForwardingRemovesHair` used to test
an armature link whose record resolved NOWHERE, matching a FormKey against `DetectedWigArmatures` with
a null EditorID. The converter it exists to mirror skips unresolvable armatures and so removes no
hair, meaning the validator disagreed with the patcher on exactly the broken mods where it matters.
It now skips them too.

`WigForwarder`'s hair-slot narrowing still tests `BipedObjectFlag.Hair` (31) alone rather than
`WigDetector.HairSlots` (31|41), and must keep doing so: it has to agree with `BuildSkinDuplicate`'s
`transfersHairSlot`, which is also Hair-only. Widening one without the other lets a LongHair-only
piece drive hair removal down one path and not the other.

## 3. `StripWigsFromForwardedOutfit` reads the raw donor outfit

`WigForwarder.TryForwardToOutfit` picks the outfit to duplicate through `OutfitDisplayResolver`
(which now follows the Inventory template chain), but `StripWigsFromForwardedOutfit` still works off
`donorOutfitGetter` — the raw `donorNpc.DefaultOutfit` read at the top of `Apply`. For an
Inventory-templated NPC the two now name different outfits.

Arguably correct as-is: that strip runs on the Include-Outfit path, where `CopyAppearanceData` also
copies the donor's raw field. But it is a divergence, and it was introduced deliberately rather than
noticed.

*A fix has to decide:* whether the strip belongs on the record NPC2 writes (raw donor) or on the
outfit the actor wears (chain-resolved) — they are only the same NPC most of the time.

## 4. Asset resolution never leaves the selected mod

`AssetHandler.FindAssetSource` searches the selected `ModSetting`'s folders and the BSAs of its
`CorrespondingModKeys`, and nothing else. An add-on mod that references assets owned by a *sibling*
mod therefore loses them — the classic shape is a "De-Standalone" conversion whose plugin points at
textures that ship in its parent mod. A miss returns `NotFound` and the copy task completes as a
no-op.

This is the root cause of the Adrianne Avenicci case (Bijin AIO De-Standalone; hair, brow and eye
textures absent in game while the face itself was correct). Note what it is NOT: those head parts
carry their own TextureSets and are not regions of the face tint, so no FaceGen-ladder choice could
ever have affected them — a whole session was spent on that wrong theory.

**Reported, not fixed, since 2026-07-29.** `AssetHandler.WarnOnFullyUnresolvedShapeTextures` emits a
forced, warning-coloured run-log line when *every* texture slot of a NIF shape is unresolvable in
the selected mod, in the game Data folder, and in the vanilla archives. Deliberately per SHAPE, at
the user's direction: single missing textures are near-universal and harmless (an absent mouth
subsurface map is the stock example), while a shape with no resolvable texture at all renders
untextured and is worth acting on. Deduplicated by (mod, NIF, shape).

**This is intended behaviour, not a pending fix** (user's decision, 2026-07-30). Linking a mod to the
assets it depends on is the user's job. Automating it would mean guessing that whichever installed
mod happens to supply a file at that path is the one the appearance mod meant — and a wrong guess
silently paints an NPC with another mod's textures, which is worse than the honest gap. So the
warning IS the feature here; do not "fix" this by broadening resolution.

Practical consequence to keep in mind when reading a bug report: an NPC with untextured hair or eyes
under an add-on mod is usually this, and the remedy is for the user to add the parent mod's folder to
the same `ModSetting`.

## 5. FaceGen ladder rows that no run has exercised

The ladder (`BackEnd/FaceGenLadder.cs`) is fully unit-tested, but two of its paths have never run
against real data. Recorded here so they are not mistaken for proven.

**Row 3 — mod ships a face tint, no mesh, and edits the record.** Zero instances in the reference
load order, so the compatibility gate and the winner fallback are unproven outside unit tests. They
are, however, *reachable*: a filesystem census on 2026-07-29 over 20,707 mod folders in 12 modlists
(`C:\Games\Skyrim AE`, `C:\Games\Skyrim VR`, `S:\Dev\MO2`, `X:\Games\Skyrim Wabbajacks`) found 3,754
mod/plugin/FormID combinations with a loose facetint `.dds` and no matching facegeom `.nif`. Treat
that number as an upper bound — 93% of the hits are in mod folders that also ship a `.bsa`, where the
mesh is most likely packed rather than absent. The 276 hits in mods with **no BSA at all** are the
real candidates; the largest, all in `Tempus Maledictum 1_11`, are Teldryn Serious
(`TSR_TeldrynSerious.esp`, 68), Darkend (`Darkend.esp`, 39), Hearthfire Extended
(`HearthFireExtended.esp`, 27) and Strongholds - Mor Khazgur (22). LoreRim has exactly one:
`katana.esp` `001724E6`. To exercise the branch, select one of those mods for one of its NPCs and
run a patch; note that a mod which is also the NPC's *origin* will exercise the abort leg rather than
the origin-forward leg.

**Face-swap destination mode** (`FaceGenDestinationMode.FaceSwap`, shared/guest appearances) has
never been verified in game either. Unlike row 3 this needs no specimen hunt — it is selection-driven,
so any install reproduces it by sharing one NPC's appearance with another.

**Step-by-step procedure for both, with the candidate mods and their FormIDs:**
`docs/LadderVerification-Handoff-2026-07.md`. Delete that file once this entry can be deleted.

---

## Resolved

**What `GiveEachNpcOwnCopy` produces when the selected mod ships no FaceGen at the terminus's path**
(was the standing open question here). **Decided 2026-07-30:** it should read exactly like inheriting
from a template that has no selection of its own — the NPC keeps the face it would have had, and the
user is TOLD their choice could not be delivered.

The classification already produced the right face: with nothing from the mod and nothing from the
origin, the ladder copies the terminus's load-order-winning FaceGen onto the NPC's own path, which in
game is the face it already had. What was missing was saying so. Under `InheritFromTemplate` the
equivalent case gets a forced end-of-run report naming every NPC
(`Patcher.ReportInheritedFaceNpcs`); under `GiveEachNpcOwnCopy` it was a verbose-only line, a
difference the user had no way to predict. `FaceGenLadderDecision.FlattenedFaceCameFromElsewhere` now
drives a matching `Patcher.ReportFlattenedFallbackNpcs`.

It requires the mod to have supplied **neither** half. A tint-only mod (row 3/4) really is applying
the user's choice to the face — only the geometry is borrowed — so reporting that as undeliverable
would be false and would bury the real cases. Pinned by the four `Flatten_*` / `*FlattenedFallback*`
tests in `Tests/Unit/FaceGenLadderTests.cs`.

**Record mode + Inherit half-applied a Traits-templated NPC and dark-faced its terminus** (measured
in game 2026-07-28, fixed the same day). Specimen `006E5C:Dawnguard.esm`, Traits-templated to
`00887B:Dawnguard.esm` (Rogen), selected from High Poly NPC Overhaul: the mod's FaceGen was written
at the TERMINUS's path (where the engine reads it) while the record patched was the SELECTED NPC's,
so a mod's mesh rendered against Rogen's unpatched vanilla head parts.

The rule now enforced: **a FaceGen file may only be written to a FormKey's path by the pass that
patches that FormKey's record.** `FaceGenLadder.KeepsInheritedFace` short-circuits an inheriting
NPC to no source at all, and `AssetHandler`'s destination is always the record this pass writes
(surrogate / face-swap target / the NPC's own — never the terminus's). Those NPCs are patched
normally and keep showing their template's face, which is what `InheritFromTemplate` has always
promised (the enum doc, the settings comment and the NPC menu's template tooltip all say so);
`Patcher.ReportInheritedFaceNpcs` now names them at the end of the run and points at
`GiveEachNpcOwnCopy`, distinguishing the case where the template has its own selection (the NPC does
change, to the template's choice) from where it has none (it does not change at all). A screening
rejection was considered and rejected: these selections are inert by design, not invalid, and the
record-mode nuance that selecting the terminus DOES rescue an inheritor makes "cannot be applied"
untrue here. The SkyPatcher rejection is unchanged.

The `destinationOwnedByAnotherNpc` deferral went with it — nothing writes to another NPC's path any
more, so there is no contention left to arbitrate. Covered by matrix specimen #9
(`SpecimenRole.TemplatedOrphan`, a direct selection whose terminus has none) and #6's donor terminus
(the same shape through an appearance swap); negative-controlled — reverting the fix fails that
check in exactly the three inherit cells and nowhere else.

---

The first four entries below were fixed on 2026-07-28. Kept here only as a pointer for
anyone who read the old version:

1. **Include Outfit inert on Inventory-templated NPCs** — now reported. `Patcher` consults
   `RecordOutfitIsInert` independently of the wig branch and emits a per-NPC forced log line plus an
   end-of-run summary (`ReportInertOutfitNpcs`). The write itself is left in place; it is harmless.
   Distributing the outfit through SkyPatcher/SPID was considered and rejected for now — it would
   make record-mode output silently require SkyPatcher.
2. **The 3D preview disagreed with the game for those NPCs** — `OutfitDisplayResolver` now models
   the flag (`ResolveInventoryOutfitSource`), depicts the template's outfit, and exposes
   `RecordOutfitInert` / `InventoryTemplateSource` plus a warning through the existing
   `WarningText` surfaces. `ComputeWigIdentitySuffix` follows the same walk.
   Covered by `Tests/Integration/TemplateMatrix/OutfitDisplayInventoryTemplateTests.cs`.
3. **The wig→HeadPart converter read the DONOR record, not the terminus** — this turned out to be a
   live bug, not the "unproven either way" the old entry described: the flatten replaces the
   record's head parts with the terminus's *before* `FinalizeNpcRecord` removes the donor's, so the
   removal matched nothing and the terminus's hair rendered alongside the minted wig. Sex, race,
   weight and hair colour were unguarded entirely. Both `HeadPartWigConverter` and `WigForwarder`
   now take a `flattenTerminusNpc` and read every Traits-governed field off it;
   `Patcher.ResolveAppearanceTerminusRecord` was hoisted above the wig pass to supply it.
   `DefaultOutfit` deliberately still reads the donor — it is Inventory-governed, not Traits.
   Covered by `WigRouteTwoModeTests.Route8/Route9` and the `Apply_Terminus_*` unit tests.
4. **ForwardToSkin did not remove hair for an already-skin-carried wig** — `WigForwarder.Apply` now
   collects hair removal for the effective hair-slot wig set already on the WornArmor, not only for
   what this run transferred. `Route2_ForwardToSkin_SkinCarriedWig_KeepsTheWigAndRemovesTheClashingHair`
   pins the new behaviour; `Route2b` pins the slot gate that keeps a circlet-slot piece from balding
   the NPC.
