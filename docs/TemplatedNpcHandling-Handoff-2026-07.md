# Templated NPC handling — unify flattening across modes, behind a user toggle

Feature handoff. Nothing here is implemented. Written to be actionable without the session that
produced it.

**Companion document:** `Docs/FaceGenLadder-Handoff-2026-07.md` covers the FaceGen ladder this
builds on — read its §2 (template flattening asymmetry) and §6b (the orc Adventurers worked
example) first. Its §7 lists claims from that session that turned out to be wrong; don't inherit
them.

---

## 1. The problem

Some NPCs don't have a face of their own. They carry the **Traits** template flag and a link to
another NPC, and the engine renders *that* NPC's face. Skyrim uses this heavily for generic actors
— bandits, guards, encounter NPCs.

In NPC2 today, choosing an appearance mod for such an NPC is **inert**: the mugshot shows what you
picked, the game shows the template's face. The NPC menu already warns about this (Template icon +
notification), but the selection still silently does nothing.

Worse, it *cannot* be made to work by supplying files: every NPC inheriting from one template
resolves to that template's single shared FaceGen path. Two NPCs sharing a template can never look
different from each other, no matter what is selected for them.

**Worked example** (from the other handoff, §6b): two "Adventurer" NPCs, both templated to
`03DE70 EncBandit01Melee2HBerserkOrcM`. Both had *High Poly NPC Overhaul* selected. In game both
showed *Lawless - A Bandit Overhaul*'s face — the terminus's own selection — because that is what
the engine resolves. The High Poly selection was inert for both.

**395 NPCs** have a resolvable Traits chain in the reference load order, so this is not niche.

## 2. What already exists

Flattening — clear the Traits flag, copy the terminus's appearance onto the record, and give the
NPC its own FaceGen — **is already implemented, but only for SkyPatcher mode**, and it is
**unconditional** there.

| Mode | Donor is Traits-templated | Current behaviour |
|---|---|---|
| SkyPatcher | `SkyPatcherInterface.CreateSkyPatcherNpc` resolves the terminus, calls `CopyInheritedAppearance`, clears Traits | **always flattens** |
| Record | `Patcher.SyncTemplateInheritance` mirrors the donor's Traits state onto the output | never flattens |

Record mode only de-standalones in the *opposite* case: `Patcher.cs` clears Traits when the target
is templated and the **donor is not**.

This asymmetry was never a considered trade-off. Flattening was designed and decided in a
SkyPatcher-framed discussion, and record mode simply carried on doing what it already did.

## 3. What to build

**One behaviour, both modes, user-selectable, defaulting to today's behaviour.**

- **Default (inherit):** unchanged from today. A templated NPC keeps its Traits flag and shows the
  template's face in game, whatever its mugshot shows. **This is a change for SkyPatcher mode**,
  which currently always flattens — see §7.
- **Opt-in (own copy):** in *both* record and SkyPatcher mode, the patcher resolves the template
  chain, copies the terminus's appearance onto the NPC's own record, clears the Traits flag, and
  writes FaceGen under the NPC's **own** FormID. Each NPC then honours its own selection and two
  NPCs sharing a template can differ.

### Naming (proposal — settle before implementing)

"Flattening" is internal jargon; it should not reach the UI. NPC2's existing UI already says
"template" to users (the Template badge, and a notification reading *"Regardless of which mod you
select here… it'll use the appearance of the template"*), so template language is consistent rather
than novel.

Matching the house pattern of `WigHandlingMode` / `AntlerHandlingMode`, an enum reads better than a
bool and leaves room for a third option later:

```
Settings.TemplateHandlingMode
    InheritFromTemplate   (default)  UI: "Use the template's appearance"
    GiveEachNpcOwnCopy               UI: "Give each NPC its own copy"
```

Suggested UI grouping and help text:

> **Templated NPCs** — Some NPCs copy their face from another NPC instead of having one of their
> own.
> - *Use the template's appearance (default)* — they keep showing the other NPC's face, and any
>   appearance you pick for them is ignored.
> - *Give each NPC its own copy* — your choice is applied to each of them individually, so NPCs
>   that share a template can look different. Adds one copy of the face files per NPC.

Alternatives considered: "Unlink inherited appearances", "Apply selections to templated NPCs",
"Break appearance inheritance". Pick whichever survives a read-aloud test with a non-modder.

## 4. Design

The **subject** (where FaceGen is sourced from) is unchanged in both modes: the template chain
terminus. What the toggle changes is the **destination** and the **record**.

| | Inherit (default) | Own copy |
|---|---|---|
| Record | Traits mirrored from donor; NPC still inherits | Terminus's appearance fields copied in; **Traits cleared** |
| FaceGen destination | terminus's shared path | **the NPC's own FormID path** |
| Two NPCs, same terminus | forced identical | independent |
| Cost | one shared FaceGen copy | one FaceGen copy per NPC |

The field set to copy already exists and is proven: `SkyPatcherInterface.CopyInheritedAppearance`
(race, head texture, hair colour, worn armor, height, weight, texture lighting, head parts, face
morph, face parts, tint layers, and the Female flag — sex drives which head parts and FaceGen the
engine builds, so it must follow the face). **Lift it somewhere both modes can call it** rather
than duplicating; it is currently private to `SkyPatcherInterface`.

### Cases that must NOT flatten

Flattening requires a concrete terminus. `Auxilliary.TryResolveAppearanceTerminus` already
distinguishes these — use its `FaceGenChainStatus`:

- `LeveledTerminus` — the chain ends in a levelled list; the game picks an actor at runtime, so
  there is no fixed face to copy. **Must keep inheriting even when the toggle is on.**
- `Unfollowable` — dangling link, cycle, or over-long chain. The ladder already aborts these.
- `NotTemplated` — nothing to do.

Only `Resolved` is eligible.

## 5. Implementation map

Verify each of these against the code rather than trusting the list.

| File | Change |
|---|---|
| `Models/Settings.cs` | new `TemplateHandlingMode` enum + property, default `InheritFromTemplate` |
| `View Models/VM_Settings.cs` + its View | expose it; group near the other appearance-handling modes |
| `BackEnd/SkyPatcherInterface.cs` | `CreateSkyPatcherNpc` must become **conditional** — it currently always flattens. Also lift `CopyInheritedAppearance` for reuse |
| `BackEnd/Patcher.cs` | `SyncTemplateInheritance` (and/or `CopyAppearanceData`): when flattening and the chain is `Resolved`, copy the terminus's appearance and clear Traits instead of mirroring. Thread the mode to the asset stage |
| `BackEnd/AssetHandler.cs` | `ScheduleCopyNpcAssets`: the `ChainStatus == Resolved → SubjectFormKey` destination branch must yield the NPC's own path when flattening. The `destinationOwnedByAnotherNpc` deferral must be **disabled** when flattening — it exists only to arbitrate the shared path that flattening removes |
| `BackEnd/FaceGenLadder.cs` | likely needs the mode as an input so `FaceGenLadderDecision` (and the CSV) reflect the real destination |
| `BackEnd/PatchVerifyRunner.cs` | add a config knob so a verification run can exercise either setting |
| Tests | both modes × both settings; the levelled-terminus carve-out; two NPCs sharing a terminus with *different* selections resolving independently |

## 6. How to verify

The specimen already exists and is documented: the two orc Adventurers (`083279`, `0C176B`, both
`Skyrim.esm`), both templated to `03DE70`, with *High Poly NPC Overhaul* selected, while `03DE70`
itself is set to *Lawless - A Bandit Overhaul*.

- **Default setting** → both Adventurers show Lawless's horned red-warpaint orc (today's behaviour,
  already photographed).
- **Own-copy setting** → both should show High Poly's face, and `03DE70` itself should still show
  Lawless's. That single comparison proves the record edit, the FaceGen destination change, and
  per-NPC independence at once.
- **Stronger check:** temporarily give the two Adventurers *different* mods. Under the default they
  must stay identical; under own-copy they must differ. That is the property inheritance cannot
  provide.

Use `PatchVerify.json` for the run and the generated spawn bats (see the ladder handoff §9); it
must be launched **through MO2** or it sees the raw Steam load order. Run it in **both** record and
SkyPatcher mode — SkyPatcher flattening has never once been exercised in game, even though it is
the mode where it is currently unconditional.

## 7. Risks and things not to break

- **This changes SkyPatcher's current behaviour.** It flattens unconditionally today; after this it
  must inherit by default. Anyone who has run SkyPatcher mode recently has flattened output. Call
  this out in release notes.
- **Saved selections gain meaning.** Selections on templated NPCs are inert today; switching the
  toggle on makes 395 of them (this load order) suddenly apply. That is the point, but it is a
  visible change, which is why the default is off.
- **Output size.** Own-copy duplicates FaceGen per NPC instead of sharing one terminus copy. Bounded
  by the templated population, but measure it on a real run.
- **Do not change the subject.** FaceGen still comes *from* the terminus. Only the destination and
  the record change. Getting this backwards would source from an NPC that has no face.
- **The `destinationOwnedByAnotherNpc` guard is load-bearing in the default mode.** Removing it
  outright rather than conditioning it would let a template-follower stamp its choice onto the
  terminus's own output.

## 8. Related, deliberately out of scope

- **Mugshot/menu truthfulness in the default mode.** When inheriting, a templated NPC's mugshot
  shows a face the game will never render. The `ChainTrace` column in `FaceGenLadder.csv` already
  carries the terminus and its selection, so `VerifyManifest` (and arguably the NPC menu, behind
  the existing Template badge) could show or annotate the terminus's mugshot instead. Worth doing
  regardless of this feature; becomes unnecessary only for NPCs under the own-copy setting.
- The zero-head-part menu exclusion and OutputValidator alignment from the ladder handoff §8.
