namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// A FormLink-bearing field of an NPC record, as the user sees it in xEdit. Identifies one
/// possible ROOT for the Include/Include-As-New override-discovery walk: the patcher starts
/// traversing from the links in the checked fields and follows them to any depth, so this
/// controls where the search begins, never which record types it may pass through.
///
/// <para>Persisted by name in <c>Settings.json</c> (per mod, and as a global default), so these
/// names are part of the settings format — rename one and existing selections stop resolving.
/// New members can be appended freely; <c>NpcRootFieldCatalog</c> has a reflection test that
/// fails if an NPC field exists with no member here, which is what keeps this list honest as
/// Mutagen's record definition evolves.</para>
/// </summary>
public enum NpcRootField
{
    // --- Appearance: on by default -------------------------------------------------
    Race,               // RNAM
    WornArmor,          // WNAM
    HeadTexture,        // FTST
    HairColor,          // HCLF
    HeadParts,          // PNAM
    DefaultOutfit,      // DOFT
    SleepingOutfit,     // SOFT
    Template,           // TPLT

    // --- Everything else: off by default --------------------------------------------
    VirtualMachineAdapter,              // VMAD
    Factions,                           // SNAM
    DeathItem,                          // INAM
    Voice,                              // VTCK
    ActorEffect,                        // SPLO
    Destructible,                       // DEST
    FarAwayModel,                       // ANAM
    AttackRace,                         // ATKR
    Attacks,                            // ATKD
    SpectatorOverridePackageList,       // SPOR
    ObserveDeadBodyOverridePackageList, // OCOR
    GuardWarnOverridePackageList,       // GWOR
    CombatOverridePackageList,          // ECOR
    Perks,                              // PRKR
    Items,                              // CNTO
    Packages,                           // PKID
    Keywords,                           // KWDA
    Class,                              // CNAM
    CombatStyle,                        // ZNAM
    GiftFilter,                         // GNAM
    Sound,                              // CSDT + CSCR (one polymorphic field in Mutagen)
    DefaultPackageList,                 // DPLT
    CrimeFaction,                       // CRIF
}
