using System.Collections.Generic;
using System.Text;

namespace NPC_Plugin_Chooser_2.BackEnd.Logging;

/// <summary>
/// Buffered HTML log builder for the end-of-run reports that are composed in memory and written
/// in one shot (ValidationLog, PatchWarnings, DeltaPatchingLog). Same theme and toolbar as the
/// streaming <see cref="HtmlLogWriter"/>; <see cref="Render"/> produces the complete document.
/// </summary>
public sealed class HtmlLogDocument
{
    private readonly StringBuilder _sb = new(16 * 1024);
    private bool _sectionOpen;

    public HtmlLogDocument(string title, IReadOnlyList<KeyValuePair<string, string>>? meta = null)
    {
        _sb.Append(HtmlLog.Prologue(title, meta));
    }

    public void AddRow(HtmlLogSeverity severity, string message,
        string? time = null, string? thread = null, string? chip = null,
        IReadOnlyList<KeyValuePair<string, string>>? fields = null, int indent = 0)
    {
        _sb.Append(HtmlLog.Row(severity, message, time, thread, chip, fields, indent));
    }

    public void AddSpacer()
    {
        _sb.Append(HtmlLog.Spacer);
    }

    /// <summary>Starts a collapsible section (closing any open one). The optional badge shows a
    /// count on the header, tinted by <paramref name="badgeSeverity"/>.</summary>
    public void BeginSection(string title, string? badge = null,
        HtmlLogSeverity badgeSeverity = HtmlLogSeverity.Info)
    {
        EndSection();
        _sb.Append(HtmlLog.SectionOpen(title, badge, badgeSeverity));
        _sectionOpen = true;
    }

    public void EndSection()
    {
        if (!_sectionOpen) return;
        _sb.Append(HtmlLog.SectionClose);
        _sectionOpen = false;
    }

    /// <summary>Prose block set in a UI font — for explanations that aren't log rows.</summary>
    public void AddParagraph(string text, bool muted = false)
    {
        _sb.Append(HtmlLog.Paragraph(text, muted));
    }

    /// <summary>A bordered per-item block: heading plus preformatted detail lines.</summary>
    public void AddCard(string heading, IReadOnlyList<string>? detailLines = null)
    {
        _sb.Append(HtmlLog.Card(heading, detailLines));
    }

    /// <summary>A bordered per-item block with structured detail content.</summary>
    public void AddCard(string heading, IReadOnlyList<HtmlDetailBlock> blocks)
    {
        _sb.Append(HtmlLog.Card(heading, blocks));
    }

    /// <summary>The complete document. Leaves the builder reusable (does not mutate state).</summary>
    public string Render()
    {
        return _sb.ToString()
               + (_sectionOpen ? HtmlLog.SectionClose : string.Empty)
               + HtmlLog.Epilogue();
    }
}
