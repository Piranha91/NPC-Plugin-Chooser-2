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
            finalHeadPath, skeletonPath, finalFaceTintPath, txstTextures);

        return new ResolvedNpcMeshPaths
        {
            BodyMeshPath = bodyPath,
            HandsMeshPath = handsPath,
            FeetMeshPath = feetPath,
            HeadMeshPath = finalHeadPath,
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
    //  Asset-resolution diagnostics
    // ─────────────────────────────────────────────────────────────────────

    private void TraceAssetResolution(FormKey npcFormKey, string npcName, NpcResolutionContext? context,
        string? body, string? hands, string? feet, string head, string? skeleton, string? faceTint,
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
    /// can roll up "any missing" for the summary line).</summary>
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

        // Game-relative: the renderer will check vanilla data folder, then BSAs.
        // Check both ourselves and report back what it'll find.
        string dataFolder = _env.DataFolderPath.ToString();
        string vanillaFull = Path.Combine(dataFolder, path);
        if (File.Exists(vanillaFull))
        {
            WriteTrace($"[AssetResolution]   [{label}] OK (vanilla loose): {vanillaFull}");
            return false;
        }

        string? bsaPath = null;
        var indexedKeys = _bsaHandler.GetIndexedModKeys();
        if (indexedKeys.Count > 0 && _bsaHandler.FileExists(path, indexedKeys, out _, out bsaPath) && bsaPath != null)
        {
            WriteTrace($"[AssetResolution]   [{label}] OK (BSA): {bsaPath} :: {path}");
            return false;
        }

        // Truly missing — dump every path the renderer would have checked, so
        // the user can quickly spot which mod is supposed to ship the asset
        // and verify whether it's actually installed.
        var missSb = new System.Text.StringBuilder();
        missSb.Append("[AssetResolution]   [").Append(label).Append("] !! MISSING\n");
        missSb.Append("    game-relative: ").Append(path).Append('\n');
        missSb.Append("    vanilla disk:  ").Append(vanillaFull).Append('\n');
        if (context != null && context.PreferredFolderPaths.Count > 0)
        {
            missSb.Append("    mod folders checked (last wins):\n");
            for (int i = context.PreferredFolderPaths.Count - 1; i >= 0; i--)
            {
                var folder = context.PreferredFolderPaths[i];
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var modCandidate = Path.Combine(folder, path);
                missSb.Append("      ").Append(modCandidate).Append('\n');
            }
        }
        missSb.Append("    BSAs scanned:  ").Append(indexedKeys.Count).Append(" indexed mod key(s)");
        WriteTrace(missSb.ToString());
        return true;
    }

    private static void WriteTrace(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        System.Diagnostics.Trace.WriteLine(message);
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
        if (context != null && context.PreferredModKeys.Count > 0)
        {
            // Disambiguation key wins when the mod's plugin set spans multiple
            // candidates for this NPC (mirrors Patcher.cs:292-298).
            if (context.DisambiguationModKey is { } disKey &&
                _recordHandler.TryGetRecordGetterFromMod(link, disKey, context.FallBackFolderNames,
                    RecordHandler.RecordLookupFallBack.None, out var disRec) &&
                disRec is T disT)
            {
                return disT;
            }

            // Iterate CorrespondingModKeys in the order recorded by the user
            // (last entry wins per NPC2's convention). reverseOrder: true makes
            // TryGetRecordFromMods walk the list backwards.
            if (_recordHandler.TryGetRecordFromMods(link, context.PreferredModKeys, context.FallBackFolderNames,
                    RecordHandler.RecordLookupFallBack.Winner, out var rec, reverseOrder: true) &&
                rec is T t)
            {
                return t;
            }
        }
        if (linkCache.TryResolve<T>(link.FormKey, out var lcRec))
        {
            return lcRec;
        }
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
