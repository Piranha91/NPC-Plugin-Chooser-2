using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Benchmarks the skin-carried (WNAM) wig detection against the real
/// High Poly NPC Overhaul SSE 2.0 install — the canonical WNAM-wig mod (bald
/// FaceGen + hair-slot ARMA in each NPC's WornArmor; the pattern the preview's
/// WornArmor walk was originally written for). Machine-local: gracefully skips
/// when the HPNO mod folders or a Skyrim environment aren't present. Read-only —
/// the source plugins are opened as binary overlays and never modified. The
/// environment's link cache supplies the same fallback resolvers production's
/// <c>VM_ModSetting.ScanForWigs</c> passes, which is what lets the
/// race-default-skin guard resolve vanilla races (without it, master-defined
/// beast-form skins like NakedTorsoWerewolfBeast would false-positive —
/// observed in an early run of this very benchmark).
///
/// What it checks at all-vanilla-NPC scale:
/// * the WNAM pass actually detects (HPNO must yield wig armatures),
/// * coverage: how many NPCs with a WNAM get at least one detection (reported),
/// * the race-default-skin guard holds (no detected ARMA belongs to any
///   resolvable race's default skin),
/// * antler exclusivity (no ARMA classified both ways),
/// * scan wall-time stays sane (the pass runs during mod analysis).
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigDetectorHpnoBenchmarkTests
{
    private const string HpnoRoot =
        @"S:\Skyrim NPC Selection\mods\High Poly NPC Overhaul - Skyrim Special Edition 2.0 (All Vanilla NPCs)";

    private const string HpnoResourcesRoot =
        @"S:\Skyrim NPC Selection\mods\High Poly NPC Overhaul - Resources";

    private const string MainEsp = HpnoRoot + @"\High Poly NPC Overhaul - Skyrim Special Edition.esp";
    private const string ResourcesEsp = HpnoResourcesRoot + @"\High Poly NPC Overhaul - Resources.esp";

    private static bool SpecimensMissing => !File.Exists(MainEsp) || !File.Exists(ResourcesEsp);

    private readonly ITestOutputHelper _output;

    public WigDetectorHpnoBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void HpnoScan_DetectsWnamWigArmatures_AtScale()
    {
        if (SpecimensMissing)
        {
            _output.WriteLine("SKIPPED: HPNO mod folders not found.");
            return;
        }
        if (!NpcChooserTestEnvironment.TryBuild(out var envHandle, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }

        using var env = envHandle;
        var linkCache = env!.Provider.LinkCache!;

        using var resources = SkyrimMod.CreateFromBinaryOverlay(ResourcesEsp, SkyrimRelease.SkyrimSE);
        using var main = SkyrimMod.CreateFromBinaryOverlay(MainEsp, SkyrimRelease.SkyrimSE);
        var plugins = new ISkyrimModGetter[] { resources, main };

        // The same fallback trio production's ScanForWigs supplies.
        var sw = Stopwatch.StartNew();
        var r = WigDetector.Scan(plugins,
            fk => linkCache.TryResolve<IArmorAddonGetter>(fk, out var arma) ? arma : null,
            fk => linkCache.TryResolve<IArmorGetter>(fk, out var armor) ? armor : null,
            fk => linkCache.TryResolve<IRaceGetter>(fk, out var race) ? race : null);
        sw.Stop();

        // Coverage: NPCs whose WNAM (resolvable within these plugins) carries a
        // detected wig ARMA.
        var localArmors = new Dictionary<FormKey, IArmorGetter>();
        foreach (var p in plugins)
        foreach (var armor in p.Armors)
            localArmors[armor.FormKey] = armor;

        int npcsWithWnam = 0, npcsWithDetectedWig = 0, totalNpcs = 0;
        foreach (var p in plugins)
        foreach (var npc in p.Npcs)
        {
            totalNpcs++;
            if (npc.WornArmor.IsNull || !localArmors.TryGetValue(npc.WornArmor.FormKey, out var wnam))
                continue;
            npcsWithWnam++;
            if (wnam.Armature != null &&
                wnam.Armature.Any(a => a != null && !a.IsNull && r.WigArmatures.Contains(a.FormKey)))
            {
                npcsWithDetectedWig++;
            }
        }

        // Race-default-skin guard: no detected ARMA may be reachable from any
        // resolvable race's default skin (beast-form and vanilla-race anatomy).
        var raceSkinViolations = new List<string>();
        foreach (var armaKey in r.WigArmatures)
        {
            if (!linkCache.TryResolve<IArmorAddonGetter>(armaKey, out var arma)) continue;
            foreach (var raceLink in EnumerateArmaRaces(arma))
            {
                if (!linkCache.TryResolve<IRaceGetter>(raceLink, out var race) || race.Skin.IsNull) continue;
                if (!linkCache.TryResolve<IArmorGetter>(race.Skin.FormKey, out var raceSkin)) continue;
                if (raceSkin.Armature != null && raceSkin.Armature.Any(a => a?.FormKey == armaKey))
                {
                    raceSkinViolations.Add(arma.EditorID ?? armaKey.ToString());
                    break;
                }
            }
        }

        var sampleEids = new List<string>();
        foreach (var p in plugins)
        foreach (var arma in p.ArmorAddons)
        {
            if (sampleEids.Count >= 12) break;
            if (r.WigArmatures.Contains(arma.FormKey) && !string.IsNullOrEmpty(arma.EditorID))
                sampleEids.Add(arma.EditorID);
        }

        _output.WriteLine($"HPNO WNAM wig scan: {sw.ElapsedMilliseconds} ms over {totalNpcs} NPCs");
        _output.WriteLine($"  detected wig ARMAs:        {r.WigArmatures.Count}");
        _output.WriteLine($"  outfit wig ARMOs:          {r.Wigs.Count}");
        _output.WriteLine($"  NPCs with WNAM:            {npcsWithWnam}");
        _output.WriteLine($"  NPCs with detected wig:    {npcsWithDetectedWig}");
        _output.WriteLine($"  sample ARMA EditorIDs:     {string.Join(", ", sampleEids)}");

        r.WigArmatures.Should().NotBeEmpty("HPNO is the canonical WNAM-wig mod");
        npcsWithDetectedWig.Should().BeGreaterThan(0,
            "NPCs actually wearing the detected skin hair must be reachable");
        raceSkinViolations.Should().BeEmpty(
            "a race default skin's ARMAs are anatomy, never wigs (the WNAM==race.Skin guard must hold)");
        r.WigArmatures.Overlaps(r.AntlerArmatures).Should().BeFalse(
            "an ARMA must never classify as both wig and antler");
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000,
            "the WNAM pass runs during mod analysis and must stay cheap at vanilla-NPC scale");
    }

    private static IEnumerable<FormKey> EnumerateArmaRaces(IArmorAddonGetter arma)
    {
        if (!arma.Race.IsNull) yield return arma.Race.FormKey;
        if (arma.AdditionalRaces == null) yield break;
        foreach (var extra in arma.AdditionalRaces)
            if (extra != null && !extra.IsNull)
                yield return extra.FormKey;
    }
}
