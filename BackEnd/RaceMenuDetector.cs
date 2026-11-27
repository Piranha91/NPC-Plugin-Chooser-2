using System.IO;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// What RaceMenu does to worn hair-slot items in the installation being patched: whether its
/// SKSE plugin is present, whether its hair-slot tint feature is on, and the slot mask it tests.
/// Produced by <see cref="RaceMenuDetector"/>; consumed by <see cref="WigHairTintDecision"/>.
/// </summary>
/// <param name="Installed">The plugin DLL exists (<see cref="DllDataRelativePath"/>).</param>
/// <param name="TintEnabled"><c>bEnableTintHairSlot</c> in ini section <c>[Features]</c>, or the
/// code default when the ini or the key is absent.</param>
/// <param name="SlotMask"><c>uTintHairSlot</c> in ini section <c>[General]</c>, or the code
/// default. Bit 0 is biped slot 30, so 2050 is Hair (31) plus LongHair (41).</param>
/// <param name="DllDataRelativePath">The Data-relative DLL path that was looked for.</param>
/// <param name="DllPath">Where the DLL was found; null when it was not.</param>
/// <param name="IniPathsRead">The ini files that existed and were read, in the order RaceMenu
/// reads them (a later file overrides an earlier one, key by key).</param>
public sealed record RaceMenuHairTintState(
    bool Installed,
    bool TintEnabled,
    uint SlotMask,
    string DllDataRelativePath,
    string? DllPath,
    IReadOnlyList<string> IniPathsRead)
{
    /// <summary><c>bool g_enableTintHairSlot = true;</c> in skee64/main.cpp.</summary>
    public const bool DefaultTintEnabled = true;

    /// <summary><c>UInt32 g_tintHairSlot = 1;</c> in skee64/main.cpp. That is biped slot 30
    /// (Head), NOT the hair slots: the shipped <c>skee64.ini</c> is what sets 2050. An
    /// installation whose ini lacks the key therefore tints no hair-slot wig.</summary>
    public const uint DefaultSlotMask = 1;

    public static RaceMenuHairTintState NotInstalled(string dllDataRelativePath) =>
        new(false, DefaultTintEnabled, DefaultSlotMask, dllDataRelativePath, null, Array.Empty<string>());

    /// <summary>
    /// RaceMenu's own test (<c>TintMaskInterface::OnAttach</c>): a worn item is tinted when
    /// <c>(armorSlotMask &amp; addonSlotMask &amp; uTintHairSlot) != 0</c>. The item's existing
    /// tint colour is never consulted. False when RaceMenu is absent or the feature is off.
    /// </summary>
    public bool TintsWornItem(uint armorSlotMask, uint addonSlotMask) =>
        Installed && TintEnabled && (armorSlotMask & addonSlotMask & SlotMask) != 0;

    /// <summary>One line for the run log: installed or not, the setting, the mask, the files.</summary>
    public string Describe()
    {
        if (!Installed)
        {
            return $"RaceMenu is not installed ({DllDataRelativePath} was not found), so worn wigs " +
                   "are not tinted in game.";
        }

        string files = IniPathsRead.Count > 0
            ? "read " + string.Join(", ", IniPathsRead)
            : "no ini found, code defaults used";
        return $"RaceMenu is installed ({DllPath}); hair-slot tint is {(TintEnabled ? "ON" : "OFF")} " +
               $"(bEnableTintHairSlot), slot mask {SlotMask} ({DescribeSlots(SlotMask)}) (uTintHairSlot); {files}.";
    }

    /// <summary>"slots 31, 41" for 2050; "no slots" for 0.</summary>
    public static string DescribeSlots(uint slotMask)
    {
        var slots = Enumerable.Range(0, 32).Where(bit => (slotMask >> bit & 1) != 0).Select(bit => 30 + bit).ToList();
        return slots.Count == 0 ? "no slots" : (slots.Count == 1 ? "slot " : "slots ") + string.Join(", ", slots);
    }
}

/// <summary>
/// The one decision behind <see cref="WigHairTintMode"/>: does the bake write the NPC's hair
/// colour into a converted wig? Pure, so the whole table is unit-tested.
/// </summary>
public static class WigHairTintDecision
{
    /// <param name="mode">The effective mode for this NPC
    /// (<see cref="Settings.GetEffectiveWigHairTintMode(ModSetting, Mutagen.Bethesda.Plugins.FormKey)"/>).</param>
    /// <param name="raceMenu">What detection found for this run.</param>
    /// <param name="carrierArmorSlotMask">Biped slots of the ARMOR the wig came from: the outfit
    /// item, or the skin for a skin-carried wig. 0 when the record has no body template.</param>
    /// <param name="carrierAddonSlotMask">Biped slots of the wig's ArmorAddon.</param>
    public static bool ShouldTint(WigHairTintMode mode, RaceMenuHairTintState raceMenu,
        uint carrierArmorSlotMask, uint carrierAddonSlotMask)
    {
        return mode switch
        {
            WigHairTintMode.Always => true,
            WigHairTintMode.Never => false,
            _ => raceMenu.TintsWornItem(carrierArmorSlotMask, carrierAddonSlotMask),
        };
    }
}

/// <summary>
/// Finds out whether RaceMenu would tint a worn wig in the installation being patched, by reading
/// what RaceMenu itself reads. Rules taken from RaceMenu's source
/// (github.com/expired6978/SKSE64Plugins, skee64/main.cpp and skee64/TintMaskInterface.cpp):
/// <list type="bullet">
/// <item>Installed means the SKSE plugin DLL exists: <c>SKSE\Plugins\skee64.dll</c>, or
/// <c>skeevr.dll</c> on a VR release. VR mod lists commonly ship BOTH mods, so the release, not
/// the files present, picks which one counts.</item>
/// <item>Values come from <c>skee64.ini</c>; <c>skee64_custom.ini</c> is read second and wins
/// key by key, but only when it has a non-empty value for that key
/// (<c>SKEE64GetConfigOption</c>).</item>
/// <item>An absent ini, an absent or empty key, or a value that does not start with a number
/// leaves the code default (<see cref="RaceMenuHairTintState.DefaultTintEnabled"/>,
/// <see cref="RaceMenuHairTintState.DefaultSlotMask"/>). The value is parsed as
/// <c>sscanf("%u")</c> does, so the shipped <c>bEnableTintHairSlot=1 ; Default[1]</c> reads as 1;
/// the flag is on when the number is above 0.</item>
/// </list>
/// <para><b>VR.</b> The VR port's source was not available, so its code defaults are assumed to
/// equal the SE ones and only <c>skeevr.ini</c> is read: no custom-ini rule is applied to it.</para>
/// </summary>
public class RaceMenuDetector
{
    public const string PluginsFolder = @"SKSE\Plugins";
    public const string FeatureSection = "Features";
    public const string FeatureKey = "bEnableTintHairSlot";
    public const string SlotSection = "General";
    public const string SlotKey = "uTintHairSlot";

    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly object _lock = new();
    private RaceMenuHairTintState? _state;

    public RaceMenuDetector(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    /// <summary>
    /// Test seam, never set by the app. Maps a Data-relative path to the file that holds it, or
    /// null when nothing does. Launched through a mod manager, the game Data folder is a virtual
    /// one that shows every enabled mod's files, so the default (look under
    /// <see cref="EnvironmentStateProvider.DataFolderPath"/>) finds RaceMenu wherever it is
    /// installed. A headless build has only the real Data folder and supplies here where the
    /// profile's files really are (compare <see cref="PluginProvider.VirtualDataPluginPaths"/>).
    /// </summary>
    internal Func<string, string?>? DataRelativeFileResolver { get; set; }

    /// <summary>What detection found, read once and kept until <see cref="Refresh"/>.</summary>
    public RaceMenuHairTintState State
    {
        get
        {
            lock (_lock)
            {
                return _state ??= Detect(_environmentStateProvider.SkyrimVersion, ResolveDataRelativeFile);
            }
        }
    }

    /// <summary>Reads the files again. The patcher calls this once at the start of a run, so a
    /// run reflects the ini as it is then and every NPC of the run gets the same answer.</summary>
    public RaceMenuHairTintState Refresh()
    {
        lock (_lock)
        {
            _state = Detect(_environmentStateProvider.SkyrimVersion, ResolveDataRelativeFile);
            return _state;
        }
    }

    private string? ResolveDataRelativeFile(string dataRelativePath)
    {
        if (DataRelativeFileResolver != null) return DataRelativeFileResolver(dataRelativePath);
        if (!_environmentStateProvider.DataFolderPath.Exists) return null;
        var candidate = Path.Combine(_environmentStateProvider.DataFolderPath, dataRelativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>True for the releases whose RaceMenu is the VR port (<c>skeevr.*</c>).</summary>
    public static bool IsVrRelease(SkyrimRelease release) => release == SkyrimRelease.SkyrimVR;

    /// <summary>
    /// Detection against any file source. <paramref name="resolveDataRelativeFile"/> maps a
    /// Data-relative path (<c>SKSE\Plugins\skee64.dll</c>) to an existing file, or null.
    /// </summary>
    public static RaceMenuHairTintState Detect(SkyrimRelease release,
        Func<string, string?> resolveDataRelativeFile)
    {
        bool vr = IsVrRelease(release);
        string baseName = vr ? "skeevr" : "skee64";
        string dllRel = Path.Combine(PluginsFolder, baseName + ".dll");

        string? dllPath = resolveDataRelativeFile(dllRel);
        if (dllPath == null) return RaceMenuHairTintState.NotInstalled(dllRel);

        // skee64 reads <name>.ini and then <name>_custom.ini. The VR port's rule is unverified,
        // so only its base ini is read.
        var iniRels = vr
            ? new[] { Path.Combine(PluginsFolder, baseName + ".ini") }
            : new[]
            {
                Path.Combine(PluginsFolder, baseName + ".ini"),
                Path.Combine(PluginsFolder, baseName + "_custom.ini"),
            };
        var iniPaths = iniRels.Select(resolveDataRelativeFile).Where(p => p != null).Select(p => p!).ToList();

        bool enabled = RaceMenuHairTintState.DefaultTintEnabled;
        if (TryParseLeadingUnsigned(ReadEffectiveValue(iniPaths, FeatureSection, FeatureKey), out var flag))
        {
            enabled = flag > 0;
        }

        uint slotMask = RaceMenuHairTintState.DefaultSlotMask;
        if (TryParseLeadingUnsigned(ReadEffectiveValue(iniPaths, SlotSection, SlotKey), out var parsedMask))
        {
            slotMask = parsedMask;
        }

        return new RaceMenuHairTintState(true, enabled, slotMask, dllRel, dllPath, iniPaths);
    }

    /// <summary>The value RaceMenu would take for one key: the first ini's, replaced by a later
    /// ini's whenever that one has a non-empty value. Null when no file supplies one.</summary>
    internal static string? ReadEffectiveValue(IEnumerable<string> iniPathsInReadOrder, string section, string key)
    {
        string? result = null;
        foreach (var iniPath in iniPathsInReadOrder)
        {
            var value = ReadIniValue(iniPath, section, key);
            if (!string.IsNullOrEmpty(value)) result = value;
        }
        return result;
    }

    /// <summary>
    /// <c>GetPrivateProfileString</c>, as far as these files need it: section and key match
    /// without regard to case, the first matching key of the first matching section wins,
    /// whitespace around the value is dropped, a value wrapped in matching quotes loses them, and
    /// a line starting with ';' is a comment. A comment AFTER a value is not stripped (Windows
    /// does not strip it either; the number parse stops at it). Null when the file, section or
    /// key is absent or the file cannot be read.
    /// </summary>
    internal static string? ReadIniValue(string iniPath, string section, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(iniPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        bool inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[')
            {
                if (inSection) return null; // left the first matching section without finding the key
                int close = line.IndexOf(']');
                if (close < 0) continue;
                inSection = string.Equals(line.Substring(1, close - 1).Trim(), section,
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line.Substring(eq + 1).Trim();
            if (value.Length >= 2 &&
                (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value.Substring(1, value.Length - 2);
            }
            return value;
        }
        return null;
    }

    /// <summary><c>sscanf(value, "%u")</c>: leading whitespace, an optional sign, then decimal
    /// digits; anything after them is ignored. False when no digit is found.</summary>
    internal static bool TryParseLeadingUnsigned(string? value, out uint parsed)
    {
        parsed = 0;
        if (string.IsNullOrEmpty(value)) return false;
        int i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        bool negative = false;
        if (i < value.Length && (value[i] == '+' || value[i] == '-'))
        {
            negative = value[i] == '-';
            i++;
        }

        int firstDigit = i;
        ulong acc = 0;
        while (i < value.Length && value[i] >= '0' && value[i] <= '9')
        {
            acc = Math.Min(acc * 10 + (ulong)(value[i] - '0'), uint.MaxValue);
            i++;
        }
        if (i == firstDigit) return false;

        parsed = negative ? unchecked((uint)(-(long)acc)) : (uint)acc;
        return true;
    }
}
