using System;
using System.Collections.Generic;
using System.Text;

namespace NPC_Plugin_Chooser_2.BackEnd.Logging;

/// <summary>Structured content inside a log card (see <see cref="HtmlLog.Card(string, IReadOnlyList{HtmlDetailBlock})"/>).
/// Producers that know their detail's shape emit these instead of preformatted text, and the
/// renderer lays them out as key/value rows, field chips, tables, or titled sub-groups.</summary>
public abstract record HtmlDetailBlock;

/// <summary>One "key: value" fact. The value may be long (an override chain); it wraps.</summary>
public sealed record HtmlDetailFact(string Key, string Value) : HtmlDetailBlock;

/// <summary>One line's worth of k=v fields, rendered as labeled chips (optionally with a
/// leading row label).</summary>
public sealed record HtmlDetailFieldRow(
    string? Label, IReadOnlyList<KeyValuePair<string, string>> Fields) : HtmlDetailBlock;

/// <summary>A small comparison table: one labeled row per source, one column per shared key.
/// Null cells render empty (a row that lacks that column's key).</summary>
public sealed record HtmlDetailTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<(string Label, IReadOnlyList<string?> Cells)> Rows) : HtmlDetailBlock;

/// <summary>A titled sub-group (e.g. one failed probe) containing child blocks.</summary>
public sealed record HtmlDetailGroup(
    string Title, IReadOnlyList<HtmlDetailBlock> Children) : HtmlDetailBlock;

/// <summary>Fallback: a preformatted text line the classifier could not shape.</summary>
public sealed record HtmlDetailText(string Text) : HtmlDetailBlock;

/// <summary>Visual weight of one log row. Maps to a CSS class in the shared theme:
/// warnings and errors get a tinted background + colored left border (and count toward the
/// "Problems only" toolbar filter), Success is the green end-of-run marker, Muted is for
/// routine noise (perf lines, blank-ish rows) a reader usually skims past.</summary>
public enum HtmlLogSeverity
{
    Info,
    Muted,
    Warning,
    Error,
    Success,
}

/// <summary>
/// Shared markup for NPC2's HTML log files: one self-contained document per log (inline CSS +
/// JS, no external references) so a user can open it straight from the app folder or attach it
/// to a bug report. The CSS/JS live in the &lt;head&gt;, and rows are appended incrementally,
/// so a file truncated by a crash or read mid-run still renders — browsers tolerate the missing
/// closing tags. Every writer keeps full detail (timestamps, thread ids, categories, message
/// text); the HTML only adds presentation: severity coloring, collapsible sections, a filter
/// box, and a "Problems only" toggle.
///
/// <para>Consumed through <see cref="HtmlLogWriter"/> (streaming, for loggers that hold a file
/// open and must survive hangs — StartupLogger et al.) and <see cref="HtmlLogDocument"/>
/// (buffered, for end-of-run reports written in one shot). <c>EventLogger</c> uses these
/// helpers directly because it appends per call without holding a handle.</para>
/// </summary>
public static class HtmlLog
{
    /// <summary>HTML-encodes text destined for markup. Null-safe (returns "").</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Maps the free-text category strings the txt-era loggers used ("INFO", "WARN",
    /// "ERROR", "DONE", ...) to a row severity, so existing call sites keep their signatures.</summary>
    public static HtmlLogSeverity SeverityFromCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return HtmlLogSeverity.Info;
        if (category.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return HtmlLogSeverity.Warning;
        if (category.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlLogSeverity.Error;
        }
        if (category.Contains("DONE", StringComparison.OrdinalIgnoreCase)) return HtmlLogSeverity.Success;
        return HtmlLogSeverity.Info;
    }

    public static string CssClass(HtmlLogSeverity severity) => severity switch
    {
        HtmlLogSeverity.Muted => " muted",
        HtmlLogSeverity.Warning => " warn",
        HtmlLogSeverity.Error => " err",
        HtmlLogSeverity.Success => " ok",
        _ => string.Empty,
    };

    /// <summary>
    /// The document head + toolbar + metadata block, through the opening of the row container.
    /// With <paramref name="tableColumns"/> the body is a real table (used by the columnar
    /// MemoryLog); otherwise rows are flex divs and <see cref="SectionOpen"/> groups them into
    /// collapsible sections.
    /// </summary>
    public static string Prologue(
        string title,
        IReadOnlyList<KeyValuePair<string, string>>? meta = null,
        IReadOnlyList<string>? tableColumns = null)
    {
        var sb = new StringBuilder(8192);
        sb.Append("<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>");
        sb.Append(Escape(title));
        sb.Append("</title>\n<style>\n").Append(Css).Append("\n</style>\n<script>\n").Append(Js)
          .Append("\n</script>\n</head>\n<body>\n");

        sb.Append("<div class=\"toolbar\"><span class=\"tb-title\">").Append(Escape(title))
          .Append("</span><input id=\"q\" type=\"search\" placeholder=\"Filter rows…\">")
          .Append("<label><input type=\"checkbox\" id=\"probs\"> Problems only</label>")
          .Append("<button id=\"expand\" type=\"button\">Expand all</button>")
          .Append("<button id=\"collapse\" type=\"button\">Collapse all</button>")
          .Append("<span id=\"count\"></span></div>\n");

        if (meta != null && meta.Count > 0)
        {
            sb.Append("<div class=\"meta\">");
            foreach (var kv in meta)
            {
                sb.Append("<div><span class=\"k\">").Append(Escape(kv.Key))
                  .Append("</span><span class=\"v\">").Append(Escape(kv.Value)).Append("</span></div>");
            }
            sb.Append("</div>\n");
        }

        sb.Append("<main class=\"log\">\n");
        if (tableColumns != null)
        {
            sb.Append("<table>\n<thead><tr>");
            foreach (var col in tableColumns)
            {
                sb.Append("<th>").Append(Escape(col)).Append("</th>");
            }
            sb.Append("</tr></thead>\n<tbody>\n");
        }
        return sb.ToString();
    }

    /// <summary>One log row. Optional cells (time, thread, chip) render as fixed columns before
    /// the message; pass null to omit a column entirely. Optional <paramref name="fields"/>
    /// render as labeled chips after the message text (an empty key gives a value-only chip,
    /// used for plain lists like a record's diff names); <paramref name="indent"/> shifts the
    /// message right by that many characters, preserving a producer's indentation hierarchy.</summary>
    public static string Row(
        HtmlLogSeverity severity, string message, string? time = null, string? thread = null,
        string? chip = null, IReadOnlyList<KeyValuePair<string, string>>? fields = null, int indent = 0)
    {
        var sb = new StringBuilder(128 + message.Length);
        sb.Append("<div class=\"row").Append(CssClass(severity)).Append("\">");
        if (time != null) sb.Append("<span class=\"t\">").Append(Escape(time)).Append("</span>");
        if (thread != null) sb.Append("<span class=\"th\">").Append(Escape(thread)).Append("</span>");
        if (chip != null) sb.Append("<span class=\"chip\">").Append(Escape(chip)).Append("</span>");
        sb.Append("<span class=\"msg\"");
        if (indent > 0) sb.Append(" style=\"padding-left:").Append(indent).Append("ch\"");
        sb.Append('>').Append(Escape(message));
        if (fields != null && fields.Count > 0)
        {
            sb.Append("<span class=\"mfields\">");
            foreach (var f in fields)
            {
                sb.Append("<span class=\"fchip").Append(ValueToneClass(f.Value)).Append("\">");
                if (f.Key.Length > 0)
                {
                    sb.Append("<b>").Append(Escape(f.Key)).Append("</b> ");
                }
                sb.Append(Escape(f.Value)).Append("</span>");
            }
            sb.Append("</span>");
        }
        sb.Append("</span></div>\n");
        return sb.ToString();
    }

    /// <summary>One table-mode row (all cells escaped; the last cell is the free-text column).</summary>
    public static string TableRow(HtmlLogSeverity severity, IReadOnlyList<string> cells)
    {
        var sb = new StringBuilder(128);
        sb.Append("<tr class=\"row").Append(CssClass(severity)).Append("\">");
        foreach (var cell in cells)
        {
            sb.Append("<td>").Append(Escape(cell)).Append("</td>");
        }
        sb.Append("</tr>\n");
        return sb.ToString();
    }

    /// <summary>Opens a collapsible section. Callers must emit <see cref="SectionClose"/> before
    /// the next section or the epilogue (the writers track this). The optional badge shows a
    /// count on the section header, tinted by <paramref name="badgeSeverity"/>. Pass
    /// <paramref name="open"/> = false for sections that should start collapsed (e.g. one
    /// section per HTTP transaction in a long log).</summary>
    public static string SectionOpen(string title, string? badge = null,
        HtmlLogSeverity badgeSeverity = HtmlLogSeverity.Info, bool open = true)
    {
        var sb = new StringBuilder(96);
        sb.Append(open ? "<details class=\"sec\" open><summary>" : "<details class=\"sec\"><summary>")
          .Append(Escape(title));
        if (badge != null)
        {
            sb.Append("<span class=\"badge").Append(CssClass(badgeSeverity)).Append("\">")
              .Append(Escape(badge)).Append("</span>");
        }
        sb.Append("</summary>\n");
        return sb.ToString();
    }

    /// <summary>A collapsed block for bulky payloads (e.g. a response body): a summary line the
    /// reader can expand into the full preformatted text. Nothing is truncated.</summary>
    public static string Collapsible(string title, string text)
    {
        return "<details class=\"fold\"><summary>" + Escape(title) + "</summary><pre>"
               + Escape(text) + "</pre></details>\n";
    }

    /// <summary>Parses "k=v, k=v, ..." — every comma-separated segment must be a k=v pair whose
    /// key is a single identifier-ish token (no ':', no spaces), so prose containing commas and
    /// a stray '=' never misparses. Requires at least two fields. Shared by the renderers that
    /// surface latent k=v structure in log lines (warning cards, per-NPC diagnostics).</summary>
    public static bool TryParseFieldList(string text, out List<KeyValuePair<string, string>> fields)
    {
        fields = new List<KeyValuePair<string, string>>();
        foreach (var segment in text.Split(", "))
        {
            int eq = segment.IndexOf('=');
            if (eq <= 0) return false;
            string key = segment[..eq].Trim();
            if (key.Length == 0 || key.Contains(':') || key.Contains(' ')) return false;
            fields.Add(new KeyValuePair<string, string>(key, segment[(eq + 1)..].Trim()));
        }
        return fields.Count >= 2;
    }

    public const string SectionClose = "</details>\n";

    /// <summary>Vertical breathing room — replaces the blank lines of the txt era.</summary>
    public const string Spacer = "<div class=\"spacer\"></div>\n";

    /// <summary>Prose block (explanations, notes) set in a UI font, distinct from log rows.</summary>
    public static string Paragraph(string text, bool muted = false) =>
        "<p class=\"para" + (muted ? " muted" : "") + "\">" + Escape(text) + "</p>\n";

    /// <summary>A bordered per-item block (e.g. one NPC in a warnings report): a heading plus
    /// optional preformatted detail lines beneath it.</summary>
    public static string Card(string heading, IReadOnlyList<string>? detailLines = null)
    {
        var sb = new StringBuilder(160);
        sb.Append("<div class=\"card\"><h3>").Append(Escape(heading)).Append("</h3>");
        if (detailLines != null && detailLines.Count > 0)
        {
            sb.Append("<pre>");
            for (int i = 0; i < detailLines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(Escape(detailLines[i]));
            }
            sb.Append("</pre>");
        }
        sb.Append("</div>\n");
        return sb.ToString();
    }

    /// <summary>A bordered per-item block whose detail is structured (facts, field rows, tables,
    /// sub-groups) rather than preformatted text.</summary>
    public static string Card(string heading, IReadOnlyList<HtmlDetailBlock> blocks)
    {
        var sb = new StringBuilder(512);
        sb.Append("<div class=\"card\"><h3>").Append(Escape(heading)).Append("</h3>");
        AppendBlocks(sb, blocks);
        sb.Append("</div>\n");
        return sb.ToString();
    }

    private static void AppendBlocks(StringBuilder sb, IReadOnlyList<HtmlDetailBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HtmlDetailFact fact:
                    sb.Append("<div class=\"fact\"><span class=\"fk\">").Append(Escape(fact.Key))
                      .Append("</span><span class=\"fv\">").Append(Escape(fact.Value))
                      .Append("</span></div>");
                    break;

                case HtmlDetailFieldRow row:
                    sb.Append("<div class=\"frow\">");
                    if (row.Label != null)
                    {
                        sb.Append("<span class=\"fk\">").Append(Escape(row.Label)).Append("</span>");
                    }
                    foreach (var f in row.Fields)
                    {
                        sb.Append("<span class=\"fchip").Append(ValueToneClass(f.Value)).Append("\"><b>")
                          .Append(Escape(f.Key)).Append("</b> ").Append(Escape(f.Value)).Append("</span>");
                    }
                    sb.Append("</div>");
                    break;

                case HtmlDetailTable table:
                    sb.Append("<div class=\"dtwrap\"><table class=\"dt\"><thead><tr><th></th>");
                    foreach (var col in table.Columns)
                    {
                        sb.Append("<th>").Append(Escape(col)).Append("</th>");
                    }
                    sb.Append("</tr></thead><tbody>");
                    foreach (var (label, cells) in table.Rows)
                    {
                        sb.Append("<tr><th scope=\"row\">").Append(Escape(label)).Append("</th>");
                        foreach (var cell in cells)
                        {
                            sb.Append("<td").Append(cell == null ? " class=\"na\"" : ValueToneAttr(cell))
                              .Append('>').Append(Escape(cell ?? "")).Append("</td>");
                        }
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table></div>");
                    break;

                case HtmlDetailGroup group:
                    sb.Append("<div class=\"dgroup\"><h4>").Append(Escape(group.Title)).Append("</h4>");
                    AppendBlocks(sb, group.Children);
                    sb.Append("</div>");
                    break;

                case HtmlDetailText text:
                    sb.Append("<pre>").Append(Escape(text.Text)).Append("</pre>");
                    break;
            }
        }
    }

    /// <summary>Presentation-only tinting of common verdict values so a reader can scan a
    /// comparison table for the failing cell: negative verdicts warn-tinted, positives ok,
    /// not-evaluated muted. Unknown values stay untinted.</summary>
    private static string ValueToneClass(string value)
    {
        string v = value.Trim();
        int paren = v.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) v = v[..paren]; // "yes (Skyrim - Meshes0.bsa)" → "yes"
        return v switch
        {
            "False" or "no" or "NotFound" => " bad",
            "True" or "yes" => " good",
            "NotEvaluated" or "-" => " na",
            _ => string.Empty,
        };
    }

    private static string ValueToneAttr(string value)
    {
        string cls = ValueToneClass(value);
        return cls.Length == 0 ? string.Empty : " class=\"" + cls.TrimStart() + "\"";
    }

    /// <summary>Closes the row container and document. Streamed files that never reach this
    /// (crash, kill) still render; this just makes clean shutdowns produce valid HTML.</summary>
    public static string Epilogue(bool tableMode = false) =>
        (tableMode ? "</tbody>\n</table>\n" : string.Empty) + "</main>\n</body>\n</html>\n";

    // Theme: system fonts only, light/dark via prefers-color-scheme, everything inline so the
    // file stays self-contained. Row layout is flex with fixed-ish utility columns so thousands
    // of rows stay cheap to render and filter.
    private const string Css = """
:root{color-scheme:light dark;
 --bg:#f6f7f9;--fg:#1c1e21;--muted:#6b7280;--card:#ffffff;--border:#e3e5e8;
 --hover:#eef1f5;--chip:#e8ecf2;--chip-fg:#3b4252;
 --warn:#9a6006;--warn-bg:#fdf5e0;--err:#b91c1c;--err-bg:#fdecec;--ok:#15803d}
@media(prefers-color-scheme:dark){:root{
 --bg:#141518;--fg:#dcdee3;--muted:#8f939c;--card:#1c1e22;--border:#2b2e34;
 --hover:#22252b;--chip:#282c33;--chip-fg:#aab2c0;
 --warn:#e0a63a;--warn-bg:#2e2713;--err:#ef6a6a;--err-bg:#331717;--ok:#57c26b}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);
 font:12.5px/1.55 "Cascadia Mono",Consolas,"Courier New",monospace}
.toolbar{position:sticky;top:0;z-index:10;display:flex;flex-wrap:wrap;align-items:center;gap:10px;
 padding:8px 14px;background:var(--card);border-bottom:1px solid var(--border);
 font-family:"Segoe UI",system-ui,sans-serif;font-size:13px}
.tb-title{font-weight:600;margin-right:6px}
.toolbar input[type=search]{flex:1 1 160px;max-width:340px;padding:4px 8px;
 border:1px solid var(--border);border-radius:6px;background:var(--bg);color:var(--fg);font:inherit}
.toolbar label{display:flex;align-items:center;gap:5px;color:var(--muted);white-space:nowrap}
.toolbar button{padding:3px 10px;border:1px solid var(--border);border-radius:6px;
 background:var(--bg);color:var(--fg);font:inherit;cursor:pointer}
.toolbar button:hover{background:var(--hover)}
#count{margin-left:auto;color:var(--muted);white-space:nowrap}
.meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,max-content));gap:2px 32px;
 padding:10px 16px 6px;font-family:"Segoe UI",system-ui,sans-serif;font-size:12.5px}
.meta .k{color:var(--muted);margin-right:8px}
.log{padding:6px 0 48px}
.row{display:flex;gap:10px;padding:1px 14px;border-left:3px solid transparent;align-items:baseline}
.row:hover{background:var(--hover)}
.row .t,.row .th{color:var(--muted);flex:0 0 auto;font-size:11.5px}
.row .th{min-width:3em;text-align:right}
.chip{flex:0 0 auto;background:var(--chip);color:var(--chip-fg);border-radius:4px;padding:0 6px;
 font-size:10.5px;line-height:1.7;align-self:center;max-width:28em;overflow:hidden;
 text-overflow:ellipsis;white-space:nowrap}
.msg{flex:1;min-width:0;white-space:pre-wrap;overflow-wrap:anywhere}
.row.warn{border-left-color:var(--warn);background:var(--warn-bg)}
.row.warn .msg{color:var(--warn)}
.row.err{border-left-color:var(--err);background:var(--err-bg)}
.row.err .msg{color:var(--err)}
.row.ok{border-left-color:var(--ok)}
.row.ok .msg{color:var(--ok)}
.row.muted .msg{color:var(--muted)}
.spacer{height:9px}
details.sec{margin:8px 0 2px}
details.sec>summary{cursor:pointer;font-family:"Segoe UI",system-ui,sans-serif;font-size:13px;
 font-weight:600;padding:5px 14px;background:var(--card);
 border-top:1px solid var(--border);border-bottom:1px solid var(--border)}
details.sec>summary:hover{background:var(--hover)}
.badge{display:inline-block;margin-left:8px;padding:0 7px;border-radius:9px;font-size:11px;
 font-weight:600;background:var(--chip);color:var(--chip-fg)}
.badge.warn{background:var(--warn);color:#fff}
.badge.err{background:var(--err);color:#fff}
.badge.ok{background:var(--ok);color:#fff}
.mfields{display:inline-flex;flex-wrap:wrap;gap:4px 6px;margin-left:8px;vertical-align:baseline}
details.fold{margin:4px 0}
details.fold>summary{cursor:pointer;color:var(--muted);font-family:"Segoe UI",system-ui,sans-serif;
 font-size:11.5px}
details.fold>pre{margin:4px 0 0;padding:6px 10px;background:var(--bg);border:1px solid var(--border);
 border-radius:6px;font:inherit;font-size:11.5px;white-space:pre-wrap;overflow-wrap:anywhere}
.para{margin:8px 16px;max-width:78em;font-family:"Segoe UI",system-ui,sans-serif;font-size:13px}
.para.muted{color:var(--muted);font-size:12px}
.card{margin:8px 14px;padding:8px 12px;background:var(--card);border:1px solid var(--border);
 border-radius:8px}
.card h3{margin:0;font:600 13px/1.5 "Segoe UI",system-ui,sans-serif}
.card pre{margin:4px 0 0;font:inherit;font-size:11.5px;color:var(--muted);
 white-space:pre-wrap;overflow-wrap:anywhere}
.fact{display:flex;gap:8px;align-items:baseline;margin:3px 0;font-size:11.5px}
.fact .fv{overflow-wrap:anywhere}
.fk{flex:0 0 auto;color:var(--muted);font-family:"Segoe UI",system-ui,sans-serif;font-size:11.5px}
.frow{display:flex;flex-wrap:wrap;gap:4px 6px;align-items:baseline;margin:3px 0}
.fchip{background:var(--chip);border-radius:4px;padding:0 6px;font-size:11px;line-height:1.7;
 overflow-wrap:anywhere}
.fchip b{font-weight:600;color:var(--muted);font-family:"Segoe UI",system-ui,sans-serif;
 font-size:10.5px}
.fchip.bad{background:var(--err-bg);color:var(--err)}
.fchip.good{color:var(--ok)}
.fchip.na{color:var(--muted)}
.dtwrap{overflow-x:auto;margin:6px 0}
table.dt{width:auto;border:1px solid var(--border);font-size:11.5px}
table.dt thead th{padding:2px 10px;text-align:left;font-size:11px;color:var(--muted);
 border-bottom:1px solid var(--border);background:transparent}
table.dt tbody th{padding:2px 10px;text-align:left;font-weight:600;
 font-family:"Segoe UI",system-ui,sans-serif;font-size:11.5px;white-space:nowrap}
table.dt td{padding:2px 10px;text-align:left;white-space:normal;overflow-wrap:anywhere;width:auto}
table.dt td.bad{color:var(--err);background:var(--err-bg)}
table.dt td.good{color:var(--ok)}
table.dt td.na{color:var(--muted)}
.dgroup{margin:8px 0 2px;padding:6px 10px;border-left:3px solid var(--warn);
 background:var(--warn-bg);border-radius:0 6px 6px 0}
.dgroup h4{margin:0 0 2px;font:600 12px/1.5 "Segoe UI",system-ui,sans-serif;color:var(--warn)}
table{border-collapse:collapse;width:100%;font-size:12px}
thead th{background:var(--card);border-bottom:1px solid var(--border);padding:4px 10px;
 text-align:right;font-family:"Segoe UI",system-ui,sans-serif;font-size:12px}
thead th:last-child{text-align:left}
td{padding:1px 10px;text-align:right;white-space:nowrap}
td:last-child{text-align:left;white-space:pre-wrap;overflow-wrap:anywhere;width:100%}
tbody tr:hover{background:var(--hover)}
""";

    // Filtering + section collapse. Runs after parse (DOMContentLoaded), so it also works on a
    // truncated (crashed-mid-write) file. Debounced; hides sections whose rows all filtered out.
    private const string Js = """
(function(){
function init(){
 var q=document.getElementById('q'),probs=document.getElementById('probs'),
     count=document.getElementById('count'),
     ex=document.getElementById('expand'),co=document.getElementById('collapse');
 if(!q||!probs||!count)return;
 if(!document.querySelector('details.sec')){
  if(ex)ex.style.display='none';
  if(co)co.style.display='none';
 }
 var timer=null;
 function apply(){
  var term=q.value.toLowerCase(),only=probs.checked,shown=0,total=0,prob=0;
  document.querySelectorAll('.row').forEach(function(r){
   total++;
   var isProb=r.classList.contains('warn')||r.classList.contains('err');
   if(isProb)prob++;
   var ok=(!term||r.textContent.toLowerCase().indexOf(term)>=0)&&(!only||isProb);
   r.style.display=ok?'':'none';
   if(ok)shown++;
  });
  var active=term||only;
  document.querySelectorAll('details.sec').forEach(function(d){
   var any=false;
   d.querySelectorAll('.row').forEach(function(r){if(r.style.display!=='none')any=true;});
   d.style.display=(active&&!any)?'none':'';
   if(active&&any)d.open=true;
  });
  count.textContent=shown+' / '+total+' rows'+(prob?' — '+prob+' flagged':'');
 }
 q.addEventListener('input',function(){clearTimeout(timer);timer=setTimeout(apply,90);});
 probs.addEventListener('change',apply);
 if(ex)ex.addEventListener('click',function(){
  document.querySelectorAll('details.sec').forEach(function(d){d.open=true;});});
 if(co)co.addEventListener('click',function(){
  document.querySelectorAll('details.sec').forEach(function(d){d.open=false;});});
 apply();
}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',init);
else init();
})();
""";
}
