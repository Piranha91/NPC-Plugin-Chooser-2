using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// The one definition of "appearance record type", used by the per-mod <b>Appearance records
/// only</b> option (<see cref="ModSetting.AppearanceOverridesOnly"/>) in both override modes: the
/// walk collects only overrides of these types, and "Include All" keeps only overrides of these
/// types.
///
/// <para><b>How the set is built.</b> Two steps, neither of which reads a plugin:</para>
/// <list type="number">
/// <item><b>One hop, by reflection.</b> The record types the mod's ticked Override Roots fields
/// point at (<see cref="RootFieldTargets"/>). It follows whatever is ticked, so unticking both
/// outfit roots drops Outfit, and ticking a non-appearance root adds what that root points at
/// (PKID adds Package; VMAD, whose script properties can name any record, adds every type, which
/// makes the option a no-op for that mod).</item>
/// <item><b>Extended along the declared table below</b> (<see cref="All"/>), closed transitively:
/// Armor to ArmorAddon, ArmorAddon to TextureSet, and so on. A type with no row is terminal:
/// it is in the set if something points at it, and nothing is followed out of it.</item>
/// </list>
///
/// <para><b>Why a declared table and not reflection all the way down.</b> Measured on Mutagen's
/// record classes: one hop from the eight default roots reaches 8 of 133 record types (no
/// armature); two hops reach all 133, because script properties and form lists can point at any
/// record. Neither depth is the wanted set, so the cut is declared, link by link.</para>
///
/// <para><b>What keeps it honest.</b> Every FormLink-bearing member of every type with a row is
/// listed here as either followed or not followed. <c>AppearanceRecordTypeCatalogTests</c> walks
/// Mutagen's record classes and fails when a member is in neither list, so a Mutagen update or a
/// newly added row cannot let a link fall through unclassified (the pattern
/// <see cref="NpcRootFieldCatalog"/> uses for NPC fields).</para>
///
/// <para>Record types are identified by their Mutagen GETTER interface (<c>IArmorGetter</c>),
/// which is what <c>record.Registration.GetterType</c> returns for a record read from a plugin.</para>
/// </summary>
public static class AppearanceRecordTypeCatalog
{
    /// <summary>
    /// One FormLink-bearing member of a record type. <see cref="Follows"/> lists the record types
    /// it contributes to the set; empty means the member is deliberately not followed, and
    /// <see cref="Reason"/> says why. A followed member may carry a note there too: why it counts,
    /// or which part of what it can point at is left out.
    /// </summary>
    public sealed record Member(string Name, IReadOnlyList<Type> Follows, string? Reason)
    {
        public bool IsFollowed => Follows.Count > 0;
    }

    /// <summary>One row: a record type and the classification of each of its link members.</summary>
    /// <param name="GetterType">The record's getter interface, e.g. <c>IArmorGetter</c>.</param>
    /// <param name="ClassType">The record's concrete class, e.g. <c>Armor</c>; what the
    /// completeness test reflects over.</param>
    /// <param name="Members">Every FormLink-bearing member, followed or not.</param>
    /// <param name="MembersComeFromNpcRootFieldCatalog">True for Npc only: its link members are
    /// the Override Roots fields, classified by <see cref="NpcRootFieldCatalog"/> (which has its
    /// own completeness test), and the ones followed are the ones the user ticked.</param>
    public sealed record TypeEntry(
        Type GetterType,
        Type ClassType,
        IReadOnlyList<Member> Members,
        bool MembersComeFromNpcRootFieldCatalog = false);

    private static Member Follow(string member, params Type[] targets) =>
        new(member, targets, null);

    private static Member FollowWithNote(string member, string note, params Type[] targets) =>
        new(member, targets, note);

    private static Member Skip(string member, string reason) =>
        new(member, Array.Empty<Type>(), reason);

    private static TypeEntry Row<TGetter, TClass>(params Member[] members)
        where TClass : TGetter =>
        new(typeof(TGetter), typeof(TClass), members);

    // Reasons used more than once.
    private const string Scripts = "script properties, which can point at any record";
    private const string Sound = "a sound";
    private const string Keywords = "keywords";
    private const string Spells = "spells and abilities";
    private const string Combat = "combat data";
    private const string Movement = "how the actor moves";
    private const string EquipSlot = "an equip slot";
    private const string Material = "what it sounds and sparks like when hit";
    private const string ChanceGlobal = "the global behind the list's chance of giving nothing";
    private const string RaceItIsFor =
        "names the race the piece is FOR; that race's own record does not change how the piece looks";

    /// <summary>
    /// The declared table. Order is the order the types join the set from the default roots.
    /// A member followed for its model's alternate textures is one whose model is drawn on the
    /// actor; a model that is not (an item lying in the world, an editor marker) is not followed.
    /// </summary>
    public static readonly IReadOnlyList<TypeEntry> All = new List<TypeEntry>
    {
        // Npc is reached through TPLT. Which of its links are followed is the user's Override
        // Roots selection itself, so step 1 has already contributed everything it points at.
        new(typeof(INpcGetter), typeof(Npc), Array.Empty<Member>(), MembersComeFromNpcRootFieldCatalog: true),

        Row<ILeveledNpcGetter, LeveledNpc>(
            FollowWithNote("Entries", "the entry's owner and global are not followed",
                typeof(INpcGetter), typeof(ILeveledNpcGetter)),
            Skip("Global", ChanceGlobal),
            Skip("Model", "the list's own editor model, not the actor's")),

        Row<IRaceGetter, Race>(
            Follow("Skin", typeof(IArmorGetter)),
            Follow("MorphRace", typeof(IRaceGetter)),
            Follow("ArmorRace", typeof(IRaceGetter)),
            Follow("DefaultHairColors", typeof(IColorRecordGetter)),
            FollowWithNote("HeadData",
                "head parts, hair colors, face textures and tint colors; the race presets (NPC records used by character creation) are not followed",
                typeof(IHeadPartGetter), typeof(IColorRecordGetter), typeof(ITextureSetGetter)),
            Follow("BodyData", typeof(ITextureSetGetter)),
            Follow("DecapitateArmors", typeof(IArmorGetter)),
            Skip("DecapitationFX", "an effect played when the head comes off, not part of the actor"),
            Skip("Hairs", "the HAIR record type of earlier games; in Skyrim hair is a head part"),
            Skip("Eyes", "the EYES record type of earlier games; in Skyrim eyes are a head part"),
            Skip("SkeletalModel", "the skeleton; nothing in it is drawn"),
            Skip("BehaviorGraph", "names the animation behaviour file; nothing in it is drawn"),
            Skip("AttackRace", "the race whose attacks are used: combat, not looks"),
            Skip("Attacks", Combat),
            Skip("ActorEffect", Spells),
            Skip("BodyPartData", "hit locations and dismemberment"),
            Skip("ImpactDataSet", Material),
            Skip("MaterialType", Material),
            Skip("Keywords", Keywords),
            Skip("Voices", "voice types"),
            Skip("OpenLootSound", Sound),
            Skip("CloseLootSound", Sound),
            Skip("EquipmentSlots", EquipSlot),
            Skip("UnarmedEquipSlot", EquipSlot),
            Skip("MovementTypes", Movement),
            Skip("BaseMovementDefaultWalk", Movement),
            Skip("BaseMovementDefaultRun", Movement),
            Skip("BaseMovementDefaultSwim", Movement),
            Skip("BaseMovementDefaultFly", Movement),
            Skip("BaseMovementDefaultSneak", Movement),
            Skip("BaseMovementDefaultSprint", Movement)),

        Row<IArmorGetter, Armor>(
            Follow("Armature", typeof(IArmorAddonGetter)),
            FollowWithNote("TemplateArmor", "an armor with a template takes its armatures from it",
                typeof(IArmorGetter)),
            Skip("Race", RaceItIsFor),
            Skip("WorldModel", "the model of the item lying in the world, not the one worn"),
            Skip("VirtualMachineAdapter", Scripts),
            Skip("ObjectEffect", "the enchantment"),
            Skip("Keywords", Keywords),
            Skip("EquipmentType", EquipSlot),
            Skip("Destructible", "destruction stages"),
            Skip("BashImpactDataSet", Material),
            Skip("AlternateBlockMaterial", Material),
            Skip("PickUpSound", Sound),
            Skip("PutDownSound", Sound)),

        Row<IArmorAddonGetter, ArmorAddon>(
            Follow("SkinTexture", typeof(ITextureSetGetter)),
            FollowWithNote("TextureSwapList", "the skin texture swap list; the list itself is terminal",
                typeof(IFormListGetter)),
            Follow("WorldModel", typeof(ITextureSetGetter)),
            Follow("FirstPersonModel", typeof(ITextureSetGetter)),
            Skip("Race", RaceItIsFor),
            Skip("AdditionalRaces", RaceItIsFor),
            Skip("ArtObject", "an effect model attached to the piece; not among the approved appearance types"),
            Skip("FootstepSound", Sound)),

        Row<ITextureSetGetter, TextureSet>(),

        Row<IColorRecordGetter, ColorRecord>(),

        Row<IHeadPartGetter, HeadPart>(
            Follow("ExtraParts", typeof(IHeadPartGetter)),
            Follow("TextureSet", typeof(ITextureSetGetter)),
            Follow("Color", typeof(IColorRecordGetter)),
            Follow("Model", typeof(ITextureSetGetter)),
            FollowWithNote("ValidRaces", "the list of races the part is valid for; the list itself is terminal",
                typeof(IFormListGetter))),

        Row<IOutfitGetter, Outfit>(
            Follow("Items", typeof(IArmorGetter), typeof(ILeveledItemGetter))),

        Row<ILeveledItemGetter, LeveledItem>(
            FollowWithNote("Entries",
                "only armor and nested lists among the item types; the entry's owner and global are not followed",
                typeof(IArmorGetter), typeof(ILeveledItemGetter)),
            Skip("Global", ChanceGlobal)),

        // Terminal by ruling: a form list is in the set (a head part's valid-races list), and its
        // items, which can be any record, are never followed.
        Row<IFormListGetter, FormList>(
            Skip("Items", "terminal by ruling: a form list can hold any record, so its items are never followed")),
    };

    private static readonly Dictionary<Type, TypeEntry> ByGetterType =
        All.ToDictionary(e => e.GetterType);

    /// <summary>The row for a record type, or null when the type is terminal (no row).</summary>
    public static TypeEntry? Find(Type getterType) =>
        ByGetterType.TryGetValue(getterType, out var entry) ? entry : null;

    // ── Reflection over Mutagen's record classes (no plugin is read) ─────────────────────────

    private static readonly Assembly SkyrimAssembly = typeof(Npc).Assembly;

    private static readonly Lazy<Type[]> SkyrimTypes = new(() => SkyrimAssembly.GetTypes());

    /// <summary>Every concrete major-record class of Mutagen.Bethesda.Skyrim with its getter
    /// interface (Armor with IArmorGetter).</summary>
    private static readonly Lazy<IReadOnlyList<(Type ClassType, Type GetterType)>> RecordTypes = new(() =>
        SkyrimTypes.Value
            .Where(t => t.IsClass && !t.IsAbstract && typeof(SkyrimMajorRecord).IsAssignableFrom(t))
            .Select(c => (ClassType: c, GetterType: c.GetInterfaces().FirstOrDefault(i =>
                i.Namespace == c.Namespace && i.Name == "I" + c.Name + "Getter")))
            .Where(x => x.GetterType != null)
            .Select(x => (ClassType: x.ClassType, GetterType: x.GetterType!))
            .ToList());

    /// <summary>Concrete record class and getter interface of every major-record type.</summary>
    public static IReadOnlyList<(Type ClassType, Type GetterType)> AllRecordTypes => RecordTypes.Value;

    /// <summary>The type argument of the <c>IFormLinkGetter&lt;T&gt;</c> a type implements, or null.</summary>
    private static Type? LinkTarget(Type t)
    {
        foreach (var i in t.GetInterfaces().Append(t))
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>))
            {
                return i.GetGenericArguments()[0];
            }
        }
        return null;
    }

    private static void CollectLinkTargets(Type type, HashSet<Type> targets, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            Visit(p.PropertyType, targets, visited);
        }
    }

    /// <summary>Link targets found anywhere inside a member's type: nested objects, lists,
    /// gendered items, and every class that can stand in for a nested type.</summary>
    private static void Visit(Type t, HashSet<Type> targets, HashSet<Type> visited)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsArray)
        {
            Visit(t.GetElementType()!, targets, visited);
            return;
        }
        if (LinkTarget(t) is { } target)
        {
            targets.Add(target);
            return;
        }
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments()) Visit(arg, targets, visited);
        }
        if (t.Assembly != SkyrimAssembly || t.IsEnum || t.IsPrimitive) return;
        // A record held by value is not a link.
        if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return;

        CollectLinkTargets(t, targets, visited);
        // Whatever can stand in its place. Not only for abstract types: ScriptProperty is a
        // concrete class, and the link is on its subclass ScriptObjectProperty.
        if (!t.IsSealed)
        {
            foreach (var impl in SkyrimTypes.Value.Where(c =>
                         c != t && c.IsClass && !c.IsAbstract && t.IsAssignableFrom(c) &&
                         !typeof(IMajorRecordGetter).IsAssignableFrom(c)))
            {
                CollectLinkTargets(impl, targets, visited);
            }
        }
    }

    private static readonly ConcurrentDictionary<(Type, string), IReadOnlySet<Type>> MemberTargetCache = new();

    /// <summary>
    /// The record types (getter interfaces) a member of a record class can point at, through any
    /// FormLink nested anywhere inside it. Empty when the member bears no link or does not exist.
    /// </summary>
    public static IReadOnlySet<Type> LinkTargetsOfMember(Type recordClass, string memberName) =>
        MemberTargetCache.GetOrAdd((recordClass, memberName), key =>
        {
            var linkTargets = new HashSet<Type>();
            foreach (var p in key.Item1.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != key.Item2 || p.GetIndexParameters().Length != 0) continue;
                Visit(p.PropertyType, linkTargets, new HashSet<Type>());
            }

            // A link names an interface (IOutfitTargetGetter); the record types are the ones
            // that implement it (Armor, LeveledItem).
            var recordTypes = new HashSet<Type>();
            foreach (var linkTarget in linkTargets)
            {
                foreach (var (_, getterType) in RecordTypes.Value)
                {
                    if (linkTarget.IsAssignableFrom(getterType)) recordTypes.Add(getterType);
                }
            }
            return recordTypes;
        });

    /// <summary>Step 1 for one field: the record types an Override Roots field points at. The
    /// enum member's name is the Mutagen property name (see <c>NpcRootFieldCatalogTests</c>).</summary>
    public static IReadOnlySet<Type> RootFieldTargets(NpcRootField field) =>
        LinkTargetsOfMember(typeof(Npc), field.ToString());

    // ── The resolved set ─────────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, IReadOnlySet<Type>> ResolvedCache = new();

    /// <summary>
    /// The appearance record types for a set of ticked Override Roots fields: step 1 over each
    /// field, then closed over the declared table. Computed on first use and cached per set of
    /// roots; reads no plugin. The returned set is shared: do not cast and modify it.
    /// </summary>
    public static IReadOnlySet<Type> Resolve(IReadOnlySet<NpcRootField> roots)
    {
        var key = string.Join(",", roots.Select(r => (int)r).OrderBy(i => i));
        return ResolvedCache.GetOrAdd(key, _ => Compute(roots));
    }

    /// <summary>The set for one mod, from the Override Roots selection in force for it
    /// (<see cref="NpcRootFieldCatalog.Resolve"/>).</summary>
    public static IReadOnlySet<Type> Resolve(ModSetting? mod, Settings? settings) =>
        Resolve(NpcRootFieldCatalog.Resolve(mod, settings));

    private static IReadOnlySet<Type> Compute(IReadOnlySet<NpcRootField> roots)
    {
        var set = new HashSet<Type>();
        var queue = new Queue<Type>();
        foreach (var entry in NpcRootFieldCatalog.All)
        {
            if (!roots.Contains(entry.Field)) continue;
            foreach (var type in RootFieldTargets(entry.Field))
            {
                if (set.Add(type)) queue.Enqueue(type);
            }
        }

        while (queue.Count > 0)
        {
            // No row: terminal. Npc's row has no members: its links are the roots above.
            if (!ByGetterType.TryGetValue(queue.Dequeue(), out var row)) continue;
            foreach (var member in row.Members)
            {
                foreach (var type in member.Follows)
                {
                    if (set.Add(type)) queue.Enqueue(type);
                }
            }
        }

        return set;
    }

    /// <summary>Whether a record's type is in a resolved set.</summary>
    public static bool Contains(IReadOnlySet<Type> appearanceTypes, IMajorRecordGetter record) =>
        appearanceTypes.Contains(record.Registration.GetterType);
}
