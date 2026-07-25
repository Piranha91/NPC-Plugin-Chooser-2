using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;

/// <summary>
/// Republishes a wig/antler outfit duplicate through the SAME runtime
/// distributor(s) that supply the NPC's outfit, so the forwarded piece actually
/// survives in game.
///
/// <para><b>Why it is needed.</b> <see cref="WigForwarder"/> duplicates the outfit
/// the NPC really wears — resolved through the SkyPatcher/SPID simulation, so the
/// duplicate already has the runtime-distributed content — adds the wig/antler, and
/// points the patched NPC record at it. A record-level <c>DefaultOutfit</c> is the
/// bottom of the runtime stack though: SkyPatcher rewrites <c>defaultOutfit</c> at
/// <c>kDataLoaded</c> and SPID distributes on actor load, so both overwrite the
/// assignment and the wig disappears. The only way to keep it is to speak the same
/// language, which is what this class emits.</para>
///
/// <para><b>What is emitted, per contesting distributor</b>
/// (<see cref="RuntimeOutfitContest"/>):
/// <list type="bullet">
/// <item><b>SkyPatcher</b> — an <c>outfitDefault=</c> directive appended to NPC2's own
/// ini (<see cref="SkyPatcherInterface"/>, <c>SKSE\Plugins\SkyPatcher\npc\NPC Plugin
/// Chooser\&lt;Output&gt;.ini</c>). SkyPatcher applies the LAST matching assignment in
/// its config walk, and NPC2's subfolder is read after every root-level config, so the
/// directive wins unless a competing config sits in a subfolder sorting after "NPC
/// Plugin Chooser" — the one case the resolver reports as
/// <see cref="RuntimeOutfitContest.SkyPatcherOutranksNpc2Ini"/> and this class warns
/// about.</item>
/// <item><b>SPID</b> — an <c>Outfit =</c> line in a generated <c>*_DISTR.ini</c> at the
/// output's Data root. SPID distributes outfits with <c>for_first_form</c>: the FIRST
/// matching entry in ordinal file-name order wins and iteration stops, so the file name
/// is deliberately prefixed with '!' to sort ahead of ordinary configs. When a
/// contesting config still sorts earlier, that is reported rather than silently lost.
/// Plain <c>Outfit</c> (not <c>FinalOutfit</c>) is used for compatibility with SPID
/// builds that predate the final-outfit key; Final would buy nothing here anyway, since
/// only one regular outfit entry is ever applied per NPC.</item>
/// </list></para>
///
/// <para>In SkyPatcher output mode nothing new is emitted: the patcher already writes an
/// <c>outfitDefault=</c> directive for every forwarded outfit, and that directive also
/// stands SPID down (SPID snapshots <c>defaultOutfit</c> at <c>TESNPC::InitItemImpl</c>,
/// before SkyPatcher patches it, so <c>OutfitManager::IsSuspendedReplacement</c> sees a
/// mismatch). Only the "outranked" warning applies there.</para>
/// </summary>
public class ForwardedOutfitDistributor : OptionalUIModule
{
    private readonly Settings _settings;
    private readonly SkyPatcherInterface _skyPatcherInterface;

    /// <summary>Leading '!' puts the file ahead of ordinary <c>*_DISTR.ini</c> names in
    /// SPID's ordinal path sort, which is what makes its entries win
    /// (<c>for_first_form</c>).</summary>
    public const string SpidFileNamePrefix = "!NPC Plugin Chooser - ";

    /// <summary>SPID only reads top-level <c>Data\*.ini</c> whose name contains
    /// "_DISTR" (case-sensitive).</summary>
    public const string SpidFileNameSuffix = "_DISTR.ini";

    private readonly List<SpidOutfitAssignment> _spidAssignments = new();
    private readonly SortedSet<string> _contestingSpidFiles = new(StringComparer.Ordinal);

    public ForwardedOutfitDistributor(Settings settings, SkyPatcherInterface skyPatcherInterface)
    {
        _settings = settings;
        _skyPatcherInterface = skyPatcherInterface;
    }

    private readonly record struct SpidOutfitAssignment(
        FormKey NpcFormKey, FormKey OutfitFormKey, string NpcIdentifier, string? PatchedFrom);

    /// <summary>True when a generated <c>*_DISTR.ini</c> is NPC2's own output from a
    /// previous run. Used by <see cref="OutfitDisplayResolver"/> to keep a deployed
    /// output from reading back as an external contest.</summary>
    public static bool IsNpc2SpidConfig(string fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        fileName.StartsWith(SpidFileNamePrefix, StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(SpidFileNameSuffix, StringComparison.OrdinalIgnoreCase);

    public static string BuildSpidFileName(ModKey outputPlugin) =>
        SpidFileNamePrefix + outputPlugin.Name + SpidFileNameSuffix;

    public bool HasSpidEntries => _spidAssignments.Count > 0;

    /// <summary>
    /// Clears the per-run queue and deletes the stale generated ini, so a run that ends
    /// up publishing nothing does not leave the previous run's file behind. Called for
    /// every output plugin, whatever the mode.
    /// </summary>
    /// <param name="clearAllGenerated">
    /// Sweep EVERY generated <c>_DISTR.ini</c>, not just this output plugin's. Pass true
    /// on the first patching iteration so a previous run that split into MORE plugins
    /// cannot leave orphaned configs distributing outfits from a plugin this run no
    /// longer writes.
    /// </param>
    public void Reinitialize(string outputRootDir, ModKey outputPlugin, bool clearAllGenerated = false)
    {
        _spidAssignments.Clear();
        _contestingSpidFiles.Clear();

        if (string.IsNullOrWhiteSpace(outputRootDir) || !Directory.Exists(outputRootDir)) return;

        IEnumerable<string> targets;
        if (clearAllGenerated)
        {
            targets = Directory.EnumerateFiles(outputRootDir, "*" + SpidFileNameSuffix)
                .Where(f => IsNpc2SpidConfig(Path.GetFileName(f)));
        }
        else
        {
            if (outputPlugin.IsNull) return;
            targets = new[] { Path.Combine(outputRootDir, BuildSpidFileName(outputPlugin)) };
        }

        foreach (var path in targets.ToList())
        {
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                AppendLog("Failed to clear the generated SPID outfit ini at " + path + Environment.NewLine +
                          ExceptionLogger.GetExceptionStack(e), true, true);
            }
        }
    }

    /// <summary>
    /// Queues the runtime directives that keep <paramref name="outfitFormKey"/> (the
    /// wig/antler outfit duplicate) on <paramref name="targetNpcFormKey"/> in game.
    /// A no-op when nothing contests the outfit. Call once per NPC, after the record
    /// links are finalized.
    /// </summary>
    public void Publish(FormKey targetNpcFormKey, FormKey outfitFormKey, RuntimeOutfitContest contest,
        string npcIdentifier)
    {
        if (targetNpcFormKey.IsNull || outfitFormKey.IsNull || !contest.Any) return;

        var channels = new List<string>();
        if (contest.SkyPatcher) channels.Add("SkyPatcher");
        if (contest.Spid) channels.Add("SPID");

        var who = new List<string>();
        if (contest.SkyPatcher) who.Add($"SkyPatcher ({contest.SkyPatcherDetail})");
        if (contest.Spid) who.Add($"SPID ({contest.SpidDetail})");
        string whoStr = string.Join(" and ", who);

        // SkyPatcher output mode already covers this: the patcher emits an
        // outfitDefault= directive for every forwarded outfit, and that directive also
        // stands SPID down (SPID snapshots defaultOutfit at TESNPC::InitItemImpl, before
        // SkyPatcher patches it, so IsSuspendedReplacement sees a mismatch). Nothing to
        // add — and nothing the Publish setting should suppress either.
        if (_settings.UseSkyPatcherMode)
        {
            if (contest.SkyPatcherOutranksNpc2Ini) WarnOutranked(npcIdentifier, contest);
            return;
        }

        if (!_settings.PublishForwardedOutfitsToDistributors)
        {
            AppendLog($"      WARNING: {npcIdentifier}'s outfit is distributed at runtime by {whoStr}, which will " +
                      "overwrite the forwarded wig/antler outfit in game. Enable 'Publish forwarded outfits to " +
                      "SkyPatcher/SPID' in Settings > Head Editing to have NPC2 republish it there.", false, true);
            return;
        }

        // A SkyPatcher config read after NPC2's own ini folder cannot be beaten from
        // here — SkyPatcher applies the LAST assignment it reads. The directive is still
        // emitted, so it wins over everything read earlier.
        if (contest.SkyPatcherOutranksNpc2Ini) WarnOutranked(npcIdentifier, contest);

        if (contest.SkyPatcher)
        {
            _skyPatcherInterface.SetOutfitStandalone(targetNpcFormKey, outfitFormKey,
                BuildProvenanceComment(targetNpcFormKey, npcIdentifier, contest));
        }

        // Emitted alongside a SkyPatcher directive when BOTH contest, not instead of it:
        // the patched record already holds the duplicate, so a SkyPatcher directive that
        // sets the same outfit leaves SPID's initial/current comparison equal — i.e.
        // un-suspended and free to distribute over it.
        if (contest.Spid)
        {
            _spidAssignments.Add(new SpidOutfitAssignment(targetNpcFormKey, outfitFormKey, npcIdentifier,
                contest.WinningSourceDetail));
            if (!string.IsNullOrEmpty(contest.SpidSourceFile)) _contestingSpidFiles.Add(contest.SpidSourceFile);
        }

        AppendLog($"      Wig/antler handling: {npcIdentifier}'s outfit is distributed at runtime by {whoStr}; " +
                  $"republishing the forwarded outfit {outfitFormKey} through {string.Join(" and ", channels)} " +
                  "so it wins in game.", false, false);
    }

    /// <summary>The per-NPC provenance note: who the NPC is, and which config was the
    /// conflict winner for their outfit BEFORE this run republished it.</summary>
    private static string BuildProvenanceComment(FormKey npcFormKey, string npcIdentifier,
        RuntimeOutfitContest contest)
    {
        var note = $"{npcIdentifier} [{npcFormKey}]";
        return contest.WinningSourceDetail is { Length: > 0 } from
            ? note + " — patched from " + from
            : note;
    }

    private void WarnOutranked(string npcIdentifier, RuntimeOutfitContest contest)
    {
        AppendLog($"      WARNING: SkyPatcher config '{contest.SkyPatcherDetail}' is read AFTER NPC2's own ini " +
                  $"('{OutfitDisplayResolver.Npc2SkyPatcherSubfolder}' subfolder) and will overwrite " +
                  $"{npcIdentifier}'s forwarded wig/antler outfit. Move that config to a folder sorting before " +
                  $"'{OutfitDisplayResolver.Npc2SkyPatcherSubfolder}' to let NPC2 win.", false, true);
    }

    /// <summary>
    /// Writes the generated <c>*_DISTR.ini</c> for the queued SPID assignments (no-op
    /// when none were queued). <paramref name="formKeyRemap"/> is the post-auto-split
    /// map: outfit duplicates live in the output plugin and may have been relocated to
    /// "&lt;name&gt;_2.esp" etc., exactly as for the SkyPatcher ini.
    /// </summary>
    public bool WriteSpidConfig(string outputRootFolder, ModKey outputPlugin,
        IReadOnlyDictionary<FormKey, FormKey>? formKeyRemap = null)
    {
        if (_spidAssignments.Count == 0) return true;

        string fileName = BuildSpidFileName(outputPlugin);
        string outputPath = Path.Combine(outputRootFolder, fileName);

        // SPID sorts config paths ordinally and applies the FIRST matching outfit
        // entry, so a contesting file that still sorts earlier beats us. Say so rather
        // than letting the user find out in game.
        var earlier = _contestingSpidFiles
            .Where(f => StringComparer.Ordinal.Compare(f, fileName) < 0)
            .ToList();
        if (earlier.Count > 0)
        {
            AppendLog($"WARNING: SPID reads '{string.Join("', '", earlier)}' before the generated '{fileName}' " +
                      "(ordinal file-name order) and applies the first matching outfit entry, so the forwarded " +
                      "wig/antler outfit will lose for the NPCs those files cover. Rename the generated file (or " +
                      "the competing one) so it sorts first.", false, true);
        }

        try
        {
            Directory.CreateDirectory(outputRootFolder);

            var sb = new StringBuilder();
            sb.AppendLine("; Generated by NPC Plugin Chooser 2 — do not edit.");
            sb.AppendLine("; Republishes the wig/antler outfits NPC2 forwarded for NPCs whose outfit is assigned by a");
            sb.AppendLine("; runtime distributor. SPID applies the FIRST matching outfit entry across all *_DISTR.ini");
            sb.AppendLine("; files in ordinal file-name order, so this file is named to sort ahead of them.");
            sb.AppendLine("; Layout: Outfit = <outfit>|<string filters>|<form filters = the target NPC>");

            foreach (var a in _spidAssignments.OrderBy(a => a.NpcFormKey.ToString(), StringComparer.Ordinal))
            {
                var outfitKey = a.OutfitFormKey;
                if (formKeyRemap != null && formKeyRemap.TryGetValue(outfitKey, out var mapped)) outfitKey = mapped;

                // The note goes on its OWN line. SimpleIni (which SPID reads configs with)
                // only treats ';' as a comment at the START of a line — mid-line it is part
                // of the value, so a trailing note would end up inside the NPC form filter
                // and silently stop the entry from ever matching.
                sb.AppendLine();
                sb.Append("; ").AppendLine(BuildComment(a).Replace('\r', ' ').Replace('\n', ' '));
                sb.Append("Outfit = ")
                  .Append(FormatFormKeyForSpid(outfitKey))
                  .Append("|NONE|")
                  .AppendLine(FormatFormKeyForSpid(a.NpcFormKey));
            }

            static string BuildComment(SpidOutfitAssignment a)
            {
                var note = $"{a.NpcIdentifier} [{a.NpcFormKey}]";
                return a.PatchedFrom is { Length: > 0 } from ? note + " — patched from " + from : note;
            }

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            AppendLog($"Saved SPID outfit ini ({_spidAssignments.Count} NPC(s)) to " + outputPath, false, true);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("Failed to write the generated SPID outfit ini to: " + outputPath + Environment.NewLine +
                      ExceptionLogger.GetExceptionStack(ex), true, true);
            return false;
        }
    }

    /// <summary>SPID form reference: "0x&lt;local id&gt;~&lt;plugin file name&gt;"
    /// (CLibUtil get_record). Leading zeros are stripped — SPID's own config sanitizer
    /// would rewrite them into a load-order-relative runtime FormID.</summary>
    public static string FormatFormKeyForSpid(FormKey fk) =>
        "0x" + fk.ID.ToString("X") + "~" + fk.ModKey.ToString();
}
