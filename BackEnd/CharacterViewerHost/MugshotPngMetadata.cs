using System;
using System.Diagnostics;
using System.IO;
using Hjg.Pngcs;
using Hjg.Pngcs.Chunks;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Reads and writes the "Parameters" tEXt chunk used by both the legacy NPC
/// Portrait Creator subprocess and the in-process Internal renderer to mark
/// mugshots as auto-generated and carry the metadata needed for staleness
/// detection. The legacy executable writes this chunk directly; the Internal
/// renderer writes it through <see cref="InjectParameters"/> after the
/// offscreen renderer's PNG output is on disk.
/// </summary>
public static class MugshotPngMetadata
{
    public const string ParametersKey = "Parameters";

    /// <summary>Reads the "Parameters" text chunk from a PNG, or returns null
    /// if absent / the file is unreadable.</summary>
    public static string? TryRead(string pngPath)
    {
        if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath)) return null;
        var sw = Stopwatch.StartNew();
        int tid = Environment.CurrentManagedThreadId;
        string label = Path.GetFileName(pngPath);
        try
        {
            using var fs = File.OpenRead(pngPath);
            var pngr = new PngReader(fs);
            pngr.ChunkLoadBehaviour = ChunkLoadBehaviour.LOAD_CHUNK_ALWAYS;
            pngr.ReadSkippingAllRows();
            string? value = pngr.GetMetadata()?.GetTxtForKey(ParametersKey);
            pngr.End();
            var elapsed = sw.ElapsedMilliseconds;
            // Only log slow reads to avoid drowning the trace stream — anything
            // over ~10ms on the UI thread is enough to start adding visible jitter.
            if (elapsed > 10)
            {
                Trace($"TryRead tid={tid} png={label} elapsed={elapsed}ms hasParams={value != null}");
            }
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            Trace($"TryRead tid={tid} png={label} FAILED elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
            return null;
        }
    }

    private static void Trace(string message)
    {
        Debug.WriteLine($"[MugshotPngMetadata] {message}");
        System.Diagnostics.Trace.WriteLine($"[MugshotPngMetadata] {message}");
    }

    /// <summary>
    /// Rewrites the PNG at <paramref name="pngPath"/> so the "Parameters"
    /// tEXt chunk holds <paramref name="parametersJson"/>. Pixels are preserved;
    /// optional ancillary chunks (pHYs, gAMA, etc.) are dropped — the offscreen
    /// renderer doesn't write any of them so there's nothing meaningful to
    /// carry forward.
    /// </summary>
    public static void InjectParameters(string pngPath, string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
        {
            throw new FileNotFoundException("PNG to inject metadata into not found.", pngPath);
        }

        ImageInfo imgInfo;
        int[][] scanlines;
        using (var fsRead = File.OpenRead(pngPath))
        {
            var pngr = new PngReader(fsRead);
            imgInfo = pngr.ImgInfo;
            scanlines = new int[imgInfo.Rows][];
            for (int row = 0; row < imgInfo.Rows; row++)
            {
                var src = pngr.ReadRowInt(row);
                var copy = new int[src.Scanline.Length];
                Array.Copy(src.Scanline, copy, src.Scanline.Length);
                scanlines[row] = copy;
            }
            pngr.End();
        }

        string tempPath = pngPath + ".tmp";
        try
        {
            using (var fsWrite = File.Create(tempPath))
            {
                var pngw = new PngWriter(fsWrite, imgInfo);
                pngw.GetMetadata().SetText(ParametersKey, parametersJson);
                var line = new ImageLine(imgInfo);
                for (int row = 0; row < imgInfo.Rows; row++)
                {
                    Array.Copy(scanlines[row], line.Scanline, scanlines[row].Length);
                    pngw.WriteRow(line, row);
                }
                pngw.End();
            }
            File.Delete(pngPath);
            File.Move(tempPath, pngPath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }
}
