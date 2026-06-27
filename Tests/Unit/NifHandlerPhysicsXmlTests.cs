using System.IO;
using FluentAssertions;
using nifly;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Round-trip coverage for <see cref="NifHandler.GetPhysicsXmlPathsFromNif"/> — the SMP/HDT
/// physics-XML detector. HDT-SMP links a mesh to its physics config by an NiStringExtraData block
/// inside the NIF (named "HDT Skinned Mesh Physics Object") whose value is the path to a .xml; the
/// detector returns that value so the asset pipeline can copy it.
///
/// Each test builds a real fixture NIF with nifly (BSFadeNode root + the chosen extra-data entries),
/// saves it to a throwaway temp file, then asserts on what the detector pulls back out. The build
/// helpers used (<c>NiVersion.getSSE</c>, <c>CreateNamedBSFadeNode</c>, <c>AddStringExtraDataToNode</c>,
/// <c>Save</c>) are the same nifly surface the production reader runs against, so this exercises the
/// genuine parse path rather than a stub.
/// </summary>
public class NifHandlerPhysicsXmlTests
{
    private const string SmpMarker = "HDT Skinned Mesh Physics Object";

    /// <summary>
    /// Writes a NIF with a "Scene Root" BSFadeNode carrying the given (extra-data name, value) entries,
    /// and returns its absolute path under <paramref name="tmp"/>.
    /// </summary>
    private static string BuildNif(TempDir tmp, string fileName, params (string Name, string Value)[] extraData)
    {
        var path = tmp.Combine(fileName);
        var version = NiVersion.getSSE();
        using var nif = new NifFile();
        nif.CreateNamedBSFadeNode(version, "Scene Root");
        var rootId = (int)nif.GetBlockID(nif.GetRootNode());
        foreach (var (name, value) in extraData)
        {
            nif.AddStringExtraDataToNode(rootId, name, value);
        }
        nif.Save(path).Should().Be(0, "the fixture NIF should save cleanly");
        return path;
    }

    [Fact]
    public void MarkerNamedExtraData_IsExtracted()
    {
        using var tmp = new TempDir();
        var nif = BuildNif(tmp, "hair.nif",
            (SmpMarker, @"meshes\actors\character\Lyodraf\Hair\13.xml"));

        NifHandler.GetPhysicsXmlPathsFromNif(nif)
            .Should().BeEquivalentTo(new[] { @"meshes\actors\character\Lyodraf\Hair\13.xml" });
    }

    [Fact]
    public void MarkerName_IsMatchedCaseInsensitively()
    {
        using var tmp = new TempDir();
        // Same marker, lower-cased: still recognized even though the value here does NOT end in .xml,
        // proving the match is driven by the name and not only the value heuristic.
        var nif = BuildNif(tmp, "hair.nif",
            ("hdt skinned mesh physics object", @"meshes\physics\config"));

        NifHandler.GetPhysicsXmlPathsFromNif(nif)
            .Should().BeEquivalentTo(new[] { @"meshes\physics\config" });
    }

    [Fact]
    public void DifferentlyNamedButXmlValued_IsExtractedByCatchAll()
    {
        using var tmp = new TempDir();
        // Legacy / variant marker names (e.g. "HDT Havok Path") still resolve because any
        // .xml-valued extra data is treated as a physics reference.
        var nif = BuildNif(tmp, "wig.nif",
            ("HDT Havok Path", @"meshes\actors\character\body\physics.xml"));

        NifHandler.GetPhysicsXmlPathsFromNif(nif)
            .Should().BeEquivalentTo(new[] { @"meshes\actors\character\body\physics.xml" });
    }

    [Fact]
    public void NonMarkerNonXmlExtraData_IsIgnored()
    {
        using var tmp = new TempDir();
        // Ordinary string extra data (not the marker, value not a .xml) must not be picked up.
        var nif = BuildNif(tmp, "plain.nif",
            ("Prn", "NPC Head [Head]"),
            ("BBX", "0.0 0.0 0.0"));

        NifHandler.GetPhysicsXmlPathsFromNif(nif).Should().BeEmpty();
    }

    [Fact]
    public void NoExtraData_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        var nif = BuildNif(tmp, "bare.nif");

        NifHandler.GetPhysicsXmlPathsFromNif(nif).Should().BeEmpty();
    }

    [Fact]
    public void MixedExtraData_ReturnsOnlyPhysicsReferences()
    {
        using var tmp = new TempDir();
        var nif = BuildNif(tmp, "mixed.nif",
            ("Prn", "NPC Head [Head]"),                                   // ignored
            (SmpMarker, @"meshes\actors\character\hair\smp.xml"),          // marker -> kept
            ("HDT Havok Path", @"meshes\actors\character\body\jiggle.xml"),// xml value -> kept
            ("SomethingElse", "not a path"));                             // ignored

        NifHandler.GetPhysicsXmlPathsFromNif(nif).Should().BeEquivalentTo(new[]
        {
            @"meshes\actors\character\hair\smp.xml",
            @"meshes\actors\character\body\jiggle.xml",
        });
    }

    [Fact]
    public void DuplicateReferences_AreDeduplicated()
    {
        using var tmp = new TempDir();
        var nif = BuildNif(tmp, "dupe.nif",
            (SmpMarker, @"meshes\hair\smp.xml"),
            ("HDT Havok Path", @"meshes\hair\smp.xml")); // same path via two blocks

        NifHandler.GetPhysicsXmlPathsFromNif(nif)
            .Should().ContainSingle().Which.Should().Be(@"meshes\hair\smp.xml");
    }

    [Fact]
    public void ReferenceMatching_IsCaseInsensitiveOnTheValue()
    {
        using var tmp = new TempDir();
        // ".XML" upper-cased should still be recognized as a physics reference.
        var nif = BuildNif(tmp, "upper.nif",
            ("HDT Havok Path", @"meshes\hair\SMP.XML"));

        NifHandler.GetPhysicsXmlPathsFromNif(nif)
            .Should().ContainSingle().Which.Should().Be(@"meshes\hair\SMP.XML");
    }
}
