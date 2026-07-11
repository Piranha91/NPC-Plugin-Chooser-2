using System.Collections.ObjectModel;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Pure / list / regex / file helpers on <see cref="VM_Mods"/> reachable without a live
/// Skyrim environment or the DI graph. The constructor is heavy (Autofac factories,
/// reactive subscriptions), so the instance methods are exercised on a
/// <see cref="Reflect.Uninitialized{T}"/> instance with only the fields each method reads
/// poked in: <c>_settings</c> and <c>_allModSettingsInternal</c>. The methods covered here
/// never touch <c>_environmentStateProvider</c>, the reactive pipeline, or any UI thread, so
/// no STA/RxApp setup is needed.
///
/// <para><see cref="VM_ModSetting"/> participants are likewise allocated without running their
/// heavy constructor: ReactiveUI.Fody renames each <c>[Reactive]</c> backing field with a
/// <c>$</c> prefix (e.g. <c>$DisplayName</c>, <c>$CorrespondingModKeys</c>), while plain
/// auto-properties keep <c>&lt;Name&gt;k__BackingField</c> — those exact names (verified
/// against the compiled assembly) are written via <see cref="Reflect.SetField"/>.</para>
///
/// Skipped (need env/DI/WPF, covered elsewhere): PopulateModSettingsAsync, RefreshNpcLists,
/// ApplyFilters, ScanForModsInModFolderAsync, AddBaseAndCreationClubMods, AnalyzeModSettingsAsync.
/// </summary>
public class VM_ModsPureTests
{
    // ------------------------------------------------------------------
    // Construction helpers (no production-code constructor invoked)
    // ------------------------------------------------------------------

    private static VM_Mods MakeVm(Settings? settings = null, List<VM_ModSetting>? internalList = null)
    {
        var vm = Reflect.Uninitialized<VM_Mods>();
        Reflect.SetField(vm, "_settings", settings ?? new Settings());
        Reflect.SetField(vm, "_allModSettingsInternal", internalList ?? new List<VM_ModSetting>());
        return vm;
    }

    /// <summary>
    /// Allocates a <see cref="VM_ModSetting"/> without its heavy ctor and seeds only the
    /// fields the VM_Mods list helpers read: DisplayName, the two collections, and the
    /// mugshot-only flag. Backing-field names match the compiled assembly (Fody '$' prefix
    /// for [Reactive] props; 'k__BackingField' for the plain bool).
    /// </summary>
    private static VM_ModSetting MakeModSetting(
        string displayName,
        IEnumerable<string>? folderPaths = null,
        IEnumerable<ModKey>? modKeys = null,
        bool isMugshotOnly = false)
    {
        var ms = Reflect.Uninitialized<VM_ModSetting>();
        Reflect.SetField(ms, "$DisplayName", displayName);
        Reflect.SetField(ms, "$CorrespondingFolderPaths",
            new ObservableCollection<string>(folderPaths ?? Enumerable.Empty<string>()));
        Reflect.SetField(ms, "$CorrespondingModKeys",
            new ObservableCollection<ModKey>(modKeys ?? Enumerable.Empty<ModKey>()));
        Reflect.SetField(ms, "<IsMugshotOnlyEntry>k__BackingField", isMugshotOnly);
        return ms;
    }

    // ==================================================================
    // MugshotNameRegex — static field, no instance required
    // ==================================================================

    [Theory]
    [InlineData("00ABCDEF.png")]
    [InlineData("00abcdef.PNG")]
    [InlineData("12345678.jpg")]
    [InlineData("DEADBEEF.jpeg")]
    [InlineData("00000000.bmp")]
    [InlineData("FfFfFfFf.JpG")] // mixed-case hex + mixed-case extension (IgnoreCase)
    public void MugshotNameRegex_Matches_EightHexDigitsWithImageExtension(string fileName)
    {
        VM_Mods.MugshotNameRegex.IsMatch(fileName).Should().BeTrue();
    }

    [Fact]
    public void MugshotNameRegex_CapturesHexGroup()
    {
        var m = VM_Mods.MugshotNameRegex.Match("00ABCDEF.png");
        m.Success.Should().BeTrue();
        m.Groups["hex"].Value.Should().Be("00ABCDEF");
    }

    [Theory]
    [InlineData("0ABCDEF.png")]    // 7 hex digits
    [InlineData("00ABCDEF0.png")]  // 9 hex digits
    [InlineData("0000000G.png")]   // 'G' is not a hex digit
    [InlineData("00ABCDEF.tga")]   // unsupported extension
    [InlineData("00ABCDEF.gif")]   // unsupported extension
    [InlineData("00ABCDEF")]       // no extension
    [InlineData("00ABCDEF.png.bak")] // trailing junk after extension
    [InlineData("prefix_00ABCDEF.png")] // leading prefix (anchored at start)
    [InlineData("Bob.png")]        // not hex at all
    [InlineData("")]               // empty
    public void MugshotNameRegex_Rejects_NonConformingNames(string fileName)
    {
        VM_Mods.MugshotNameRegex.IsMatch(fileName).Should().BeFalse();
    }

    // ==================================================================
    // SortVMs / SortVMsInPlace
    // ==================================================================

    [Fact]
    public void SortVMs_OrdersAlphabeticallyCaseInsensitive()
    {
        var vm = MakeVm();
        var inputs = new List<VM_ModSetting>
        {
            MakeModSetting("zebra"),
            MakeModSetting("Apple"),
            MakeModSetting("banana"),
        };

        var sorted = vm.SortVMs(inputs);

        sorted.Select(x => x.DisplayName).Should().Equal("Apple", "banana", "zebra");
    }

    [Fact]
    public void SortVMs_PinsBaseGameFirst_AndCreationClubSecond()
    {
        var vm = MakeVm();
        var inputs = new List<VM_ModSetting>
        {
            MakeModSetting("Zeta Mod"),
            MakeModSetting(VM_Mods.CreationClubModsettingName),
            MakeModSetting("Alpha Mod"),
            MakeModSetting(VM_Mods.BaseGameModSettingName),
        };

        var sorted = vm.SortVMs(inputs);

        // Base Game is inserted at index 0 LAST, so it wins index 0; Creation Club at index 1.
        sorted.Select(x => x.DisplayName).Should()
            .Equal(VM_Mods.BaseGameModSettingName, VM_Mods.CreationClubModsettingName, "Alpha Mod", "Zeta Mod");
    }

    [Fact]
    public void SortVMs_CreationClubWithoutBaseGame_GoesFirst()
    {
        var vm = MakeVm();
        var inputs = new List<VM_ModSetting>
        {
            MakeModSetting("Mod B"),
            MakeModSetting("Mod A"),
            MakeModSetting(VM_Mods.CreationClubModsettingName),
        };

        var sorted = vm.SortVMs(inputs);

        sorted.Select(x => x.DisplayName).Should()
            .Equal(VM_Mods.CreationClubModsettingName, "Mod A", "Mod B");
    }

    [Fact]
    public void SortVMs_NoReservedEntries_PurelyAlphabetical()
    {
        var vm = MakeVm();
        var inputs = new List<VM_ModSetting>
        {
            MakeModSetting("Gamma"),
            MakeModSetting("alpha"),
            MakeModSetting("Beta"),
        };

        vm.SortVMs(inputs).Select(x => x.DisplayName).Should().Equal("alpha", "Beta", "Gamma");
    }

    [Fact]
    public void SortVMs_EmptyInput_ReturnsEmpty()
    {
        var vm = MakeVm();
        vm.SortVMs(Enumerable.Empty<VM_ModSetting>()).Should().BeEmpty();
    }

    [Fact]
    public void SortVMs_DoesNotMutateCaller_AndReturnsNewList()
    {
        var vm = MakeVm();
        var inputs = new List<VM_ModSetting>
        {
            MakeModSetting("B"),
            MakeModSetting("A"),
        };

        var sorted = vm.SortVMs(inputs);

        sorted.Should().NotBeSameAs(inputs);
        inputs.Select(x => x.DisplayName).Should().Equal(new[] { "B", "A" }, "input order is untouched");
    }

    [Fact]
    public void SortVMsInPlace_ReordersInternalList_WithReservedPinning()
    {
        var internalList = new List<VM_ModSetting>
        {
            MakeModSetting("Yankee"),
            MakeModSetting(VM_Mods.CreationClubModsettingName),
            MakeModSetting("Echo"),
            MakeModSetting(VM_Mods.BaseGameModSettingName),
            MakeModSetting("alpha"),
        };
        var vm = MakeVm(internalList: internalList);

        vm.SortVMsInPlace();

        var result = Reflect.GetField<List<VM_ModSetting>>(vm, "_allModSettingsInternal");
        result.Select(x => x.DisplayName).Should()
            .Equal(VM_Mods.BaseGameModSettingName, VM_Mods.CreationClubModsettingName, "alpha", "Echo", "Yankee");
        // AllModSettings is the public read-only view over the same backing list.
        vm.AllModSettings.Select(x => x.DisplayName).Should()
            .Equal(VM_Mods.BaseGameModSettingName, VM_Mods.CreationClubModsettingName, "alpha", "Echo", "Yankee");
    }

    [Fact]
    public void SortVMsInPlace_PreservesElementIdentity()
    {
        var keep = MakeModSetting("Middle");
        var internalList = new List<VM_ModSetting>
        {
            MakeModSetting("Zulu"),
            keep,
            MakeModSetting("Alpha"),
        };
        var vm = MakeVm(internalList: internalList);

        vm.SortVMsInPlace();

        var result = Reflect.GetField<List<VM_ModSetting>>(vm, "_allModSettingsInternal");
        result.Should().Contain(keep);
        result.Single(x => x.DisplayName == "Middle").Should().BeSameAs(keep);
    }

    // ==================================================================
    // AddGuestAppearanceToSettings (private; reads/writes _settings.GuestAppearances)
    // ==================================================================

    private static bool AddGuest(VM_Mods vm, FormKey target, FormKey guest, string modName, string displayStr) =>
        Reflect.Invoke<bool>(vm, "AddGuestAppearanceToSettings", target, guest, modName, displayStr);

    [Fact]
    public void AddGuestAppearance_CreatesEntry_ForNewTarget()
    {
        var settings = new Settings();
        var vm = MakeVm(settings);
        var target = MutagenFixtures.Fk("000801:Skyrim.esm");
        var guest = MutagenFixtures.Fk("000ABC:Cool.esp");

        var added = AddGuest(vm, target, guest, "Cool Mod", "Lydia");

        added.Should().BeTrue();
        settings.GuestAppearances.Should().ContainKey(target);
        settings.GuestAppearances[target].Should().ContainSingle()
            .Which.Should().Be(("Cool Mod", guest, "Lydia"));
    }

    [Fact]
    public void AddGuestAppearance_DuplicateTuple_IsIgnored()
    {
        var settings = new Settings();
        var vm = MakeVm(settings);
        var target = MutagenFixtures.Fk("000801:Skyrim.esm");
        var guest = MutagenFixtures.Fk("000ABC:Cool.esp");

        AddGuest(vm, target, guest, "Cool Mod", "Lydia").Should().BeTrue();
        var second = AddGuest(vm, target, guest, "Cool Mod", "Lydia");

        second.Should().BeFalse("the (ModName, FormKey, DisplayName) tuple already exists in the set");
        settings.GuestAppearances[target].Should().HaveCount(1);
    }

    [Fact]
    public void AddGuestAppearance_TwoDistinctGuests_ForSameTarget_BothStored()
    {
        var settings = new Settings();
        var vm = MakeVm(settings);
        var target = MutagenFixtures.Fk("000801:Skyrim.esm");
        var guestA = MutagenFixtures.Fk("000ABC:ModA.esp");
        var guestB = MutagenFixtures.Fk("000DEF:ModB.esp");

        AddGuest(vm, target, guestA, "Mod A", "Lydia-A").Should().BeTrue();
        AddGuest(vm, target, guestB, "Mod B", "Lydia-B").Should().BeTrue();

        settings.GuestAppearances[target].Should().BeEquivalentTo(new[]
        {
            ("Mod A", guestA, "Lydia-A"),
            ("Mod B", guestB, "Lydia-B"),
        });
    }

    [Fact]
    public void AddGuestAppearance_DistinctTargets_GetSeparateSets()
    {
        var settings = new Settings();
        var vm = MakeVm(settings);
        var targetA = MutagenFixtures.Fk("000801:Skyrim.esm");
        var targetB = MutagenFixtures.Fk("000901:Skyrim.esm");
        var guest = MutagenFixtures.Fk("000ABC:Cool.esp");

        AddGuest(vm, targetA, guest, "Cool Mod", "NpcA").Should().BeTrue();
        AddGuest(vm, targetB, guest, "Cool Mod", "NpcB").Should().BeTrue();

        settings.GuestAppearances.Should().HaveCount(2);
        settings.GuestAppearances[targetA].Should().ContainSingle().Which.Item3.Should().Be("NpcA");
        settings.GuestAppearances[targetB].Should().ContainSingle().Which.Item3.Should().Be("NpcB");
    }

    [Fact]
    public void AddGuestAppearance_SameGuestKey_DifferentDisplayName_BothStored()
    {
        // The set key is the whole tuple, so a differing display string is a distinct entry.
        var settings = new Settings();
        var vm = MakeVm(settings);
        var target = MutagenFixtures.Fk("000801:Skyrim.esm");
        var guest = MutagenFixtures.Fk("000ABC:Cool.esp");

        AddGuest(vm, target, guest, "Cool Mod", "Lydia").Should().BeTrue();
        AddGuest(vm, target, guest, "Cool Mod", "Lydia (variant)").Should().BeTrue();

        settings.GuestAppearances[target].Should().HaveCount(2);
    }

    // ==================================================================
    // FinalizeModList (private; no instance fields read)
    // ==================================================================

    private static void FinalizeList(VM_Mods vm, List<VM_ModSetting> tempList, List<VM_ModSetting> mugshotOnly) =>
        Reflect.InvokeVoid(vm, "FinalizeModList", tempList, mugshotOnly);

    [Fact]
    public void FinalizeModList_AppendsMugshotOnlyEntry_WhenNameIsUnique()
    {
        var vm = MakeVm();
        var tempList = new List<VM_ModSetting> { MakeModSetting("Existing") };
        var mugshotOnly = new List<VM_ModSetting> { MakeModSetting("Brand New", isMugshotOnly: true) };

        FinalizeList(vm, tempList, mugshotOnly);

        tempList.Select(x => x.DisplayName).Should().Equal("Existing", "Brand New");
    }

    [Fact]
    public void FinalizeModList_SkipsMugshotOnlyEntry_WhenNameAlreadyPresent_CaseInsensitive()
    {
        var vm = MakeVm();
        var tempList = new List<VM_ModSetting> { MakeModSetting("Apachii") };
        var mugshotOnly = new List<VM_ModSetting> { MakeModSetting("apachii", isMugshotOnly: true) };

        FinalizeList(vm, tempList, mugshotOnly);

        tempList.Should().ContainSingle("the duplicate (case-insensitive) name is not re-added");
        tempList[0].DisplayName.Should().Be("Apachii");
    }

    [Fact]
    public void FinalizeModList_ClearsMugshotOnlyFlag_WhenEntryNowHasFolderPaths()
    {
        var vm = MakeVm();
        var withFolder = MakeModSetting("HasFolder", folderPaths: new[] { @"C:\Mods\HasFolder" }, isMugshotOnly: true);
        var tempList = new List<VM_ModSetting> { withFolder };

        FinalizeList(vm, tempList, new List<VM_ModSetting>());

        withFolder.IsMugshotOnlyEntry.Should().BeFalse("an entry with folder paths is no longer mugshot-only");
    }

    [Fact]
    public void FinalizeModList_ClearsMugshotOnlyFlag_WhenEntryNowHasModKeys()
    {
        var vm = MakeVm();
        var withKey = MakeModSetting("HasKey", modKeys: new[] { MutagenFixtures.Mk("Plug.esp") }, isMugshotOnly: true);
        var tempList = new List<VM_ModSetting> { withKey };

        FinalizeList(vm, tempList, new List<VM_ModSetting>());

        withKey.IsMugshotOnlyEntry.Should().BeFalse("an entry with mod keys is no longer mugshot-only");
    }

    [Fact]
    public void FinalizeModList_LeavesFlag_WhenEntryHasNeitherFolderNorKey()
    {
        var vm = MakeVm();
        var pure = MakeModSetting("PureMugshot", isMugshotOnly: true);
        var tempList = new List<VM_ModSetting> { pure };

        FinalizeList(vm, tempList, new List<VM_ModSetting>());

        pure.IsMugshotOnlyEntry.Should().BeTrue("a genuinely mugshot-only entry keeps its flag");
    }

    [Fact]
    public void FinalizeModList_AppendedMugshotEntryWithFolder_GetsFlagCleared()
    {
        // A unique mugshot-only entry that also carries folder paths is appended AND
        // demoted from mugshot-only in the same pass.
        var vm = MakeVm();
        var tempList = new List<VM_ModSetting> { MakeModSetting("Existing") };
        var newcomer = MakeModSetting("Newcomer", folderPaths: new[] { @"C:\m\Newcomer" }, isMugshotOnly: true);

        FinalizeList(vm, tempList, new List<VM_ModSetting> { newcomer });

        tempList.Should().Contain(newcomer);
        newcomer.IsMugshotOnlyEntry.Should().BeFalse();
    }

    // ==================================================================
    // UpgradeVmWithPathAndPlugins (private; no instance fields read)
    // ==================================================================

    private static void Upgrade(VM_Mods vm, VM_ModSetting target, string folder, List<ModKey> keys) =>
        Reflect.InvokeVoid(vm, "UpgradeVmWithPathAndPlugins", target, folder, keys);

    [Fact]
    public void Upgrade_AddsFolderPathAndKeys_WhenAbsent()
    {
        var vm = MakeVm();
        var target = MakeModSetting("Target");
        var keys = new List<ModKey> { MutagenFixtures.Mk("A.esp"), MutagenFixtures.Mk("B.esp") };

        Upgrade(vm, target, @"C:\Mods\Target", keys);

        target.CorrespondingFolderPaths.Should().Equal(@"C:\Mods\Target");
        target.CorrespondingModKeys.Should().BeEquivalentTo(new[] { MutagenFixtures.Mk("A.esp"), MutagenFixtures.Mk("B.esp") });
    }

    [Fact]
    public void Upgrade_DoesNotDuplicateFolderPath_CaseInsensitive()
    {
        var vm = MakeVm();
        var target = MakeModSetting("Target", folderPaths: new[] { @"C:\Mods\Target" });

        Upgrade(vm, target, @"c:\mods\target", new List<ModKey>());

        target.CorrespondingFolderPaths.Should().ContainSingle("the path matches case-insensitively");
    }

    [Fact]
    public void Upgrade_AddsSecondDistinctFolderPath()
    {
        var vm = MakeVm();
        var target = MakeModSetting("Target", folderPaths: new[] { @"C:\Mods\Target" });

        Upgrade(vm, target, @"C:\Mods\TargetExtra", new List<ModKey>());

        target.CorrespondingFolderPaths.Should().Equal(@"C:\Mods\Target", @"C:\Mods\TargetExtra");
    }

    [Fact]
    public void Upgrade_DoesNotDuplicateExistingModKey()
    {
        var vm = MakeVm();
        var existing = MutagenFixtures.Mk("A.esp");
        var target = MakeModSetting("Target", modKeys: new[] { existing });

        Upgrade(vm, target, @"C:\Mods\Target",
            new List<ModKey> { existing, MutagenFixtures.Mk("B.esp") });

        target.CorrespondingModKeys.Should().HaveCount(2);
        target.CorrespondingModKeys.Should().BeEquivalentTo(new[] { existing, MutagenFixtures.Mk("B.esp") });
    }

    [Fact]
    public void Upgrade_EmptyKeyList_OnlyAddsFolder()
    {
        var vm = MakeVm();
        var target = MakeModSetting("Target");

        Upgrade(vm, target, @"C:\Mods\Target", new List<ModKey>());

        target.CorrespondingFolderPaths.Should().Equal(@"C:\Mods\Target");
        target.CorrespondingModKeys.Should().BeEmpty();
    }

    // ==================================================================
    // GetEnabledModNamesFromModlist (private; reads MO2 settings + a real file)
    // ==================================================================

    private static HashSet<string>? GetEnabled(VM_Mods vm) =>
        Reflect.Invoke<HashSet<string>>(vm, "GetEnabledModNamesFromModlist");

    [Fact]
    public void GetEnabledModNames_FilterDisabled_ReturnsNull()
    {
        var settings = new Settings { FilterByActiveModsMO2 = false, MO2ModlistPath = "anything" };
        GetEnabled(MakeVm(settings)).Should().BeNull("MO2 filtering is off");
    }

    [Fact]
    public void GetEnabledModNames_BlankPath_ReturnsNull()
    {
        var settings = new Settings { FilterByActiveModsMO2 = true, MO2ModlistPath = "   " };
        GetEnabled(MakeVm(settings)).Should().BeNull();
    }

    [Fact]
    public void GetEnabledModNames_MissingFile_ReturnsNull()
    {
        var settings = new Settings
        {
            FilterByActiveModsMO2 = true,
            MO2ModlistPath = @"C:\definitely\does\not\exist\modlist.txt",
        };
        GetEnabled(MakeVm(settings)).Should().BeNull();
    }

    [Fact]
    public void GetEnabledModNames_ReturnsOnlyPlusLines_TrimmedAndMinusLinesExcluded()
    {
        using var dir = new TempDir("modlist");
        var modlist = dir.WriteText("modlist.txt",
            "# This is an MO2 modlist\n" +
            "+Enabled Mod One\n" +
            "-Disabled Mod\n" +
            "+ Enabled Mod Two \n" + // leading/trailing whitespace must be trimmed
            "-Another Disabled\n" +
            "*Some Separator\n" +
            "+Enabled Mod Three\n");
        var settings = new Settings { FilterByActiveModsMO2 = true, MO2ModlistPath = modlist };

        var result = GetEnabled(MakeVm(settings));

        result.Should().NotBeNull();
        result!.Should().BeEquivalentTo(new[] { "Enabled Mod One", "Enabled Mod Two", "Enabled Mod Three" });
    }

    [Fact]
    public void GetEnabledModNames_SetIsCaseInsensitive()
    {
        using var dir = new TempDir("modlist");
        var modlist = dir.WriteText("modlist.txt", "+Apachii Hair\n-Disabled\n");
        var settings = new Settings { FilterByActiveModsMO2 = true, MO2ModlistPath = modlist };

        var result = GetEnabled(MakeVm(settings));

        result.Should().NotBeNull();
        result!.Contains("apachii hair").Should().BeTrue("the HashSet uses OrdinalIgnoreCase");
    }

    [Fact]
    public void GetEnabledModNames_AllDisabled_ReturnsEmptySet()
    {
        using var dir = new TempDir("modlist");
        var modlist = dir.WriteText("modlist.txt", "-One\n-Two\n# comment\n");
        var settings = new Settings { FilterByActiveModsMO2 = true, MO2ModlistPath = modlist };

        var result = GetEnabled(MakeVm(settings));

        result.Should().NotBeNull().And.BeEmpty();
    }

    // ==================================================================
    // GetModDirectories (private; reads ModsFolder + the MO2 filter above)
    // ==================================================================

    private static List<string> GetDirs(VM_Mods vm) =>
        Reflect.Invoke<List<string>>(vm, "GetModDirectories")!;

    [Fact]
    public void GetModDirectories_BlankModsFolder_ReturnsEmpty()
    {
        var settings = new Settings { ModsFolder = "   " };
        GetDirs(MakeVm(settings)).Should().BeEmpty();
    }

    [Fact]
    public void GetModDirectories_NonexistentModsFolder_ReturnsEmpty()
    {
        var settings = new Settings { ModsFolder = @"C:\no\such\mods\folder\at\all" };
        GetDirs(MakeVm(settings)).Should().BeEmpty();
    }

    [Fact]
    public void GetModDirectories_NoMo2Filter_ReturnsAllSubdirectories()
    {
        using var dir = new TempDir("mods");
        dir.Dir("Mod A");
        dir.Dir("Mod B");
        dir.Dir("Mod C");
        var settings = new Settings { ModsFolder = dir.Path, FilterByActiveModsMO2 = false };

        var result = GetDirs(MakeVm(settings));

        result.Select(System.IO.Path.GetFileName).Should().BeEquivalentTo(new[] { "Mod A", "Mod B", "Mod C" });
    }

    [Fact]
    public void GetModDirectories_WithMo2Filter_ReturnsOnlyEnabledFolders()
    {
        using var modsDir = new TempDir("mods");
        modsDir.Dir("Enabled One");
        modsDir.Dir("Disabled One");
        modsDir.Dir("Enabled Two");

        using var listDir = new TempDir("list");
        var modlist = listDir.WriteText("modlist.txt",
            "+Enabled One\n-Disabled One\n+Enabled Two\n+Not On Disk\n");

        var settings = new Settings
        {
            ModsFolder = modsDir.Path,
            FilterByActiveModsMO2 = true,
            MO2ModlistPath = modlist,
        };

        var result = GetDirs(MakeVm(settings));

        result.Select(System.IO.Path.GetFileName).Should()
            .BeEquivalentTo(new[] { "Enabled One", "Enabled Two" },
                "only on-disk folders that are also '+enabled' in the modlist pass");
    }

    [Fact]
    public void GetModDirectories_Mo2FilterMatchesCaseInsensitively()
    {
        using var modsDir = new TempDir("mods");
        modsDir.Dir("MixedCase Mod");

        using var listDir = new TempDir("list");
        var modlist = listDir.WriteText("modlist.txt", "+mixedcase mod\n");

        var settings = new Settings
        {
            ModsFolder = modsDir.Path,
            FilterByActiveModsMO2 = true,
            MO2ModlistPath = modlist,
        };

        var result = GetDirs(MakeVm(settings));

        result.Select(System.IO.Path.GetFileName).Should().ContainSingle().Which.Should().Be("MixedCase Mod");
    }

    [Fact]
    public void GetModDirectories_Mo2FilterEnabled_ButMissingFile_FallsBackToAll()
    {
        // GetEnabledModNamesFromModlist returns null when the file is missing, and
        // GetModDirectories treats null as "no filter" -> returns every subdirectory.
        using var modsDir = new TempDir("mods");
        modsDir.Dir("Mod A");
        modsDir.Dir("Mod B");

        var settings = new Settings
        {
            ModsFolder = modsDir.Path,
            FilterByActiveModsMO2 = true,
            MO2ModlistPath = @"C:\missing\modlist.txt",
        };

        var result = GetDirs(MakeVm(settings));

        result.Select(System.IO.Path.GetFileName).Should().BeEquivalentTo(new[] { "Mod A", "Mod B" });
    }

    // ------------------------------------------------------------------
    // CollectBaseGameAssetOverlaps (static; base-game-overwrite scan core)
    // ------------------------------------------------------------------

    private static readonly IReadOnlySet<string> VanillaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"textures\actors\character\female\femalebody_1.dds",
        @"textures\actors\character\female\femalehead.dds",
        @"meshes\actors\character\character assets\femalebody_1.nif",
        // Vanilla FaceGen lives in the vanilla BSAs; the scan must NOT flag it.
        @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\0001A696.nif",
    };

    [Fact]
    public void CollectOverlaps_FlagsVanillaCollisions_IgnoresNewFiles()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        VM_Mods.CollectBaseGameAssetOverlaps(new[]
        {
            @"textures\actors\character\female\femalebody_1.dds",   // collision
            @"textures\actors\character\KSHairdos\hair01.dds",      // new file — no collision
            @"MyMod.esp",                                           // plugin at data root — no collision
        }, VanillaPaths, results);

        results.Should().BeEquivalentTo(new[] { @"textures\actors\character\female\femalebody_1.dds" });
    }

    [Fact]
    public void CollectOverlaps_ExcludesFaceGenPaths()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        VM_Mods.CollectBaseGameAssetOverlaps(new[]
        {
            @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\0001A696.nif",
        }, VanillaPaths, results);

        results.Should().BeEmpty();
    }

    [Fact]
    public void CollectOverlaps_NormalizesForwardSlashes_AndDedupes()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Same file seen twice (e.g. loose + packed in the mod's BSA), one with forward
        // slashes and different casing — must normalize and collapse to a single entry.
        VM_Mods.CollectBaseGameAssetOverlaps(new[]
        {
            "textures/actors/character/female/femalehead.dds",
            @"Textures\Actors\Character\Female\FemaleHead.dds",
        }, VanillaPaths, results);

        results.Should().ContainSingle();
    }

    [Fact]
    public void CollectOverlaps_EmptyAndNullCandidates_AreIgnored()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        VM_Mods.CollectBaseGameAssetOverlaps(new[] { "", null! }, VanillaPaths, results);

        results.Should().BeEmpty();
    }

    [Fact]
    public void CollectOverlaps_AccumulatesAcrossCalls()
    {
        // The scan calls this once per mod folder and once per BSA content set, all into the
        // same results set.
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        VM_Mods.CollectBaseGameAssetOverlaps(
            new[] { @"textures\actors\character\female\femalebody_1.dds" }, VanillaPaths, results);
        VM_Mods.CollectBaseGameAssetOverlaps(
            new[] { @"meshes\actors\character\character assets\femalebody_1.nif" }, VanillaPaths, results);

        results.Should().HaveCount(2);
    }
}
