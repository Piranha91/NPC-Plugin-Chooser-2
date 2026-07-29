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

*A fix has to decide:* which key is canonical — and then migrate existing `SpecificNpc`
designations, because changing the key silently invalidates the ones users already made.

## 2. The effective-WNAM-wig walk is duplicated in five places

The same shape — walk a WornArmor's `Armature`, resolve each ARMA, keep the ones
`Settings.IsWigArmature` accepts — appears in `WigForwarder.CollectWnamWigArmas`,
`HeadPartWigConverter.CollectWnamWigArmas`, `OutputValidator` (~`:1197`), `NpcMeshResolver` (~`:777`)
and `OutfitDisplayResolver` (~`:647`). They use three different record-resolution mechanisms
(`ResolveFromModsOrWinner`, `ResolveRecord`, `TryGetRecordFromMods`) and three of the five add a
race filter the forwarder does not.

Not currently causing a defect — but five copies of a predicate is five places for the next change
to miss one.

*A fix has to decide:* the extractable core is an iteration helper with the resolver and the
predicate injected, e.g. `WigDetector.EffectiveWnamWigArmatures(wnam, resolveArma, isEffectiveWig,
extraFilter)`. Worth doing on its own, not folded into a behavioural change.

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

*A fix has to decide:* whether cross-mod asset resolution is permitted at all, and if so in what
order (the mod's own folders must keep winning) and how the borrowed file is attributed in
`AssetProvenance.csv`. It also has to decide what happens when the sibling mod is not installed —
the warning is the correct outcome there, so any fix has to keep it for that case.

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

---

## Open question, not a limitation

**What should `GiveEachNpcOwnCopy` produce when the selected mod ships no FaceGen at the terminus's
path?** The record and mesh then come from the pre-patch load-order winner of the terminus rather
than from the chosen mod. The template matrix sidesteps this by giving every fixture both FaceGen
halves (`TemplateFixtureBuilder.WriteFaceGen`), so no test encodes an expectation — deliberately, so
that whatever the code currently emits does not silently become the spec. Decide the correct
behaviour before writing an assertion for it.

`Tests/Unit/FaceGenLadderTests.cs` (the "Template flattening" section) is where a decided
expectation would most cheaply land — pure `Classify` inputs, no fixture I/O.

---

## Resolved

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
