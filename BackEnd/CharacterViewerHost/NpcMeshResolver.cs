using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyRender;
using SkyRender.Character;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Resolves all mesh + texture + record-derived state needed to render an NPC:
/// body / hands / feet / head (FaceGen) NIF paths, skeleton, FaceTint DDS,
/// QNAM TextureLighting, ARMA SkinTexture (TXST), HCLR hair color, NPC weight,
/// race-uniform height. Paths are Data-relative by default.
///
/// <para>When a <see cref="NpcResolutionContext"/> is supplied (i.e. the user
/// has selected a specific appearance mod for this NPC), record reads are
/// scoped to <see cref="NpcResolutionContext.PreferredModKeys"/> first via
/// <see cref="RecordHandler.TryGetRecordFromMods"/> with
/// <see cref="RecordHandler.RecordLookupFallBack.Winner"/> fallback — so the
/// preview reflects the mod's overrides for things like face morph, hair
/// color, and worn armor while still inheriting the conflict-winning record
/// for anything the mod doesn't touch (typically Race / vanilla Armors).
/// Asset paths are then rebased to absolute when present in any
/// <see cref="NpcResolutionContext.PreferredFolderPaths"/> entry, so the
/// renderer's <c>GameAssetResolver</c> uses the mod's loose-file overrides
/// instead of the vanilla data folder. Files not in the mod's folders fall
/// through as game-relative paths and the renderer resolves them against
/// the vanilla Data folder + BSAs as before.</para>
/// </summary>
public class NpcMeshResolver
{
    private readonly IRenderLogger _logger;
    private readonly RenderLogGate _logGate;
    private readonly Settings _settings;
    private readonly NpcConsistencyProvider _consistency;
    private readonly RecordHandler _recordHandler;
    private readonly EnvironmentStateProvider _env;
    private readonly BsaHandler _bsaHandler;
    private readonly OutfitDisplayResolver _outfitDisplayResolver;

    public NpcMeshResolver(
        IRenderLogger logger,
        RenderLogGate logGate,
        Settings settings,
        NpcConsistencyProvider consistency,
        RecordHandler recordHandler,
        EnvironmentStateProvider env,
        BsaHandler bsaHandler,
        OutfitDisplayResolver outfitDisplayResolver)
    {
        _logger = logger;
        _logGate = logGate;
        _settings = settings;
        _consistency = consistency;
        _recordHandler = recordHandler;
        _env = env;
        _bsaHandler = bsaHandler;
        _outfitDisplayResolver = outfitDisplayResolver;
    }

    private void LogVerbose(string message)
    {
        if (_logGate != null && _logGate.Verbose) _logger?.LogMessage(message);
    }

    /// <summary>
    /// Returns <see cref="ModSetting.CorrespondingFolderPaths"/> for the
    /// given mod, or null if no mod was provided. Hosts pass this to
    /// <c>OffscreenRenderRequest.AdditionalDataFolders</c> (offscreen path)
    /// or <c>VM_CharacterViewer.AdditionalDataFolders</c> (live preview) so
    /// the renderer's <c>GameAssetResolver</c> consults the mod's loose
    /// overrides before vanilla — including textures referenced lazily from
    /// inside NIFs that this resolver never sees directly.
    /// </summary>
    public IReadOnlyList<string>? GetModFolders(ModSetting? modSetting)
        => modSetting?.CorrespondingFolderPaths;

    /// <summary>
    /// Builds the full strict two-phase asset-resolution chain for the
    /// renderer's <c>AdditionalScopes</c> property. Returns the user-spec
    /// 8-step chain encoded as <see cref="RenderScope"/> entries:
    /// <list type="bullet">
    /// <item><b>scopes[0]</b>: vanilla (data folder + Base Game / Creation
    /// Club plugin filenames). Lowest priority — checked LAST during the
    /// last-to-first iteration.</item>
    /// <item><b>scopes[1..N]</b>: each entry of
    /// <see cref="ModSetting.CorrespondingFolderPaths"/> paired with
    /// <see cref="ModSetting.CorrespondingModKeys"/> filenames. Last
    /// folder wins.</item>
    /// </list>
    /// When <paramref name="modSetting"/> is null, returns just the
    /// vanilla scope so the chain still works for tiles that have no
    /// associated mod (renders behave like LinkCache-only resolution).
    /// </summary>
    /// <param name="npcFormKey">
    /// Enables the origin scope below, because a mod may ship only part of an NPC's FaceGen and
    /// leave the rest to the mod that originally added them. Every render path passes it — mugshot
    /// tiles, both live-preview entry points, and the mesh survey — since a render that omits it
    /// silently loses any asset owned by the originating mod. Still optional (and still keyed off
    /// the appearance SOURCE NPC, not the patch target) only so a caller with no NPC in hand can
    /// build the strict two-tier chain.
    /// </param>
    public IReadOnlyList<RenderScope> BuildResolutionScopes(ModSetting? modSetting, FormKey? npcFormKey = null)
    {
        var scopes = new List<RenderScope>();

        // scopes[0] — vanilla. Base Game + Creation Club plugin filenames
        // bound to the vanilla data folder. Checked last in the last-to-first
        // iteration, providing the spec's "if still no, look in the data
        // folder" fallback for both loose (phase 1) and BSA (phase 2).
        var vanillaModKeyNames = new List<string>();
        foreach (var mk in _env.BaseGamePlugins) vanillaModKeyNames.Add(mk.FileName.String);
        foreach (var mk in _env.CreationClubPlugins) vanillaModKeyNames.Add(mk.FileName.String);
        scopes.Add(new RenderScope(_env.DataFolderPath.ToString(), vanillaModKeyNames));

        // scopes[1] — the mod that ORIGINALLY added this NPC, when it is not the selected one.
        // Placed above vanilla but below the selection, so it fills gaps without ever outranking
        // the user's choice. Without it the renderer draws a headless body for every NPC whose
        // mod ships a face tint (or a record) but leaves the mesh to the original mod — the same
        // class the FaceGen ladder sources from the origin. Note the archives may belong to a
        // SIBLING plugin of the one the FaceGen path names, which is why this pairs the origin's
        // whole CorrespondingModKeys list with its folders (Casival is filed under "3DNPC.esp"
        // but stored in 3DNPC0.bsa, owned by 3DNPC0.esp).
        if (npcFormKey.HasValue && modSetting != null)
        {
            var origin = FindOriginModSetting(npcFormKey.Value.ModKey, modSetting);
            if (origin != null && origin.CorrespondingFolderPaths.Count > 0)
            {
                var originKeyNames = origin.CorrespondingModKeys.Select(mk => mk.FileName.String).ToList();
                foreach (var folder in origin.CorrespondingFolderPaths)
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    scopes.Add(new RenderScope(folder, originKeyNames));
                }
            }
        }

        // scopes[2..N] — active mod's folders, in CorrespondingFolderPaths
        // order (so the last folder ends up as the LAST list entry, which
        // is the highest priority under last-to-first iteration). Each
        // folder pairs with the same CorrespondingModKeys list — the spec
        // calls for "BSAs at this folder owned by any of the
        // CorrespondingModKeys".
        //
        // Copy Assets OFF demotes these scopes below the data folder + the
        // broadcast archive tier (engine-order mode only): nothing from the
        // mod's folders will be copied into the output, so at runtime the
        // game resolves the NPC's assets from the user's live setup — the
        // preview should rank sources the same way. The scopes stay in the
        // chain as a last resort so browsing a mod that isn't enabled still
        // shows content, and FaceGen paths ignore the demotion inside the
        // resolver because FaceGen is copied regardless of Copy Assets.
        if (modSetting != null && modSetting.CorrespondingFolderPaths.Count > 0)
        {
            bool demoteBelowDataFolder = !modSetting.CopyAssets;
            var modKeyNames = modSetting.CorrespondingModKeys
                .Select(mk => mk.FileName.String)
                .ToList();
            foreach (var folder in modSetting.CorrespondingFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                scopes.Add(new RenderScope(folder, modKeyNames)
                {
                    DeprioritizeBelowDataFolder = demoteBelowDataFolder
                });
            }
        }

        return scopes;
    }

    /// <summary>
    /// Returns the folder list for the user-selected appearance mod for
    /// <paramref name="targetNpcFormKey"/> (consulting
    /// <see cref="NpcConsistencyProvider"/>), or null if no selection is
    /// recorded. Used by the live preview, which renders the user's chosen
    /// final selection. Mugshot tiles call <see cref="GetModFolders"/>
    /// directly with the tile's own mod instead.
    /// </summary>
    public IReadOnlyList<string>? GetSelectedModFolders(FormKey targetNpcFormKey)
        => GetModFolders(LookupSelectedModSetting(targetNpcFormKey, out _));

    /// <summary>
    /// Live-preview counterpart to <see cref="BuildResolutionScopes"/> —
    /// builds the strict resolution chain for the user's active appearance
    /// selection (consulting <see cref="NpcConsistencyProvider"/>). Mugshot
    /// tiles call <see cref="BuildResolutionScopes"/> directly with the
    /// tile's own mod instead.
    /// </summary>
    public IReadOnlyList<RenderScope> BuildResolutionScopesForActiveSelection(FormKey targetNpcFormKey)
    {
        // Pass the SOURCE key, not the target: the origin scope asks "which mod
        // added the NPC whose face we are drawing", and for a guest appearance
        // that is the donor. Dropping this key cost the preview the origin scope
        // the mugshot gets, so assets living in the mod that originally added the
        // NPC (face tints, outfit meshes) silently failed to resolve.
        var modSetting = LookupSelectedModSetting(targetNpcFormKey, out var sourceFormKey);
        return BuildResolutionScopes(modSetting, sourceFormKey);
    }


    /// <summary>
    /// Resolves the NPC's mesh paths scoped to a specific
    /// <paramref name="modSetting"/>. Used by per-tile mugshot generators
    /// where each tile must render the NPC from THAT TILE'S source mod —
    /// passing <paramref name="modSetting"/> explicitly avoids the
    /// every-tile-looks-the-same bug that arises from defaulting to the
    /// user's currently-selected mod via
    /// <see cref="NpcConsistencyProvider.GetSelectedMod"/>.
    ///
    /// <para>When <paramref name="modSetting"/> is null no mod scope is
    /// applied and resolution falls back to the LinkCache winner +
    /// vanilla data folder.</para>
    /// </summary>
    public ResolvedNpcMeshPaths? Resolve(FormKey npcFormKey, ModSetting? modSetting)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return null;
        // A Traits-templated NPC has no appearance of its own — resolve the whole
        // render (record, FaceGen NIF, FaceTint, skin) against the record at the end
        // of its template chain, exactly as the game does.
        var appearanceKey = ResolveAppearanceNpcKey(npcFormKey, modSetting);
        // npcFormKey, not appearanceKey, for the manual wig/antler designation scope: the user made
        // those designations on the NPC they had open, and this is the only place that still knows
        // which one that was. See ComputeWigHideHeadShapeNames.
        return Resolve(appearanceKey, linkCache, BuildContext(appearanceKey, modSetting), modSetting,
            designationScopeKey: npcFormKey);
    }

    /// <summary>
    /// The NPC whose appearance is actually drawn for <paramref name="npcFormKey"/> under
    /// <paramref name="modSetting"/>'s record scope: itself normally, or the end of its
    /// Traits template chain when it inherits — a templated NPC ships no FaceGen, head
    /// parts or skin of its own, so the game renders the template's face for it.
    ///
    /// <para>Every hop resolves through the same mod-scoped path the render uses, so a mod
    /// that re-points an NPC's template is honoured (and two mods may legitimately answer
    /// differently for the same NPC). A chain that can't be followed — dangling link,
    /// Leveled-NPC terminus, cycle — yields <paramref name="npcFormKey"/> unchanged, so
    /// callers fall back to their existing "no FaceGen" behaviour.</para>
    /// </summary>
    public FormKey ResolveAppearanceNpcKey(FormKey npcFormKey, ModSetting? modSetting)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return npcFormKey;

        var appearanceKey = Auxilliary.ResolveAppearanceTemplateTerminus(
            npcFormKey,
            fk => ResolveRecord<INpcGetter>(fk.ToLink<INpcGetter>(), linkCache, BuildContext(fk, modSetting)));

        if (!appearanceKey.Equals(npcFormKey))
        {
            LogVerbose("CharacterViewer: " + npcFormKey + " inherits its traits from template "
                + appearanceKey + " — rendering the template's appearance.");
        }
        return appearanceKey;
    }

    /// <summary>
    /// True when the NPC's record — resolved in the MOD's own scope (mod plugins → origin
    /// family, never the load-order winner) — keeps the Traits template flag. The raw
    /// engine then renders the template terminus's face and NEVER loads a FaceGen shipped
    /// at this NPC's own path, so a mod-shipped file there is engine-inert unpatched (SOGS
    /// field specimen, docs/DarkFaceTriggerInvestigation-2026-08.md field report 3 — the
    /// Winner-scoped appearance hop used for RENDERING had followed a load-order override
    /// that stripped the template, so the scanner graded a file the mod's own context
    /// never shows).
    /// </summary>
    public bool KeepsTraitsTemplateInModScope(FormKey npcFormKey, ModSetting? modSetting)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return false;
        var context = BuildContext(npcFormKey, modSetting, RecordHandler.RecordLookupFallBack.Origin);
        var npc = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        return npc != null &&
               npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) &&
               !npc.Template.IsNull;
    }

    /// <summary>
    /// Resolves the NPC and returns a head-part resolver bound to the SAME mod scope
    /// used for mesh resolution (mod plugins first, link cache fallback for shared /
    /// vanilla parts). Used by the FaceGen-vs-records consistency check so the head
    /// parts are evaluated against the exact records that produced the rendered NIF,
    /// rather than the load-order winner (which may be a different mod). Returns
    /// (null, _ => null) when the environment has no link cache.
    /// </summary>
    public (INpcGetter? Npc, Func<FormKey, IHeadPartGetter?> ResolveHeadPart, Func<FormKey, IRaceGetter?> ResolveRace,
            bool NpcFromModPlugins)
        ResolveNpcForConsistency(FormKey npcFormKey, ModSetting? modSetting)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return (null, _ => null, _ => null, false);
        // The rendered NIF belongs to the appearance source (a templated NPC renders
        // its template's FaceGen), so compare against THAT record's head parts.
        // The chain hop itself stays Winner-scoped (which NIF gets compared must match
        // what the renderer draws); only the record/head-part/race comparison below is
        // Origin-scoped — see NpcResolutionContext.FallbackMode for why a load-order
        // winner must not leak into a mod-scoped consistency verdict.
        var appearanceKey = ResolveAppearanceNpcKey(npcFormKey, modSetting);
        var context = BuildContext(appearanceKey, modSetting,
            RecordHandler.RecordLookupFallBack.Origin);
        var npc = ResolveRecord<INpcGetter>(appearanceKey.ToLink<INpcGetter>(), linkCache, context);
        Func<FormKey, IHeadPartGetter?> resolveHeadPart =
            fk => ResolveRecord<IHeadPartGetter>(fk.ToLink<IHeadPartGetter>(), linkCache, context);
        Func<FormKey, IRaceGetter?> resolveRace =
            fk => ResolveRecord<IRaceGetter>(fk.ToLink<IRaceGetter>(), linkCache, context);

        // Whether the mod's OWN plugins carry the record (vs the origin fallback
        // supplying it for a mesh-only pairing) — drives whose plugin/nif versions
        // the analyzer's single-slot remedy blames.
        bool npcFromModPlugins = npc != null && modSetting != null &&
            modSetting.CorrespondingModKeys.Count > 0 &&
            _recordHandler.TryGetRecordFromMods(appearanceKey.ToLink<INpcGetter>(),
                modSetting.CorrespondingModKeys, context?.FallBackFolderNames ?? new HashSet<string>(),
                RecordHandler.RecordLookupFallBack.None, out _, reverseOrder: true);

        return (npc, resolveHeadPart, resolveRace, npcFromModPlugins);
    }

    /// <summary>
    /// Resolves the NPC's record from EACH of the mod's plugins that carries it, in
    /// CorrespondingModKeys order. The Mod Issues scanner grades the dark-face class per
    /// plugin so a "scan the mod" verdict cannot silently depend on the user's current
    /// per-NPC source-plugin pin (the WICO field case, 2026-08-17: the two plugins carry
    /// wildly different head-part sets for the same NPC — one with zero parts — and only
    /// one matches the shipped bake). No fallback of any kind: a plugin that doesn't
    /// carry the record contributes nothing, and an empty result means no mod plugin
    /// carries it (mesh-only pairing — callers keep the pinned/origin resolution).
    /// </summary>
    public IReadOnlyList<(ModKey Plugin, INpcGetter Record)> ResolveNpcRecordPerPlugin(
        FormKey npcFormKey, ModSetting modSetting)
    {
        var results = new List<(ModKey, INpcGetter)>();
        var folderNames = modSetting.CorrespondingFolderPaths.ToHashSet();
        var link = npcFormKey.ToLink<INpcGetter>();
        foreach (var modKey in modSetting.CorrespondingModKeys)
        {
            if (_recordHandler.TryGetRecordGetterFromMod(link, modKey, folderNames,
                    RecordHandler.RecordLookupFallBack.None, out var rec) &&
                rec is INpcGetter npcRec)
            {
                results.Add((modKey, npcRec));
            }
        }
        return results;
    }

    /// <summary>
    /// Convenience entry point that consults the user's active appearance-mod
    /// selection (via <see cref="NpcConsistencyProvider"/>) and builds a
    /// <see cref="NpcResolutionContext"/> automatically. When no selection is
    /// recorded for <paramref name="targetNpcFormKey"/>, falls back to the
    /// pure load-order path (LinkCache only). Used by the live preview where
    /// the renderer should reflect the user's final chosen selection. Mugshot
    /// tiles call <see cref="Resolve(FormKey, ModSetting?)"/> directly with
    /// the tile's own mod.
    /// </summary>
    public ResolvedNpcMeshPaths? ResolveForActiveSelection(FormKey targetNpcFormKey)
    {
        var modSetting = LookupSelectedModSetting(targetNpcFormKey, out var sourceFormKey);
        return Resolve(sourceFormKey, modSetting);
    }

    /// <summary>
    /// Looks up the user's selected appearance mod for
    /// <paramref name="targetNpcFormKey"/> via <see cref="NpcConsistencyProvider"/>
    /// and resolves the matching <see cref="ModSetting"/> from
    /// <see cref="Settings.ModSettings"/>. <paramref name="sourceFormKey"/>
    /// receives the appearance-source NPC FormKey when the selection
    /// includes one (guest appearances) and falls back to
    /// <paramref name="targetNpcFormKey"/> otherwise.
    /// </summary>
    private ModSetting? LookupSelectedModSetting(FormKey targetNpcFormKey, out FormKey sourceFormKey)
    {
        var selection = _consistency.GetSelectedMod(targetNpcFormKey);
        sourceFormKey = (!string.IsNullOrEmpty(selection.ModName) && !selection.SourceNpcFormKey.IsNull)
            ? selection.SourceNpcFormKey
            : targetNpcFormKey;
        if (string.IsNullOrEmpty(selection.ModName)) return null;
        return _settings.ModSettings.FirstOrDefault(m => m.DisplayName == selection.ModName);
    }

    /// <summary>
    /// Builds an <see cref="NpcResolutionContext"/> for the given mod scope.
    /// Returns null when <paramref name="modSetting"/> is null so the
    /// downstream resolver falls through to the LinkCache-only path.
    /// <paramref name="fallbackMode"/> selects what happens when the mod's own
    /// plugins lack a record — Winner (render paths) or Origin (consistency
    /// checks); see <see cref="NpcResolutionContext.FallbackMode"/>.
    /// </summary>
    private NpcResolutionContext? BuildContext(FormKey targetNpcFormKey, ModSetting? modSetting,
        RecordHandler.RecordLookupFallBack fallbackMode = RecordHandler.RecordLookupFallBack.Winner)
    {
        if (modSetting == null) return null;

        // Stored as a separate local so the conditional doesn't pick
        // ModKey's string-implicit conversion over ModKey? as the common
        // type — `(condition ? ModKey : null)` would otherwise route through
        // op_Implicit(string) and throw on null.
        ModKey? disambiguation = null;
        if (modSetting.NpcPluginDisambiguation.TryGetValue(targetNpcFormKey, out var dis))
        {
            disambiguation = dis;
        }

        LogVerbose("CharacterViewer: Mod-scoped resolve via '" + modSetting.DisplayName +
            "' (" + modSetting.CorrespondingModKeys.Count + " plugin(s), " +
            modSetting.CorrespondingFolderPaths.Count + " folder(s)); target NPC=" + targetNpcFormKey);

        var origin = FindOriginModSetting(targetNpcFormKey.ModKey, modSetting);

        return new NpcResolutionContext
        {
            PreferredModKeys = modSetting.CorrespondingModKeys,
            PreferredFolderPaths = modSetting.CorrespondingFolderPaths,
            FallBackFolderNames = modSetting.CorrespondingFolderPaths.ToHashSet(),
            OriginModKeys = origin?.CorrespondingModKeys ?? (IReadOnlyList<ModKey>)Array.Empty<ModKey>(),
            OriginFolderPaths = origin?.CorrespondingFolderPaths ?? (IReadOnlyList<string>)Array.Empty<string>(),
            DisambiguationModKey = disambiguation,
            FallbackMode = fallbackMode,
        };
    }

    /// <summary>
    /// The mod entry owning the plugin an NPC is first defined in. Mirrors
    /// <see cref="AssetHandler.FindOriginModSetting"/>; resolution is by
    /// <see cref="ModSetting.CorrespondingModKeys"/> because a mod's assets routinely live in a
    /// sibling plugin's archive (Interesting NPCs keeps Casival's FaceGen, filed under
    /// "3DNPC.esp", inside 3DNPC0.bsa — owned by 3DNPC0.esp), and a ModSetting groups all of a
    /// mod's plugins together. Returns null when the selected mod IS the origin, since it is
    /// already the highest-priority scope.
    /// </summary>
    private ModSetting? FindOriginModSetting(ModKey originKey, ModSetting selected)
    {
        if (originKey.IsNull) return null;
        if (selected.CorrespondingModKeys.Contains(originKey)) return null;

        var candidates = _settings.ModSettings
            .Where(ms => ms.CorrespondingModKeys.Contains(originKey))
            .ToList();

        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(ms => ms.CorrespondingFolderPaths.Count > 0) ?? candidates[0];
    }

    /// <summary>
    /// Returns true when a FaceGen head NIF for <paramref name="npcFormKey"/>
    /// can be located via the same scope-iteration logic the renderer uses:
    /// loose files under each of <paramref name="modSetting"/>'s folders
    /// (the vanilla loose scope is skipped, mirroring the renderer's hard
    /// Phase 1 FaceGen skip), then any scoped BSA owned by the matching
    /// plugin keys (vanilla BSA last). Callers use this to early-abort
    /// templated NPCs (no FaceGen on disk) before the renderer would
    /// otherwise produce a headless body — see
    /// <see cref="InternalMugshotGenerator"/>'s pre-render gate.
    /// </summary>
    public bool FaceGenExists(FormKey npcFormKey, ModSetting? modSetting)
    {
        // FaceGen is keyed by the record that owns the appearance: a templated NPC's
        // face ships under its template's FormID, so probe for that one.
        var appearanceKey = ResolveAppearanceNpcKey(npcFormKey, modSetting);
        var context = BuildContext(appearanceKey, modSetting);
        string facegenPath = BuildFaceGenPath(appearanceKey);

        // Loose-file probe under any preferred mod folder rebases to absolute;
        // if that absolute path lands on disk, we're done.
        var rebased = RebaseToAbsoluteIfPresent(facegenPath, context);
        if (rebased != null && Path.IsPathRooted(rebased) && File.Exists(rebased)) return true;

        var scopes = BuildDiagnosticScopes(context);

        // Phase 1: loose files, highest-priority-first. Skip vanilla (i==0)
        // for FaceGen — same rule the renderer enforces.
        for (int i = scopes.Count - 1; i >= 1; i--)
        {
            var scope = scopes[i];
            if (string.IsNullOrWhiteSpace(scope.FolderPath)) continue;
            string candidate;
            try { candidate = Path.Combine(scope.FolderPath, facegenPath); }
            catch { continue; }
            if (File.Exists(candidate)) return true;
        }

        // Phase 2: scoped BSAs, highest-priority-first (vanilla included).
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (string.IsNullOrWhiteSpace(scope.FolderPath)) continue;
            foreach (var keyName in scope.ModKeyFileNames)
            {
                if (!ModKey.TryFromNameAndExtension(keyName, out var modKey)) continue;
                if (_bsaHandler.FileExistsInArchiveAtFolder(facegenPath, modKey, scope.FolderPath, out var bsaPath) &&
                    bsaPath != null)
                {
                    return true;
                }
            }
        }

        // Phase 3: what the GAME itself would resolve.
        //
        // The scopes above only cover the selected mod and vanilla, but an appearance mod may
        // legitimately ship a face tint without the mesh and let the mesh come from the mod that
        // originally added the NPC — a whole class of mods works this way, and the FaceGen ladder
        // now sources exactly that case. Answering "no" for those made this gate refuse to render
        // precisely the NPCs the ladder exists to serve, even though the resolution AFTER the gate
        // falls through to the game asset resolver and would have found the mesh.
        //
        // The question this gate actually means to ask (per its own remarks) is whether the NPC is
        // genuinely FaceGen-less, so it ends by asking the engine's question: is there anything at
        // this path in the live data folder, or in ANY indexed archive.
        //
        // Only the live data folder, deliberately. An earlier version also probed every indexed
        // archive, on the theory that the loader would fall through to a broad lookup — it does
        // not. FaceGen resolution uses TryLocateInScopedBsa exclusively, so a broad "yes" here
        // promised something the loader could not deliver and the renderer drew a headless body
        // instead of skipping cleanly. The gate must never answer more optimistically than the
        // scopes above, which is why cross-mod reach was added to those scopes (see the origin
        // entries in BuildDiagnosticScopes) rather than to this backstop.
        try
        {
            if (File.Exists(Path.Combine(_env.DataFolderPath, facegenPath))) return true;
        }
        catch
        {
            // Malformed data-folder path — treat as absent.
        }

        return false;
    }

    /// <summary>
    /// Resolves the full set of paths for the NPC. Returns null if the NPC
    /// itself can't be resolved either from the context's plugins or the
    /// link cache.
    /// </summary>
    /// <param name="designationScopeKey">
    /// The NPC the user actually has open, when that differs from <paramref name="npcFormKey"/> —
    /// i.e. the donor, where <paramref name="npcFormKey"/> has already been advanced to a Traits
    /// terminus. Used ONLY as the scope key for manual wig designations, which the UI stores against
    /// the open NPC. Null means "they are the same NPC".
    /// </param>
    public ResolvedNpcMeshPaths? Resolve(FormKey npcFormKey, ILinkCache linkCache,
        NpcResolutionContext? context = null, ModSetting? modSetting = null,
        FormKey? designationScopeKey = null)
    {
        var npcGetter = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        if (npcGetter == null)
        {
            _logger?.LogError("CharacterViewer: Could not resolve NPC " + npcFormKey);
            return null;
        }

        var sex = Auxilliary.IsFemale(npcGetter) ? NpcGender.Female : NpcGender.Male;
        string npcName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcFormKey.ToString();
        LogVerbose("CharacterViewer: Resolving NPC " + npcName + " (" + npcFormKey + ")");

        FormKey? npcRaceKey = (npcGetter.Race != null && !npcGetter.Race.IsNull) ? npcGetter.Race.FormKey : null;
        FormKey? armorRaceKey = ResolveArmorRaceKey(npcGetter, linkCache, context);

        var (armorGetter, armorSource) = ResolveWornArmor(npcGetter, linkCache, context, npcName);

        string? bodyPath = null;
        string? handsPath = null;
        string? feetPath = null;
        string? hairPath = null;
        string? tailPath = null;
        var chains = new Dictionary<string, string>();
        var txstTextures = new Dictionary<string, Dictionary<int, string>>();
        string sexLabel = sex == NpcGender.Female ? "Female" : "Male";

        if (armorGetter?.Armature != null)
        {
            foreach (var armaLink in armorGetter.Armature)
            {
                var armaGetter = ResolveRecord<IArmorAddonGetter>(armaLink, linkCache, context);
                if (armaGetter == null) continue;
                if (armaGetter.BodyTemplate == null) continue;
                if (!IsArmatureForRace(armaGetter, npcRaceKey, armorRaceKey))
                {
                    LogVerbose("CharacterViewer: Skipping Armature " + armaLink.FormKey +
                        " — race " + (npcRaceKey?.ToString() ?? "(none)") +
                        " (ArmorRace " + (armorRaceKey?.ToString() ?? "(none)") + ")" +
                        " not in ARMA.Race/AdditionalRaces");
                    continue;
                }

                var flags = armaGetter.BodyTemplate.FirstPersonFlags;
                string? meshPath = GetWorldModelPath(armaGetter, sex);
                if (meshPath == null) continue;

                string meshFileName = Path.GetFileName(meshPath);
                var txstPaths = ResolveTxstTextures(armaGetter, sex, linkCache, context, armaLink.FormKey);

                if (bodyPath == null && flags.HasFlag(BipedObjectFlag.Body))
                {
                    bodyPath = meshPath;
                    chains["Body"] = npcName + " → " + armorSource + " → Armature(Body):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Body]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Body"] = txstPaths;
                }
                if (handsPath == null && flags.HasFlag(BipedObjectFlag.Hands))
                {
                    handsPath = meshPath;
                    chains["Hands"] = npcName + " → " + armorSource + " → Armature(Hands):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Hands]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Hands"] = txstPaths;
                }
                if (feetPath == null && flags.HasFlag(BipedObjectFlag.Feet))
                {
                    feetPath = meshPath;
                    chains["Feet"] = npcName + " → " + armorSource + " → Armature(Feet):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Feet]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Feet"] = txstPaths;
                }
                // Hair (biped slots 31 Hair / 41 LongHair): NPC overhauls like
                // High Poly NPC Overhaul ship a "bald" FaceGen scalp + a wig
                // ARMO whose ARMA occupies a hair slot. Without picking it up
                // here the NPC renders hairless. Both slots match, mirroring
                // WigDetector.HairSlots (detection/conversion parity).
                if (hairPath == null && (flags & WigDetector.HairSlots) != 0)
                {
                    hairPath = meshPath;
                    chains["Hair"] = npcName + " → " + armorSource + " → Armature(Hair):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Hair]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Hair"] = txstPaths;
                }
                // Tail (biped slot 40): required for Khajiit / Argonian
                // races whose tails are armatures rather than shapes baked
                // into a body NIF.
                if (tailPath == null && flags.HasFlag(BipedObjectFlag.Tail))
                {
                    tailPath = meshPath;
                    chains["Tail"] = npcName + " → " + armorSource + " → Armature(Tail):" + armaLink.FormKey +
                        " → " + sexLabel + " → " + meshFileName;
                    LogVerbose("CharacterViewer: Armature[Tail]=" + armaLink.FormKey + ", WorldModel=" + meshPath);
                    if (txstPaths.Count > 0) txstTextures["Tail"] = txstPaths;
                }
            }
        }

        string headPath = BuildFaceGenPath(npcFormKey);
        chains["Head"] = npcName + " → FaceGen → " + Path.GetFileName(headPath);
        LogVerbose("CharacterViewer: FaceGen head mesh=" + headPath);

        string faceTintPath = BuildFaceTintPath(npcFormKey);
        string? skeletonPath = ResolveSkeletonPath(npcGetter, sex, linkCache, context);

        (float R, float G, float B)? textureLightingColor = null;
        var qnam = npcGetter.TextureLighting;
        if (qnam != null)
        {
            textureLightingColor = (qnam.Value.R / 255f, qnam.Value.G / 255f, qnam.Value.B / 255f);
            LogVerbose("CharacterViewer: TextureLighting (QNAM)=RGB(" +
                qnam.Value.R + ", " + qnam.Value.G + ", " + qnam.Value.B + ")");
        }

        int weight = Math.Clamp((int)npcGetter.Weight, 0, 100);
        float recordHeight = npcGetter.Height;
        float baseHeight = (float.IsFinite(recordHeight) && recordHeight > 0f) ? recordHeight : 1f;

        (float R, float G, float B)? hairRgb = null;
        if (npcGetter.HairColor != null && !npcGetter.HairColor.IsNull)
        {
            var hclr = ResolveRecord<IColorRecordGetter>(npcGetter.HairColor, linkCache, context);
            if (hclr != null)
            {
                var c = hclr.Color;
                hairRgb = (c.R / 255f, c.G / 255f, c.B / 255f);
            }
        }

        // Eyeball shape names from the resolved HeadPart records — the
        // authoritative IsEye input for the renderer. Custom eyes authored as
        // BSLSP_ENVMAP with unconventional shape names (FoxGlove Auri's
        // "FoxGloveEyeMesh") evade the renderer's plural-"Eyes" name
        // heuristic and would receive eye-socket SSAO plus lose the
        // catchlight; the baked FaceGen shape is named after the head part's
        // EditorID, so this set identifies them exactly.
        var eyeShapeNames = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npcGetter,
            fk => ResolveRecord<IHeadPartGetter>(fk.ToLink<IHeadPartGetter>(), linkCache, context),
            fk => ResolveRecord<IRaceGetter>(fk.ToLink<IRaceGetter>(), linkCache, context),
            HeadPart.TypeEnum.Eyes);
        if (eyeShapeNames.Count > 0)
            LogVerbose("CharacterViewer: Eyes HeadPart shape name(s): " + string.Join(", ", eyeShapeNames));

        // Antler Remove (source 3): the patch strips keyword-detected antler head
        // parts from the FaceGen NIF, so the preview/mugshot must not draw those
        // baked shapes either. Collect their shape names (head part EditorID +
        // ExtraParts EditorIDs) so the renderer hides them (HideHeadShapeNames).
        var hideHeadShapeNames = ComputeAntlerHideHeadShapeNames(npcGetter, modSetting, linkCache, context);
        if (hideHeadShapeNames.Count > 0)
            LogVerbose("CharacterViewer: hiding baked antler head shape(s): " + string.Join(", ", hideHeadShapeNames));

        // Skin-carried (WNAM) wig ConvertToHeadParts parity: the patch strips the
        // donor's Hair-type head parts and bakes the skin wig instead; the preview
        // already draws that wig through the Hair channel (WornArmor walk above),
        // so hiding the donor's baked hair shapes makes the preview depict the
        // output — and makes is/is-not-a-wig designations visibly change it.
        var wigHideNames = ComputeWigHideHeadShapeNames(npcGetter, modSetting, linkCache, context, npcRaceKey,
            armorRaceKey, designationScopeKey);
        if (wigHideNames.Count > 0)
        {
            var union = new HashSet<string>(hideHeadShapeNames, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(wigHideNames);
            hideHeadShapeNames = union;
            LogVerbose("CharacterViewer: hiding donor hair shape(s) superseded by the skin-carried wig: " +
                       string.Join(", ", wigHideNames));
        }

        // Final pass: rebase any path that exists as a loose file under one of
        // the context's mod folders to its absolute disk path. The renderer's
        // GameAssetResolver passes rooted paths through unchanged; everything
        // else stays game-relative and resolves against the vanilla data folder
        // / BSAs as before.
        bodyPath = RebaseToAbsoluteIfPresent(bodyPath, context);
        handsPath = RebaseToAbsoluteIfPresent(handsPath, context);
        feetPath = RebaseToAbsoluteIfPresent(feetPath, context);
        hairPath = RebaseToAbsoluteIfPresent(hairPath, context);
        tailPath = RebaseToAbsoluteIfPresent(tailPath, context);
        string finalHeadPath = RebaseToAbsoluteIfPresent(headPath, context) ?? headPath;
        string? finalFaceTintPath = RebaseToAbsoluteIfPresent(faceTintPath, context);
        skeletonPath = RebaseToAbsoluteIfPresent(skeletonPath, context);
        foreach (var bodyPart in txstTextures.Keys.ToList())
        {
            var slotMap = txstTextures[bodyPart];
            foreach (var slot in slotMap.Keys.ToList())
            {
                var rebased = RebaseToAbsoluteIfPresent(slotMap[slot], context);
                if (rebased != null) slotMap[slot] = rebased;
            }
        }

        // Diagnostic pass: log every path's expected location and warn if it
        // cannot be found anywhere (loose under any preferred mod folder, loose
        // in the vanilla data folder, or inside any indexed BSA). This is the
        // ONLY place that has full visibility — the renderer's GameAssetResolver
        // logs internally only when RenderLogGate.Verbose is on, and
        // that doesn't tell us about absolute paths it never resolves.
        TraceAssetResolution(npcFormKey, npcName, context, bodyPath, handsPath, feetPath,
            finalHeadPath, skeletonPath, finalFaceTintPath, hairPath, tailPath, txstTextures);

        return new ResolvedNpcMeshPaths
        {
            BodyMeshPath = bodyPath,
            HandsMeshPath = handsPath,
            FeetMeshPath = feetPath,
            HeadMeshPath = finalHeadPath,
            HairMeshPath = hairPath,
            TailMeshPath = tailPath,
            Gender = sex,
            SkeletonPath = skeletonPath,
            ResolutionChains = chains,
            TxstTextures = txstTextures,
            FaceTintPath = finalFaceTintPath,
            TextureLightingColor = textureLightingColor,
            NpcWeight = weight,
            NpcBaseHeight = baseHeight,
            HairColorRgb = hairRgb,
            // Emulate RaceMenu's automatic tinting of worn hair-slot items
            // (skee64 bEnableTintHairSlot) so a wig authored with a placeholder
            // tint previews the way the game draws it. Non-null also ENABLES the
            // emulation; the renderer prefers the FaceGen's own baked hair color
            // over this and only falls back here when the FaceGen has none. x2
            // because that is the space FaceGen tints live in (see
            // NifHandler.HclrToFaceGenTint) — the same value the wig→HeadPart
            // bake writes, so the preview matches the patch output.
            WornHairSlotTintRgb = _settings.InternalMugshot.EmulateRaceMenuHairSlotTint && hairRgb != null
                ? NifHandler.HclrToFaceGenTint(hairRgb.Value)
                : null,
            EyeShapeNames = eyeShapeNames,
            HideHeadShapeNames = hideHeadShapeNames,
        };
    }

    /// <summary>Shape names to hide from the FaceGen head when the effective
    /// antler mode is Remove: the NPC's keyword-detected antler head parts
    /// (<see cref="ModSetting.DetectedAntlerHeadParts"/>) — their EditorIDs plus
    /// ExtraParts' EditorIDs (baked FaceGen shapes are named after them). Mirrors
    /// <c>WigForwarder.CollectAntlerHeadPartRemoval</c> so the preview matches the
    /// patched output. Empty unless antler Remove applies. Uses the RENDER antler
    /// mode so the harness override and the output-mode gate are honored.</summary>
    private IReadOnlySet<string> ComputeAntlerHideHeadShapeNames(INpcGetter npcGetter, ModSetting? modSetting,
        ILinkCache linkCache, NpcResolutionContext? context)
        => CollectAntlerHiddenShapeNames(npcGetter, modSetting, _settings,
            link => ResolveRecord<IHeadPartGetter>(link, linkCache, context));

    /// <summary>
    /// The FaceGen shape names antler Remove hides, resolved through a
    /// caller-supplied head-part resolver. Shared with
    /// <c>OutfitDisplayResolver.ComputeWigIdentitySuffix</c>, which folds the
    /// result into the mugshot's depicted-content identity stamp: hiding baked
    /// antlers changes the image, so the tile must go stale when the set changes
    /// (switching a mod to Remove, or designating a head part in the 3D preview).
    /// Keeping both callers on one implementation is the point — a private copy
    /// in the identity resolver would silently drift from what is drawn.
    /// </summary>
    internal static IReadOnlySet<string> CollectAntlerHiddenShapeNames(
        INpcGetter npcGetter, ModSetting? modSetting, Settings settings,
        Func<IFormLinkGetter<IHeadPartGetter>, IHeadPartGetter?> resolveHeadPart)
    {
        if (modSetting == null) return EmptyShapeNameSet;
        if (settings.GetEffectiveRenderAntlerMode(modSetting) != AntlerHandlingMode.Remove)
            return EmptyShapeNameSet;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hpLink in npcGetter.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            var hpRec = resolveHeadPart(hpLink);
            if (hpRec == null) continue;

            // Mirror WigForwarder.CollectAntlerHeadPartRemoval: the whole head part is
            // an antler (keyword-detected OR its own EditorID designated = "remove the
            // whole head part") → hide its own shape AND every ExtraPart shape; else
            // hide only the individually-designated ExtraPart shapes.
            bool groupAuto = modSetting.DetectedAntlerHeadParts.Contains(hpLink.FormKey);
            bool mainRemoved = groupAuto ||
                settings.IsManualAntlerHeadPart(hpRec.EditorID, modSetting.DisplayName, npcGetter.FormKey);

            if (mainRemoved && !string.IsNullOrEmpty(hpRec.EditorID)) names.Add(hpRec.EditorID);
            if (hpRec.ExtraParts != null)
            {
                foreach (var extraLink in hpRec.ExtraParts)
                {
                    if (extraLink == null || extraLink.IsNull) continue;
                    var extraRec = resolveHeadPart(extraLink);
                    if (string.IsNullOrEmpty(extraRec?.EditorID)) continue;
                    if (mainRemoved ||
                        settings.IsManualAntlerHeadPart(extraRec.EditorID, modSetting.DisplayName, npcGetter.FormKey))
                        names.Add(extraRec.EditorID);
                }
            }
        }
        return names;
    }

    /// <summary>Shape names to hide from the FaceGen head when the effective
    /// RENDER wig mode is ConvertToHeadParts and the donor's skin carries the
    /// NPC's wig (the WNAM source): the donor's Hair-type head parts' EditorIDs
    /// plus their ExtraParts' EditorIDs. Mirrors
    /// <see cref="HeadPartWigConverter"/>'s WNAM-path gates — exactly one
    /// race-applicable effective wig ARMA (<see cref="Settings.IsWigArmature"/>),
    /// and no detected outfit wig (the outfit-wig render plan owns that case).
    /// Empty when nothing applies.</summary>
    /// <param name="designationScopeKey">
    /// The NPC a manual wig designation would have been stored against — the one the user had open,
    /// which for a templated NPC is NOT <paramref name="npcGetter"/> (that has already been advanced
    /// to the Traits terminus). The UI stores designations against the open NPC
    /// (<c>VM_InternalMugshotPreview.PopulateWigSelector</c>) and the patcher reads them back under
    /// the donor's key, so this reader has to agree with both or a <c>SpecificNpc</c>-scoped
    /// designation is written under one key and looked up under another. Null falls back to
    /// <paramref name="npcGetter"/>'s own key, which is correct for an untemplated NPC.
    /// </param>
    private IReadOnlySet<string> ComputeWigHideHeadShapeNames(INpcGetter npcGetter, ModSetting? modSetting,
        ILinkCache linkCache, NpcResolutionContext? context, FormKey? npcRaceKey, FormKey? armorRaceKey,
        FormKey? designationScopeKey = null)
    {
        if (modSetting == null) return EmptyShapeNameSet;
        if (_settings.GetEffectiveRenderWigMode(modSetting) != WigHandlingMode.ConvertToHeadParts)
            return EmptyShapeNameSet;
        if (npcGetter.WornArmor.IsNull) return EmptyShapeNameSet;

        // An outfit wig converting for this NPC owns the donor-hair hiding
        // (BuildWigRenderPlan flow) — don't double-cover.
        if (!npcGetter.DefaultOutfit.IsNull)
        {
            var outfit = ResolveRecord<IOutfitGetter>(npcGetter.DefaultOutfit, linkCache, context);
            if (outfit?.Items != null && outfit.Items.Any(i =>
                    i != null && !i.IsNull && modSetting.DetectedWigArmors.Contains(i.FormKey)))
            {
                return EmptyShapeNameSet;
            }
        }

        var wnam = ResolveRecord<IArmorGetter>(npcGetter.WornArmor, linkCache, context);
        var scopeKey = designationScopeKey ?? npcGetter.FormKey;
        int applicableWigArmas = WigDetector.EffectiveWnamWigArmatures(
            wnam,
            link => ResolveRecord<IArmorAddonGetter>(link, linkCache, context),
            arma => _settings.IsWigArmature(modSetting, arma.FormKey, arma.EditorID, scopeKey),
            arma => IsArmatureForRace(arma, npcRaceKey, armorRaceKey))
            .Count();

        // The converter declines zero or multiple applicable wig ARMAs — the
        // donor state is then preserved, so the preview must not hide anything.
        if (applicableWigArmas != 1) return EmptyShapeNameSet;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hpLink in npcGetter.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            var hpRec = ResolveRecord<IHeadPartGetter>(hpLink, linkCache, context);
            if (hpRec?.Type != HeadPart.TypeEnum.Hair) continue;
            if (!string.IsNullOrEmpty(hpRec.EditorID)) names.Add(hpRec.EditorID);
            if (hpRec.ExtraParts == null) continue;
            foreach (var extraLink in hpRec.ExtraParts)
            {
                if (extraLink == null || extraLink.IsNull) continue;
                var extraRec = ResolveRecord<IHeadPartGetter>(extraLink, linkCache, context);
                if (!string.IsNullOrEmpty(extraRec?.EditorID)) names.Add(extraRec.EditorID);
            }
        }
        return names;
    }

    private static readonly IReadOnlySet<string> EmptyShapeNameSet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────
    //  Attire / headgear mesh-override resolution
    //  ("Include Default Outfit" / "Include headgear" preview features).
    //  Resolves Mutagen Armor / Outfit / NPC records into neutral
    //  CharacterViewer.Rendering MeshOverrides fed through
    //  VM_CharacterViewer.ApplyMeshOverrides. Mutagen stays host-side here, like
    //  the rest of this resolver; the renderer never sees a Mutagen type. Slot
    //  occupancy/hiding is the renderer's job — body armor hides the nude body,
    //  headgear hides hair — so this only has to label each piece with its biped
    //  slots + Kind. See Part 2 of the Character Preview design.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Biped-object-flag bits that mark a piece as headgear rather than
    /// body attire: Head (slot 30 = 0x1) and Circlet (slot 42 = 0x1000). The
    /// Hair bit (slot 31) is deliberately excluded from this classification —
    /// a Hair-only armature is a wig/hairpiece, not a helmet, and treating it as
    /// headgear would wrongly hide the very hair it provides.</summary>
    private const int HeadSlotMask = 0x1 | 0x1000;

    /// <summary>The Head/face biped slot bit (slot 30 = 0x1). Never added to a
    /// piece's hide mask: the FaceGen face (and the eyes/brows/mouth that fall
    /// back to slot 30) are tagged slot 30, so hiding it would cull the face out
    /// from under any helmet.</summary>
    private const int HeadFaceSlotBit = 0x1;

    /// <summary>The Hair biped slot bit (slot 31 = 0x2). A helmet/hood that
    /// occupies the Head (30) or Hair (31) slot hides this so it replaces the
    /// NPC's hair the way it does in game; an open circlet (slot 42 only) does
    /// not occupy either, so it leaves the hair visible.</summary>
    private const int HairSlotBit = 0x2;

    /// <summary>
    /// Resolves the user's active appearance selection for
    /// <paramref name="targetNpcFormKey"/> (via
    /// <see cref="NpcConsistencyProvider"/>) and builds the attire / headgear
    /// MeshOverrides for it. Live-preview counterpart to
    /// <see cref="ResolveAttireMeshOverrides(FormKey, ModSetting?, bool, bool)"/>;
    /// the per-tile popup calls that overload directly with the tile's own mod.
    /// </summary>
    public IReadOnlyList<MeshOverride> ResolveAttireMeshOverridesForActiveSelection(
        FormKey targetNpcFormKey, bool includeDefaultOutfit, bool includeHeadgear)
        => ResolveAttireMeshOverridesForActiveSelection(targetNpcFormKey, includeDefaultOutfit, includeHeadgear, out _);

    /// <summary>Overload exposing the effective-outfit resolution (source /
    /// warning / identity stamp) alongside the mesh overrides, so the live
    /// preview can surface runtime-distribution conflicts.</summary>
    public IReadOnlyList<MeshOverride> ResolveAttireMeshOverridesForActiveSelection(
        FormKey targetNpcFormKey, bool includeDefaultOutfit, bool includeHeadgear,
        out OutfitDisplayResult outfitDisplay)
    {
        var modSetting = LookupSelectedModSetting(targetNpcFormKey, out var sourceFormKey);
        return ResolveAttireMeshOverrides(sourceFormKey, modSetting, includeDefaultOutfit, includeHeadgear,
            targetNpcFormKey, out outfitDisplay);
    }

    /// <summary>Active-selection companion to
    /// <see cref="AntlerRemovalApplies"/> for the live preview's info banner.</summary>
    public bool AntlerRemovalAppliesForActiveSelection(FormKey targetNpcFormKey)
    {
        var modSetting = LookupSelectedModSetting(targetNpcFormKey, out var sourceFormKey);
        return AntlerRemovalApplies(sourceFormKey, modSetting);
    }

    /// <summary>Active-selection companion to <see cref="WigNotPersistedApplies"/>
    /// for the live preview's info banner.</summary>
    public WigPersistenceResult WigNotPersistedAppliesForActiveSelection(FormKey targetNpcFormKey)
    {
        var modSetting = LookupSelectedModSetting(targetNpcFormKey, out var sourceFormKey);
        return WigNotPersistedApplies(sourceFormKey, modSetting, targetNpcFormKey);
    }

    /// <summary>
    /// Whether this NPC carries a Default-Outfit wig that the patch will NOT carry
    /// into the output. Drives the preview's "wig not persisted" banner — the live
    /// preview renders output-faithfully (a mode-None wig obeys Include Headgear
    /// like any other head-slot outfit piece), so the banner is what tells the user
    /// the mod supplies a wig they aren't getting. The mugshot answers the same
    /// question by drawing the wig and crossing out its has-wig badge instead.
    /// <para>Pure settings + persisted scan data via
    /// <see cref="OutfitDisplayResolver.ComputeWigPersistence"/> — no record walk,
    /// unlike <see cref="AntlerRemovalApplies"/>.</para>
    /// </summary>
    public WigPersistenceResult WigNotPersistedApplies(FormKey npcFormKey, ModSetting? modSetting,
        FormKey? targetNpcFormKey = null)
        => _outfitDisplayResolver.ComputeWigPersistence(
            npcFormKey, targetNpcFormKey ?? npcFormKey, modSetting);

    /// <summary>
    /// True when the effective antler mode is Remove AND this NPC actually carries
    /// an antler that the patch will strip — from its Default Outfit (source 1),
    /// its WornArmor (source 2), or a keyword-detected FaceGen head part
    /// (source 3). Drives the preview's "antlers removed" notice; a cheap record
    /// walk mirroring what <see cref="WigForwarder"/> acts on.
    /// </summary>
    public bool AntlerRemovalApplies(FormKey npcFormKey, ModSetting? modSetting)
    {
        if (modSetting == null || !_settings.ModHasAntlers(modSetting)) return false;
        if (_settings.GetEffectiveRenderAntlerMode(modSetting) != AntlerHandlingMode.Remove) return false;
        var linkCache = _env.LinkCache;
        if (linkCache == null) return false;
        // Head parts and the WornArmor skin are traits-inherited — for a templated NPC
        // the antlers on screen belong to the template's record, so check that one.
        var appearanceKey = ResolveAppearanceNpcKey(npcFormKey, modSetting);
        var context = BuildContext(appearanceKey, modSetting);
        var npc = ResolveRecord<INpcGetter>(appearanceKey.ToLink<INpcGetter>(), linkCache, context);
        if (npc == null) return false;

        // Source 3: an antler baked shape on the NPC — the whole head part is
        // keyword-detected, or the user designated its main shape or any of its
        // ExtraPart shapes (per-shape, mirroring CollectAntlerHeadPartRemoval).
        foreach (var hp in npc.HeadParts)
        {
            if (hp == null || hp.IsNull) continue;
            if (modSetting.DetectedAntlerHeadParts.Contains(hp.FormKey)) return true;
            var hpRec = ResolveRecord<IHeadPartGetter>(hp, linkCache, context);
            if (hpRec == null) continue;
            if (_settings.IsManualAntlerHeadPart(hpRec.EditorID, modSetting.DisplayName, npc.FormKey)) return true;
            if (hpRec.ExtraParts != null)
            {
                foreach (var extraLink in hpRec.ExtraParts)
                {
                    if (extraLink == null || extraLink.IsNull) continue;
                    var extraRec = ResolveRecord<IHeadPartGetter>(extraLink, linkCache, context);
                    if (_settings.IsManualAntlerHeadPart(extraRec?.EditorID, modSetting.DisplayName, npc.FormKey))
                        return true;
                }
            }
        }

        // Source 2: antler ArmorAddon baked into the WornArmor.
        if (!npc.WornArmor.IsNull)
        {
            var wnam = ResolveRecord<IArmorGetter>(npc.WornArmor, linkCache, context);
            if (wnam?.Armature != null)
                foreach (var a in wnam.Armature)
                    if (a != null && !a.IsNull && modSetting.DetectedAntlerArmatures.Contains(a.FormKey)) return true;
        }

        // Source 1: antler ARMO in the Default Outfit.
        if (!npc.DefaultOutfit.IsNull)
        {
            var outfit = ResolveRecord<IOutfitGetter>(npc.DefaultOutfit, linkCache, context);
            if (outfit?.Items != null)
                foreach (var i in outfit.Items)
                    if (i != null && !i.IsNull && modSetting.DetectedAntlerArmors.Contains(i.FormKey)) return true;
        }

        return false;
    }

    /// <summary>One ExtraPart baked shape of a head part, for a child row of the
    /// "Set Antler Head Parts" selector. <see cref="ShapeName"/> is the baked NIF
    /// shape name (= the ExtraPart's EditorID); it is the highlight key AND the
    /// manual-designation key. Designating it strips the baked geometry AND removes
    /// the ExtraPart link from the parent head-part record (leaving the link while
    /// its shape is gone dark-faces the NPC — engine-verified).</summary>
    public sealed record AntlerHeadPartShape(
        FormKey HeadPartFormKey,
        string ShapeName,
        string DisplayName,
        bool IsAutoDetected,
        bool IsDesignated);

    /// <summary>One top-level head part of the previewed NPC (the selector's parent
    /// row). Checking the parent removes the WHOLE head part — the record reference
    /// AND every baked shape — keyed on <see cref="MainShapeName"/> (the head part's
    /// own EditorID). <see cref="ExtraShapes"/> are its ExtraParts, each an
    /// independently strippable child. <see cref="IsAutoDetected"/> = the whole head
    /// part is keyword-detected (locked on).</summary>
    public sealed record AntlerHeadPartGroup(
        FormKey HeadPartFormKey,
        string MainShapeName,
        string DisplayName,
        string TypeLabel,
        bool IsAutoDetected,
        bool IsMainDesignated,
        IReadOnlyList<AntlerHeadPartShape> ExtraShapes);

    /// <summary>Enumerates the previewed NPC's head parts as antler-selector groups
    /// (resolved through the mod scope, matching the rendered NIF). Each group is a
    /// top-level head part (parent = remove the whole head part); its
    /// <see cref="AntlerHeadPartGroup.ExtraShapes"/> are the ExtraParts the user can
    /// strip individually (e.g. antlers baked as an ExtraPart of the hair). Empty
    /// when the NPC or environment can't be resolved.</summary>
    public IReadOnlyList<AntlerHeadPartGroup> GetAntlerHeadPartCandidates(FormKey npcFormKey, ModSetting? modSetting)
    {
        var result = new List<AntlerHeadPartGroup>();
        if (modSetting == null) return result;
        var linkCache = _env.LinkCache;
        if (linkCache == null) return result;
        var context = BuildContext(npcFormKey, modSetting);
        var npc = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        if (npc == null) return result;

        var seenHeadParts = new HashSet<FormKey>();
        foreach (var hpLink in npc.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull || !seenHeadParts.Add(hpLink.FormKey)) continue;
            var hp = ResolveRecord<IHeadPartGetter>(hpLink, linkCache, context);
            if (hp == null || string.IsNullOrEmpty(hp.EditorID)) continue;

            bool groupAuto = modSetting.DetectedAntlerHeadParts.Contains(hpLink.FormKey);
            bool mainDesignated = groupAuto ||
                _settings.IsManualAntlerHeadPart(hp.EditorID, modSetting.DisplayName, npcFormKey);

            var extras = new List<AntlerHeadPartShape>();
            var seenShapeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hp.EditorID };
            if (hp.ExtraParts != null)
            {
                foreach (var extraLink in hp.ExtraParts)
                {
                    if (extraLink == null || extraLink.IsNull) continue;
                    var extraRec = ResolveRecord<IHeadPartGetter>(extraLink, linkCache, context);
                    if (string.IsNullOrEmpty(extraRec?.EditorID) || !seenShapeNames.Add(extraRec.EditorID)) continue;
                    // An ExtraPart is "designated" when the whole head part is (auto or
                    // main) — it goes with it — or when that specific ExtraPart is.
                    bool designated = mainDesignated ||
                        _settings.IsManualAntlerHeadPart(extraRec.EditorID, modSetting.DisplayName, npcFormKey);
                    string extraDisplay = extraRec.Name?.String;
                    if (string.IsNullOrEmpty(extraDisplay)) extraDisplay = extraRec.EditorID;
                    extras.Add(new AntlerHeadPartShape(extraLink.FormKey, extraRec.EditorID, extraDisplay,
                        groupAuto, designated));
                }
            }

            string display = hp.Name?.String ?? string.Empty;
            if (string.IsNullOrEmpty(display)) display = hp.EditorID;

            result.Add(new AntlerHeadPartGroup(hpLink.FormKey, hp.EditorID, display,
                hp.Type?.ToString() ?? "Misc", groupAuto, mainDesignated, extras));
        }
        return result;
    }

    /// <summary>One hair-slot ArmorAddon carried in the previewed NPC's
    /// WornArmor, for a row of the "Set Wig Meshes" selector.
    /// <see cref="EditorId"/> is the manual-designation key;
    /// <see cref="ShapeNames"/> are the resolved hair NIF's render shape names
    /// (the hover-highlight keys on the preview's Hair channel — empty when the
    /// mesh could not be read, leaving the row toggleable without a glow).
    /// <see cref="IsEffectiveWig"/> folds scan detection, manual vetoes and
    /// manual promotions (<see cref="Settings.IsWigArmature"/>).</summary>
    public sealed record WigArmatureCandidate(
        FormKey ArmaFormKey,
        string EditorId,
        string DisplayName,
        bool IsAutoDetected,
        bool IsEffectiveWig,
        IReadOnlyList<string> ShapeNames);

    /// <summary>Enumerates the previewed NPC's WNAM hair-slot ArmorAddons as
    /// wig-selector rows (resolved through the mod scope, matching the rendered
    /// NIF). ALL race-applicable hair-slot ARMAs are candidates — including
    /// slot-only ones the scan did not auto-detect, which is what makes manual
    /// promotion possible. Antler-classified ARMAs are excluded (the antler flow
    /// owns them). Empty when the NPC or environment can't be resolved.</summary>
    public IReadOnlyList<WigArmatureCandidate> GetWigArmatureCandidates(FormKey npcFormKey, ModSetting? modSetting)
    {
        var result = new List<WigArmatureCandidate>();
        if (modSetting == null) return result;
        var linkCache = _env.LinkCache;
        if (linkCache == null) return result;
        var context = BuildContext(npcFormKey, modSetting);
        var npc = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        if (npc == null || npc.WornArmor.IsNull) return result;
        var wnam = ResolveRecord<IArmorGetter>(npc.WornArmor, linkCache, context);
        if (wnam?.Armature == null) return result;

        FormKey? npcRaceKey = npc.Race.IsNull ? null : npc.Race.FormKey;
        FormKey? armorRaceKey = ResolveArmorRaceKey(npc, linkCache, context);
        var sex = Auxilliary.IsFemale(npc) ? NpcGender.Female : NpcGender.Male;
        var seen = new HashSet<FormKey>();
        foreach (var armaLink in wnam.Armature)
        {
            if (armaLink == null || armaLink.IsNull || !seen.Add(armaLink.FormKey)) continue;
            if (modSetting.DetectedAntlerArmatures.Contains(armaLink.FormKey)) continue;
            var arma = ResolveRecord<IArmorAddonGetter>(armaLink, linkCache, context);
            if (arma?.BodyTemplate == null || string.IsNullOrEmpty(arma.EditorID)) continue;
            if ((arma.BodyTemplate.FirstPersonFlags & WigDetector.HairSlots) == 0) continue;
            if (!IsArmatureForRace(arma, npcRaceKey, armorRaceKey)) continue;

            bool isAuto = modSetting.DetectedWigArmatures.Contains(armaLink.FormKey);
            bool isEffective = _settings.IsWigArmature(modSetting, armaLink.FormKey, arma.EditorID, npcFormKey);

            // Highlight keys: the resolved hair NIF's render shape names. Loose
            // files only (a BSA-resident mesh just yields no glow).
            IReadOnlyList<string> shapeNames = Array.Empty<string>();
            string? meshPath = GetWorldModelPath(arma, sex);
            if (meshPath != null)
            {
                string? absolute = RebaseToAbsoluteIfPresent(meshPath, context);
                if (absolute != null && File.Exists(absolute))
                {
                    try { shapeNames = NifHandler.GetRenderShapeNames(absolute); }
                    catch { /* unreadable NIF — row stays toggleable without a glow */ }
                }
            }

            result.Add(new WigArmatureCandidate(armaLink.FormKey, arma.EditorID, arma.EditorID,
                isAuto, isEffective, shapeNames));
        }
        return result;
    }

    /// <summary>
    /// Resolves the NPC's worn attire ("Include Default Outfit") and/or head
    /// gear ("Include headgear") into neutral <see cref="MeshOverride"/>s for
    /// <see cref="VM_CharacterViewer.ApplyMeshOverrides"/>. Walks
    /// <c>NPC.DefaultOutfit → OTFT.Items</c> (ARMO directly; LeveledItem per its
    /// Use All flag — every entry for use-all gear lists, else deterministically
    /// the first valid armor — and logged), plus the worn/skin
    /// armor for head-slot pieces, and emits one override per applicable
    /// ArmorAddon (filtered by NPC race). Body pieces are <c>Kind=Armor</c> (hide
    /// exactly the slots they fill, so clothing hides the nude body); head pieces
    /// are <c>Kind=Headgear</c> with the hair slot added to <c>HidesSlots</c>.
    /// Non-apparel outfit items (weapons / ammo / quest items) are skipped.
    /// Returns an empty list when both flags are off, the NPC can't be resolved,
    /// or the environment isn't ready.
    /// </summary>
    public IReadOnlyList<MeshOverride> ResolveAttireMeshOverrides(
        FormKey npcFormKey, ModSetting? modSetting, bool includeDefaultOutfit, bool includeHeadgear)
        => ResolveAttireMeshOverrides(npcFormKey, modSetting, includeDefaultOutfit, includeHeadgear,
            targetNpcFormKey: null, out _);

    /// <summary>
    /// Overload exposing the effective-outfit resolution alongside the mesh
    /// overrides. <paramref name="targetNpcFormKey"/> is the NPC being patched
    /// in the user's load order (differs from <paramref name="npcFormKey"/>
    /// for guest appearances; runtime distributors filter on the target) —
    /// null means the rendered NPC is its own target.
    /// <para><paramref name="alwaysRenderOutfitWigsAndAntlers"/> is set by the MUGSHOT
    /// generator only: it draws a detected Default-Outfit wig OR antler even under
    /// handling mode None, where the patch would not forward it. Both are part of
    /// the face a mod gives an NPC, and the outfit walk that would otherwise draw
    /// them promotes their head-slot armature to headgear — so with the attire
    /// toggles off they disappeared entirely. They are depicted regardless of both
    /// toggles; the tile marks the output discrepancy on its has-wig badge. The
    /// live 3D preview leaves this false and stays output-faithful.</para>
    /// </summary>
    public IReadOnlyList<MeshOverride> ResolveAttireMeshOverrides(
        FormKey npcFormKey, ModSetting? modSetting, bool includeDefaultOutfit, bool includeHeadgear,
        FormKey? targetNpcFormKey, out OutfitDisplayResult outfitDisplay,
        bool alwaysRenderOutfitWigsAndAntlers = false)
    {
        outfitDisplay = OutfitDisplayResult.NoOutfit;
        // Wig/antler handling: the mugshot depicts the POST-PATCH NPC, so a
        // ForwardToSkin mode renders the forwarded piece even with the outfit
        // toggle off — it is part of the skin after patching. ConvertToHeadParts
        // is depicted the same way: post-patch the wig is baked into the FaceGen
        // (the renderer never loads HDPT Models and the baked file doesn't exist
        // at preview time), so the wig ARMA mesh stands in for it regardless of
        // the outfit toggle.
        var wigMode = _settings.GetEffectiveRenderWigMode(modSetting);
        var antlerMode = _settings.GetEffectiveRenderAntlerMode(modSetting);
        bool anySkinForward = wigMode == WigHandlingMode.ForwardToSkin ||
                              wigMode == WigHandlingMode.ConvertToHeadParts ||
                              antlerMode == AntlerHandlingMode.ForwardToSkin;
        // A mode-None outfit wig or antler the mugshot depicts anyway
        // (PieceForward.Depiction) renders with both toggles off, exactly like a
        // skin-forwarded one, so it has to defeat this bail too.
        bool anyDepicted = alwaysRenderOutfitWigsAndAntlers &&
                           ((wigMode == WigHandlingMode.None && _settings.ModHasWigs(modSetting)) ||
                            (antlerMode == AntlerHandlingMode.None && _settings.ModHasAntlers(modSetting)));
        if (!includeDefaultOutfit && !includeHeadgear && !anySkinForward && !anyDepicted)
            return Array.Empty<MeshOverride>();
        var linkCache = _env.LinkCache;
        if (linkCache == null) return Array.Empty<MeshOverride>();

        // Effective outfit: patch-mode plugin level + runtime distributors
        // (SkyPatcher / SPID). Replaces the old direct read of the donor
        // record's DefaultOutfit.
        outfitDisplay = _outfitDisplayResolver.ResolveForDisplay(
            targetNpcFormKey ?? npcFormKey, npcFormKey, modSetting, includeDefaultOutfit);

        // The effective OUTFIT above stays keyed to this NPC — the Traits flag doesn't
        // inherit inventory, so a templated NPC wears its own. Everything the walk below
        // reads off the NPC RECORD is traits-inherited (race + sex for the armature
        // filter, and the WornArmor skin the head-slot scan uses), so it has to come from
        // the appearance source, matching the body and face already rendered from it.
        // The wig/antler plan follows that record too: its forwarding targets are the skin
        // and the baked FaceGen, both of which a templated NPC inherits along with the
        // face. (The exception is a ForwardToOutfit piece off the TEMPLATE's own outfit —
        // that lands in an outfit this NPC doesn't wear. Narrow enough to accept rather
        // than split the record in two.)
        var appearanceKey = ResolveAppearanceNpcKey(npcFormKey, modSetting);
        return ResolveAttireMeshOverrides(appearanceKey, linkCache,
            BuildContext(appearanceKey, modSetting), includeDefaultOutfit, includeHeadgear, outfitDisplay,
            wigMode, antlerMode, modSetting, alwaysRenderOutfitWigsAndAntlers);
    }

    private IReadOnlyList<MeshOverride> ResolveAttireMeshOverrides(
        FormKey npcFormKey, ILinkCache linkCache, NpcResolutionContext? context,
        bool includeDefaultOutfit, bool includeHeadgear, OutfitDisplayResult outfitDisplay,
        WigHandlingMode wigMode = WigHandlingMode.None,
        AntlerHandlingMode antlerMode = AntlerHandlingMode.None, ModSetting? modSetting = null,
        bool alwaysRenderOutfitWigsAndAntlers = false)
    {
        var result = new List<MeshOverride>();
        // Outfit is the dominant toggle — headgear is part of the outfit and never
        // renders on its own, so with the outfit off nothing attire-related is
        // emitted regardless of the headgear flag. (The UI also hides the headgear
        // toggle while the outfit is off.) Exception: an active ForwardToSkin wig
        // or antler mode still emits the forwarded pieces (post-patch they are skin).
        includeHeadgear = includeHeadgear && includeDefaultOutfit;
        // alwaysRenderOutfitWigsAndAntlers (mugshot only) makes a mode-None wig or
        // antler mod relevant to the plan too — the pass below emits those as
        // PieceForward.Depiction.
        bool maybeWigsOrAntlers = (wigMode != WigHandlingMode.None ||
                                   antlerMode != AntlerHandlingMode.None ||
                                   (alwaysRenderOutfitWigsAndAntlers &&
                                    (_settings.ModHasWigs(modSetting) || _settings.ModHasAntlers(modSetting))))
                                  && modSetting != null;
        bool anySkinForward = wigMode == WigHandlingMode.ForwardToSkin ||
                              wigMode == WigHandlingMode.ConvertToHeadParts ||
                              antlerMode == AntlerHandlingMode.ForwardToSkin ||
                              (alwaysRenderOutfitWigsAndAntlers &&
                               (wigMode == WigHandlingMode.None || antlerMode == AntlerHandlingMode.None));
        if (!includeDefaultOutfit && !includeHeadgear && !(maybeWigsOrAntlers && anySkinForward))
        {
            return result;
        }

        var npcGetter = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        if (npcGetter == null)
        {
            _logger?.LogError("CharacterViewer: ResolveAttireMeshOverrides could not resolve NPC " + npcFormKey);
            return result;
        }

        var sex = Auxilliary.IsFemale(npcGetter) ? NpcGender.Female : NpcGender.Male;
        FormKey? npcRaceKey = (npcGetter.Race != null && !npcGetter.Race.IsNull) ? npcGetter.Race.FormKey : null;
        FormKey? armorRaceKey = ResolveArmorRaceKey(npcGetter, linkCache, context);
        string npcName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcFormKey.ToString();

        // Dedup by override Key (slot(s) + ARMA FormKey) so a piece reachable via
        // both the outfit and the worn armor is only emitted once.
        var seenOverrideKeys = new HashSet<string>();

        // Wig/antler pass — runs BEFORE the outfit walk so the forwarded pieces
        // are emitted with the headgear gate bypassed (a forwarded wig is the
        // character's hair, not removable headgear; the outfit walk then dedups
        // the same ARMAs by key). Skin pieces render regardless of the outfit
        // toggle; outfit pieces only with the outfit on; Removed pieces never
        // (and are suppressed from the outfit walk) — mirroring what the patched
        // NPC wears in game.
        var wigPlan = maybeWigsOrAntlers
            ? BuildWigRenderPlan(npcGetter, modSetting!, wigMode, antlerMode, linkCache, context,
                alwaysRenderOutfitWigsAndAntlers)
            : null;
        if (wigPlan != null)
        {
            int emitted = 0;
            foreach (var (wigArmor, isAntler, forward) in wigPlan.Pieces)
            {
                bool render = forward == PieceForward.Skin ||
                              forward == PieceForward.Depiction ||
                              (forward == PieceForward.Outfit && includeDefaultOutfit);
                if (!render) continue;
                // Depiction pieces are NOT forwarded — labelling them WigForward in the
                // render log would send anyone reading it looking for a patch action
                // that never happened.
                string sourceLabel = forward == PieceForward.Depiction
                    ? "WigDepict(mode None):" + wigArmor.FormKey
                    : "WigForward(" + forward + "):" + wigArmor.FormKey;
                AppendArmorMeshOverrides(wigArmor, sourceLabel,
                    sex, npcRaceKey, armorRaceKey, linkCache, context,
                    includeBody: false, includeHeadgear: false,
                    hairCountsAsHeadgear: true, result, seenOverrideKeys,
                    isAntler ? WigPieceClass.Antler : WigPieceClass.Wig,
                    // The wig NIF itself is mod-scoped, but its TEXTURES
                    // routinely point into a third mod's archive (a wig reusing
                    // the original hair mod's paths) — the same shape as the
                    // outfit fallback. Also keeps these pieces in the
                    // unreachable-sweep probe set. ForceLegacy = harness A/B only.
                    allowLoadOrderFallback: !RenderResolutionMode.ForceLegacy);
                emitted++;
            }

            if (emitted > 0)
            {
                LogVerbose("CharacterViewer: wig/antler handling emitted " + emitted +
                           " forwarded piece(s) for " + npcName);
            }
        }

        if (!includeDefaultOutfit && !includeHeadgear)
        {
            // ForwardToSkin with the outfit off: only the wig pieces are shown.
            return result;
        }

        // Effective outfit → apparel armors. Drives both features: a head-slot
        // piece becomes headgear, everything else body attire. The outfit
        // FormKey comes from OutfitDisplayResolver (patch-mode plugin level +
        // SkyPatcher/SPID runtime layers); donor-sourced outfits resolve
        // through the mod's plugins, winner/runtime outfits through the plain
        // load-order winner — matching where the record actually comes from
        // in game.
        if (outfitDisplay.OutfitFormKey is { } effectiveOutfitKey)
        {
            var outfitContext = outfitDisplay.UseModScopedResolution ? context : null;
            // Asset resolution follows the same split as the record resolution above.
            // Mod-scoped = the appearance mod's own donor outfit, whose assets the
            // render's scope chain already covers. NOT mod-scoped = the load-order
            // winner, or a SkyPatcher / SPID distribution: the record comes from the
            // link cache (which spans the load order) and its armors can belong to any
            // mod at all — one that contributes no scope, so its NIFs and textures were
            // unreachable and the outfit rendered as nothing. Let those fall back to the
            // load-order-ranked archive lookup, which reproduces what the game resolves.
            bool outfitAssetsOutsideScope = !outfitDisplay.UseModScopedResolution;
            // Collects the plugins that define the outfit's armors / addons / texture
            // sets, so their BSAs can be indexed before the render. Only allocated when
            // the fallback is live: the broadcast lookup is the sole consumer, and a
            // donor outfit's assets are already covered by the scope chain.
            var outfitPluginSink = outfitAssetsOutsideScope ? new HashSet<ModKey>() : null;
            var outfit = ResolveRecord<IOutfitGetter>(effectiveOutfitKey.ToLink<IOutfitGetter>(), linkCache, outfitContext);
            if (outfitDisplay.Source != OutfitDisplaySource.AppearanceMod)
            {
                LogVerbose("CharacterViewer: effective outfit " + effectiveOutfitKey + " via "
                    + outfitDisplay.Source + (outfitDisplay.SourceDetail != null ? " (" + outfitDisplay.SourceDetail + ")" : ""));
            }
            if (outfit?.Items != null)
            {
                // Antler Remove (source 1): the patcher strips the antler from a
                // FORWARDED outfit, so suppress it from the depicted attire — but
                // only when the depicted outfit IS the forwarded donor outfit
                // (Source == AppearanceMod). A load-order/runtime-owned outfit is
                // not forwarded, so Remove can't reach it and the game still shows
                // the antler; leave it visible there.
                bool suppressRemovedAntlers = wigPlan != null &&
                    wigPlan.SuppressedOutfitItemKeys.Count > 0 &&
                    outfitDisplay.Source == OutfitDisplaySource.AppearanceMod;

                var outfitArmors = new List<(IArmorGetter armor, string source)>();
                var seenArmorKeys = new HashSet<FormKey>();
                foreach (var itemLink in outfit.Items)
                {
                    if (itemLink == null || itemLink.IsNull) continue;
                    if (suppressRemovedAntlers && wigPlan!.SuppressedOutfitItemKeys.Contains(itemLink.FormKey))
                        continue;
                    CollectOutfitItemArmors(itemLink.FormKey, linkCache, outfitContext,
                        "Outfit:" + effectiveOutfitKey, outfitArmors, seenArmorKeys, depth: 0,
                        pluginSink: outfitPluginSink);
                }
                foreach (var (armor, source) in outfitArmors)
                {
                    AppendArmorMeshOverrides(armor, source, sex, npcRaceKey, armorRaceKey, linkCache, outfitContext,
                        includeBody: includeDefaultOutfit, includeHeadgear: includeHeadgear,
                        hairCountsAsHeadgear: true, result, seenOverrideKeys,
                        allowLoadOrderFallback: outfitAssetsOutsideScope,
                        pluginSink: outfitPluginSink);
                }

                // Index the donor plugins' Data-folder archives now, while we still know
                // which plugins the depicted attire actually came from. Must happen here,
                // BEFORE the render consumes these overrides: the renderer discovers a
                // NIF's textures lazily and caches NotFound per scope signature, so an
                // archive admitted after the first lookup would never be consulted.
                WidenArchiveIndexForOutfitDonors(outfitPluginSink, npcName);
            }
            else
            {
                LogVerbose("CharacterViewer: ResolveAttireMeshOverrides could not resolve effective outfit "
                    + effectiveOutfitKey + " for " + npcName);
            }
        }

        // Headgear can also be worn via the skin/worn armor (rare, but the design
        // calls for "worn/outfit"). Scan it for HEAD pieces only — body/hands/
        // feet/hair ARMAs there are already rendered as base meshes, so we must
        // not re-add them (includeBody:false). hairCountsAsHeadgear:false keeps a
        // worn slot-31 hair "wig" out of the head class for the same reason: it's
        // the NPC's base hair, not removable headgear.
        if (includeHeadgear)
        {
            var (wornArmor, wornSource) = ResolveWornArmor(npcGetter, linkCache, context, npcName);
            if (wornArmor != null)
            {
                // Antler Remove (source 2): a WornArmor-baked antler ArmorAddon
                // (slot 42/circlet) would otherwise render here — the patch strips
                // it from the WNAM duplicate, so suppress it in the preview too.
                var suppressAntlerArmas = (antlerMode == AntlerHandlingMode.Remove && modSetting != null)
                    ? modSetting.DetectedAntlerArmatures
                    : null;
                AppendArmorMeshOverrides(wornArmor, wornSource, sex, npcRaceKey, armorRaceKey, linkCache, context,
                    includeBody: false, includeHeadgear: true,
                    hairCountsAsHeadgear: false, result, seenOverrideKeys,
                    suppressArmaKeys: suppressAntlerArmas,
                    // Worn-armor headgear assets can live outside the mod's
                    // scope just like outfit armors; let them reach the
                    // broadcast tier and join the unreachable-sweep probe.
                    // ForceLegacy = harness A/B only.
                    allowLoadOrderFallback: !RenderResolutionMode.ForceLegacy);
            }
        }

        // Last-resort widening, AFTER every override is emitted but BEFORE the
        // render consumes them (the same NotFound-cache deadline the donor
        // widening above runs against): if a fallback-eligible attire asset is
        // still unreachable, index the whole enabled load order's archives.
        WidenArchiveIndexToFullLoadOrderIfUnreachable(result, npcName);

        LogVerbose("CharacterViewer: ResolveAttireMeshOverrides for " + npcName + " (" + npcFormKey
            + ") outfit=" + includeDefaultOutfit + " headgear=" + includeHeadgear
            + " -> " + result.Count + " override(s)");
        return result;
    }

    /// <summary>Passthrough to
    /// <see cref="OutfitDisplayResolver.ComputeWigIdentitySuffix"/> for hosts
    /// that hold this resolver but not the outfit-display resolver (the
    /// offscreen generator's metadata stamp).
    /// <para>Deliberately NOT template-substituted (unlike the render paths above):
    /// the other callers of ComputeWigIdentitySuffix — the staleness checker's
    /// identity providers — go straight to the outfit resolver, and a stamp
    /// computed on a different NPC than the comparison would re-stale the tile on
    /// every pass. The cost is that changing a wig designation on a TEMPLATE
    /// doesn't auto-restale the mugshots of the NPCs inheriting from it; substitute
    /// here only together with every other call site.</para></summary>
    public string ComputeWigIdentitySuffix(FormKey sourceNpcFormKey, ModSetting? modSetting,
        bool includeDefaultOutfit)
        => _outfitDisplayResolver.ComputeWigIdentitySuffix(sourceNpcFormKey, modSetting, includeDefaultOutfit);

    /// <summary>Piece classification for the wig-forwarding pass: Wig pieces
    /// emit only their hair-slot ARMAs (matching WigForwarder's transfer);
    /// Antler pieces emit every ARMA (antler slots aren't standardized).</summary>
    private enum WigPieceClass
    {
        None,
        Wig,
        Antler
    }

    /// <summary>Where a forwarded piece ends up (per WigForwarder): the skin
    /// (shows regardless of outfit), the worn outfit (shows only with the outfit
    /// on), or Removed (antler Remove — never rendered, and suppressed from the
    /// outfit walk when the outfit is forwarded).
    /// <para><see cref="Depiction"/> is the odd one out: it makes NO claim about
    /// the output. It carries a wig or antler the patch will NOT forward (mode
    /// None) into the MUGSHOT anyway, because both are part of the face this mod
    /// gives an NPC and a mugshot exists to show that face. Renders like
    /// <see cref="Skin"/> (both attire toggles bypassed) and is emitted only when
    /// the caller passes <c>alwaysRenderOutfitWigsAndAntlers</c> — the live 3D preview does
    /// not, so it stays output-faithful and flags the discrepancy in a banner
    /// instead (see <see cref="WigNotPersistedApplies"/>).</para></summary>
    private enum PieceForward
    {
        Skin,
        Outfit,
        Removed,
        Depiction
    }

    private sealed class WigRenderPlan
    {
        public List<(IArmorGetter Armor, bool IsAntler, PieceForward Forward)> Pieces = new();

        /// <summary>Source-1 antler item FormKeys that antler Remove strips from a
        /// forwarded donor outfit — suppressed from the outfit walk (only when the
        /// depicted outfit is that forwarded donor outfit).</summary>
        public HashSet<FormKey> SuppressedOutfitItemKeys = new();
    }

    /// <summary>
    /// Mirrors <see cref="WigForwarder"/>'s per-NPC, per-class routing for the
    /// renderer: the applicable wigs/antlers are the DONOR outfit's direct items
    /// the analysis scan detected (resolved mod-scoped — they come from the
    /// appearance mod's plugins). Wigs follow the wig mode, antlers the antler
    /// mode; ForwardToSkin falls back to ForwardToOutfit when the donor record
    /// assigns no WNAM (shared, one WNAM). Antler Remove is not rendered and its
    /// item key is collected for outfit-walk suppression. Returns null when
    /// nothing applies. (Sources 2/3 — antlers baked into the WornArmor or FaceGen
    /// — are not suppressed in the preview yet; CV.R has no hide-by-name FaceGen
    /// hook, so that is deferred to the 3D-preview antler-designator work. The
    /// patch output is correct for all sources.)
    /// </summary>
    private WigRenderPlan? BuildWigRenderPlan(INpcGetter npcGetter, ModSetting modSetting,
        WigHandlingMode wigMode, AntlerHandlingMode antlerMode, ILinkCache linkCache,
        NpcResolutionContext? context, bool alwaysRenderOutfitWigsAndAntlers)
    {
        if (npcGetter.DefaultOutfit == null || npcGetter.DefaultOutfit.IsNull) return null;
        var donorOutfit = ResolveRecord<IOutfitGetter>(npcGetter.DefaultOutfit, linkCache, context);
        if (donorOutfit?.Items == null) return null;

        bool hasWnam = npcGetter.WornArmor != null && !npcGetter.WornArmor.IsNull;
        var plan = new WigRenderPlan();
        foreach (var item in donorOutfit.Items)
        {
            if (item == null || item.IsNull) continue;
            bool isAntler = modSetting.DetectedAntlerArmors.Contains(item.FormKey);
            bool isWig = !isAntler && modSetting.DetectedWigArmors.Contains(item.FormKey);
            if (!isAntler && !isWig) continue;

            if (isAntler)
            {
                switch (antlerMode)
                {
                    case AntlerHandlingMode.ForwardToSkin:
                        AddRenderPiece(plan, item.FormKey, true, hasWnam ? PieceForward.Skin : PieceForward.Outfit,
                            linkCache, context);
                        break;
                    case AntlerHandlingMode.ForwardToOutfit:
                        AddRenderPiece(plan, item.FormKey, true, PieceForward.Outfit, linkCache, context);
                        break;
                    case AntlerHandlingMode.Remove:
                        plan.SuppressedOutfitItemKeys.Add(item.FormKey);
                        break;
                    case AntlerHandlingMode.None when alwaysRenderOutfitWigsAndAntlers:
                        // Same reasoning as the mode-None wig below: antlers are part
                        // of the face this mod gives the NPC, and the outfit walk that
                        // would otherwise draw them promotes their head-slot armature
                        // to headgear — so with the attire toggles off (the mugshot
                        // default) they vanished entirely under "Leave As Is". Depict
                        // them; whether they reach the OUTPUT is a separate question.
                        AddRenderPiece(plan, item.FormKey, true, PieceForward.Depiction, linkCache, context);
                        break;
                    // None (preview): legacy passthrough — the outfit walk depicts it,
                    // gated by Include Headgear, keeping the preview output-faithful.
                }
            }
            else
            {
                switch (wigMode)
                {
                    case WigHandlingMode.ForwardToSkin:
                        AddRenderPiece(plan, item.FormKey, false, hasWnam ? PieceForward.Skin : PieceForward.Outfit,
                            linkCache, context);
                        break;
                    case WigHandlingMode.ForwardToOutfit:
                        AddRenderPiece(plan, item.FormKey, false, PieceForward.Outfit, linkCache, context);
                        break;
                    case WigHandlingMode.ConvertToHeadParts:
                        // Post-patch the wig is baked into the FaceGen (the NPC's
                        // hair head part), so it shows regardless of outfit — the
                        // ForwardToSkin visual, with no WNAM requirement (the bake
                        // doesn't need one; the converter's own per-NPC fallback
                        // isn't knowable here and ForwardToSkin looks identical).
                        AddRenderPiece(plan, item.FormKey, false, PieceForward.Skin, linkCache, context);
                        break;
                    case WigHandlingMode.None when alwaysRenderOutfitWigsAndAntlers:
                        // Legacy passthrough, but the MUGSHOT still depicts the wig:
                        // it is the NPC's hair, and the tile exists to show the face
                        // this mod supplies. Purely a depiction — whether the wig
                        // reaches the output is a separate question the tile answers
                        // with the crossed-out has-wig badge (VM_NpcsMenuMugshot) and
                        // the preview with its own banner.
                        AddRenderPiece(plan, item.FormKey, false, PieceForward.Depiction, linkCache, context);
                        break;
                    // None (preview): legacy passthrough — the outfit walk depicts it,
                    // gated by Include Headgear like the hood it is indistinguishable
                    // from at the record level.
                }
            }
        }

        if (plan.Pieces.Count == 0 && plan.SuppressedOutfitItemKeys.Count == 0) return null;
        return plan;
    }

    private void AddRenderPiece(WigRenderPlan plan, FormKey armorKey, bool isAntler, PieceForward forward,
        ILinkCache linkCache, NpcResolutionContext? context)
    {
        var armor = ResolveRecord<IArmorGetter>(armorKey.ToLink<IArmorGetter>(), linkCache, context);
        if (armor != null) plan.Pieces.Add((armor, isAntler, forward));
    }

    /// <summary>
    /// Resolves one outfit <c>Items</c> entry to apparel armor(s) and appends
    /// them to <paramref name="armors"/>. ARMO entries are taken directly.
    /// LeveledItem entries honor the list's Use All flag: a use-all list
    /// contributes armor from EVERY entry (the engine equips them all — vanilla
    /// soldier outfits are one use-all gear list with one per-slot sub-list,
    /// e.g. the Thalmor Elven Light set), while a plain list is a runtime
    /// random pick that the preview stands in for DETERMINISTICALLY with the
    /// first entry that yields any armor — never random per render. Either way
    /// the result is logged. Non-apparel items (weapons / ammo / quest items)
    /// and unresolvable links are skipped.
    /// </summary>
    /// <summary>
    /// Indexes the Data-folder BSAs of the plugins that supplied a load-order-owned
    /// outfit, so the renderer's Phase 3 broadcast can actually find their meshes and
    /// textures.
    ///
    /// <para>Without this the broadcast is starved: every writer of the BSA index takes a
    /// <c>ModSetting</c>, and a mod only becomes a ModSetting if it ships FaceGen — so an
    /// armor mod that is active in the load order but carries no faces had its archives
    /// invisible, and an outfit SkyPatcher/SPID distributed from it rendered as nothing.
    /// (Its LOOSE files resolved fine all along, because the vanilla scope probes the
    /// Data folder directly and MO2's VFS merges every enabled mod into it — which is why
    /// the failure looked intermittent and mod-specific for so long.)</para>
    ///
    /// <para>Masters are included one level deep: a plugin's records routinely reference
    /// assets that ship in the parent mod it masters (the "De-Standalone" shape). Base
    /// game and Creation Club keys are dropped because their archives are indexed at
    /// startup, and anything absent from the load order is dropped because the game would
    /// not load its archive either.</para>
    /// </summary>
    private void WidenArchiveIndexForOutfitDonors(HashSet<ModKey>? donorPlugins, string npcName)
    {
        if (donorPlugins == null || donorPlugins.Count == 0) return;

        var loadOrder = _env.LoadOrder;
        if (loadOrder == null) return;

        var keys = SelectDonorPluginsToIndex(
            donorPlugins,
            loadOrder.ListedOrder.Select(l =>
                (l.ModKey, (IReadOnlyList<ModKey>?)l.Mod?.ModHeader.MasterReferences.Select(m => m.Master).ToList())),
            _env.BaseGamePlugins,
            _env.CreationClubPlugins);
        if (keys.Count == 0) return;

        LogVerbose("CharacterViewer: widening BSA index for " + npcName + "'s load-order outfit donors: "
            + string.Join(", ", keys.Select(k => k.FileName.String)));
        _bsaHandler.EnsureDataFolderArchivesIndexed(keys, _env.SkyrimVersion.ToGameRelease());
    }

    /// <summary>
    /// Narrows the raw donor-plugin set to the keys actually worth indexing: those
    /// present in the load order, plus one level of their masters, minus base game and
    /// Creation Club (already indexed at startup).
    ///
    /// <para>Masters are pulled in because a plugin's records routinely point at assets
    /// shipped by the parent mod it masters — the "De-Standalone" shape. Only one level:
    /// deeper ancestry is almost always vanilla, which is filtered out anyway.</para>
    ///
    /// <para>Pure and static so the selection rule is testable without a game install or
    /// a resolved environment, matching
    /// <c>NpcChooserBsaProviderAdapter.SelectByLoadOrder</c>.</para>
    /// </summary>
    /// <param name="loadOrder">
    /// (plugin, its masters) in listed order. Masters may be null for a listing whose
    /// mod could not be loaded — the plugin itself still counts.
    /// </param>
    public static HashSet<ModKey> SelectDonorPluginsToIndex(
        IReadOnlyCollection<ModKey> donorPlugins,
        IEnumerable<(ModKey Key, IReadOnlyList<ModKey>? Masters)> loadOrder,
        IEnumerable<ModKey>? baseGamePlugins,
        IEnumerable<ModKey>? creationClubPlugins)
    {
        var keys = new HashSet<ModKey>();
        if (donorPlugins == null || donorPlugins.Count == 0 || loadOrder == null) return keys;

        var wanted = donorPlugins as ISet<ModKey> ?? new HashSet<ModKey>(donorPlugins);

        // One pass: confirm membership and harvest masters together, rather than a
        // lookup per key.
        foreach (var (key, masters) in loadOrder)
        {
            if (!wanted.Contains(key)) continue;
            keys.Add(key);
            if (masters == null) continue;
            // Masters of a loaded plugin are by definition in the load order, so no
            // second membership check is needed.
            foreach (var master in masters) keys.Add(master);
        }

        if (baseGamePlugins != null) keys.ExceptWith(baseGamePlugins);
        if (creationClubPlugins != null) keys.ExceptWith(creationClubPlugins);
        keys.Remove(ModKey.Null);
        return keys;
    }

    /// <summary>
    /// Last-resort archive widening: if any fallback-eligible attire asset in
    /// <paramref name="overrides"/> is reachable neither loose at the Data folder nor in
    /// any indexed archive, index the archives of EVERY enabled load-order plugin not yet
    /// in the index — the engine's own rule, which the record-scoped donor widening only
    /// approximates.
    ///
    /// <para><b>Why donor+master selection isn't enough.</b> BSA ownership is by plugin
    /// FILENAME (<see cref="PluginArchiveIndex"/>), and some mods ship their records in
    /// one plugin while their assets sit in an archive named after a second,
    /// resource-only plugin — frequently a renamed copy of the first, so it carries no
    /// master link back to it and defines only parallel duplicate records nothing
    /// references (TW3 Geralt Prologue Gear.esp with its meshes in TW3Resources.bsa,
    /// loaded by a byte-identical TW3Resources.esp). No record the outfit walk can reach
    /// ever names the carrier plugin, so <see cref="SelectDonorPluginsToIndex"/> cannot
    /// discover it. The game renders these fine because it loads every enabled plugin's
    /// archives globally; this fallback reproduces that rule when — and only when — the
    /// proportional pass demonstrably missed something.</para>
    ///
    /// <para><b>Probe fidelity.</b> The probe mirrors where the renderer will look for a
    /// fallback-eligible asset: loose under the Data folder (which under MO2's VFS spans
    /// every enabled mod's loose files) and the broadcast archive index
    /// (<see cref="BsaHandler.LocateAllInBsas"/>). A rebased (absolute) path is reachable
    /// by construction. The one thing it can't see — a loose file in a render-scope mod
    /// folder outside MO2 — only costs a harmless extra sweep, never a skipped one, and
    /// per-plugin memoization inside
    /// <see cref="BsaHandler.EnsureDataFolderArchivesIndexed"/> keeps even a persistent
    /// miss (a genuinely absent asset) from paying the sweep twice in a session.</para>
    /// </summary>
    /// <summary>Latched once the full enabled load order's archives are known
    /// to be indexed (a sweep found nothing left to add, or one completed the
    /// indexing). From then on no sweep can ever admit another archive — the
    /// load order is fixed for the session — so the per-render probe loop
    /// below is pure waste and is skipped. The broadcast adapter's own
    /// startup pre-warm typically satisfies this before the first render.</summary>
    private volatile bool _fullLoadOrderIndexSatisfied;

    private void WidenArchiveIndexToFullLoadOrderIfUnreachable(
        IReadOnlyList<MeshOverride> overrides, string npcName)
    {
        if (_fullLoadOrderIndexSatisfied) return;

        string dataFolder = _env.DataFolderPath;
        var unreachable = CollectUnreachableFallbackAssetPaths(
            overrides,
            p => !string.IsNullOrWhiteSpace(dataFolder) && File.Exists(Path.Combine(dataFolder, p)),
            p => _bsaHandler.LocateAllInBsas(p).Count > 0);
        if (unreachable.Count == 0) return;

        var loadOrder = _env.LoadOrder;
        if (loadOrder == null) return;

        var keys = SelectEnabledLoadOrderPluginsToIndex(
                loadOrder.ListedOrder.Select(l => (l.ModKey, l.Enabled)),
                _env.BaseGamePlugins,
                _env.CreationClubPlugins)
            // Keys the index has already seen (ModSetting-owned plugins, earlier
            // widenings) are dropped: their archives are already reachable, and
            // re-indexing them from the Data folder would open a duplicate reader
            // on the same physical BSA through its VFS path.
            .Where(k => !_bsaHandler.CacheContainsModKey(k))
            .ToList();

        LogVerbose("CharacterViewer: " + unreachable.Count + " attire asset(s) for " + npcName
            + " unreachable after donor-scoped widening (" + string.Join(", ", unreachable)
            + ") — widening archive index to the full enabled load order ("
            + keys.Count + " plugin(s) not yet indexed).");
        if (keys.Count == 0)
        {
            _fullLoadOrderIndexSatisfied = true;
            return;
        }

        _bsaHandler.EnsureDataFolderArchivesIndexed(keys, _env.SkyrimVersion.ToGameRelease());
        _fullLoadOrderIndexSatisfied = true;
    }

    /// <summary>
    /// The full-sweep counterpart of <see cref="SelectDonorPluginsToIndex"/>: every
    /// enabled load-order plugin, minus base game and Creation Club (indexed at startup).
    /// Enabled-only because the game only loads an ENABLED plugin's archives — a listed
    /// but disabled plugin's BSA is exactly as invisible in game as it is here.
    ///
    /// <para>Pure and static so the selection rule is testable without a game install or
    /// a resolved environment, matching <see cref="SelectDonorPluginsToIndex"/>.</para>
    /// </summary>
    public static HashSet<ModKey> SelectEnabledLoadOrderPluginsToIndex(
        IEnumerable<(ModKey Key, bool Enabled)>? loadOrder,
        IEnumerable<ModKey>? baseGamePlugins,
        IEnumerable<ModKey>? creationClubPlugins)
    {
        var keys = new HashSet<ModKey>();
        if (loadOrder == null) return keys;

        foreach (var (key, enabled) in loadOrder)
        {
            if (enabled) keys.Add(key);
        }

        if (baseGamePlugins != null) keys.ExceptWith(baseGamePlugins);
        if (creationClubPlugins != null) keys.ExceptWith(creationClubPlugins);
        keys.Remove(ModKey.Null);
        return keys;
    }

    /// <summary>
    /// The game-relative asset paths of every fallback-eligible override
    /// (<see cref="MeshOverride.AllowLoadOrderFallback"/>) that neither probe can reach.
    /// Only fallback-stamped overrides are probed: everything else resolves through the
    /// strict scope chain, whose mod folders these probes deliberately don't cover —
    /// including them would false-trigger the sweep on every render. Rooted paths were
    /// already rebased onto an on-disk mod folder and are reachable by construction.
    /// Paths are deduplicated case-insensitively so one shared missing texture reports
    /// (and logs) once.
    ///
    /// <para>Pure apart from the injected probes, so the trigger rule is testable
    /// without disk or archive I/O.</para>
    /// </summary>
    public static List<string> CollectUnreachableFallbackAssetPaths(
        IEnumerable<MeshOverride> overrides,
        Func<string, bool> looseExistsAtDataFolder,
        Func<string, bool> existsInAnyIndexedArchive)
    {
        var unreachable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var over in overrides)
        {
            if (over == null || !over.AllowLoadOrderFallback) continue;
            foreach (var path in EnumerateOverrideAssetPaths(over))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!seen.Add(path)) continue;
                if (Path.IsPathRooted(path)) continue;
                if (looseExistsAtDataFolder(path)) continue;
                if (existsInAnyIndexedArchive(path)) continue;
                unreachable.Add(path);
            }
        }
        return unreachable;
    }

    /// <summary>Every asset path an override hands the renderer: the mesh, the flat TXST
    /// channel, and the AlternateTextures channel. ShapeTextures is omitted — it's the
    /// legacy name-only channel this resolver never emits.</summary>
    private static IEnumerable<string> EnumerateOverrideAssetPaths(MeshOverride over)
    {
        if (!string.IsNullOrWhiteSpace(over.MeshPath)) yield return over.MeshPath;
        if (over.Textures != null)
        {
            foreach (var path in over.Textures.Values) yield return path;
        }
        if (over.AlternateTextures != null)
        {
            foreach (var spec in over.AlternateTextures)
            {
                if (spec?.Textures == null) continue;
                foreach (var path in spec.Textures.Values) yield return path;
            }
        }
    }

    private void CollectOutfitItemArmors(FormKey itemFormKey, ILinkCache linkCache, NpcResolutionContext? context,
        string source, List<(IArmorGetter armor, string source)> armors, HashSet<FormKey> seen, int depth,
        HashSet<ModKey>? pluginSink = null)
    {
        if (depth > 10) return; // guard against pathological leveled-list cycles

        var armor = ResolveRecord<IArmorGetter>(itemFormKey.ToLink<IArmorGetter>(), linkCache, context, pluginSink);
        if (armor != null)
        {
            if (seen.Add(itemFormKey)) armors.Add((armor, source));
            return;
        }

        var lvli = ResolveRecord<ILeveledItemGetter>(itemFormKey.ToLink<ILeveledItemGetter>(), linkCache, context);
        if (lvli != null)
        {
            var collected = new List<(IArmorGetter armor, string label)>();
            CollectArmorsFromLeveledItem(lvli, linkCache, context, depth, collected, pluginSink);
            if (collected.Count > 0)
            {
                bool useAll = lvli.Flags.HasFlag(LeveledItem.Flag.UseAll);
                LogVerbose("CharacterViewer: Outfit LeveledItem " + itemFormKey +
                    (useAll
                        ? " -> use-all list: collected " + collected.Count + " armor(s): "
                          + string.Join(", ", collected.Select(c => c.label))
                        : " -> chose armor " + collected[0].label
                          + " (deterministic: first valid armor in entry order)"));
                foreach (var (collectedArmor, _) in collected)
                {
                    if (seen.Add(collectedArmor.FormKey))
                        armors.Add((collectedArmor, source + " -> LVLI:" + itemFormKey));
                }
            }
            else
            {
                LogVerbose("CharacterViewer: Outfit LeveledItem " + itemFormKey + " yielded no apparel armor (skipped)");
            }
            return;
        }

        LogVerbose("CharacterViewer: Outfit item " + itemFormKey + " is not Armor/LeveledItem (non-apparel, skipped)");
    }

    /// <summary>Collects apparel armors reachable from <paramref name="lvli"/>,
    /// honoring the Use All flag at every level: a use-all list visits EVERY
    /// entry (the engine equips them all); a plain list stops at the first
    /// entry (in declared order) that yields at least one armor — the
    /// deterministic stand-in for the engine's runtime roll. Entries that
    /// resolve to neither Armor nor LeveledItem are skipped and never count
    /// as a plain list's "first yielding entry".</summary>
    private void CollectArmorsFromLeveledItem(
        ILeveledItemGetter lvli, ILinkCache linkCache, NpcResolutionContext? context, int depth,
        List<(IArmorGetter armor, string label)> collected, HashSet<ModKey>? pluginSink = null)
    {
        if (depth > 10 || lvli.Entries == null) return;
        bool useAll = lvli.Flags.HasFlag(LeveledItem.Flag.UseAll);

        foreach (var entry in lvli.Entries)
        {
            var refLink = entry?.Data?.Reference;
            if (refLink == null || refLink.IsNull) continue;
            var fk = refLink.FormKey;

            int countBefore = collected.Count;

            var armor = ResolveRecord<IArmorGetter>(fk.ToLink<IArmorGetter>(), linkCache, context, pluginSink);
            if (armor != null)
            {
                collected.Add((armor, armor.EditorID ?? fk.ToString()));
            }
            else
            {
                var nested = ResolveRecord<ILeveledItemGetter>(fk.ToLink<ILeveledItemGetter>(), linkCache, context);
                if (nested != null)
                {
                    CollectArmorsFromLeveledItem(nested, linkCache, context, depth + 1, collected, pluginSink);
                }
            }

            if (!useAll && collected.Count > countBefore) return; // plain list: first yielding entry wins
        }
    }

    /// <summary>Emits one <see cref="MeshOverride"/> per applicable ArmorAddon of
    /// <paramref name="armor"/> (filtered by NPC race), classifying each by slot
    /// into body attire (<see cref="MeshOverrideKind.Armor"/>) or headgear
    /// (<see cref="MeshOverrideKind.Headgear"/>). <paramref name="includeBody"/> /
    /// <paramref name="includeHeadgear"/> gate which classes are emitted. The Key
    /// combines slot(s) + ARMA FormKey so it's stable and unique per piece.</summary>
    /// <param name="allowLoadOrderFallback">Stamped onto every emitted override (see
    /// <see cref="MeshOverride.AllowLoadOrderFallback"/>). Set it when these armors come from
    /// somewhere other than the appearance mod, so their assets may live in a mod that
    /// contributes no render scope.</param>
    /// <param name="pluginSink">Collects the ARMA / TXST donor plugins so their archives can
    /// be indexed before the render. Passed only alongside
    /// <paramref name="allowLoadOrderFallback"/>; null everywhere else.</param>
    private void AppendArmorMeshOverrides(IArmorGetter armor, string source, NpcGender sex, FormKey? npcRaceKey,
        FormKey? armorRaceKey,
        ILinkCache linkCache, NpcResolutionContext? context, bool includeBody, bool includeHeadgear,
        bool hairCountsAsHeadgear, List<MeshOverride> result, HashSet<string> seenKeys,
        WigPieceClass wigPiece = WigPieceClass.None, IReadOnlySet<FormKey>? suppressArmaKeys = null,
        bool allowLoadOrderFallback = false, HashSet<ModKey>? pluginSink = null)
    {
        if (armor.Armature == null) return;
        foreach (var armaLink in armor.Armature)
        {
            if (armaLink == null || armaLink.IsNull) continue;
            // Host-designated ArmorAddon suppression (antler Remove, source 2).
            if (suppressArmaKeys != null && suppressArmaKeys.Contains(armaLink.FormKey)) continue;
            var arma = ResolveRecord<IArmorAddonGetter>(armaLink, linkCache, context, pluginSink);
            if (arma?.BodyTemplate == null) continue;
            if (!IsArmatureForRace(arma, npcRaceKey, armorRaceKey)) continue;

            int slots = (int)arma.BodyTemplate.FirstPersonFlags;
            if (slots == 0) continue;

            // Wig-forwarding pass (see WigPieceClass): forwarded pieces bypass
            // the includeBody/includeHeadgear gates below — a forwarded wig is
            // the character's hair, not removable headgear. The slot-based
            // classification itself is untouched, so a hair-slot wig lands as
            // Kind=Headgear (draw priority hides the base/FaceGen hair, exactly
            // how the piece behaves worn in game) and a slot-42 antler follows
            // the open-circlet rule and leaves the hair visible.
            bool isForwardedWigPiece =
                wigPiece == WigPieceClass.Antler ||
                (wigPiece == WigPieceClass.Wig && (slots & (int)WigDetector.HairSlots) != 0);

            // A piece is headgear if it occupies the Head (30) or Circlet (42)
            // slot, or — when hairCountsAsHeadgear is set — the Hair (31) slot,
            // which is how hoods get flagged (e.g. Wylandriah's MonkVariant hood
            // is slot 31|41|43, none of which are in HeadSlotMask, so it would
            // otherwise read as body attire). Only the outfit path sets the flag;
            // the worn-armor scan leaves it false so a worn hair "wig" (the
            // bald-FaceGen + slot-31-ARMO pattern) stays the NPC's base hair
            // rather than being re-added as toggleable headgear.
            //
            // Only the Hair slot (31) promotes a hood for now. LongHair (41) and
            // Ears (43) are plausible head-covering slots too, but left out: it's
            // unclear what outfit would use LongHair alone, and Ears is commonly
            // earrings — and the headgear toggle exists to drop hats/hoods, not
            // jewelry, so folding in Ears would over-hide.
            int headMask = HeadSlotMask | (hairCountsAsHeadgear ? HairSlotBit : 0);
            bool isHead = (slots & headMask) != 0;
            if (!isForwardedWigPiece)
            {
                if (isHead && !includeHeadgear) continue;
                if (!isHead && !includeBody) continue;
            }

            string? meshPath = GetWorldModelPath(arma, sex);
            if (meshPath == null) continue;

            string key = (isHead ? "Headgear:" : "Outfit:") + armaLink.FormKey + ":" + slots;
            if (!seenKeys.Add(key)) continue;

            // Texture sets — two independent channels can apply, both rebased to
            // the mod's loose overrides like the base meshes:
            //  • ArmorAddon.SkinTexture (NAM0/NAM1) — a mesh-wide skin TXST used by
            //    skin-type addons; carried in the flat MeshOverride.Textures.
            //  • WorldModel.AlternateTextures (MODS) — per-object TXST overrides
            //    that retexture individual shapes of the world-model NIF (how one
            //    shared mesh serves multiple visual variants); carried as
            //    MeshOverride.AlternateTextures entries preserving BOTH identity
            //    fields the record stores (3D Name + 3D Index). The renderer
            //    matches by name first and falls back to the index so a
            //    BodySlide-rebuilt mesh whose shapes were renamed still gets its
            //    variant applied the way the engine applies it. Absent both
            //    channels, Textures/AlternateTextures stay null and the NIF's own
            //    embedded BSShaderTextureSet renders (correct for plain armor).
            meshPath = RebaseToAbsoluteIfPresent(meshPath, context) ?? meshPath;

            var txst = ResolveTxstTextures(arma, sex, linkCache, context, armaLink.FormKey, pluginSink);
            Dictionary<int, string>? textures = null;
            if (txst.Count > 0)
            {
                foreach (var slot in txst.Keys.ToList())
                {
                    var rebased = RebaseToAbsoluteIfPresent(txst[slot], context);
                    if (rebased != null) txst[slot] = rebased;
                }
                textures = txst;
            }

            var altTxst = ResolveAlternateTextures(arma, sex, linkCache, context, armaLink.FormKey, pluginSink);
            IReadOnlyList<AlternateTextureSpec>? alternateTextures = null;
            if (altTxst.Count > 0)
            {
                var specs = new List<AlternateTextureSpec>(altTxst.Count);
                foreach (var (shapeName, shapeIndex, slotMap) in altTxst)
                {
                    foreach (var slot in slotMap.Keys.ToList())
                    {
                        var rebased = RebaseToAbsoluteIfPresent(slotMap[slot], context);
                        if (rebased != null) slotMap[slot] = rebased;
                    }
                    specs.Add(new AlternateTextureSpec
                    {
                        ShapeName = shapeName,
                        ShapeIndex = shapeIndex,
                        Textures = slotMap,
                    });
                }
                alternateTextures = specs;
                LogVerbose("CharacterViewer: ARMA " + armaLink.FormKey + " AlternateTextures resolved ("
                    + specs.Count + "): "
                    + string.Join(", ", specs.Select(s => "[" + s.ShapeIndex + "]'" + s.ShapeName + "'")));
                // Slot-path dump (post-rebase: an absolute path means the TXST
                // was redirected into the selected mod's folders). Logged here —
                // inside the resolve phase's guaranteed capture window — so the
                // handed-over TXST contents are documented even if the renderer's
                // own apply trace is lost (it runs later, on a render tick).
                if (_logGate != null && _logGate.Verbose)
                    foreach (var spec in specs)
                        LogVerbose("CharacterViewer:   altTex [3D index " + spec.ShapeIndex
                            + "] name='" + spec.ShapeName + "' slots {"
                            + string.Join(", ", spec.Textures.OrderBy(kv => kv.Key)
                                .Select(kv => kv.Key + "=" + kv.Value)) + "}");
            }

            var kind = isHead ? MeshOverrideKind.Headgear : MeshOverrideKind.Armor;
            // What a piece hides is driven by the biped slots it actually
            // occupies — matching the engine. Body attire leaves HidesSlots null
            // (defaults to its own BipedSlots), so it hides exactly the slots it
            // covers; a hood occupying slot 31 thereby hides the hair. Headgear
            // is the same with one twist: it never hides the Head slot (30) bit,
            // or the FaceGen face/eyes/brows (tagged slot 30) would be culled —
            // but a helmet/hood on the head (slot 30 or 31) still hides the hair
            // (slot 31). An open circlet (slot 42 only) occupies neither, so it
            // leaves the hair visible, as in game.
            int? hides = null;
            if (isHead)
            {
                int headHides = slots & ~HeadFaceSlotBit;
                if ((slots & (HeadFaceSlotBit | HairSlotBit)) != 0)
                    headHides |= HairSlotBit;
                hides = headHides;
            }

            result.Add(new MeshOverride
            {
                Key = key,
                MeshPath = meshPath,
                BipedSlots = slots,
                HidesSlots = hides,
                Kind = kind,
                Textures = textures,
                AlternateTextures = alternateTextures,
                AllowLoadOrderFallback = allowLoadOrderFallback,
                // Whether the engine morphs this piece between its _0/_1 pair.
                // Pieces with the slider off ship a single weight file, so a
                // "missing" sibling is not a defect for them (Mod Issues scan).
                HasWeightVariants = GetWeightSliderEnabled(arma, sex),
            });
            LogVerbose("CharacterViewer: attire override '" + key + "' mesh=" + meshPath
                + " slots=" + slots + " kind=" + kind + " src=" + source
                + (allowLoadOrderFallback ? " (load-order asset fallback enabled)" : ""));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Asset-resolution diagnostics
    // ─────────────────────────────────────────────────────────────────────

    private void TraceAssetResolution(FormKey npcFormKey, string npcName, NpcResolutionContext? context,
        string? body, string? hands, string? feet, string head, string? skeleton, string? faceTint,
        string? hair, string? tail,
        Dictionary<string, Dictionary<int, string>> txstTextures)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[AssetResolution] NPC=").Append(npcName).Append(" (").Append(npcFormKey).Append(')');
        if (context != null && context.PreferredModKeys.Count > 0)
        {
            sb.Append(" mod-scoped (").Append(context.PreferredModKeys.Count).Append(" plugin(s), ")
              .Append(context.PreferredFolderPaths.Count).Append(" folder(s))");
        }
        WriteTrace(sb.ToString());

        bool anyMissing = false;
        anyMissing |= LogAssetCheck("Body", body, context);
        anyMissing |= LogAssetCheck("Hands", hands, context);
        anyMissing |= LogAssetCheck("Feet", feet, context);
        anyMissing |= LogAssetCheck("Head", head, context);
        anyMissing |= LogAssetCheck("Hair", hair, context);
        anyMissing |= LogAssetCheck("Tail", tail, context);
        anyMissing |= LogAssetCheck("Skeleton", skeleton, context);
        anyMissing |= LogAssetCheck("FaceTint", faceTint, context);
        foreach (var (bodyPart, slotMap) in txstTextures)
        {
            foreach (var (slot, path) in slotMap)
            {
                anyMissing |= LogAssetCheck($"{bodyPart} TXST[{slot}]", path, context);
            }
        }
        if (!anyMissing) WriteTrace("[AssetResolution]   all assets located");
    }

    /// <summary>Returns true if the asset wasn't found anywhere (so callers
    /// can roll up "any missing" for the summary line). Mirrors the
    /// renderer's strict two-phase scope iteration (Phase 1 loose, Phase 2
    /// scoped BSA — both walked highest-priority-first) so the diagnostic
    /// reports what the renderer will actually pick, not what a broadcast
    /// BSA lookup would happen to find first.</summary>
    private bool LogAssetCheck(string label, string? path, NpcResolutionContext? context)
    {
        if (string.IsNullOrEmpty(path)) return false; // not requested

        // Absolute path: already rebased to a mod folder during resolution.
        // The renderer's GameAssetResolver passes these through; we just need
        // to confirm the file is on disk where we said it is.
        if (Path.IsPathRooted(path))
        {
            if (File.Exists(path))
            {
                WriteTrace($"[AssetResolution]   [{label}] OK (mod folder): {path}");
                return false;
            }
            WriteTrace($"[AssetResolution]   [{label}] !! MISSING (rebased to mod folder but file gone): {path}");
            return true;
        }

        var scopes = BuildDiagnosticScopes(context);
        // Mirror the renderer's hard-coded Phase 1 skip at the vanilla scope
        // (i==0) for FaceGen paths — FaceGeom NIFs and FaceTint DDS are NPC-
        // keyed (FormID-named under <plugin>\), so a vanilla loose copy must
        // never preempt the BSA-packed canonical asset. Without this skip the
        // diagnostic would log "OK (vanilla loose)" for a Bijin / Pandorable
        // override deployed into Data via VFS even though the renderer itself
        // ignores it and uses the vanilla BSA.
        bool isFaceGen = path.IndexOf("FaceGenData", StringComparison.OrdinalIgnoreCase) >= 0;

        // Phase 1: loose files, walked highest-priority-first (last scope wins).
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            if (i == 0 && isFaceGen) continue;
            var scope = scopes[i];
            if (string.IsNullOrWhiteSpace(scope.FolderPath)) continue;
            string candidate;
            try { candidate = Path.Combine(scope.FolderPath, path); }
            catch { continue; }
            if (File.Exists(candidate))
            {
                string sourceLabel = (i == 0) ? "vanilla loose" : "mod loose";
                WriteTrace($"[AssetResolution]   [{label}] OK ({sourceLabel}): {candidate}");
                return false;
            }
        }

        // Phase 2: scoped BSAs, walked highest-priority-first. Within a scope,
        // any BSA owned by one of its plugin filenames AT that folder counts.
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (string.IsNullOrWhiteSpace(scope.FolderPath)) continue;
            foreach (var keyName in scope.ModKeyFileNames)
            {
                if (!ModKey.TryFromNameAndExtension(keyName, out var modKey)) continue;
                if (_bsaHandler.FileExistsInArchiveAtFolder(path, modKey, scope.FolderPath, out var bsaPath) && bsaPath != null)
                {
                    string sourceLabel = (i == 0) ? "vanilla BSA" : "mod BSA";
                    WriteTrace($"[AssetResolution]   [{label}] OK ({sourceLabel}): {bsaPath} :: {path}");
                    return false;
                }
            }
        }

        // Truly missing — dump every loose path and BSA scope the renderer
        // would have checked, in the same highest-priority-first order, so
        // the user can quickly see which mod was expected to ship the asset.
        var missSb = new System.Text.StringBuilder();
        missSb.Append("[AssetResolution]   [").Append(label).Append("] !! MISSING\n");
        missSb.Append("    game-relative: ").Append(path).Append('\n');
        missSb.Append("    loose paths checked (highest-priority first):\n");
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            if (i == 0 && isFaceGen) continue;
            var scope = scopes[i];
            if (string.IsNullOrWhiteSpace(scope.FolderPath)) continue;
            string candidate;
            try { candidate = Path.Combine(scope.FolderPath, path); }
            catch { continue; }
            missSb.Append("      ").Append(candidate).Append('\n');
        }
        missSb.Append("    BSA scopes checked (highest-priority first):\n");
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (string.IsNullOrWhiteSpace(scope.FolderPath)) continue;
            foreach (var keyName in scope.ModKeyFileNames)
            {
                missSb.Append("      ").Append(scope.FolderPath).Append(" :: ").Append(keyName).Append('\n');
            }
        }
        WriteTrace(missSb.ToString());
        return true;
    }

    /// <summary>Builds the same scope chain as <see cref="BuildResolutionScopes"/>
    /// but driven from <see cref="NpcResolutionContext"/> so the diagnostic can
    /// mirror the renderer's strict resolution without re-needing the original
    /// <see cref="ModSetting"/>. Order: [vanilla, modFolder1, modFolder2, ...]
    /// — the last entry is the highest-priority scope under last-to-first
    /// iteration.</summary>
    private List<RenderScope> BuildDiagnosticScopes(NpcResolutionContext? context)
    {
        var scopes = new List<RenderScope>();

        var vanillaModKeyNames = new List<string>();
        foreach (var mk in _env.BaseGamePlugins) vanillaModKeyNames.Add(mk.FileName.String);
        foreach (var mk in _env.CreationClubPlugins) vanillaModKeyNames.Add(mk.FileName.String);
        scopes.Add(new RenderScope(_env.DataFolderPath.ToString(), vanillaModKeyNames));

        // Origin sits between vanilla and the selected mod: it must be reachable (a tint-only or
        // record-only mod leaves the mesh here), but must never outrank the mod the user picked.
        if (context != null && context.OriginFolderPaths.Count > 0)
        {
            var originKeyNames = context.OriginModKeys.Select(mk => mk.FileName.String).ToList();
            foreach (var folder in context.OriginFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                scopes.Add(new RenderScope(folder, originKeyNames));
            }
        }

        if (context != null && context.PreferredFolderPaths.Count > 0)
        {
            var modKeyNames = context.PreferredModKeys
                .Select(mk => mk.FileName.String)
                .ToList();
            foreach (var folder in context.PreferredFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                scopes.Add(new RenderScope(folder, modKeyNames));
            }
        }
        return scopes;
    }

    private static void WriteTrace(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        System.Diagnostics.Trace.WriteLine(message);
        RenderLogCapture.Write(message);
    }

    /// <summary>
    /// Looks up <paramref name="link"/> in the context's preferred plugins
    /// first (with disambiguation), falling back to the LinkCache winner.
    /// When <paramref name="context"/> is null, this is a straight LinkCache
    /// lookup and behaves identically to the pre-mod-scoping resolver.
    /// </summary>
    /// <param name="pluginSink">
    /// When non-null, receives the plugins that could carry this record's ASSETS: the
    /// originating plugin (<c>link.FormKey.ModKey</c>) and, additionally, the plugin whose
    /// override the link cache resolves to — a patch that repoints a model at its own
    /// asset is a different plugin from the one that first defined the record.
    /// <para>Only the outfit walk passes a sink, and only when the outfit came from
    /// outside the appearance mod's scope. The extra context lookup is deliberately
    /// ADDITIVE rather than replacing the <c>TryResolve</c> below: that branch is shared
    /// by every record type this resolver touches (NPC, Race, WornArmor, head parts) and
    /// the widening must not perturb any of them.</para>
    /// </param>
    private T? ResolveRecord<T>(IFormLinkGetter<T> link, ILinkCache linkCache, NpcResolutionContext? context,
        HashSet<ModKey>? pluginSink = null)
        where T : class, IMajorRecordGetter
    {
        if (pluginSink != null)
        {
            if (!link.FormKey.ModKey.IsNull) pluginSink.Add(link.FormKey.ModKey);
            if (linkCache.TryResolveSimpleContext<T>(link, out var winnerCtx) && !winnerCtx.ModKey.IsNull)
            {
                pluginSink.Add(winnerCtx.ModKey);
            }
        }

        bool capturing = RenderLogCapture.IsCapturing;
        if (capturing)
        {
            string ctxSummary;
            if (context == null) ctxSummary = "(no context)";
            else
            {
                ctxSummary = $"PreferredModKeys=[{string.Join(", ", context.PreferredModKeys.Select(k => k.FileName.String))}]"
                             + $", PreferredFolderPaths=[{string.Join(", ", context.PreferredFolderPaths)}]"
                             + $", DisambiguationModKey={(context.DisambiguationModKey?.FileName.String ?? "(none)")}";
            }
            WriteTrace($"[ResolveRecord] type={typeof(T).Name} fk={link.FormKey} {ctxSummary}");
        }

        // Origin mode runs the mod-scoped block even with zero PreferredModKeys
        // (mesh-only mods carry no plugins) so its Origin fallback still applies;
        // Winner mode keeps the historical shape and falls through to the outer
        // linkCache below instead.
        if (context != null &&
            (context.PreferredModKeys.Count > 0 ||
             context.FallbackMode == RecordHandler.RecordLookupFallBack.Origin))
        {
            // Disambiguation key wins when the mod's plugin set spans multiple
            // candidates for this NPC (mirrors Patcher.cs:292-298).
            if (context.DisambiguationModKey is { } disKey &&
                _recordHandler.TryGetRecordGetterFromMod(link, disKey, context.FallBackFolderNames,
                    RecordHandler.RecordLookupFallBack.None, out var disRec) &&
                disRec is T disT)
            {
                if (capturing) WriteTrace($"[ResolveRecord] resolved via DisambiguationModKey={disKey.FileName}");
                return disT;
            }

            // Iterate CorrespondingModKeys in the order recorded by the user
            // (last entry wins per NPC2's convention). reverseOrder: true makes
            // TryGetRecordFromMods walk the list backwards.
            if (_recordHandler.TryGetRecordFromMods(link, context.PreferredModKeys, context.FallBackFolderNames,
                    context.FallbackMode, out var rec, reverseOrder: true) &&
                rec is T t)
            {
                if (capturing) WriteTrace($"[ResolveRecord] resolved via TryGetRecordFromMods (mod-scoped or {context.FallbackMode} fallback)");
                return t;
            }
            if (capturing) WriteTrace($"[ResolveRecord] mod-scoped path (incl. {context.FallbackMode} fallback) returned no match");

            // Origin mode must NOT fall through to the load-order winner below:
            // the whole point is that a record the mod (and its origin) doesn't
            // supply stays unresolved instead of borrowing a foreign override.
            if (context.FallbackMode == RecordHandler.RecordLookupFallBack.Origin)
            {
                if (capturing) WriteTrace($"[ResolveRecord] FAILED (Origin mode, no winner fallback) fk={link.FormKey} type={typeof(T).Name}");
                return null;
            }
        }
        if (linkCache.TryResolve<T>(link.FormKey, out var lcRec))
        {
            if (capturing) WriteTrace("[ResolveRecord] resolved via outer linkCache.TryResolve");
            return lcRec;
        }
        if (capturing) WriteTrace($"[ResolveRecord] FAILED to resolve fk={link.FormKey} type={typeof(T).Name}");
        return null;
    }

    /// <summary>
    /// If <paramref name="gameRelativePath"/> exists as a loose file under any
    /// folder in <paramref name="context"/>.PreferredFolderPaths, returns the
    /// absolute disk path of the override. Iterates in reverse so the last
    /// folder wins (NPC2's "later mod folder = override winner" convention).
    /// Otherwise returns the input unchanged so the renderer can resolve it
    /// against the vanilla data folder / BSAs.
    /// </summary>
    private static string? RebaseToAbsoluteIfPresent(string? gameRelativePath, NpcResolutionContext? context)
    {
        if (gameRelativePath == null) return null;
        if (context == null || context.PreferredFolderPaths.Count == 0) return gameRelativePath;
        if (Path.IsPathRooted(gameRelativePath)) return gameRelativePath; // already absolute
        for (int i = context.PreferredFolderPaths.Count - 1; i >= 0; i--)
        {
            var folder = context.PreferredFolderPaths[i];
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder, gameRelativePath);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed path; skip and let vanilla resolution try */ }
        }
        return gameRelativePath;
    }

    private (IArmorGetter? armor, string source) ResolveWornArmor(INpcGetter npcGetter, ILinkCache linkCache,
        NpcResolutionContext? context, string npcName)
    {
        if (npcGetter.WornArmor != null && !npcGetter.WornArmor.IsNull)
        {
            var armorGetter = ResolveRecord<IArmorGetter>(npcGetter.WornArmor, linkCache, context);
            if (armorGetter != null)
            {
                LogVerbose("CharacterViewer: WornArmor=" + npcGetter.WornArmor.FormKey);
                return (armorGetter, "WornArmor:" + npcGetter.WornArmor.FormKey);
            }
        }

        if (npcGetter.Race != null && !npcGetter.Race.IsNull)
        {
            var raceGetter = ResolveRecord<IRaceGetter>(npcGetter.Race, linkCache, context);
            if (raceGetter?.Skin != null && !raceGetter.Skin.IsNull)
            {
                var raceSkinArmor = ResolveRecord<IArmorGetter>(raceGetter.Skin, linkCache, context);
                if (raceSkinArmor != null)
                {
                    LogVerbose("CharacterViewer: No WornArmor for " + npcName +
                        ", falling back to Race.Skin=" + raceGetter.Skin.FormKey);
                    return (raceSkinArmor, "Race.Skin:" + raceGetter.Skin.FormKey);
                }
            }
        }

        LogVerbose("CharacterViewer: No WornArmor or Race.Skin found for " + npcName);
        return (null, "(none)");
    }

    private static bool IsArmatureForRace(IArmorAddonGetter armaGetter, FormKey? npcRaceKey, FormKey? armorRaceKey)
    {
        if (npcRaceKey == null) return true;
        return Auxilliary.ArmaNamesRace(armaGetter, npcRaceKey, armorRaceKey);
    }

    /// <summary>The NPC race's ArmorRace (RNAM) — the key the engine actually
    /// matches armatures against — resolved through the same mod-scoped chain as
    /// the rest of this class so a mod overriding the race record wins here too.
    /// Null when the NPC has no race, the race won't resolve, or ArmorRace adds
    /// nothing; every caller then falls back to the raw race comparison.</summary>
    private FormKey? ResolveArmorRaceKey(INpcGetter npcGetter, ILinkCache linkCache, NpcResolutionContext? context)
    {
        if (npcGetter.Race == null || npcGetter.Race.IsNull) return null;
        return Auxilliary.GetArmorRaceKey(ResolveRecord<IRaceGetter>(npcGetter.Race, linkCache, context));
    }

    /// <summary>The ARMA's per-sex weight-slider flag — the engine's own signal
    /// for whether this piece morphs between a _0/_1 world-model pair.</summary>
    private static bool GetWeightSliderEnabled(IArmorAddonGetter armaGetter, NpcGender sex)
        => sex == NpcGender.Female ? armaGetter.WeightSliderEnabled.Female : armaGetter.WeightSliderEnabled.Male;

    private static string? GetWorldModelPath(IArmorAddonGetter armaGetter, NpcGender sex)
    {
        if (armaGetter.WorldModel == null) return null;
        var model = sex == NpcGender.Female ? armaGetter.WorldModel.Female : armaGetter.WorldModel.Male;
        if (model?.File == null) return null;
        string path = model.File.GivenPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            path = "meshes\\" + path;
        }
        return path;
    }

    private Dictionary<int, string> ResolveTxstTextures(
        IArmorAddonGetter armaGetter, NpcGender sex, ILinkCache linkCache, NpcResolutionContext? context, FormKey armaFormKey,
        HashSet<ModKey>? pluginSink = null)
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (armaGetter.SkinTexture == null) return result;
            var skinTextureLink = sex == NpcGender.Female ? armaGetter.SkinTexture.Female : armaGetter.SkinTexture.Male;
            if (skinTextureLink == null || skinTextureLink.IsNull) return result;
            var txst = ResolveRecord<ITextureSetGetter>(skinTextureLink, linkCache, context, pluginSink);
            if (txst == null)
            {
                LogVerbose("CharacterViewer: Could not resolve TXST " + skinTextureLink.FormKey + " from ARMA " + armaFormKey);
                return result;
            }

            PopulateTxstSlots(txst, result);
        }
        catch (Exception ex)
        {
            LogVerbose("CharacterViewer: TXST resolution failed for ARMA " + armaFormKey + ": " + ex.Message);
        }
        return result;
    }

    /// <summary>Resolves an ArmorAddon's per-object AlternateTextures (the MODS
    /// subrecord on the sex-appropriate WorldModel) into a per-shape TXST map:
    /// NIF shape node name -> (renderer texture-slot -> game-relative path). This
    /// is how a mod retextures individual shapes of a shared world-model NIF
    /// (e.g. alternate-coloured variants of one cuirass) without shipping a
    /// separate mesh. Each entry's <c>Name</c> is the target shape's NIF node name
    /// (matched by the renderer against BuiltMesh.ShapeName) and <c>NewTexture</c>
    /// is the replacement TextureSet. Slot indices match
    /// <see cref="ResolveTxstTextures"/>.</summary>
    /// <summary>Resolves an ArmorAddon world-model's AlternateTextures (MODS)
    /// entries to (3D Name, 3D Index, texture-slot map) tuples, in record order.
    /// Both identity fields are carried: the renderer matches by name first and
    /// falls back to the index, so a rebuilt mesh whose shapes were renamed
    /// (BodySlide output) still gets its variant — the engine itself keys on the
    /// index (see <c>AlternateTextureSpec</c>). Entries whose TXST fails to
    /// resolve, or that carry neither a name nor a usable index, are skipped
    /// with a log line. Duplicate targets are preserved; the renderer applies
    /// them in order (later wins per slot).</summary>
    private List<(string ShapeName, int ShapeIndex, Dictionary<int, string> Slots)> ResolveAlternateTextures(
        IArmorAddonGetter armaGetter, NpcGender sex, ILinkCache linkCache, NpcResolutionContext? context, FormKey armaFormKey,
        HashSet<ModKey>? pluginSink = null)
    {
        var result = new List<(string, int, Dictionary<int, string>)>();
        try
        {
            var model = sex == NpcGender.Female ? armaGetter.WorldModel?.Female : armaGetter.WorldModel?.Male;
            var alts = model?.AlternateTextures;
            if (alts == null || alts.Count == 0) return result;

            foreach (var alt in alts)
            {
                if (alt == null) continue;
                string shapeName = alt.Name ?? string.Empty;
                int shapeIndex = alt.Index;
                if (string.IsNullOrWhiteSpace(shapeName) && shapeIndex < 0) continue;
                if (alt.NewTexture.IsNull) continue;
                var txst = ResolveRecord<ITextureSetGetter>(alt.NewTexture, linkCache, context, pluginSink);
                if (txst == null)
                {
                    LogVerbose("CharacterViewer: Could not resolve AlternateTexture TXST " + alt.NewTexture.FormKey
                        + " for shape '" + shapeName + "' (3D index " + shapeIndex + ") from ARMA " + armaFormKey);
                    continue;
                }

                var slots = new Dictionary<int, string>();
                PopulateTxstSlots(txst, slots);
                if (slots.Count > 0) result.Add((shapeName, shapeIndex, slots));
            }
        }
        catch (Exception ex)
        {
            LogVerbose("CharacterViewer: AlternateTexture resolution failed for ARMA " + armaFormKey + ": " + ex.Message);
        }
        return result;
    }

    /// <summary>Reads a TextureSet's slot paths into <paramref name="slots"/> using
    /// the renderer's NIF-order slot indices (0 diffuse, 1 normal/gloss,
    /// 2 glow/subsurface, 3 height, 4 environment cubemap, 5 environment mask,
    /// 6 multilayer/inner, 7 backlight/specular), prefixing a bare "textures\"
    /// root when the TXST stores a Data-relative path without it. Shared by the
    /// SkinTexture and AlternateTextures resolvers.</summary>
    private static void PopulateTxstSlots(ITextureSetGetter txst, Dictionary<int, string> slots)
    {
        void TryAdd(int slot, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string fullPath = path;
            if (!fullPath.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = "textures\\" + fullPath;
            }
            slots[slot] = fullPath;
        }

        TryAdd(0, txst.Diffuse?.DataRelativePath.Path);
        TryAdd(1, txst.NormalOrGloss?.DataRelativePath.Path);
        // TX02 is dual-purpose by shader type: subsurface (_sk) for skin
        // shaders (renderer slot 2) and environment mask (_m) for env-mapped
        // gear (renderer slot 5). Write it to both; a TXST that also authors
        // TX03 (glow/detail) wins slot 2 below. Dropping TX02 (the previous
        // behavior) left an AlternateTextures variant reflecting its new
        // cubemap through the NIF's STALE embedded env mask — the Caenarvon
        // Gala dress rendered gold instead of navy because the default
        // colorway's high-reflectivity mask stayed bound under the variant's
        // gold cubemap.
        TryAdd(2, txst.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        TryAdd(5, txst.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        // TX03 (Glow/Detail) goes to slot 3 (detail/parallax), NOT slot 2:
        // the renderer's slot 2 is the skin/SSS sampler for skin shaders, and
        // vanilla beast skin TXSTs author BOTH TX02 (_sk) and TX03 — routing
        // TX03 to slot 2 clobbered the subsurface map (AUD-2). Slot 2 as a
        // GLOW sampler is unimplemented (AUD-4), so nothing is lost for glow
        // shapes. TX04 (Height) is written after so an explicit height map
        // wins slot 3 over a detail map in the (pathological) both-authored case.
        TryAdd(3, txst.GlowOrDetailMap?.DataRelativePath.Path);
        TryAdd(3, txst.Height?.DataRelativePath.Path);
        TryAdd(4, txst.Environment?.DataRelativePath.Path);
        // NIF slot 6 (inner/multilayer) — previously written to 5, where it
        // would have stomped the environment mask.
        TryAdd(6, txst.Multilayer?.DataRelativePath.Path);
        TryAdd(7, txst.BacklightMaskOrSpecular?.DataRelativePath.Path);
    }

    private static string BuildFaceGenPath(FormKey formKey)
    {
        string plugin = formKey.ModKey.FileName;
        string formId = formKey.ID.ToString("X8");
        return "meshes\\actors\\character\\FaceGenData\\FaceGeom\\" + plugin + "\\" + formId + ".nif";
    }

    private static string BuildFaceTintPath(FormKey formKey)
    {
        string plugin = formKey.ModKey.FileName;
        string formId = formKey.ID.ToString("X8");
        return "textures\\actors\\character\\FaceGenData\\FaceTint\\" + plugin + "\\" + formId + ".dds";
    }

    private string? ResolveSkeletonPath(INpcGetter npcGetter, NpcGender sex, ILinkCache linkCache, NpcResolutionContext? context)
    {
        if (npcGetter.Race == null || npcGetter.Race.IsNull) return null;
        var raceGetter = ResolveRecord<IRaceGetter>(npcGetter.Race, linkCache, context);
        if (raceGetter?.SkeletalModel == null) return null;
        var skelModel = sex == NpcGender.Female ? raceGetter.SkeletalModel.Female : raceGetter.SkeletalModel.Male;
        if (skelModel?.File == null) return null;
        string path = skelModel.File.GivenPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            path = "meshes\\" + path;
        }
        LogVerbose("CharacterViewer: Skeleton=" + path + " (Race=" + npcGetter.Race.FormKey + ", " + sex + ")");
        return path;
    }
}
