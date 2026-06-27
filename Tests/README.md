# NPC Plugin Chooser 2 — Test Suite

xUnit + FluentAssertions test battery for N.P.C.2, modelled on the sibling
`SynthEBD.Tests` project: a large body of pure unit tests plus a Skyrim-SE
integration layer that **skips gracefully** when no game is installed.

## Running

```sh
dotnet test "Tests/NPC Plugin Chooser 2.Tests.csproj" -c Debug
```

The test project lives inside the app's repo (`Tests/`), references the app
project, and is excluded from the app's own compilation via a `<Compile Remove="Tests\**" />`
group in the app csproj. The app exposes its `internal` members to the tests via
`[assembly: InternalsVisibleTo("NPC Plugin Chooser 2.Tests")]` (in `AssemblyInfo.cs`);
genuinely private seams are reached through the `TestSupport/Reflect` helper, so the
production code needed no behavioural changes to become testable.

## Layout

- **`Unit/`** — pure, deterministic tests (models, enums, serialization, converters,
  version/migration logic, `Auxilliary` string/path/record helpers, record-delta diffing,
  patcher constants/extensions, FaceGen analysis, ImagePacker, NPC display/consistency, ETA,
  init warnings). No game, no files, no UI thread.
- **`Harness/`** — deterministic tests that touch the temp filesystem or construct a service
  (update-handler file ops, `PluginArchiveIndex`, `Auxilliary` file helpers, OutputValidator
  parsers/index, SkyPatcher `.ini` emission, FaceFinder/Portrait statics, VM pure helpers).
- **`Integration/`** — tests that need a live Skyrim SE environment, the WPF STA thread, or the
  backend Autofac graph. Each game-dependent test calls `NpcChooserTestEnvironment.TryBuild`
  and, when it returns a skip reason, logs it and passes as a no-op (so CI without Skyrim stays
  green). VM/static-state tests join `NpcChooserIntegrationCollection` so they run sequentially.
- **`TestSupport/`** — shared helpers:
  - `MutagenFixtures` — in-memory Skyrim records (`NewMod`/`NewNpc`/`NewRace`); no mocking library.
  - `TempDir` — disposable scratch directory.
  - `StaticStateGuard` — snapshots/restores ReactiveUI schedulers, culture, and the static
    `NpcDiagnosticLogger`.
  - `Reflect` — invoke private members / allocate uninitialized instances for heavy-ctor types.
  - `TestModuleInitializer` — registers the `FormKey` `TypeConverter` once (as `App` does at
    startup) so FormKey-keyed dictionaries round-trip through JSON.

## Harness pieces (mirror of SynthEBD.Tests)

- `NpcChooserTestEnvironment` — stands up the real `EnvironmentStateProvider` against the
  installed game (`TryBuild`), or an Invalid (no-game) provider (`Invalid`) for guard tests.
- `WpfStaFixture` — owns one WPF `Application` on a dedicated STA thread; `RunOnStaAsync` marshals
  a test body onto it (needed by view-model and ReactiveCommand tests).
- `NpcChooserHarness` — the backend Autofac graph (the closure rooted at `Patcher`/`Validator`)
  around a supplied environment + settings.

## Golden-output patcher test (`Integration/GoldenOutput/`)

`PatcherGoldenOutputTests` runs the **real `Patcher.RunPatchingLogic` end-to-end** across all 12 setting
combinations ({CreateAndPatch, Create} x {Ignore, Include, IncludeAsNew} x {non-SkyPatcher, SkyPatcher})
and compares the output (plugin records + assets + SkyPatcher `.ini`) against a committed reference set.
Pieces:

- **`GoldenEnvironmentBuilder`** reproduces the exact patch-time environment: vanilla + DLC + Creation Club
  auto-detected from the game Data folder, plus the active extras (USSEP, AI Overhaul) loaded from their
  mod-manager folders and injected via the `EnvironmentStateProvider.UpdateEnvironmentForTest` seam; the
  prior `NPC.esp` output is trimmed.
- **`GoldenComboSettingsBuilder` / `GoldenPatchRunner`** build the per-combo `Settings` (selections, ModSettings,
  the synthetic "Base Game" entry) and drive the patcher exactly as `VM_Run` does.
- **Comparators** — appearance is compared by **resolved EditorID** (merged-in records get fresh FormKeys but
  stable EditorIDs); floats with a small tolerance (the reference round-trips through ESL compaction); assets
  by **content hash** (SkyPatcher FaceGen, whose path embeds an allocated FormID, is matched by content);
  SkyPatcher `.ini` directives semantically (output-plugin pointers compared by presence, not FormID).
- **Machine-specific config** lives in `Tests/TestData/EnvironmentMap.local.json` (gitignored; copy from the
  committed `EnvironmentMap.example.json`). The small reference plugins/tokens/`sel.txt`/`.ini` are committed
  under `Tests/TestData/GoldenReference`; the reference **assets** (meshes/textures) are read from the
  external root named in the local map and are never committed (licensing). The whole suite **skips gracefully**
  when the local map, the referenced paths, or a Skyrim SE install is absent.

All 12 reference combos (including the two SkyPatcher+Include cases, 08 and 11) are captured from the fixed
patcher, so every combo is compared in full. `GoldenCombos.IsStaleForChildClothesFix` is kept as a hook to
tolerate a single known deviation should a future fix invalidate a reference until it is regenerated.
`PatcherAppearanceLinksTests` (no game needed) locks the contract of the helper the fix turns on.

### Reference mod versions

The committed reference set + the source-oracle expectations were generated against these exact mod versions.
If you reproduce the reference outputs (or the asset comparison reports a hash mismatch), match these:

| Mod | Version | Notes |
| --- | --- | --- |
| Unofficial Skyrim Special Edition Patch (USSEP) | 4.3.8alpha | environment plugin |
| AI Overhaul SSE | 1.9.5 | USSEP-compatible version installed; environment plugin |
| Nordic Faces | 5.0 | installed just the FaceTints from the main file + the non-SMP FaceGen meshes optional file |
| The Ordinary Women SSE | 3.0 | appearance source |
| WICO - Windsong Immersive Character Overhaul | 0.9f | appearance source |
| RS Children Overhaul | 1.1.3 | appearance source (the source-oracle target) |

Most of these are old and stable. **USSEP is the most likely cause of a failure for anyone cloning the repo:**
its author updates it frequently and removes old versions, so the installed version commonly drifts from
4.3.8alpha. AI Overhaul also updates somewhat often, but its old versions stay available. A USSEP/AI Overhaul
update only breaks the comparison if it happens to touch this small handful of Riverwood NPCs (or
ChildClothes01 / the child races) - unlikely, but not impossible. When it does, the asset comparison's
hash-mismatch message already points at this: regenerate the reference set against the current mod versions.

### Source-oracle record comparison (`SourceOracleTests`)

The golden comparison checks the *output against a trusted reference*; `SourceOracleTests` additionally checks
the *dependency record contents against the source mod*, so it catches both patcher bugs and mistakes in the
golden output. For the RS Children records the patcher writes for Dorthe it serializes each record's full
element tree (Mutagen's YAML serialization, via `RecordYaml`) and compares element by element:

- **Create / Include (wholesale forward):** output record == RS Children's source record, exactly.
- **CreateAndPatch / Include (delta patch):** for each element, output == RS Children's value where RS Children
  differs from the Skyrim.esm base, else == the conflict-winning value (e.g. USSEP) - the delta derived here
  independently from the source/base/winning records.
- **Merged-in-as-new:** each duplicated RS Children record is a faithful element-by-element copy of its source.

FormKey leaves are normalized to EditorID (so merged-in records' remapped FormKeys compare equal); sequences are
compared order-independently; and benign round-trip representation differences (an unset nullable serialized as
`Null` vs omitted, trailing null padding on fixed-size strings) are treated as equal. This is what confirms the
delta patcher leaves untouched elements as the winning version and forwards only the source's real changes.

The test project references `Mutagen.Bethesda.Serialization.Yaml` + its source generator (the generator is
call-site driven, so the assembly that calls `Serialize` must reference it).

## Known gaps / future work
- **`EasyNpcTranslator` import/export** parse cores are coupled to file-dialog/UI calls; only the
  `TryGetDefaultPlugin` preset branch is unit-reachable. Extracting a pure `ParseProfileLine`
  would make the round-trip testable.
- A handful of seams that require real `.bsa` archives or the GL renderer are exercised only
  indirectly; their empty-state/pure branches are covered.
