using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

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
    private readonly ICharacterViewerLogger _logger;
    private readonly CharacterViewerLogGate _logGate;
    private readonly Settings _settings;
    private readonly NpcConsistencyProvider _consistency;
    private readonly RecordHandler _recordHandler;
    private readonly EnvironmentStateProvider _env;
    private readonly BsaHandler _bsaHandler;

    public NpcMeshResolver(
        ICharacterViewerLogger logger,
        CharacterViewerLogGate logGate,
        Settings settings,
        NpcConsistencyProvider consistency,
        RecordHandler recordHandler,
        EnvironmentStateProvider env,
        BsaHandler bsaHandler)
    {
        _logger = logger;
        _logGate = logGate;
        _settings = settings;
        _consistency = consistency;
        _recordHandler = recordHandler;
        _env = env;
        _bsaHandler = bsaHandler;
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
    public IReadOnlyList<RenderScope> BuildResolutionScopes(ModSetting? modSetting)
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

        // scopes[1..N] — active mod's folders, in CorrespondingFolderPaths
        // order (so the last folder ends up as the LAST list entry, which
        // is the highest priority under last-to-first iteration). Each
        // folder pairs with the same CorrespondingModKeys list — the spec
        // calls for "BSAs at this folder owned by any of the
        // CorrespondingModKeys".
        if (modSetting != null && modSetting.CorrespondingFolderPaths.Count > 0)
        {
            var modKeyNames = modSetting.CorrespondingModKeys
                .Select(mk => mk.FileName.String)
                .ToList();
            foreach (var folder in modSetting.CorrespondingFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                scopes.Add(new RenderScope(folder, modKeyNames));
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
        => BuildResolutionScopes(LookupSelectedModSetting(targetNpcFormKey, out _));


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
        return Resolve(npcFormKey, linkCache, BuildContext(npcFormKey, modSetting));
    }

    /// <summary>
    /// Resolves the NPC and returns a head-part resolver bound to the SAME mod scope
    /// used for mesh resolution (mod plugins first, link cache fallback for shared /
    /// vanilla parts). Used by the FaceGen-vs-records consistency check so the head
    /// parts are evaluated against the exact records that produced the rendered NIF,
    /// rather than the load-order winner (which may be a different mod). Returns
    /// (null, _ => null) when the environment has no link cache.
    /// </summary>
    public (INpcGetter? Npc, Func<FormKey, IHeadPartGetter?> ResolveHeadPart, Func<FormKey, IRaceGetter?> ResolveRace)
        ResolveNpcForConsistency(FormKey npcFormKey, ModSetting? modSetting)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return (null, _ => null, _ => null);
        var context = BuildContext(npcFormKey, modSetting);
        var npc = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        Func<FormKey, IHeadPartGetter?> resolveHeadPart =
            fk => ResolveRecord<IHeadPartGetter>(fk.ToLink<IHeadPartGetter>(), linkCache, context);
        Func<FormKey, IRaceGetter?> resolveRace =
            fk => ResolveRecord<IRaceGetter>(fk.ToLink<IRaceGetter>(), linkCache, context);
        return (npc, resolveHeadPart, resolveRace);
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
    /// </summary>
    private NpcResolutionContext? BuildContext(FormKey targetNpcFormKey, ModSetting? modSetting)
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

        return new NpcResolutionContext
        {
            PreferredModKeys = modSetting.CorrespondingModKeys,
            PreferredFolderPaths = modSetting.CorrespondingFolderPaths,
            FallBackFolderNames = modSetting.CorrespondingFolderPaths.ToHashSet(),
            DisambiguationModKey = disambiguation,
        };
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
        var context = BuildContext(npcFormKey, modSetting);
        string facegenPath = BuildFaceGenPath(npcFormKey);

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
        return false;
    }

    /// <summary>
    /// Resolves the full set of paths for the NPC. Returns null if the NPC
    /// itself can't be resolved either from the context's plugins or the
    /// link cache.
    /// </summary>
    public ResolvedNpcMeshPaths? Resolve(FormKey npcFormKey, ILinkCache linkCache, NpcResolutionContext? context = null)
    {
        var npcGetter = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        if (npcGetter == null)
        {
            _logger?.LogError("CharacterViewer: Could not resolve NPC " + npcFormKey);
            return null;
        }

        var sex = Auxilliary.IsFemale(npcGetter) ? Sex.Female : Sex.Male;
        string npcName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcFormKey.ToString();
        LogVerbose("CharacterViewer: Resolving NPC " + npcName + " (" + npcFormKey + ")");

        FormKey? npcRaceKey = (npcGetter.Race != null && !npcGetter.Race.IsNull) ? npcGetter.Race.FormKey : null;

        var (armorGetter, armorSource) = ResolveWornArmor(npcGetter, linkCache, context, npcName);

        string? bodyPath = null;
        string? handsPath = null;
        string? feetPath = null;
        string? hairPath = null;
        string? tailPath = null;
        var chains = new Dictionary<string, string>();
        var txstTextures = new Dictionary<string, Dictionary<int, string>>();
        string sexLabel = sex == Sex.Female ? "Female" : "Male";

        if (armorGetter?.Armature != null)
        {
            foreach (var armaLink in armorGetter.Armature)
            {
                var armaGetter = ResolveRecord<IArmorAddonGetter>(armaLink, linkCache, context);
                if (armaGetter == null) continue;
                if (armaGetter.BodyTemplate == null) continue;
                if (!IsArmatureForRace(armaGetter, npcRaceKey))
                {
                    LogVerbose("CharacterViewer: Skipping Armature " + armaLink.FormKey +
                        " — race " + (npcRaceKey?.ToString() ?? "(none)") + " not in ARMA.Race/AdditionalRaces");
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
                // Hair (biped slot 31): NPC overhauls like High Poly NPC
                // Overhaul ship a "bald" FaceGen scalp + a wig ARMO whose
                // ARMA occupies this slot. Without picking it up here the
                // NPC renders hairless.
                if (hairPath == null && flags.HasFlag(BipedObjectFlag.Hair))
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
        // logs internally only when CharacterViewerLogGate.Verbose is on, and
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
            Sex = sex,
            SkeletonPath = skeletonPath,
            ResolutionChains = chains,
            TxstTextures = txstTextures,
            FaceTintPath = finalFaceTintPath,
            TextureLightingColor = textureLightingColor,
            NpcWeight = weight,
            NpcBaseHeight = baseHeight,
            HairColorRgb = hairRgb,
        };
    }

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
    {
        var modSetting = LookupSelectedModSetting(targetNpcFormKey, out var sourceFormKey);
        return ResolveAttireMeshOverrides(sourceFormKey, modSetting, includeDefaultOutfit, includeHeadgear);
    }

    /// <summary>
    /// Resolves the NPC's worn attire ("Include Default Outfit") and/or head
    /// gear ("Include headgear") into neutral <see cref="MeshOverride"/>s for
    /// <see cref="VM_CharacterViewer.ApplyMeshOverrides"/>. Walks
    /// <c>NPC.DefaultOutfit → OTFT.Items</c> (ARMO directly, LeveledItem resolved
    /// deterministically to the first valid armor and logged), plus the worn/skin
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
    {
        if (!includeDefaultOutfit && !includeHeadgear) return Array.Empty<MeshOverride>();
        var linkCache = _env.LinkCache;
        if (linkCache == null) return Array.Empty<MeshOverride>();
        return ResolveAttireMeshOverrides(npcFormKey, linkCache,
            BuildContext(npcFormKey, modSetting), includeDefaultOutfit, includeHeadgear);
    }

    private IReadOnlyList<MeshOverride> ResolveAttireMeshOverrides(
        FormKey npcFormKey, ILinkCache linkCache, NpcResolutionContext? context,
        bool includeDefaultOutfit, bool includeHeadgear)
    {
        var result = new List<MeshOverride>();
        // Outfit is the dominant toggle — headgear is part of the outfit and never
        // renders on its own, so with the outfit off nothing attire-related is
        // emitted regardless of the headgear flag. (The UI also hides the headgear
        // toggle while the outfit is off.)
        includeHeadgear = includeHeadgear && includeDefaultOutfit;
        if (!includeDefaultOutfit && !includeHeadgear) return result;

        var npcGetter = ResolveRecord<INpcGetter>(npcFormKey.ToLink<INpcGetter>(), linkCache, context);
        if (npcGetter == null)
        {
            _logger?.LogError("CharacterViewer: ResolveAttireMeshOverrides could not resolve NPC " + npcFormKey);
            return result;
        }

        var sex = Auxilliary.IsFemale(npcGetter) ? Sex.Female : Sex.Male;
        FormKey? npcRaceKey = (npcGetter.Race != null && !npcGetter.Race.IsNull) ? npcGetter.Race.FormKey : null;
        string npcName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcFormKey.ToString();

        // Dedup by override Key (slot(s) + ARMA FormKey) so a piece reachable via
        // both the outfit and the worn armor is only emitted once.
        var seenOverrideKeys = new HashSet<string>();

        // Default outfit → apparel armors. Drives both features: a head-slot
        // piece becomes headgear, everything else body attire.
        if (npcGetter.DefaultOutfit != null && !npcGetter.DefaultOutfit.IsNull)
        {
            var outfit = ResolveRecord<IOutfitGetter>(npcGetter.DefaultOutfit, linkCache, context);
            if (outfit?.Items != null)
            {
                var outfitArmors = new List<(IArmorGetter armor, string source)>();
                var seenArmorKeys = new HashSet<FormKey>();
                foreach (var itemLink in outfit.Items)
                {
                    if (itemLink == null || itemLink.IsNull) continue;
                    CollectOutfitItemArmors(itemLink.FormKey, linkCache, context,
                        "Outfit:" + npcGetter.DefaultOutfit.FormKey, outfitArmors, seenArmorKeys, depth: 0);
                }
                foreach (var (armor, source) in outfitArmors)
                {
                    AppendArmorMeshOverrides(armor, source, sex, npcRaceKey, linkCache, context,
                        includeBody: includeDefaultOutfit, includeHeadgear: includeHeadgear,
                        hairCountsAsHeadgear: true, result, seenOverrideKeys);
                }
            }
            else
            {
                LogVerbose("CharacterViewer: ResolveAttireMeshOverrides could not resolve DefaultOutfit "
                    + npcGetter.DefaultOutfit.FormKey + " for " + npcName);
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
                AppendArmorMeshOverrides(wornArmor, wornSource, sex, npcRaceKey, linkCache, context,
                    includeBody: false, includeHeadgear: true,
                    hairCountsAsHeadgear: false, result, seenOverrideKeys);
            }
        }

        LogVerbose("CharacterViewer: ResolveAttireMeshOverrides for " + npcName + " (" + npcFormKey
            + ") outfit=" + includeDefaultOutfit + " headgear=" + includeHeadgear
            + " -> " + result.Count + " override(s)");
        return result;
    }

    /// <summary>
    /// Resolves one outfit <c>Items</c> entry to apparel armor(s) and appends
    /// them to <paramref name="armors"/>. ARMO entries are taken directly;
    /// LeveledItem entries are resolved DETERMINISTICALLY to the first valid
    /// armor (in declared entry order, recursing into nested leveled lists) and
    /// the choice is logged — never random per render. Non-apparel items
    /// (weapons / ammo / quest items) and unresolvable links are skipped.
    /// </summary>
    private void CollectOutfitItemArmors(FormKey itemFormKey, ILinkCache linkCache, NpcResolutionContext? context,
        string source, List<(IArmorGetter armor, string source)> armors, HashSet<FormKey> seen, int depth)
    {
        if (depth > 10) return; // guard against pathological leveled-list cycles

        var armor = ResolveRecord<IArmorGetter>(itemFormKey.ToLink<IArmorGetter>(), linkCache, context);
        if (armor != null)
        {
            if (seen.Add(itemFormKey)) armors.Add((armor, source));
            return;
        }

        var lvli = ResolveRecord<ILeveledItemGetter>(itemFormKey.ToLink<ILeveledItemGetter>(), linkCache, context);
        if (lvli != null)
        {
            var chosen = ResolveFirstArmorFromLeveledItem(lvli, linkCache, context, depth);
            if (chosen != null)
            {
                LogVerbose("CharacterViewer: Outfit LeveledItem " + itemFormKey + " -> chose armor "
                    + chosen.Value.label + " (deterministic: first valid armor in entry order)");
                if (seen.Add(chosen.Value.armor.FormKey))
                    armors.Add((chosen.Value.armor, source + " -> LVLI:" + itemFormKey));
            }
            else
            {
                LogVerbose("CharacterViewer: Outfit LeveledItem " + itemFormKey + " yielded no apparel armor (skipped)");
            }
            return;
        }

        LogVerbose("CharacterViewer: Outfit item " + itemFormKey + " is not Armor/LeveledItem (non-apparel, skipped)");
    }

    /// <summary>Deterministically picks the first valid armor reachable from
    /// <paramref name="lvli"/> in declared entry order, recursing into nested
    /// leveled lists. Returns null when the list contains no apparel armor.</summary>
    private (IArmorGetter armor, string label)? ResolveFirstArmorFromLeveledItem(
        ILeveledItemGetter lvli, ILinkCache linkCache, NpcResolutionContext? context, int depth)
    {
        if (depth > 10 || lvli.Entries == null) return null;
        foreach (var entry in lvli.Entries)
        {
            var refLink = entry?.Data?.Reference;
            if (refLink == null || refLink.IsNull) continue;
            var fk = refLink.FormKey;

            var armor = ResolveRecord<IArmorGetter>(fk.ToLink<IArmorGetter>(), linkCache, context);
            if (armor != null) return (armor, armor.EditorID ?? fk.ToString());

            var nested = ResolveRecord<ILeveledItemGetter>(fk.ToLink<ILeveledItemGetter>(), linkCache, context);
            if (nested != null)
            {
                var inner = ResolveFirstArmorFromLeveledItem(nested, linkCache, context, depth + 1);
                if (inner != null) return inner;
            }
        }
        return null;
    }

    /// <summary>Emits one <see cref="MeshOverride"/> per applicable ArmorAddon of
    /// <paramref name="armor"/> (filtered by NPC race), classifying each by slot
    /// into body attire (<see cref="MeshOverrideKind.Armor"/>) or headgear
    /// (<see cref="MeshOverrideKind.Headgear"/>). <paramref name="includeBody"/> /
    /// <paramref name="includeHeadgear"/> gate which classes are emitted. The Key
    /// combines slot(s) + ARMA FormKey so it's stable and unique per piece.</summary>
    private void AppendArmorMeshOverrides(IArmorGetter armor, string source, Sex sex, FormKey? npcRaceKey,
        ILinkCache linkCache, NpcResolutionContext? context, bool includeBody, bool includeHeadgear,
        bool hairCountsAsHeadgear, List<MeshOverride> result, HashSet<string> seenKeys)
    {
        if (armor.Armature == null) return;
        foreach (var armaLink in armor.Armature)
        {
            if (armaLink == null || armaLink.IsNull) continue;
            var arma = ResolveRecord<IArmorAddonGetter>(armaLink, linkCache, context);
            if (arma?.BodyTemplate == null) continue;
            if (!IsArmatureForRace(arma, npcRaceKey)) continue;

            int slots = (int)arma.BodyTemplate.FirstPersonFlags;
            if (slots == 0) continue;

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
            if (isHead && !includeHeadgear) continue;
            if (!isHead && !includeBody) continue;

            string? meshPath = GetWorldModelPath(arma, sex);
            if (meshPath == null) continue;

            string key = (isHead ? "Headgear:" : "Outfit:") + armaLink.FormKey + ":" + slots;
            if (!seenKeys.Add(key)) continue;

            // Texture set: §2.6.6 — ArmorAddon.SkinTexture takes precedence. When
            // present (skin-type addons), use it; rebase to the mod's loose
            // overrides like the base meshes. Otherwise leave Textures null so the
            // NIF's own embedded BSShaderTextureSet renders (correct for armor) —
            // the armature's per-object AlternateTextures can't be expressed
            // through the renderer's flat per-shape slot channel, so they degrade
            // to the NIF default (logged for traceability).
            var txst = ResolveTxstTextures(arma, sex, linkCache, context, armaLink.FormKey);
            meshPath = RebaseToAbsoluteIfPresent(meshPath, context) ?? meshPath;
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
            else if (HasAlternateTextures(arma, sex))
            {
                LogVerbose("CharacterViewer: ARMA " + armaLink.FormKey + " has AlternateTextures but no SkinTexture; "
                    + "using the NIF's own texture set (per-object overrides not routable through MeshOverride)");
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
            });
            LogVerbose("CharacterViewer: attire override '" + key + "' mesh=" + meshPath
                + " slots=" + slots + " kind=" + kind + " src=" + source);
        }
    }

    private static bool HasAlternateTextures(IArmorAddonGetter arma, Sex sex)
    {
        var model = sex == Sex.Female ? arma.WorldModel?.Female : arma.WorldModel?.Male;
        var alts = model?.AlternateTextures;
        return alts != null && alts.Count > 0;
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
    private T? ResolveRecord<T>(IFormLinkGetter<T> link, ILinkCache linkCache, NpcResolutionContext? context)
        where T : class, IMajorRecordGetter
    {
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

        if (context != null && context.PreferredModKeys.Count > 0)
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
                    RecordHandler.RecordLookupFallBack.Winner, out var rec, reverseOrder: true) &&
                rec is T t)
            {
                if (capturing) WriteTrace("[ResolveRecord] resolved via TryGetRecordFromMods (mod-scoped or Winner fallback)");
                return t;
            }
            if (capturing) WriteTrace("[ResolveRecord] mod-scoped path (incl. Winner fallback) returned no match");
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

    private static bool IsArmatureForRace(IArmorAddonGetter armaGetter, FormKey? npcRaceKey)
    {
        if (npcRaceKey == null) return true;
        if (armaGetter.Race != null && !armaGetter.Race.IsNull &&
            armaGetter.Race.FormKey.Equals(npcRaceKey.Value)) return true;
        if (armaGetter.AdditionalRaces != null)
        {
            foreach (var addRace in armaGetter.AdditionalRaces)
            {
                if (!addRace.IsNull && addRace.FormKey.Equals(npcRaceKey.Value)) return true;
            }
        }
        return false;
    }

    private static string? GetWorldModelPath(IArmorAddonGetter armaGetter, Sex sex)
    {
        if (armaGetter.WorldModel == null) return null;
        var model = sex == Sex.Female ? armaGetter.WorldModel.Female : armaGetter.WorldModel.Male;
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
        IArmorAddonGetter armaGetter, Sex sex, ILinkCache linkCache, NpcResolutionContext? context, FormKey armaFormKey)
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (armaGetter.SkinTexture == null) return result;
            var skinTextureLink = sex == Sex.Female ? armaGetter.SkinTexture.Female : armaGetter.SkinTexture.Male;
            if (skinTextureLink == null || skinTextureLink.IsNull) return result;
            var txst = ResolveRecord<ITextureSetGetter>(skinTextureLink, linkCache, context);
            if (txst == null)
            {
                LogVerbose("CharacterViewer: Could not resolve TXST " + skinTextureLink.FormKey + " from ARMA " + armaFormKey);
                return result;
            }

            void TryAdd(int slot, string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string fullPath = path;
                if (!fullPath.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) &&
                    !fullPath.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                {
                    fullPath = "textures\\" + fullPath;
                }
                result[slot] = fullPath;
            }

            TryAdd(0, txst.Diffuse?.DataRelativePath.Path);
            TryAdd(1, txst.NormalOrGloss?.DataRelativePath.Path);
            TryAdd(2, txst.GlowOrDetailMap?.DataRelativePath.Path);
            TryAdd(3, txst.Height?.DataRelativePath.Path);
            TryAdd(4, txst.Environment?.DataRelativePath.Path);
            TryAdd(5, txst.Multilayer?.DataRelativePath.Path);
            TryAdd(7, txst.BacklightMaskOrSpecular?.DataRelativePath.Path);
        }
        catch (Exception ex)
        {
            LogVerbose("CharacterViewer: TXST resolution failed for ARMA " + armaFormKey + ": " + ex.Message);
        }
        return result;
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

    private string? ResolveSkeletonPath(INpcGetter npcGetter, Sex sex, ILinkCache linkCache, NpcResolutionContext? context)
    {
        if (npcGetter.Race == null || npcGetter.Race.IsNull) return null;
        var raceGetter = ResolveRecord<IRaceGetter>(npcGetter.Race, linkCache, context);
        if (raceGetter?.SkeletalModel == null) return null;
        var skelModel = sex == Sex.Female ? raceGetter.SkeletalModel.Female : raceGetter.SkeletalModel.Male;
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
