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
/// <para><b>Per-NPC fallback:</b> when the conversion would be risky — no donor
/// Hair head part to harvest dismember partitions from (the bake transplants the
/// donor hair's partition entry; without one the baked shapes keep their source
/// skin-instance types and may dark-face), an unresolvable wig NIF, zero render
/// shapes, or an ambiguous multi-wig outfit — <see cref="Apply"/> returns null
/// with <c>fallBackToForwardToSkin</c> set, and the Patcher routes that NPC
/// through the proven <see cref="WigForwarder"/> ForwardToSkin flow instead
/// (which itself falls back to ForwardToOutfit when the donor has no WNAM).</para>
/// </summary>
public class HeadPartWigConverter
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly RecordHandler _recordHandler;
    private readonly BsaHandler _bsaHandler;

    /// <summary>Prefix of every minted HeadPart EditorID (and therefore every
    /// baked FaceGen shape name): NPC2Wig_&lt;sanitized wig EDID&gt;_&lt;sanitized
    /// shape name&gt;. Matches the engine-proven spike package.</summary>
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
    // NPCs sharing the same wig ARMO + resolved NIF share one minted HDPT set and
    // identical baked shape names. The NIF path is part of the key because the ARMA
    // WorldModel is per-sex — a wig serving both sexes with distinct meshes mints
    // one set per mesh (cache keys must include scope context).
    private readonly Dictionary<(FormKey WigKey, string NifPath), MintedWigSet> _mintedSets = new();

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
        BsaHandler bsaHandler)
    {
        _environmentStateProvider = environmentStateProvider;
        _recordHandler = recordHandler;
        _bsaHandler = bsaHandler;
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
    public Result? Apply(
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths,
        string npcIdentifier,
        Action<string, bool, bool> appendLog,
        out bool fallBackToForwardToSkin)
    {
        fallBackToForwardToSkin = false;

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

        if (wigItemKeys.Count == 0) return null; // nothing to convert; not a fallback

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
        CollectHairRemoval(donorNpc, appearanceModSetting, modFolderPaths, donorHairKeys, stripNames);
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
        string? wigNifRecordPath = ResolveWigNifRecordPath(wigArmor, donorNpc, appearanceModSetting,
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
        var (donorFaceGenRelPath, _) = Auxilliary.GetFaceGenSubPathStrings(donorNpc.FormKey, regularized: true);
        string? donorFaceGenPath = MaterializeDataRelFile(donorFaceGenRelPath, appearanceModSetting);
        if (donorFaceGenPath == null || !PartitionProbe(donorFaceGenPath, stripNames))
        {
            appendLog($"      Wig conversion: {npcIdentifier}'s donor FaceGen " +
                      $"{(donorFaceGenPath == null ? "was not found" : "has no hair shape with dismember partitions")} " +
                      "— the bake cannot normalize the wig shapes to CK skin data. Falling back to ForwardToSkin.",
                false, true);
            fallBackToForwardToSkin = true;
            return null;
        }

        // 4. Mint (or reuse) the per-wig HDPT set.
        MintedWigSet? set = GetOrMintWigSet(wigArmor, wigKey, wigNifSourcePath, wigNifDataRelPath,
            wigNifRecordPath, appearanceModSetting, npcIdentifier, appendLog);
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
        };
        result.DonorHairHeadPartKeys.UnionWith(donorHairKeys);
        result.FaceGenShapeNamesToStrip.UnionWith(stripNames);

        appendLog($"      Wig conversion: {npcIdentifier} → wig '{wigArmor.EditorID ?? wigKey.ToString()}' " +
                  $"as head parts ('{set.ParentEditorId}' + {set.ShapeRenames.Count - 1} extra(s)); " +
                  $"donor hair {string.Join(", ", stripNames)} will be stripped and the wig baked into the " +
                  "copied FaceGen after asset copy.", false, false);
        return result;
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
    private string? ResolveWigNifRecordPath(IArmorGetter wigArmor, INpcGetter donorNpc,
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
            // applicable to the donor's race.
            var raceKey = donorNpc.Race.IsNull ? (FormKey?)null : donorNpc.Race.FormKey;
            var raceMatched = hairArmas.Where(a => IsArmatureForRace(a, raceKey)).ToList();
            if (raceMatched.Count > 0) hairArmas = raceMatched;
        }

        if (hairArmas.Count > 1)
        {
            appendLog($"      Wig conversion: wig '{wigArmor.EditorID}' has {hairArmas.Count} applicable " +
                      $"hair-slot ArmorAddons for {npcIdentifier} — cannot pick a single wig mesh to bake. " +
                      "Falling back to ForwardToSkin.", false, true);
            return null;
        }

        var arma = hairArmas[0];
        bool isFemale = Auxilliary.IsFemale(donorNpc);
        string? recordPath = GetWorldModelRecordPath(arma, female: isFemale)
                             ?? GetWorldModelRecordPath(arma, female: !isFemale); // shared/single-sex meshes
        if (recordPath == null)
        {
            appendLog($"      Wig conversion: wig ArmorAddon {arma.FormKey} has no WorldModel path for " +
                      $"{npcIdentifier}. Falling back to ForwardToSkin.", false, true);
            return null;
        }

        // Weight-matched _0/_1 variant: >= 50 → _1, else _0 (nearest — no
        // interpolation, an accepted limitation). Fall back to whichever
        // variant actually exists; a suffix-less path is a single-weight mesh.
        string preferred = SwapWeightSuffix(recordPath, donorNpc.Weight >= 50f);
        foreach (var candidate in new[] { preferred, recordPath, SwapWeightSuffix(recordPath, donorNpc.Weight < 50f) })
        {
            if (Auxilliary.TryRegularizePath(candidate, out var rel) && DataRelFileExists(rel, appearanceModSetting))
            {
                return candidate;
            }
        }

        appendLog($"      Wig conversion: no weight variant of wig NIF '{recordPath}' exists in " +
                  $"'{appearanceModSetting.DisplayName}' for {npcIdentifier}. Falling back to ForwardToSkin.",
            false, true);
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
    private static bool IsArmatureForRace(IArmorAddonGetter arma, FormKey? npcRaceKey)
    {
        if (npcRaceKey == null) return true;
        if (arma.Race.IsNull && (arma.AdditionalRaces == null || arma.AdditionalRaces.Count == 0)) return true;
        if (!arma.Race.IsNull && arma.Race.FormKey == npcRaceKey.Value) return true;
        return arma.AdditionalRaces != null &&
               arma.AdditionalRaces.Any(r => r != null && !r.IsNull && r.FormKey == npcRaceKey.Value);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Record minting
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns the cached minted set for (wig, NIF) or mints a fresh
    /// one: parent HDPT (Type=Hair, Model, ExtraParts) + modeled IsExtraPart
    /// extras, EDID == future baked shape name for every part. Null when the
    /// wig NIF yields no usable render shapes (caller falls back).</summary>
    private MintedWigSet? GetOrMintWigSet(IArmorGetter wigArmor, FormKey wigKey, string wigNifSourcePath,
        string wigNifDataRelPath, string wigNifRecordPath, ModSetting appearanceModSetting,
        string npcIdentifier, Action<string, bool, bool> appendLog)
    {
        lock (_lock)
        {
            if (_mintedSets.TryGetValue((wigKey, wigNifSourcePath), out var existing)) return existing;
        }

        var renderShapes = RenderShapeNamesProvider(wigNifSourcePath);
        if (renderShapes.Count == 0)
        {
            appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' contains no render shapes " +
                      $"(shader-bearing) for {npcIdentifier}. Falling back to ForwardToSkin.", false, true);
            return null;
        }

        if (renderShapes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != renderShapes.Count)
        {
            appendLog($"      Wig conversion: wig NIF '{wigNifDataRelPath}' has duplicate render shape names — " +
                      "EDID==shape-name reconciliation would be ambiguous. Falling back to ForwardToSkin.",
                false, true);
            return null;
        }

        // Rename map: source shape → NPC2Wig_<sanitized wig EDID>_<sanitized shape>.
        // Per-WIG so all NPCs sharing the wig share one HDPT set and identical
        // baked names. A prefix already claimed by a DIFFERENT wig mesh (same
        // EditorID in another plugin, or a per-sex mesh pair) gets a short
        // disambiguator so EDIDs stay unique per set.
        string wigId = SanitizeForEditorId(wigArmor.EditorID) ?? SanitizeForEditorId(wigKey.ToString())!;
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
                    hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
                    parent = hp;
                }
                else
                {
                    hp.Type = HeadPart.TypeEnum.Misc;
                    hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female | HeadPart.Flag.IsExtraPart;
                    parent.ExtraParts.Add(hp.FormKey.ToLink<IHeadPartGetter>());
                }
                RecordProvenanceDiag.RecordGenerated(hp.FormKey, hp.EditorID, "HeadPart");
                set.MintedRecords.Add(hp);
            }

            set.ParentKey = parent!.FormKey;
            set.ParentEditorId = parent.EditorID ?? string.Empty;
            foreach (var kvp in renames) set.ShapeRenames[kvp.Key] = kvp.Value;

            _mintedSets[(wigKey, wigNifSourcePath)] = set;
        }

        appendLog($"      Wig conversion: minted {set.MintedRecords.Count} head part record(s) for wig " +
                  $"'{wigArmor.EditorID ?? wigKey.ToString()}' (parent '{set.ParentEditorId}').", false, false);
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

    private void CollectHairRemoval(INpcGetter donorNpc, ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, HashSet<FormKey> donorHairKeys, HashSet<string> stripNames)
    {
        foreach (var hpLink in donorNpc.HeadParts)
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
