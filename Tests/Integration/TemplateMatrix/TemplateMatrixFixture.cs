using System.IO;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Authors the synthetic specimen plugins and stands the environment up ONCE for the whole matrix
/// class. Holds a skip reason rather than throwing when no Skyrim SE install is resolvable, so the
/// suite skips gracefully — the existing contract for every integration test here.
/// </summary>
public sealed class TemplateMatrixFixture : IDisposable
{
    internal TemplateFixture? Fixture { get; }
    internal EnvironmentStateProvider? Provider { get; }
    public string SkipReason { get; } = string.Empty;

    /// <summary>Temp root for the authored fixture plugins; deleted on dispose.</summary>
    public string FixtureRoot { get; }

    /// <summary>
    /// Persistent artifact directory next to the test host — <c>TemplateMatrixReport/</c> — holding the
    /// HTML report and each cell's output. Cleared at the start of every run so it only ever contains
    /// the latest one, and deliberately NOT deleted afterwards: a failing run is exactly when these
    /// are wanted.
    /// </summary>
    public string ReportDirectory { get; }

    public string CellsDirectory => Path.Combine(ReportDirectory, "cells");

    public bool Available => Fixture != null && Provider != null;

    public TemplateMatrixFixture()
    {
        FixtureRoot = Path.Combine(Path.GetTempPath(), "NpcTemplateMatrix_" + Guid.NewGuid().ToString("N"));
        ReportDirectory = Path.Combine(AppContext.BaseDirectory, "TemplateMatrixReport");

        try
        {
            Fixture = TemplateFixtureBuilder.Build(Path.Combine(FixtureRoot, "Fixture"));
        }
        catch (Exception ex)
        {
            SkipReason = "Could not author the synthetic fixture plugins: " + ex;
            return;
        }

        var env = TemplateMatrixEnvironment.Build(Fixture, Path.Combine(FixtureRoot, "EnvOutput"));
        if (!env.Available)
        {
            // Leave any previous run's artifacts alone: a run that skips has nothing to replace them with.
            SkipReason = env.SkipReason;
            return;
        }
        Provider = env.Provider;

        // Only now that this run will actually produce artifacts, clear the previous run's.
        try
        {
            if (Directory.Exists(ReportDirectory)) Directory.Delete(ReportDirectory, recursive: true);
        }
        catch { /* best effort — a stale file just gets overwritten */ }
        Directory.CreateDirectory(CellsDirectory);
    }

    internal string CellOutputDirectory(TemplateMatrixCell cell) =>
        Path.Combine(CellsDirectory, cell.FolderName);

    // Each cell costs a full patch run, and several tests need the same cells, so run each once and
    // share the result. Integration tests are serialized by their collection, and the semaphore covers
    // the async path within that.
    private readonly Dictionary<int, CellResult> _cellCache = new();
    private readonly SemaphoreSlim _cellGate = new(1, 1);

    internal async Task<CellResult> CellAsync(TemplateMatrixCell cell)
    {
        await _cellGate.WaitAsync();
        try
        {
            if (_cellCache.TryGetValue(cell.Index, out var cached)) return cached;
            var result = await TemplateMatrixRunner.RunAsync(
                Fixture!, Provider!, cell, CellOutputDirectory(cell));
            _cellCache[cell.Index] = result;
            return result;
        }
        finally
        {
            _cellGate.Release();
        }
    }

    internal async Task<IReadOnlyList<CellResult>> AllCellsAsync()
    {
        var results = new List<CellResult>();
        foreach (var cell in TemplateMatrixCells.All) results.Add(await CellAsync(cell));
        return results;
    }

    public void Dispose()
    {
        // The injected plugins are memory-mapped by the environment for its lifetime, so the fixture
        // plugin files may still be locked here. Everything lives under the system temp dir, so a
        // failed delete leaks a few KB rather than anything that matters.
        try { if (Directory.Exists(FixtureRoot)) Directory.Delete(FixtureRoot, recursive: true); } catch { /* mapped */ }
    }
}
