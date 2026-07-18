# Outfit Rendering Audit — July 2026

Systematic audit of the outfit/attire rendering path (NPC2 `BackEnd/CharacterViewerHost` +
`CharacterViewer.Rendering`), motivated by three bugs that shipped in quick succession:

1. **AlternateTextures matched by name only** — BodySlide-renamed shapes silently missed
   (fixed: name-first matching with 3D-index fallback, `f63c906` + `ede7465` + CV.R `e3ff3c12`).
2. **TXST TX02 (EnvironmentMask/SubsurfaceTint) dropped; TX06 mis-slotted** — variant TXSTs
   swapped the cubemap but kept the NIF's stale env mask (Caenarvon Gala dress rendered gold
   instead of navy; fixed: `5d592db`).
3. **Preview render log closed before the apply** — the renderer-side matching was invisible
   in `_Preview.txt` (fixed: capture spans to SceneCommitted, `5d592db` + CV.R `412bf843`).

All three were **convention-translation errors** at the seams between: Mutagen TXST record
fields ↔ NIF `BSShaderTextureSet` slot order ↔ renderer texture slots ↔ GL texture units,
and NIF shader flags ↔ GL state. This audit swept every such seam.

## Audit scope & verified-clean items

Inventoried every shader-flag constant, shader-type gate, texture-slot literal, alpha-property
bit, and TXST field mapping across `NifMeshBuilder.cs`, `VM_CharacterViewer.cs`, `GlMesh.cs`,
`GlRenderer.cs`, `basic.frag`, and `NpcMeshResolver.cs`. Verified **correct** against the
NifSkope/Bethesda spec (do not re-litigate without new evidence):

- SLSF1 bits 0/4/7/10/12/17/18/22/26/27 and SLSF2 bits 0/4/5/6/25/26/27 — all match spec,
  in both the NifMeshBuilder named constants and the raw literals in VM_CharacterViewer.
- Shader types 1/4/5/6/16 (EnvMap/Face/SkinTint/HairTint/Eye) routing, incl. QNAM tint gates.
- NiAlphaProperty: blend=bit0, test=bit9, src=bits1-4, dst=bits5-8, threshold/255;
  `MapBethesdaBlendFactor` matches NifSkope's `blendMap` (0=ONE … 10=SRC_ALPHA_SATURATE).
- TXST→slot mapping post-`5d592db` (TX02 dual-write to 2+5, TX06→6).
- `AlternateTextureMatching` name-first / dangling-only index fallback semantics.
- LeveledItem Use-All collection (`74d024f`), collision-proxy cull, face alpha-test
  suppression, sequin bumped-normal env reflection (`f0df822a` — `transpose(mat3(u_view))`
  is a valid inverse for the orthonormal view rotation).

## Findings

| ID | Severity | Summary | Status |
|----|----------|---------|--------|
| AUD-1 | **Medium-High** | Flat NAM0 skin TXST applied to every shape of an attire override | Open |
| AUD-2 | Medium | TXST TX03 (Glow/Detail) routed only to slot 2; clobbers skin SSS | Open |
| AUD-3 | Medium-Low | Specular enabled by slot-7 map presence even when SLSF1_Specular is clear | Open |
| AUD-4 | Low (gap) | Glow maps unimplemented; External_Emittance ignored | Open |
| AUD-5 | Low | Eye detection is case-sensitive name heuristic; eye cubemap scale misapplication | Open |
| AUD-6 | Low (watch) | NiAlphaProperty with no bits set forces alpha-test on | Open |
| AUD-7 | Low (edge) | Duplicate shape names: name match fans out; blocks index fallback | Open |
| AUD-8 | Info | Trace-only missing-texture log lines; unverified folder-precedence convention | Open |

Each issue below carries: the defect, engine-correct behavior, **trigger conditions** (what
record/NIF data makes it visible — this is what the affected-NPC scan searches for), the
proposed fix, and the verification plan.

---

### AUD-1 — Flat NAM0 skin TXST applied to ALL shapes of an attire override

- **Code:** `VM_CharacterViewer.ApplyOneMeshOverride` — the `ov.Textures` merge into
  `effectiveTextures` has no shader-type gate. Contrast with the base-scene path, which
  applies ARMA skin TXST overrides **only** to `ShaderType == 5` shapes
  (`VM_CharacterViewer.cs` ~4070 and the `InstallOneShape` comment), and with the armor
  skin-inheritance block which treats NAM0 as a per-slot *skin* override.
- **Engine behavior:** ARMA `SkinTexture` (NAM0) replaces the texture set of the addon's
  **skin-shader shapes** (exposed arms/midriff on revealing armor). Metal/cloth shapes keep
  their own texture sets.
- **Ours:** every shape of the override NIF — including non-skin armor material — gets the
  skin TXST slots merged over its embedded set.
- **Trigger conditions:** an NPC whose effective outfit (or WornArmor) contains an ARMO whose
  ARMA has NAM0/NAM1 set **and** whose world NIF contains at least one non-ShaderType-4/5
  shape. Common on modded "revealing"/3BA outfits; vanilla candidates from scan.
- **Fix:** gate the flat `ov.Textures` merge to `b.ShaderType == 4 || b.ShaderType == 5`,
  mirroring the base path (2 lines + comment).
- **Verify:** before/after full-body render of a wearer; before = armor material shows skin
  diffuse; after = armor material shows its authored textures, exposed skin still matches race.

### AUD-2 — TXST TX03 (Glow/Detail) routed to slot 2 only

- **Code:** `NpcMeshResolver.PopulateTxstSlots` (~line 1365): `GlowOrDetailMap → slot 2`,
  overwriting the TX02 subsurface write for the same slot.
- **Engine behavior:** TX03 is dual-purpose like TX02 — glow map for emissive shaders (NIF
  slot 2), detail map for FaceGen heads (NIF slot 3). The engine routes by shader.
- **Ours:** a skin-shader shape receiving a TXST that authors TX03 gets its `_sk` subsurface
  sampler replaced by a glow/detail texture (wrong content in the SSS path). For non-skin
  shapes the value is inert (CV.R has no glow sampler — see AUD-4).
- **Trigger conditions:** a TXST referenced by ARMA NAM0 **or** an AlternateTextures entry,
  with `GlowOrDetailMap` non-empty, applied to a shape with ShaderType 4/5.
- **Fix:** mirror the TX02 dual-write — TX03 → slots 2 **and** 3 (slot 3 is only consumed
  under the FaceGen-detail flag, so the extra write is safe); keep TX03 winning slot 2 over
  TX02 only for non-skin shapes, or simply document that authored-TX02+TX03 skin TXSTs are
  pathological. Decide at fix time with a real specimen if the scan finds one.
- **Verify:** before/after on a scanned specimen (skin shape with TX03-bearing TXST).

### AUD-3 — Specular forced on by slot-7 map presence

- **Code:** `VM_CharacterViewer.cs` ~4538-4543: loading a slot-7 specular map sets
  `HasSpecular = true` unconditionally. The no-map branch (~4549) correctly consults
  SLSF1 bit 0.
- **Engine behavior:** SLSF1_Specular gates the entire specular term; a shape with an
  authored `_s.dds` but the flag clear renders without specular.
- **Trigger conditions:** worn-mesh shape with a slot-7 texture path and
  `(ShaderFlags1 & 1) == 0`.
- **Fix:** `HasSpecular = (built.ShaderFlags1 & (1u << 0)) != 0` in both branches;
  `HasSpecularMap` can stay tied to the texture.
- **Verify:** before/after on a scanned specimen; expect sheen removed only on flag-clear
  shapes.

### AUD-4 — Glow maps unimplemented; External_Emittance ignored

- **Code:** `basic.frag` has no glow sampler; emissive = `OwnEmit` (SLSF1 bit 22) color ×
  multiple only. SLSF2_Glow_Map (bit 6) and SLSF1_External_Emittance (bit 29) are read
  nowhere.
- **Engine behavior:** glow-mapped gear (enchanted armor, glowing eyes) samples the slot-2
  glow texture and adds it emissively.
- **Trigger conditions:** worn/facegen shape with SLSF2_Glow_Map set and a slot-2 path,
  shader type not 4/5.
- **Fix (scoped):** this is a feature gap, not a mis-translation. Minimum viable: load slot 2
  as a glow texture for non-skin shapes when Glow_Map/OwnEmit is set and add
  `glowTex.rgb * emissiveColor * emissiveMultiple`. Decide priority after the scan shows how
  common it is in real loadouts.
- **Verify:** before/after on a glow-gear wearer (expect glow to appear; nothing else moves).

### AUD-5 — Eye detection heuristics

- **Code:** `VM_CharacterViewer.cs` ~4606: `ShaderType == 16 || ShapeName.Contains("Eyes",
  Ordinal)`.
- **Risk A:** mod eye shapes named `eyes`/`EYES`/localized names miss `is_eye` → they get
  SSAO rim darkening and the wrong cubemap scale.
- **Risk B:** a non-eye shape with "Eyes" in its name (e.g. an "Eyes of the Falmer" pendant
  shape) is treated as an eye → `eyeCubemapScale` applied instead of `envMapScale`.
- **Fix:** case-insensitive compare for A; for B, additionally require the shape to be part
  of the head/facegen mesh or shader type 16/1. Low urgency — scan first to see if any real
  data hits it.
- **Verify:** specimen-dependent.

### AUD-6 — NiAlphaProperty with neither bit set forces alpha-test

- **Code:** `NifMeshBuilder.cs` ~2003-2006: property present, blend bit 0 clear, test bit 9
  clear → `hasAlphaTest = true` ("matches NPC Portrait Creator behavior").
- **Engine/NifSkope behavior:** neither bit → opaque.
- **Risk:** with a nonzero threshold byte, fragments below threshold get discarded on a shape
  the engine draws opaque (visible holes).
- **Action:** watch item. Scan for `flags & 0x201 == 0 && threshold > 0` on worn/facegen
  meshes; if specimens exist, render one and decide against in-game truth before changing —
  the current behavior is deliberate and may be masking a different historical issue.

### AUD-7 — Duplicate shape names vs name-first matching

- **Code:** `AlternateTextureMatching.MatchForShape` — a name-matched entry applies to
  *every* shape bearing that name, and a name that matches anywhere keeps the entry out of
  the index-fallback pool.
- **Engine behavior:** applies by 3D index — exactly one shape.
- **Trigger conditions:** an outfit NIF with duplicate shape names that is targeted by
  AlternateTextures. BodySlide output can produce duplicates.
- **Action:** edge case, now log-visible (the per-shape verdict lines in `_Preview.txt` /
  `_Mugshot.txt` show double application). Fix only if the scan finds a real specimen:
  prefer exact-ordinal match among same-named shapes when the entry's index also matches one.

### AUD-8 — Logging / consistency notes

- `WIREFRAME-FALLBACK` / `CULLED` missing-texture dispositions go to `Trace.WriteLine` only
  (`VM_CharacterViewer.cs` ~4777) — they never reach the RenderLogs captures. Route through
  the logger when touching that code next.
- `NpcMeshResolver.RebaseToAbsoluteIfPresent` (~1176) probes `PreferredFolderPaths` in
  **reverse** order. Confirm this matches the precedence used by `PluginProvider`/asset
  resolution elsewhere (last-folder-wins vs first-folder-wins). No observed misbehavior;
  consistency check only.

---

## Step 2 — Finding affected NPCs (scan plan)

**Implemented:** `BackEnd/CharacterViewerHost/AuditScanRunner.cs` — drop an `AuditScan.json`
next to the exe (`{ "exitWhenDone": true }` suffices; optional `outputDirectory`,
`parseNifs`, `maxRowsPerIssue`) and launch the app. After startup it writes
`AuditScanOutput/AuditScanReport.csv` + `AuditScan.log` and exits. **Launch through MO2**
for the real modlist; a direct launch scans the raw Steam load order (vanilla census).
Delete `AuditScan.json` afterwards — it re-triggers on every launch. Detectors implemented:
AUD-1/2/3/4/5/6/7 (AUD-6 uses the new `BuiltMesh.AlphaFlagsRaw` in CV.R to distinguish the
forced fallback from authored alpha-test).

Original design notes:

- Walk every NPC in the load order → effective WornArmor + outfit ARMO set (reuse
  `NpcMeshResolver`'s resolution, which already handles outfits/LVLI/SkyPatcher).
- For each reachable ARMA: record NAM0 presence (AUD-1), NAM0/alt-tex TXSTs with TX03
  (AUD-2); parse the world NIF once with nifly (concurrent loads are safe) and record:
  shader types per shape (AUD-1 confirmation), slot-7-with-flag-clear shapes (AUD-3),
  Glow_Map/slot-2 shapes (AUD-4), alpha-property `flags==0 && threshold>0` (AUD-6),
  duplicate shape names + alt-tex targeting (AUD-7).
- FaceGen/headpart NIF pass for AUD-5 (`/eyes/i` name census).
- Output: `AuditScanReport.csv` — one row per (issue, NPC example, plugin, record, NIF,
  shape, detail) — plus a summary count per issue. From that we pick one **specimen NPC per
  issue** for the before/after images.

**Data needed from the user:** none up front — the mods live at
`S:\Skyrim NPC Selection\mods` and the scan runs inside MO2 with the existing profile. If
the scan finds *no* specimen for an issue in the current modlist (plausible for AUD-2/5/6/7),
options are: install a known-affected mod (I'll name candidates), author a tiny test plugin +
NIF, or park the issue as "no real-world exposure in this loadout."

## Step 3 — Before/after comparison images

Protocol (constrained by measured cross-process GPU nondeterminism — see the determinism
remarks in `RenderHarnessRunner.cs`):

1. One harness process per build ("before" = current master, "after" = fix branch), same
   `RenderHarness.json`, `"burnInRenders": 1`, launched through MO2.
2. Output folders named by git SHA: `Audit/before-<sha>/`, `Audit/after-<sha>/`.
3. Comparison is **visual side-by-side** plus a tolerant pixel diff (±2/255 fuzz). A global
   ~+2.8-luma warm shift on skin/hair is a known benign per-process driver state — if the
   whole image shifted uniformly, re-run the process once before reading anything into it.
4. Full-body framing via a Manual-camera variant, e.g.:

```json
{
  "outputDirectory": "S:\\temp\\npc2-audit\\before-<sha>",
  "exitWhenDone": true,
  "burnInRenders": 1,
  "renders": [
    { "modName": "Pandorable's NPCs", "npcFormKey": "01326A:Skyrim.esm", "fileName": "elisif-gala.png" }
  ],
  "variants": [
    { "name": "fullbody", "settings": {
        "CameraMode": "Manual",
        "ManualDistance": 260, "ManualElevation": 0,
        "ManualTargetY": 90, "OutputWidth": 900, "OutputHeight": 1400 } }
  ]
}
```

(Delete `RenderHarness.json` after each run — it re-triggers on every launch.)

## Step 4 — Reference regression set

Fixed set of NPC+outfit combos rendered before every CV.R/resolver change, kept under
`Docs/Screenshots/RenderReference/` (small PNGs + the harness JSON that produced them).
Seed set from recent investigations — FormKeys marked TBD to be filled from the next
harness/scan run rather than guessed:

| Ref | NPC | Appearance mod | Outfit / why it's here |
|-----|-----|----------------|------------------------|
| R1 | Elisif the Fair `01326A:Skyrim.esm` | Pandorable's NPCs | Caenarvon Gala dress — alt-textures name match **and** index fallback, env cubemap + TX02 mask (regression guard for both recent fixes) |
| R2 | Taarie (TBD) | Northbourne NPCs of Haafingar | Obi's Nocturnal Noir skirt — BodySlide-renamed shapes, the original index-fallback case |
| R3 | Knight-Paladin Gelebor (TBD) | (vanilla/Dawnguard) | Ancient Falmer cuirass — armor skin inheritance, Snow Elf race skin |
| R4 | Ri'saad (TBD) | (vanilla) | Khajiit head — face alpha-test suppression, vertex-alpha seam |
| R5 | Thalmor soldier (TBD) | (vanilla) | Elven Light set via Use-All LVLI outfit |
| R6 | One Base Game NPC in plain clothes (TBD) | Base Game | portable no-mods baseline |
| R7+ | One specimen per AUD issue | from scan | pin each fix |

Regression rule: after any change to `NpcMeshResolver`, `NifMeshBuilder`,
`VM_CharacterViewer` texture/shader application, or `basic.frag`, re-render the set in one
process and eyeball against the stored references (tolerant diff; same nondeterminism caveat
as Step 3).

## Scan results — vanilla pass (2026-07-17, direct launch, raw Steam load order)

7114 NPCs / 4962 with outfits / 1368 outfit ARMOs / 909 NIFs parsed, 0 failures, ~14 s.
Report: `AuditScanOutput/AuditScanReport.csv` (56 rows). Vanilla-confirmed specimens:

| Issue | Hits | Best base-game specimen | Notes |
|-------|------|------------------------|-------|
| AUD-1 | 10 | **Forsworn Briarheart** (`044313:Skyrim.esm`, ArmorBriarHeart — NAM0 `SkinBodyMaleBriarHeart` over 2 non-skin shapes); also every beast-race gauntlet ARMA (steel/hide/scaled/stalhrim) | Beast-gauntlet rows need a **Khajiit/Argonian wearer** (the ARMA is race-gated; the CSV's example NPC is outfit-based and may be human — pick e.g. a Khajiit caravan guard). |
| AUD-2 | 30 | Same wearers — vanilla beast hand-skin TXSTs (`SkinHandMaleArgonian`, `SkinHandFemaleKhajiit`, …) and male body skins author TX03 → **confirmed** slot-2 SSS clobber on skin shapes | Abundant; any Argonian/Khajiit in gauntlets. |
| AUD-3 | 4 | **Keeper** (`0074F9:Dawnguard.esm`, DLC1KeeperArmor — Dragonbone `MaleArmorBody` skin shape, `MaleBody_1_S.dds` in slot 7, Specular flag clear); beggar robes / Falmer boots body proxies | All hits are skin shapes under armor. |
| AUD-4 | 9 | **Nightingale Sentinel** (`0E0CDD:Skyrim.esm`, ArmorNightingaleHelmet — `_emit.dds` glow map on Hood/EyeCover); Gen. Falx Carius (Heartstone necklace); CC Gray Cowl, Elytra Nymph | Real, visible glow gear exists in base game — raises AUD-4's priority. |
| AUD-5 | 3 | CC Redguard Elite hood `EyeShineF` shapes (glow shader) | **Fix-design insight:** these contain "eyes" case-insensitively but are NOT eyeballs — a naive case-insensitive heuristic would misclassify them. The fix needs shader-type gating or segment matching, not blind case-folding. No genuinely miscased eyeballs in vanilla (all 12 eye headparts match). |
| AUD-6 | 0 | — | Forced-alpha fallback never fires on vanilla outfit/eye NIFs. Re-check in the MO2 pass. |
| AUD-7 | 0 | — | Expected: duplicate names come from BodySlide output — MO2 pass needed. |

## Scan results — MO2 modlist pass (2026-07-17)

17,567 NPCs / 11,242 with outfits / 3,730 outfit ARMOs / 2,545 NIFs parsed (39 unresolved,
0 failures). Report: `AuditScanOutput-MO2/AuditScanReport.csv` (249 rows, some issues
row-capped at 250 total). Deltas vs vanilla and consequences:

- **AUD-1 (31):** best modded specimen — **Gore** (follower): `GorePlateCuirass` NAM0 merged
  onto **8** non-skin shapes; also `GoreGauntlets`, Yngol cuirass, Fathis Indaryn's pants,
  and Sa'chil's Redguard boots (NAM0 with ZERO skin shapes — the whole boot gets repainted).
- **AUD-2 (119):** abundant; same beast-gauntlet class plus modded skins.
- **AUD-3 (17), AUD-4 (46):** plenty of specimens; Nightingale Sentinel remains the AUD-4
  poster child.
- **AUD-5 (30, of which 26 are FALSE-EYE hits):** priority ↑↑ — 26 in-loadout shapes named
  `Eyes`/`Eyes01`/`FlyEyes` on helmets and creature skins (mostly ShaderType 1 envmap) are
  **misclassified as eyes by the CURRENT heuristic today**: they take `eyeCubemapScale`
  instead of `envMapScale` and the eye AO opt-out. This is no longer hypothetical; the fix
  (shader-type gating, NOT case-folding — see the EyeShineF trap above) has real coverage.
- **AUD-6 (0):** zero hits across vanilla AND the full modlist (≈3,400 parsed NIFs total).
  Downgraded to "no real-world exposure — leave the deliberate fallback as-is."
- **AUD-7 (6, all latent):** duplicate names exist (GreatWarSkyrim helmets, arnima/Credo
  armors, Val Serano saddle) but none are targeted by AlternateTextures in this loadout.
  Stays latent/low priority.

## Fix verification — before/after renders (2026-07-17)

Fixes applied: **AUD-1** (skin-shape gate on the flat NAM0 merge in
`ApplyOneMeshOverride`), **AUD-2** (TX03→slot 3 in `PopulateTxstSlots`),
**AUD-3** (specular gated by SLSF1_Specular in `ApplyTexturesToGlMesh`),
**AUD-5** (eye-name heuristic disabled for attire via `allowEyeNameHeuristic`).
AUD-4 deferred (glow feature); AUD-6 closed; AUD-7 parked.

Each specimen rendered full-body on the pre-fix build and the post-fix build
(same process settings, burn-in 1). Composites (before | after | amplified heat
diff) in `Docs/Screenshots/OutfitAudit/` — **local artifacts, git-ignored**:
rendered NPC imagery stays out of the repo (some outfits are revealing; the
repo stays SFW). Regenerate any composite from the harness + diff procedure
below.

| Evidence | Issue | Changed px | What the diff shows |
|----------|-------|-----------|---------------------|
| `diff-aud1-briarheart.png` | AUD-1 | 15.6% | Forsworn Briarheart's fur skirt + briar heart stop being painted with body-skin texture — whole garment restored. |
| `diff-aud3-azadi.png` | AUD-3 | 1.3% | Change localized to Azadi's bare forearms + chest-V (the one skin shape whose `_s.dds` has SLSF1_Specular clear); waxy→matte. Shirt/face/legs untouched. |
| `diff-aud12-khayla.png` | AUD-2 | 0.24% | Change localized to the exposed Khajiit fingertips (beast hand skin shape) — subsurface map restored. Armor unchanged (AUD-1 not the visible actor for this gauntlet). |
| (Orc Dragonplate) | AUD-3 | 0.00% | Rejected specimen — clipped body fully covered, no visible skin. |

The heat-diff method is the reliable way to read the subtle ones (AUD-2/3): a
full-body PNG pair looks nearly identical, but the amplified diff pinpoints the
exact skin regions the fix touched, and confirms nothing else moved. The two
render processes agreed globally (no cross-process warm-shift noise this run),
so the localized heat is real signal.

### AUD-5 MO2 A/B (2026-07-17)

Two MO2 launches (before = gate temporarily neutralized, after = committed fix)
rendered Shadow Hunter (`121460:LegacyoftheDragonborn.esm`, Kynreeve hooded
helmet, shape "Eyes") head-framed with headgear forced on via harness variant
settings (`IncludeDefaultOutfit`/`IncludeHeadgear` are InternalMugshotSettings
properties, so variants can set them). The other two candidates (Clockwork
"Gilded", Glenmoril "Ozwald") are not renderable through the standard resolve
(custom races) and failed identically in both passes.

**Result: bit-identical renders (0.00% diff).** NIF forensics
(CharacterViewer.NifDump on `kalhoodedhelmet_0.nif` block 23/24) explain it and
prove the bug class at the same time: the "Eyes" ornament is BSLSP_ENVMAP with
**envMapScale 0.000 authored** but **eyeCubemapScale 1.000** (nifly default for
a non-eye block), cubemap `EyeCubeMap.dds` + vanilla eye env mask bound. So the
pre-fix misclassification genuinely flipped the reflection scale from the
authored 0.0 to 1.0 — but the visible contribution rounds below pixel
quantization here (few-pixel ornament, hood shadow, near-black env mask), so no
pixel changed. Verdict: AUD-5 stays fixed on correctness + census grounds
(34 affected shapes in the v2 scan); its visible impact in this loadout is nil,
and the A/B doubles as a no-regression proof (identical output includes the
NPC's real eyes).

The race-aware v2 MO2 scan (`AuditScanOutput-MO2-v2`) also deduplicated the v1
counts (AUD-2 119→38 unique — v1 re-inspected shared NIFs per ARMO) and parsed
2,934 NIFs (more sex-variant world models registered per-wearer).

## AUD-4 implementation — glow maps (2026-07-18)

Implemented in CV.R: `texture_glow` sampler (unit 12) + `has_glow_map`;
`basic.frag`'s emissive term becomes `emissiveColor * emissiveMultiple *
glowMap.rgb * albedo` when SLSF2_Glow_Map is set (sk_default.frag semantics);
VM loads NIF slot 2 as the glow map for NON-skin shapes gated on
Glow_Map + Own_Emit (slot 2 remains the skin/SSS sampler for shader types 4/5).

Verification (`diff-aud4-nightingale.png`, `diff-aud4-falxcarius.png` in
`Docs/Screenshots/OutfitAudit/`): **the pre-fix state was not merely "missing
glow" — it was a visible wash-out bug.** Glow-mapped gear authors a white
Own_Emit emissive (the map confines it per texel); without the map modulation
the flat white emissive self-lit the whole mesh. Nightingale Sentinel's cowl
rendered washed-out grey before (7.12% of frame changed) and now shades like
the rest of the armor with emission confined to the map; Falx Carius's
heartstone necklace localized at 0.93%. NIF forensics for the cowl
(`BSLSP_GLOWMAP`, Own_Emit, white emissive x 1.0, `_emit.dds` in slot 2)
confirmed the authoring pattern before implementation.

## Reference regression set — locked in (2026-07-18)

Canonical harness configs live in `Docs/RenderReference/` (vanilla-direct +
MO2 variants); baseline PNGs in `Docs/RenderReference/baseline/` (720x1080,
burn-in 1, full-body framing with outfit + headgear on). The baselines are
**local artifacts, git-ignored** — they are loadout/GPU-specific and contain
rendered NPC imagery (SFW policy), so each machine captures its own via the
two configs and keeps them out of the repo.

| File | NPC | Scope | Pins |
|------|-----|-------|------|
| r1-elisif-gala | Elisif `01326A:Skyrim.esm` / Pandorable's | MO2 | alt-tex name+index matching, TX02 env mask |
| r2-taarie | Taarie `0132AB:Skyrim.esm` / Northbourne Haafingar | MO2 | BodySlide-renamed shapes (Nocturnal Noir) |
| r3-gelebor | Gelebor `00A877:Dawnguard.esm` | direct | armor skin inheritance, Snow Elf skin |
| r4-risaad | Ri'saad `01B1DB:Skyrim.esm` | direct | Khajiit face alpha-test suppression |
| r6-hulda | Hulda `013BA3:Skyrim.esm` | direct | plain-clothes baseline |
| r7-shadowhunter | Shadow Hunter `121460:LotD.esm` | MO2 | AUD-5 eye-name gate (helmet ornament) |
| r8-gore | Gore `000D63:GORE.esp` | MO2 | AUD-1 NAM0 gate (8 non-skin shapes) |
| s1-briarheart | Forsworn Briarheart `044313:Skyrim.esm` | direct | AUD-1 |
| s2-khayla | Khayla `01B1D9:Skyrim.esm` | direct | AUD-2 beast-hand SSS |
| s3-azadi | Azadi `00081E:ccEDHSSE003.esl` | direct | AUD-3 specular flag |
| s4-nightingale | Nightingale Sentinel `0E0CDD:Skyrim.esm` | direct | AUD-4 glow map |

(R5, a Use-All-LVLI Thalmor soldier, was dropped: vanilla Thalmor soldiers are
leveled/template NPCs without a stable named wearer; the Use-All path is
covered by unit tests and the 74d024f log line.)

**Procedure** after any change to `NpcMeshResolver`, `NifMeshBuilder`,
`VM_CharacterViewer` texture/shader application, or the shaders: copy the
vanilla config to the exe as `RenderHarness.json`, run directly; copy the MO2
config, run through MO2; heat-diff each PNG against `baseline/` (amplify x4-8,
tolerate ±1 LSB scatter and the rare uniform +2.8-luma process state — re-run
once if the WHOLE image shifted). Localized heat = investigate. Update the
baseline only deliberately, alongside the change that legitimately moved it.

## Status log

- 2026-07-17 — Audit performed; document written. Findings AUD-1..8 open.
- 2026-07-17 — `AuditScanRunner` implemented (`AuditScan.json` trigger) + CV.R
  `BuiltMesh.AlphaFlagsRaw`. Vanilla scan pass complete (table above): AUD-1/2/3/4/5 have
  confirmed vanilla specimens; AUD-6/7 need the MO2 modlist pass. Before/after images and
  the reference-set lock-in are next.
- 2026-07-17 — MO2 modlist pass complete (section above). Fix order set by specimen
  evidence: AUD-1, AUD-2, AUD-3, AUD-5 (shader-type gate; 26 live false-eyes), then AUD-4
  (glow feature); AUD-6 closed (no exposure); AUD-7 parked (latent only).
- 2026-07-17 — Scan made race-aware (example wearer must be served by the ARMA's
  Race/AdditionalRaces, mirroring the resolver) so beast-mesh findings get a beast-race
  specimen (Khayla, not the race-mismatched Inimoro). AUD-1/2/3/5 implemented and verified
  via before/after heat-diff composites (section above). Fixes are UNCOMMITTED pending
  review. Remaining: commit (NPC2 + SynthEBD separately), optional AUD-5 MO2 render, AUD-4
  scope decision, reference-set lock-in (step 4).
