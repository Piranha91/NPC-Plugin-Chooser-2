using System.IO;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Reads an NPC2 output plugin produced by one of the validation runs and states PASS/FAIL per
/// specimen, so the fix can be judged without needing a before-run to diff against.
///
/// <para>Set <see cref="OutputRoot"/> to the run's output folder, then run one of the three facts.
/// Each is skipped (with a note) if that run's plugin is not present.</para>
/// </summary>
public class ValidationChecker
{
    private readonly ITestOutputHelper _o;
    public ValidationChecker(ITestOutputHelper o) => _o = o;

    private const string Data = @"C:\Games\Steam\steamapps\common\Skyrim Special Edition\Data";
    private const string Mods = @"S:\Skyrim NPC Selection\mods";

    /// <summary>Every run of a given test that has actually been performed, as (mode, folder).
    /// NPC2 writes to &lt;mods folder&gt;\&lt;OutputDirectory&gt;, and run.py names those
    /// "NPC Output - A-skypatcher" / "-record", so one checker run covers every mode tried.</summary>
    private static IEnumerable<(string Mode, string Dir)> Runs(string test)
    {
        foreach (var mode in new[] { "skypatcher", "record" })
        {
            var dir = Path.Combine(Mods, $"NPC Output - {test}-{mode}");
            if (Directory.Exists(dir)) yield return (mode, dir);
        }
    }

    private sealed class Ctx
    {
        public ILinkCache Cache = null!;
        public ISkyrimModGetter Output = null!;
        public StringBuilder Log = new();
        public int Pass, Fail;
    }

    private Ctx? Open(string pluginDir)
    {
        var esp = Directory.GetFiles(pluginDir, "*.esp").Concat(Directory.GetFiles(pluginDir, "*.esm")).FirstOrDefault();
        if (esp == null) { _o.WriteLine($"SKIPPED: no plugin in {pluginDir}"); return null; }

        var loaded = new List<ISkyrimModGetter>();
        foreach (var f in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" })
            loaded.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Data, f), SkyrimRelease.SkyrimSE));
        loaded.Add(SkyrimMod.CreateFromBinaryOverlay(
            Mods + @"\High Poly NPC Overhaul - Resources\High Poly NPC Overhaul - Resources.esp", SkyrimRelease.SkyrimSE));
        loaded.Add(SkyrimMod.CreateFromBinaryOverlay(
            Mods + @"\High Poly NPC Overhaul - Skyrim Special Edition 2.0 (All Vanilla NPCs)\High Poly NPC Overhaul - Skyrim Special Edition.esp",
            SkyrimRelease.SkyrimSE));
        var output = SkyrimMod.CreateFromBinaryOverlay(esp, SkyrimRelease.SkyrimSE);
        loaded.Add(output);

        _o.WriteLine($"output plugin: {esp}");
        return new Ctx { Cache = loaded.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>(), Output = output };
    }

    private static void Check(Ctx c, string label, bool ok, string expected, string actual)
    {
        if (ok) { c.Pass++; c.Log.AppendLine($"  PASS  {label}\n          {actual}"); }
        else { c.Fail++; c.Log.AppendLine($"  FAIL  {label}\n          expected: {expected}\n          actual:   {actual}"); }
    }

    /// <summary>Every ArmorAddon EditorID reachable from an NPC's WornArmor, in the output's terms.</summary>
    private static List<string> Armature(Ctx c, INpcGetter npc)
    {
        if (npc.WornArmor.IsNull || !c.Cache.TryResolve<IArmorGetter>(npc.WornArmor.FormKey, out var a) || a.Armature == null)
            return new List<string>();
        return a.Armature
            .Select(l => c.Cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var arma) ? arma.EditorID ?? "?" : l.FormKey.ToString())
            .ToList();
    }

    private static List<string> OutfitItems(Ctx c, INpcGetter npc)
    {
        if (npc.DefaultOutfit.IsNull || !c.Cache.TryResolve<IOutfitGetter>(npc.DefaultOutfit.FormKey, out var o) || o.Items == null)
            return new List<string>();
        return o.Items.Select(l => c.Cache.TryResolve(l.FormKey, out var r) ? r.EditorID ?? "?" : l.FormKey.ToString()).ToList();
    }

    /// <summary>Every head part on the NPC, hair or not — the minted wig set is a Hair parent plus
    /// Misc extras, so a wig-name search has to see all of them.</summary>
    private static List<string> AllHeadParts(Ctx c, INpcGetter npc) => npc.HeadParts
        .Select(h => c.Cache.TryResolve<IHeadPartGetter>(h.FormKey, out var hp) ? hp.EditorID ?? "?" : h.FormKey.ToString())
        .ToList();

    private static List<string> HairParts(Ctx c, INpcGetter npc) => npc.HeadParts
        .Select(h => c.Cache.TryResolve<IHeadPartGetter>(h.FormKey, out var hp) ? hp : null)
        .Where(hp => hp is { Type: HeadPart.TypeEnum.Hair })
        .Select(hp => $"{hp!.EditorID}{(string.IsNullOrEmpty(hp.Model?.File.GivenPath) ? "(modeless)" : "")}")
        .ToList();

    private INpcGetter? Patched(Ctx c, string fk)
    {
        var key = FormKey.Factory(fk);
        // Record mode: an override of the recipient. SkyPatcher mode: a "_Template" surrogate whose
        // FormKey is the output's, found by the directive; fall back to matching EditorID suffix.
        var direct = c.Output.Npcs.FirstOrDefault(n => n.FormKey == key);
        if (direct != null) return direct;
        if (!c.Cache.TryResolve<INpcGetter>(key, out var orig)) return null;
        var edid = orig.EditorID;
        return c.Output.Npcs.FirstOrDefault(n =>
            n.EditorID != null && edid != null && n.EditorID.StartsWith(edid, StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================================
    [Fact]
    public void A_FlattenSeam()
    {
        int runs = 0, fails = 0;
        foreach (var (mode, dir) in Runs("A"))
        {
            var c = Open(dir); if (c == null) continue;
            runs++;

            // Under GiveEachNpcOwnCopy the output record carries the TERMINUS's appearance. The wig
            // pass must therefore have read the TERMINUS's WornArmor — the donor's wig must NOT appear.
            foreach (var (fk, name, wantWig, dontWantWig) in new[]
                     {
                         ("0D0573:Skyrim.esm", "Legate Rikke", "HighPoly_WigAA_HairFemaleNord03", "HighPoly_WigAA_HairFemaleNord12"),
                         ("06A152:Skyrim.esm", "Arniel's Shade", "HighPoly_WigAA_HairMaleElder3", "HighPoly_WigAA_HairMaleNord01"),
                         ("017938:Dragonborn.esm", "Miraak MQ02", "HighPoly_WigAA_HairMaleElder1", "HighPoly_WigAA_HairMaleNord01"),
                         ("10C471:Skyrim.esm", "Vigilant 02NordM01", "HighPoly_WigAA_HairMaleRedguard2", "HighPoly_WigAA_HairMaleNord03"),
                     })
            {
                var npc = Patched(c, fk);
                if (npc == null) { Check(c, $"{name} present in output", false, "an output record", "not found"); continue; }

                // The wig can legitimately land in any of three places, and WHICH one is not what
                // this test is about — it is about which RECORD the wig was read off. The routes:
                //
                //   outfit item   ForwardToOutfit relocated the skin wig into a minted wearable ARMO
                //   head parts    record mode + an Inventory-templated NPC: a forwarded outfit could
                //                 never reach it, so Patcher redirects to ConvertToHeadParts
                //                 (Patcher.cs, "outfitFieldInert") and the wig is minted as HDPTs
                //   armature      ForwardToSkin left it on the WornArmor where it already was
                //
                // So search all three, and assert only that the name is the TERMINUS's wig.
                var all = string.Join(", ",
                    Armature(c, npc).Concat(OutfitItems(c, npc)).Concat(AllHeadParts(c, npc)));
                bool ok = all.Contains(wantWig, StringComparison.OrdinalIgnoreCase)
                          && !all.Contains(dontWantWig, StringComparison.OrdinalIgnoreCase);
                Check(c, $"{name} wears the TERMINUS's wig", ok,
                    $"contains {wantWig} and NOT {dontWantWig}, in the armature, the outfit, or the head parts",
                    all);
            }

            _o.WriteLine($"\n=== A / {mode} (flatten seam) ===\n{c.Log}PASS={c.Pass} FAIL={c.Fail}");
            fails += c.Fail;
        }

        if (runs == 0) { _o.WriteLine("SKIPPED: no A run found. `python validation\\run.py A skypatcher` first."); return; }
        Assert.True(fails == 0, $"{fails} specimen check(s) failed across {runs} run(s) — see output");
    }

    // =========================================================================================
    [Fact]
    public void B_SkinCarriedWigHair()
    {
        int runs = 0, fails = 0;
        foreach (var (mode, dir) in Runs("B"))
        {
            var c = Open(dir); if (c == null) continue;
            runs++;

            foreach (var (fk, name, deadHair) in new[]
                     {
                         ("044310:Skyrim.esm", "Forsworn Briarheart", "HairFemaleNord19"),
                         ("0558F3:Skyrim.esm", "Pit Fan", "HairMaleNord10"),
                         ("075C7F:Skyrim.esm", "Velehk Sain", "MaleDremoraHair01"),
                         ("016F69:Skyrim.esm", "Dremora Kynval", "MaleDremoraHair01"),
                     })
            {
                var npc = Patched(c, fk);
                if (npc == null) { Check(c, $"{name} present in output", false, "an output record", "not found"); continue; }

                var hair = HairParts(c, npc);
                var joined = string.Join(", ", hair);
                bool removed = !hair.Any(h => h.StartsWith(deadHair, StringComparison.OrdinalIgnoreCase));
                bool bald = hair.Any(h => h.StartsWith("NPC2_HairBald", StringComparison.OrdinalIgnoreCase));
                Check(c, $"{name}: clashing '{deadHair}' removed + bald back-fill", removed && bald,
                    $"no {deadHair}, and NPC2_HairBald present", joined.Length == 0 ? "(no hair parts)" : joined);

                // The wig itself must still be on the skin — the removal must not have cost the wig.
                var arm = string.Join(", ", Armature(c, npc));
                Check(c, $"{name}: skin wig survives", arm.Contains("_WigAA_", StringComparison.OrdinalIgnoreCase),
                    "a HighPoly_WigAA_* armature on the WornArmor", arm);
            }

            _o.WriteLine($"\n=== B / {mode} (skin-carried wig) ===\n{c.Log}PASS={c.Pass} FAIL={c.Fail}");
            fails += c.Fail;
        }

        if (runs == 0) { _o.WriteLine("SKIPPED: no B run found. `python validation\\run.py B skypatcher` first."); return; }
        Assert.True(fails == 0, $"{fails} specimen check(s) failed across {runs} run(s) — see output");
    }

    // =========================================================================================
    [Fact]
    public void C_InertIncludeOutfit()
    {
        int runs = 0, fails = 0;
        foreach (var (mode, dir) in Runs("C"))
        {
            var c = Open(dir); if (c == null) continue;
            runs++;

            // The write itself is deliberately LEFT IN PLACE (it is harmless), so the plugin-side
            // check is just that the NPC was patched at all. The real evidence is the run log.
            // 006E5C is Traits-templated as well as Inventory-templated, so SkyPatcher mode screens
            // it out for the unrelated inherited-face reason. run.py drops it from that variant;
            // expect it only in record mode.
            var specimens = new List<(string Fk, string Name)>
            {
                ("034FC5:Dragonborn.esm", "DLC2EncCultist06NordM"),
                ("034FC3:Dragonborn.esm", "DLC2EncCultist06DarkElfF"),
            };
            if (mode == "record") specimens.Add(("006E5C:Dawnguard.esm", "DLC1VQ03VampireDriverDead"));

            foreach (var (fk, name) in specimens)
            {
                var npc = Patched(c, fk);
                Check(c, $"{name} was patched (face still applies)", npc != null, "an output record",
                    npc == null ? "not found" : $"outfit={(npc.DefaultOutfit.IsNull ? "(none)" : npc.DefaultOutfit.FormKey.ToString())}");
            }

            _o.WriteLine($"\n=== C / {mode} (inert Include Outfit) ===\n{c.Log}PASS={c.Pass} FAIL={c.Fail}");
            _o.WriteLine(mode == "record"
                ? "  EXPECT in the run log: \"takes its inventory from template\" x3, plus the summary\n" +
                  "  \"3 NPC(s) had 'Include Outfit' enabled but take their whole inventory\"."
                : "  NEGATIVE CONTROL: the run log must contain NEITHER of those lines — SkyPatcher's\n" +
                  "  outfitDefault= directive reaches the actor, so nothing here is inert.\n" +
                  "  (2 specimens here, not 3: 006E5C is screened out for an unrelated reason.)");
            fails += c.Fail;
        }

        if (runs == 0) { _o.WriteLine("SKIPPED: no C run found. `python validation\\run.py C record` first."); return; }
        Assert.True(fails == 0, $"{fails} specimen check(s) failed across {runs} run(s) — see output");
    }
}
