using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="SurrogateNpcRegistry"/> — builds the surrogate NPC a runtime backend applies the
/// appearance from, and maps it to and from its target. Driven like
/// <see cref="SkyPatcherInterfaceTests"/>: an Invalid provider with an in-memory output plugin.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SurrogateNpcRegistryTests
{
    private static SurrogateNpcRegistry NewRegistry(out EnvironmentStateProvider env)
    {
        env = NpcChooserTestEnvironment.Invalid();
        env.OutputMod = new SkyrimMod(ModKey.FromName("NPCTest", ModType.Plugin), SkyrimRelease.SkyrimSE);
        return new SurrogateNpcRegistry(env);
    }

    /// <summary>
    /// Builds a standalone appearance-donor NPC in its own in-memory plugin so DeepCopyIn has a
    /// real INpcGetter to copy. (The donor lives in a different mod than the output, mirroring how
    /// CreateSurrogate decouples the surrogate's content from the target FormKey.)
    /// </summary>
    private static INpcGetter MakeDonor(string editorId = "DonorNpc", string plugin = "Donor.esp")
    {
        var mod = MutagenFixtures.NewMod(plugin);
        return MutagenFixtures.NewNpc(mod, editorId: editorId, name: "Donor");
    }

    // ── CreateSurrogate seeds the surrogate map ───────────────────────────────

    [Fact]
    public void CreateSurrogate_SeedsSurrogateMapsBothDirections_AndOutputsEntry()
    {
        var reg = NewRegistry(out var env);
        var target = FormKey.Factory("000801:Target.esp");
        var donor = MakeDonor("Lydia");

        var surrogate = reg.CreateSurrogate(target, donor);

        // The surrogate is a brand-new NPC in the output plugin, EditorID = donor + "_Template".
        surrogate.FormKey.ModKey.Should().Be(env.OutputMod.ModKey);
        surrogate.EditorID.Should().Be("Lydia_Template");
        env.OutputMod.Npcs.Should().ContainSingle();

        // target -> surrogate and surrogate -> target are both registered.
        reg.TryGetSurrogateFormKey(target, out var surFk).Should().BeTrue();
        surFk.Should().Be(surrogate.FormKey);
        reg.TryGetOriginalFormKey(surrogate.FormKey, out var origFk).Should().BeTrue();
        origFk.Should().Be(target);
    }

    [Fact]
    public void CreateSurrogate_DonorWithoutEditorId_UsesNoEditorIdPlaceholder()
    {
        var reg = NewRegistry(out _);
        var mod = MutagenFixtures.NewMod("Donor.esp");
        var donor = mod.Npcs.AddNew(); // no EditorID set

        var surrogate = reg.CreateSurrogate(FormKey.Factory("000900:T.esp"), donor);

        surrogate.EditorID.Should().Be("NoEditorID_Template");
    }

    [Fact]
    public void TryGetSurrogateFormKey_Unknown_ReturnsFalse()
    {
        var reg = NewRegistry(out _);
        reg.TryGetSurrogateFormKey(FormKey.Factory("00BEEF:Other.esp"), out var fk).Should().BeFalse();
        fk.Should().Be(FormKey.Null);
    }

    [Fact]
    public void TryGetOriginalFormKey_Unknown_ReturnsFalse()
    {
        var reg = NewRegistry(out _);
        reg.TryGetOriginalFormKey(FormKey.Factory("00BEEF:Other.esp"), out var fk).Should().BeFalse();
        fk.Should().Be(FormKey.Null);
    }

    [Fact]
    public void CreateSurrogate_DuplicateTarget_ThrowsWithoutAddingARecord()
    {
        // A second surrogate for the same target throws (the patcher must create each surrogate
        // exactly once), and it throws BEFORE adding a record, so the output gains no orphan.
        var reg = NewRegistry(out var env);
        var target = FormKey.Factory("000801:Target.esp");
        reg.CreateSurrogate(target, MakeDonor("A"));

        Action again = () => reg.CreateSurrogate(target, MakeDonor("B"));
        again.Should().Throw<ArgumentException>();
        env.OutputMod.Npcs.Should().ContainSingle();
    }

    /// <summary>
    /// The wild failure behind the "failed to replace a vanilla NPC with Lilith" report: the
    /// donor plugin's NPC record carries a truncated fixed-length subrecord (there NAM5, a
    /// 2-byte unknown), which xEdit and the game read leniently but Mutagen's lazy overlay
    /// throws on the moment DeepCopyIn touches the field. CreateSurrogate must let the
    /// throw escape (the Patcher logs and skips that one NPC) and unwind completely: no empty
    /// surrogate left in OutputMod — the orphan sweep only REPORTS leftover NPC records, it
    /// never removes them — and no surrogate-map state, so a later selection may re-seed
    /// the same target.
    /// </summary>
    [Fact]
    public async Task CreateSurrogate_CorruptDonorRecord_ThrowsAndLeavesNoTrace()
    {
        using var tmp = new TempDir();

        var donorMod = MutagenFixtures.NewMod("CorruptDonor.esp");
        var donorNpc = MutagenFixtures.NewNpc(donorMod, editorId: "CorruptDonor", name: "Corrupt");
        donorNpc.NAM5 = 0x00FF;
        var path = tmp.Combine("CorruptDonor.esp");
        await donorMod.BeginWrite.ToPath(path).WithLoadOrderFromHeaderMasters().WithNoDataFolder().WriteAsync();
        TruncateNam5(path);

        using var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var corruptDonor = overlay.Npcs.Single();

        // Premise guard: the truncated field read must throw. If a Mutagen upgrade ever parses
        // it leniently, the assertions below stop testing the unwind.
        Action readField = () => _ = corruptDonor.NAM5;
        readField.Should().Throw<Exception>();

        var reg = NewRegistry(out var env);
        var target = FormKey.Factory("000801:Target.esp");

        Action act = () => reg.CreateSurrogate(target, corruptDonor);
        act.Should().Throw<Exception>();

        env.OutputMod.Npcs.Should().BeEmpty("a failed surrogate build must unwind its AddNew");
        reg.TryGetSurrogateFormKey(target, out _).Should().BeFalse();

        // The failed seed must not poison the target: the next (healthy) donor still lands.
        reg.CreateSurrogate(target, MakeDonor()).EditorID.Should().Be("DonorNpc_Template");
    }

    /// <summary>
    /// Byte surgery on a written plugin: truncates the single NAM5 subrecord's payload from 2
    /// bytes to 1, keeping the subrecord length, the NPC_ record's data size and the top group's
    /// length consistent — the file still opens and its subrecord walk stays aligned everywhere
    /// except the NAM5 field read itself, the same shape as the corrupt plugins seen in the wild.
    /// </summary>
    private static void TruncateNam5(string pluginPath)
    {
        var bytes = File.ReadAllBytes(pluginPath);

        int nam5 = bytes.AsSpan().IndexOf("NAM5"u8);
        nam5.Should().BeGreaterThan(0, "the donor NPC was written with NAM5 set");
        bytes.AsSpan().LastIndexOf("NAM5"u8).Should().Be(nam5, "the test assumes a single NAM5 subrecord");
        BitConverter.ToUInt16(bytes, nam5 + 4).Should().Be(2, "NAM5 is written as a 2-byte field");

        // File layout: TES4 record (24-byte header, uint32 data size at +4), then the NPC_ top
        // group ("GRUP", uint32 length at +4 that includes its own 24-byte header, "NPC_" label
        // at +8), then the single NPC_ record (24-byte header, uint32 data size at +4).
        int grup = 24 + BitConverter.ToInt32(bytes, 4);
        Encoding.ASCII.GetString(bytes, grup, 4).Should().Be("GRUP");
        Encoding.ASCII.GetString(bytes, grup + 8, 4).Should().Be("NPC_");
        int rec = grup + 24;
        Encoding.ASCII.GetString(bytes, rec, 4).Should().Be("NPC_");
        int recSize = BitConverter.ToInt32(bytes, rec + 4);
        nam5.Should().BeInRange(rec + 24, rec + 24 + recSize - 6, "NAM5 must sit inside the NPC_ record's data");

        BitConverter.GetBytes((ushort)1).CopyTo(bytes, nam5 + 4);
        BitConverter.GetBytes(recSize - 1).CopyTo(bytes, rec + 4);
        BitConverter.GetBytes(BitConverter.ToInt32(bytes, grup + 4) - 1).CopyTo(bytes, grup + 4);

        var truncated = new byte[bytes.Length - 1];
        int drop = nam5 + 6 + 1; // subrecord header is sig(4) + len(2); keep payload byte 1, drop byte 2
        Array.Copy(bytes, 0, truncated, 0, drop);
        Array.Copy(bytes, drop + 1, truncated, drop, bytes.Length - drop - 1);
        File.WriteAllBytes(pluginPath, truncated);
    }

    // ── Reinitialize ──────────────────────────────────────────────────────────

    [Fact]
    public void Reinitialize_ForgetsEveryMapping_AndAllowsReseedingTheSameTarget()
    {
        var reg = NewRegistry(out _);
        var target = FormKey.Factory("000801:T.esp");
        var surrogate = reg.CreateSurrogate(target, MakeDonor("A"));

        reg.Reinitialize();

        reg.TryGetSurrogateFormKey(target, out _).Should().BeFalse();
        reg.TryGetOriginalFormKey(surrogate.FormKey, out _).Should().BeFalse();
        Action reseed = () => reg.CreateSurrogate(target, MakeDonor("B"));
        reseed.Should().NotThrow();
    }
}
