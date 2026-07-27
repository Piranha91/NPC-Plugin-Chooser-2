using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Decides, per plugin, whether records from that plugin may be deep-copied ("merged in")
/// into the output patch.
///
/// <para>Merge-in used to be a single per-mod switch, which broke whenever one mod entry
/// bundled plugins with opposite needs. The motivating case: an appearance mod whose NPC
/// records live in a plugin that stays in the load order (so its records must NOT be merged)
/// but which references a race and head parts from a bundled *resource-only* plugin that is
/// NOT in the load order (so its records MUST be merged, or the output plugin references a
/// master that cannot be written and the save fails outright).</para>
///
/// <para>The rule, in precedence order:</para>
/// <list type="number">
/// <item>An explicit per-plugin override (set in the Set Resource-Only Plugins dialog) wins.</item>
/// <item>A plugin that is NOT resource-only mirrors its own mod entry's
/// <see cref="ModSetting.MergeInDependencyRecords"/> — it supplies NPCs, so it is expected to
/// stay in the load order and its records should be referenced, not copied.</item>
/// <item>A resource-only plugin that is ALSO owned by another mod entry providing NPCs
/// inherits that entry's merge-in setting: the owner is the authority on whether that mod is
/// being merged away or kept.</item>
/// <item>Any other resource-only plugin defaults to merging in — nothing else vouches for it
/// being present at runtime, so copying its records is the safe default.</item>
/// </list>
/// </summary>
public static class MergeEligibility
{
    /// <summary>
    /// Builds the plugin -&gt; owning mod entry index used by <see cref="IsPluginMergeEligible"/>
    /// for rule 3. Only entries that actually provide NPCs are indexed: a mod entry that
    /// supplies no NPCs has no opinion worth inheriting (rule 4 covers it). When several
    /// entries claim the same plugin the first is kept, matching the mods list's own ordering.
    /// </summary>
    public static Dictionary<ModKey, ModSetting> BuildNpcProvidingOwnerIndex(IEnumerable<ModSetting> allModSettings)
    {
        var index = new Dictionary<ModKey, ModSetting>();
        if (allModSettings == null) return index;

        foreach (var mod in allModSettings)
        {
            if (mod?.CorrespondingModKeys == null) continue;
            if (mod.NpcFormKeys == null || mod.NpcFormKeys.Count == 0) continue;

            foreach (var key in mod.CorrespondingModKeys)
            {
                // A plugin the owner itself treats as resource-only doesn't make that owner
                // the NPC-providing authority for it.
                if (mod.ResourceOnlyModKeys != null && mod.ResourceOnlyModKeys.Contains(key)) continue;
                if (!index.ContainsKey(key)) index[key] = mod;
            }
        }

        return index;
    }

    /// <summary>
    /// Whether records defined in <paramref name="plugin"/> may be merged into the output while
    /// patching an NPC from <paramref name="owner"/>. See the class remarks for the rule table.
    /// </summary>
    /// <param name="owner">The mod entry currently being patched from.</param>
    /// <param name="plugin">One of that entry's <see cref="ModSetting.CorrespondingModKeys"/>.</param>
    /// <param name="npcProvidingOwnersByPlugin">
    /// Index from <see cref="BuildNpcProvidingOwnerIndex"/>. May be null, in which case rule 3
    /// is skipped and resource-only plugins fall through to rule 4.
    /// </param>
    public static bool IsPluginMergeEligible(ModSetting owner, ModKey plugin,
        IReadOnlyDictionary<ModKey, ModSetting>? npcProvidingOwnersByPlugin)
    {
        if (owner == null) return false;

        // 1. Explicit user choice.
        if (owner.PluginMergeInOverrides != null &&
            owner.PluginMergeInOverrides.TryGetValue(plugin, out var explicitChoice))
        {
            return explicitChoice;
        }

        // 2. Not resource-only: mirror the mod entry's own toggle.
        bool isResourceOnly = owner.ResourceOnlyModKeys != null && owner.ResourceOnlyModKeys.Contains(plugin);
        if (!isResourceOnly) return owner.MergeInDependencyRecords;

        // 3. Resource-only, but another NPC-providing mod entry owns it — defer to that entry.
        //    Never defer to the owner itself; that would just re-apply rule 2 to a plugin the
        //    owner has explicitly classified as a resource. Identity is by DisplayName (the key
        //    the whole app indexes mod entries by) rather than by reference, so this also holds
        //    when callers pass lightweight snapshots rather than the persisted instances.
        if (npcProvidingOwnersByPlugin != null &&
            npcProvidingOwnersByPlugin.TryGetValue(plugin, out var npcProvidingOwner) &&
            npcProvidingOwner != null &&
            !IsSameModEntry(npcProvidingOwner, owner))
        {
            return npcProvidingOwner.MergeInDependencyRecords;
        }

        // 4. Unclaimed resource plugin: merge it, or its records dangle.
        return true;
    }

    /// <summary>Mod entries are identified by DisplayName throughout the app.</summary>
    public static bool IsSameModEntry(ModSetting a, ModSetting b) =>
        ReferenceEquals(a, b) ||
        (a != null && b != null && string.Equals(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The subset of <paramref name="owner"/>'s plugins whose records may be merged in — the
    /// set handed to RecordHandler as <c>modKeysToDuplicateFrom</c>. An empty result means the
    /// merge path is skipped entirely for this mod (the pre-2.2.3 "Merge Dependencies off"
    /// behaviour, which an all-defaults mod still reproduces exactly).
    /// </summary>
    public static HashSet<ModKey> GetMergeEligiblePlugins(ModSetting owner,
        IReadOnlyDictionary<ModKey, ModSetting>? npcProvidingOwnersByPlugin)
    {
        var eligible = new HashSet<ModKey>();
        if (owner?.CorrespondingModKeys == null) return eligible;

        foreach (var key in owner.CorrespondingModKeys.Distinct())
        {
            if (IsPluginMergeEligible(owner, key, npcProvidingOwnersByPlugin)) eligible.Add(key);
        }

        return eligible;
    }
}
