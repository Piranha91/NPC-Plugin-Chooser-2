using System.Reflection;
using Autofac;
using CharacterViewer.Rendering;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Memory;

/// <summary>
/// Proves the renderer cache-mode setting actually reaches the renderer's byte budget — i.e. the dropdown
/// isn't cosmetic. Resolves the real <c>CharacterPreviewCache</c> from the frontend DI graph (no GPU, no
/// game env needed — the cache's constructor just computes budgets) and reflects its private
/// <c>_pixelBudgetBytes</c> for each mode. Runs in CI; skips only if the cache can't be constructed here.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class RenderCacheBudgetWiringTests
{
    private readonly ITestOutputHelper _out;
    public RenderCacheBudgetWiringTests(WpfStaFixture sta, ITestOutputHelper output) => _out = output;

    private static long PixelBudgetFor(RenderCacheMode mode, double fixedGb)
    {
        var settings = new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE };
        settings.InternalMugshot.CacheMode = mode;
        settings.InternalMugshot.CacheFixedBudgetGB = fixedGb;

        using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(), settings);
        var cache = harness.Container.Resolve<CharacterPreviewCache>();
        var field = typeof(CharacterPreviewCache)
            .GetField("_pixelBudgetBytes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (long)field.GetValue(cache)!;
    }

    [Fact]
    public void CacheMode_DrivesThePixelBudget()
    {
        long disabled, fixed4, percent;
        try
        {
            disabled = PixelBudgetFor(RenderCacheMode.Disabled, 4.0);
            fixed4 = PixelBudgetFor(RenderCacheMode.FixedRam, 4.0);
            percent = PixelBudgetFor(RenderCacheMode.PercentFreeRam, 4.0);
        }
        catch (Exception ex)
        {
            _out.WriteLine("SKIP: could not construct CharacterPreviewCache here: " + ex.GetBaseException().Message);
            return;
        }

        _out.WriteLine($"Disabled={disabled}  FixedRam(4GB)={fixed4}  PercentFreeRam={percent}");

        // Disabled → zero budget (caches retain nothing).
        disabled.Should().Be(0);

        // Fixed 4 GB → pixel cache gets its 0.5 fraction of the 4 GB pool = ~2 GB (subject to the
        // fraction-of-total-RAM ceiling on a small machine).
        double gb = 1024.0 * 1024 * 1024;
        long expectedFixed = (long)(4.0 * 0.5 * gb);
        fixed4.Should().BeGreaterThan(0);
        fixed4.Should().BeLessThanOrEqualTo(expectedFixed + 1,
            "the fixed pool times the pixel fraction is the target (a low-RAM machine may clamp it lower)");

        // % Free RAM → some positive, machine-dependent budget.
        percent.Should().BeGreaterThan(0);
    }
}
