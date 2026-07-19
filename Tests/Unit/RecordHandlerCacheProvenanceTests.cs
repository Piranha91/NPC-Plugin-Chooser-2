using System.Collections.Concurrent;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Provenance bookkeeping around <see cref="RecordHandler"/>'s ModKey-keyed link-cache
/// trio (_modLinkCaches / _modLinkCachePlugins / _modLinkCacheSourcePaths):
/// EvictIfSourcePathChanged's path-mismatch AND unknown-provenance eviction rules,
/// ClearLinkCachesFor's full teardown, and TryWarmPlugin's refusal to resurrect an
/// entry that was evicted while it was priming.
///
/// Regression coverage for the 2026-07 "Auri wig" bug: 19 mod folders all ship a
/// FoxGloveAuri.esp with per-variant contents, _modLinkCaches keys by ModKey alone,
/// and a cache entry whose source-path record had been lost (torn clear/rebuild
/// interleave) was immune to eviction — every sibling variant's preview silently
/// resolved records against the wrong variant's plugin (outfit had no wig).
///
/// The seams under test only touch the in-memory dictionaries (seeded via Reflect
/// with link caches wrapped around in-memory SkyrimMod instances), so the env and
/// plugin provider are passed as null — no Skyrim install or disk plugins required.
/// </summary>
public class RecordHandlerCacheProvenanceTests
{
    private static readonly ModKey Key = ModKey.FromNameAndExtension("FoxGloveAuri.esp");

    private const string VariantAPath = @"C:\Mods\FoxGlove Variant A\FoxGloveAuri.esp";
    private const string VariantBPath = @"C:\Mods\FoxGlove Variant B\FoxGloveAuri.esp";

    private static RecordHandler MakeHandler() =>
        new RecordHandler(null!, null!, new Settings());

    private static SkyrimMod MakeMod() => new(Key, SkyrimRelease.SkyrimSE);

    private static ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter> MakeCache(ISkyrimModGetter mod) =>
        new(mod, new LinkCachePreferences());

    private static ConcurrentDictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>> Caches(RecordHandler h) =>
        Reflect.GetField<ConcurrentDictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>>>(h, "_modLinkCaches");

    private static ConcurrentDictionary<ModKey, ISkyrimModGetter> Plugins(RecordHandler h) =>
        Reflect.GetField<ConcurrentDictionary<ModKey, ISkyrimModGetter>>(h, "_modLinkCachePlugins");

    private static ConcurrentDictionary<ModKey, string> Paths(RecordHandler h) =>
        Reflect.GetField<ConcurrentDictionary<ModKey, string>>(h, "_modLinkCacheSourcePaths");

    private static ConcurrentDictionary<ModKey, bool> Warmed(RecordHandler h) =>
        Reflect.GetField<ConcurrentDictionary<ModKey, bool>>(h, "_warmedPlugins");

    private static string Normalize(string path) =>
        Reflect.InvokeStatic<RecordHandler, string>("NormalizePath", path)!;

    /// <summary>Seeds a cache entry the way the production factory does: cache + backing
    /// plugin, and (unless simulating a torn clear) the source-path record.</summary>
    private static ISkyrimModGetter Seed(RecordHandler handler, string? sourcePath)
    {
        var mod = MakeMod();
        Caches(handler)[Key] = MakeCache(mod);
        Plugins(handler)[Key] = mod;
        if (sourcePath != null) Paths(handler)[Key] = Normalize(sourcePath);
        return mod;
    }

    private static void Evict(RecordHandler handler, string desiredPath) =>
        Reflect.InvokeVoid(handler, "EvictIfSourcePathChanged", Key, desiredPath);

    // ---------------------------------------------------------------------
    // EvictIfSourcePathChanged
    // ---------------------------------------------------------------------

    [Fact]
    public void Evict_MatchingPath_KeepsEntry()
    {
        var handler = MakeHandler();
        Seed(handler, VariantAPath);
        var originalCache = Caches(handler)[Key];

        // Different casing + separators for the same file: NormalizePath must equate them.
        Evict(handler, VariantAPath.ToLowerInvariant().Replace('\\', '/'));

        Caches(handler).Should().ContainKey(Key);
        Caches(handler)[Key].Should().BeSameAs(originalCache, "a path-matching entry must survive eviction untouched");
        Paths(handler).Should().ContainKey(Key);
    }

    [Fact]
    public void Evict_DifferentPath_EvictsAllBookkeeping()
    {
        var handler = MakeHandler();
        Seed(handler, VariantAPath);
        Warmed(handler)[Key] = true;

        Evict(handler, VariantBPath);

        Caches(handler).Should().NotContainKey(Key);
        Plugins(handler).Should().NotContainKey(Key);
        Paths(handler).Should().NotContainKey(Key);
        Warmed(handler).Should().NotContainKey(Key, "a rebuilt plugin instance must re-run its cold-ESL warmup");
    }

    [Fact]
    public void Evict_NoRecordedSourcePath_EvictsEntry()
    {
        // The "Auri wig" state: content-bearing entry, no path record. Before the
        // hardening this early-returned and the entry was immune to eviction forever.
        var handler = MakeHandler();
        Seed(handler, sourcePath: null);

        Evict(handler, VariantAPath);

        Caches(handler).Should().NotContainKey(Key, "an entry with unknown provenance must not be trusted once the desired file is resolvable");
        Plugins(handler).Should().NotContainKey(Key);
    }

    [Fact]
    public void Evict_NoCacheEntry_IsNoOp()
    {
        var handler = MakeHandler();

        var act = () => Evict(handler, VariantAPath);

        act.Should().NotThrow();
        Caches(handler).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // ClearLinkCachesFor
    // ---------------------------------------------------------------------

    [Fact]
    public void ClearLinkCachesFor_RemovesAllFourMaps()
    {
        var handler = MakeHandler();
        Seed(handler, VariantAPath);
        Warmed(handler)[Key] = true;

        handler.ClearLinkCachesFor(new[] { Key });

        Caches(handler).Should().BeEmpty();
        Plugins(handler).Should().BeEmpty();
        Paths(handler).Should().BeEmpty();
        Warmed(handler).Should().BeEmpty();
    }

    [Fact]
    public void ClearLinkCachesFor_UnknownKey_IsNoOp()
    {
        var handler = MakeHandler();
        var act = () => handler.ClearLinkCachesFor(new[] { Key });
        act.Should().NotThrow();
    }

    // ---------------------------------------------------------------------
    // TryWarmPlugin
    // ---------------------------------------------------------------------

    [Fact]
    public void TryWarmPlugin_EntryStillCurrent_RebuildsCacheAndMemoizes()
    {
        var handler = MakeHandler();
        Seed(handler, VariantAPath);
        var originalCache = Caches(handler)[Key];

        var warmed = Reflect.Invoke<bool>(handler, "TryWarmPlugin", Key);

        warmed.Should().BeTrue();
        Caches(handler)[Key].Should().NotBeSameAs(originalCache, "the cold cache is replaced by a fresh one around the primed plugin");
        Warmed(handler).Should().ContainKey(Key);
        Warmed(handler)[Key].Should().BeTrue();
    }

    [Fact]
    public void TryWarmPlugin_EntryEvictedBeforeRebuild_DoesNotResurrectIt()
    {
        // Stash holds a plugin but the cache entry is gone — the state seen when an
        // eviction lands while a warmup is in flight. Installing a cache around the
        // orphaned instance would recreate a content-bearing entry with no path
        // record (the un-evictable poison state), so the rebuild must be discarded.
        var handler = MakeHandler();
        Plugins(handler)[Key] = MakeMod();

        var warmed = Reflect.Invoke<bool>(handler, "TryWarmPlugin", Key);

        warmed.Should().BeFalse();
        Caches(handler).Should().NotContainKey(Key, "a warmup must never re-create an evicted cache entry");
        Warmed(handler).Should().NotContainKey(Key, "whatever replaces the entry must warm itself on demand");
    }

    [Fact]
    public void TryWarmPlugin_MemoizedOutcome_ShortCircuits()
    {
        var handler = MakeHandler();
        var mod = Seed(handler, VariantAPath);
        Warmed(handler)[Key] = false;
        var originalCache = Caches(handler)[Key];

        var warmed = Reflect.Invoke<bool>(handler, "TryWarmPlugin", Key);

        warmed.Should().BeFalse();
        Caches(handler)[Key].Should().BeSameAs(originalCache, "a memoized outcome must not rebuild anything");
        Plugins(handler)[Key].Should().BeSameAs(mod);
    }

    [Fact]
    public void TryWarmPlugin_NoPluginAnywhere_AbortsWithoutMemoizing()
    {
        var handler = MakeHandler();

        var warmed = Reflect.Invoke<bool>(handler, "TryWarmPlugin", Key);

        warmed.Should().BeFalse();
        Warmed(handler).Should().NotContainKey(Key, "an aborted warmup (no plugin reference) is not an outcome worth memoizing");
    }
}
