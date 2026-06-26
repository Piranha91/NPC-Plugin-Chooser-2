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

## Known gaps / future work

- **Full `Patcher.RunPatchingLogic` end-to-end run** (Create / Create-and-Patch / SkyPatcher
  producing an output plugin) is only covered at the guard/early-return level. A faithful run
  needs committed fixture mods (a plugin overriding a vanilla NPC + a matching `ModSetting`/
  selection), as `SynthEBD.Tests` does with its `DemoSettings` tree. `NpcChooserHarness` is the
  scaffolding for it.
- **`EasyNpcTranslator` import/export** parse cores are coupled to file-dialog/UI calls; only the
  `TryGetDefaultPlugin` preset branch is unit-reachable. Extracting a pure `ParseProfileLine`
  would make the round-trip testable.
- A handful of seams that require real `.bsa` archives or the GL renderer are exercised only
  indirectly; their empty-state/pure branches are covered.
