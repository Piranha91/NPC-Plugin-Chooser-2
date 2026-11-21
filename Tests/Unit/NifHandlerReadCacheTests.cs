using System.IO;
using FluentAssertions;
using NiflySharp;
using NiflySharp.Blocks;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The read-only NIF lookups share one parse per version of a file. A patch run edits copied NIFs
/// between lookups, so the guarantee that matters is that no lookup ever answers for an older
/// version of the file, and that a caller can't change what another caller is told.
/// </summary>
public class NifHandlerReadCacheTests
{
    private const string SmpMarker = "HDT Skinned Mesh Physics Object";

    private static void WriteNif(string path, string xmlValue)
    {
        var nif = new NifFile(NiVersion.GetSSE());
        var root = new BSFadeNode { Name = new NiStringRef("Scene Root") };
        nif.AddBlock(root);
        root.ExtraDataList = new NiBlockRefArray<NiExtraData>();
        int id = nif.AddBlock(new NiStringExtraData
        {
            Name = new NiStringRef(SmpMarker),
            StringData = new NiStringRef(xmlValue)
        });
        root.ExtraDataList.AddBlockRef(id);
        nif.Save(path);
    }

    [Fact]
    public void Lookup_AfterTheFileChanges_ReportsTheNewFile()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("a.nif");
        WriteNif(path, "meshes\\first.xml");
        NifHandler.GetPhysicsXmlPathsFromNif(path).Should().BeEquivalentTo("meshes\\first.xml");

        WriteNif(path, "meshes\\secnd.xml");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

        NifHandler.GetPhysicsXmlPathsFromNif(path).Should().BeEquivalentTo("meshes\\secnd.xml");
    }

    [Fact]
    public void ReturnedSets_AreTheCallersOwn()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("b.nif");
        WriteNif(path, "meshes\\first.xml");

        NifHandler.GetPhysicsXmlPathsFromNif(path).Add("meshes\\added.xml");

        NifHandler.GetPhysicsXmlPathsFromNif(path).Should().BeEquivalentTo("meshes\\first.xml");
    }

    [Fact]
    public void MissingFile_ReportsNothing_AndAFileAppearingLaterIsRead()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("c.nif");
        NifHandler.GetPhysicsXmlPathsFromNif(path).Should().BeEmpty();
        NifHandler.GetShapeTextureDetails(path).Should().BeEmpty();

        WriteNif(path, "meshes\\first.xml");

        NifHandler.GetPhysicsXmlPathsFromNif(path).Should().BeEquivalentTo("meshes\\first.xml");
    }

    [Fact]
    public void CorruptFile_ReportsNothing()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("d.nif");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        NifHandler.GetPhysicsXmlPathsFromNif(path).Should().BeEmpty();
        NifHandler.GetExtraTexturesFromNif(path).Should().BeEmpty();
        NifHandler.GetShapeTextureDetails(path).Should().BeEmpty();
    }
}
