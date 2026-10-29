# Recast output backend — implementation plan

Status (branch `recast-backend`): **Phases 1–4 done; Phase 6 item 0 done; Phase 7b done except the
steps marked open.** Written 2026-10-28; Phases 2 and 4 landed 2026-10-29, after the `skyrender-split`
session cleared the two-line `App.xaml.cs` registration. Phase 5 is next; its record checks share code
with `NpcMeshResolver`, so it still waits for that extraction to merge.

The shared TOML reader is `BackEnd/RecastPatchReader.cs` (Tomlyn 2.10.1). It follows Recast 1.6.3's
`TomlLoader.cpp`: a TOML error, a missing `[manifest]` or `api_version != 1` skips the whole file; a bad
entry skips only that entry; selectors are smart-detected and a full runtime FormID is cut to its local ID
(12 bits under an `FE` prefix, else 24). Conflicts rank by (priority, file name, line), highest wins, and
**only between entries with the same selector identity**: a FormID entry and an EditorID entry for the
same NPC are not ranked against each other at all.

Evidence this plan is based on:
- Recast 1.6.3 source: `github.com/heathbrownkeyworks/recast` @ `b6c5948`.
- SkyPatcher public source: `github.com/Zzyxz/SkyPatcher` @ `876662e`. This predates 7.1.3 and 8.0.x, so it is only a hint.
- In-game measurement of Recast 1.6.3 and SkyPatcher 8.0.0 in houseCARL:
  `S:\Dev\houseCARL\_claude\runs\2026-10-28-faceswap-frameworks\summary.md`, with judgments and pictures beside it.

## 1. Facts the design rests on (measured)

| Fact | Consequence for NPC2 |
|---|---|
| A Recast `face=` entry copies the face, head parts, hair colour, height, weight, body tint, **skin (WNAM)** and the **female flag**. The female flag is copied even without `sex=`. | A Recast block needs only `target` and `face`. The SkyPatcher-mode extras `skin=`, `height=`, `weight=` and `setFlags/removeFlags=female` all become unnecessary. The target's sex always follows the surrogate's, which is the same intent `ShouldChangeGender` already has. |
| Recast reaches a Traits-templated **target**: it fills in the template's data on the target and clears the target's Traits flag itself. | No `removeTemplateFlags=traits` equivalent is needed. |
| A templated **source** renders as its template ROOT's face in both frameworks. | This *confirms* the mechanism behind `Validator.CanSkyPatcherApplyAppearance`: a Traits-flagged surrogate shows the root's FaceGen, not the FaceGen NPC2 wrote. The houseCARL summary's line "the refusal is not needed with 8.0.0" is wrong for NPC2's case. The rule stays, and it now covers Recast too (§4.5). |
| Recast never changes race. It logs a cross-race warning, and humanoid swaps render fine. | Recast-only mode cannot deliver a race change. Hybrid delivers it with SkyPatcher `race=`. |
| Recast has no outfit and no keyword support. | Recast-only mode cannot deliver Include Outfit, ForwardToOutfit wigs or mod keywords. |
| A Recast face plus SkyPatcher `race=` / `outfitDefault=` / `keywordsToAdd=` on the same NPC all apply, and hold through save, reload and a fresh process. | Hybrid works. |
| A Recast face and a SkyPatcher `copyVisualStyle` on the **same** NPC is broken. SkyPatcher wins on a new game; after a save load Recast wins. | NPC2 must never emit both for one NPC, and must sweep its *other* backend's stale output (§4.4). The output validator should flag third-party `copyVisualStyle` lines on Recast targets. |
| Recast resolves conflicts by manifest `priority`; SkyPatcher by file name. | NPC2's TOML needs a priority above typical replacer packs (§6, D1). |
| Recast loads FaceGen from the **source** record's plugin and FormID path. | The surrogate FaceGen path used by SkyPatcher mode carries over unchanged. |
| The Nexus package ships `Settings.toml` with `force_female_animations = true` (the source default is `false`). In the lab it rewrote 286 vanilla NPCs. | NPC2 can't control this. Mention it in the Recast-mode help text. |

## 2. Settings model

```csharp
public enum OutputBackend { ESP, SkyPatcher, Recast }
public enum RecastMode { Hybrid, RecastOnly }        // only meaningful when OutputBackend == Recast
```

In `Models/Settings.cs`:
- Replace `UseSkyPatcherMode` with `OutputBackend OutputBackend = ESP`, `RecastMode RecastMode = Hybrid`, and
  `int RecastPatchPriority = <D1>`.
- **Migration.** Keep reading the old key as a JSON-only setter, so an old `Settings.json` maps
  `"UseSkyPatcherMode": true` to `OutputBackend.SkyPatcher`:

  ```csharp
  [JsonProperty("UseSkyPatcherMode")] private bool LegacyUseSkyPatcherMode { set { if (value) OutputBackend = OutputBackend.SkyPatcher; } }
  ```

  It is never serialized back. Check that `UpdateHandler` doesn't need its own step for this. The JSON-only setter is
  the lighter option, and unit-test it with an old settings fixture.
- **Intent-named properties** replace every raw bool read, so each call site states *why* it branches:
  - `UsesSurrogates`: `OutputBackend != ESP`. Surrogate creation, the FaceGen destination, cross-NPC swaps,
    `_useSelectedModRecord`, and the template screening in §4.5.
  - `UsesSkyPatcherAppearance`: `OutputBackend == SkyPatcher`. `copyVisualStyle`, `skin`, `height`, `weight`,
    gender, traits-clear.
  - `SkyPatcherDirectivesAvailable`: `SkyPatcher || (Recast && Hybrid)`. `race=`, outfit and keyword directives.
  - `UsesRecast`: `OutputBackend == Recast`.
  - `WigHandlingActiveForOutputMode` becomes `UsesSurrogates || CreateAndPatch`. A wig converted to head parts rides
    the surrogate in every backend.
  - `OutfitFieldIsInert` currently returns false whenever SkyPatcher is on. It becomes
    `!SkyPatcherDirectivesAvailable && (UsesRecast || <existing inventory-template test>)`. In RecastOnly, nothing can
    deliver an outfit, so the existing per-NPC fallback turns ForwardToOutfit into ConvertToHeadParts at no extra
    cost. Check that the run-log wording still reads right.

Raw-bool call sites to convert (non-test; from `grep UseSkyPatcherMode`):
- `Patcher.cs`: 13
- `OutputValidator.cs`: 11
- `VM_Settings.cs`: 7
- `PatchVerifyRunner.cs`: 6
- `OutfitDisplayResolver.cs`: 5
- `Settings.cs`: 3
- `Validator.cs`: 3
- `VM_ModSetting.cs`: 2
- 1 each: `ForwardedOutfitDistributor.cs`, `NpcToken.cs`, `VM_NpcSelectionBar.cs`, `VM_Run.cs`, `SettingsView.xaml`

None are in `CharacterViewerHost/`, so this doesn't collide with the viewer extraction.

`NpcToken`: add `string? OutputBackend` and `string? RecastMode`. Keep reading `bool? UseSkyPatcherMode` from old
tokens. The token-mode mismatch warning (`OutputValidator` around line 523) compares the backend, not the bool.

## 3. Phase plan

Each phase builds and passes the full test suite before the next one starts. Phases 1–2 must not change output:
golden outputs stay byte-identical.

### Phase 1 — enum and intent properties (no behaviour change)
1. Settings, migration and intent properties as in §2; convert every call site.
2. UI: replace the SkyPatcher checkbox (`SettingsView.xaml:453`) with an Output backend dropdown (ESP / SkyPatcher /
   Recast). Below it, shown only for Recast: the RecastMode dropdown and the priority box.
   - Move the confirm dialog and the ForwardToOutfit warning in `VM_Settings.cs:1607-1635` / `:1287-1310` onto the
     enum.
   - The ForwardToOutfit-naked-NPC warning applies to SkyPatcher and Hybrid. RecastOnly converts wigs instead, so it
     doesn't apply there.
   - `VM_Settings.cs:4285` (diagnostic dump) prints the backend and mode.
3. `VM_ModSetting.cs:893/900` wig/antler dropdown visibility: use `WigHandlingActiveForOutputMode` instead of
   repeating the formula.
4. Tests: settings migration (old JSON with true or false), the intent-property truth table, and existing golden
   combos 1-12 unchanged.

### Phase 2 — split surrogate handling out of `SkyPatcherInterface` (done)
**Done** as below, with two deviations: `SkyPatcherInterface.AddNpc(target)` opens a target's ini line
(the Patcher calls it beside `CreateSurrogate` when directives are available), and `WriteIni` skips a
line with no directive, which only Hybrid produces. `OutputValidator` and `WigForwarder` never used the
maps, so they did not change.

1. New `BackEnd/SurrogateNpcRegistry.cs` takes from `SkyPatcherInterface`:
   - `CreateSkyPatcherNpc` (renamed `CreateSurrogate`) and `StripNonAppearanceData`
   - the two FormKey maps, `TryGetSurrogateFormKey`, `TryGetOriginalFormKey`
   - their reset in `Reinitialize`
2. `SkyPatcherInterface` keeps INI directives and writing, and asks the registry for surrogate↔target lookups
   (`ApplyKeywords` maps a surrogate back to its target).
3. Re-point `AssetHandler.cs:1809`, `Patcher.cs`, `OutputValidator` and `WigForwarder` (if it uses the maps) at the
   registry. Register the registry in the Autofac setup next to `SkyPatcherInterface`.
4. Rename `FaceGenDestinationMode.SkyPatcher` to `Surrogate`, and update `KeepsInheritedFace` and its doc comment.
   `Patcher.cs:3614` selects it when `UsesSurrogates`.
5. Tests: move the surrogate cases out of `SkyPatcherInterfaceTests` into `SurrogateNpcRegistryTests`; golden
   outputs unchanged.

### Phase 3 — `RecastInterface` writer (done; not yet registered in Autofac)
New `BackEnd/RecastInterface.cs`, built like `SkyPatcherInterface`:
- `Reinitialize(outputRoot, clearAllGeneratedTomls)`, `AddFace(targetFk, surrogateFk, comment)`, `HasEntries`,
  `EnumerateDirectiveFormKeys()` (the surrogates, for the orphan sweep at `Patcher.cs:3386`), and
  `WriteToml(outputRoot, formKeyRemap)`. The remap applies to both selectors. A repeat `AddFace` for one target
  replaces the earlier entry. Priority comes from `Settings.RecastPatchPriority`.
- Output path: `<outputRoot>\SKSE\Plugins\Recast\Patches\NPC Plugin Chooser\<OutputPluginName>.toml`. One file
  per run; the selectors name their plugin, so auto-split surrogates need no extra file.
- Format, hand-written (no library is needed to write it): UTF-8 without BOM, `\n` line endings, blocks sorted by
  target FormKey.

  ```toml
  # Generated by NPC Plugin Chooser 2 — do not edit; regenerated every run.
  [manifest]
  name        = "NPC Plugin Chooser 2"
  priority    = 1000
  api_version = 1

  # Lydia
  [[npcs]]
  target = "0x000A2C8E~Skyrim.esm"
  face   = "0x00000801~NPC Output.esp"
  ```

- Selector: `$"0x{fk.ID:X8}~{fk.ModKey.FileName}"`. `FormKey.ID` is already the plugin-local ID that Recast passes
  to `TESDataHandler::LookupForm`, for ESL and full plugins alike. Write a test for an ESL-flagged output.
- Escape TOML basic strings (`\\`, `\"`, control characters) in the comment-free fields. Keep comments to one line;
  strip any line breaks from NPC names.
- Use FormID selectors only, so users don't need powerofthree's Tweaks.
- Tests (`Tests/Harness/RecastInterfaceTests.cs`):
  - selector formatting (full plugin, ESL, IDs with leading zeros)
  - sort order, remap, encoding and newline
  - empty run writes no file and deletes the old one
  - Reinitialize sweeps old TOMLs
  - escaping

### Phase 4 — patcher wiring (done)
**Done** as below, except item 3's debug assertion: the tail emits the face through exactly one branch
(`UsesSkyPatcherAppearance` → `ApplyCoreAppearance`, else `UsesRecast` → `AddFace`), and
`ApplySkyPatcherDirectives` gates gender and traits on `UsesSkyPatcherAppearance`, so the pairing can't
arise; `PatcherTemplateInheritanceTests.RecastHybrid_EmitsRaceButNoAppearanceDirective` pins it. Item 6
needed no new code: both writers already sweep every file they ever generated on the first iteration,
whatever the backend, and only the active one writes again. Under Hybrid the ini is written only when
some NPC needed a directive. The RecastOnly keyword warning is logged once per iteration, before the
configs are written.

1. **Surrogate creation:** `Patcher.cs:1199` (CreateAndPatch) and `:1648` (Create) branch on `UsesSurrogates`.
   They're otherwise unchanged; the merge-in and asset scheduling already work on `patchNpc`.
2. **Tail at `Patcher.cs:2083-2105`.** Split `ApplySkyPatcherDirectives` by intent:
   - `UsesSkyPatcherAppearance` (SkyPatcher only): gender, the traits-clear and `ApplyCoreAppearance`, exactly as
     today.
   - `SkyPatcherDirectivesAvailable` (SkyPatcher or Hybrid): `race=`, `outfitDefault=` / `outfitSleep=` (the
     Include Outfit and IncludeAsNew outfits) and keywords.
   - `UsesRecast`: `_recastInterface.AddFace(npcFormKey, patchNpc.FormKey, name)`.
3. **Never pair a Recast face with SkyPatcher appearance.** Hybrid lines carry no `copyVisualStyle`, `skin`,
   `height`, `weight`, gender or template directive. Assert this in `SkyPatcherInterface.WriteIni` with a debug
   check that a target with a Recast face has no appearance directive.
4. **Keywords** (`Patcher.cs:4788`): SkyPatcher directive when `SkyPatcherDirectivesAvailable`. In RecastOnly,
   skip them and add one run-summary warning listing the affected mods.
5. **Writing** (`Patcher.cs:2326-2338`): write the Recast TOML when `UsesRecast`; write the INI when
   `SkyPatcherDirectivesAvailable || HasEntries`. Leave the record-mode standalone wig outfit lines from
   `ForwardedOutfitDistributor` as they are.
6. **Stale output from the other backend.** On the first iteration, delete NPC2's own generated files for the
   backends *not* in use:
   - the Recast TOML when the backend is ESP or SkyPatcher;
   - the SkyPatcher appearance INI when the backend is Recast.

   Otherwise a switch from SkyPatcher to Recast leaves `copyVisualStyle` on the same NPCs: the measured "broken, winner
   flips" case. This matters most when the output folder is reused without a clean.
7. `NpcToken` stamp (`Patcher.cs:2482`): the backend and mode.

### Phase 5 — pre-run screening (`Validator.cs`, `VM_Run.cs`)
1. **Cross-NPC swaps** (`Validator.cs:257`): allowed when `UsesSurrogates`.
2. **Templated source**: rename `CanSkyPatcherApplyAppearance` to `CanRuntimeBackendApplyAppearance`, gated on
   `UsesSurrogates`. Rewrite the doc comment with the measured evidence: both frameworks render the template root.
   The GiveEachNpcOwnCopy remedy stays the same.
3. **RecastOnly limits:**
   - **Race change needed** (`ShouldChangeRace` true). A cross-race face does **not** cause the dark-face bug: the
     engine reads FaceGen from the surrogate's own path, and the measured Breton-on-Nord swap rendered correctly. The
     NPC simply keeps its own race. What can break is the body and the head mesh:
     - Recast copies the donor's WNAM. If that skin's ArmorAddons don't list the target's race, the body is
       invisible. A donor with no WNAM leaves the target's own race skin under the donor's head, giving a tone mismatch
       or seam.
     - Khajiit, Argonian and Orc heads have different topology from the other races.

     **Rule (D2): check the records, don't guess from race names.** The question is whether, after the swap, the
     target would render the **same body the donor renders**. If so, the donor's FaceGen meets the body exactly as
     its author built it. Compare the donor `D` (donor race, donor sex) with the target after the swap `T`
     (target race, donor sex, since Recast copies sex):
     1. **Effective skin.** For both sides: the surrogate's `WornArmor`, or when that's null, the side's own
        `Race.Skin`. Recast copies WNAM, so a non-null donor WNAM is used on both sides; a null one falls back to each
        race's own skin, which is exactly the tone-mismatch case.
     2. **Body coverage.** For each body slot the donor's skin covers for `D` (via `ArmorAddon.BodyTemplate`): at least
        one of the skin's ArmorAddons must match `T`'s race.
        - Matching uses `Auxilliary.ArmaNamesRace` with the `GetArmorRaceKey` (RNAM `ArmorRace`) indirection, the
          same matcher the wig and render paths use.
        - A slot that `D` covers and `T` doesn't means an invisible body part → **reject**.
     3. **Skin tone and body mesh.** For each slot, compare what each side resolves to: the matching ArmorAddon's
        `WorldModel` for the sex, and its `SkinTexture` (the TXST for that sex). The same addon, or identical model
        paths and textures → pass. Different → **reject** (D2a): a visible seam or tone change. Body tint is
        copied by Recast, so it is not compared.
     4. **Head and skeleton.** The head geometry is baked into the surrogate's FaceGen NIF, so the record-level head
        risk is the skeleton it sits on. Compare `Race.SkeletalModel` for the donor's sex on both races. Different
        paths (beast races) → **reject**.
     5. **Height (note only).** `Race.Height` for the sex multiplies the copied NPC height. If the two races differ,
        note the size change.

     All of this is read from records already in the link cache. Field names were checked against the Mutagen schema:
     - `Race.Skin`, `.ArmorRace`, `.SkeletalModel`, `.Height`
     - `ArmorAddon.Race`, `.AdditionalRaces`, `.BodyTemplate`, `.WorldModel`, `.SkinTexture`

     `CharacterViewerHost/NpcMeshResolver` already resolves the effective skin (WNAM, then `Race.Skin`) and picks
     ArmorAddons for rendering. Share that logic once the viewer extraction lands rather than writing a second copy.

     **Unmeasured: head parts outside their `ValidRaces`.** Recast copies the donor's head parts; Orc head parts, for
     example, list only Orc races. Nobody has measured whether the engine drops hair or eyes for an NPC whose race
     isn't in that list when a baked FaceGen is present. Phase 8 measures it (Orc→Nord, Khajiit→Nord, custom race
     with and without skin coverage, and Breton→Nord as the control). If parts drop, add a check 6: every copied
     head part's `ValidRaces` FormList contains `T`'s race → reject. The same Phase 8 cases confirm that checks 2–4
     predict what renders.

     The rejection text names which check failed and points at Hybrid, which delivers `race=` and makes every check
     moot.
   - **Include Outfit on, or IncludeAsNew outfit records:** one start-of-run warning listing the mods. Outfits are
     dropped.
   - **Mod keywords:** see Phase 4.4.
4. **Run popup at `VM_Run.cs:379`:** extend it to Hybrid. In RecastOnly, add a popup that lists what is dropped
   (race, outfits, keywords) and offers to switch to Hybrid.

### Phase 6 — output validation (`OutputValidator.cs`)
Recast awareness is needed in **every** backend, ESP included. A Recast patch from a third-party replacer overrides
NPC2's chosen appearance at runtime whatever NPC2 wrote, just as `CheckSkyPatcher` (line 2965) already catches
third-party SkyPatcher lines in record mode.

0. **(Done: `OutputValidator.CheckRecast` / `BuildRecastIndex`, `ValidationCheckKind.Recast`.)** **All
   backends:** run the `RecastIndex` (item 3) and flag any third-party Recast `face` on an NPC that has an NPC2
   selection. Files Recast itself would skip are listed in a run note instead. Under the Recast backend an
   EditorID-selector hit is a Warning, since Recast does not rank it against NPC2's FormID entry.
   - **ESP output:** an error. Recast replaces the face NPC2 wrote.
   - **SkyPatcher output:** an error. This is the measured winner-flip case.
   - **Recast output:** a conflict only when the third-party priority is `>=` NPC2's.

   Report the patch file, its manifest priority, and the fix: disable that TOML, or remove the replacer once its
   donor has been imported (Phase 7b).
1. **Readiness:** expect `Data\SKSE\Plugins\Recast\Patches\NPC Plugin Chooser\<out>.toml` when `UsesRecast`. In
   Hybrid, also expect the SkyPatcher INI if any NPC needed race, outfit or keywords.
2. **Read NPC2's own TOML:** add `ParseNpc2RecastToml`, which maps each target to its surrogate. Reuse the
   surrogate checks from `ValidateNpcSkyPatcher` (lines 1311-1461): the line exists, the surrogate resolves, and its
   appearance matches the chosen donor. Factor the shared part into `ValidateNpcSurrogate(target, surrogate, …)`.
3. **Index third-party Recast patches:** add a `RecastIndex` over every `.toml` under
   `Data\SKSE\Plugins\Recast\Patches\`, including subfolders. Flag:
   - another patch with `priority >=` NPC2's on the same target;
   - a third-party SkyPatcher `copyVisualStyle` on a Recast target (the flip case); use the existing
     `SkyPatcherIndex`.
   - In SkyPatcher mode, also flag third-party Recast patches on SkyPatcher targets.
4. **Parsing third-party TOML** needs a real parser, because third-party files use inline tables and the legacy
   `[replacements]` and `[[recasts]]` forms. Proposed library: **Tomlyn 2.10.1** (BSD-2; targets net10). Its
   tolerant parser is `SyntaxParser.Parse(text, sourceName)` → `DocumentSyntax` with `Diagnostics`/`HasErrors`,
   per https://xoofx.github.io/Tomlyn/docs/low-level/. Before writing the parser, read the Serialization/DOM docs
   for the `TomlTable` model API, which I haven't checked yet. → D3
5. Tests: parser cases in `OutputValidatorParsersTests` (all three Recast syntaxes, FormID and EditorID selectors,
   malformed files), plus index and conflict cases.

### Phase 7b — import Recast replacers as appearance sources (required before the version bump)
This is what the original user request asked for: a Recast replacer pack's donor NPCs should be offered as
appearance choices **for the NPCs the pack targets**, not listed as new NPCs. SkyPatcher replacers already get this
treatment. That handling is spread over these places:

| SkyPatcher piece | Where | Recast counterpart |
|---|---|---|
| `GetSkyPatcherImportsAsync`: (target, source) pairs from `filterByNpcs=…:copyVisualStyle=…` lines under `SKSE\Plugins\SkyPatcher\npc\**.ini` | `VM_ModSetting.cs:2960` | Pairs from `SKSE\Plugins\Recast\Patches\**.toml` in the mod's own folders: `[[npcs]] target/face`, plus the legacy `[replacements]` map and `[[recasts]] target/source` inline tables. |
| `GetSkyPatcherTargetModKeysAsync` → `SkyPatcherTargetModKeys` | `VM_ModSetting.cs:3037`, `:267` | Target ModKeys from the same TOMLs. |
| Turning guests into shares: `AddGuestAppearanceToSettings`, `PruneStaleGuestAppearances` | `VM_Mods.cs:1699-1731`, `:2648-2666`, `ResolveAndApplySkyPatcherGuests` `:2858` | Unchanged; fed the merged pair list. |
| Donor hiding: `CachedSkyPatcherTemplates` / `ShowSkyPatcherTemplates` | `Settings.cs:560,581`; `VM_NpcSelectionBar.cs:997,3479,4033,4291,4854` | The same set holds Recast donors. |
| Template cache rebuild | `UpdateHandler.cs:1378-1419` | Scans both formats. |
| Migration-time target population | `UpdateHandler.cs:850-878` | Scans both formats. |
| Lexical target count for the merge-in classifier | `MergeInClassifier.CountSkyPatcherVisualTargets` `:122` | Also counts Recast targets, or a Recast-only replacer pack is classified as a base mod. |

**Done:** `VM_ModSetting.GetSkyPatcherImportsAsync` and `GetSkyPatcherTargetModKeysAsync` now also read the
mod's Recast TOMLs (through `RecastPatchReader`, all three syntaxes), and
`MergeInClassifier.CountSkyPatcherVisualTargets` counts Recast face targets. Everything downstream (guest
shares, donor hiding, the 7c missing-master exclusion, `UpdateHandler`'s scans) is fed by those two methods,
so it picks up Recast with no further change. This replaced item 1's move into a new
`RuntimeReplacerScanner`: the extension was three small edits, the move would have been churn.

**Open:**
- **Re-analysis migration at release.** Startup reads a mod's replacer files only on an analysis cache
  miss, so a Recast pack the user already has installed imports nothing until that mod is refreshed. The
  `UpdateHandler` step for the release version must null `LastKnownState` (forcing re-analysis) and
  re-run the donor-cache scan (`UpdateTo2_1_1_Final`'s body) so `CachedSkyPatcherTemplates` gains the
  Recast donors.
- Item 2's renames (`CachedReplacerDonors`, the UI label) are cosmetic and not done.
- Method names still say SkyPatcher; rename with 7c (`ReplacerTargetModKeys`) if wanted.

Original approach:
1. Add `BackEnd/RuntimeReplacerScanner.cs` with one entry point that returns `(Target, Source)` pairs and target
   ModKeys from both formats.
   - Move the SkyPatcher parsing out of `VM_ModSetting` into it unchanged.
   - Add the Recast parser on Tomlyn (D3), reusing the Phase 6 parser.
   - Selectors: `0xID~Plugin` resolves directly; an EditorID resolves through the same env/mod EditorID maps
     `TryParseSkyPatcherNpc` uses. Keep the env-then-mod fallback for target ModKeys.
   - The `VM_ModSetting` methods become thin wrappers, so their call sites barely move.
2. Generalize the names, keeping the persisted JSON keys:
   - `CachedSkyPatcherTemplates` → `CachedReplacerDonors` with `[JsonProperty("CachedSkyPatcherTemplates")]`.
   - `ShowSkyPatcherTemplates` gets the same treatment.
   - UI label: "Show replacer donor NPCs (SkyPatcher/Recast)".
3. Self-targets and unresolvable selectors are skipped exactly as now (`target.Equals(source)` guard).
4. Tests:
   - parser cases (all three syntaxes, FormID and EditorID, ESL, malformed);
   - a fixture mod folder with a Recast TOML, giving one guest share and a hidden donor;
   - classifier counts;
   - mixed SkyPatcher and Recast packs in one mod.
5. **This is mandatory for release.** Once a user imports a replacer's donors through NPC2, the replacer's own TOML
   must be disabled. Otherwise it fights NPC2's output: Phase 6 item 0 reports it.

### Phase 7c — missing-master detection
**Behaviour done via 7b:** `SkyPatcherTargetModKeys` now holds Recast targets too, so the exclusion below
already covers Recast packs. Left: the rename, the comments, and the Recast-only cleanup test.
`VM_Mods.FindAndAddMissingMasters` (around line 3361) adds `SkyPatcherTargetModKeys` to the NPC-source plugins it
excludes. Without the same for Recast, a Recast replacer pack whose donors live in a plugin that masters the target
mod gets the target's folder re-attached as a "missing master" resource on every cleanup pass. That is the
`t_Amalee_Replacer.esp` → `3DNPC.esp` case, through Recast instead of SkyPatcher.
- Feed it the merged target ModKeys from `RuntimeReplacerScanner` (rename the property `ReplacerTargetModKeys`;
  it isn't persisted, so no migration).
- Update the comments at `VM_Mods.cs:3361-3366` and `VM_ModSetting.cs:254-266`.
- `MasterAnalyzer` itself reads plugin master lists only and needs no change.
- NPC2's own Recast output adds no masters (the TOML names plugins by file name, as the INI does), so output master
  handling is unchanged. Add a golden check that a Recast-only run's plugin has the same master list as the
  SkyPatcher run.
- Tests: extend the existing SkyPatcher missing-master cleanup tests with a Recast-only fixture.

### Phase 7 — remaining tooling, golden tests, docs
1. `PatchVerifyRunner`: `ModeOverride` accepts `"Recast"` and `"RecastOnly"`; save and restore the enum.
2. Golden outputs: add Recast × {Hybrid, RecastOnly} × {CreateAndPatch, Create} × {Ignore, Include, IncludeAsNew}.
   Add a `RecastTomlComparer` modelled on `SkyPatcherIniComparer`: keyed by target, with output-plugin FormIDs
   compared by presence. Extend `AssetComparer` to accept the backend. TemplateMatrix: run the
   templated-source and surrogate-appearance suites for Recast too.
3. `docs/KnownLimitations.md`: what RecastOnly drops; the global `force_female_animations` setting; don't mix with
   third-party SkyPatcher face replacers on the same NPCs.

### Phase 8 — in-game check
Re-run the houseCARL faceswap harness (`_claude/harness/faceswap/`) on NPC2's own output: Hybrid and RecastOnly, one
templated target, one cross-sex swap, one race change, one Include Outfit, one ForwardToOutfit wig, one switch
from SkyPatcher to Recast with the output folder reused (checks the stale-output sweep), and one imported Recast
replacer donor applied to its target through each backend with the replacer's own TOML disabled.

## 4. Decisions

- **D1 (agreed):** the Recast priority defaults to **1000**, editable by the user.
- **D2 (agreed in principle):** RecastOnly race mismatches are judged by checking the records (Phase 5.3), not by a
  list of races.
- **D2a (agreed):** a skin-tone or body-mesh difference (check 3) **rejects** the NPC.
- **D3 (agreed):** Tomlyn 2.10.1, shared by the output validator (Phase 6) and the replacer import (Phase 7b).
- **D4 (agreed):** importing Recast replacers is Phase 7b, and it is **required before the version bump and
  release**. It is the original user request.

## 5. Follow-ups outside this plan (existing SkyPatcher mode, measured on 8.0.0)
- `removeTemplateFlags=traits` changed nothing on a Traits-templated target (row S2). NPC2 emits it in SkyPatcher
  mode, so check whether it has any effect.
- `setFlags=female` (what `ToggleGender` emits) was not part of the matrix. Row S5 shows a female head on a male body
  without it; measure it before relying on it.
- The comment at `Patcher.cs:3271` cites `npc_patcher.cpp`, which upstream deleted. It should point at `npc.cpp` and
  name the SkyPatcher version.

## 6. Size and clash notes
- The mugshot renderer imitates SkyPatcher and SPID runtime layers for outfits (`NpcMeshResolver`,
  `InternalMugshotGenerator`). It has no Recast layer. Hybrid outfits still travel by SkyPatcher, so they're
  covered. Whether previews of an NPC targeted by a third-party Recast patch need a face layer is a question for
  after the viewer extraction; leave those files alone until then.
- About 25 production files and 12 test files, counting Phases 7b and 7c. Phase 1 has the most churn; phases 3 and 6 add the most new code.
- No overlap with `CharacterViewerHost/`. `AssetHandler.cs` changes in Phase 2 only (one lookup); rebase on the
  extraction first in case it moved.
