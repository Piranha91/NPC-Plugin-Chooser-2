using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Stands up an <see cref="EnvironmentStateProvider"/> holding vanilla + Creation Club (auto-detected
/// from the game install) plus the synthetic fixture plugins, injected through the
/// <c>UpdateEnvironmentForTest</c> seam exactly as <c>GoldenEnvironmentBuilder</c> injects USSEP.
///
/// <para>Deliberately does NOT read the machine's mod-manager profile: the fixture is self-contained,
/// so the only external requirement is a resolvable Skyrim SE install (which
/// <c>UpdateEnvironmentCore</c> hard-requires — it needs a real load-order file and Skyrim.esm).
/// Without one, <see cref="SkipReason"/> is set and the suite skips.</para>
/// </summary>
internal sealed class TemplateMatrixEnvironment
{
    public EnvironmentStateProvider? Provider { get; private set; }
    public string SkipReason { get; private set; } = string.Empty;
    public bool Available => Provider != null;

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    public static TemplateMatrixEnvironment Build(TemplateFixture fixture, string envOutputFolder)
    {
        var result = new TemplateMatrixEnvironment();
        Directory.CreateDirectory(envOutputFolder);

        var fixturePlugins = fixture.PluginsInLoadOrder().ToList();

        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
            IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
        {
            var listings = input.OnlyEnabledAndExisting()
                .Where(l => IsVanillaOrCc(l.ModKey))
                .ToList();

            foreach (var (key, path) in fixturePlugins)
            {
                var loaded = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                listings.Add(new ModListing<ISkyrimModGetter>(key, loaded, enabled: true));
            }

            return listings;
        }

        try
        {
            // Opt-in hook for verifying the graceful-skip contract on a machine that DOES have Skyrim —
            // there is otherwise no way to exercise the skip path. Never set in normal runs.
            if (Environment.GetEnvironmentVariable("NPC2_SIMULATE_NO_SKYRIM") == "1")
            {
                result.SkipReason = "SIMULATED: no Skyrim SE install (NPC2_SIMULATE_NO_SKYRIM=1).";
                return result;
            }
            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutputFolder);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid
                || provider.LinkCache == null || provider.LoadOrder == null)
            {
                result.SkipReason = "Could not resolve a valid Skyrim SE environment" +
                                    (string.IsNullOrEmpty(provider.EnvironmentBuilderError)
                                        ? "."
                                        : ": " + provider.EnvironmentBuilderError);
                return result;
            }

            var loaded = provider.LoadOrder.ListedOrder.Select(l => l.ModKey).ToHashSet();
            var missing = fixturePlugins.Where(p => !loaded.Contains(p.Key)).ToList();
            if (missing.Count > 0)
            {
                result.SkipReason = "Fixture plugin(s) failed to load into the resolved load order: " +
                                    string.Join(", ", missing.Select(m => m.Key.FileName.String));
                return result;
            }

            result.Provider = provider;
            return result;
        }
        catch (Exception ex)
        {
            result.SkipReason = "Exception building the fixture environment: " + ex.Message;
            return result;
        }
    }
}
