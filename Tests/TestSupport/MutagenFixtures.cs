using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Factory helpers for building small in-memory Mutagen Skyrim records. Building real
/// records (rather than mocking the getter interfaces) is faithful — it is exactly how
/// the production code and the sibling SynthEBD.Tests exercise record logic — and keeps
/// the suite free of a mocking-library dependency.
/// </summary>
public static class MutagenFixtures
{
    public static SkyrimRelease Release => SkyrimRelease.SkyrimSE;

    /// <summary>A fresh, empty, writable mod with the given plugin name (default null-key).</summary>
    public static SkyrimMod NewMod(string? name = null) =>
        new(name is null ? ModKey.Null : ModKey.FromFileName(name.EndsWith(".esp") ? name : name + ".esp"),
            Release);

    /// <summary>Parses a FormKey from "001234:Some.esp".</summary>
    public static FormKey Fk(string s) => FormKey.Factory(s);

    /// <summary>ModKey from a plugin filename ("Some.esp").</summary>
    public static ModKey Mk(string fileName) =>
        ModKey.FromFileName(fileName.Contains('.') ? fileName : fileName + ".esp");

    /// <summary>
    /// Adds a new NPC to <paramref name="mod"/> with optional appearance-relevant fields set.
    /// </summary>
    public static Npc NewNpc(
        SkyrimMod mod,
        string? editorId = null,
        string? name = null,
        bool female = false,
        bool traitsTemplate = false,
        INpcGetter? template = null,
        IRaceGetter? race = null)
    {
        var npc = mod.Npcs.AddNew();
        if (editorId != null) npc.EditorID = editorId;
        if (name != null) npc.Name = name;
        if (female) npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
        if (traitsTemplate) npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        if (template != null) npc.Template.SetTo(template);
        if (race != null) npc.Race.SetTo(race);
        return npc;
    }

    /// <summary>
    /// Adds a head part that bears baked FaceGen geometry — i.e. one the Creation Kit would
    /// bake into the NPC's FaceGen mesh as a shape named after its EditorID.
    ///
    /// <para>Use this for any part a test means as REAL hair/brows/an antler. A head part with
    /// no MODL is a modeless placeholder that renders nothing, and the wig/antler pipeline now
    /// tells the two apart (see <c>FaceGenConsistencyAnalyzer.BearsBakedGeometry</c>): it will
    /// not remove a placeholder, strip a shape for it, or expect the validator to excuse it.
    /// Fixtures that assigned only an EditorID and a Type were therefore describing High Poly
    /// NPC Overhaul's <c>HighPoly_HairBald</c>, not the hair they were named after. Pass
    /// <paramref name="modeless"/> to build the placeholder deliberately.</para>
    /// </summary>
    public static HeadPart NewHeadPart(
        SkyrimMod mod,
        string editorId,
        HeadPart.TypeEnum? type = null,
        bool modeless = false)
    {
        var hp = mod.HeadParts.AddNew();
        hp.EditorID = editorId;
        if (type != null) hp.Type = type;
        if (!modeless) hp.Model = new Model { File = $@"actors\character\character assets\{editorId}.nif" };
        return hp;
    }

    /// <summary>Adds a new race with the given EditorID.</summary>
    public static Race NewRace(SkyrimMod mod, string? editorId = null)
    {
        var race = mod.Races.AddNew();
        if (editorId != null) race.EditorID = editorId;
        return race;
    }
}
