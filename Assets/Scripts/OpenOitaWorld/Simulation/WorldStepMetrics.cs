using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenOita.Simulation
{
    // 所属线程上的可选诊断。热路径只累加整数；导出时才创建字典。
    internal sealed class WorldStepMetrics
    {
        internal enum Timing { BeginTick, PreFlowContacts, Water, Steam, Extinguish, Burning, Physics, PhysicsContacts, RuleInput, InputRevisions, SpatialRebuild, Copy, SnapshotCopy, ChangeSet, DisplayPrepare, Publish, Count }
        internal enum Work { RuleInputCells, RuleInputBuilds, RuleInputReuses, RuleSourceChanges, RuleSourceReads, RevisionCells, SkippedRuleStages, RuleParticipants, LiquidSearches, LiquidNodes, LiquidEdges, LiquidLinkHits, OutletProbes, LandingCacheHits, SolidQueries, SolidOverlapCalls, SolidCacheHits, PassableQueries, PassableCacheHits, GridReads, DynamicQueries, ExactSolidTests, SpatialRebuilds, SpatialEntries, ChunkCopies, InstanceCopies, DirectoryCopies, DirectoryEntries, CopiedItems, CopiedPayloadBytes, PublishedCells, Count }
        [ThreadStatic] internal static WorldStepMetrics Current;
        private readonly long[] _times = new long[(int)Timing.Count];
        private readonly long[] _work = new long[(int)Work.Count];
        private long _start;
        internal bool Enabled = true;
        internal double StepMilliseconds { get; private set; }

        internal void Begin()
        {
            Array.Clear(_times, 0, _times.Length);
            Array.Clear(_work, 0, _work.Length);
            StepMilliseconds = 0;
            _start = Enabled ? Stopwatch.GetTimestamp() : 0;
        }
        internal void End() { if (Enabled) StepMilliseconds = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency; }
        internal Activation Activate() => new Activation(Enabled ? this : null);
        internal static Measurement Measure(Timing timing) => new Measurement(Current, timing);
        internal static void Add(Work work, long amount = 1) { if (Current != null) Current._work[(int)work] += amount; }
        internal object Snapshot()
        {
            var times = new Dictionary<string, double>();
            var work = new Dictionary<string, long>();
            for (int i = 0; i < _times.Length; i++) times.Add(((Timing)i).ToString(), _times[i] * 1000.0 / Stopwatch.Frequency);
            for (int i = 0; i < _work.Length; i++) work.Add(((Work)i).ToString(), _work[i]);
            return new { stepMs = StepMilliseconds, timesMs = times, work };
        }
        internal readonly struct Activation : IDisposable
        {
            private readonly WorldStepMetrics _previous;
            internal Activation(WorldStepMetrics metrics) { _previous = Current; Current = metrics; }
            public void Dispose() { Current = _previous; }
        }
        internal readonly struct Measurement : IDisposable
        {
            private readonly WorldStepMetrics _metrics;
            private readonly Timing _timing;
            private readonly long _start;
            internal Measurement(WorldStepMetrics metrics, Timing timing)
            { _metrics = metrics; _timing = timing; _start = metrics == null ? 0 : Stopwatch.GetTimestamp(); }
            public void Dispose() { if (_metrics != null) _metrics._times[(int)_timing] += Stopwatch.GetTimestamp() - _start; }
        }
    }
}
