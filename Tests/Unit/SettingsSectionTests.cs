using System;
using System.Collections.Generic;
using FluentAssertions;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The Settings view's sections are Expanders whose open/closed state persists to
/// Settings.SettingsViewExpandedSections. Two rules here are easy to break silently and
/// only show up a launch later: the setter write-back (without it nothing persists), and
/// the rule that only user-toggled sections are stored (without it, a section the user
/// never touched freezes at whatever default shipped when they first ran the build, so
/// changing a default in a later release would have no effect).
/// </summary>
public class SettingsSectionTests
{
    private static (VM_SettingsSection section, Dictionary<string, bool> store) Make(
        string key, bool defaultExpanded, Dictionary<string, bool>? store = null)
    {
        store ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return (new VM_SettingsSection(key, defaultExpanded, store), store);
    }

    [Fact]
    public void AbsentFromStore_UsesDefault()
    {
        Make("Game Environment", defaultExpanded: true).section.IsExpanded.Should().BeTrue();
        Make("Logging", defaultExpanded: false).section.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void PresentInStore_OverridesDefault()
    {
        var store = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["Game Environment"] = false,
            ["Logging"] = true,
        };

        Make("Game Environment", defaultExpanded: true, store).section.IsExpanded.Should().BeFalse();
        Make("Logging", defaultExpanded: false, store).section.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Construction_DoesNotWriteToStore()
    {
        // An untouched section must keep following its default, so restoring state must not
        // itself count as a toggle.
        var (_, store) = Make("Logging", defaultExpanded: false);
        store.Should().BeEmpty();
    }

    [Fact]
    public void Toggle_WritesBackToStore()
    {
        var (section, store) = Make("Logging", defaultExpanded: false);

        section.IsExpanded = true;

        store.Should().ContainKey("Logging").WhoseValue.Should().BeTrue();
    }

    [Fact]
    public void TogglingBackToTheDefault_IsStillRecorded()
    {
        // Deliberate: the user has now expressed a preference, so it is stored explicitly
        // rather than falling back to the default.
        var (section, store) = Make("Game Environment", defaultExpanded: true);

        section.IsExpanded = false;
        section.IsExpanded = true;

        store.Should().ContainKey("Game Environment").WhoseValue.Should().BeTrue();
    }

    [Fact]
    public void SettingTheSameValue_RaisesNothingAndWritesNothing()
    {
        var (section, store) = Make("Logging", defaultExpanded: false);
        var raised = 0;
        section.PropertyChanged += (_, _) => raised++;

        section.IsExpanded = false;

        raised.Should().Be(0);
        store.Should().BeEmpty();
    }

    [Fact]
    public void RoundTripsThroughSettings()
    {
        var settings = new Settings();
        settings.SettingsViewExpandedSections.Should()
            .BeEmpty("a fresh install has no remembered sections and falls back to the defaults");

        var section = new VM_SettingsSection("Mugshot Settings", defaultExpanded: false,
            settings.SettingsViewExpandedSections);
        section.IsExpanded = true;

        // A later session rebuilds from the persisted state, not the default.
        var reloaded = new VM_SettingsSection("Mugshot Settings", defaultExpanded: false,
            settings.SettingsViewExpandedSections);
        reloaded.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void SectionsAreIndependent()
    {
        var store = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var head = new VM_SettingsSection("Head Editing", defaultExpanded: false, store);
        var output = new VM_SettingsSection("Output Settings", defaultExpanded: true, store);

        head.IsExpanded = true;

        output.IsExpanded.Should().BeTrue("the nested section's toggle must not disturb its parent");
        store.Should().HaveCount(1, "only the toggled section is recorded");
    }

    [Fact]
    public void NoStore_StillTogglesInMemory()
    {
        // The parameterless-store overload exists for design-time / test use; it must not
        // throw on toggle.
        var section = new VM_SettingsSection("Logging", defaultExpanded: false);

        section.IsExpanded = true;

        section.IsExpanded.Should().BeTrue();
    }
}
