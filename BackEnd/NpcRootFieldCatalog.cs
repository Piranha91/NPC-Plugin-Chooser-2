using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// The one table describing every FormLink-bearing field of an NPC record: its xEdit signature and
/// label, whether it is an appearance field (checked by default), and how to read its links.
/// Drives BOTH the "Override Roots" dialog and the patcher's discovery roots, so the list a user
/// ticks and the list the walk actually starts from cannot drift apart.
///
/// <para><b>Why this exists.</b> Override discovery used to root on the NPC's entire
/// <c>EnumerateFormLinks()</c>, so AI packages were roots — and from a package the walk reaches
/// placed references, cells, quests, and back into other NPCs. Anything genuinely overridden down
/// there then dragged its whole ancestry in as private copies: on a measured run six NPCs had their
/// package links repointed at private duplicates of vanilla packages referencing private duplicates
/// of vanilla quests (DB01, SolitudeOpening). Restricting the roots fixes that, but no fixed
/// allowlist can be proven complete — appearance can hide in oblique places, and this app had
/// already needed three ad-hoc patches around its previous hardcoded list. So the default is the
/// appearance set and the user can correct it per mod.</para>
///
/// <para>Roots only. A checked field is followed to unlimited depth through any record type; an
/// unchecked one is simply not a starting point. Nothing here filters by record TYPE.</para>
/// </summary>
public static class NpcRootFieldCatalog
{
    /// <summary>One row of the table. <see cref="Extract"/> returns the field's links on a given
    /// record — empty when unset, which is the common case.</summary>
    public sealed record Entry(
        NpcRootField Field,
        string? Signature,
        string Label,
        bool OnByDefault,
        Func<INpcGetter, IEnumerable<IFormLinkGetter>> Extract)
    {
        /// <summary>xEdit's own rendering, e.g. "WNAM - Worn Armor". Signature-less fields
        /// (xEdit shows them as a bare group) fall back to the label alone.</summary>
        public string DisplayName => Signature == null ? Label : $"{Signature} - {Label}";
    }

    private static IEnumerable<IFormLinkGetter> One(IFormLinkGetter? link) =>
        link is null || link.IsNull ? Array.Empty<IFormLinkGetter>() : new[] { link };

    private static IEnumerable<IFormLinkGetter> Many(IEnumerable<IFormLinkGetter>? links) =>
        links?.Where(l => !l.IsNull) ?? Array.Empty<IFormLinkGetter>();

    // Nested structures (script properties, attack data, container entries...) are read through
    // Mutagen's own link enumeration rather than by naming their sub-fields here: it keeps the
    // table from having to track shapes it does not own, and it cannot miss a link the record
    // format grows later.
    private static IEnumerable<IFormLinkGetter> Nested(IFormLinkContainerGetter? container) =>
        container?.EnumerateFormLinks().Where(l => !l.IsNull) ?? Array.Empty<IFormLinkGetter>();

    private static IEnumerable<IFormLinkGetter> NestedAll<T>(IEnumerable<T>? items)
        where T : IFormLinkContainerGetter =>
        items?.SelectMany(i => i.EnumerateFormLinks()).Where(l => !l.IsNull)
        ?? Array.Empty<IFormLinkGetter>();

    /// <summary>
    /// The table, in xEdit's own field order so the dialog reads like the record does.
    /// </summary>
    public static readonly IReadOnlyList<Entry> All = new List<Entry>
    {
        new(NpcRootField.VirtualMachineAdapter, "VMAD", "Virtual Machine Adapter", false,
            n => Nested(n.VirtualMachineAdapter)),
        new(NpcRootField.Factions, "SNAM", "Faction", false,
            n => NestedAll(n.Factions)),
        new(NpcRootField.DeathItem, "INAM", "Death item", false,
            n => One(n.DeathItem)),
        new(NpcRootField.Voice, "VTCK", "Voice", false,
            n => One(n.Voice)),
        new(NpcRootField.Template, "TPLT", "Template", true,
            n => One(n.Template)),
        new(NpcRootField.Race, "RNAM", "Race", true,
            n => One(n.Race)),
        new(NpcRootField.ActorEffect, "SPLO", "Actor Effects", false,
            n => Many(n.ActorEffect)),
        new(NpcRootField.Destructible, "DEST", "Destructible", false,
            n => Nested(n.Destructible)),
        new(NpcRootField.WornArmor, "WNAM", "Worn Armor", true,
            n => One(n.WornArmor)),
        new(NpcRootField.FarAwayModel, "ANAM", "Far away model", false,
            n => One(n.FarAwayModel)),
        new(NpcRootField.AttackRace, "ATKR", "Attack Race", false,
            n => One(n.AttackRace)),
        new(NpcRootField.Attacks, "ATKD", "Attacks", false,
            n => NestedAll(n.Attacks)),
        new(NpcRootField.SpectatorOverridePackageList, "SPOR", "Spectator override package list", false,
            n => One(n.SpectatorOverridePackageList)),
        new(NpcRootField.ObserveDeadBodyOverridePackageList, "OCOR", "Observe dead body override package list", false,
            n => One(n.ObserveDeadBodyOverridePackageList)),
        new(NpcRootField.GuardWarnOverridePackageList, "GWOR", "Guard warn override package list", false,
            n => One(n.GuardWarnOverridePackageList)),
        new(NpcRootField.CombatOverridePackageList, "ECOR", "Combat override package list", false,
            n => One(n.CombatOverridePackageList)),
        new(NpcRootField.Perks, "PRKR", "Perks", false,
            n => NestedAll(n.Perks)),
        new(NpcRootField.Items, "CNTO", "Items", false,
            n => NestedAll(n.Items)),
        new(NpcRootField.Packages, "PKID", "Package", false,
            n => Many(n.Packages)),
        new(NpcRootField.Keywords, "KWDA", "Keywords", false,
            n => Many(n.Keywords)),
        new(NpcRootField.Class, "CNAM", "Class", false,
            n => One(n.Class)),
        new(NpcRootField.HeadParts, "PNAM", "Head Part", true,
            n => Many(n.HeadParts)),
        new(NpcRootField.HairColor, "HCLF", "Hair Color", true,
            n => One(n.HairColor)),
        new(NpcRootField.CombatStyle, "ZNAM", "Combat Style", false,
            n => One(n.CombatStyle)),
        new(NpcRootField.GiftFilter, "GNAM", "Gift Filter", false,
            n => One(n.GiftFilter)),
        // xEdit shows these as two fields; Mutagen models them as one polymorphic Sound value
        // (NpcSoundTypes vs NpcSoundInheritance), so they cannot be ticked apart.
        new(NpcRootField.Sound, null, "Sound Types / Inherits Sounds From (CSDT, CSCR)", false,
            n => Nested(n.Sound)),
        new(NpcRootField.DefaultOutfit, "DOFT", "Default outfit", true,
            n => One(n.DefaultOutfit)),
        new(NpcRootField.SleepingOutfit, "SOFT", "Sleeping outfit", true,
            n => One(n.SleepingOutfit)),
        new(NpcRootField.DefaultPackageList, "DPLT", "Default Package List", false,
            n => One(n.DefaultPackageList)),
        new(NpcRootField.CrimeFaction, "CRIF", "Crime faction", false,
            n => One(n.CrimeFaction)),
        new(NpcRootField.HeadTexture, "FTST", "Head texture", true,
            n => One(n.HeadTexture)),
    };

    private static readonly Dictionary<NpcRootField, Entry> ByField =
        All.ToDictionary(e => e.Field);

    /// <summary>The appearance set — what a fresh install and a fresh mod entry start from.</summary>
    public static IReadOnlySet<NpcRootField> Defaults { get; } =
        All.Where(e => e.OnByDefault).Select(e => e.Field).ToHashSet();

    /// <summary>Every field, for the "Check All" affordance and for grandfathering.</summary>
    public static IReadOnlySet<NpcRootField> AllFields { get; } =
        All.Select(e => e.Field).ToHashSet();

    public static Entry Get(NpcRootField field) => ByField[field];

    /// <summary>
    /// The root set in force for one mod: its own selection if it has one, else the global
    /// default, else the appearance set. Null at either level means "not customised" and falls
    /// through; an EMPTY set is a real answer ("root at nothing") and stops the fallthrough, which
    /// is what lets a user deliberately switch discovery off for a mod.
    ///
    /// <para>Both levels default to null so an install that predates this option — and a mod entry
    /// the user never opened the dialog for — resolve to <see cref="Defaults"/> with no migration.</para>
    /// </summary>
    public static IReadOnlySet<NpcRootField> Resolve(ModSetting? mod, Settings? settings) =>
        mod?.OverrideTraversalRoots
        ?? settings?.DefaultOverrideTraversalRoots
        ?? Defaults;

    /// <summary>
    /// The links the discovery walk should start from, given a selection. Order follows the table
    /// (xEdit's field order) so a run's traversal order is stable and reproducible.
    /// </summary>
    public static List<IFormLinkGetter> GetRootLinks(INpcGetter npc, IReadOnlySet<NpcRootField> selected)
    {
        var roots = new List<IFormLinkGetter>();
        foreach (var entry in All)
        {
            if (!selected.Contains(entry.Field)) continue;
            roots.AddRange(entry.Extract(npc));
        }
        return roots;
    }
}
