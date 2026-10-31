using System;
using System.IO;
using System.Security.Cryptography;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Opt-in check that both BSA serving modes hand the renderer the same bytes: with a
/// <c>LogBsaAssetHashes.txt</c> next to the exe, every archive file the adapter extracts or
/// reads is logged to <c>BsaAssetHashes.csv</c> as <c>mode,bsa,subpath,length,sha256</c>, so an
/// extract-mode run and a memory-mode run can be joined on (bsa, subpath). Hashing costs a
/// full read per file, so leave it off for timing runs.
/// </summary>
internal static class BsaAssetHashDiag
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static readonly bool IsEnabled =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "LogBsaAssetHashes.txt"));

    public static void LogFile(string mode, string bsaPath, string subpath, string filePath)
    {
        if (!IsEnabled) return;
        try
        {
            using var stream = File.OpenRead(filePath);
            Write(mode, bsaPath, subpath, stream.Length, SHA256.HashData(stream));
        }
        catch (Exception ex)
        {
            Write(mode, bsaPath, subpath, -1, null, ex.Message);
        }
    }

    public static void LogBytes(string mode, string bsaPath, string subpath, byte[] bytes)
    {
        if (!IsEnabled) return;
        Write(mode, bsaPath, subpath, bytes.LongLength, SHA256.HashData(bytes));
    }

    private static void Write(string mode, string bsaPath, string subpath, long length, byte[]? hash, string? error = null)
    {
        lock (Gate)
        {
            try
            {
                if (_writer == null)
                {
                    _writer = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "BsaAssetHashes.csv"), append: false)
                        { AutoFlush = true };
                    _writer.WriteLine("Mode,Bsa,Subpath,Length,Sha256");
                }
                _writer.WriteLine($"{mode},\"{bsaPath}\",\"{subpath}\",{length},{(hash == null ? "ERROR " + error : Convert.ToHexString(hash))}");
            }
            catch
            {
                // Diagnostic only; never fail an asset read over it.
            }
        }
    }
}
