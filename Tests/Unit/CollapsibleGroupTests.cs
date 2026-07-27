using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using FluentAssertions;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The NPCs view's settings-bar group boxes collapse to just their caption so users on
/// narrower displays can reclaim the row. Two things here are easy to break silently and
/// invisible until someone relaunches: the toggle -> Settings write-back, and the rule that
/// only collapsed groups are stored (so a group added later defaults to expanded rather
/// than needing an UpdateHandler migration).
/// </summary>
public class CollapsibleGroupTests
{
    private static (VM_CollapsibleGroup group, HashSet<string> store) Make(
        string title, HashSet<string> collapsed = null)
    {
        var store = collapsed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var group = new VM_CollapsibleGroup(
            title,
            isExpanded: !store.Contains(title),
            onChanged: (key, expanded) =>
            {
                if (expanded) store.Remove(key);
                else store.Add(key);
            });
        return (group, store);
    }

    [Fact]
    public void AbsentFromStore_StartsExpanded()
    {
        var (group, _) = Make("Show");
        group.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void PresentInStore_StartsCollapsed()
    {
        var (group, _) = Make("Show", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Show" });
        group.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void Toggle_WritesOnlyCollapsedGroupsToStore()
    {
        var (group, store) = Make("Show");

        group.ToggleCommand.Execute().Subscribe();
        group.IsExpanded.Should().BeFalse();
        store.Should().Contain("Show");

        group.ToggleCommand.Execute().Subscribe();
        group.IsExpanded.Should().BeTrue();
        store.Should().BeEmpty("expanded is the default state and must not be persisted");
    }

    [Fact]
    public void StoreIsCaseInsensitive_SoCaptionCasingDriftDoesNotResetState()
    {
        var (group, _) = Make("show", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Show" });
        group.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void RoundTripsThroughSettings()
    {
        var settings = new Settings();
        settings.NpcsViewCollapsedGroups.Should().BeEmpty("nothing is collapsed on a fresh install");

        var (group, _) = Make("Selected Mugshots", settings.NpcsViewCollapsedGroups);
        group.ToggleCommand.Execute().Subscribe();

        settings.NpcsViewCollapsedGroups.Should().BeEquivalentTo(new[] { "Selected Mugshots" });

        // A later session rebuilds from the persisted set.
        var (reloaded, _) = Make("Selected Mugshots", settings.NpcsViewCollapsedGroups);
        reloaded.IsExpanded.Should().BeFalse();
    }

    /// <summary>
    /// Collapsing zeroes the GroupBox's BorderThickness and Padding so no empty frame is
    /// left behind — that is what actually reclaims the horizontal space.
    /// </summary>
    [Theory]
    [InlineData(true, 1.0, 2.0)]
    [InlineData(false, 0.0, 0.0)]
    public void ThicknessConverter_ZeroesChromeWhenCollapsed(bool expanded, double border, double padding)
    {
        var borderConverter = new BooleanToThicknessConverter { TrueThickness = new Thickness(1), FalseThickness = new Thickness(0) };
        var paddingConverter = new BooleanToThicknessConverter { TrueThickness = new Thickness(2), FalseThickness = new Thickness(0) };

        borderConverter.Convert(expanded, typeof(Thickness), null!, CultureInfo.InvariantCulture)
            .Should().Be(new Thickness(border));
        paddingConverter.Convert(expanded, typeof(Thickness), null!, CultureInfo.InvariantCulture)
            .Should().Be(new Thickness(padding));
    }

    [Fact]
    public void ThicknessConverter_NonBoolIsTreatedAsCollapsedAndNeverThrows()
    {
        var converter = new BooleanToThicknessConverter();
        converter.Invoking(c => c.Convert("not a bool", typeof(Thickness), null!, CultureInfo.InvariantCulture))
            .Should().NotThrow("a converter that throws during layout would take the window down");
    }
}
