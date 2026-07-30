using System.Text;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>OutputValidator.RenderValidationHtml</c> — the render-time decomposition of the
/// validator's accumulated log lines into the ValidationLog.html structure: per-NPC collapsible
/// sections with count badges, the heading's selection/donor/winner as field chips, a leading
/// all-caps check name as the row chip, trailing "; k=v" clauses and "(k=v, ...)" groups as
/// labeled chips, and "(a | b)" diff lists as value-only chips. Exercised end-to-end through
/// the private renderer (via <see cref="Reflect"/>) with lines shaped exactly as
/// <c>Validate</c>/<c>ValidateNpc</c> emit them, so a change to either side of the line-shape
/// contract fails here.
/// </summary>
public class ValidationLogRenderTests
{
    private static string Render(params string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.AppendLine(line);
        return Reflect.InvokeStatic<OutputValidator, string>("RenderValidationHtml", sb)!;
    }

    private const string Heading =
        "NPC Adrianne Avenicci | AdrianneAvenicci | 013BA5:Skyrim.esm [013BA5:Skyrim.esm] " +
        "-> 'Pride of Valhalla' (donor 013BA5:Skyrim.esm, winner MyPatch.esp)";

    [Fact]
    public void NpcHeading_BecomesSectionTitle_WithSelectionDonorWinnerChips()
    {
        string html = Render(
            "=== Validate Output ===",
            "Mode: Create and Patch",
            "NPCs requested: 1",
            Heading,
            "  RECORD mismatch (HeadParts | FaceMorph); winner=MyPatch.esp",
            "Done. NPCs checked: 1. Issues: 1.");

        // The title keeps the NPC's identity; the selection tail moves into field chips.
        html.Should().Contain(
            "<summary>NPC Adrianne Avenicci | AdrianneAvenicci | 013BA5:Skyrim.esm [013BA5:Skyrim.esm]");
        html.Should().NotContain("-&gt; &#39;Pride of Valhalla&#39;",
            "the raw arrow tail should be decomposed, not echoed");
        html.Should().Contain("<b>selected</b> Pride of Valhalla");
        html.Should().Contain("<b>donor</b> 013BA5:Skyrim.esm");
        html.Should().Contain("<b>winner</b> MyPatch.esp");
        html.Should().Contain("class=\"badge warn\">1<", "one warning finding is badged on the section");
    }

    [Fact]
    public void Finding_LeadingCheckName_BecomesChip_AndDiffListBecomesValueChips()
    {
        string html = Render(
            Heading,
            "  RECORD mismatch (HeadParts | FaceMorph); winner=MyPatch.esp");

        html.Should().Contain("<span class=\"chip\">RECORD</span>");
        html.Should().Contain(">HeadParts</span>").And.Contain(">FaceMorph</span>",
            "the '|'-joined diff list renders as one chip per diff");
        html.Should().Contain("<b>winner</b> MyPatch.esp", "the trailing '; k=v' clause is a labeled chip");
        html.Should().NotContain("HeadParts | FaceMorph", "the raw joined form should be gone");
    }

    [Fact]
    public void Finding_TrailingParenKvGroup_BecomesLabeledChips()
    {
        string html = Render(
            Heading,
            "  TEMPLATE -> SomeTemplate [ABC123:Foo.esp] (inReport=True, hasSelection=False, honoured=True)");

        html.Should().Contain("<span class=\"chip\">TEMPLATE</span>");
        html.Should().Contain("<b>inReport</b> True");
        html.Should().Contain("<b>hasSelection</b> False");
        html.Should().Contain("-&gt; SomeTemplate [ABC123:Foo.esp]", "the message keeps the target");
    }

    [Fact]
    public void UnrecognizedShapes_SurviveVerbatim()
    {
        string html = Render(
            Heading,
            "  FACEGEN missing everywhere (no loose, no mod source, no BSA).");

        html.Should().Contain("<span class=\"chip\">FACEGEN</span>");
        html.Should().Contain("missing everywhere (no loose, no mod source, no BSA).",
            "a paren group that is neither k=v nor a diff list stays in the message");
    }

    [Fact]
    public void ModeAndRequestedCount_LiftIntoDocumentMetadata()
    {
        string html = Render(
            "=== Validate Output ===",
            "Mode: SkyPatcher",
            "NPCs requested: 42",
            "Done. NPCs checked: 42. Issues: 0.");

        html.Should().Contain("<span class=\"k\">Mode</span><span class=\"v\">SkyPatcher</span>");
        html.Should().Contain("<span class=\"k\">NPCs requested</span><span class=\"v\">42</span>");
        html.Should().NotContain("=== Validate Output ===");
    }
}
