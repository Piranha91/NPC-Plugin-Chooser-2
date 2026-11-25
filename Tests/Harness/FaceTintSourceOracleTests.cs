using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Known-answer tests for <see cref="FaceTintSourceOracle"/>: the golden combos only ever show it
/// agreeing, so these show it DISAGREEING when the delivered tint is wrong, and declining to judge
/// when it has no single source. No game needed: mod folders, the output folder and the "game"
/// archives are temp files, and the selections are the golden suite's own.
/// </summary>
public class FaceTintSourceOracleTests
{
    private const string OrdinaryWomen = "The Ordinary Women SSE";
    private const string BaseGame = "Base Game";

    // Golden selections exercised here (GoldenComboSettingsBuilder.Selections):
    private static readonly FormKey Delphine = FormKey.Factory("013478:Skyrim.esm");   // <- Gerdur, The Ordinary Women
    private static readonly FormKey Gerdur = FormKey.Factory("01347C:Skyrim.esm");
    private static readonly FormKey Lucan = FormKey.Factory("01347A:Skyrim.esm");      // <- General Tullius, Base Game
    private static readonly FormKey Tullius = FormKey.Factory("01327E:Skyrim.esm");

    private static readonly byte[] DonorTint = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
    private static readonly byte[] OtherTint = Enumerable.Range(0, 4096).Select(i => (byte)(i * 11 + 3)).ToArray();

    private static void WriteFile(string root, string rel, byte[] bytes)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static GoldenOutputConfig ConfigWithMod(string modFolder) => new()
    {
        AppearanceMods =
        {
            [OrdinaryWomen] = new GoldenOutputConfig.AppearanceModEntry { Folders = { modFolder } },
        },
    };

    private static FaceTintSourceOracle.Verdict VerdictFor(
        GoldenOutputConfig config, string dataFolder, string outDir, FormKey target,
        IReadOnlyDictionary<FormKey, FormKey>? surrogates = null) =>
        FaceTintSourceOracle.Check(config, dataFolder, outDir, surrogates).Single(v => v.Target == target);

    private static void WriteArchive(string path, string archiveRelPath, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(archiveRelPath)!;
        var name = Path.GetFileName(archiveRelPath);
        BsaFixtureWriter.Write(path, new[]
        {
            (folder, (IReadOnlyList<(string Name, byte[] Data)>)new[] { (name, bytes) }),
        });
    }

    [Fact]
    public void GuestFace_DeliveredTintEqualsTheDonorsFile_IsVerified()
    {
        using var tmp = new TempDir("tintoracle");
        var mod = tmp.Dir("mod"); var data = tmp.Dir("data"); var output = tmp.Dir("out");
        WriteFile(mod, FaceTintSourceOracle.TintRelPath(Gerdur), DonorTint);
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Delphine), DonorTint);

        var v = VerdictFor(ConfigWithMod(mod), data, output, Delphine);

        v.Outcome.Should().Be(FaceTintSourceOracle.Outcome.Verified);
        v.FreshHash.Should().Be(v.SourceHash);
    }

    [Fact]
    public void GuestFace_TargetsOwnTintDeliveredInsteadOfTheDonors_IsAMismatch()
    {
        // The realistic failure: the guest face was not applied and the target kept a tint that is
        // perfectly valid, just not the donor's. The mod ships both, so only the selection tells them apart.
        using var tmp = new TempDir("tintoracle");
        var mod = tmp.Dir("mod"); var data = tmp.Dir("data"); var output = tmp.Dir("out");
        WriteFile(mod, FaceTintSourceOracle.TintRelPath(Gerdur), DonorTint);
        WriteFile(mod, FaceTintSourceOracle.TintRelPath(Delphine), OtherTint);
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Delphine), OtherTint);

        var v = VerdictFor(ConfigWithMod(mod), data, output, Delphine);

        v.Outcome.Should().Be(FaceTintSourceOracle.Outcome.Mismatch);
        v.FreshHash.Should().NotBe(v.SourceHash);
    }

    [Fact]
    public void OneChangedByte_IsAMismatch()
    {
        using var tmp = new TempDir("tintoracle");
        var mod = tmp.Dir("mod"); var data = tmp.Dir("data"); var output = tmp.Dir("out");
        var altered = (byte[])DonorTint.Clone();
        altered[2048] ^= 0x01;
        WriteFile(mod, FaceTintSourceOracle.TintRelPath(Gerdur), DonorTint);
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Delphine), altered);

        VerdictFor(ConfigWithMod(mod), data, output, Delphine)
            .Outcome.Should().Be(FaceTintSourceOracle.Outcome.Mismatch);
    }

    [Fact]
    public void NothingDelivered_IsReportedAndNotJudged()
    {
        using var tmp = new TempDir("tintoracle");
        var mod = tmp.Dir("mod"); var data = tmp.Dir("data"); var output = tmp.Dir("out");
        WriteFile(mod, FaceTintSourceOracle.TintRelPath(Gerdur), DonorTint);

        VerdictFor(ConfigWithMod(mod), data, output, Delphine)
            .Outcome.Should().Be(FaceTintSourceOracle.Outcome.NotDelivered);
    }

    [Fact]
    public void ModDoesNotShipTheDonorsTint_IsUnresolved_NotAMismatch()
    {
        using var tmp = new TempDir("tintoracle");
        var mod = tmp.Dir("mod"); var data = tmp.Dir("data"); var output = tmp.Dir("out");
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Delphine), DonorTint);

        var v = VerdictFor(ConfigWithMod(mod), data, output, Delphine);

        v.Outcome.Should().Be(FaceTintSourceOracle.Outcome.SourceUnresolved);
        v.SourceHash.Should().BeNull("an oracle with no source must not invent one");
    }

    [Fact]
    public void SkyPatcherOutput_IsReadUnderTheSurrogatesFormKey()
    {
        using var tmp = new TempDir("tintoracle");
        var mod = tmp.Dir("mod"); var data = tmp.Dir("data"); var output = tmp.Dir("out");
        var surrogate = FormKey.Factory("000802:NPC.esp");
        WriteFile(mod, FaceTintSourceOracle.TintRelPath(Gerdur), DonorTint);
        WriteFile(output, FaceTintSourceOracle.TintRelPath(surrogate), DonorTint);
        var map = new Dictionary<FormKey, FormKey> { [Delphine] = surrogate };

        var v = VerdictFor(ConfigWithMod(mod), data, output, Delphine, map);

        v.Outcome.Should().Be(FaceTintSourceOracle.Outcome.Verified);
        v.FreshRel.Should().Be("textures/actors/character/facegendata/facetint/npc.esp/00000802.dds");
    }

    [Fact]
    public void BaseGame_SourceIsReadFromTheGamesArchive()
    {
        using var tmp = new TempDir("tintoracle");
        var data = tmp.Dir("data"); var output = tmp.Dir("out");
        var inArchive = FaceTintSourceOracle.TintRelPath(Tullius).Replace('/', '\\');
        WriteArchive(Path.Combine(data, "Skyrim - Textures0.bsa"), inArchive, DonorTint);
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Lucan), DonorTint);

        var v = VerdictFor(new GoldenOutputConfig(), data, output, Lucan);

        v.Outcome.Should().Be(FaceTintSourceOracle.Outcome.Verified);
        v.Detail.Should().Contain("Skyrim - Textures0.bsa");

        // ...and a stale copy of that tint in the output (what the stored reference had become) is caught.
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Lucan), OtherTint);
        VerdictFor(new GoldenOutputConfig(), data, output, Lucan)
            .Outcome.Should().Be(FaceTintSourceOracle.Outcome.Mismatch);
    }

    [Fact]
    public void BaseGame_TwoArchivesDisagree_IsUnresolved()
    {
        using var tmp = new TempDir("tintoracle");
        var data = tmp.Dir("data"); var output = tmp.Dir("out");
        var inArchive = FaceTintSourceOracle.TintRelPath(Tullius).Replace('/', '\\');
        WriteArchive(Path.Combine(data, "Skyrim - Textures0.bsa"), inArchive, DonorTint);
        WriteArchive(Path.Combine(data, "Skyrim - Textures1.bsa"), inArchive, OtherTint);
        WriteFile(output, FaceTintSourceOracle.TintRelPath(Lucan), DonorTint);

        VerdictFor(new GoldenOutputConfig(), data, output, Lucan)
            .Outcome.Should().Be(FaceTintSourceOracle.Outcome.SourceUnresolved);
    }
}
