using System.IO;
using nifly;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class NifHandler
{
    public static HashSet<string> GetExtraTexturesFromNif(string nifPath)
    {
        var debug = File.Exists(nifPath);
        HashSet<string> uniqueTextures = new HashSet<string>();
        using (NifFile nif = new NifFile())
        {
            nif.Load(nifPath);
            // Assume 'niFile' is your loaded NIF file object and niFile.Header is the NiHeader.
            NiHeader header = nif.GetHeader();

            var blockCount = header.GetNumBlocks();
            for (uint id = 0; id < blockCount; id++)
            {
                NiObject block = header.GetBlockById(id);
                if (block is BSShaderTextureSet textureSet)
                {
                    // Access the texture paths safely
                    using var texturesArray = textureSet.textures;           // textures is a container (e.g. NiTArray<NiString>)
                    using var textureItems = texturesArray.items();          // items() gives an enumerable collection of NiString
                    foreach (NiString tex in textureItems)
                    {
                        if (tex != null)
                        {
                            string path = tex.get();  // Get the actual string from NiString
                            if (!string.IsNullOrEmpty(path))
                            {
                                uniqueTextures.Add(path);
                            }
                        }
                        // (NiString will be disposed at end of using scope if required)
                    }
                }
            }
            return uniqueTextures;
        }
    }

    /// <summary>
    /// Deletes the named shapes from a NIF in place and saves it. Used by the
    /// wig-forwarding pipeline to strip the baked hair shape(s) from a copied
    /// FaceGen NIF after the hair head part is removed from the NPC record —
    /// otherwise the baked hair renders alongside the forwarded wig in game.
    /// Matching is by exact shape name (FaceGen shapes are named after their
    /// head part EditorIDs), case-insensitive. Modeled on SynthEBD's
    /// FaceGenPatcher.RemoveShapesByHeadPartType: collect first, then delete
    /// (DeleteShape re-indexes blocks), with the GetShapes scope held open
    /// across the deletes and the Save. Returns the number of shapes removed
    /// (0 = file left untouched, no save).
    /// </summary>
    public static int RemoveShapesByName(string nifPath, IReadOnlyCollection<string> shapeNames,
        Action<string>? log = null)
    {
        var wanted = shapeNames as ISet<string> ??
                     new HashSet<string>(shapeNames, StringComparer.OrdinalIgnoreCase);
        using NifFile nif = new NifFile();
        int loadResult = nif.Load(nifPath);
        if (loadResult != 0)
        {
            log?.Invoke($"RemoveShapesByName: failed to load NIF (code {loadResult}): {nifPath}");
            return 0;
        }

        int removedCount = RemoveShapesByNameCore(nif, wanted, nifPath, log);
        if (removedCount == 0) return 0;

        int saveResult = nif.Save(nifPath);
        if (saveResult != 0)
        {
            log?.Invoke($"RemoveShapesByName: save FAILED (code {saveResult}): {nifPath}");
            return 0;
        }

        return removedCount;
    }

    /// <summary>In-memory core of <see cref="RemoveShapesByName"/>: deletes the named
    /// shapes from an already-loaded NIF without saving. Shared with the wig bake,
    /// which strips the donor hair shapes and merges the wig in one load/save.</summary>
    private static int RemoveShapesByNameCore(NifFile nif, ISet<string> wanted, string nifPathForLog,
        Action<string>? log)
    {
        var toRemove = new List<(NiShape Shape, string Name)>();
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            string? name = shape.name?.get();
            if (string.IsNullOrEmpty(name)) continue;
            if (wanted.Contains(name))
            {
                toRemove.Add((shape, name));
            }
        }

        foreach (var (shape, name) in toRemove)
        {
            nif.DeleteShape(shape);
            log?.Invoke($"RemoveShapesByName: removed shape '{name}' from {Path.GetFileName(nifPathForLog)}");
        }

        return toRemove.Count;
    }

    /// <summary>
    /// The NiStringExtraData block name that HDT-SMP / FSMP uses to point a NIF at its
    /// physics XML config file. hdtSMP64 scanBBP() reads the XML path from the
    /// <c>stringData</c> of an extra-data block with this name (see hdtDefaultBBP.cpp).
    /// </summary>
    public const string SmpPhysicsExtraDataName = "HDT Skinned Mesh Physics Object";

    /// <summary>
    /// Scans a NIF for SMP/HDT physics XML references. A physics-enabled NIF carries an
    /// NiStringExtraData block whose string value is the (Data-relative) path to its physics
    /// XML. We collect that value when the block is named "HDT Skinned Mesh Physics Object"
    /// (the modern SMP marker) OR when its value simply ends in ".xml" (a robust catch-all for
    /// legacy/variant marker names such as the old "HDT Havok Path"). The same mechanism is
    /// used for hair, wigs, armor and body, so this single pass covers every physics type.
    /// Returns the raw string values exactly as stored in the NIF (caller normalizes/resolves).
    /// </summary>
    public static HashSet<string> GetPhysicsXmlPathsFromNif(string nifPath)
    {
        HashSet<string> xmlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (NifFile nif = new NifFile())
        {
            nif.Load(nifPath);
            NiHeader header = nif.GetHeader();

            var blockCount = header.GetNumBlocks();
            for (uint id = 0; id < blockCount; id++)
            {
                NiObject block = header.GetBlockById(id);
                if (block is NiStringExtraData sed)
                {
                    // name lives on the NiExtraData base; stringData holds the XML path.
                    string? blockName = sed.name?.get();
                    string? value = sed.stringData?.get();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    bool isPhysicsMarker = !string.IsNullOrEmpty(blockName) &&
                        blockName.Equals(SmpPhysicsExtraDataName, StringComparison.OrdinalIgnoreCase);
                    bool looksLikeXml = value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

                    if (isPhysicsMarker || looksLikeXml)
                    {
                        xmlPaths.Add(value.Trim());
                    }
                }
            }
            return xmlPaths;
        }
    }

    /// <summary>The FaceGen skin node every baked head shape must live under. The
    /// engine (and SynthEBD's FaceGenPatcher, which this mirrors) treats shapes
    /// under this node as the NPC's head geometry.</summary>
    public const string FaceGenSkinNodeName = "BSFaceGenNiNodeSkinned";

    /// <summary>
    /// One wig→FaceGen bake operation. <see cref="FaceGenNifPath"/> is edited in
    /// place (callers operate on a copy of the donor FaceGen). Wig shapes are
    /// renamed via <see cref="ShapeRenames"/> (destination names become the minted
    /// HeadPart EditorIDs — the engine reconciles records against baked shapes by
    /// name). <see cref="ShapeNamesToStrip"/> removes the donor's baked hair first.
    /// <see cref="PhysicsXmlNewDataRelPath"/> repoints the wig's SMP extra-data to
    /// the rewritten physics XML (whose per-shape entries match the renames).
    /// </summary>
    /// <param name="OnlyRenderShapes">When non-null, bake ONLY these source
    /// render shapes (others are dropped from the merge like collision meshes).
    /// Diagnostic/bisect hook — null bakes every render shape.</param>
    public sealed record WigBakeInstruction(
        string FaceGenNifPath,
        string WigNifPath,
        IReadOnlyDictionary<string, string> ShapeRenames,
        IReadOnlyCollection<string> ShapeNamesToStrip,
        string? PhysicsXmlNewDataRelPath,
        IReadOnlyCollection<string>? OnlyRenderShapes = null);

    /// <summary>
    /// Names of the render shapes (shapes carrying a shader property) in a NIF, in
    /// enumeration order. Shader-less shapes (SMP collision/virtual meshes) are
    /// excluded — they get no HeadPart record and keep their original names during
    /// a bake. The first entry is the designated parent HeadPart shape.
    /// </summary>
    public static IReadOnlyList<string> GetRenderShapeNames(string nifPath)
    {
        var names = new List<string>();
        using NifFile nif = new NifFile();
        if (nif.Load(nifPath) != 0) return names;
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            string? name = shape.name?.get();
            if (string.IsNullOrEmpty(name)) continue;
            if (shape.HasShaderProperty()) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Whether any of the named shapes in a NIF carries dismember partition data
    /// (<c>GetShapePartitions</c> succeeds). Mirrors the partition-template
    /// harvest at the top of <see cref="BakeWigIntoFaceGen"/>: the bake
    /// transplants the donor hair's BSDismemberSkinInstance partition entry onto
    /// the wig shapes, and without one the baked shapes keep their source
    /// skin-instance types (dark-face risk — see the bake's invariant 4). The
    /// wig→HeadPart converter probes this BEFORE minting records so a
    /// partition-less donor can fall back to ForwardToSkin instead of shipping a
    /// risky bake.
    /// </summary>
    public static bool HasShapeWithPartitions(string nifPath, IReadOnlyCollection<string> shapeNames)
    {
        if (shapeNames.Count == 0) return false;
        var wanted = shapeNames as ISet<string> ??
                     new HashSet<string>(shapeNames, StringComparer.OrdinalIgnoreCase);
        using NifFile nif = new NifFile();
        if (nif.Load(nifPath) != 0) return false;
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            string? name = shape.name?.get();
            if (string.IsNullOrEmpty(name) || !wanted.Contains(name)) continue;
            using var template = new NiVectorBSDismemberSkinInstancePartitionInfo();
            using var triParts = new vectorint();
            if (nif.GetShapePartitions(shape, template, triParts)) return true;
        }
        return false;
    }

    /// <summary>
    /// Merges an entire wig NIF scene into a FaceGen NIF (the wig→HeadPart
    /// conversion bake). Strips the donor hair shapes, then clones the wig's full
    /// block tree (render shapes, SMP bone-chain NiNodes with hierarchy intact,
    /// physics extra-data) into the FaceGen NIF via nifly's CloneChildren. Render
    /// shapes are reparented under BSFaceGenNiNodeSkinned and renamed per the
    /// instruction. Returns the number of render shapes baked (0 = failure, file
    /// untouched on disk).
    ///
    /// <para><b>Engine-verified FaceGen invariants</b> — established 2026-07 by an
    /// in-game bisect on the FoxGlove Auri specimen (variants A–H; each violation
    /// below INDIVIDUALLY reproduces the dark-face bug, i.e. the face renders
    /// with no tint while geometry and even SMP physics keep working):</para>
    /// <list type="number">
    /// <item><b>Record↔NIF reconciliation is by name, both directions.</b> The
    /// engine matches baked shape names against the EditorIDs of the NPC's
    /// resolved head parts (own parts + ExtraParts recursively + race defaults
    /// for unfilled slots). A geometry-bearing part with no baked shape dark-faces;
    /// an ORPHAN baked shape (no matching part) also dark-faces — so a strip
    /// list must cover a removed part's ExtraParts (hairline AND scalp).
    /// MODELESS parts are excluded from the expected set (why the modeless
    /// NPC2_HairBald works with nothing baked) — every part whose shape IS baked
    /// must therefore carry a Model, extra parts included.</item>
    /// <item><b>Baked head-part geometry must be BSDynamicTriShape.</b> Plain
    /// BSTriShape renders but is not accepted as facegen geometry by the tint
    /// pass. nifly cannot promote already-SSE shapes (OptimizeFor early-outs
    /// unless converting LE→SSE; SetShapeDynamic only flags legacy
    /// NiGeometryData), hence the manual rebuild in
    /// ConvertRenderShapesToDynamic.</item>
    /// <item><b>No duplicate node names, no stray skeleton bones.</b> The engine
    /// merges facegen nodes onto the actor skeleton BY NAME; a second
    /// "NPC Head [Head]" (or an unreferenced orphan block with that name)
    /// poisons the merge. Cloned nodes whose names already exist are grafted
    /// onto the existing node and deleted; subtrees no kept shape skins to
    /// (SMP collision/constraint anchors: breasts, clavicles, NPC Root) are
    /// dropped. Working reference: Project AHO's shipped SMP facegen — chain
    /// bones once each under the EXISTING NPC Head, nothing else.</item>
    /// <item><b>Skin data must match the CK pattern.</b> BSDismemberSkinInstance
    /// with hair-classified partitions (transplanted from the donor hair; nifly
    /// partition splits inherit the partition ID), GPU skin weights present in
    /// vertData (a Create()-rebuilt shape has ZERO weights and renders nowhere),
    /// and the NiSkinPartition's embedded vertex data rebuilt to the shape's
    /// layout via UpdateSkinPartitions.</item>
    /// <item><b>The engine does NOT validate baked shapes against the HDPT
    /// Model NIF.</b> Shape names and vertex counts may differ freely from the
    /// Model's contents (the shipping FoxGlove facegen is hand-built:
    /// 17833-vert baked hair vs an 8674-vert model named 's4studio_mesh_3').
    /// The Model's role is record-side only (geometry-bearing marker, player/
    /// RaceMenu use).</item>
    /// <item><b>HDT-SMP works on facegen-baked shapes.</b> Requirements: the
    /// "HDT Skinned Mesh Physics Object" NiStringExtraData at root pointing at
    /// the physics XML, plus the chain bones in-file. Collision/virtual meshes
    /// need NOT be baked (dropped here — no working facegen carries them); the
    /// XML tolerates entries for absent shapes, and its body-bone references
    /// resolve against the actor's runtime skeleton, not the NIF.</item>
    /// </list>
    ///
    /// <para><b>nifly/SWIG mechanics relied on here:</b> CloneChildren deep-clones
    /// with hierarchy intact and rewrites the SOURCE root's child/extra refs in
    /// place to the new destination ids (that rewrite is the readback used to
    /// find the clones); skin-instance bone PTRS are not rewritten and need a
    /// name-based SetShapeBoneIDList remap + targetRef repoint. ReplaceBlock
    /// transfers C++ ownership (suppress the SWIG proxy finalizer or it
    /// double-frees). RenameShape is static in the binding. DeleteUnreferencedNodes
    /// only removes LEAF nodes, so duplicates/unkept subtrees are deleted
    /// explicitly, highest block id first.</para>
    /// </summary>
    public static int BakeWigIntoFaceGen(WigBakeInstruction ins, Action<string>? log = null)
    {
        using NifFile faceGen = new NifFile();
        if (faceGen.Load(ins.FaceGenNifPath) != 0)
        {
            log?.Invoke($"BakeWigIntoFaceGen: failed to load FaceGen NIF: {ins.FaceGenNifPath}");
            return 0;
        }

        using NifFile wig = new NifFile();
        if (wig.Load(ins.WigNifPath) != 0)
        {
            log?.Invoke($"BakeWigIntoFaceGen: failed to load wig NIF: {ins.WigNifPath}");
            return 0;
        }

        // 0a. Harvest the donor hair's dismember partition info BEFORE stripping.
        //     Every CK-baked facegen shape uses BSDismemberSkinInstance with
        //     hair-classified partitions (the same partition IDs helmets use to
        //     hide hair); the wig's armor shapes use plain NiSkinInstance, which
        //     no working facegen precedent carries. Transplanting the donor
        //     hair's partition entry normalizes the wig shapes to CK shape.
        NiVectorBSDismemberSkinInstancePartitionInfo? hairPartitionTemplate = null;
        foreach (var stripName in ins.ShapeNamesToStrip)
        {
            var donorShape = FindShapeByName(faceGen, stripName);
            if (donorShape == null) continue;
            var template = new NiVectorBSDismemberSkinInstancePartitionInfo();
            using var donorTriParts = new vectorint();
            if (faceGen.GetShapePartitions(donorShape, template, donorTriParts))
            {
                hairPartitionTemplate = template;
                log?.Invoke($"BakeWigIntoFaceGen: harvested dismember partition template from '{stripName}'.");
                break;
            }
            template.Dispose();
        }
        if (hairPartitionTemplate == null)
        {
            log?.Invoke("BakeWigIntoFaceGen: WARNING - no donor hair shape with partitions found; " +
                        "wig shapes keep their source skin-instance types.");
        }

        // 0b. Normalize the wig scene in memory (never saved back to its path):
        //     drop collision meshes, then promote render shapes to
        //     BSDynamicTriShape so the clone carries CK-shaped facegen geometry.
        DropShaderlessShapes(wig, log);
        if (ins.OnlyRenderShapes != null)
        {
            var keep = ins.OnlyRenderShapes as ISet<string> ??
                       new HashSet<string>(ins.OnlyRenderShapes, StringComparer.OrdinalIgnoreCase);
            var excluded = new List<(NiShape Shape, string Name)>();
            using var allShapes = wig.GetShapes();
            foreach (var shape in allShapes)
            {
                string? shapeName = shape.name?.get();
                if (!string.IsNullOrEmpty(shapeName) && !keep.Contains(shapeName))
                {
                    excluded.Add((shape, shapeName));
                }
            }
            foreach (var (shape, shapeName) in excluded)
            {
                wig.DeleteShape(shape);
                log?.Invoke($"BakeWigIntoFaceGen: excluded render shape '{shapeName}' (OnlyRenderShapes bisect).");
            }
        }
        bool converted = ConvertRenderShapesToDynamic(wig, hairPartitionTemplate, log);
        hairPartitionTemplate?.Dispose();
        if (!converted)
        {
            return 0;
        }

        // 1. Strip the donor's baked hair shapes first (same pass as the
        //    ForwardToSkin strip, but in-memory so it's one load/save).
        if (ins.ShapeNamesToStrip.Count > 0)
        {
            var wanted = ins.ShapeNamesToStrip as ISet<string> ??
                         new HashSet<string>(ins.ShapeNamesToStrip, StringComparer.OrdinalIgnoreCase);
            RemoveShapesByNameCore(faceGen, wanted, ins.FaceGenNifPath, log);
        }

        var destRoot = faceGen.GetRootNode();
        if (destRoot == null)
        {
            log?.Invoke("BakeWigIntoFaceGen: FaceGen NIF has no root node.");
            return 0;
        }
        NiNode skinNode = FindNodeByName(faceGen, FaceGenSkinNodeName) ?? destRoot;

        var srcRoot = wig.GetRootNode();
        if (srcRoot == null)
        {
            log?.Invoke("BakeWigIntoFaceGen: wig NIF has no root node.");
            return 0;
        }

        // Union of bone names the kept render shapes actually skin to. Drives
        // which cloned node subtrees the FaceGen needs: working SMP facegens
        // (Project AHO; engine-verified via the G1/H bisect) carry ONLY the
        // physics chain bones — no duplicate skeleton nodes, no body anchors.
        // A duplicate "NPC Head [Head]"-style node alone dark-faces the NPC
        // (the engine merges facegen nodes onto the skeleton by name).
        var boneUnion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var keptShapes = wig.GetShapes())
        {
            foreach (var shape in keptShapes)
            {
                if (!shape.IsSkinned()) continue;
                using var boneNames = new vectorstring();
                wig.GetShapeBoneList(shape, boneNames);
                foreach (string boneName in boneNames) boneUnion.Add(boneName);
            }
        }

        uint preCloneBlockCount = faceGen.GetHeader().GetNumBlocks();

        // 2. Snapshot the wig root's child/extra-data slots BEFORE the clone:
        //    CloneChildren rewrites these refs in place to the new destination
        //    block IDs, which is how the cloned top-level blocks are found again
        //    afterwards — but the SOURCE ids must be captured now (needed to pair
        //    source shapes with their clones for the bone remap).
        var childSlots = SnapshotRefArray(srcRoot.childRefs);
        var extraSlots = SnapshotRefArray(srcRoot.extraDataRefs);

        // 3. Whole-tree merge. Every block referenced (recursively) from the wig
        //    root is cloned into the FaceGen NIF with hierarchy and strings
        //    intact; nothing in the destination references the clones yet.
        faceGen.CloneChildren(srcRoot, wig);

        var destHeader = faceGen.GetHeader();
        var srcHeader = wig.GetHeader();

        // 4. Attach the cloned top-level blocks. Render shapes go under the
        //    FaceGen skin node. Cloned NODES are normalized to the working
        //    SMP-facegen pattern (Project AHO; confirmed by the G1/H bisect):
        //    - a node whose name already exists in the FaceGen (NPC Head,
        //      NPC Spine2, ...) has its children GRAFTED onto the existing
        //      node and the duplicate deleted — duplicate skeleton nodes
        //      dark-face the NPC;
        //    - a subtree none of the kept shapes skin to (collision/constraint
        //      anchors like breasts/clavicles) is dropped entirely;
        //    - everything else (the physics chain roots) attaches to the root.
        int bakedRenderShapes = 0;
        var clonedShapePairs = new List<(NiShape Src, NiShape Dest, bool IsRender)>();
        var duplicateNodeIds = new List<uint>();   // deleted alone (children grafted/shared)
        var unkeptSubtreeRoots = new List<uint>(); // deleted recursively
        for (int slot = 0; slot < childSlots.Count; slot++)
        {
            uint srcId = childSlots[slot];
            if (srcId == uint.MaxValue) continue;
            uint destId = srcRoot.childRefs.GetBlockRef((uint)slot);
            if (destId == uint.MaxValue || destId < preCloneBlockCount)
            {
                log?.Invoke($"BakeWigIntoFaceGen: WARNING - child slot {slot} was not rewritten to a cloned block (got {destId}); skipping.");
                continue;
            }

            NiObject srcBlock = srcHeader.GetBlockById(srcId);
            NiObject destBlock = destHeader.GetBlockById(destId);

            if (srcBlock is NiShape srcShape && destBlock is NiShape destShape)
            {
                bool isRender = srcShape.HasShaderProperty();
                clonedShapePairs.Add((srcShape, destShape, isRender));
                if (isRender)
                {
                    skinNode.childRefs.AddBlockRef(destId);
                    bakedRenderShapes++;
                }
                else
                {
                    unkeptSubtreeRoots.Add(destId); // stray shader-less shape
                }
            }
            else if (destBlock is NiNode clonedNode)
            {
                var subtreeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectSubtreeNodeNames(destHeader, destId, subtreeNames);
                if (!subtreeNames.Overlaps(boneUnion))
                {
                    unkeptSubtreeRoots.Add(destId);
                    log?.Invoke($"BakeWigIntoFaceGen: dropping unneeded node subtree '{clonedNode.name?.get()}' " +
                                "(no kept shape skins to it).");
                    continue;
                }

                string? clonedName = clonedNode.name?.get();
                NiNode? existing = null;
                if (!string.IsNullOrEmpty(clonedName))
                {
                    for (uint pid = 0; pid < preCloneBlockCount; pid++)
                    {
                        if (destHeader.GetBlockById(pid) is NiNode preNode &&
                            clonedName.Equals(preNode.name?.get(), StringComparison.OrdinalIgnoreCase))
                        {
                            existing = preNode;
                            break;
                        }
                    }
                }

                if (existing != null)
                {
                    var clonedChildren = clonedNode.childRefs;
                    uint childCount = clonedChildren.GetSize();
                    int grafted = 0;
                    for (uint c = 0; c < childCount; c++)
                    {
                        uint childId = clonedChildren.GetBlockRef(c);
                        if (childId == uint.MaxValue) continue;
                        existing.childRefs.AddBlockRef(childId);
                        grafted++;
                    }
                    duplicateNodeIds.Add(destId);
                    log?.Invoke($"BakeWigIntoFaceGen: grafted {grafted} child(ren) of duplicate node " +
                                $"'{clonedName}' onto the existing FaceGen node.");
                }
                else
                {
                    destRoot.childRefs.AddBlockRef(destId);
                }
            }
            else
            {
                destRoot.childRefs.AddBlockRef(destId);
            }
        }

        // 5. Rename render shapes to their minted HeadPart EditorIDs.
        foreach (var (src, dest, isRender) in clonedShapePairs)
        {
            if (!isRender) continue;
            string? srcName = src.name?.get();
            if (srcName != null && ins.ShapeRenames.TryGetValue(srcName, out var newName) &&
                !string.IsNullOrEmpty(newName))
            {
                NifFile.RenameShape(dest, newName);
            }
        }

        // 6. Bone remap. The cloned skin instances still carry SOURCE block
        //    indices in their bone ptr lists (and skeleton root). Build a
        //    name→id map over the cloned NiNodes and rewrite each skinned
        //    shape's bone ID list in source order.
        // Pre-existing FaceGen nodes WIN name collisions: cloned duplicates of
        // skeleton nodes (NPC Head, NPC Spine2, ...) are deleted after the
        // graft, so skinning must resolve to the surviving originals. Chain
        // bones exist only as clones and resolve there.
        var clonedNodeIdsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        uint postCloneBlockCount = destHeader.GetNumBlocks();
        for (uint id = 0; id < preCloneBlockCount; id++)
        {
            if (destHeader.GetBlockById(id) is NiNode node)
            {
                string? nodeName = node.name?.get();
                if (!string.IsNullOrEmpty(nodeName) && !clonedNodeIdsByName.ContainsKey(nodeName))
                {
                    clonedNodeIdsByName[nodeName] = (int)id;
                }
            }
        }
        for (uint id = preCloneBlockCount; id < postCloneBlockCount; id++)
        {
            if (destHeader.GetBlockById(id) is NiNode node)
            {
                string? nodeName = node.name?.get();
                if (!string.IsNullOrEmpty(nodeName) && !clonedNodeIdsByName.ContainsKey(nodeName))
                {
                    clonedNodeIdsByName[nodeName] = (int)id;
                }
            }
        }

        uint destRootId = faceGen.GetBlockID(destRoot);
        foreach (var (src, dest, _) in clonedShapePairs)
        {
            if (!src.IsSkinned()) continue;

            using var srcBoneNames = new vectorstring();
            wig.GetShapeBoneList(src, srcBoneNames);

            var destBoneIds = new vectorint();
            bool remapOk = true;
            foreach (string boneName in srcBoneNames)
            {
                if (clonedNodeIdsByName.TryGetValue(boneName, out int destBoneId))
                {
                    destBoneIds.Add(destBoneId);
                }
                else
                {
                    // No collapse fallback (SynthEBD's would silently kill SMP
                    // weights): a missing bone means the merge failed.
                    log?.Invoke($"BakeWigIntoFaceGen: ERROR - bone '{boneName}' of shape " +
                                $"'{src.name?.get()}' has no cloned node in the FaceGen NIF; aborting bake.");
                    remapOk = false;
                    break;
                }
            }

            if (!remapOk)
            {
                destBoneIds.Dispose();
                return 0; // nothing saved; the on-disk FaceGen is untouched
            }

            using (destBoneIds)
            {
                faceGen.SetShapeBoneIDList(dest, destBoneIds);
            }

            // Skeleton root ptr is also a stale source index — repoint at the
            // FaceGen root (matches how the baked head shapes are rigged).
            var skinRef = dest.SkinInstanceRef();
            if (skinRef != null && !skinRef.IsEmpty() &&
                destHeader.GetBlockById(skinRef.index) is NiSkinInstance skinInst)
            {
                skinInst.targetRef.index = destRootId;
            }
        }

        // 7. Physics extra-data: the clone carried the wig's NiStringExtraData
        //    across (it is a ref of the wig root). Attach it to the FaceGen root
        //    and repoint its value at the rewritten XML.
        for (int slot = 0; slot < extraSlots.Count; slot++)
        {
            if (extraSlots[slot] == uint.MaxValue) continue;
            uint destId = srcRoot.extraDataRefs.GetBlockRef((uint)slot);
            if (destId == uint.MaxValue || destId < preCloneBlockCount) continue;

            destRoot.extraDataRefs.AddBlockRef(destId);

            if (ins.PhysicsXmlNewDataRelPath != null &&
                destHeader.GetBlockById(destId) is NiStringExtraData sed)
            {
                string? sedName = sed.name?.get();
                if (sedName != null &&
                    sedName.Equals(SmpPhysicsExtraDataName, StringComparison.OrdinalIgnoreCase))
                {
                    sed.stringData = new NiStringRef(ins.PhysicsXmlNewDataRelPath);
                }
            }
        }

        // Delete the duplicate nodes (children survive via the graft) and the
        // unkept subtrees — bottom-up, highest block id first, so earlier ids
        // stay valid across deletions. Leaving them as orphan blocks is NOT
        // safe: a stray duplicate-named node poisons the engine's name-based
        // facegen/skeleton merge just like an attached one (G1 bisect).
        var deleteIds = new List<uint>(duplicateNodeIds);
        foreach (uint subtreeRoot in unkeptSubtreeRoots)
        {
            CollectSubtreeBlockIds(destHeader, subtreeRoot, deleteIds);
        }
        deleteIds.Sort((x, y) => y.CompareTo(x));
        int deleted = 0;
        foreach (uint id in deleteIds.Distinct())
        {
            destHeader.DeleteBlock(id);
            deleted++;
        }
        if (deleted > 0)
        {
            log?.Invoke($"BakeWigIntoFaceGen: removed {deleted} duplicate/unneeded cloned block(s).");
        }

        // A provided-but-empty OnlyRenderShapes is the additions-only
        // diagnostic mode: clone just the bone tree + physics extra-data with
        // no shapes. Otherwise zero baked shapes means the bake failed.
        bool additionsOnly = ins.OnlyRenderShapes is { Count: 0 };
        if (bakedRenderShapes == 0 && !additionsOnly)
        {
            log?.Invoke($"BakeWigIntoFaceGen: no render shapes found in {ins.WigNifPath}; nothing baked.");
            return 0;
        }

        int saveResult = faceGen.Save(ins.FaceGenNifPath);
        if (saveResult != 0)
        {
            log?.Invoke($"BakeWigIntoFaceGen: save FAILED (code {saveResult}): {ins.FaceGenNifPath}");
            return 0;
        }

        log?.Invoke($"BakeWigIntoFaceGen: baked {bakedRenderShapes} render shape(s) from " +
                    $"{Path.GetFileName(ins.WigNifPath)} into {Path.GetFileName(ins.FaceGenNifPath)}.");
        return bakedRenderShapes;
    }

    /// <summary>Deletes every shape without a shader property from an in-memory
    /// NIF (SMP collision/virtual meshes). Collect-then-delete: DeleteShape
    /// re-indexes blocks.</summary>
    private static void DropShaderlessShapes(NifFile nif, Action<string>? log)
    {
        var doomed = new List<(NiShape Shape, string Name)>();
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            if (!shape.HasShaderProperty())
            {
                doomed.Add((shape, shape.name?.get() ?? "(unnamed)"));
            }
        }
        foreach (var (shape, name) in doomed)
        {
            nif.DeleteShape(shape);
            log?.Invoke($"BakeWigIntoFaceGen: dropped shader-less shape '{name}' " +
                        "(SMP collision/virtual mesh — not facegen geometry).");
        }
    }

    /// <summary>
    /// Rebuilds every shader-bearing BSTriShape of an in-memory NIF as a
    /// BSDynamicTriShape (the block type the CK bakes into facegen NIFs and the
    /// engine's tint reconciliation expects). nifly has no direct promotion for
    /// already-SSE files — OptimizeFor only converts during LE→SSE migration and
    /// SetShapeDynamic only flags legacy NiGeometryData — so this rebuilds each
    /// shape via Create (positions/triangles/UVs/normals), recalculates tangent
    /// space, and re-links the original shader/alpha/skin blocks by index before
    /// ReplaceBlock swaps it in under the same block id (keeping all parent
    /// childRefs valid). The wig has no vertex colors / eye data / second UVs
    /// (verified on the specimen), so those attributes don't need carrying.
    /// </summary>
    private static bool ConvertRenderShapesToDynamic(NifFile nif,
        NiVectorBSDismemberSkinInstancePartitionInfo? partitionTemplate, Action<string>? log)
    {
        var header = nif.GetHeader();
        var version = header.GetVersion();

        var toConvert = new List<uint>();
        for (uint id = 0; id < header.GetNumBlocks(); id++)
        {
            if (header.GetBlockById(id) is NiShape s && s.HasShaderProperty() &&
                header.GetBlockTypeStringById(id) != "BSDynamicTriShape")
            {
                toConvert.Add(id);
            }
        }

        foreach (uint id in toConvert)
        {
            if (header.GetBlockById(id) is not BSTriShape src)
            {
                log?.Invoke($"BakeWigIntoFaceGen: ERROR - render shape block {id} is " +
                            $"{header.GetBlockTypeStringById(id)}, not BSTriShape; cannot convert. Aborting.");
                return false;
            }

            string name = src.name?.get() ?? string.Empty;
            using var verts = nif.GetVertsForShape(src);
            using var uvs = nif.GetUvsForShape(src);
            using var normals = nif.GetNormalsForShape(src);
            using var tris = new vectorTriangle();
            src.GetTriangles(tris);

            // Capture the per-vertex GPU skin weights/bone indices BEFORE
            // ReplaceBlock frees the source block. Create() can't know them,
            // and an SSE skinned shape whose vertData weights are zero
            // collapses to nothing when GPU-skinned (all renderers draw
            // skinned shapes from this data via the partition).
            ushort numVerts = src.GetNumVertices();
            float[][]? vertWeights = null;
            byte[][]? vertWeightBones = null;
            if (src.IsSkinned())
            {
                vertWeights = new float[numVerts][];
                vertWeightBones = new byte[numVerts][];
                using var srcVertData = src.vertData;
                for (int v = 0; v < numVerts; v++)
                {
                    using var item = srcVertData[v];
                    var w = new float[4];
                    var b = new byte[4];
                    using (var wArr = item.weights) wArr.CopyTo(w);
                    using (var bArr = item.weightBones) bArr.CopyTo(b);
                    vertWeights[v] = w;
                    vertWeightBones[v] = b;
                }
            }

            var dyn = new BSDynamicTriShape();
            dyn.Create(version, verts, tris, uvs, normals);
            dyn.CalcTangentSpace();
            dyn.flags = src.flags;
            dyn.transform = src.transform;
            dyn.SetBounds(src.GetBounds());

            var shaderRef = src.ShaderPropertyRef();
            if (shaderRef != null && !shaderRef.IsEmpty()) dyn.SetShaderPropertyRef(shaderRef.index);
            var alphaRef = src.AlphaPropertyRef();
            if (alphaRef != null && !alphaRef.IsEmpty()) dyn.SetAlphaPropertyRef(alphaRef.index);
            var skinRef = src.SkinInstanceRef();
            bool skinned = skinRef != null && !skinRef.IsEmpty();
            if (skinned)
            {
                dyn.SetSkinned(true);
                dyn.SetSkinInstanceRef(skinRef!.index);
            }

            header.ReplaceBlock(id, dyn);
            // ReplaceBlock transfers ownership of the C++ object to the NIF
            // header (nifly's own conversion calls unique_ptr.release() here).
            // The SWIG proxy still thinks it owns it, and its finalizer would
            // double-free the block later — a delayed native heap corruption.
            System.GC.SuppressFinalize(dyn);
            NifFile.RenameShape(dyn, name);

            if (skinned && vertWeights != null && vertWeightBones != null)
            {
                // Restore the captured GPU skin weights into the rebuilt
                // vertData (SetShapeVertWeights resolves the shape by name —
                // the conversion keeps the source name; EditorID renames
                // happen later in the facegen). Mirrors what nifly's own
                // LE→SSE headParts conversion does before repartitioning.
                using var boneIds = new vectoruchar();
                using var weightsVec = new vectorfloat();
                for (ushort v = 0; v < numVerts; v++)
                {
                    boneIds.Clear();
                    weightsVec.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        if (vertWeights[v][k] > 0f)
                        {
                            boneIds.Add(vertWeightBones[v][k]);
                            weightsVec.Add(vertWeights[v][k]);
                        }
                    }
                    if (weightsVec.Count == 0) continue;
                    nif.SetShapeVertWeights(name, v, boneIds, weightsVec);
                }

                // Normalize the skin instance to the CK-baked shape:
                // BSDismemberSkinInstance carrying the donor hair's partition
                // classification, all triangles in that partition
                // (convertSkinInstance replaces a plain NiSkinInstance C++-side
                // under the same block id). UpdateSkinPartitions then rebuilds
                // the partition data — splitting by the 80-bone SSE limit with
                // each split inheriting the hair partition ID — and refreshes
                // the partition's embedded copy of the (dynamic-layout,
                // weighted) vertData, without which the shape renders nothing.
                if (partitionTemplate != null)
                {
                    using var allFirstPartition = new vectorint();
                    for (int t = 0; t < tris.Count; t++) allFirstPartition.Add(0);
                    nif.SetShapePartitions(dyn, partitionTemplate, allFirstPartition, true);
                    nif.RemoveEmptyPartitions(dyn);
                }
                nif.UpdateSkinPartitions(dyn);
            }

            log?.Invoke($"BakeWigIntoFaceGen: converted '{name}' BSTriShape → BSDynamicTriShape.");
        }

        return true;
    }

    /// <summary>Collects all NiNode names in a cloned subtree (root inclusive).</summary>
    private static void CollectSubtreeNodeNames(NiHeader header, uint nodeId, HashSet<string> names)
    {
        if (header.GetBlockById(nodeId) is not NiNode node) return;
        string? nodeName = node.name?.get();
        if (!string.IsNullOrEmpty(nodeName)) names.Add(nodeName);
        var refs = node.childRefs;
        uint size = refs.GetSize();
        for (uint i = 0; i < size; i++)
        {
            uint childId = refs.GetBlockRef(i);
            if (childId != uint.MaxValue) CollectSubtreeNodeNames(header, childId, names);
        }
    }

    /// <summary>Collects the block ids of a subtree (root inclusive) for deletion.</summary>
    private static void CollectSubtreeBlockIds(NiHeader header, uint blockId, List<uint> ids)
    {
        ids.Add(blockId);
        if (header.GetBlockById(blockId) is not NiNode node) return;
        var refs = node.childRefs;
        uint size = refs.GetSize();
        for (uint i = 0; i < size; i++)
        {
            uint childId = refs.GetBlockRef(i);
            if (childId != uint.MaxValue) CollectSubtreeBlockIds(header, childId, ids);
        }
    }

    /// <summary>Finds a shape by name (case-insensitive). The returned proxy
    /// stays valid after the enumeration container is disposed (shape blocks
    /// are owned by the NifFile).</summary>
    private static NiShape? FindShapeByName(NifFile nif, string name)
    {
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            if (name.Equals(shape.name?.get(), StringComparison.OrdinalIgnoreCase)) return shape;
        }
        return null;
    }

    /// <summary>Finds a NiNode by name (case-insensitive). Port of the lookup at
    /// the heart of SynthEBD FaceGenPatcher.FindFaceGenSkinNode.</summary>
    private static NiNode? FindNodeByName(NifFile nif, string nodeName)
    {
        var header = nif.GetHeader();
        uint blockCount = header.GetNumBlocks();
        for (uint id = 0; id < blockCount; id++)
        {
            if (header.GetBlockById(id) is NiNode node)
            {
                string? name = node.name?.get();
                if (name != null && name.Equals(nodeName, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
        }
        return null;
    }

    /// <summary>Captures the current ref indices of a block-ref array, slot by
    /// slot (empty refs come back as uint.MaxValue). Used to pair pre-clone
    /// source ids with their post-clone destination rewrites.</summary>
    private static List<uint> SnapshotRefArray(NiBlockRefArrayNiAVObject? refs)
    {
        var slots = new List<uint>();
        if (refs == null) return slots;
        uint size = refs.GetSize();
        for (uint i = 0; i < size; i++) slots.Add(refs.GetBlockRef(i));
        return slots;
    }

    private static List<uint> SnapshotRefArray(NiBlockRefArrayNiExtraData? refs)
    {
        var slots = new List<uint>();
        if (refs == null) return slots;
        uint size = refs.GetSize();
        for (uint i = 0; i < size; i++) slots.Add(refs.GetBlockRef(i));
        return slots;
    }
}