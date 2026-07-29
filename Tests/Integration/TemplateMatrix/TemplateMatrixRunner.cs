using System.IO;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// What one matrix cell observed about one specimen. Every field here is a MEASUREMENT read back off
/// disk or out of the ladder diagnostic — no expectations are encoded. The tests decide what is
/// correct; the report renders these verbatim.
/// </summary>
internal sealed class SpecimenObservation
{
    public required string Role { get; init; }
    public required FormKey TargetFormKey { get; init; }
    public required string EditorId { get; init; }

    /// <summary>Present in <c>GoldenPatchResult.PatchedTargets</c> — i.e. it passed SCREENING.
    /// Deliberately not used as the presence gate: it is computed before the patch loop runs, so an
    /// NPC the ladder later aborts still appears here. See <see cref="Processed"/>.</summary>
    public bool ScreenedValid { get; set; }

    /// <summary>Listed in the run's own <c>NPC_Token.json</c> — the authoritative "this NPC was
    /// actually patched" oracle, and the gate every other assertion hangs off.</summary>
    public bool Processed { get; set; }

    /// <summary>The validator's rejection text, when it rejected this selection.</summary>
    public string? InvalidReason { get; set; }

    // ---- the output record that carries this specimen's appearance ----
    /// <summary>Record mode: the specimen's own override. SkyPatcher: its surrogate.</summary>
    public bool RecordPresent { get; set; }
    public string? RecordFormKey { get; set; }
    public string? RecordEditorId { get; set; }
    public bool? TraitsFlag { get; set; }
    public string? TemplateTarget { get; set; }
    public float? Height { get; set; }
    public ushort? Weight { get; set; }
    public bool? Female { get; set; }
    public string? RaceEditorId { get; set; }
    public IReadOnlyList<string> HeadPartEditorIds { get; set; } = Array.Empty<string>();

    /// <summary>SkyPatcher only: the surrogate FormKey the .ini's copyVisualStyle points at.</summary>
    public FormKey? SurrogateFormKey { get; set; }
    /// <summary>SkyPatcher only: whether the .ini carries a line for this target at all.</summary>
    public bool HasIniLine { get; set; }

    // ---- FaceGen on disk ----
    /// <summary>The path the engine reads for THIS NPC: its own FormID path (record modes) or its
    /// surrogate's (SkyPatcher).</summary>
    public string? OwnFaceGenRelPath { get; set; }
    public string? OwnFaceGenHash { get; set; }
    /// <summary>Which fixture mod's bytes those are, when recognisable.</summary>
    public string? OwnFaceGenSource { get; set; }

    /// <summary>The subject (template terminus) path — where an inheriting NPC's face actually lands.</summary>
    public string? SubjectFaceGenRelPath { get; set; }
    public string? SubjectFaceGenHash { get; set; }
    public string? SubjectFaceGenSource { get; set; }

    // ---- the ladder's decision (classifier), beside the disk result (writer) ----
    public FaceGenLadderDecision? Ladder { get; set; }

    public string LadderSummary => Ladder == null
        ? "(no decision recorded)"
        : $"{Ladder.Inputs.ChainStatus} / flatten={Ladder.Inputs.FlattenTemplateChain} / " +
          $"subject={Ladder.Inputs.SubjectFormKey} / nif={Ladder.NifChoice} / dds={Ladder.DdsChoice} / " +
          $"abort={Ladder.Abort}";

    /// <summary>The appearance fields as one comparable string — the §3a "identical across both
    /// template settings" control compares exactly this.</summary>
    public string AppearanceSignature =>
        $"present={RecordPresent}; traits={TraitsFlag}; tplt={TemplateTarget}; race={RaceEditorId}; " +
        $"height={Height}; weight={Weight}; female={Female}; " +
        $"headParts=[{string.Join(",", HeadPartEditorIds)}]";
}

/// <summary>Everything one cell's run produced.</summary>
internal sealed class CellResult
{
    public required TemplateMatrixCell Cell { get; init; }
    public required string OutputDirectory { get; init; }
    public required string Log { get; init; }
    public required IReadOnlyList<string> InvalidSelections { get; init; }
    public required IReadOnlyDictionary<string, SpecimenObservation> ByRole { get; init; }
    /// <summary>Every FaceGen file the run wrote, relative path -&gt; short content hash. Lets the report
    /// show a file that landed somewhere unexpected instead of only reporting the ones asked about.</summary>
    public required IReadOnlyDictionary<string, string> FaceGenFiles { get; init; }

    /// <summary>EditorIDs of every NPC record in the output plugin. Lets an assertion ask about a
    /// record that is not a specimen — specifically #9's terminus, which must stay out of the output
    /// while its FaceGen path stays empty. The two together are the defect; either alone is not.</summary>
    public required IReadOnlySet<string> OutputNpcEditorIds { get; init; }

    public SpecimenObservation this[string role] => ByRole[role];
}

internal static class TemplateMatrixRunner
{
    /// <summary>
    /// Runs one cell end to end and reads the result back. Does not assert — measurement only.
    /// </summary>
    public static async Task<CellResult> RunAsync(TemplateFixture fixture, EnvironmentStateProvider provider,
        TemplateMatrixCell cell, string outputDirectory,
        IReadOnlyDictionary<string, TemplateHandlingMode>? perModTemplateOverride = null)
    {
        Directory.CreateDirectory(outputDirectory);

        var settings = TemplateMatrixSettingsBuilder.Build(fixture, cell, outputDirectory, perModTemplateOverride);

        // Process-global; reset per run or decisions from the previous cell leak into this one.
        FaceGenLadderDiag.Reset();
        FaceGenLadderDiag.SetEnabled(true);

        GoldenPatchResult run;
        try
        {
            run = await GoldenPatchRunner.RunAsync(provider, settings);
        }
        finally
        {
            // Sticky if LogFaceGenLadder.txt sits next to the test host; harmless either way.
            FaceGenLadderDiag.SetEnabled(false);
        }

        var ladderByTarget = FaceGenLadderDiag.Decisions
            .GroupBy(d => d.Inputs.TargetFormKey)
            .ToDictionary(g => g.Key, g => g.First());

        var processed = ReferenceToken.ProcessedTargets(outputDirectory);
        var screened = run.PatchedTargets.ToHashSet();
        var faceGen = EnumerateFaceGen(outputDirectory);

        var outputPluginPath = Path.Combine(outputDirectory, "NPC.esp");
        Dictionary<FormKey, FormKey> surrogateByTarget = new();
        if (cell.UseSkyPatcher)
        {
            var iniPath = Path.Combine(outputDirectory, SkyPatcherIniComparer.DefaultIniRelativePath);
            if (File.Exists(iniPath)) surrogateByTarget = SkyPatcherIniComparer.SurrogateByTarget(iniPath);
        }

        var byRole = new Dictionary<string, SpecimenObservation>(StringComparer.Ordinal);
        var outputNpcEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The overlay holds a memory map for the duration of this block only.
        using (var outHandle = File.Exists(outputPluginPath)
                   ? SkyrimMod.CreateFromBinaryOverlay(outputPluginPath, SkyrimRelease.SkyrimSE)
                   : null)
        {
            ISkyrimModGetter? outMod = outHandle;
            var npcsByFormKey = outMod?.Npcs.ToDictionary(n => n.FormKey) ?? new Dictionary<FormKey, INpcGetter>();

            foreach (var npc in npcsByFormKey.Values)
            {
                if (!string.IsNullOrEmpty(npc.EditorID)) outputNpcEditorIds.Add(npc.EditorID!);
            }

            foreach (var role in TemplateMatrixSettingsBuilder.SpecimenRoles)
            {
                var target = fixture.Npc(role);
                var obs = new SpecimenObservation
                {
                    Role = role,
                    TargetFormKey = target,
                    EditorId = fixture.EditorId(role),
                    ScreenedValid = screened.Contains(target),
                    Processed = processed.Contains(target),
                    // The validator names an NPC by Auxilliary.GetLogString, which prefers the record's
                    // NAME and falls back to its EditorID — it does not print the FormKey — so match on
                    // all three rather than on the FormKey alone.
                    InvalidReason = FindInvalidReason(run.InvalidSelections, target, fixture.EditorId(role), role),
                };

                ladderByTarget.TryGetValue(target, out var decision);
                obs.Ladder = decision;

                // Which output record represents this specimen, and which FaceGen path the engine reads.
                FormKey recordKey;
                if (cell.UseSkyPatcher)
                {
                    obs.HasIniLine = surrogateByTarget.ContainsKey(target);
                    if (surrogateByTarget.TryGetValue(target, out var surrogate))
                    {
                        obs.SurrogateFormKey = surrogate;
                        recordKey = surrogate;
                    }
                    else
                    {
                        recordKey = FormKey.Null;
                    }
                }
                else
                {
                    recordKey = target;
                }

                if (!recordKey.IsNull && npcsByFormKey.TryGetValue(recordKey, out var rec))
                {
                    Populate(obs, rec, provider.LinkCache!);
                }

                // FaceGen destinations.
                if (!recordKey.IsNull)
                {
                    var (meshRel, _) = Auxilliary.GetFaceGenSubPathStrings(recordKey, regularized: true);
                    obs.OwnFaceGenRelPath = meshRel;
                    if (faceGen.TryGetValue(Normalize(meshRel), out var hash))
                    {
                        obs.OwnFaceGenHash = hash;
                        obs.OwnFaceGenSource = IdentifySource(fixture, outputDirectory, Normalize(meshRel));
                    }
                }

                var subject = decision?.Inputs.SubjectFormKey;
                if (subject.HasValue && !subject.Value.IsNull && subject.Value != recordKey)
                {
                    var (subjMesh, _) = Auxilliary.GetFaceGenSubPathStrings(subject.Value, regularized: true);
                    obs.SubjectFaceGenRelPath = subjMesh;
                    if (faceGen.TryGetValue(Normalize(subjMesh), out var hash))
                    {
                        obs.SubjectFaceGenHash = hash;
                        obs.SubjectFaceGenSource = IdentifySource(fixture, outputDirectory, Normalize(subjMesh));
                    }
                }

                byRole[role] = obs;
            }
        }

        return new CellResult
        {
            Cell = cell,
            OutputDirectory = outputDirectory,
            Log = run.Log,
            InvalidSelections = run.InvalidSelections,
            ByRole = byRole,
            FaceGenFiles = faceGen,
            OutputNpcEditorIds = outputNpcEditorIds,
        };
    }

    /// <summary>
    /// The validator's rejection line for a specimen, if it has one. Lines read
    /// <c>&lt;identifier&gt; -&gt; '&lt;mod&gt;' (&lt;donor identifier&gt;) - (&lt;reason&gt;)</c>, so the TARGET's identifier
    /// is the prefix — anchoring on that stops a donor's name in the parenthetical from matching the
    /// wrong specimen.
    /// </summary>
    private static string? FindInvalidReason(IReadOnlyList<string> invalidSelections, FormKey target,
        string editorId, string role) =>
        invalidSelections.FirstOrDefault(s =>
            s.StartsWith(role, StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(editorId, StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(target.ToString(), StringComparison.OrdinalIgnoreCase));

    private static void Populate(SpecimenObservation obs, INpcGetter rec, ILinkCache linkCache)
    {
        obs.RecordPresent = true;
        obs.RecordFormKey = rec.FormKey.ToString();
        obs.RecordEditorId = rec.EditorID;
        obs.TraitsFlag = rec.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
        obs.TemplateTarget = rec.Template.IsNull ? null : Describe(rec.Template.FormKey, linkCache);
        obs.Height = rec.Height;
        obs.Weight = (ushort)rec.Weight;
        obs.Female = rec.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        obs.RaceEditorId = Describe(rec.Race.FormKey, linkCache);
        // By resolved EditorID, never FormKey: the patcher remaps FormKeys when merging dependencies.
        obs.HeadPartEditorIds = rec.HeadParts
            .Select(hp => Describe(hp.FormKey, linkCache))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Describe(FormKey formKey, ILinkCache linkCache)
    {
        if (formKey.IsNull) return "(null)";
        if (linkCache.TryResolve<ISkyrimMajorRecordGetter>(formKey, out var rec) && !string.IsNullOrEmpty(rec.EditorID))
        {
            return rec.EditorID!;
        }
        return formKey.ToString();
    }

    /// <summary>Relative path -&gt; short content hash for every FaceGen file the run wrote.</summary>
    private static Dictionary<string, string> EnumerateFaceGen(string outputDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { "meshes", "textures" })
        {
            var dir = Path.Combine(outputDirectory, root);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Normalize(Path.GetRelativePath(outputDirectory, file));
                if (!rel.Contains(@"facegendata\", StringComparison.OrdinalIgnoreCase)) continue;
                map[rel] = ShortHash(file);
            }
        }
        return map;
    }

    /// <summary>Names the fixture mod whose placeholder bytes are sitting at a path, when they match one.</summary>
    private static string? IdentifySource(TemplateFixture fixture, string outputDirectory, string relPath)
    {
        var full = Path.Combine(outputDirectory, relPath);
        if (!File.Exists(full)) return null;
        string content;
        try { content = File.ReadAllText(full); } catch { return null; }

        foreach (var mod in fixture.Mods)
        {
            foreach (var role in TemplateFixtureBuilder.FaceGenSubjectsByMod[mod.DisplayName])
            {
                foreach (var half in new[] { "nif", "dds" })
                {
                    if (content == TemplateFixtureBuilder.FaceGenContent(mod.DisplayName, role, half))
                    {
                        return $"{mod.DisplayName} ({role})";
                    }
                }
            }
        }
        return "(unrecognised bytes)";
    }

    public static string ShortHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).Substring(0, 12).ToLowerInvariant();
    }

    private static string Normalize(string relPath) => relPath.Replace('/', '\\').TrimStart('\\');
}
