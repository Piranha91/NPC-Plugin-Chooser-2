using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Diagnostic batch runner that walks every enabled appearance mod with a
/// non-empty mod-folder list, picks one NPC per mod (the alphabetically-first
/// FormKey it overrides), resolves and parses the NIFs that
/// <see cref="NpcMeshResolver"/> would, and writes per-shape metadata to a
/// CSV so heuristic decisions (primary-head election, biped-slot filter,
/// FaceGen-NIF detection) can be validated empirically across the full mod
/// library instead of one-NPC-at-a-time.
///
/// <para>No GL pipeline involvement — purely NIF parse + metadata extraction
/// via <see cref="NifMeshBuilder.SurveyNif"/>. Cancellable; on cancel the
/// partial CSV is left on disk with an "(aborted)" marker line so the user
/// can still inspect what completed.</para>
/// </summary>
public sealed class MeshSurveyRunner
{
    private readonly Settings _settings;
    private readonly NpcMeshResolver _resolver;
    private readonly NifMeshBuilder _meshBuilder;
    private readonly GameAssetResolver _assetResolver;
    private readonly IBsaArchiveProvider _bsa;
    private readonly EnvironmentStateProvider _env;

    public MeshSurveyRunner(
        Settings settings,
        NpcMeshResolver resolver,
        // NifMeshBuilder isn't registered as a top-level service in NPC2's DI —
        // CharacterPreviewCache constructs it internally and exposes it via
        // its MeshBuilder property. Reach in here so the survey shares the
        // same parser instance (and its NIF parse cache) with the rest of
        // the rendering pipeline.
        CharacterPreviewCache previewCache,
        GameAssetResolver assetResolver,
        IBsaArchiveProvider bsa,
        EnvironmentStateProvider env)
    {
        _settings = settings;
        _resolver = resolver;
        _meshBuilder = previewCache.MeshBuilder;
        _assetResolver = assetResolver;
        _bsa = bsa;
        _env = env;
    }

    /// <summary>Single progress notification dispatched to the UI between mods.</summary>
    public sealed record ProgressInfo(int Completed, int Total, string CurrentLabel);

    /// <summary>Runs the survey. Returns the CSV path on success, or null when
    /// no eligible mods were found / the link cache is unavailable.</summary>
    public async Task<string?> RunAsync(IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return null;

        // Eligible mods: non-empty mod folders, has at least one NPC FormKey to
        // sample. Order alphabetically so the CSV is reproducible run-to-run.
        var mods = _settings.ModSettings
            .Where(m => m.CorrespondingFolderPaths != null && m.CorrespondingFolderPaths.Count > 0)
            .Where(m => m.NpcFormKeys != null && m.NpcFormKeys.Count > 0)
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mods.Count == 0) return null;

        // Pre-warm BSA index off the UI thread; opening hundreds of archives
        // can take several seconds.
        await Task.Run(() => _bsa.EnsureAllArchivesOpened(), ct).ConfigureAwait(false);

        string folder = Path.Combine(AppContext.BaseDirectory, "RenderLogs");
        Directory.CreateDirectory(folder);
        string outputPath = Path.Combine(folder,
            $"MeshSurvey_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        bool aborted = false;
        await using (var writer = new StreamWriter(outputPath, append: false) { AutoFlush = false })
        {
            await writer.WriteLineAsync("# NPC2 Mesh Survey — one NPC per enabled appearance mod with non-empty mod folders").ConfigureAwait(false);
            await writer.WriteLineAsync("# generated " + DateTime.Now.ToString("o")).ConfigureAwait(false);
            await writer.WriteLineAsync("mod,npc_formkey,npc_name,body_part_loaded,nif_disk_path,has_facegen_node,shape_name,has_dismember,partitions,would_pass_biped_filter,would_be_primary_head,shader_type,baked_diffuse,vertex_count,tri_count,local_z_height,flags_hex,note").ConfigureAwait(false);

            try
            {
                for (int i = 0; i < mods.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var mod = mods[i];
                    var npcKey = mod.NpcFormKeys
                        .OrderBy(fk => fk.ToString(), StringComparer.OrdinalIgnoreCase)
                        .First();
                    string npcName = mod.NpcFormKeysToDisplayName.TryGetValue(npcKey, out var n)
                        ? n
                        : npcKey.ToString();

                    progress.Report(new ProgressInfo(i, mods.Count,
                        $"{mod.DisplayName} → {npcName}"));

                    await SurveyNpcAsync(writer, mod, npcKey, npcName, ct).ConfigureAwait(false);

                    // Flush after each mod so a mid-run cancel preserves
                    // everything completed so far.
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                aborted = true;
                await writer.WriteLineAsync($"# (aborted at {DateTime.Now:o})").ConfigureAwait(false);
            }
            finally
            {
                _assetResolver.SetAdditionalScopes(null);
                _assetResolver.SetAdditionalFolders(null);
            }
        }

        progress.Report(new ProgressInfo(
            aborted ? 0 : mods.Count, mods.Count,
            aborted ? "Aborted." : "Done."));
        return outputPath;
    }

    private async Task SurveyNpcAsync(StreamWriter writer, ModSetting mod,
        FormKey npcKey, string npcName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ResolvedNpcMeshPaths? paths;
        try
        {
            // Build mod-scoped resolution context the same way mugshot tiles
            // do (NpcMeshResolver.Resolve internally calls BuildContext).
            paths = _resolver.Resolve(npcKey, mod);
        }
        catch (Exception ex)
        {
            await WriteErrorRowAsync(writer, mod, npcKey, npcName,
                $"resolve threw: {ex.Message}").ConfigureAwait(false);
            return;
        }

        if (paths == null)
        {
            await WriteErrorRowAsync(writer, mod, npcKey, npcName,
                "resolve returned null (no link cache or no record)").ConfigureAwait(false);
            return;
        }

        // Push the same scope chain the mugshot pipeline would use so disk
        // resolution honors the mod's CorrespondingFolderPaths. Cleared in
        // RunAsync's finally block.
        var scopes = _resolver.BuildResolutionScopes(mod);
        _assetResolver.SetAdditionalScopes(scopes);

        await SurveyMeshPartAsync(writer, mod, npcKey, npcName, "Body", paths.BodyMeshPath, ct).ConfigureAwait(false);
        await SurveyMeshPartAsync(writer, mod, npcKey, npcName, "Hands", paths.HandsMeshPath, ct).ConfigureAwait(false);
        await SurveyMeshPartAsync(writer, mod, npcKey, npcName, "Feet", paths.FeetMeshPath, ct).ConfigureAwait(false);
        await SurveyMeshPartAsync(writer, mod, npcKey, npcName, "Head", paths.HeadMeshPath, ct).ConfigureAwait(false);
        await SurveyMeshPartAsync(writer, mod, npcKey, npcName, "Hair", paths.HairMeshPath, ct).ConfigureAwait(false);
        await SurveyMeshPartAsync(writer, mod, npcKey, npcName, "Tail", paths.TailMeshPath, ct).ConfigureAwait(false);
    }

    private async Task SurveyMeshPartAsync(StreamWriter writer, ModSetting mod,
        FormKey npcKey, string npcName, string bodyPart, string? gamePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(gamePath)) return; // body part not present (e.g. no hair / tail)

        string? diskPath;
        try
        {
            var src = _assetResolver.ResolveAssetSource(gamePath);
            diskPath = src.ResolvedDiskPath;
        }
        catch (Exception ex)
        {
            await WriteErrorRowAsync(writer, mod, npcKey, npcName,
                $"{bodyPart} resolve threw: {ex.Message}", bodyPart).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(diskPath))
        {
            await WriteErrorRowAsync(writer, mod, npcKey, npcName,
                $"{bodyPart} unresolved: {gamePath}", bodyPart).ConfigureAwait(false);
            return;
        }

        // Run the parse on a thread-pool thread so the dialog stays
        // responsive during BSA-extracted NIF reads.
        NifMeshBuilder.NifSurveyResult result;
        try
        {
            result = await Task.Run(() => _meshBuilder.SurveyNif(diskPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteErrorRowAsync(writer, mod, npcKey, npcName,
                $"{bodyPart} survey threw: {ex.Message}", bodyPart, diskPath).ConfigureAwait(false);
            return;
        }

        if (!result.LoadOk)
        {
            await WriteErrorRowAsync(writer, mod, npcKey, npcName,
                $"{bodyPart} load failed: {result.Error}", bodyPart, diskPath).ConfigureAwait(false);
            return;
        }

        ushort? expectedSlot = NifMeshBuilder.GetExpectedBipedSlot(bodyPart);
        foreach (var shape in result.Shapes)
        {
            ct.ThrowIfCancellationRequested();
            bool wouldPass = !expectedSlot.HasValue
                || !shape.HasDismember
                || shape.Partitions.Count == 0
                || shape.Partitions.Contains(expectedSlot.Value);

            await writer.WriteLineAsync(string.Join(",", new[]
            {
                Csv(mod.DisplayName),
                Csv(npcKey.ToString()),
                Csv(npcName),
                Csv(bodyPart),
                Csv(diskPath),
                result.HasFaceGenNode ? "1" : "0",
                Csv(shape.ShapeName),
                shape.HasDismember ? "1" : "0",
                Csv(string.Join(";", shape.Partitions)),
                wouldPass ? "1" : "0",
                shape.WouldBePrimaryHead ? "1" : "0",
                shape.ShaderType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(shape.BakedDiffusePath ?? ""),
                shape.VertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                shape.TriangleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                shape.LocalZHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                "0x" + shape.Flags.ToString("X8"),
                "",
            })).ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorRowAsync(StreamWriter writer, ModSetting mod,
        FormKey npcKey, string npcName, string note,
        string? bodyPart = null, string? diskPath = null)
    {
        await writer.WriteLineAsync(string.Join(",", new[]
        {
            Csv(mod.DisplayName),
            Csv(npcKey.ToString()),
            Csv(npcName),
            Csv(bodyPart ?? ""),
            Csv(diskPath ?? ""),
            "", "", "", "", "", "", "", "", "", "", "", "",
            Csv(note),
        })).ConfigureAwait(false);
    }

    /// <summary>RFC 4180 minimal CSV escaping: quote when the value contains
    /// a comma, double-quote, or newline; double interior quotes.</summary>
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needsQuoting = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ',' || c == '"' || c == '\n' || c == '\r') { needsQuoting = true; break; }
        }
        if (!needsQuoting) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
