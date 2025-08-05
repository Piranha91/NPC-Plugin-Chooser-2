// [ContextualPerformanceTracer.cs] - Final Version with Both Group-Specific and Comprehensive Reporting
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public static class ContextualPerformanceTracer
    {
        private static readonly ConcurrentDictionary<Guid, ProfileData> _stats = new();
        private static readonly AsyncLocal<string?> _currentContext = new();
        private static readonly AsyncLocal<Guid?> _currentParentId = new();

        private class ProfileData
        {
            public string FunctionName { get; }
            public string Context { get; }
            public Guid Id { get; }
            public Guid? ParentId { get; }
            public double TotalMilliseconds { get; set; }
            public long CallCount { get; set; }
            public double MaxMilliseconds { get; set; }
            public ConcurrentBag<ProfileData> Children { get; } = new();

            public ProfileData(string functionName, string context, Guid id, Guid? parentId)
            {
                FunctionName = functionName;
                Context = context;
                Id = id;
                ParentId = parentId;
            }
        }

        private class ReportNode
        {
            public string FunctionName { get; }
            public long TotalCalls { get; set; }
            public double TotalMilliseconds { get; set; }
            public double MaxMilliseconds { get; set; }
            public List<ReportNode> Children { get; } = new();

            public ReportNode(string functionName)
            {
                FunctionName = functionName;
            }
        }
        
        public static void Reset()
        {
            _stats.Clear();
            _currentContext.Value = null;
            _currentParentId.Value = null;
            Debug.WriteLine($"--- ContextualPerformanceTracer Reset ---");
        }

        public static IDisposable BeginContext(string contextName)
        {
            _currentContext.Value = contextName;
            return new ContextScope();
        }

        public static IDisposable Trace(string functionName, string? detail = null)
        {
            string contextToUse = _currentContext.Value ?? "No Batch Context";
            string fullFunctionName = detail == null ? functionName : $"{functionName}[{detail}]";
            var parentId = _currentParentId.Value;
            var newId = Guid.NewGuid();
            var data = new ProfileData(fullFunctionName, contextToUse, newId, parentId);
            _stats.TryAdd(newId, data);
            _currentParentId.Value = newId;
            return new Tracer(newId);
        }

        /// <summary>
        /// RESTORED: Generates a comprehensive, hierarchical report for ALL contexts traced since the last Reset().
        /// This is the replacement for your call in VM_Settings.cs.
        /// </summary>
        public static string GenerateDetailedReport(string reportTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n================= DETAILED PERFORMANCE REPORT: [{reportTitle}] =================");

            if (_stats.IsEmpty)
            {
                sb.AppendLine("No performance data was recorded.");
                sb.AppendLine("========================================================================================");
                Debug.WriteLine(sb.ToString());
                return sb.ToString();
            }

            // Group all traced data by the context they ran in.
            var allContexts = _stats.Values.GroupBy(d => d.Context);

            foreach (var contextGroup in allContexts.OrderBy(g => g.Key))
            {
                sb.AppendLine($"\n--- Context: {contextGroup.Key} ---");
                sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
                sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");
                
                var groupStats = contextGroup.ToList();
                var rootNodes = BuildCallTree(groupStats);
                var aggregatedRoot = AggregateNodes(rootNodes);

                foreach (var node in aggregatedRoot.Children.OrderByDescending(c => c.TotalMilliseconds))
                {
                    PrintNode(sb, node, 0);
                }
            }

            sb.AppendLine("========================================================================================");
            Debug.WriteLine(sb.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// Generates a report for a single, specified group. Still available for the Patcher's specific use case.
        /// </summary>
        public static string GenerateReportForGroup(string groupName, bool showFunctionStats = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n================= PERFORMANCE REPORT for Group: [{groupName}] =================");

            var groupStats = _stats.Values.Where(d => d.Context.Equals(groupName, StringComparison.Ordinal)).ToList();

            if (!groupStats.Any())
            {
                sb.AppendLine("No performance data was recorded for this group.");
                sb.AppendLine("=====================================================================================");
                Debug.WriteLine(sb.ToString());
                return sb.ToString();
            }

            var mainLoopIterations = groupStats.Where(s => s.FunctionName == "Patcher.MainLoopIteration").ToList();
            long totalNpcsProcessed = mainLoopIterations.Count;
            double totalNpcProcessingTime = mainLoopIterations.Sum(s => s.TotalMilliseconds);
            
            if (totalNpcsProcessed > 0)
            {
                double avgTimePerNpc = totalNpcProcessingTime / totalNpcsProcessed;
                sb.AppendLine($"Total NPCs Processed: {totalNpcsProcessed}");
                sb.AppendLine($"Total Cycle Time:     {totalNpcProcessingTime:N2} ms");
                sb.AppendLine($"Average Time per NPC: {avgTimePerNpc:N4} ms");
            }
            else
            {
                sb.AppendLine("No 'Patcher.MainLoopIteration' calls were traced for this group.");
            }

            if (showFunctionStats)
            {
                sb.AppendLine("\n--- Sub-function Details (Hierarchical) ---");
                sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
                sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");

                var rootNodes = BuildCallTree(groupStats);
                var aggregatedRoot = AggregateNodes(rootNodes);
                foreach (var node in aggregatedRoot.Children.OrderByDescending(c => c.TotalMilliseconds))
                {
                    PrintNode(sb, node, 0);
                }
            }
            
            sb.AppendLine("=====================================================================================");
            Debug.WriteLine(sb.ToString());
            return sb.ToString();
        }

        #region Helper Methods for Reporting
        private static List<ProfileData> BuildCallTree(IEnumerable<ProfileData> flatList)
        {
            var nodeDict = flatList.ToDictionary(n => n.Id);
            var rootNodes = new List<ProfileData>();

            foreach (var node in flatList)
            {
                if (node.ParentId.HasValue && nodeDict.TryGetValue(node.ParentId.Value, out var parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    rootNodes.Add(node);
                }
            }
            return rootNodes;
        }

        private static ReportNode AggregateNodes(IEnumerable<ProfileData> nodes)
        {
            var root = new ReportNode("root");
            var queue = new Queue<(IEnumerable<ProfileData> sourceNodes, ReportNode parentReportNode)>();
            if (nodes.Any())
            {
                queue.Enqueue((nodes, root));
            }

            while (queue.Count > 0)
            {
                var (currentLevelNodes, parentReportNode) = queue.Dequeue();

                var groupedByName = currentLevelNodes
                    .GroupBy(n => n.FunctionName)
                    .Select(g =>
                    {
                        var aggregatedNode = new ReportNode(g.Key)
                        {
                            TotalCalls = g.Count(),
                            TotalMilliseconds = g.Sum(n => n.TotalMilliseconds),
                            MaxMilliseconds = g.Max(n => n.MaxMilliseconds),
                        };

                        var allChildren = g.SelectMany(n => n.Children);
                        if (allChildren.Any())
                        {
                            queue.Enqueue((allChildren, aggregatedNode));
                        }

                        return aggregatedNode;
                    });
                
                parentReportNode.Children.AddRange(groupedByName);
            }
            return root;
        }
        
        private static void PrintNode(StringBuilder sb, ReportNode node, int depth)
        {
            if (node.TotalCalls <= 0) return;

            double avg = node.TotalMilliseconds / node.TotalCalls;
            string indent = new string(' ', depth * 2);
            string funcName = $"{indent}{node.FunctionName}";

            const int nameColumnWidth = 38;
            funcName = funcName.Length > nameColumnWidth
                ? funcName.Substring(0, nameColumnWidth - 3) + "..."
                : funcName.PadRight(nameColumnWidth);

            sb.AppendLine($"{funcName} | {node.TotalCalls,10} | {node.TotalMilliseconds,14:N2} | {avg,12:N4} | {node.MaxMilliseconds,12:N2}");

            foreach (var child in node.Children.OrderByDescending(c => c.TotalMilliseconds))
            {
                PrintNode(sb, child, depth + 1);
            }
        }
        #endregion

        #region Private Helper Classes
        private class Tracer : IDisposable
        {
            private readonly Guid _traceId;
            private readonly Guid? _parentIdOnExit;
            private readonly Stopwatch _stopwatch;

            public Tracer(Guid traceId)
            {
                _traceId = traceId;
                _parentIdOnExit = _stats[traceId].ParentId;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                var data = _stats[_traceId];
                data.TotalMilliseconds = _stopwatch.Elapsed.TotalMilliseconds;
                data.MaxMilliseconds = data.TotalMilliseconds;
                data.CallCount = 1;
                _currentParentId.Value = _parentIdOnExit;
            }
        }

        private class ContextScope : IDisposable
        {
            public void Dispose()
            {
                _currentContext.Value = null;
            }
        }
        #endregion
    }
}