using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Patch-time wig→HeadPart conversion (<see cref="WigHandlingMode.ConvertToHeadParts"/>).
/// Discards the wig armor system entirely and delivers the wig as head parts:
/// mints one parent HDPT (Type=Hair, carries the Model) plus one IsExtraPart HDPT
/// per additional wig render shape, replaces the donor's Hair-type head parts
/// with the parent on the patched NPC record, and hands the Patcher a bake
/// instruction (<see cref="NifHandler.BakeWigIntoFaceGen"/>) that merges the wig
/// scene into the copied FaceGen NIF after the asset copy completes. The record
/// shape minted here is engine-proven — it ports
/// <c>Tests/Unit/WigHeadPartSpikeGeneratorTests.GenerateSpikeModFolder</c>, the
/// spike package the user validated in game (full SMP wig from facegen, tint
/// correct). The engine invariants that shape every decision here (EDID == baked
/// shape name, every baked part needs a Model, ExtraParts stripped recursively,
/// …) are documented on <see cref="NifHandler.BakeWigIntoFaceGen"/>.
///
/// <para>Mirrors <see cref="WigForwarder"/>'s lifecycle: <see cref="Apply"/> per
/// NPC before the appearance merge, <see cref="FinalizeNpcRecord"/> after
/// CopyAppearanceData (or right after surrogate creation in the SkyPatcher
/// Create path), <see cref="ResetCache"/> per appearance-mod batch. Minted HDPT
/// sets are cached per (wig ARMO, resolved NIF path) so all NPCs sharing a wig
/// share one record set and identical baked shape names.</para>
///
/// <para><b>Two wig sources.</b> The original source is the donor OUTFIT's wig
/// ARMO (<see cref="ModSetting.DetectedWigArmors"/>). The second source is a
/// skin-carried wig: an effective wig ArmorAddon
/// (<see cref="Settings.IsWigArmature"/>) carried directly in the donor's
/// WornArmor (WNAM) — the High Poly NPC Overhaul pattern (bald FaceGen + skin
/// hair ARMA). An outfit wig takes precedence (its path is engine-proven); any
/// skin-carried wig ARMAs are then reported via
/// <see cref="Result.WnamArmatureKeysToStrip"/> so the forwarder strips them
/// from the WNAM duplicate (they would double-render against the baked wig).</para>
///
/// <para><b>Per-NPC fallback (outfit source only):</b> when the conversion
/// would be risky — no donor Hair head part to harvest dismember partitions
/// from (the bake transplants the donor hair's partition entry; without one the
/// baked shapes keep their source skin-instance types and may dark-face), an
/// unresolvable wig NIF, zero render shapes, or an ambiguous multi-wig outfit —
/// <see cref="Apply"/> returns null with <c>fallBackToForwardToSkin</c> set,
/// and the Patcher routes that NPC through the proven <see cref="WigForwarder"/>
/// ForwardToSkin flow instead (which itself falls back to ForwardToOutfit when
/// the donor has no WNAM). A WNAM-source decline NEVER sets the fallback: a
/// skin-carried wig is already in its ForwardToSkin end state, so declining
/// just leaves the donor's correct in-game state. The WNAM source also
/// tolerates a bald donor (empty hair-removal set is legal — the minted parent
/// simply becomes the Hair part) by synthesizing the SBP_131_HAIR partition
/// template in the bake
/// (<see cref="NifHandler.WigBakeInstruction"/>.SynthesizeHairPartitionIfNoDonor).</para>
/// </summary>
public class HeadPartWigConverter
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly RecordHandler _recordHandler;
    private readonly BsaHandler _bsaHandler;
    private readonly Settings _settings;

    /// <summary>Prefix of every minted HeadPart EditorID (and therefore every
    /// baked FaceGen shape name): NPC2Wig_&lt;sanitized wig EDID&gt;_&lt;F|M&gt;_&lt;sanitized
    /// shape name&gt;. The F/M token exists because minted parts must be
    /// single-gender (see <see cref="GetOrMintWigSet"/>) so a unisex wig mints
    /// twin sets whose EDIDs may not collide.</summary>
    public const string MintedEditorIdPrefix = "NPC2Wig_";

    /// <summary>Output-owned folder the rewritten SMP physics XMLs are emitted
    /// to (data-relative). The baked FaceGen's physics extra-data is repointed
    /// here; per-shape entries in the XML are renamed in lockstep with the
    /// baked shape renames (see <see cref="SmpXmlRewriter"/>).</summary>
    public const string PhysicsXmlOutputFolder = @"meshes\NPC2\WigPhysics";

    // HeadPartsAllRacesMinusBeast [FLST:0A803F] — the same ValidRaces list the
    // bald-hair mint and the engine-proven spike package use.
    private static readonly FormKey HeadPartsAllRacesMinusBeastKey = FormKey.Factory("0A803F:Skyrim.esm");

    // Per appearance-mod-batch reuse cache (reset alongside WigForwarder.ResetCache):
    // NPCs sharing the same wig ARMO + resolved NIF + sex share one minted HDPT set
    // and identical baked shape names. The NIF path is part of the key because the
    // ARMA WorldModel is per-sex — a wig serving both sexes with distinct meshes
    // mints one set per mesh (cache keys must include scope context). Sex is part of
    // the key because minted parts must be single-gender (see GetOrMintWigSet), so a
    // same-mesh unisex wig mints twin sets.
    private readonly Dictionary<(FormKey WigKey, string NifPath, bool Female), MintedWigSet> _mintedSets = new();

    // Session-scoped guards (reset on ResetSession, i.e. once per patch run):
    // rename-prefix → NIF path (two different wigs sharing an EditorID must not
    // mint colliding EDIDs) and the emitted physics-XML rel paths.
    private readonly Dictionary<string, string> _renamePrefixOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedPhysicsXmlRelPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private string? _tempExtractDir;

    // NIF-probe seams (unit tests stub these; production uses NifHandler).
    internal Func<string, IReadOnlyList<string>> RenderShapeNamesProvider { get; set; }
        = NifHandler.GetRenderShapeNames;
    internal Func<string, IReadOnlyCollection<string>, bool> PartitionProbe { get; set; }
        = NifHandler.HasShapeWithPartitions;
    internal Func<string, IReadOnlyCollection<string>> PhysicsXmlProvider { get; set; }
        = path => NifHandler.GetPhysicsXmlPathsFromNif(path);

    public HeadPartWigConverter(EnvironmentStateProvider environmentStateProvider, RecordHandler recordHandler,
        BsaHandler bsaHandler, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _recordHandler = recordHandler;
        _bsaHandler = bsaHandler;
        _settings = settings;
        HeadPartRaceAllowedProbe = IsRaceInHeadPartsAllRacesMinusBeast;
    }

    /// <summary>Whether a race may wear the minted parts
    /// (<see cref="Result"/> ValidRaces = HeadPartsAllRacesMinusBeast) —
    /// the WNAM source's beast-race guard. Seam for tests; production resolves
    /// the FLST via the link cache (unresolvable counts as allowed).</summary>
    internal Func<FormKey, bool> HeadPartRaceAllowedProbe { get; set; }

    private HashSet<FormKey>? _headPartAllowedRaces;
    private bool _headPartAllowedRacesResolved;

    private bool IsRaceInHeadPartsAllRacesMinusBeast(FormKey raceKey)
    {
        lock (_lock)
        {
            if (!_headPartAllowedRacesResolved)
            {
                _headPartAllowedRacesResolved = true;
                var lc = _environmentStateProvider.LinkCache;
                if (lc != null &&
                    lc.TryResolve<IFormListGetter>(HeadPartsAllRacesMinusBeastKey, out var flst) &&
                    flst?.Items != null)
                {
                    _headPartAllowedRaces = flst.Items
                        .Where(i => i != null && !i.IsNull)
                        .Select(i => i.FormKey)
                        .ToHashSet();
                }
            }

            // Unresolvable FLST (no environment / missing master) → allow;
            // blocking on a resolution failure would wrongly decline every
            // conversion, and the engine checks ValidRaces at runtime anyway.
            return _headPartAllowedRaces == null || _headPartAllowedRaces.Contains(raceKey);
        }
    }

    /// <summary>One minted HDPT set, shared by every NPC wearing the same wig
    /// mesh within a batch.</summary>
    private sealed class MintedWigSet
    {
        public FormKey ParentKey;
        public string ParentEditorId = string.Empty;
        public List<MajorRecord> MintedRecords = new();
        public Dictionary<string, string> ShapeRenames = new(StringComparer.OrdinalIgnoreCase);
        public string WigNifSourcePath = string.Empty;
        public string WigNifDataRelPath = string.Empty;
        public string? PhysicsXmlSourcePath;
        public string? PhysicsXmlSourceDataRelPath;
        public string? PhysicsXmlNewDataRelPath;
    }

    public sealed class Result
    {
        /// <summary>The minted Hair-type parent HDPT added to the patched NPC's
        /// HeadParts in <see cref="FinalizeNpcRecord"/>.</summary>
        public FormKey ParentHeadPartKey { get; init; }

        public string ParentEditorId { get; init; } = string.Empty;

        /// <summary>All minted HDPT records (parent + extras), shared across
        /// NPCs wearing the same wig — the Patcher registers per-NPC ownership
        /// for rollback accounting.</summary>
        public IReadOnlyList<MajorRecord> MintedRecords { get; init; } = Array.Empty<MajorRecord>();

        /// <summary>Wig NIF shape name → minted HeadPart EditorID (== the baked
        /// FaceGen shape name; the engine reconciles records against baked
        /// shapes by name).</summary>
        public IReadOnlyDictionary<string, string> ShapeRenames { get; init; }
            = new Dictionary<string, string>();

        /// <summary>Donor-side FormKeys of the Hair-type head parts replaced by
        /// the minted parent. Removed from the patched NPC record in
        /// <see cref="FinalizeNpcRecord"/> (expanded through the merge's
        /// duplicate mappings). NO bald back-fill — the wig parent IS the Hair
        /// part.</summary>
        public HashSet<FormKey> DonorHairHeadPartKeys { get; } = new();

        /// <summary>Baked FaceGen shape names the bake strips before merging the
        /// wig in: the removed hair head parts' EditorIDs plus their ExtraParts'
        /// EditorIDs recursively (an orphan baked shape dark-faces,
        /// engine-verified). Passed to the bake instruction — NOT also queued
        /// through the Patcher's RemoveShapesByName strip.</summary>
        public HashSet<string> FaceGenShapeNamesToStrip { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Absolute, readable path of the wig NIF the bake merges in
        /// (a mod loose file, or a temp-extracted BSA copy that lives for the
        /// whole patch run).</summary>
        public string WigNifSourcePath { get; init; } = string.Empty;

        /// <summary>Data-relative path of the wig NIF (meshes\…) — the asset
        /// copy destination; the minted parts' Model records point at the same
        /// file so it must ship in the output.</summary>
        public string WigNifDataRelPath { get; init; } = string.Empty;

        /// <summary>Absolute, readable path of the wig's SMP physics XML (null
        /// for non-SMP wigs).</summary>
        public string? PhysicsXmlSourcePath { get; init; }

        /// <summary>Data-relative source path of the physics XML (for include
        /// scanning / provenance).</summary>
        public string? PhysicsXmlSourceDataRelPath { get; init; }

        /// <summary>Output-owned data-relative path the rewritten physics XML is
        /// emitted to; the bake repoints the FaceGen's physics extra-data here.
        /// Null for non-SMP wigs.</summary>
        public string? PhysicsXmlNewDataRelPath { get; init; }

        /// <summary>ARMA FormKeys of donor-WNAM wig armatures superseded by this
        /// conversion — removed from the WNAM duplicate by
        /// <see cref="WigForwarder.BuildSkinDuplicate"/> (the forwarder stays
        /// sole owner of the duplicate; the Patcher threads this set into
        /// <see cref="WigForwarder.Apply"/>). Populated on BOTH source paths:
        /// the WNAM source's converted ARMA, and (outfit source) any effective
        /// skin-carried wig ARMAs that would double-render against the baked
        /// outfit wig.</summary>
        public HashSet<FormKey> WnamArmatureKeysToStrip { get; } = new();

        /// <summary>True when the bake should synthesize the SBP_131_HAIR
        /// partition template because the donor has no harvestable hair shape
        /// (the WNAM bald-donor pattern). Threaded into
        /// <see cref="NifHandler.WigBakeInstruction"/>.</summary>
        public bool SynthesizeHairPartitionTemplate { get; init; }

        /// <summary>This mod's effective <see cref="Models.WigHairTintMode"/> —
        /// whether the bake re-tints the wig with the NPC's hair color once it
        /// leaves skee64's reach. Threaded into
        /// <see cref="NifHandler.WigBakeInstruction"/>.</summary>
        public Models.WigHairTintMode HairTintMode { get; init; } = Models.WigHairTintMode.Auto;

        /// <summary>The donor NPC's resolved HCLR as sRGB 0..1 — the color
        /// skee64 applied to the wig while it was still worn, and so what the
        /// bake writes into it. Null when the NPC has no hair color record; the
        /// bake then harvests the FaceGen's own hair tint instead.</summary>
        public (float R, float G, float B)? HairTintRgb { get; init; }
    }

    /// <summary>The NPC's hair color (HCLR → CLFM) as sRGB 0..1 — the same
    /// space <c>BSLightingShaderProperty.hairTintColor</c> is stored in, so the
    /// byte channels divide straight through with no gamma rebase (mirrors
    /// <c>NpcMeshResolver</c>'s resolve for the renderer). Read off the record whose
    /// appearance the output carries (HairColor is Traits-governed, so under a flatten
    /// that is the terminus).</summary>
    private (float R, float G, float B)? ResolveHairColorRgb(
        INpcGetter appearanceNpc, ModSetting appearanceModSetting, HashSet<string> modFolderPaths)
    {
        if (appearanceNpc.HairColor == null || appearanceNpc.HairColor.IsNull) return null;
        var hclr = ResolveFromModsOrWinner<IColorRecordGetter>(appearanceNpc.HairColor,
            appearanceModSetting.CorrespondingModKeys, modFolderPaths);
        if (hclr == null) return null;
        var c = hclr.Color;
        return (c.R / 255f, c.G / 255f, c.B / 255f);
    }

    /// <summary>
    /// Evaluates the wig→HeadPart conversion for one NPC: identifies the wig in
    /// the donor's outfit, resolves the weight/sex-matched wig NIF, mints (or
    /// reuses) the HDPT set, and collects the donor hair removal. Returns null
    /// when there is nothing to convert (no detected wig in the donor outfit) —
    /// <paramref name="fallBackToForwardToSkin"/> is then false — or when the
    /// conversion would be risky — <paramref name="fallBackToForwardToSkin"/> is
    /// true and the caller must route this NPC through
    /// <see cref="WigForwarder"/> with ForwardToSkin instead. Must run BEFORE
    /// CopyAppearanceData; the caller invokes <see cref="FinalizeNpcRecord"/> on
    /// the patched NPC afterwards and queues the bake after the FaceGen copy.
    /// </summary>
    /// <param name="faceGenSubjectFormKey">
    /// The FormKey whose FaceGen mesh will be copied onto this NPC — the bake base. Normally the
    /// donor's own, but when a Traits chain is being FLATTENED the mesh comes from the chain
    /// terminus (measured there by the FaceGen ladder) and lands at the NPC's own path, so the
    /// probes below have to read the terminus's copy or they find nothing and decline. Null =
    /// the donor's own FormKey, which is also the right answer whenever no flatten is happening:
    /// a templated NPC that keeps inheriting genuinely has no FaceGen of its own to bake into.
    /// </param>
    /// <param name="flattenTerminusNpc">
    /// The chain terminus record when a Traits chain is being FLATTENED, else null. Every
    /// Traits-governed input below (race, sex, weight, hair colour, WornArmor, head parts — the
    /// set <see cref="Auxilliary.CopyInheritedAppearance"/> defines) must be read off THIS record,
    /// because that is what the output NPC ends up carrying. Reading the donor instead picked the
    /// wrong sex/weight wig variant and collected hair head parts the flatten had already replaced,
    /// so the removal in <see cref="FinalizeNpcRecord"/> matched nothing and the terminus's hair
    /// survived alongside the minted wig.
    ///
    /// <para>Deliberately separate from <paramref name="faceGenSubjectFormKey"/> even though they
    /// share a gate: the ladder can resolve the chain (so the bake target IS the terminus's mesh)
    /// while the terminus RECORD fails to resolve from the mod, in which case no flatten happens
    /// and the donor's own fields are what the output carries. Two parameters keep this class
    /// field-for-field consistent with <c>CopyAppearanceData</c>.</para>
    /// </param>
    public Result? Apply(
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths,
        string npcIdentifier,
        Action<string, bool, bool> appendLog,
        out bool fallBackToForwardToSkin,
        FormKey? faceGenSubjectFormKey = null,
        INpcGetter? flattenTerminusNpc = null)
    {
        fallBackToForwardToSkin = false;
        var faceGenKey = faceGenSubjectFormKey ?? donorNpc.FormKey;

        // The record whose Traits-governed appearance the OUTPUT will carry. Under a flatten that
        // is the terminus; otherwise the donor is its own. DefaultOutfit is NOT in this set — it is
        // Inventory-governed, and the patcher copies the donor's — so it keeps reading donorNpc.
        var appearanceNpc = flattenTerminusNpc ?? donorNpc;

        // Applicable wigs = the donor outfit's direct items the scan classified
        // as wigs (same detection basis as WigForwarder — outfit-item based).
        var donorOutfit = ResolveFromModsOrWinner<IOutfitGetter>(donorNpc.DefaultOutfit,
            appearanceModSetting.CorrespondingModKeys, modFolderPaths);
        var wigItemKeys = new List<FormKey>();
        if (donorOutfit?.Items != null)
        {
            foreach (var item in donorOutfit.Items)
            {
                if (item == null || item.IsNull) continue;
                if (appearanceModSetting.DetectedWigArmors.Contains(item.FormKey) &&
                    !appearanceModSetting.DetectedAntlerArmors.Contains(item.FormKey))
                {
                    wigItemKeys.Add(item.FormKey);
                }
            }
        }

        // WNAM source: effective wig ARMAs carried directly in the donor's skin
        // (the High Poly NPC Overhaul pattern). Collected regardless of which
        // source converts: when an outfit wig converts, a skin-carried wig ARMA
        // must still be stripped from the WNAM duplicate or both would render.
        var wnamWigArmas = CollectWnamWigArmas(appearanceNpc, donorNpc, appearanceModSetting, modFolderPaths);

        if (wigItemKeys.Count == 0)
        {
            if (wnamWigArmas.Count == 0) return null; // nothing to convert; not a fallback

            // Every WNAM-source decline keeps fallBackToForwardToSkin=false: a
            // skin-carried wig is already in its ForwardToSkin end state, so a
            // declined conversion just leaves the donor's correct in-game state.
            return ApplyWnamConversion(appearanceNpc, donorNpc, appearanceModSetting, modFolderPaths,
                npcIdentifier, appendLog, wnamWigArmas, faceGenKey);
        }

        if (wigItemKeys.Count > 1)
        {
            appendLog($"      Wig conversion: {npcIdentifier}'s outfit contains {wigItemKeys.Count} detected wigs — " +
                      "only a single wig can become the NPC's Hair head part. Falling back to ForwardToSkin.",
                false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        var wigKey = wigItemKeys[0];
        var wigArmor = ResolveFromModsOrWinner<IArmorGetter>(wigKey.ToLink<IArmorGetter>(),
            appearanceModSetting.CorrespondingModKeys, modFolderPaths);
        if (wigArmor == null)
        {
            appendLog($"      Wig conversion: could not resolve wig ARMO {wigKey} for {npcIdentifier}. " +
                      "Falling back to ForwardToSkin.", false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        // 1. Donor hair removal — collected FIRST because it is also the bake's
        //    partition-donor requirement: the bake transplants dismember
        //    partition data from a stripped donor hair shape. A donor with no
        //    Hair-type head part has nothing to harvest → risky bake → fallback.
        var donorHairKeys = new HashSet<FormKey>();
        var stripNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectHairRemoval(appearanceNpc, appearanceModSetting, modFolderPaths, donorHairKeys, stripNames);
        if (donorHairKeys.Count == 0 || stripNames.Count == 0)
        {
            appendLog($"      Wig conversion: {npcIdentifier} has no donor Hair head part to harvest FaceGen " +
                      "dismember partitions from (bald-donor pattern) — the bake would be risky. " +
                      "Falling back to ForwardToSkin.", false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        // 2. Resolve the worn wig NIF: hair-slot ARMA for the donor's race/sex,
        //    weight-matched _0/_1 variant (nearest, no interpolation).
        string? wigNifRecordPath = ResolveWigNifRecordPath(wigArmor, appearanceNpc, appearanceModSetting,
            modFolderPaths, npcIdentifier, appendLog);
        if (wigNifRecordPath == null)
        {
            fallBackToForwardToSkin = true;
            return null;
        }

        if (!Auxilliary.TryRegularizePath(wigNifRecordPath, out var wigNifDataRelPath))
        {
            appendLog($"      Wig conversion: could not regularize wig NIF path '{wigNifRecordPath}' for " +
                      $"{npcIdentifier}. Falling back to ForwardToSkin.", false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        string? wigNifSourcePath = MaterializeDataRelFile(wigNifDataRelPath, appearanceModSetting);
        if (wigNifSourcePath == null)
        {
            appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' was not found in " +
                      $"'{appearanceModSetting.DisplayName}' (loose or BSA) for {npcIdentifier}. " +
                      "Falling back to ForwardToSkin.", false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        // 3. Partition probe on the donor FaceGen (source-side; the output copy
        //    is byte-identical). Without a strippable hair shape carrying
        //    dismember partitions the bake keeps source skin-instance types —
        //    dark-face risk — so fall back instead.
        var (donorFaceGenRelPath, _) = Auxilliary.GetFaceGenSubPathStrings(faceGenKey, regularized: true);
        string? donorFaceGenPath = MaterializeDataRelFile(donorFaceGenRelPath, appearanceModSetting);
        if (donorFaceGenPath == null)
        {
            LogMissingDonorFaceGen(donorNpc, npcIdentifier, "Falling back to ForwardToSkin.", appendLog,
                flattening: flattenTerminusNpc != null);
            fallBackToForwardToSkin = true;
            return null;
        }

        if (!PartitionProbe(donorFaceGenPath, stripNames))
        {
            appendLog($"      Wig conversion: {npcIdentifier}'s donor FaceGen has no hair shape with dismember " +
                      "partitions — the bake cannot normalize the wig shapes to CK skin data. " +
                      "Falling back to ForwardToSkin.", false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        // 4. Mint (or reuse) the per-(wig, sex) HDPT set.
        MintedWigSet? set = GetOrMintWigSet(wigArmor.EditorID, wigKey, wigNifSourcePath, wigNifDataRelPath,
            wigNifRecordPath, appearanceNpc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female),
            appearanceModSetting, npcIdentifier, appendLog);
        if (set == null)
        {
            fallBackToForwardToSkin = true;
            return null;
        }

        var result = new Result
        {
            ParentHeadPartKey = set.ParentKey,
            ParentEditorId = set.ParentEditorId,
            MintedRecords = set.MintedRecords,
            ShapeRenames = set.ShapeRenames,
            WigNifSourcePath = set.WigNifSourcePath,
            WigNifDataRelPath = set.WigNifDataRelPath,
            PhysicsXmlSourcePath = set.PhysicsXmlSourcePath,
            PhysicsXmlSourceDataRelPath = set.PhysicsXmlSourceDataRelPath,
            PhysicsXmlNewDataRelPath = set.PhysicsXmlNewDataRelPath,
            HairTintMode = _settings.GetEffectiveWigHairTintMode(appearanceModSetting),
            HairTintRgb = ResolveHairColorRgb(appearanceNpc, appearanceModSetting, modFolderPaths),
        };
        result.DonorHairHeadPartKeys.UnionWith(donorHairKeys);
        result.FaceGenShapeNamesToStrip.UnionWith(stripNames);

        // A skin-carried wig ARMA alongside the converted outfit wig would
        // double-render against the baked shapes — strip it from the WNAM
        // duplicate.
        foreach (var wnamArma in wnamWigArmas) result.WnamArmatureKeysToStrip.Add(wnamArma.FormKey);

        appendLog($"      Wig conversion: {npcIdentifier} → wig '{wigArmor.EditorID ?? wigKey.ToString()}' " +
                  $"as head parts ('{set.ParentEditorId}' + {set.ShapeRenames.Count - 1} extra(s)); " +
                  $"donor hair {string.Join(", ", stripNames)} will be stripped and the wig baked into the " +
                  "copied FaceGen after asset copy.", false, false);
        return result;
    }

    /// <summary>Effective wig ArmorAddons (<see cref="Settings.IsWigArmature"/>)
    /// carried in <paramref name="appearanceNpc"/>'s WornArmor — the terminus's under a flatten,
    /// since that is the skin the output record ends up with. Empty when it has no WNAM.
    /// <paramref name="donorNpc"/> supplies only the manual-designation scope key, which stays
    /// keyed to the NPC the user picked in the UI.</summary>
    private List<IArmorAddonGetter> CollectWnamWigArmas(INpcGetter appearanceNpc, INpcGetter donorNpc,
        ModSetting appearanceModSetting, HashSet<string> modFolderPaths)
    {
        if (appearanceNpc.WornArmor.IsNull) return new List<IArmorAddonGetter>();
        var wnam = ResolveFromModsOrWinner<IArmorGetter>(appearanceNpc.WornArmor,
            appearanceModSetting.CorrespondingModKeys, modFolderPaths);

        // No race filter here on purpose — ApplyWnamConversion applies it afterwards, and it needs
        // the unfiltered count to tell "this NPC's race isn't served" apart from "there is no wig".
        return WigDetector.EffectiveWnamWigArmatures(
            wnam,
            link => ResolveFromModsOrWinner<IArmorAddonGetter>(link.FormKey.ToLink<IArmorAddonGetter>(),
                appearanceModSetting.CorrespondingModKeys, modFolderPaths),
            arma => _settings.IsWigArmature(appearanceModSetting, arma.FormKey, arma.EditorID,
                donorNpc.FormKey))
            .ToList();
    }

    /// <summary>
    /// The WNAM (skin-carried) wig source: converts the donor skin's single
    /// effective wig ARMA into the NPC's Hair head part. Differences from the
    /// outfit path: a bald donor is LEGAL (empty hair-removal set — the minted
    /// parent simply becomes the Hair part, and the bake synthesizes the
    /// SBP_131_HAIR partition template instead of harvesting), a beast-race
    /// donor is declined (the minted parent's ValidRaces excludes beast races),
    /// and every decline returns null WITHOUT the ForwardToSkin fallback (the
    /// wig is already in its end state; declining preserves the donor's correct
    /// in-game appearance).
    /// </summary>
    private Result? ApplyWnamConversion(INpcGetter appearanceNpc, INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, string npcIdentifier, Action<string, bool, bool> appendLog,
        List<IArmorAddonGetter> wnamWigArmas, FormKey faceGenKey)
    {
        var raceKey = appearanceNpc.Race.IsNull ? (FormKey?)null : appearanceNpc.Race.FormKey;
        var armorRaceKey = ResolveArmorRaceKey(appearanceNpc,
            appearanceModSetting.CorrespondingModKeys, modFolderPaths);
        var applicable = wnamWigArmas.Where(a => IsArmatureForRace(a, raceKey, armorRaceKey)).ToList();
        if (applicable.Count == 0)
        {
            appendLog($"      Wig conversion: {npcIdentifier}'s skin-carried wig ArmorAddon(s) are not " +
                      "applicable to the NPC's race — leaving the skin-carried hair as-is.", false, true);
            return null;
        }

        if (applicable.Count > 1)
        {
            appendLog($"      Wig conversion: {npcIdentifier}'s skin carries {applicable.Count} wig " +
                      "ArmorAddons — only a single wig can become the Hair head part. Leaving the " +
                      "skin-carried hair as-is (marking the extras as 'not a wig' in the 3D preview's " +
                      "Set Wig Meshes selector can thin the set to one).", false, true);
            return null;
        }

        var arma = applicable[0];

        // Beast-race guard: the minted parent's ValidRaces is
        // HeadPartsAllRacesMinusBeast; converting a beast-race NPC would leave
        // it with no valid Hair head part (engine back-fills or bares the head).
        if (raceKey != null && !HeadPartRaceAllowedProbe(raceKey.Value))
        {
            appendLog($"      Wig conversion: {npcIdentifier}'s race is not in HeadPartsAllRacesMinusBeast — " +
                      "a minted Hair head part would be invalid for it. Leaving the skin-carried hair as-is.",
                false, true);
            return null;
        }

        // Donor hair removal — EMPTY IS LEGAL for this source (typically a bald
        // FaceGen): the minted parent then becomes the NPC's Hair part with
        // nothing to strip or remove.
        var donorHairKeys = new HashSet<FormKey>();
        var stripNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectHairRemoval(appearanceNpc, appearanceModSetting, modFolderPaths, donorHairKeys, stripNames);

        string armaLabel = $"skin-carried wig ArmorAddon '{arma.EditorID ?? arma.FormKey.ToString()}'";
        string? wigNifRecordPath = ResolveArmaNifRecordPath(arma, appearanceNpc, appearanceModSetting,
            npcIdentifier, armaLabel, appendLog);
        if (wigNifRecordPath == null) return null;

        if (!Auxilliary.TryRegularizePath(wigNifRecordPath, out var wigNifDataRelPath))
        {
            appendLog($"      Wig conversion: could not regularize wig NIF path '{wigNifRecordPath}' for " +
                      $"{npcIdentifier}. Leaving the skin-carried hair as-is.", false, true);
            return null;
        }

        string? wigNifSourcePath = MaterializeDataRelFile(wigNifDataRelPath, appearanceModSetting);
        if (wigNifSourcePath == null)
        {
            appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' was not found in " +
                      $"'{appearanceModSetting.DisplayName}' (loose or BSA) for {npcIdentifier}. " +
                      "Leaving the skin-carried hair as-is.", false, true);
            return null;
        }

        // The donor FaceGen must exist — it is the bake target.
        var (donorFaceGenRelPath, _) = Auxilliary.GetFaceGenSubPathStrings(faceGenKey, regularized: true);
        string? donorFaceGenPath = MaterializeDataRelFile(donorFaceGenRelPath, appearanceModSetting);
        if (donorFaceGenPath == null)
        {
            LogMissingDonorFaceGen(donorNpc, npcIdentifier, "Leaving the skin-carried hair as-is.", appendLog,
                flattening: !ReferenceEquals(appearanceNpc, donorNpc));
            return null;
        }

        // Partition template: harvest when the donor has strippable hair WITH
        // dismember partitions; otherwise the bake synthesizes SBP_131_HAIR
        // (engine invariants verified by spike variant I).
        bool synthesize = stripNames.Count == 0 || !PartitionProbe(donorFaceGenPath, stripNames);

        MintedWigSet? set = GetOrMintWigSet(arma.EditorID, arma.FormKey, wigNifSourcePath, wigNifDataRelPath,
            wigNifRecordPath, appearanceNpc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female),
            appearanceModSetting, npcIdentifier, appendLog);
        if (set == null) return null;

        var result = new Result
        {
            ParentHeadPartKey = set.ParentKey,
            ParentEditorId = set.ParentEditorId,
            MintedRecords = set.MintedRecords,
            ShapeRenames = set.ShapeRenames,
            WigNifSourcePath = set.WigNifSourcePath,
            WigNifDataRelPath = set.WigNifDataRelPath,
            PhysicsXmlSourcePath = set.PhysicsXmlSourcePath,
            PhysicsXmlSourceDataRelPath = set.PhysicsXmlSourceDataRelPath,
            PhysicsXmlNewDataRelPath = set.PhysicsXmlNewDataRelPath,
            SynthesizeHairPartitionTemplate = synthesize,
            HairTintMode = _settings.GetEffectiveWigHairTintMode(appearanceModSetting),
            HairTintRgb = ResolveHairColorRgb(appearanceNpc, appearanceModSetting, modFolderPaths),
        };
        result.DonorHairHeadPartKeys.UnionWith(donorHairKeys);
        result.FaceGenShapeNamesToStrip.UnionWith(stripNames);
        result.WnamArmatureKeysToStrip.Add(arma.FormKey);

        appendLog($"      Wig conversion: {npcIdentifier} → {armaLabel} as head parts " +
                  $"('{set.ParentEditorId}' + {set.ShapeRenames.Count - 1} extra(s)); " +
                  (stripNames.Count > 0
                      ? $"donor hair {string.Join(", ", stripNames)} will be stripped and "
                      : "bald donor (synthesized hair partition) — ") +
                  "the wig is baked into the copied FaceGen after asset copy; the skin duplicate loses the " +
                  "converted ArmorAddon.", false, false);
        return result;
    }

    /// <summary>
    /// Reports a conversion decline caused by a missing donor FaceGen. An NPC
    /// that inherits Traits from a template has NO FaceGen of its own by design
    /// — the engine renders its template's face — so for those the decline is
    /// expected rather than a problem, and it is logged verbose-only. Whole
    /// vanilla NPC classes are templated (generic encounter/leveled actors:
    /// Vigilants of Stendarr, Dremora, …), so forcing that line into the log
    /// buries the genuinely missing-FaceGen case it shares wording with — which
    /// stays a forced entry.
    ///
    /// <para>A FLATTENED chain is the exception to that exception: the donor still carries the
    /// Traits flag, but the ladder measured the terminus's FaceGen in this mod and the bake target
    /// is the NPC's own path, so a miss here IS a real problem and stays forced.</para>
    /// </summary>
    private static void LogMissingDonorFaceGen(INpcGetter donorNpc, string npcIdentifier, string outcome,
        Action<string, bool, bool> appendLog, bool flattening = false)
    {
        if (!flattening && Auxilliary.HasTraitsFlag(donorNpc))
        {
            string template = donorNpc.Template is { IsNull: false }
                ? donorNpc.Template.FormKey.ToString()
                : "(none)";
            appendLog($"      Wig conversion: {npcIdentifier} inherits Traits from template {template}, so it " +
                      $"has no FaceGen of its own to bake the wig into. {outcome}", false, false);
            return;
        }

        appendLog($"      Wig conversion: {npcIdentifier}'s donor FaceGen was not found — there is no " +
                  $"FaceGen to bake the wig into. {outcome}", false, true);
    }

    /// <summary>
    /// Applies a conversion result to the patched NPC record: removes the donor
    /// Hair-type head part links (expanded through the merge's duplicate
    /// mappings — CopyAppearanceData may have remapped them to output records)
    /// and adds the minted wig parent. Deliberately NO NPC2_HairBald back-fill:
    /// the wig parent IS the NPC's Hair-type head part. Call AFTER
    /// CopyAppearanceData in the record path, or right after surrogate creation
    /// (before the merge walker) in the SkyPatcher Create path — same contract
    /// as <see cref="WigForwarder.FinalizeNpcRecord"/>. The FaceGen work (hair
    /// strip + wig bake) happens later, after the asset copy completes (see the
    /// Patcher's pending wig bakes).
    /// </summary>
    public void FinalizeNpcRecord(Result result, Npc patchNpc, string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        var removeKeys = ExpandWithDuplicateMappings(result.DonorHairHeadPartKeys);
        int removed = patchNpc.HeadParts.RemoveAll(l => l != null && removeKeys.Contains(l.FormKey));

        if (patchNpc.HeadParts.All(l => l.FormKey != result.ParentHeadPartKey))
        {
            patchNpc.HeadParts.Add(result.ParentHeadPartKey.ToLink<IHeadPartGetter>());
        }

        appendLog($"      Wig conversion: replaced {removed} hair head part(s) on {npcIdentifier} with the " +
                  $"minted wig parent '{result.ParentEditorId}' ({result.ParentHeadPartKey}); the wig is baked " +
                  "into the FaceGen NIF after asset copy.", false, false);
    }

    /// <summary>Clears the per-batch minted-set cache. Call alongside
    /// <see cref="WigForwarder.ResetCache"/> — the minted records live in the
    /// output mod, but reuse must not leak across appearance-mod batches.</summary>
    public void ResetCache()
    {
        lock (_lock)
        {
            _mintedSets.Clear();
        }
    }

    /// <summary>Per-patch-run reset: clears the batch cache, the session-scoped
    /// EDID/XML collision guards, and the temp directory BSA-packed wig NIFs are
    /// extracted to (those files must survive until the post-copy bake drains,
    /// so they are only deleted at the START of the next run).</summary>
    public void ResetSession()
    {
        lock (_lock)
        {
            _mintedSets.Clear();
            _renamePrefixOwners.Clear();
            _usedPhysicsXmlRelPaths.Clear();
            _headPartAllowedRaces = null;
            _headPartAllowedRacesResolved = false;
        }

        try
        {
            string dir = GetTempExtractDir();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Leftover temp files are harmless; a locked file must not fail the run.
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Wig NIF resolution
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Resolves the record-style (no "meshes\" prefix) path of the wig
    /// NIF this NPC actually wears: the single race-applicable hair-slot ARMA's
    /// per-sex WorldModel with the weight-matched _0/_1 variant. Null (with a
    /// log line) when ambiguous or unresolvable — the caller falls back.</summary>
    private string? ResolveWigNifRecordPath(IArmorGetter wigArmor, INpcGetter appearanceNpc,
        ModSetting appearanceModSetting, HashSet<string> modFolderPaths, string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        IArmorAddonGetter? ResolveArma(FormKey fk) =>
            ResolveFromModsOrWinner<IArmorAddonGetter>(fk.ToLink<IArmorAddonGetter>(),
                appearanceModSetting.CorrespondingModKeys, modFolderPaths);

        var hairArmas = WigDetector.GetForwardableArmatures(wigArmor, isAntler: false, ResolveArma)
            .Select(l => ResolveArma(l.FormKey))
            .Where(a => a != null)
            .Select(a => a!)
            .ToList();

        if (hairArmas.Count == 0)
        {
            appendLog($"      Wig conversion: wig '{wigArmor.EditorID}' has no resolvable hair-slot ArmorAddon " +
                      $"for {npcIdentifier}. Falling back to ForwardToSkin.", false, true);
            return null;
        }

        if (hairArmas.Count > 1)
        {
            // Multiple hair ARMAs are usually per-race variants; keep the ones
            // applicable to the race the output record will carry.
            var raceKey = appearanceNpc.Race.IsNull ? (FormKey?)null : appearanceNpc.Race.FormKey;
            var armorRaceKey = ResolveArmorRaceKey(appearanceNpc,
                appearanceModSetting.CorrespondingModKeys, modFolderPaths);
            var raceMatched = hairArmas.Where(a => IsArmatureForRace(a, raceKey, armorRaceKey)).ToList();
            if (raceMatched.Count > 0) hairArmas = raceMatched;
        }

        if (hairArmas.Count > 1)
        {
            appendLog($"      Wig conversion: wig '{wigArmor.EditorID}' has {hairArmas.Count} applicable " +
                      $"hair-slot ArmorAddons for {npcIdentifier} — cannot pick a single wig mesh to bake. " +
                      "Falling back to ForwardToSkin.", false, true);
            return null;
        }

        return ResolveArmaNifRecordPath(hairArmas[0], appearanceNpc, appearanceModSetting, npcIdentifier,
            "wig ArmorAddon", appendLog);
    }

    /// <summary>Shared ARMA→NIF tail of both wig sources: the addon's per-sex
    /// WorldModel with the weight-matched _0/_1 variant. Null (with a log line)
    /// when unresolvable — each caller decides its own fallback semantics. Sex and weight
    /// are Traits-governed, so both come off the record whose appearance the output carries.
    /// </summary>
    private string? ResolveArmaNifRecordPath(IArmorAddonGetter arma, INpcGetter appearanceNpc,
        ModSetting appearanceModSetting, string npcIdentifier, string sourceLabel,
        Action<string, bool, bool> appendLog)
    {
        bool isFemale = Auxilliary.IsFemale(appearanceNpc);
        string? recordPath = GetWorldModelRecordPath(arma, female: isFemale)
                             ?? GetWorldModelRecordPath(arma, female: !isFemale); // shared/single-sex meshes
        if (recordPath == null)
        {
            appendLog($"      Wig conversion: {sourceLabel} {arma.FormKey} has no WorldModel path for " +
                      $"{npcIdentifier}.", false, true);
            return null;
        }

        // Weight-matched _0/_1 variant: >= 50 → _1, else _0 (nearest — no
        // interpolation, an accepted limitation). Fall back to whichever
        // variant actually exists; a suffix-less path is a single-weight mesh.
        string preferred = SwapWeightSuffix(recordPath, appearanceNpc.Weight >= 50f);
        foreach (var candidate in new[] { preferred, recordPath, SwapWeightSuffix(recordPath, appearanceNpc.Weight < 50f) })
        {
            if (Auxilliary.TryRegularizePath(candidate, out var rel) && DataRelFileExists(rel, appearanceModSetting))
            {
                return candidate;
            }
        }

        appendLog($"      Wig conversion: no weight variant of wig NIF '{recordPath}' exists in " +
                  $"'{appearanceModSetting.DisplayName}' for {npcIdentifier}.", false, true);
        return null;
    }

    private static string? GetWorldModelRecordPath(IArmorAddonGetter arma, bool female)
    {
        var model = female ? arma.WorldModel?.Female : arma.WorldModel?.Male;
        string? path = model?.File?.GivenPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>Swaps a trailing _0.nif/_1.nif weight suffix to the requested
    /// weight. A path without the suffix is returned unchanged.</summary>
    internal static string SwapWeightSuffix(string nifPath, bool wantHighWeight)
    {
        string want = wantHighWeight ? "_1.nif" : "_0.nif";
        string other = wantHighWeight ? "_0.nif" : "_1.nif";
        if (nifPath.EndsWith(want, StringComparison.OrdinalIgnoreCase)) return nifPath;
        if (nifPath.EndsWith(other, StringComparison.OrdinalIgnoreCase))
        {
            return nifPath.Substring(0, nifPath.Length - other.Length) + want;
        }
        return nifPath;
    }

    /// <summary>Race applicability mirror of the renderer's ARMA filter: the
    /// addon's Race or AdditionalRaces contains the NPC's race; a null/empty
    /// Race is treated as universal.</summary>
    private static bool IsArmatureForRace(IArmorAddonGetter arma, FormKey? npcRaceKey, FormKey? armorRaceKey)
    {
        if (npcRaceKey == null) return true;
        if (arma.Race.IsNull && (arma.AdditionalRaces == null || arma.AdditionalRaces.Count == 0)) return true;
        return Auxilliary.ArmaNamesRace(arma, npcRaceKey, armorRaceKey);
    }

    /// <summary>The NPC race's ArmorRace (RNAM) — the key the engine matches
    /// armatures against — resolved mod-scoped-then-winner like every other
    /// record here. Null when absent or unresolvable; callers then compare the
    /// raw race key only, i.e. the pre-fix behavior.</summary>
    private FormKey? ResolveArmorRaceKey(INpcGetter npc, IEnumerable<ModKey> modKeys,
        HashSet<string> modFolderPaths)
    {
        if (npc.Race.IsNull) return null;
        return Auxilliary.GetArmorRaceKey(
            ResolveFromModsOrWinner<IRaceGetter>(npc.Race, modKeys, modFolderPaths));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Record minting
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns the cached minted set for (wig, NIF, sex) or mints a
    /// fresh one: parent HDPT (Type=Hair, Model, ExtraParts) + modeled
    /// IsExtraPart extras, EDID == future baked shape name for every part.
    /// Null when the wig NIF yields no usable render shapes (each caller
    /// decides its own fallback). Source-agnostic: <paramref name="wigKey"/> is
    /// the outfit wig ARMO's key or the skin-carried wig ARMA's key (FormKeys
    /// are globally unique so both share <see cref="_mintedSets"/>), and
    /// <paramref name="sourceEditorId"/> seeds the minted EDID prefix.
    /// <para><b>Minted parts are single-gender</b> (<paramref name="female"/>):
    /// the engine's headgear hair suppression looks the actor's Hair part up
    /// with a gender filter, and a part flagged both Male and Female is
    /// invisible to that lookup — the baked wig then renders through hoods and
    /// helmets. In-game proven 2026-07-26 (Wylandriah): identical NIF, flags
    /// Male|Female → hair pokes through; Female only → hair suppressed
    /// correctly. Vanilla, Bijin-style replacers and ARMO_2_HDPT all ship
    /// single-gender hair parts; UseSolidTint likewise mirrors the vanilla
    /// hair-part convention.</para></summary>
    private MintedWigSet? GetOrMintWigSet(string? sourceEditorId, FormKey wigKey, string wigNifSourcePath,
        string wigNifDataRelPath, string wigNifRecordPath, bool female, ModSetting appearanceModSetting,
        string npcIdentifier, Action<string, bool, bool> appendLog)
    {
        lock (_lock)
        {
            if (_mintedSets.TryGetValue((wigKey, wigNifSourcePath, female), out var existing)) return existing;
        }

        var renderShapes = RenderShapeNamesProvider(wigNifSourcePath);
        if (renderShapes.Count == 0)
        {
            appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' contains no render shapes " +
                      $"(shader-bearing) for {npcIdentifier}. Skipping conversion for this wig.", false, true);
            return null;
        }

        if (renderShapes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != renderShapes.Count)
        {
            appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' has duplicate render shape names — " +
                      "EDID==shape-name reconciliation would be ambiguous. Skipping conversion for this wig.",
                false, true);
            return null;
        }

        // Rename map: source shape → NPC2Wig_<sanitized wig EDID>_<F|M>_<sanitized
        // shape>. Per-(WIG, sex) so all same-sex NPCs sharing the wig share one
        // HDPT set and identical baked names; the sex token keeps a unisex
        // wig's twin sets from colliding. A prefix already claimed by a
        // DIFFERENT wig mesh (same EditorID in another plugin, or a per-sex
        // mesh pair) gets a short disambiguator so EDIDs stay unique per set.
        string wigId = SanitizeForEditorId(sourceEditorId) ?? SanitizeForEditorId(wigKey.ToString())!;
        wigId += female ? "_F" : "_M";
        string prefix = MintedEditorIdPrefix + wigId + "_";
        lock (_lock)
        {
            if (_renamePrefixOwners.TryGetValue(prefix, out var owner) &&
                !string.Equals(owner, wigNifSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                prefix = MintedEditorIdPrefix + wigId + "_" + wigKey.ID.ToString("X8") + "_";
            }
            _renamePrefixOwners[prefix] = wigNifSourcePath;
        }

        var renames = renderShapes.ToDictionary(
            n => n,
            n => prefix + SanitizeForEditorId(n),
            StringComparer.OrdinalIgnoreCase);

        // Physics XML (SMP wigs): first reference wins (deterministic); the
        // rewritten copy goes to the NPC2-owned path the bake repoints the
        // FaceGen's extra-data at.
        string? xmlSourcePath = null, xmlSourceRel = null, xmlNewRel = null;
        var xmlRefs = PhysicsXmlProvider(wigNifSourcePath)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (xmlRefs.Count > 0)
        {
            if (xmlRefs.Count > 1)
            {
                appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' references {xmlRefs.Count} physics " +
                          $"XMLs; using '{xmlRefs[0]}' (the baked FaceGen carries a single physics reference).",
                    false, false);
            }

            if (AssetHandler.TryNormalizePhysicsXmlPath(xmlRefs[0], wigNifDataRelPath, out var normalizedRel))
            {
                xmlSourcePath = MaterializeDataRelFile(normalizedRel, appearanceModSetting);
                if (xmlSourcePath != null)
                {
                    xmlSourceRel = normalizedRel;
                    lock (_lock)
                    {
                        string baseName = wigId;
                        string candidate = Path.Combine(PhysicsXmlOutputFolder, baseName + ".xml");
                        if (_usedPhysicsXmlRelPaths.Contains(candidate))
                        {
                            candidate = Path.Combine(PhysicsXmlOutputFolder,
                                baseName + "_" + wigKey.ID.ToString("X8") + ".xml");
                        }
                        _usedPhysicsXmlRelPaths.Add(candidate);
                        xmlNewRel = candidate;
                    }
                }
                else
                {
                    appendLog($"      Wig conversion: physics XML '{normalizedRel}' referenced by the wig NIF was " +
                              "not found (loose or BSA) — the baked wig will have no SMP physics.", false, true);
                }
            }
        }

        // Mint the records. Every part carries a Model — the engine's facegen
        // reconciliation only expects baked geometry for geometry-bearing parts
        // (a modeless extra leaves its baked shape orphaned → dark face), and it
        // never validates Model contents against baked shapes, so all parts can
        // share the whole wig NIF (engine-proven; no NIF splitting needed).
        var set = new MintedWigSet
        {
            WigNifSourcePath = wigNifSourcePath,
            WigNifDataRelPath = wigNifDataRelPath,
            PhysicsXmlSourcePath = xmlSourcePath,
            PhysicsXmlSourceDataRelPath = xmlSourceRel,
            PhysicsXmlNewDataRelPath = xmlNewRel,
        };

        lock (_lock)
        {
            var outputMod = _environmentStateProvider.OutputMod;
            // Single-gender + UseSolidTint — vanilla hair-part parity. Both
            // gender bits set at once makes the part invisible to the engine's
            // gender-filtered hair lookup, which silently disables headgear
            // hair suppression (see the method doc).
            var genderFlag = female ? HeadPart.Flag.Female : HeadPart.Flag.Male;
            HeadPart? parent = null;
            foreach (var srcName in renderShapes)
            {
                var hp = outputMod.HeadParts.AddNew();
                hp.EditorID = renames[srcName];
                hp.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);
                hp.Model = new Model { File = wigNifRecordPath };
                if (parent == null)
                {
                    hp.Type = HeadPart.TypeEnum.Hair;
                    hp.Flags = genderFlag | HeadPart.Flag.UseSolidTint;
                    parent = hp;
                }
                else
                {
                    hp.Type = HeadPart.TypeEnum.Misc;
                    hp.Flags = genderFlag | HeadPart.Flag.UseSolidTint | HeadPart.Flag.IsExtraPart;
                    parent.ExtraParts.Add(hp.FormKey.ToLink<IHeadPartGetter>());
                }
                RecordProvenanceDiag.RecordGenerated(hp.FormKey, hp.EditorID, "HeadPart");
                set.MintedRecords.Add(hp);
            }

            set.ParentKey = parent!.FormKey;
            set.ParentEditorId = parent.EditorID ?? string.Empty;
            foreach (var kvp in renames) set.ShapeRenames[kvp.Key] = kvp.Value;

            _mintedSets[(wigKey, wigNifSourcePath, female)] = set;
        }

        appendLog($"      Wig conversion: minted {set.MintedRecords.Count} head part record(s) for wig " +
                  $"'{sourceEditorId ?? wigKey.ToString()}' (parent '{set.ParentEditorId}').", false, false);
        return set;
    }

    /// <summary>Spike-proven EditorID sanitizer: letters/digits/underscore kept,
    /// everything else becomes '_'.</summary>
    internal static string? SanitizeForEditorId(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Donor hair collection (mirrors WigForwarder.CollectHairRemoval)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Collects the Hair-type head parts of <paramref name="appearanceNpc"/> — the
    /// record whose HeadParts the OUTPUT will carry, so the terminus's under a flatten. Reading
    /// the donor here left <see cref="FinalizeNpcRecord"/> removing keys the flatten had already
    /// replaced, so the terminus's hair rendered alongside the minted wig.</summary>
    private void CollectHairRemoval(INpcGetter appearanceNpc, ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, HashSet<FormKey> donorHairKeys, HashSet<string> stripNames)
    {
        foreach (var hpLink in appearanceNpc.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            var hpRec = ResolveFromModsOrWinner<IHeadPartGetter>(hpLink,
                appearanceModSetting.CorrespondingModKeys, modFolderPaths);
            if (hpRec?.Type != HeadPart.TypeEnum.Hair) continue;

            donorHairKeys.Add(hpLink.FormKey);
            if (!string.IsNullOrEmpty(hpRec.EditorID)) stripNames.Add(hpRec.EditorID);
            if (hpRec.ExtraParts == null) continue;
            foreach (var extraLink in hpRec.ExtraParts)
            {
                if (extraLink == null || extraLink.IsNull) continue;
                var extraRec = ResolveFromModsOrWinner<IHeadPartGetter>(extraLink,
                    appearanceModSetting.CorrespondingModKeys, modFolderPaths);
                if (!string.IsNullOrEmpty(extraRec?.EditorID)) stripNames.Add(extraRec.EditorID);
            }
        }
    }

    private HashSet<FormKey> ExpandWithDuplicateMappings(HashSet<FormKey> donorKeys)
    {
        var set = new HashSet<FormKey>(donorKeys);
        foreach (var donorKey in donorKeys)
        {
            if (_recordHandler.TryGetDuplicateMapping(donorKey, out var mapped)) set.Add(mapped);
        }
        return set;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  File materialization (loose folders, then BSAs → temp extract)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>True when the data-relative file exists as a loose file in one
    /// of the mod's folders or inside one of its indexed BSAs.</summary>
    private bool DataRelFileExists(string dataRelPath, ModSetting modSetting)
    {
        for (int i = modSetting.CorrespondingFolderPaths.Count - 1; i >= 0; i--)
        {
            if (File.Exists(Path.Combine(modSetting.CorrespondingFolderPaths[i], dataRelPath))) return true;
        }
        return _bsaHandler.FileExists(dataRelPath, modSetting.CorrespondingModKeys, out _, out _);
    }

    /// <summary>Resolves a data-relative path to a readable absolute path: the
    /// last mod folder wins (parity with AssetHandler.FindAssetSource); a
    /// BSA-packed file is extracted to the session temp directory (kept for the
    /// whole run — the bake reads it after all batches finish). Null when the
    /// file exists nowhere.</summary>
    private string? MaterializeDataRelFile(string dataRelPath, ModSetting modSetting)
    {
        for (int i = modSetting.CorrespondingFolderPaths.Count - 1; i >= 0; i--)
        {
            var candidate = Path.Combine(modSetting.CorrespondingFolderPaths[i], dataRelPath);
            if (File.Exists(candidate)) return candidate;
        }

        if (_bsaHandler.FileExists(dataRelPath, modSetting.CorrespondingModKeys, out _, out var bsaPath) &&
            bsaPath != null)
        {
            string dest = Path.Combine(GetTempExtractDir(), dataRelPath);
            if (File.Exists(dest)) return dest; // already extracted this run
            var (ok, _) = _bsaHandler.ExtractFileAsync(bsaPath, dataRelPath, dest)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            if (ok && File.Exists(dest)) return dest;
        }

        return null;
    }

    private string GetTempExtractDir()
    {
        _tempExtractDir ??= Path.Combine(Path.GetTempPath(), "NPC2", "WigConvert");
        return _tempExtractDir;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Record resolution (mirrors WigForwarder)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Resolves a record from the appearance mod's own plugins first
    /// (mod-scoped, matching where donor data actually comes from), falling back
    /// to the load-order winner for records the mod inherits from masters.</summary>
    private TGetter? ResolveFromModsOrWinner<TGetter>(IFormLinkGetter link, IEnumerable<ModKey> modKeys,
        HashSet<string> modFolderPaths)
        where TGetter : class, IMajorRecordGetter
    {
        if (link.IsNull) return null;
        if (_recordHandler.TryGetRecordFromMods(link, modKeys, modFolderPaths,
                RecordHandler.RecordLookupFallBack.None, out var record) && record is TGetter typed)
        {
            return typed;
        }

        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null) return null;
        return linkCache.TryResolve<TGetter>(link.FormKey, out var winner) ? winner : null;
    }
}
