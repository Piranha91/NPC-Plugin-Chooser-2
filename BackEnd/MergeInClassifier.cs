using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Shared classifier deciding whether a plugin set looks like an NPC appearance replacer
/// (its dependency records should be merged into the output by default) or a base/source
/// mod (defines its own content, stays in the load order — merging is unnecessary and can
/// freeze the patcher on large plugins).
///
/// The decision is NPC-provenance based and is computed ONLY from record identities (group
/// FormKey caches and counts) — record contents are never parsed, so malformed-but-game-legal
/// subrecord data (e.g. a FootstepSet whose DATA disagrees with its XCNT counts, or a
/// zero-length INCC) cannot abort classification.
///
/// Used by BOTH <c>VM_ModSetting.CheckMergeInSuitability</c> (the "Merge Dependencies"
/// default) and <c>VM_Mods.IsMasterAppearanceMod</c> (the dependency-folder keep logic).
/// Those two must never diverge — route any change through this class.
///
/// Validated 2026-07-12 against six libraries (~1,130 ModSettings) cross-checked against each
/// Wabbajack author's separator grouping (LoreRim, FUS Heavy, Tempus Maledictum, Mad God
/// Overhaul, SUP + the reference library). Re-tune with the offline model at
/// <c>..\classifier_model\model_classifier.py</c>, which mirrors this logic.
/// </summary>
public static class MergeInClassifier
{
    // ================================ Tunable parameters ================================
    // The volume guard grants each candidate replacer a "hard record" allowance (hard =
    // records that are neither NPCs nor appearance-support records). If a mod carries more
    // hard records than its allowance, it is classified as a base mod:
    //
    //     allowance = max(eff, MinHardAllowance, min(HardAllowancePerOverride * eff,
    //                                                HardAllowanceBonusCap))
    //     (or just eff when the mod ships zero appearance-support records)
    //
    // where eff = external NPC overrides + SkyPatcher visual targets.

    /// <summary>
    /// Minimum hard-record allowance for a replacer that ships appearance-support records.
    /// Calibration anchors: must stay ABOVE 23 (Bijin Warmaidens: 22 overrides, 23 hard
    /// records, legit replacer) and BELOW 99 (Infiltration - Quest Expansion: 3 overrides,
    /// 99 hard records, quest mod).
    /// </summary>
    public const int MinHardAllowance = 25;

    /// <summary>
    /// Hard records a replacer may carry per NPC override / SkyPatcher target. Small
    /// replacers legitimately ship a few outfits/leveled lists per overridden NPC.
    /// </summary>
    public const int HardAllowancePerOverride = 4;

    /// <summary>
    /// Cap on the per-override allowance bonus: a handful of overrides must not excuse
    /// content-mod record volume (Project AHO Requiem patch: 85 overrides, 254 hard records,
    /// must stay base). Mods whose override count itself exceeds the cap are judged
    /// overrides-vs-volume directly via the max(eff, ...) term — USSEP (952 overrides,
    /// 8383 hard records) must classify base, while a hypothetical 500-override replacer
    /// carrying 300 outfit records must classify appearance.
    /// </summary>
    public const int HardAllowanceBonusCap = 100;

    // ====================================================================================

    public enum Verdict
    {
        AppearanceReplacer,
        BaseMod
    }

    /// <summary>
    /// Identity-level record tallies for one or more plugins, summable across a ModSetting.
    /// </summary>
    public readonly record struct Counts(int OverrideNpcs, int NewNpcs, int SupportRecords, int HardRecords)
    {
        public static Counts operator +(Counts a, Counts b) => new(
            a.OverrideNpcs + b.OverrideNpcs,
            a.NewNpcs + b.NewNpcs,
            a.SupportRecords + b.SupportRecords,
            a.HardRecords + b.HardRecords);
    }

    /// <summary>
    /// Tallies a single plugin. <paramref name="internalKeys"/> is the full set of plugins
    /// belonging to the same ModSetting (including resource-only ones): an NPC record whose
    /// FormKey originates outside that set is an override of an external NPC; one originating
    /// inside it is the mod's own NPC. Only group FormKey caches and counts are touched.
    /// </summary>
    public static Counts CountPlugin(ISkyrimModGetter plugin, IReadOnlySet<ModKey> internalKeys)
    {
        int overrideNpcs = 0, newNpcs = 0;
        foreach (var formKey in plugin.Npcs.FormKeys)
        {
            if (internalKeys.Contains(formKey.ModKey)) newNpcs++;
            else overrideNpcs++;
        }

        // Appearance-support records: the non-NPC members of Auxilliary.AppearanceRecordTypes.
        int support =
            plugin.Armors.Count +
            plugin.ArmorAddons.Count +
            plugin.TextureSets.Count +
            plugin.HeadParts.Count +
            plugin.Hairs.Count +
            plugin.Colors.Count +
            plugin.Eyes.Count;

        // Hard records: every top-level record outside the appearance groups.
        int hard = Auxilliary.LazyEnumerateMajorRecords(plugin, Auxilliary.AppearanceRecordTypes).Count();

        return new Counts(overrideNpcs, newNpcs, support, hard);
    }

    /// <summary>
    /// Counts the distinct NPCs targeted by SkyPatcher visual-replacer INIs
    /// (<c>filterByNpcs=</c> lines that carry a <c>copyVisualStyle=</c> action) under the
    /// given mod folders. Purely lexical — no FormKey/EditorID resolution — so it can run at
    /// scan time before the environment-dependent SkyPatcher analysis populates
    /// <c>SkyPatcherTargetModKeys</c>.
    /// </summary>
    public static int CountSkyPatcherVisualTargets(IEnumerable<string> modFolderPaths)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in modFolderPaths)
        {
            string iniDir = Path.Combine(folder, "SKSE", "Plugins", "SkyPatcher", "npc");
            if (!Directory.Exists(iniDir)) continue;

            foreach (var iniFile in Directory.EnumerateFiles(iniDir, "*.ini", SearchOption.AllDirectories))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(iniFile);
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("filterByNpcs=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!trimmed.Contains("copyVisualStyle=", StringComparison.OrdinalIgnoreCase)) continue;

                    var filterValue = trimmed.Split(':', 2)[0].Substring("filterByNpcs=".Length);
                    foreach (var token in filterValue.Split(','))
                    {
                        var target = token.Trim();
                        if (target.Length > 0) targets.Add(target);
                    }
                }
            }
        }

        return targets.Count;
    }

    /// <summary>
    /// The provenance decision. <paramref name="skyPatcherTargets"/> counts distinct
    /// SkyPatcher visual-replacer targets; when nonzero, the new-NPC ratio test is bypassed
    /// because SkyPatcher template NPCs are donor payloads and must not vote against their
    /// own targets (Bijin Redux ships 47 templates for 45 targets).
    /// </summary>
    public static Verdict Classify(Counts totals, int skyPatcherTargets)
    {
        int eff = totals.OverrideNpcs + skyPatcherTargets;

        // Nothing is being replaced: the mod defines its content (item mods, followers,
        // quest mods, new-NPC variety adders with no overrides).
        if (eff == 0) return Verdict.BaseMod;

        // Ratio test: when a mod's own new NPCs outnumber its overrides, it is a source mod
        // that happens to touch some vanilla NPCs (Lawless, DIVERSE SKYRIM), not a replacer.
        if (skyPatcherTargets == 0 && totals.OverrideNpcs < totals.NewNpcs) return Verdict.BaseMod;

        // Volume guard: a mod with zero appearance-support records is a facegen-carrying
        // location/quest patch, not a replacer — it gets no small-replacer allowance.
        int allowance = totals.SupportRecords > 0
            ? Math.Max(eff, Math.Max(MinHardAllowance, Math.Min(HardAllowancePerOverride * eff, HardAllowanceBonusCap)))
            : eff;

        return totals.HardRecords > allowance ? Verdict.BaseMod : Verdict.AppearanceReplacer;
    }
}
