using System.Collections.Generic;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Settings.NormalizeNpcGroupAssignments"/>: JSON deserialization builds case-sensitive
/// HashSets, so after a restart an NPC could hold the same group twice ("Riften" and "riften").
/// </summary>
public class NpcGroupAssignmentsTests
{
    private static readonly FormKey Npc = FormKey.Factory("000ABC:Skyrim.esm");
    private static readonly FormKey Empty = FormKey.Factory("000DEF:Skyrim.esm");

    [Fact]
    public void Normalize_MergesCaseAndWhitespaceVariants_AndDropsEmptySets()
    {
        var settings = new Settings
        {
            NpcGroupAssignments = new Dictionary<FormKey, HashSet<string>>
            {
                [Npc] = new() { "Riften", "riften", "Riften ", "Whiterun", " " },
                [Empty] = new() { "" },
            },
        };

        settings.NormalizeNpcGroupAssignments();

        settings.NpcGroupAssignments.Should().ContainKey(Npc).And.NotContainKey(Empty);
        settings.NpcGroupAssignments[Npc].Should().HaveCount(2);
        settings.NpcGroupAssignments[Npc].Add("RIFTEN").Should().BeFalse("the rebuilt set is case-insensitive");
    }
}
