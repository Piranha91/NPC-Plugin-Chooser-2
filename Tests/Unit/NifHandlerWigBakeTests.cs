using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Wig→HeadPart bake feasibility spike, checkpoint A (the nifly API leg):
/// exercises <see cref="NifHandler.BakeWigIntoFaceGen"/> against the real
/// FoxGlove Auri specimens — the 13-render-shape / 3-collision-shape / ~300
/// SMP-bone wig merged into the baked FaceGen. Proves that CloneChildren
/// carries the whole scene (hierarchy, shaders, extra-data) across NIFs, that
/// the childRefs rewrite is readable, and that the name-based bone remap
/// yields valid skin instances. Machine-local: gracefully skips when the
/// specimen mod isn't installed (suite convention). Works on temp copies; the
/// source files are never modified.
/// </summary>
public class NifHandlerWigBakeTests
{
    private const string FoxGloveModRoot =
        @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint - Test";

    private const string FaceGenNif =
        FoxGloveModRoot + @"\meshes\actors\character\FaceGenData\FaceGeom\018auri.esp\00000D63.NIF";

    private const string WigNif =
        FoxGloveModRoot + @"\meshes\actors\FoxGlove Auri\Wig\22a_1.nif";

    private const string PhysicsXml =
        FoxGloveModRoot + @"\meshes\actors\FoxGlove Auri\Wig\22a.xml";

    private const string NewPhysicsXmlRelPath = @"meshes\NPC2\WigPhysics\FoxGlove_Wig.xml";

    /// <summary>EditorID-style prefix applied to every baked render shape.</summary>
    private const string RenamePrefix = "NPC2Wig_FoxGlove_Wig_";

    private static bool SpecimensMissing => !File.Exists(FaceGenNif) || !File.Exists(WigNif);

    private static Dictionary<string, string> BuildRenames(IEnumerable<string> renderShapeNames) =>
        renderShapeNames.ToDictionary(n => n, n => RenamePrefix + SanitizeForEditorId(n),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>EditorIDs allow alphanumerics and underscores; shape names like
    /// "01a_inv" are already safe, but be defensive about future specimens.</summary>
    private static string SanitizeForEditorId(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    [Fact]
    public void GetRenderShapeNames_FindsThirteenRenderShapes_ExcludingCollision()
    {
        if (SpecimensMissing) return;

        var names = NifHandler.GetRenderShapeNames(WigNif);

        names.Should().HaveCount(13);
        names.Should().Contain(new[] { "01a", "01L", "01b", "F", "Hl" });
        names.Should().NotContain(new[] { "VirtualGroundBDO", "colWigMiniBDO", "colHeadBDO" },
            "collision/virtual shapes have no shader and get no HeadPart");
    }

    [Fact]
    public void BakeWigIntoFaceGen_MergesFullWigScene()
    {
        if (SpecimensMissing) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "npc2-wigbake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempFaceGen = Path.Combine(tempDir, "00000D63.NIF");
        File.Copy(FaceGenNif, tempFaceGen);
        try
        {
            var renderShapes = NifHandler.GetRenderShapeNames(WigNif);
            var renames = BuildRenames(renderShapes);
            var logLines = new List<string>();

            // Hair + BOTH its ExtraParts (hairline AND scalp): the record loses
            // all three when the Hair part is replaced by the wig, so all three
            // baked shapes must go — an orphan baked shape dark-faces the NPC
            // (engine-verified: leaving FoxGloveHairScalp baked dark-faced Auri).
            int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                    tempFaceGen, WigNif, renames,
                    new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" },
                    NewPhysicsXmlRelPath),
                logLines.Add);

            baked.Should().Be(13, string.Join(Environment.NewLine, logLines));

            // ---- Reload and dissect the result ----
            using var nif = new nifly.NifFile();
            nif.Load(tempFaceGen).Should().Be(0, "the baked NIF must remain loadable");
            var header = nif.GetHeader();

            // Donor hair stripped, original head kept.
            var shapeNames = new List<string>();
            var shapesByName = new Dictionary<string, nifly.NiShape>(StringComparer.OrdinalIgnoreCase);
            var shapes = nif.GetShapes();
            foreach (var shape in shapes)
            {
                string? name = shape.name?.get();
                if (string.IsNullOrEmpty(name)) continue;
                shapeNames.Add(name);
                shapesByName[name] = shape;
            }

            shapeNames.Should().NotContain("FoxGloveHairMesh");
            shapeNames.Should().NotContain("FoxGloveHairlineMesh");
            shapeNames.Should().NotContain("FoxGloveHairScalp");
            shapeNames.Should().Contain("FoxGloveHead");

            // All 13 render shapes present under their new names, and CONVERTED
            // to BSDynamicTriShape (the CK-baked block type the engine's tint
            // reconciliation expects — plain BSTriShape dark-faces). Shader-less
            // SMP collision meshes are dropped (no working facegen carries them).
            foreach (var newName in renames.Values)
            {
                shapeNames.Should().Contain(newName);
                uint shapeBlockId = nif.GetBlockID(shapesByName[newName]);
                header.GetBlockTypeStringById(shapeBlockId).Should().Be("BSDynamicTriShape",
                    $"baked wig shape '{newName}' must use the CK's facegen block type");
                // CK-baked facegen shapes universally use BSDismemberSkinInstance
                // (hair-classified partitions); plain NiSkinInstance has no
                // working facegen precedent.
                var skinRef = shapesByName[newName].SkinInstanceRef();
                skinRef.IsEmpty().Should().BeFalse();
                header.GetBlockTypeStringById(skinRef.index).Should().Be("BSDismemberSkinInstance",
                    $"baked wig shape '{newName}' must carry dismember partition data");
            }
            shapeNames.Should().NotContain(new[] { "VirtualGroundBDO", "colWigMiniBDO", "colHeadBDO" });

            // Render shapes hang under BSFaceGenNiNodeSkinned; collision shapes
            // hang under the root (which may itself be the skin node).
            var skinNode = FindNode(nif, NifHandler.FaceGenSkinNodeName);
            skinNode.Should().NotBeNull("FaceGen NIFs carry the skin node");
            var skinChildIds = GetChildIds(skinNode!);
            foreach (var newName in renames.Values)
            {
                uint shapeId = nif.GetBlockID(shapesByName[newName]);
                skinChildIds.Should().Contain(shapeId,
                    $"render shape '{newName}' must live under {NifHandler.FaceGenSkinNodeName}");
            }

            // Every skinned cloned shape's bone IDs resolve to NiNodes with the
            // right names — the stale-source-pointer remap worked.
            uint blockCount = header.GetNumBlocks();
            foreach (var pair in renames)
            {
                var destShape = shapesByName[pair.Value];
                if (!destShape.IsSkinned()) continue;

                using var boneIds = new nifly.vectorint();
                nif.GetShapeBoneIDList(destShape, boneIds);
                boneIds.Count.Should().BeGreaterThan(0, $"'{pair.Value}' is skinned");
                using var boneNames = new nifly.vectorstring();
                nif.GetShapeBoneList(destShape, boneNames);
                boneNames.Count.Should().Be(boneIds.Count);

                for (int i = 0; i < boneIds.Count; i++)
                {
                    int boneId = boneIds[i];
                    ((uint)boneId).Should().BeLessThan(blockCount,
                        $"bone {i} of '{pair.Value}' must reference a real block");
                    var boneBlock = header.GetBlockById((uint)boneId);
                    boneBlock.Should().BeAssignableTo<nifly.NiNode>(
                        $"bone {i} of '{pair.Value}' must be a NiNode");
                    ((nifly.NiNode)boneBlock).name?.get().Should().Be(boneNames[i]);
                }

                // The rebuilt vertData must carry GPU skin weights — all-zero
                // weights GPU-skin every vertex to nothing (shape invisible in
                // game and NifSkope even though the blocks look intact).
                // GetShapes() yields non-downcast NiShape proxies; go through
                // GetBlockById for the typed BSTriShape view of the same block.
                var dynShape = (nifly.BSTriShape)header.GetBlockById(nif.GetBlockID(destShape));
                using var vertData = dynShape.vertData;
                bool anyWeight = false;
                int scan = Math.Min(vertData.Count, 200);
                for (int v = 0; v < scan && !anyWeight; v++)
                {
                    using var item = vertData[v];
                    using var w = item.weights;
                    for (int k = 0; k < 4; k++)
                    {
                        if (w[k] > 0f) { anyWeight = true; break; }
                    }
                }
                anyWeight.Should().BeTrue(
                    $"'{pair.Value}' must have nonzero GPU skin weights in its vertex data");
            }

            // The SMP bone chains came across: the wig carries ~300 kinematic
            // chain NiNodes named BDOr_H_PPW22_*. And NO node name may appear
            // twice — duplicate skeleton nodes (a second "NPC Head [Head]")
            // dark-face the NPC (engine-verified via the G1/H bisect), so the
            // bake grafts chains onto existing nodes instead of duplicating.
            int chainNodes = 0;
            var nodeNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (uint id = 0; id < blockCount; id++)
            {
                if (header.GetBlockById(id) is nifly.NiNode anyNode)
                {
                    string nodeName = anyNode.name?.get() ?? "";
                    if (nodeName.StartsWith("BDOr_H_PPW22", StringComparison.OrdinalIgnoreCase)) chainNodes++;
                    if (nodeName.Length > 0)
                        nodeNameCounts[nodeName] = nodeNameCounts.TryGetValue(nodeName, out var c) ? c + 1 : 1;
                }
            }
            chainNodes.Should().BeGreaterThan(100,
                "the wig's SMP bone-chain NiNodes must be cloned with the scene");
            nodeNameCounts.Where(kv => kv.Value > 1).Should().BeEmpty(
                "duplicate node names dark-face the NPC (engine name-merges facegen nodes onto the skeleton)");

            // Physics extra-data present and repointed at the rewritten XML.
            var physicsPaths = NifHandler.GetPhysicsXmlPathsFromNif(tempFaceGen);
            physicsPaths.Should().Contain(NewPhysicsXmlRelPath);

            // Shader survived the clone (texture paths reachable) — the wig's
            // hair diffuse must be among the NIF's referenced textures.
            var textures = NifHandler.GetExtraTexturesFromNif(tempFaceGen);
            textures.Should().Contain(t =>
                t.Contains(@"wig\meyvet\13a", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BakedFaceGen_LoadsThroughRendererMeshSurvey()
    {
        if (SpecimensMissing) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "npc2-wigbake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempFaceGen = Path.Combine(tempDir, "00000D63.NIF");
        File.Copy(FaceGenNif, tempFaceGen);
        try
        {
            var renames = BuildRenames(NifHandler.GetRenderShapeNames(WigNif));
            NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                tempFaceGen, WigNif, renames,
                new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" },
                NewPhysicsXmlRelPath)).Should().Be(13);

            // The mugshot pipeline reads FaceGen NIFs through the renderer's
            // survey/parse path — the baked NIF must not break it.
            var meshBuilder = new CharacterViewer.Rendering.NifMeshBuilder(
                new SilentViewerLogger(), new CharacterViewer.Rendering.CharacterViewerLogGate());
            var survey = meshBuilder.SurveyNif(tempFaceGen);
            survey.LoadOk.Should().BeTrue(survey.Error);
            var surveyedNames = survey.Shapes.Select(s => s.ShapeName).ToList();
            surveyedNames.Should().Contain(renames.Values.First());
            surveyedNames.Should().Contain("FoxGloveHead");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SmpXmlRewriter_RenamesShapeEntries_LeavesCollisionAlone()
    {
        if (!File.Exists(PhysicsXml)) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "npc2-wigxml-" + Guid.NewGuid().ToString("N"));
        try
        {
            var renames = BuildRenames(new[] { "01a", "01L", "01b", "F", "Hl" });
            string dest = Path.Combine(tempDir, "FoxGlove_Wig.xml");

            int renamed = SmpXmlRewriter.RewriteShapeNames(PhysicsXml, dest, renames);

            // 22a.xml has per-vertex-shape entries for 01a, 01b and F (01L/Hl
            // have no physics entries of their own).
            renamed.Should().Be(3);

            string content = File.ReadAllText(dest);
            content.Should().Contain(RenamePrefix + "01a");
            content.Should().Contain(RenamePrefix + "01b");
            content.Should().Contain(RenamePrefix + "F");
            content.Should().NotContain("name=\"01a\"");
            // Collision entries untouched (they keep their NIF shape names).
            content.Should().Contain("VirtualGroundBDO");
            content.Should().Contain("colWigMiniBDO");
            content.Should().Contain("colHeadBDO");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private sealed class SilentViewerLogger : CharacterViewer.Rendering.ICharacterViewerLogger
    {
        public void LogMessage(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception ex) { }
    }

    private static nifly.NiNode? FindNode(nifly.NifFile nif, string name)
    {
        var header = nif.GetHeader();
        uint blockCount = header.GetNumBlocks();
        for (uint id = 0; id < blockCount; id++)
        {
            if (header.GetBlockById(id) is nifly.NiNode node &&
                name.Equals(node.name?.get(), StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }
        return null;
    }

    private static HashSet<uint> GetChildIds(nifly.NiNode node)
    {
        var ids = new HashSet<uint>();
        var refs = node.childRefs;
        uint size = refs.GetSize();
        for (uint i = 0; i < size; i++)
        {
            uint id = refs.GetBlockRef(i);
            if (id != uint.MaxValue) ids.Add(id);
        }
        return ids;
    }
}
