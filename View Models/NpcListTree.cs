using System;
using System.Collections.Generic;
using System.Linq;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Orders the NPCs tab's list as a template tree (Settings.GroupTemplatedNpcsInList): each templated
/// NPC directly under the NPC it takes its face from, nested for multi-hop chains.
///
/// <para>The list stays a flat ListBox — rows only carry a depth — so virtualization, keyboard
/// navigation, ScrollIntoView and every "is it in FilteredNpcs" check keep working unchanged. A
/// WPF TreeView would break all of those for thousands of rows.</para>
///
/// <para>Kept free of view-model types so the ordering rules can be tested directly; the caller
/// supplies the parent relation (which template link counts depends on the patching mode).</para>
/// </summary>
internal static class NpcListTree
{
    /// <param name="Guides">One entry per ancestor lane (0..Depth-1): whether that lane's vertical
    /// line runs on below this row, i.e. the ancestor one level deeper on this row's path has a later
    /// sibling. The last entry is this row's own elbow lane. Drives the connector lines.</param>
    internal readonly record struct Row<T>(T Node, int Depth, bool IsContext, bool HasChildren,
        IReadOnlyList<bool> Guides);

    /// <summary>
    /// Lays out <paramref name="sortedMatches"/> as a tree.
    /// <list type="bullet">
    /// <item>A match whose ancestors did not match still sits under them: the missing ancestors are
    /// added as context rows (<see cref="Row{T}.IsContext"/>), so a search hit keeps its place.</item>
    /// <item>Siblings — and roots — keep the incoming sort order. A context row sorts where its
    /// earliest-sorted descendant would.</item>
    /// <item>A collapsed node (<paramref name="isExpanded"/> false) emits no descendants.</item>
    /// <item>A node on a template loop is treated as a root rather than looping forever.</item>
    /// </list>
    /// </summary>
    internal static List<Row<T>> Build<T>(IReadOnlyList<T> sortedMatches, Func<T, T?> parentOf,
        Func<T, bool> isExpanded) where T : class
    {
        var cmp = ReferenceEqualityComparer.Instance;
        var matches = new HashSet<T>(sortedMatches, cmp);

        // Effective parent of every node in play (matches plus the ancestors they pull in), with
        // loops broken. Ranks propagate up so a context row sorts with its first descendant.
        var parent = new Dictionary<T, T?>(cmp);
        var rank = new Dictionary<T, int>(cmp);
        for (int i = 0; i < sortedMatches.Count; i++)
        {
            var node = sortedMatches[i];
            var seen = new HashSet<T>(cmp);
            while (node != null && seen.Add(node))
            {
                if (!rank.TryGetValue(node, out var r) || i < r) rank[node] = i;
                if (parent.ContainsKey(node))
                {
                    node = parent[node];
                    continue;
                }

                var p = parentOf(node);
                if (p != null && IsOnLoop(node, p, parentOf)) p = null;
                parent[node] = p;
                node = p;
            }
        }

        var children = new Dictionary<T, List<T>>(cmp);
        var roots = new List<T>();
        foreach (var (node, p) in parent)
        {
            if (p == null) roots.Add(node);
            else
            {
                if (!children.TryGetValue(p, out var list)) children[p] = list = new List<T>();
                list.Add(node);
            }
        }

        var rows = new List<Row<T>>(parent.Count);
        void Emit(T node, int depth, bool[] guides)
        {
            bool hasChildren = children.TryGetValue(node, out var kids);
            rows.Add(new Row<T>(node, depth, !matches.Contains(node), hasChildren, guides));
            if (!hasChildren || !isExpanded(node)) return;
            var ordered = kids!.OrderBy(k => rank[k]).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                Emit(ordered[i], depth + 1, [..guides, i < ordered.Count - 1]);
            }
        }
        foreach (var root in roots.OrderBy(r => rank[r])) Emit(root, 0, []);
        return rows;
    }

    /// <summary>
    /// Same-name grouping (Settings.GroupSameNameNpcsInList): for each key, the first node in
    /// <paramref name="sorted"/> order leads the group, so a gathered group sits where its first
    /// member sorts. Nodes with a null key (unnamed, or templated) join no group. Keys compare
    /// case-insensitively.
    /// </summary>
    internal static Dictionary<string, T> PickNameGroupLeaders<T>(IEnumerable<T> sorted,
        Func<T, string?> groupKey) where T : class
    {
        var leaders = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sorted)
        {
            if (groupKey(node) is { } key) leaders.TryAdd(key, node);
        }
        return leaders;
    }

    /// <summary>
    /// The top of <paramref name="node"/>'s chain — the NPC it ultimately takes its face from among
    /// the listed ones — or null when it has no parent, or the chain loops.
    /// </summary>
    internal static T? FindChainTerminus<T>(T node, Func<T, T?> parentOf) where T : class
    {
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance) { node };
        var current = parentOf(node);
        if (current == null) return null;
        while (true)
        {
            if (!seen.Add(current)) return null;
            var next = parentOf(current);
            if (next == null) return current;
            current = next;
        }
    }

    // Does walking up from `parent` come back to `node`?
    private static bool IsOnLoop<T>(T node, T parent, Func<T, T?> parentOf) where T : class
    {
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (var cur = parent; cur != null && seen.Add(cur); cur = parentOf(cur))
        {
            if (ReferenceEquals(cur, node)) return true;
        }
        return false;
    }
}
