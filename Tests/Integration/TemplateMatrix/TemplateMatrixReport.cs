using System.IO;
using System.Net;
using System.Text;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>One assertion the suite made, and how it came out. Rendered at the top of the report.</summary>
internal sealed record MatrixCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Emits a self-contained HTML side-by-side of the whole matrix — written on every run, pass or fail,
/// because a failing run is exactly when it is wanted.
///
/// <para>Modelled on <c>PatchVerifyRunner</c>'s VerifyManifest: the two things being compared are put
/// ADJACENT with a caption stating what agreement and disagreement each mean. Here the payload is
/// records and file hashes rather than photographs, but the shape is the same. The ladder's decision
/// sits beside the disk result so a failure says whether the classifier or the writer is at fault.</para>
///
/// <para>No images: the fixture's <c>.nif</c> files are placeholders, so rendering them would imply a
/// verification that did not happen.</para>
///
/// <para><b>This is not the assertion mechanism.</b> The tests assert; the report explains.</para>
/// </summary>
internal static class TemplateMatrixReport
{
    /// <summary>What each specimen is for, and what the two settings should look like — stated as the
    /// expected OBSERVATION, not the rule.</summary>
    private static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>
    {
        [SpecimenRole.PlainSelf] =
            "Untemplated, replaced from its own record. Both settings must produce the same record and the " +
            "same face file — any difference here means template handling reached an NPC it has no business touching.",
        [SpecimenRole.PlainShared] =
            "Untemplated, but wearing a different NPC's face. Both settings must agree. In plain Create mode " +
            "the validator rejects the swap outright, so 'absent' is the correct observation there, not a failure.",
        [SpecimenRole.TemplatedA] =
            "Templated to #5, given Mod X. Under own-copy it gets its OWN face file, holding Mod X's copy of the " +
            "terminus's face, and its record carries the terminus's appearance with Traits cleared. Under inherit " +
            "it gets no file of its own and keeps inheriting.",
        [SpecimenRole.TemplatedB] =
            "Templated to the SAME terminus as #3 but given Mod Y. Under own-copy its file must exist AND DIFFER " +
            "from #3's — identical hashes would mean flattening ran but both still resolved to the same source, " +
            "which is the failure this whole exercise exists to catch.",
        [SpecimenRole.Terminus] =
            "#3 and #4's terminus, with its own selection (Mod Z). Its own path must hold Mod Z's file in every " +
            "cell — if #3's or #4's choice appears here, a template follower has stamped its face onto the NPC it inherits from.",
        [SpecimenRole.TemplatedShared] =
            "Templated AND wearing a templated donor's face: the chain and the face swap interact. The subject is " +
            "the DONOR's terminus, not this NPC's. Rejected in plain Create mode like #2.",
        [SpecimenRole.TemplatedLeveled] =
            "Chain ends in a levelled list, so there is no fixed face to copy. Both settings must produce an " +
            "IDENTICAL record that still inherits — a difference means the own-copy gate leaked past its carve-out.",
        [SpecimenRole.TemplatedUnfollowable] =
            "Template cycle. The ladder aborts it and the patcher leaves it unchanged, so it is absent from the " +
            "output in BOTH settings. Note it still passes screening — which is why presence in the output, not " +
            "in PatchedTargets, is what this suite gates on.",
    };

    public static string Write(TemplateMatrixFixture fixture, IReadOnlyList<CellResult> cells,
        IReadOnlyList<MatrixCheck> checks)
    {
        var fx = fixture.Fixture!;
        var byCell = cells.ToDictionary(c => c.Cell.Index);
        var sb = new StringBuilder();

        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Templated NPC patch matrix</title>
            <style>
            :root{--bg:#fff;--fg:#1b1b1b;--muted:#666;--line:#dcdcdc;--card:#fafafa;
                  --ok:#0a7a35;--bad:#b3261e;--warn:#8a6100;--accentbg:#eef4ff;--code:#f2f2f2}
            @media (prefers-color-scheme:dark){
            :root{--bg:#14161a;--fg:#e6e6e6;--muted:#9aa0a6;--line:#333941;--card:#1b1e24;
                  --ok:#5bd98a;--bad:#ff7a70;--warn:#e8b64c;--accentbg:#1d2634;--code:#22262c}}
            *{box-sizing:border-box}
            body{margin:0;padding:24px;background:var(--bg);color:var(--fg);
                 font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}
            h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}
            h3{font-size:15px;margin:18px 0 6px;color:var(--muted);font-weight:600}
            .sub{color:var(--muted);margin:0 0 20px}
            .card{border:1px solid var(--line);border-radius:8px;background:var(--card);
                  padding:14px 16px;margin:0 0 18px}
            .cap{color:var(--muted);margin:0 0 12px;max-width:72ch}
            table{border-collapse:collapse;width:100%;margin:0 0 12px}
            th,td{border:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}
            th{background:var(--accentbg);font-weight:600;width:22%}
            td{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:12.5px;word-break:break-word}
            .scroll{overflow-x:auto}
            .ok{color:var(--ok);font-weight:600}.bad{color:var(--bad);font-weight:600}
            .warn{color:var(--warn);font-weight:600}.muted{color:var(--muted)}
            .diff td{background:var(--accentbg)}
            .rel{display:inline-block;padding:1px 7px;border-radius:10px;font-size:11.5px;
                 font-weight:700;letter-spacing:.03em;border:1px solid currentColor}
            code{background:var(--code);padding:1px 4px;border-radius:3px}
            ul.checks{list-style:none;padding:0;margin:0}
            ul.checks li{padding:3px 0;border-bottom:1px solid var(--line)}
            </style></head><body>
            """);

        sb.Append("<h1>Templated NPC patch matrix</h1>");
        sb.Append($"<p class=\"sub\">{cells.Count} cells &times; {TemplateMatrixSettingsBuilder.SpecimenRoles.Count} " +
                  "synthetic specimens, real patcher, read back off disk. " +
                  "The two <b>Template Handling</b> settings are shown adjacent for every output mode; " +
                  "differing values are highlighted.</p>");

        // ---- checks ----
        int failed = checks.Count(c => !c.Passed);
        sb.Append("<h2>Assertions</h2><div class=\"card\">");
        sb.Append(failed == 0
            ? $"<p class=\"ok\">All {checks.Count} checks passed.</p>"
            : $"<p class=\"bad\">{failed} of {checks.Count} checks FAILED.</p>");
        sb.Append("<ul class=\"checks\">");
        foreach (var c in checks)
        {
            sb.Append($"<li><span class=\"{(c.Passed ? "ok" : "bad")}\">{(c.Passed ? "PASS" : "FAIL")}</span> " +
                      $"{Esc(c.Name)}<br><span class=\"muted\">{Esc(c.Detail)}</span></li>");
        }
        sb.Append("</ul></div>");

        // ---- decisive pairs ----
        sb.Append("<h2>The two comparisons that carry the suite</h2>");
        AppendDecisiveFaceGen(sb, byCell);
        AppendDecisiveControls(sb, byCell);

        // ---- per specimen ----
        sb.Append("<h2>Per specimen</h2>");
        foreach (var role in TemplateMatrixSettingsBuilder.SpecimenRoles)
        {
            var selection = TemplateMatrixSettingsBuilder.Selections.First(s => s.TargetRole == role);
            sb.Append("<div class=\"card\">");
            sb.Append($"<h3 style=\"color:var(--fg);font-size:16px\">{Esc(role)} " +
                      $"<span class=\"muted\" style=\"font-weight:400\">&mdash; {fx.Npc(role)} " +
                      $"&larr; {Esc(selection.Mod)} (donor {Esc(selection.DonorRole)})</span></h3>");
            sb.Append($"<p class=\"cap\">{Esc(Captions.TryGetValue(role, out var cap) ? cap : string.Empty)}</p>");

            foreach (var (modeName, inherit, ownCopy) in TemplateMatrixCells.Pairs)
            {
                sb.Append($"<h3>{Esc(modeName)}</h3>");
                AppendSideBySide(sb, byCell[inherit.Index][role], byCell[ownCopy.Index][role]);
            }
            sb.Append("</div>");
        }

        sb.Append("<h2>Every FaceGen file written, per cell</h2>");
        foreach (var cell in cells)
        {
            sb.Append($"<div class=\"card\"><h3>{Esc(cell.Cell.Name)}</h3><div class=\"scroll\"><table>");
            sb.Append("<tr><th style=\"width:14%\">hash</th><th style=\"width:auto\">path</th></tr>");
            foreach (var (rel, hash) in cell.FaceGenFiles.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append($"<tr><td>{Esc(hash)}</td><td>{Esc(rel)}</td></tr>");
            }
            if (cell.FaceGenFiles.Count == 0) sb.Append("<tr><td colspan=\"2\" class=\"muted\">(none)</td></tr>");
            sb.Append("</table></div>");
            if (cell.InvalidSelections.Count > 0)
            {
                sb.Append("<p class=\"muted\">Validator rejections: " +
                          Esc(string.Join(" | ", cell.InvalidSelections)) + "</p>");
            }
            sb.Append("</div>");
        }

        sb.Append("</body></html>");

        Directory.CreateDirectory(fixture.ReportDirectory);
        var path = Path.Combine(fixture.ReportDirectory, "TemplateMatrix.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static void AppendDecisiveFaceGen(StringBuilder sb, IReadOnlyDictionary<int, CellResult> byCell)
    {
        sb.Append("<div class=\"card\"><h3 style=\"color:var(--fg)\">#3 and #4 &mdash; two NPCs, one terminus, different mods</h3>");
        sb.Append("<p class=\"cap\">Nothing else distinguishes the feature working from the feature merely not " +
                  "crashing. Under own-copy each must have its own face file and the two files must differ; " +
                  "identical hashes would mean flattening ran but both still resolved to the terminus.</p>");
        sb.Append("<div class=\"scroll\"><table><tr><th>Mode / setting</th><th>#3 file (Mod X)</th>" +
                  "<th>#4 file (Mod Y)</th><th>expected relationship</th></tr>");

        foreach (var cell in byCell.Values.OrderBy(c => c.Cell.Index))
        {
            var a = cell[SpecimenRole.TemplatedA];
            var b = cell[SpecimenRole.TemplatedB];
            bool ownCopy = cell.Cell.TemplateMode == TemplateHandlingMode.GiveEachNpcOwnCopy;

            // In SkyPatcher mode every target gets its own surrogate FormKey, so each has a distinct
            // destination path in BOTH settings; what the setting changes there is the surrogate RECORD.
            string relationship = cell.Cell.UseSkyPatcher
                ? "these must DIFFER (each surrogate owns a path)"
                : ownCopy ? "these must DIFFER" : "neither may exist";

            string verdict;
            if (cell.Cell.UseSkyPatcher || ownCopy)
            {
                verdict = a.OwnFaceGenHash != null && b.OwnFaceGenHash != null && a.OwnFaceGenHash != b.OwnFaceGenHash
                    ? "<span class=\"ok\">differ</span>" : "<span class=\"bad\">NOT distinct</span>";
            }
            else
            {
                verdict = a.OwnFaceGenHash == null && b.OwnFaceGenHash == null
                    ? "<span class=\"ok\">neither exists</span>" : "<span class=\"bad\">a file exists</span>";
            }

            sb.Append($"<tr><td>{Esc(cell.Cell.Name)}</td>" +
                      $"<td>{FaceGenCell(a)}</td><td>{FaceGenCell(b)}</td>" +
                      $"<td><span class=\"rel\">{Esc(relationship)}</span><br>{verdict}</td></tr>");
        }
        sb.Append("</table></div></div>");
    }

    private static void AppendDecisiveControls(StringBuilder sb, IReadOnlyDictionary<int, CellResult> byCell)
    {
        sb.Append("<div class=\"card\"><h3 style=\"color:var(--fg)\">#1, #2, #7, #8 &mdash; the inertness controls</h3>");
        sb.Append("<p class=\"cap\">The strongest guard that the feature does nothing where it must do nothing. " +
                  "#1 and #2 are untemplated; #7's chain ends in a levelled list and #8's loops, so both are " +
                  "carve-outs the own-copy setting must not touch. Their records must be identical across the two settings.</p>");
        sb.Append("<div class=\"scroll\"><table><tr><th>Mode</th><th>Specimen</th><th>Inherit</th>" +
                  "<th>Give each NPC its own copy</th><th>expected relationship</th></tr>");

        foreach (var (modeName, inherit, ownCopy) in TemplateMatrixCells.Pairs)
        {
            foreach (var role in new[]
                     {
                         SpecimenRole.PlainSelf, SpecimenRole.PlainShared,
                         SpecimenRole.TemplatedLeveled, SpecimenRole.TemplatedUnfollowable,
                     })
            {
                var a = byCell[inherit.Index][role];
                var b = byCell[ownCopy.Index][role];
                bool same = a.AppearanceSignature == b.AppearanceSignature
                            && a.OwnFaceGenHash == b.OwnFaceGenHash;
                sb.Append($"<tr class=\"{(same ? "" : "diff")}\"><td>{Esc(modeName)}</td><td>{Esc(role)}</td>" +
                          $"<td>{Esc(a.AppearanceSignature)}<br>facegen={Esc(a.OwnFaceGenHash ?? "none")}</td>" +
                          $"<td>{Esc(b.AppearanceSignature)}<br>facegen={Esc(b.OwnFaceGenHash ?? "none")}</td>" +
                          $"<td><span class=\"rel\">these must be IDENTICAL</span><br>" +
                          $"{(same ? "<span class=\"ok\">identical</span>" : "<span class=\"bad\">DIFFER</span>")}</td></tr>");
            }
        }
        sb.Append("</table></div></div>");
    }

    private static void AppendSideBySide(StringBuilder sb, SpecimenObservation inherit, SpecimenObservation ownCopy)
    {
        sb.Append("<div class=\"scroll\"><table>");
        sb.Append("<tr><th></th><th style=\"width:39%\">Use the template's appearance</th>" +
                  "<th style=\"width:39%\">Give each NPC its own copy</th></tr>");

        Row("patched (in output token)", o => o.Processed.ToString());
        Row("record in output", o => o.RecordPresent ? o.RecordFormKey + " '" + o.RecordEditorId + "'" : "ABSENT");
        Row("Traits flag", o => o.TraitsFlag?.ToString() ?? "-");
        Row("TPLT target", o => o.TemplateTarget ?? "-");
        Row("appearance", o => o.RecordPresent
            ? $"race={o.RaceEditorId} height={o.Height} weight={o.Weight} female={o.Female} " +
              $"headParts=[{string.Join(",", o.HeadPartEditorIds)}]"
            : "-");
        Row("FaceGen at its own path", o => o.OwnFaceGenRelPath == null
            ? "-"
            : $"{o.OwnFaceGenRelPath}<br>{(o.OwnFaceGenHash == null ? "<span class=\"muted\">none written</span>" : Esc(o.OwnFaceGenHash) + " &larr; " + Esc(o.OwnFaceGenSource ?? ""))}",
            raw: true);
        Row("FaceGen at the terminus's path", o => o.SubjectFaceGenRelPath == null
            ? "<span class=\"muted\">n/a (not templated)</span>"
            : $"{o.SubjectFaceGenRelPath}<br>{(o.SubjectFaceGenHash == null ? "<span class=\"muted\">none written</span>" : Esc(o.SubjectFaceGenHash) + " &larr; " + Esc(o.SubjectFaceGenSource ?? ""))}",
            raw: true);
        Row("ladder decision", o => o.LadderSummary);
        Row("validator", o => o.InvalidReason ?? "accepted");

        sb.Append("</table></div>");
        return;

        void Row(string label, Func<SpecimenObservation, string> get, bool raw = false)
        {
            var a = get(inherit);
            var b = get(ownCopy);
            sb.Append($"<tr class=\"{(a == b ? "" : "diff")}\"><th>{Esc(label)}</th>" +
                      $"<td>{(raw ? a : Esc(a))}</td><td>{(raw ? b : Esc(b))}</td></tr>");
        }
    }

    private static string FaceGenCell(SpecimenObservation o) =>
        o.OwnFaceGenHash == null
            ? "<span class=\"muted\">none written</span><br>" + Esc(o.OwnFaceGenRelPath ?? "(no destination)")
            : Esc(o.OwnFaceGenHash) + "<br>" + Esc(o.OwnFaceGenSource ?? "") + "<br>" +
              "<span class=\"muted\">" + Esc(o.OwnFaceGenRelPath ?? "") + "</span>";

    private static string Esc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
