# Setup Wizard Plan (2026-10, target 2.2.6)

## Goal

Walk a new user through the settings needed for a first successful run, one setting per page,
with validation on each page that targets the setup mistakes users have actually reported.

## Evidence base

Read on 2026-10-30 from the Nexus page (mod 157055): all 38 pages of posts (~1,360 posts), the
changelog 2.0.1–2.2.5, the description, and all 65 bug titles. Reply text was read for the 18
oldest bug reports only (Nexus rate-limited the rest), so the bodies of the seven open
"Invalid environment" reports against 2.2.5 (#1017952, #1034171, #1087419, #1094655, #1136081,
#1138535, #1149790) were not read there.

**Addendum (same day):** the 2.2.5 bug-report triage session's pasted report text (identified
by title/reporter, no IDs) covers the environment reports. Most "Invalid environment" reports
are a Mutagen `ModGroupsMalformedException` from one bad plugin — a corrupt CC file on a clean
Steam install (kloxkinq, theskyrimdepot), LE plugins hand-ported to SE (Grosaprap), a broken
downgrade (Jaykoby) — or a patch renamed to its own master (french20: "Cannot add mod to itself
as a master"). `EnvironmentProbe.DescribeBuildFailure` therefore reads the plugin from Mutagen's
typed exception context first and gives the fix for each failure kind; a blank Data path that
fails adds the Stock Game hint; the missing-plugins.txt message says copying it into Data does
not help (Dinkle99). Considered and not done: flagging form-43 (LE) headers on every plugin
(too many false positives — many working SE plugins are still form 43), VR loose-Strings checks.

Most-reported setup problems (approximate distinct users):

| Area | ~Users | Typical cause |
|---|---|---|
| Game environment Invalid | 25 | Stock Game / GOG / VR / modlist installs with a blank Game Data Path; version dropdown mismatch |
| Mugshots not showing | 15 | Zips not extracted; extra nesting level; subfolder name ≠ mod folder name; "Vanilla" vs "Base Game" |
| Mods folder | 10 | Vortex users unsure which folder; Data picked instead; MO2 filter set only after the first scan |
| plugins.txt / launched outside MO2 | 14 | Only base-game plugins visible |
| One corrupt plugin breaks the environment | 6 | Raw stack trace; user cannot tell which plugin |
| Installed inside Data / Mods / via mod manager | 6 | Settings.json collisions (e.g. Pandora) |
| Output path | 6 | `{ }` in path; bare-name vs full-path confusion; Vortex staging |
| Creation Club / Skyrim.ccc | 5 | CC plugins present on disk but not active |
| Permissions / antivirus | 3 | Access denied on start or write |

## Entry points

- **No Settings.json** → welcome dialog: *Launch Setup Wizard* / *Enter settings manually* /
  *Import existing Settings.json*. Keyed to the no-file branch of `VM_Settings.LoadSettings`
  (in-memory `Settings.IsFreshInstall`), so files from before 2.2.6 never trigger it and no
  migration is needed.
- **Everyone** → *Setup Wizard…* button at the top of the Settings page.

## Pages

| # | Setting | Checks |
|---|---|---|
| 1 | Mod manager (wizard-only) | Tailors hints on pages 2, 5, 6, 11 |
| 2 | Install location (info) | Inside Data / Mods / Program Files / OneDrive; exe folder writable; launched through MO2 (usvfs loaded) — MO2 users only, which is why the mod-manager question comes first |
| 3 | Skyrim version | Game exe in the detected/selected folder matches the version |
| 4 | Game Data path | Stock Game suggestion; trial environment build (status, plugin count, plugins.txt, Skyrim.ccc, vanilla-only); name the failing plugin; CC plugins on disk but inactive |
| 5 | Mods folder | Not Data / game root; MO2 `mods` vs Vortex staging recognised; folders with plugins/meshes |
| 6 | MO2 active-mods filter | Profile picker; enabled/matched preview; before the first scan |
| 7 | Mugshot sources | Curated (with download links) / FaceFinder / Auto-generate / Live Tiles → priority |
| 8 | Mugshots folder | Unextracted archives; double nesting; empty subfolders; name match rate; "Vanilla" → "Base Game" |
| 9 | FaceFinder cache folder | Writable; connectivity test |
| 10 | Auto-generation folder & renderer | Writable; non-ASCII mod paths |
| 11 | Output directory | Invalid characters; resolved path; writable; not Data or an existing mod; Vortex redeploy hint |
| 12 | Output plugin name | Valid name; collision with a foreign plugin; stale output not last in load order |
| 13 | Patching mode | Create and Patch vs Create explained; keep appearance plugins disabled |
| 14 | Output backend | ESP / SkyPatcher / Recast (+ Recast mode) explained |
| 15 | Summary | All values; apply on Finish |

## Design

- `BackEnd/SetupValidation/` — pure validators returning `SetupFinding`s (Info / Warning /
  Error) plus `EnvironmentProbe`, a disposable trial environment build. Unit-tested on temp
  folders without a game install.
- `View Models/SetupWizard/` — `VM_SetupWizard` holds a draft of every value; one
  `WizardStep` subclass per page.
- `Views/SetupWizardWindow.xaml`, `Views/WelcomeWindow.xaml`.
- **Apply once at Finish**, in dependency order: game environment (one rebuild, guarded so the
  throttled subscription does not rebuild again) → modlist filter → mugshots → mods folder
  (one scan, only if it changed). Cancel applies nothing. Changing the mods folder of an
  existing setup is warned about, because it clears the mod list.

## Out of scope

- Missing .NET runtime (app cannot start) and Wine/Proton UI quirks — README/Nexus material.
- Test render on the auto-generation page (needs the applied environment).
