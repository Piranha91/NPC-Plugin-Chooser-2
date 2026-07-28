using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The one assertion that catches the whole "a link nobody merged" defect class in a single pass:
/// every FormLink on every record of the written output plugin must point at the output mod itself,
/// at a plugin that is actually in the load order, or at nothing.
///
/// <para>A link into a plugin outside the load order becomes a master reference, and Mutagen then
/// refuses to write the plugin at all ("A referenced mod was not present on the load order being
/// sorted against"). Checking the master list alone would catch the same thing but names no record;
/// sweeping the links names the exact record and FormKey, which is what makes a failure actionable.</para>
///
/// <para>This is deliberately generic rather than per-record: the wig/antler routes all mint or
/// duplicate records into the output, and the merge walker never recurses INTO an output record, so
/// any of them could grow this defect. One sweep covers all of them.</para>
/// </summary>
internal static class OutputLinkSweep
{
    /// <summary>
    /// Asserts the plugin is internally consistent with the load order it was written against.
    /// <paramref name="because"/> is folded into the failure message.
    /// </summary>
    public static void AssertNoLinksOutsideLoadOrder(
        ISkyrimModGetter outMod,
        IEnumerable<ModKey> loadOrderKeys,
        ITestOutputHelper output,
        string because)
    {
        var allowed = loadOrderKeys.ToHashSet();
        allowed.Add(outMod.ModKey);

        var dangling = new List<string>();
        foreach (var rec in outMod.EnumerateMajorRecords())
        {
            foreach (var link in rec.EnumerateFormLinks())
            {
                if (link.FormKey.IsNull) continue;
                if (allowed.Contains(link.FormKey.ModKey)) continue;
                dangling.Add(
                    $"{rec.Registration.Name} {rec.FormKey} '{rec.EditorID ?? "(no EditorID)"}' " +
                    $"-> {link.FormKey}");
            }
        }

        if (dangling.Count > 0)
        {
            output.WriteLine("--- DANGLING OUTPUT LINKS ---");
            foreach (var d in dangling) output.WriteLine("  " + d);
        }

        dangling.Should().BeEmpty(
            "every output link must resolve to the output mod, a load-order plugin, or null — " + because);

        // Redundant with the sweep when it passes, but it is the condition Mutagen itself enforces,
        // so assert it directly rather than inferring it.
        outMod.ModHeader.MasterReferences.Select(m => m.Master)
            .Where(m => !allowed.Contains(m))
            .Should().BeEmpty("the output plugin must not master to a plugin outside the load order");
    }

    /// <summary>Dumps every output record, for diagnosing a route that produced nothing.</summary>
    public static void DumpRecords(ISkyrimModGetter outMod, ITestOutputHelper output, string label)
    {
        output.WriteLine($"--- OUTPUT RECORDS ({label}) ---");
        foreach (var rec in outMod.EnumerateMajorRecords())
        {
            output.WriteLine($"  {rec.Registration.Name} {rec.FormKey} '{rec.EditorID ?? "(no EditorID)"}'");
        }
    }

    /// <summary>
    /// The output records identified the way the rest of this codebase compares appearance data —
    /// by "&lt;type&gt; '&lt;EditorID&gt;'", not FormKey, so two runs that allocated different FormKeys
    /// for the same merged record still compare equal.
    /// </summary>
    public static HashSet<string> MergedRecordIdentities(ISkyrimModGetter outMod) =>
        outMod.EnumerateMajorRecords()
            .Select(r => $"{r.Registration.Name} '{r.EditorID ?? "(no EditorID)"}'")
            .ToHashSet();
}
