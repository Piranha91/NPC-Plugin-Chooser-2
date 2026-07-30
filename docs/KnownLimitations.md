# Known limitations

Behaviours that are understood, deliberate for now, and not defects to be re-diagnosed. Each entry
says what happens, where it lives, and what a fix would have to decide — so the next person can act
on it rather than rediscover it.

Anything in here has been verified against the code, not inferred. Line references are a starting
point, not a contract.

Last verified: 2026-07-30.

---

# Deliberate limitations

## 1. Manual wig designations are scoped by a different NPC in the preview than in the patcher

`Settings.IsWigArmature` takes an `npcFormKey` used only to evaluate `ManualWigBlockScope`. The
renderer passes the **terminus's** key (`NpcMeshResolver.ComputeWigHideHeadShapeNames`, whose
`npcGetter` came through `ResolveAppearanceNpcKey`), while `HeadPartWigConverter` and `WigForwarder`
pass the **donor's**. So under `SpecificNpc` scope a designation made in the 3D preview can be
stored against one key and read against another.

Invisible under the default `ManualWigBlockScope = AllNpcs`, where the key is ignored entirely.

**RESOLVED 2026-07-30 — the donor's key, fixed, no migration needed.** `NpcMeshResolver.Resolve` now
threads a `designationScopeKey` (the NPC the user actually has open) down to
`ComputeWigHideHeadShapeNames`, which passes it to `IsWigArmature` instead of the possibly-advanced
`npcGetter.FormKey`. Semantics pinned by `Tests/Unit/ManualWigDesignationScopeTests.cs`. The trace
that settled which key is canonical is kept below, because it is also the argument for why nothing
needed migrating.

Traced
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

**Reviewed 2026-07-30 and deliberately left as-is.** Written out in full because the reasoning is
what makes it a non-issue today, and that reasoning is exactly what a future change could invalidate.

There are two separate outfit operations in `WigForwarder.Apply`, and only one of them was updated
when Inventory-template handling landed:

- **Step 2, "add the wig to an outfit"** (`TryForwardToOutfit`, fires when there are pieces to add)
  asks `OutfitDisplayResolver.ResolveForDisplay` which outfit the actor will *actually wear*:
  plugin-level effective outfit, patch-mode aware, plus the SkyPatcher/SPID runtime layers — the same
  simulation the 3D preview draws. For an Inventory-templated NPC that resolves to the **template's**
  outfit, because the engine takes the whole inventory from the template.
- **Step 3, "remove the wig from an outfit"** (`StripWigsFromForwardedOutfit`, fires only when step 2
  produced nothing *and* Include Outfit is on) works off `donorOutfitGetter` — the **raw**
  `donorNpc.DefaultOutfit` read at the top of `Apply`. No chain resolution.

For a normal NPC those name the same outfit and there is no difference at all. For an
Inventory-templated NPC they name different outfits: step 2 would target the template's, step 3
targets the NPC's own.

**Why it is arguably right as-is.** Step 3 only runs on the Include-Outfit path, and on that path
`CopyAppearanceData` also writes the donor's *raw* `DefaultOutfit` onto the output record. So the
outfit NPC2 is writing to the record is the raw one, and stripping the wig out of that is
self-consistent. On top of that the record field is inert for Inventory-templated NPCs anyway — the
engine ignores it — so in game the whole question currently has no visible consequence.

**Why it is recorded anyway.** The two steps answer different questions — "what outfit does NPC2
write?" (raw donor) versus "what outfit does the actor wear?" (chain-resolved) — and step 3 picked
one without that being a decision anyone made. It fell out of step 2 being updated and step 3 not. A
future change that makes Include Outfit actually reach Inventory-templated NPCs (via SkyPatcher/SPID
distribution, which was considered and rejected once) would make the divergence live immediately, and
at that point it becomes a real bug rather than a latent inconsistency.

*A fix has to decide:* whether the strip follows the record NPC2 writes or the outfit the actor
wears. They are only the same NPC most of the time, and the answer probably depends on whether
Include Outfit stays record-only.

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

**Reported, not fixed, since 2026-07-29.** `AssetHandler.WarnOnFullyUnresolvedShapeTextures` flags a
NIF shape when *every* one of its texture slots is unresolvable in the selected mod, in the game
Data folder, and in the vanilla archives. Detection is deliberately per SHAPE, at the user's
direction: single missing textures are near-universal and harmless (an absent mouth subsurface map
is the stock example), while a shape with no resolvable texture at all renders untextured and is
worth acting on. Deduplicated by (mod, NIF, shape).

Reporting was revised 2026-07-30, also at the user's direction: one forced, warning-coloured line
per NPC at the end of the run (`ReportTexturelessShapes`, flushed after the copy tasks drain),
listing that NPC's affected shapes and their missing textures — several broken shapes on one NPC
used to produce a wall of near-identical per-shape lines. The dedup means a mesh shared by many
NPCs still surfaces once, on the first NPC that hit it.

**This is intended behaviour, not a pending fix** (user's decision, 2026-07-30). Linking a mod to the
assets it depends on is the user's job. Automating it would mean guessing that whichever installed
mod happens to supply a file at that path is the one the appearance mod meant — and a wrong guess
silently paints an NPC with another mod's textures, which is worse than the honest gap. So the
warning IS the feature here; do not "fix" this by broadening resolution.

Practical consequence to keep in mind when reading a bug report: an NPC with untextured hair or eyes
under an add-on mod is usually this, and the remedy is for the user to add the parent mod's folder to
the same `ModSetting`.

## 5. Rows 4/5 take the origin mesh ungated; a race override defeats "compatible by construction"

Found 2026-07-30 by the ladder-verification campaign (see the Resolved entry below), on real data.

`FaceGenLadder.ResolveOriginFallback` (rows 4 and 5) takes the mod-of-origin's face mesh
unconditionally whenever it exists, on the premise that the origin record — which the same branch
forwards — and the origin mesh are compatible *by construction*. Row 3 gates the same mesh on
`OriginNifCompatible`; rows 4/5 deliberately do not consult it, even though
`Patcher.ComputeFaceGenDecisionAsync` computes it for them (the CSV shows it evaluated).

**The premise fails when a third mod overrides the subject's RACE.** The engine builds a face from
the NPC's own head parts *plus the race's default head parts for unoccupied slots*
(`FaceGenConsistencyAnalyzer` models exactly this), and the race link on a faithfully-forwarded
vanilla record still resolves through the load order. Measured specimen (Tempus Maledictum 1_11,
2026-07-30): FaceGen-only selection of RedBag's Rorikstead's tints for **Britte
(`0136B9:Skyrim.esm`) and Sissel (`0136BA`)** with RS Children installed. The output record is a
verified-faithful vanilla copy (esp dump: vanilla child race `02C65B`, vanilla head parts), but
`02C65B` resolves to RS Children's override whose chargen head parts are the `0RCOChild*` set — so
the engine reconciles RCO EditorIDs against the forwarded vanilla mesh's baked `ChildEyes`/`ChildMouth`
shapes, fails, and renders the dark face. The ladder's own probe knew: both CSV rows carry
`OriginNifCompatible=False`. `Validate Output` catches the result (FaceGen warning naming `NPC.esp`),
so the failure is at least visible after the fact.

**Decided 2026-07-30: keep the assumption, warn per NPC.** Rows 4/5 still take the origin mesh
ungated. The reasoning: a mod that ships no face mesh is almost always authored against its
origin's data, and RS Children is the only mod known to break that premise in the wild — a hard
gate mirroring row 3 would spend its time refusing healthy NPCs (and for the Britte specimen, where
origin and winner both fail the probe, would turn a mostly-cosmetic risk into a hard abort). So
when the probe *positively* fails, the pairing ships anyway and
`FaceGenLadderDecision.OriginCompatWarning` carries a forced, warning-coloured line naming the NPC,
explaining that another installed mod appears to have changed its original head data, and telling
the user to spawn-test it in game (printed beside `TintWarning` in
`AssetHandler.ScheduleCopyNpcAssets`; pinned by the `OriginCompatWarning_*` tests in
`Tests/Unit/FaceGenLadderTests.cs`). `Validate Output` remains the after-the-fact detector, and its
FaceGen warning is the confirmation that a flagged NPC really did pair mismatched halves.

---

## Resolved

**The FaceGen ladder's unexercised paths (old entry #5) — verified against real data 2026-07-30.**
The face-swap destination and row 3's three legs were "proven only by unit tests"; a two-part
campaign (procedure lived in `docs/LadderVerification-Handoff-2026-07.md`, now deleted) closed it:

- **Face swap** (`FaceGenDestinationMode.FaceSwap`): Lydia (`0A2C8E:Skyrim.esm`) ← Mulush
  (`0133A9`) from Ordinary People, record mode. CSV row `Mode=FaceSwap, Row=1`; both halves landed
  at the *target's* path and nothing at the donor's; Validate Output clean; **in-game pass** (full
  donor appearance, correct tint, no seam). Note for future docs: Lydia's base record is `0A2C8E` —
  the `000A2C94` the old handoff quoted is her ref.
- **Row 3, winner leg**, both flavors, in Tempus Maledictum 1_11 (a Skyrim VR list): 87 NPCs across
  Teldryn Serious / Darkend / Skyrim on Skooma whose missing meshes are supplied by
  `PGPatcher_Output`. Record mode → `WinnerInPlace` (tint copied, mesh left in place); SkyPatcher
  mode → `Winner` (mesh copied to the surrogate's path — all 87 verified byte-identical to their
  PGPatcher sources). The compatibility gate evaluated `True` on every one, and Validate Output's
  independent mismatch detector raised zero findings on them. No VR spawn was performed: for
  `WinnerInPlace` the shipped trio (TSR record + PGPatcher mesh + TSR loose tint) is byte-for-byte
  what stock Tempus already renders in play, so the modlist itself is the in-game evidence.
- **Row 3, abort leg**, both modes, same trio each time: Dead Dunmer (`067775`), Zedras (`067777`),
  Little Ghost (`2466EC`) of `tsr_teldrynserious.esp`. The strongest form: a winner mesh *exists*
  but its baked shapes are `00KLH_`-renamed, the gate rejected it (`WinnerNifCompatible=False`),
  the forced end-of-run summary named all three, and the output contains no FaceGen, no token entry
  and no record for them (the abort precedes any write). Validate Output independently derived the
  same mismatch on the untouched load-order state — gate and validator agree.
- **Row 3, origin leg (the affirmative case): no real-world specimen exists.** Measured 2026-07-30
  across 9 modlists / ~25k mod folders (all four installs plus the live profile): every
  tint-without-mesh candidate fails one requirement — quest mods' own NPCs have no external origin;
  the vanilla-keyed strays (RedBag's Rorikstead, Men of Skyrim / True Sons / Women of Skyrim
  Refined) ship no plugin record for those NPCs (→ row 4); Skyrim on Skooma has records but changed
  head parts, so the gate correctly *rejected* the origin mesh (Jeremy `10D13E/3F/40` — the negative
  half of the leg, proven live). Every ingredient of the affirmative branch is separately proven on
  real data (origin BSA probe, compat evaluation, origin-copy execution via row 4); the conjunction
  — a mod that edits the record, keeps geometry head parts effectively vanilla, and ships the tint
  without the mesh — appears not to occur in the wild. It stays pinned by
  `Tests/Unit/FaceGenLadderTests.cs`; do not re-run the specimen hunt without new data.
- Two side-findings: `FaceGenLadderDiag` recorded but **never flushed** on a normal run (only the
  PatchVerify harness called Flush) — fixed 2026-07-30, `Patcher` now flushes it beside the two
  provenance diags; and the campaign surfaced the rows-4/5 origin-gate defect recorded as entry #5
  above (Britte/Sissel).

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
