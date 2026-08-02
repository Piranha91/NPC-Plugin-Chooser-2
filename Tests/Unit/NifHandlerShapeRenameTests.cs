using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="NifHandler.RenameShapesByName"/> against the real RS Children specimen — Assur's
/// FaceGen, whose mesh carries <c>HairMaleImperialChild01</c> / <c>HairLineMaleImperialChild01</c>.
///
/// <para><b>Why it exists.</b> The engine pairs an NPC's head parts to its baked geometry BY NAME
/// (<c>TESNPC::DismemberHeadParts</c> → <c>GetObjectByName(headPart-&gt;formEditorID)</c>). Include
/// As New mints a duplicate head part as <c>&lt;original&gt;_&lt;sourcePlugin&gt;</c> so one mod's
/// copy of a shared record stays off every other mod's NPCs — which left the record naming a shape
/// the mesh does not carry. Assur and Svari were confirmed dark-faced in game. Renaming the shapes
/// to follow the records closes the loop.</para>
///
/// <para>Machine-local: gracefully skips when the specimen mod isn't installed, following the
/// suite's convention. Always works on a temp copy; the source NIF is never modified.</para>
/// </summary>
public class NifHandlerShapeRenameTests
{
    private const string AssurFaceGenNif =
        @"S:\Skyrim NPC Selection\mods\RS Children Overhaul\meshes\actors\character\facegendata\facegeom\skyrim.esm\0001C18A.NIF";

    private const string Suffix = "_RSkyrimChildren.esm";

    /// <summary>Temp copy of the specimen, or null when it isn't installed here.</summary>
    private static string? Stage()
    {
        if (!File.Exists(AssurFaceGenNif)) return null;
        string temp = Path.Combine(Path.GetTempPath(), "npc2-renametest-" + Guid.NewGuid().ToString("N") + ".nif");
        File.Copy(AssurFaceGenNif, temp);
        return temp;
    }

    [Fact]
    public void RenamesTheHairShapes_ToTheirIncludeAsNewEditorIds()
    {
        var nif = Stage();
        if (nif == null) return; // specimen not installed on this machine
        try
        {
            var before = NifHandler.GetRenderShapeNames(nif);
            before.Should().Contain("HairMaleImperialChild01");
            before.Should().Contain("HairLineMaleImperialChild01");

            var renames = new Dictionary<string, string>
            {
                ["HairMaleImperialChild01"] = "HairMaleImperialChild01" + Suffix,
                ["HairLineMaleImperialChild01"] = "HairLineMaleImperialChild01" + Suffix,
            };

            NifHandler.RenameShapesByName(nif, renames).Should().Be(2);

            var after = NifHandler.GetRenderShapeNames(nif);
            after.Should().Contain("HairMaleImperialChild01" + Suffix);
            after.Should().Contain("HairLineMaleImperialChild01" + Suffix);
            after.Should().NotContain("HairMaleImperialChild01");
            after.Should().NotContain("HairLineMaleImperialChild01");

            after.Count.Should().Be(before.Count, "renaming must not add or drop shapes");
            after.Except(renames.Values).Should().BeEquivalentTo(before.Except(renames.Keys),
                "every shape the map does not name is untouched");

            using var reloaded = new nifly.NifFile();
            reloaded.Load(nif).Should().Be(0, "the renamed NIF must remain loadable");
        }
        finally { File.Delete(nif); }
    }

    [Fact]
    public void SecondPass_IsANoOp()
    {
        // RunPatchingLogic runs once per output plugin; a re-applied rename must not corrupt a
        // file whose shapes already carry the new names.
        var nif = Stage();
        if (nif == null) return;
        try
        {
            var renames = new Dictionary<string, string>
            {
                ["HairMaleImperialChild01"] = "HairMaleImperialChild01" + Suffix,
            };

            NifHandler.RenameShapesByName(nif, renames).Should().Be(1);
            var afterFirst = NifHandler.GetRenderShapeNames(nif);

            NifHandler.RenameShapesByName(nif, renames).Should().Be(0);
            NifHandler.GetRenderShapeNames(nif).Should().BeEquivalentTo(afterFirst);
        }
        finally { File.Delete(nif); }
    }

    [Fact]
    public void UnknownNames_LeaveTheFileUntouched()
    {
        var nif = Stage();
        if (nif == null) return;
        try
        {
            var before = NifHandler.GetRenderShapeNames(nif);
            var stamp = File.GetLastWriteTimeUtc(nif);

            NifHandler.RenameShapesByName(nif,
                new Dictionary<string, string> { ["NoSuchShape"] = "AlsoNoSuchShape" })
                .Should().Be(0);

            NifHandler.GetRenderShapeNames(nif).Should().BeEquivalentTo(before);
            File.GetLastWriteTimeUtc(nif).Should().Be(stamp, "a no-op must not rewrite the file");
        }
        finally { File.Delete(nif); }
    }

    [Fact]
    public void RenamingOntoANamePresentInTheFile_IsRefused()
    {
        // Two shapes sharing a name are indistinguishable to the by-name lookup — the exact
        // failure this mechanism exists to prevent, so it must not create one.
        var nif = Stage();
        if (nif == null) return;
        try
        {
            var before = NifHandler.GetRenderShapeNames(nif);

            NifHandler.RenameShapesByName(nif,
                new Dictionary<string, string> { ["HairMaleImperialChild01"] = "HairLineMaleImperialChild01" })
                .Should().Be(0);

            NifHandler.GetRenderShapeNames(nif).Should().BeEquivalentTo(before);
        }
        finally { File.Delete(nif); }
    }

    [Fact]
    public void EmptyRenameMap_ShortCircuits()
    {
        // Runs per NPC and is empty for nearly all of them, so it must not touch the disk.
        NifHandler.RenameShapesByName(@"Z:\definitely\missing\file.nif", new Dictionary<string, string>())
            .Should().Be(0);
    }

    [Fact]
    public void MissingFile_ReportsRatherThanThrows()
    {
        var log = new List<string>();

        NifHandler.RenameShapesByName(
            Path.Combine(Path.GetTempPath(), "npc2-no-such-" + Path.GetRandomFileName() + ".nif"),
            new Dictionary<string, string> { ["A"] = "B" }, log.Add).Should().Be(0);

        log.Should().ContainSingle().Which.Should().Contain("failed to load");
    }
}
