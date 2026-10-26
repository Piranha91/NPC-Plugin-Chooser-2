using System.IO;
using Newtonsoft.Json.Linq;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;

/// <summary>
/// Applies the controlled input change of the benchmark's "refresh after a fixture change" scenario.
/// A mutation file holds one tab-separated command per line:
/// <c>copy&#9;src&#9;dst</c>, <c>delete&#9;path</c>, <c>mkdir&#9;path</c>, <c>touch&#9;path</c>.
///
/// <para><b>Safety:</b> every destination must resolve inside <c>NPC2_BENCH_MODS_FOLDER</c> (the disposable
/// fixture subset), and that folder must not be inside the real MO2 instance, so this can never modify the
/// main test installation. Sources may be anywhere (they are only read).</para>
/// </summary>
internal static class FixtureMutations
{
    public static JArray Apply(string mutationFile)
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("NPC2_BENCH_MODS_FOLDER");
        var instance = Environment.GetEnvironmentVariable("NPC2_BENCH_INSTANCE") ?? @"S:\Skyrim NPC Selection";
        if (string.IsNullOrWhiteSpace(fixtureRoot))
            throw new InvalidOperationException("Fixture mutations require NPC2_BENCH_MODS_FOLDER (the disposable subset).");
        fixtureRoot = Path.GetFullPath(fixtureRoot).TrimEnd('\\') + "\\";
        if (fixtureRoot.StartsWith(Path.GetFullPath(instance).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to mutate '{fixtureRoot}': it is inside the real instance.");

        string Guard(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Mutation target '{full}' is outside the fixture root '{fixtureRoot}'.");
            return full;
        }

        var applied = new JArray();
        foreach (var raw in File.ReadAllLines(mutationFile))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#')) continue;
            var parts = raw.Split('\t');
            switch (parts[0])
            {
                case "copy":
                    var dst = Guard(parts[2]);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(parts[1], dst, overwrite: true);
                    // A copied file keeps its source timestamp; stamp it so size+mtime snapshots see a change
                    // even when a same-sized archive replaces another.
                    File.SetLastWriteTimeUtc(dst, DateTime.UtcNow);
                    break;
                case "delete":
                    var del = Guard(parts[1]);
                    if (File.Exists(del)) File.Delete(del);
                    else if (Directory.Exists(del)) Directory.Delete(del, recursive: true);
                    break;
                case "mkdir":
                    Directory.CreateDirectory(Guard(parts[1]));
                    break;
                case "touch":
                    File.SetLastWriteTimeUtc(Guard(parts[1]), DateTime.UtcNow);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown mutation command '{parts[0]}'.");
            }
            applied.Add(raw);
        }
        return applied;
    }
}
