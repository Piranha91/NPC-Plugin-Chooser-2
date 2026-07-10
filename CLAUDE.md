# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

NPC Plugin Chooser 2 (N.P.C.2) is a Windows desktop utility for Skyrim mod
users: it lets you pick which appearance mod supplies each NPC's face, then
generates an output plugin + assets (or a SkyPatcher .ini) that applies those
choices. It is the successor to the original NPC Plugin Chooser and overlaps in
purpose with EasyNPC (it can import/export EasyNPC profiles). See README.md for
the full end-user feature walkthrough and UI semantics.

## Build & run

- **IDE/SDK:** .NET 8 WPF app (`net8.0-windows`, `WinExe`). Solution:
  `NPC Plugin Chooser 2.sln`; single project `NPC Plugin Chooser 2.csproj`.
- **Build:** `dotnet build "NPC Plugin Chooser 2.csproj" -c Debug`
- **Run:** launch the built exe in `bin/Debug/net8.0-windows/`, or `dotnet run`.
  In production it is meant to be launched *through a mod manager* (MO2/Vortex).
- **Close the app before rebuilding.** A running instance locks output DLLs
  (notably `CharacterViewer.Rendering.dll`); MSB3027/MSB3021 copy-lock errors
  mean the app is still open, not a compile failure.
- **No automated tests exist.** There is no test project; verification is
  manual — launch the app against a real Skyrim install and exercise the
  affected flow. Logs (below) are the primary diagnostic tool.

### External/sibling dependencies
- **`CharacterViewer.Rendering`** — the offscreen OpenGL 3D renderer used for
  in-app mugshot generation. It lives in the SynthEBD repo and is published to
  nuget.org as **`SynthEBD.CharacterViewer.Rendering`**. The csproj reference is
  *conditional*: if the SynthEBD repo is checked out as a sibling
  (`../../SynthEBD/CharacterViewer.Rendering`) the build uses its live source (so
  it can be co-developed in place — it is in-scope for edits; change it directly);
  otherwise it restores the published NuGet package, so a fresh clone builds with
  no extra setup. When bumping the renderer, keep the csproj `PackageReference`
  version, the CV.R `<Version>`, and `CharacterViewerRendering.Version` in sync,
  and publish a new package (SynthEBD `publish-cvr.yml` workflow). Its GLSL
  shaders ship embedded in the assembly (and copied beside it for local builds)
  and must be **ASCII-only** (non-ASCII chars in comments break the compiler with
  a misleading "unexpected $end" error).
- **NPC Portrait Creator** native binaries (`NPCPortraitCreator.exe`, `glfw3`,
  `libbsarch`, shaders, `lighting.json`) are copied from
  `../../NPC Portrait Creator/out/build/x64-Release` by the csproj; the external
  portrait renderer won't work without them.
- **Mutagen.Bethesda** (Skyrim) is the core library for reading/writing
  Bethesda plugins and resolving the load order. Versions are pinned to specific
  alphas — match them when adding Mutagen calls, and verify API signatures
  against the installed package rather than guessing.

## Architecture

MVVM with **ReactiveUI** (+ `ReactiveUI.Fody` `[Reactive]` properties),
**Autofac** DI, and **Splat** for view location. `App.xaml.cs` is the
composition root: it loads `Settings`, runs `UpdateHandler` migrations, registers
everything, then resolves `VM_Settings` and runs the startup pipeline. Backend
services and the primary VMs are registered `SingleInstance()`; per-item VMs
(e.g. `VM_ModSetting`, mugshot tile VMs) are transient and created via injected
factory delegates. Themes (`Themes/*.xaml`) are loaded from disk at runtime by
`ThemeManager` (deliberately excluded from BAML compilation in the csproj).

Layers: `Views/` (XAML) ↔ `View Models/` (`VM_*`) ↔ `BackEnd/` services ↔
`Models/` (plain serializable state).

### Central state model
`Models/Settings.cs` is the persisted root (serialized to `Settings.json` next to
the exe). Key pieces:
- **`ModSettings: List<ModSetting>`** — each `ModSetting` represents one selectable
  "mod": a `DisplayName`, the plugins it owns (`CorrespondingModKeys`), where its
  files live (`CorrespondingFolderPaths`), its mugshot folders, and the NPCs it
  provides (`NpcFormKeys*`). This is the spine of the whole app.
- **`SelectedAppearanceMods`** — per-NPC FormKey → the chosen mod + source NPC.
- Two **reserved synthetic auto-generated entries** exist by name: **"Base Game"**
  (vanilla masters: Skyrim/Update/Dawnguard/HearthFires/Dragonborn) and
  **"Creation Club"** (cc* plugins). They are (re)created in
  `VM_Mods.AddBaseAndCreationClubMods`. Several subsystems look these up *by their
  display name* — notably the mugshot BSA adapter (which registers vanilla BSAs
  off the "Base Game" entry) and the NPC menu. If the "Base Game" entry is
  missing or non-auto-generated, vanilla assets fail to resolve and vanilla NPCs
  drop out of the menu, so its existence is guarded/self-healed during population.

### Mod-list population & analysis (`VM_Mods`)
`PopulateModSettingsAsync` is the load pipeline: FaceGen path caching → load mods
from `Settings.ModSettings` → scan mugshot-only folders → scan mod folders →
consolidate → `AddBaseAndCreationClubMods` → `AnalyzeModSettingsAsync`. Analysis
runs `VM_ModSetting.RefreshNpcLists` per mod (loading the mod's plugins to map
which NPCs it provides; gated on `HasModPathsAssigned || IsAutoGenerated`). An
analysis cache (`LastKnownState` snapshot, `Models/StateSnapshot.cs`) skips
re-analysis on a cache hit — null the snapshot to force re-analysis.
Note the two-list pattern: **`_allModSettingsInternal`** is the full in-memory VM
list; **`_settings.ModSettings`** is the persisted subset. `SaveModSettingsToModel`
syncs in-memory → model (dropping entries with no keys/folders), and is guarded so
an Invalid environment can't overwrite good persisted settings.

### Environment
`BackEnd/EnvironmentStateProvider.cs` wraps Mutagen's `GameEnvironment`
(load order, link cache, data folder). `SetEnvironmentTarget` +
`UpdateEnvironment` (re)resolve it from `SkyrimRelease` + game path;
`BaseGamePlugins`/`CreationClubPlugins` derive from the version via Mutagen
`Implicits`. Changing the release/path re-resolves the environment but must **not**
delete the user's mod settings (only a mods-folder change does that).

### Patching (`BackEnd/Patcher.cs`)
`RunPatchingLogic` is the entry point. Three behaviors driven by `PatchingMode` /
`UseSkyPatcherMode`: *Create* (splice selected appearances into a standalone
plugin), *Create and Patch* (delta into the conflict-winning load order, like
EasyNPC), and *SkyPatcher* (emit a SkyPatcher .ini instead of editing NPC
records). Supporting services: `RecordHandler`/`RecordDeltaPatcher` (record copy
and non-NPC override delta patching), `AssetHandler` (textures/meshes),
`PluginProvider` (ref-counted cache of loaded plugin getters; resolves a ModKey
to a path via the mod's folders, then falls back to the game Data folder),
`BsaHandler`/`PluginArchiveIndex` (BSA reading), `Validator` (pre-patch screening
of masters/races), `SkyPatcherInterface`, `EasyNpcTranslator` (profile import/export).

### Mugshot generation (`BackEnd/CharacterViewerHost/`)
`InternalMugshotGenerator` / `BatchMugshotGenerator` drive the `IOffscreenRenderer`
from `CharacterViewer.Rendering`. NPC2's services are bound behind the renderer's
interfaces by the **`Adapters/`** (e.g. `NpcChooserBsaProviderAdapter` →
`IBsaArchiveProvider`, `NpcChooserNpcMeshDataSourceAdapter` → `INpcMeshDataSource`,
`NpcChooserDataFolderAdapter`, logger/settings adapters) so the renderer never
sees Mutagen or NPC2 types directly. The renderer is a singleton (its GLFW window
+ FBO are amortized; the factory must run on the WPF UI thread).
`PortraitCreator` is a separate path that shells out to the external
`NPCPortraitCreator.exe`.

## Diagnostics (use these first)
The app emits several opt-in logs next to the exe — prefer reading them over
speculating from code:
- **`StartupLog.txt`** (`StartupLogger`) — phased startup trace incl. environment
  resolution and the full mod-population pipeline. Enable via the `LogStartup`
  setting or a file trigger.
- **`BsaContentsDiag.log`** (`BsaContentsDiag`) — BSA registration + per-asset
  hit/miss for mugshot resolution. Opt-in: drop a `LogBsaDiag.txt` file next to
  the exe.
- **`AssetProvenance.csv`** (`AssetProvenanceDiag`) — per patch run, why each asset
  file was copied into the output. One CSV row per atomic reference (columns: `DestFile,
  Reason, Referencer, NPC, TargetFormKey, Mod, DonorFormKey, DonorEditorID, SourceKind,
  SourcePath`) — sort/pivot in a spreadsheet to view by-file or by-NPC. `Reason` is
  FaceGen / PluginRef / NifTexture / SmpXml / AssetLink; `Referencer` names the specific
  referencing record for PluginRef (e.g. `HeadPart 'Hair01' [ID]`) or the source NIF/XML
  for NifTexture/SmpXml. Unlike the other opt-in logs this is **user-facing**: the "Log
  Asset Provenance" checkbox in Settings > Logging (`Settings.LogAssetProvenance`),
  applied at runtime so it takes effect on the next Run. A `LogAssetProvenance.txt` file
  next to the exe still force-enables it as a dev fallback.
- **`RenderLogs/`** — per-NPC mugshot render traces (asset resolution paths).
- **`Rejected NPCs/`** — logs why each discarded NPC was excluded from the menu.

## Release Workflow

- **The version-bump commit must always be the LAST commit before a release
  upload.** Do not author further commits on top of a version bump intended for
  release — that release would silently include the later changes under the
  bumped version. The version is defined centrally in `App.ProgramVersion`
  (App.xaml.cs) and mirrored into `Settings.ProgramVersion`.
- If commits are needed after a version bump, treat the bump as stale and make a
  fresh version-bump commit so it is again the final pre-release commit.
- When committing on top of an existing version-bump commit, flag it to the user
  so they can decide whether to re-bump before uploading.
