# Known limitations

Behaviours that are understood, deliberate for now, and not defects to be re-diagnosed. Each entry
says what happens, where it lives, and what a fix would have to decide — so the next person can act
on it rather than rediscover it.

Anything in here has been verified against the code, not inferred. Line references are a starting
point, not a contract.

Last verified: 2026-07-28.

---

# Open defect — not deliberate, not yet fixed

## Record mode + Inherit half-applies a Traits-templated NPC, and dark-faces its terminus

**Measured in game 2026-07-28.** `PatchingMode = CreateAndPatch`, `UseSkyPatcherMode = false`,
`TemplateHandlingMode = InheritFromTemplate`, specimen `006E5C:Dawnguard.esm`
(DLC1VQ03VampireDriverDead), Traits-templated to `00887B:Dawnguard.esm` (Rogen), selected from High
Poly NPC Overhaul.

The FaceGen subject is always the Traits-chain terminus — `ComputeFaceGenDecisionAsync` derives
`subjectFormKey` from `Auxilliary.TryResolveAppearanceTerminus` (`BackEnd/Patcher.cs:2825`) and both
measures and copies at that record's paths. Under Inherit there is no flatten, so the patcher writes
the *selected* NPC's record. The two halves land on different records:

| | |
|---|---|
| selected NPC `006E5C`'s record patched | yes — but inert; the engine ignores a Traits-templated NPC's own appearance |
| mod's FaceGen written to `dawnguard.esm\0000887b.nif` (the TERMINUS's path) | yes |
| terminus `00887B`'s record patched | **no — absent from the output plugin** |

So the terminus renders the mod's mesh over its own unpatched record:

```
mesh came from   000806 / 00092A / 000932 (High Poly Head), HighPoly_HairBald
record resolves  HairMaleNord07, HumanBeard42, BrowsMaleHumanoid02, ...
```

Head parts and mesh disagree — dark face, on an NPC the user never selected.

**SkyPatcher mode is already guarded**; record mode is not. The validator rejects this exact shape
with *"SkyPatcher mode cannot redirect an inherited face. This selection will be skipped."*
(`BackEnd/Validator.cs`), and that guard is mode-scoped.

**Not reachable via Inventory-only templating.** The same run's two cultists (`034FC5`, `034FC3`)
are Inventory-templated but not Traits-templated, so the engine renders their own faces, the FaceGen
lands at their own paths, and they came out correct.

**Workaround:** also select the terminus. Its record is then patched from the same mod and matches
the mesh. (This differs from SkyPatcher mode, where selecting the terminus does *not* rescue an
inheritor — see `reference_skypatcher_template_chain_resolution`.)

*A fix has to decide:* whether Inherit mode should patch the **terminus** (record *and* FaceGen —
the only record the engine reads, but it changes appearance for every NPC sharing that terminus,
which is precisely what Inherit means), or should refuse to write the terminus's mesh unless the
terminus's record is being patched too, or should simply extend the validator's existing rejection
to record mode. The one thing it must not keep doing is writing half.

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

The first four entries in this file were fixed on 2026-07-28. Kept here only as a pointer for
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
