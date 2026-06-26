using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// <see cref="NpcDiagnosticLogger"/> — the opt-in, process-global, per-NPC diagnostic
/// logger. Because every member touches the class's static state (the swapped
/// <c>_targets</c> dictionary, the <c>IsEnabled</c> flag, the <see cref="System.Threading.AsyncLocal{T}"/>
/// active-NPC context, and on-disk file handles under <c>{baseDir}\NPC Logs</c>), every test
/// runs in the serial integration collection and wraps its body in
/// <c>using var _ = new StaticStateGuard()</c>, whose <c>Dispose</c> calls
/// <c>Configure(null)</c> + <c>Shutdown()</c> to release handles and disarm the logger.
///
/// These tests do NOT need the WPF STA thread, but they DO touch <see cref="NpcDiagnosticLogger"/>'s
/// process-global static state, so per the suite convention they live in the Integration
/// namespace + collection (serial). They are otherwise pure: only the file-writing tests
/// touch disk, and those clean up the files they create under the test output dir.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class NpcDiagnosticLoggerTests
{
    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>A stable, non-null FormKey ("012345:Skyrim.esm" style).</summary>
    private static FormKey Fk(string s) => FormKey.Factory(s);

    private static string LogDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NPC Logs");

    private static string BuildFileName(string display, FormKey fk) =>
        Reflect.InvokeStatic<string>(typeof(NpcDiagnosticLogger), "BuildFileName", display, fk)!;

    private static string Sanitize(string value) =>
        Reflect.InvokeStatic<string>(typeof(NpcDiagnosticLogger), "Sanitize", value)!;

    /// <summary>Deletes a file under the NPC Logs dir, best-effort, after the handle is released.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // ---------------------------------------------------------------------
    // Configure / IsEnabled / IsLogged
    // ---------------------------------------------------------------------

    [Fact]
    public void Configure_Null_DisablesLogging()
    {
        using var _ = new StaticStateGuard();

        NpcDiagnosticLogger.Configure(null);

        NpcDiagnosticLogger.IsEnabled.Should().BeFalse();
        NpcDiagnosticLogger.IsLogged(Fk("000001:Skyrim.esm")).Should().BeFalse();
    }

    [Fact]
    public void Configure_EmptySet_DisablesLogging()
    {
        using var _ = new StaticStateGuard();

        NpcDiagnosticLogger.Configure(Array.Empty<(FormKey, string)>());

        NpcDiagnosticLogger.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Configure_ValidList_EnablesLoggingForEachFormKey()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000A01:Skyrim.esm");
        var b = Fk("000B02:Dawnguard.esm");

        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia"), (b, "Serana") });

        NpcDiagnosticLogger.IsEnabled.Should().BeTrue();
        NpcDiagnosticLogger.IsLogged(a).Should().BeTrue();
        NpcDiagnosticLogger.IsLogged(b).Should().BeTrue();
        NpcDiagnosticLogger.IsLogged(Fk("000C03:Skyrim.esm")).Should().BeFalse("unconfigured FormKey is not logged");
    }

    [Fact]
    public void Configure_OnlyNullFormKey_LeavesLoggingDisabled()
    {
        using var _ = new StaticStateGuard();

        // FormKey.Null entries are skipped; with nothing else, no targets remain -> disabled.
        NpcDiagnosticLogger.Configure(new[] { (FormKey.Null, "Ignored") });

        NpcDiagnosticLogger.IsEnabled.Should().BeFalse();
        NpcDiagnosticLogger.IsLogged(FormKey.Null).Should().BeFalse();
    }

    [Fact]
    public void Configure_NullFormKeyAmongValid_SkipsOnlyTheNullEntry()
    {
        using var _ = new StaticStateGuard();
        var valid = Fk("000123:Skyrim.esm");

        NpcDiagnosticLogger.Configure(new[] { (FormKey.Null, "Skip"), (valid, "Keep") });

        NpcDiagnosticLogger.IsEnabled.Should().BeTrue();
        NpcDiagnosticLogger.IsLogged(valid).Should().BeTrue();
        NpcDiagnosticLogger.IsLogged(FormKey.Null).Should().BeFalse();
    }

    [Fact]
    public void Configure_DuplicateFormKey_CollapsesToOneTarget()
    {
        using var _ = new StaticStateGuard();
        var dup = Fk("00ABCD:Skyrim.esm");

        // Second entry for the same FormKey is ignored (first wins, no throw on dup).
        Action act = () => NpcDiagnosticLogger.Configure(new[] { (dup, "First"), (dup, "Second") });

        act.Should().NotThrow();
        NpcDiagnosticLogger.IsEnabled.Should().BeTrue();
        NpcDiagnosticLogger.IsLogged(dup).Should().BeTrue();
    }

    [Fact]
    public void Configure_ReconfiguringWithNull_DisarmsAPreviouslyEnabledLogger()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000111:Skyrim.esm");

        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia") });
        NpcDiagnosticLogger.IsEnabled.Should().BeTrue();

        NpcDiagnosticLogger.Configure(null);
        NpcDiagnosticLogger.IsEnabled.Should().BeFalse();
        NpcDiagnosticLogger.IsLogged(a).Should().BeFalse();
    }

    [Fact]
    public void IsLogged_WhenDisabled_AlwaysFalse()
    {
        using var _ = new StaticStateGuard();
        NpcDiagnosticLogger.Configure(null);

        NpcDiagnosticLogger.IsLogged(Fk("000001:Skyrim.esm")).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // BeginNpc / EndNpc / IsActive
    // ---------------------------------------------------------------------

    [Fact]
    public void IsActive_NoContext_IsFalse()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000201:Skyrim.esm");
        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia") });

        // Enabled, but no BeginNpc has run on this flow.
        NpcDiagnosticLogger.IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ConfiguredNpcContext_IsTrue()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000202:Skyrim.esm");
        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia") });

        NpcDiagnosticLogger.BeginNpc(a);

        NpcDiagnosticLogger.IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsActive_UnconfiguredNpcContext_IsFalse()
    {
        using var _ = new StaticStateGuard();
        var configured = Fk("000203:Skyrim.esm");
        var other = Fk("000204:Skyrim.esm");
        NpcDiagnosticLogger.Configure(new[] { (configured, "Lydia") });

        NpcDiagnosticLogger.BeginNpc(other);

        NpcDiagnosticLogger.IsActive.Should().BeFalse("the active NPC isn't in the configured set");
        NpcDiagnosticLogger.IsLogged(other).Should().BeFalse();
    }

    [Fact]
    public void IsActive_AfterEndNpc_IsFalse()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000205:Skyrim.esm");
        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia") });

        NpcDiagnosticLogger.BeginNpc(a);
        NpcDiagnosticLogger.IsActive.Should().BeTrue();

        NpcDiagnosticLogger.EndNpc();
        NpcDiagnosticLogger.IsActive.Should().BeFalse("the context was cleared");
    }

    [Fact]
    public void BeginNpc_WhenDisabled_DoesNotArmContext()
    {
        using var _ = new StaticStateGuard();
        NpcDiagnosticLogger.Configure(null);

        // BeginNpc returns early when disabled, so IsActive stays false even with a "context".
        NpcDiagnosticLogger.BeginNpc(Fk("000206:Skyrim.esm"));

        NpcDiagnosticLogger.IsActive.Should().BeFalse();
    }

    [Fact]
    public void EndNpc_WhenDisabled_DoesNotThrow()
    {
        using var _ = new StaticStateGuard();
        NpcDiagnosticLogger.Configure(null);

        Action act = () => NpcDiagnosticLogger.EndNpc();

        act.Should().NotThrow();
        NpcDiagnosticLogger.IsActive.Should().BeFalse();
    }

    [Fact]
    public void BeginNpc_RetargetsActiveNpc_OnSameFlow()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000207:Skyrim.esm");
        var b = Fk("000208:Skyrim.esm");
        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia"), (b, "Serana") });

        NpcDiagnosticLogger.BeginNpc(a);
        NpcDiagnosticLogger.IsActive.Should().BeTrue();

        // Re-begin onto the other configured NPC; still active (different target).
        NpcDiagnosticLogger.BeginNpc(b);
        NpcDiagnosticLogger.IsActive.Should().BeTrue();

        // Re-begin onto an unconfigured NPC -> no longer active.
        NpcDiagnosticLogger.BeginNpc(Fk("000209:Skyrim.esm"));
        NpcDiagnosticLogger.IsActive.Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Sanitize (private static via Reflect)
    // ---------------------------------------------------------------------

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        using var _ = new StaticStateGuard();

        Sanitize(null!).Should().BeEmpty();
        Sanitize(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_ColonAndPipe_ReplacedWithUnderscore()
    {
        using var _ = new StaticStateGuard();

        // ':' (FormKey separator) and '|' (NPC list separator) are explicitly added to the invalid set.
        Sanitize("012345:Skyrim.esm").Should().Be("012345_Skyrim.esm");
        Sanitize("A|B").Should().Be("A_B");
    }

    [Fact]
    public void Sanitize_OsInvalidPathChars_ReplacedWithUnderscore()
    {
        using var _ = new StaticStateGuard();

        // Slashes are invalid on Windows filenames -> underscored.
        Sanitize("a/b\\c").Should().Be("a_b_c");
        // Other reserved characters.
        Sanitize("x*y?z").Should().Be("x_y_z");
        Sanitize("q\"r").Should().Be("q_r");
    }

    [Fact]
    public void Sanitize_TrailingDotAndSpace_AreTrimmed()
    {
        using var _ = new StaticStateGuard();

        // Trim() removes leading/trailing whitespace, then TrimEnd('.', ' ') strips trailing dots/spaces.
        Sanitize("Name.").Should().Be("Name");
        Sanitize("Name ").Should().Be("Name");
        Sanitize("  Name  ").Should().Be("Name");
        Sanitize("Name. .").Should().Be("Name");
    }

    [Fact]
    public void Sanitize_InteriorDotsAndSpaces_ArePreserved()
    {
        using var _ = new StaticStateGuard();

        // Only TRAILING dots/spaces are trimmed; interior ones survive.
        Sanitize("My Mod.esp Name").Should().Be("My Mod.esp Name");
    }

    [Fact]
    public void Sanitize_CleanName_PassesThroughUnchanged()
    {
        using var _ = new StaticStateGuard();

        Sanitize("Lydia_HousecarlOfWhiterun").Should().Be("Lydia_HousecarlOfWhiterun");
    }

    // ---------------------------------------------------------------------
    // BuildFileName (private static via Reflect)
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildFileName_AppendsSanitizedFormKeyTag()
    {
        using var _ = new StaticStateGuard();
        var fk = Fk("00442D:3DNPC.esp");

        var name = BuildFileName("Lydia", fk);

        // The FormKey tag is "_" + Sanitize(fk.ToString()); ':' becomes '_'.
        name.Should().StartWith("Lydia_");
        name.Should().EndWith("_" + Sanitize(fk.ToString()));
        name.Should().NotContain(":", "the colon in the FormKey is sanitized out");
    }

    [Fact]
    public void BuildFileName_BlankDisplay_FallsBackToFormKeyDerivedName()
    {
        using var _ = new StaticStateGuard();
        var fk = Fk("00442D:3DNPC.esp");

        // Whitespace/empty display -> cleaned name derives from formKey.ToString(), then the
        // FormKey tag is appended again, so the FormKey portion appears twice.
        var fromBlank = BuildFileName("   ", fk);
        var fromEmpty = BuildFileName(string.Empty, fk);
        var fromNull = BuildFileName(null!, fk);

        var fkSan = Sanitize(fk.ToString());
        var expected = fkSan + "_" + fkSan;
        fromBlank.Should().Be(expected);
        fromEmpty.Should().Be(expected);
        fromNull.Should().Be(expected);
    }

    [Fact]
    public void BuildFileName_NeverContainsRawSeparators()
    {
        using var _ = new StaticStateGuard();
        var fk = Fk("0ABCDE:SomeMod.esp");

        var name = BuildFileName("Disp:lay|Name", fk);

        name.Should().NotContain(":");
        name.Should().NotContain("|");
    }

    [Fact]
    public void BuildFileName_VeryLongDisplay_IsTruncatedToFit150()
    {
        using var _ = new StaticStateGuard();
        var fk = Fk("00442D:3DNPC.esp");
        var longDisplay = new string('X', 500);

        var name = BuildFileName(longDisplay, fk);

        // MaxFileNameLength = 150: the cleaned portion is truncated so cleaned+tag fits.
        name.Length.Should().BeLessThanOrEqualTo(150);
        var fkTag = "_" + Sanitize(fk.ToString());
        name.Should().EndWith(fkTag, "the FormKey tag is preserved; the display is what gets truncated");
        name.Length.Should().Be(150);
    }

    [Fact]
    public void BuildFileName_TruncationTrimsTrailingSpaceBeforeTag()
    {
        using var _ = new StaticStateGuard();
        var fk = Fk("00442D:3DNPC.esp");
        var fkTag = "_" + Sanitize(fk.ToString());
        // Make a display that, when cut at the truncation boundary, would end on a space.
        int cut = 150 - fkTag.Length;
        var display = new string('A', cut - 1) + " " + new string('B', 50);

        var name = BuildFileName(display, fk);

        // Substring(0, cut) ends in a space -> TrimEnd() drops it before the tag is appended.
        name.Should().EndWith(fkTag);
        name.Should().NotContain(" _", "the trailing space at the cut boundary is trimmed");
        name.Length.Should().BeLessThan(150);
    }

    [Fact]
    public void BuildFileName_ShortDisplay_FitsWithoutTruncation()
    {
        using var _ = new StaticStateGuard();
        var fk = Fk("00442D:3DNPC.esp");

        var name = BuildFileName("Lydia", fk);

        name.Should().Be("Lydia_" + Sanitize(fk.ToString()));
    }

    // ---------------------------------------------------------------------
    // Log / LogFor / LogSection — gating semantics (no write expected)
    // ---------------------------------------------------------------------

    [Fact]
    public void Log_WhenDisabled_IsNoOpAndDoesNotThrow()
    {
        using var _ = new StaticStateGuard();
        NpcDiagnosticLogger.Configure(null);

        Action act = () =>
        {
            NpcDiagnosticLogger.Log("hello");
            NpcDiagnosticLogger.LogSection("VALIDATION");
            NpcDiagnosticLogger.LogFor(Fk("000301:Skyrim.esm"), "hi");
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_EnabledButNoActiveContext_DoesNotCreateAnyFile()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000302:Skyrim.esm");
        var expectedFile = Path.Combine(LogDir, BuildFileName("Lydia", a) + ".txt");
        TryDelete(expectedFile);

        NpcDiagnosticLogger.Configure(new[] { (a, "Lydia") });

        // No BeginNpc -> Log's _currentNpc is null -> no write, no file.
        NpcDiagnosticLogger.Log("should not be written");

        File.Exists(expectedFile).Should().BeFalse("Log requires an active BeginNpc context");
    }

    // ---------------------------------------------------------------------
    // Log / Shutdown — actual file production + handle release
    // ---------------------------------------------------------------------

    [Fact]
    public void Log_WithActiveContext_ProducesFileThenShutdownReleasesHandle()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000401:Skyrim.esm");
        var fileName = BuildFileName("LydiaLog", a) + ".txt";
        var path = Path.Combine(LogDir, fileName);
        TryDelete(path);

        try
        {
            NpcDiagnosticLogger.Configure(new[] { (a, "LydiaLog") });
            NpcDiagnosticLogger.BeginNpc(a);
            NpcDiagnosticLogger.Log("first line of the trace");

            // The target opens lazily on first write -> the file now exists.
            File.Exists(path).Should().BeTrue("the first Log call opens and writes the file");

            NpcDiagnosticLogger.EndNpc();
            NpcDiagnosticLogger.Shutdown();

            // After Shutdown the writer is disposed; the file is fully readable (handle released).
            Action read = () =>
            {
                var text = File.ReadAllText(path);
                text.Should().Contain("first line of the trace");
                text.Should().Contain("per-NPC diagnostic");
            };
            read.Should().NotThrow("Shutdown flushes and releases the file handle");

            // And we can delete it now that the handle is closed.
            Action del = () => File.Delete(path);
            del.Should().NotThrow();
        }
        finally
        {
            // StaticStateGuard.Dispose also Shutdown()s; clean up regardless.
            NpcDiagnosticLogger.Shutdown();
            TryDelete(path);
        }
    }

    [Fact]
    public void LogFor_WritesToSpecificNpc_RegardlessOfActiveContext()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000402:Skyrim.esm");
        var fileName = BuildFileName("DirectTarget", a) + ".txt";
        var path = Path.Combine(LogDir, fileName);
        TryDelete(path);

        try
        {
            NpcDiagnosticLogger.Configure(new[] { (a, "DirectTarget") });

            // No BeginNpc context, but LogFor names the target explicitly.
            NpcDiagnosticLogger.LogFor(a, "explicit-target message");
            NpcDiagnosticLogger.Shutdown();

            File.Exists(path).Should().BeTrue("LogFor bypasses the active-context requirement");
            File.ReadAllText(path).Should().Contain("explicit-target message");
        }
        finally
        {
            NpcDiagnosticLogger.Shutdown();
            TryDelete(path);
        }
    }

    [Fact]
    public void LogFor_UnconfiguredNpc_WritesNothing()
    {
        using var _ = new StaticStateGuard();
        var configured = Fk("000403:Skyrim.esm");
        var stranger = Fk("000404:Skyrim.esm");
        var strangerPath = Path.Combine(LogDir, BuildFileName("Stranger", stranger) + ".txt");
        TryDelete(strangerPath);

        try
        {
            NpcDiagnosticLogger.Configure(new[] { (configured, "Known") });

            // WriteTo no-ops when the FormKey has no target.
            NpcDiagnosticLogger.LogFor(stranger, "should be dropped");
            NpcDiagnosticLogger.Shutdown();

            File.Exists(strangerPath).Should().BeFalse("an unconfigured FormKey has no target to write to");
        }
        finally
        {
            NpcDiagnosticLogger.Shutdown();
            TryDelete(strangerPath);
        }
    }

    [Fact]
    public void Log_TruncatesFileOnReconfigure_FreshRunStartsClean()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000405:Skyrim.esm");
        var fileName = BuildFileName("Reused", a) + ".txt";
        var path = Path.Combine(LogDir, fileName);
        TryDelete(path);

        try
        {
            // Run 1: write a marker.
            NpcDiagnosticLogger.Configure(new[] { (a, "Reused") });
            NpcDiagnosticLogger.BeginNpc(a);
            NpcDiagnosticLogger.Log("RUN_ONE_MARKER");
            NpcDiagnosticLogger.EndNpc();
            NpcDiagnosticLogger.Shutdown();
            File.ReadAllText(path).Should().Contain("RUN_ONE_MARKER");

            // Run 2: reconfigure (new target), first write truncates the file (FileMode.Create).
            NpcDiagnosticLogger.Configure(new[] { (a, "Reused") });
            NpcDiagnosticLogger.BeginNpc(a);
            NpcDiagnosticLogger.Log("RUN_TWO_MARKER");
            NpcDiagnosticLogger.EndNpc();
            NpcDiagnosticLogger.Shutdown();

            var text = File.ReadAllText(path);
            text.Should().Contain("RUN_TWO_MARKER");
            text.Should().NotContain("RUN_ONE_MARKER", "FileMode.Create truncates the prior run's contents");
        }
        finally
        {
            NpcDiagnosticLogger.Shutdown();
            TryDelete(path);
        }
    }

    [Fact]
    public void LogSection_WithActiveContext_WritesBannerToFile()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000406:Skyrim.esm");
        var path = Path.Combine(LogDir, BuildFileName("Banners", a) + ".txt");
        TryDelete(path);

        try
        {
            NpcDiagnosticLogger.Configure(new[] { (a, "Banners") });
            NpcDiagnosticLogger.BeginNpc(a);
            NpcDiagnosticLogger.LogSection("VALIDATION");
            NpcDiagnosticLogger.EndNpc();
            NpcDiagnosticLogger.Shutdown();

            var text = File.ReadAllText(path);
            text.Should().Contain("VALIDATION");
            text.Should().Contain("====================");
        }
        finally
        {
            NpcDiagnosticLogger.Shutdown();
            TryDelete(path);
        }
    }

    [Fact]
    public void Log_EmptyMessage_WritesBlankLineNotTimestamped()
    {
        using var _ = new StaticStateGuard();
        var a = Fk("000407:Skyrim.esm");
        var path = Path.Combine(LogDir, BuildFileName("BlankLines", a) + ".txt");
        TryDelete(path);

        try
        {
            NpcDiagnosticLogger.Configure(new[] { (a, "BlankLines") });
            NpcDiagnosticLogger.BeginNpc(a);
            NpcDiagnosticLogger.Log("BEFORE");
            NpcDiagnosticLogger.Log(string.Empty); // blank line, no "[HH:mm:ss.fff]" prefix
            NpcDiagnosticLogger.Log("AFTER");
            NpcDiagnosticLogger.EndNpc();
            NpcDiagnosticLogger.Shutdown();

            var lines = File.ReadAllLines(path);
            lines.Should().Contain(l => l.Contains("BEFORE"));
            lines.Should().Contain(l => l.Contains("AFTER"));
            // At least one fully-empty line exists between the two messages.
            lines.Should().Contain(string.Empty);
        }
        finally
        {
            NpcDiagnosticLogger.Shutdown();
            TryDelete(path);
        }
    }

    [Fact]
    public void Shutdown_WhenNothingConfigured_DoesNotThrow()
    {
        using var _ = new StaticStateGuard();
        NpcDiagnosticLogger.Configure(null);

        Action act = () => NpcDiagnosticLogger.Shutdown();

        act.Should().NotThrow();
    }
}
