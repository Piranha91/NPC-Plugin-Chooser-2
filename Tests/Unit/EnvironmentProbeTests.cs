using System;
using System.Reflection;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using NPC_Plugin_Chooser_2.BackEnd.SetupValidation;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="EnvironmentProbe.DescribeBuildFailure"/>: users who hit one unreadable plugin got a raw
/// stack trace. The wizard names the plugin instead.
/// </summary>
public class EnvironmentProbeTests
{
    [Fact]
    public void BuildFailure_NamesThePluginInTheError()
    {
        var ex = new InvalidOperationException("Error reading ccbgssse062-ba_dwarvenmail.esl: data count mismatch");
        EnvironmentProbe.DescribeBuildFailure(ex).Should().Contain("because of ccbgssse062-ba_dwarvenmail.esl.");
    }

    [Fact]
    public void BuildFailure_IgnoresSkyrimEsmAsTheCulprit()
    {
        var ex = new InvalidOperationException("Cannot add mod to itself as a master: Skyrim.esm, MyPatch.esp");
        EnvironmentProbe.DescribeBuildFailure(ex).Should().Contain("MyPatch.esp");
    }

    [Fact]
    public void BuildFailure_QuotedNameWithSpaces_IsKeptWhole()
    {
        var ex = new InvalidOperationException("Failed to parse 'Unofficial Skyrim Special Edition Patch.esp'");
        var text = EnvironmentProbe.DescribeBuildFailure(ex);
        text.Should().Contain("because of Unofficial Skyrim Special Edition Patch.esp.");
        text.Should().NotContain("because of Failed");
    }

    [Fact]
    public void BuildFailure_MalformedGroups_NamesPluginFromTypedContext()
    {
        // Jaykoby / kloxkinq / Grosaprap: "Read in data that was not a GRUP" from one bad plugin,
        // wrapped the way Mutagen's environment builder delivers it.
        var plugin = ModKey.FromNameAndExtension("Hand Ported Overlay.esp");
        var malformed = new ModGroupsMalformedException(new ModPath(plugin, @"C:\Mods\Overlay\Hand Ported Overlay.esp"),
            "Malformed groups", new ArgumentException("Read in data that was not a GRUP"));
        var ex = new AggregateException(RecordException.Enrich(new TargetInvocationException(malformed), plugin));

        var text = EnvironmentProbe.DescribeBuildFailure(ex);
        text.Should().Contain("because of Hand Ported Overlay.esp.");
        text.Should().Contain("LE (Oldrim)").And.Contain("verify your game files");
    }

    [Fact]
    public void BuildFailure_SelfMaster_ExplainsRenamedPatch()
    {
        // french20: a patch renamed to its master's file name.
        var ex = new InvalidOperationException("Cannot add mod to itself as a master: Oblivion Gates Remade.esp");
        EnvironmentProbe.DescribeBuildFailure(ex).Should().Contain("lists itself as a master").And.Contain("renamed");
    }

    [Fact]
    public void BuildFailure_WrappedInAggregate_StillNamesPlugin()
    {
        var ex = new AggregateException(new InvalidOperationException("x"),
            new InvalidOperationException("Error reading Broken Port.esp"));
        EnvironmentProbe.DescribeBuildFailure(ex).Should().Contain("Port.esp");
    }

    [Fact]
    public void BuildFailure_WithoutPlugin_FallsBackToMessage() =>
        EnvironmentProbe.DescribeBuildFailure(new InvalidOperationException("Could not locate game folder"))
            .Should().Contain("Could not locate game folder");
}
