using System;
using System.Globalization;
using System.Threading;
using SkyRender;
using SkyRender.Character;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// The DDS upload policy this process's <c>CharacterPreviewCache</c> renders with, resolved once at
/// startup by <see cref="Apply"/> before the container builds the cache. The library reads
/// <c>CVR_DDS_MAX_FINAL_MIP_DIMENSION</c> in the cache constructor; precedence is that variable when
/// already set, then the <c>DdsMaxFinalMip.txt</c> dev trigger (App sets the variable from it
/// earlier), then <see cref="Models.InternalMugshotSettings.UseCompressedTextures"/>. Unset means
/// the library default (authored-mip compressed uploads). <see cref="StampValue"/> is folded into
/// the schema-v20 mugshot settings hash, so tiles re-stale only when the effective policy changes.
/// </summary>
public static class TextureUploadPolicy
{
    /// <summary>Null = decoded; otherwise the final-mip limit. Mirrors the cache's own parse,
    /// including its invalid-value fallback to decoded.</summary>
    public static int? MaxFinalMipDimension { get; private set; } = DdsMipPolicy.DefaultMaximumFinalMipDimension;

    public static string StampValue => StampFor(MaxFinalMipDimension);

    public static string StampFor(int? maxFinalMipDimension) => maxFinalMipDimension switch
    {
        null => "decoded",
        int.MaxValue => "unlimited",
        int v => v.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The variable value the cache should see: an already-set value wins; otherwise
    /// "decoded" when the setting is off, or null (library default) when it is on.</summary>
    public static string? EffectiveEnvironmentValue(string? current, bool useCompressedTextures)
        => !string.IsNullOrWhiteSpace(current) ? current : useCompressedTextures ? null : "decoded";

    /// <summary>The cache constructor's parse, including its invalid-value fallback to decoded.</summary>
    public static int? ParseAsCacheDoes(string? value)
    {
        try { return DdsMipPolicy.Parse(value); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Call once, after settings load and before the cache is constructed.</summary>
    public static void Apply(bool useCompressedTextures)
    {
        string? current = Environment.GetEnvironmentVariable(DdsMipPolicy.EnvironmentVariable);
        string? value = EffectiveEnvironmentValue(current, useCompressedTextures);
        if (value != current)
            Environment.SetEnvironmentVariable(DdsMipPolicy.EnvironmentVariable, value);
        MaxFinalMipDimension = ParseAsCacheDoes(value);
    }
}

/// <summary>
/// Dev-only A/B escape hatch for the render harness (see
/// <see cref="RenderHarnessRunner"/>'s click-simulation mode): when
/// <see cref="ForceLegacy"/> is set, mugshot renders run the pre-2.8.0
/// resolution algorithm — no engine-order mode (the request-level
/// <c>AllowLoadOrderFallback</c> stays off), wig/worn-headgear overrides are
/// not fallback-stamped, and the broadcast tier's lazy load-order index widen
/// is suppressed — so the harness can measure the old and new algorithms in
/// otherwise-identical processes.
///
/// <para>Never set outside the harness. Normal renders must stay on
/// engine-order resolution: legacy mode reintroduces the out-of-scope-BSA
/// misses (hair/skin textures in another mod's archive) the 2.8.0 work
/// exists to fix.</para>
/// </summary>
public static class RenderResolutionMode
{
    /// <summary>True = render with the pre-2.8.0 strict resolution. Volatile:
    /// flipped by the harness between phases while render workers run.</summary>
    public static volatile bool ForceLegacy;
}

/// <summary>
/// Lightweight counters around the broadcast archive tier
/// (<see cref="Adapters.NpcChooserBsaProviderAdapter.TryLocateInBsa"/>), the
/// prime suspect for render-time regressions: every engine-order miss lands
/// there, and its per-call cost scales with the index size. Collected always
/// (a few Interlocked ops per lookup — noise next to the disk/GL work) and
/// reported by the render harness per phase.
/// </summary>
public static class BroadcastLookupStats
{
    private static long _calls;
    private static long _hits;
    private static long _ticks;
    private static long _widenMs;

    public static void RecordLookup(long elapsedTicks, bool hit)
    {
        Interlocked.Increment(ref _calls);
        if (hit) Interlocked.Increment(ref _hits);
        Interlocked.Add(ref _ticks, elapsedTicks);
    }

    public static void RecordWiden(long elapsedMs) => Interlocked.Add(ref _widenMs, elapsedMs);

    public static void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _ticks, 0);
        Interlocked.Exchange(ref _widenMs, 0);
    }

    public static string Report()
    {
        long calls = Interlocked.Read(ref _calls);
        long hits = Interlocked.Read(ref _hits);
        long ticks = Interlocked.Read(ref _ticks);
        long widenMs = Interlocked.Read(ref _widenMs);
        double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"broadcast lookups={calls} (hits={hits}, misses={calls - hits}) totalMs={ms:F1} widenMs={widenMs}";
    }
}
