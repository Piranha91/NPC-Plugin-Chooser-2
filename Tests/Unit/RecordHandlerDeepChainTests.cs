using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The dependency walks in <c>RecordHandler</c> recurse once per link, and reset their depth limit
/// every time a link resolves inside the selected mod. A mod that overrides a long, connected chain
/// of its master's records — Falskaar under Include As New, reported as a 0xC00000FD stack overflow in
/// coreclr.dll — therefore recursed as deep as the chain is long, with nothing to stop it. A stack
/// overflow cannot be caught in .NET; it kills the process with no log.
///
/// <para>Each walk runs on a thread with a deliberately small 1 MB stack, the size of a default
/// thread-pool thread, over a chain far deeper than 1 MB of frames can hold. Before the fix this
/// test crashed the test host; it now has to complete.</para>
/// </summary>
public class RecordHandlerDeepChainTests
{
    private static readonly ModKey BaseKey = ModKey.FromFileName("Base.esm");
    private static readonly ModKey ModKeyEsp = ModKey.FromFileName("Appearance.esp");
    private static readonly ModKey OutputKey = ModKey.FromFileName("NPC.esp");

    private const int ChainLength = 20_000;
    private const int SmallStack = 1024 * 1024;

    private sealed record Fixture(RecordHandler Handler, SkyrimMod OutputMod, FormList Head);

    /// <summary>
    /// Base.esm holds FormList[0] → FormList[1] → … → FormList[N-1]; Appearance.esp overrides every
    /// one of them, so every hop resolves in the selected mod and resets the depth limit.
    /// </summary>
    private static Fixture Build()
    {
        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        var appearanceMod = new SkyrimMod(ModKeyEsp, SkyrimRelease.SkyrimSE);
        var outputMod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);

        var lists = new List<FormList>(ChainLength);
        for (int i = 0; i < ChainLength; i++) lists.Add(baseMod.FormLists.AddNew());
        for (int i = 0; i < ChainLength - 1; i++) lists[i].Items.Add(lists[i + 1]);
        foreach (var list in lists)
        {
            var ovr = appearanceMod.FormLists.GetOrAddAsOverride(list);
            ovr.EditorID = "Overridden";
        }

        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "OutputMod", outputMod);
        var handler = new RecordHandler(env, null!, new Settings());

        var caches = Reflect.GetField<
            System.Collections.Concurrent.ConcurrentDictionary<
                ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>>>(handler, "_modLinkCaches");
        caches[BaseKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(baseMod, new LinkCachePreferences());
        caches[ModKeyEsp] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(appearanceMod, new LinkCachePreferences());

        return new Fixture(handler, outputMod, lists[0]);
    }

    /// <summary>Runs <paramref name="body"/> on a thread with <see cref="SmallStack"/>.</summary>
    private static void RunOnSmallStack(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }, SmallStack);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static object? InvokePrivate(RecordHandler handler, string name, object?[] args) =>
        typeof(RecordHandler)
            .GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(handler, args);

    [Fact]
    public void IncludeAsNewWalk_DeepOverrideChain_DoesNotOverflowTheStack()
    {
        var fx = Build();
        bool mergeParent = false;

        RunOnSmallStack(() =>
        {
            object?[] args =
            {
                fx.Head.ToLink(), new List<ModKey> { ModKeyEsp }, fx.OutputMod,
                new Dictionary<FormKey, FormKey>(), new HashSet<IMajorRecord>(),
                10, 0, new List<string>(), new HashSet<FormKey>(), null, CancellationToken.None,
            };
            mergeParent = (bool)InvokePrivate(fx.Handler, "TraverseAndDuplicateInOverrideRecords", args)!;
        });

        mergeParent.Should().BeTrue();
        fx.OutputMod.FormLists.Should().HaveCount(ChainLength, "every overridden list in the chain is copied");
    }

    [Fact]
    public void IncludeWalk_DeepOverrideChain_DoesNotOverflowTheStack()
    {
        var fx = Build();
        var collected = new HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>();

        RunOnSmallStack(() =>
        {
            object?[] args =
            {
                fx.Head.ToLink(), new List<ModKey> { ModKeyEsp }, collected,
                10, 0, new HashSet<FormKey>(), null, CancellationToken.None,
            };
            InvokePrivate(fx.Handler, "CollectOverriddenDependencyRecords", args);
        });

        collected.Should().HaveCount(ChainLength, "every overridden list in the chain is an override to collect");
    }
}
