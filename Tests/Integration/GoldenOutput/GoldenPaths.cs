using System.IO;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Locates the test project's <c>TestData</c> directory at runtime by walking up from the test
/// assembly's output folder (<c>Tests/bin/Debug/net8.0-windows</c>) until an ancestor that contains a
/// <c>TestData</c> sub-directory is found. Reading committed fixtures (and the gitignored
/// <c>EnvironmentMap.local.json</c>) from the source tree avoids copying multi-megabyte reference
/// plugins into every build's output directory.
/// </summary>
internal static class GoldenPaths
{
    private static string? _testDataDir;

    /// <summary>The absolute path to <c>Tests/TestData</c>, or null if it could not be located.</summary>
    public static string? TestDataDir => _testDataDir ??= LocateTestDataDir();

    public static string? LocalMapPath =>
        TestDataDir == null ? null : Path.Combine(TestDataDir, "EnvironmentMap.local.json");

    public static string? ExampleMapPath =>
        TestDataDir == null ? null : Path.Combine(TestDataDir, "EnvironmentMap.example.json");

    /// <summary>Directory holding the committed reference plugins/tokens (<c>TestData/GoldenReference</c>).</summary>
    public static string? CommittedReferenceDir =>
        TestDataDir == null ? null : Path.Combine(TestDataDir, "GoldenReference");

    private static string? LocateTestDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
