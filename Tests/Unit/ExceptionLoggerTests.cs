using System.Reflection;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

public class ExceptionLoggerTests
{
    [Fact]
    public void EnvironmentImportFailure_ReportsPluginPathAndUnderlyingFailure()
    {
        var plugin = ModKey.FromNameAndExtension("BrokenAppearance.esp");
        const string path = @"C:\Mods\Appearance\BrokenAppearance.esp";
        var malformed = new ModGroupsMalformedException(new ModPath(plugin, path),
            "Malformed groups", new ArgumentException("Read in data that was not a GRUP"));
        var error = new AggregateException(
            RecordException.Enrich(new TargetInvocationException(malformed), plugin));

        var report = ExceptionLogger.GetExceptionStack(error);

        report.Should().Contain("Plugin: BrokenAppearance.esp")
            .And.Contain($"Plugin path: {path}")
            .And.Contain(typeof(ModGroupsMalformedException).FullName!)
            .And.Contain("Read in data that was not a GRUP");
    }

    [Fact]
    public void RecordFailure_ReportsContainingPluginAndRecordIdentity()
    {
        var plugin = ModKey.FromNameAndExtension("AppearanceOverride.esp");
        var formKey = FormKey.Factory("000123:Skyrim.esm");
        var error = RecordException.Enrich(new InvalidOperationException("Failed to read NPC"),
            formKey, typeof(INpcGetter), "ExampleNpc", plugin);

        var report = ExceptionLogger.GetExceptionStack(error);

        report.Should().Contain("Plugin: AppearanceOverride.esp")
            .And.Contain($"Record: {formKey}")
            .And.Contain($"Record type: {typeof(INpcGetter).FullName}")
            .And.Contain("Editor ID: ExampleNpc");
    }

    [Fact]
    public void NestedAggregates_ReportEveryBranchWithoutRepeatingFirstChild()
    {
        Exception Failure(string plugin) => RecordException.Enrich(
            new TargetInvocationException(new InvalidOperationException("Import failed")),
            ModKey.FromNameAndExtension(plugin));
        var error = new AggregateException(Failure("First.esp"),
            new AggregateException(Failure("Second.esp"), Failure("Third.esp")));

        var report = ExceptionLogger.GetExceptionStack(error);

        foreach (var plugin in new[] { "First.esp", "Second.esp", "Third.esp" })
            report.Split($"Plugin: {plugin}").Should().HaveCount(2);
        report.Should().Contain("(branch 1)")
            .And.Contain("(branch 2.1)")
            .And.Contain("(branch 2.2)");
    }

    [Fact]
    public void TooManyMasters_KeepsMasterListAndNamesOutputPlugin()
    {
        var error = new TooManyMastersException(ModKey.FromNameAndExtension("NPC.esp"),
            [ModKey.FromNameAndExtension("Skyrim.esm"), ModKey.FromNameAndExtension("Appearance.esp")]);

        ExceptionLogger.GetExceptionStack(error).Should().Contain("Plugin: NPC.esp")
            .And.Contain($"Current Masters:{Environment.NewLine}Skyrim.esm{Environment.NewLine}Appearance.esp");
    }

    [Fact]
    public void OrdinaryException_KeepsTypeMessageAndStackTrace()
    {
        Exception error;
        try
        {
            throw new InvalidOperationException("Outer failure", new ArgumentException("Inner failure"));
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var report = ExceptionLogger.GetExceptionStack(error);

        report.Should().Contain("System.InvalidOperationException: Outer failure")
            .And.Contain("System.ArgumentException: Inner failure")
            .And.Contain(error.StackTrace!);
    }

    [Fact]
    public void ReactiveWrapper_KeepsInnerFailureWithoutWrapperNoise()
    {
        var error = new UnhandledErrorException("Reactive wrapper", new InvalidOperationException("Actual failure"));

        ExceptionLogger.GetExceptionStack(error).Should().Contain("Actual failure")
            .And.NotContain("Reactive wrapper");
    }

    [Fact]
    public void ReactiveWrapperWithoutInnerException_IsStillReported()
    {
        ExceptionLogger.GetExceptionStack(new UnhandledErrorException("Standalone failure"))
            .Should().Contain("Standalone failure");
    }

    [Fact]
    public void MalformedGroupsWithoutPluginContext_StillReportsUnderlyingFailure()
    {
        var error = new ModGroupsMalformedException("Malformed groups", new ArgumentException("Bad header"));

        ExceptionLogger.GetExceptionStack(error).Should().Contain("Malformed groups").And.Contain("Bad header");
    }
}
