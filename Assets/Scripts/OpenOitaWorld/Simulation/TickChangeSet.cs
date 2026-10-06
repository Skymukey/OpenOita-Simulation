using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Simulation
{
    // M02持有的有界Tick增量；用原实例追踪搬运/提取/拆分，同IDReplace仍可观测。
    internal sealed class TickChangeSet : IChangeSetView
    {
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private bool _open;
        private CellChange[] _cells;
        private BodyIdMapping[] _mappings;
        private ulong[] _created, _retired;
        private DirtyCellRange[] _ranges;
        internal void Open() { _open = true; }
        internal void Close() { _open = false; }
        private void Require() { if (!_open || _thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("ChangeSet仅在提交通知回调中有效；长期使用请复制值。 "); }
        public ReadOnlySpan<CellChange> Cells { get { Require(); return _cells; } }
        public ReadOnlySpan<BodyIdMapping> BodyMappings { get { Require(); return _mappings; } }
        public ReadOnlySpan<ulong> CreatedBodies { get { Require(); return _created; } }
        public ReadOnlySpan<ulong> RetiredBodies { get { Require(); return _retired; } }
        public ReadOnlySpan<DirtyCellRange> DirtyRanges { get { Require(); return _ranges; } }

        internal static TickChangeSet Build(ICommittedWorldView before, ICommittedWorldView after,
            Dictionary<CellKey, CellInstanceHandle> original, ITickInstanceMap instances,
            IEnumerable<CellPositionKey> writes, Dictionary<CellInstanceHandle, CellChangeKind> removals, Dictionary<CellKey, CellInstanceHandle> replacements)
        {
            var cells = new List<CellChange>();
            var consumed = new HashSet<CellKey>();
            var mappings = new SortedSet<(ulong oldId, ulong newId)>();
            var ranges = new SortedDictionary<(OwnerKind kind, ulong id), RectInt>();
            foreach (CellKey oldKey in AllKeys(before))
            {
                before.Read(oldKey, out CellSnapshot old);
                if (original.TryGetValue(oldKey, out CellInstanceHandle instance) && instances.TryResolve(instance, out CellKey target))
                {
                    after.Read(target, out CellSnapshot next);
                    consumed.Add(target);
                    if (!oldKey.Equals(target) || !old.Equals(next))
                        cells.Add(new CellChange(!oldKey.Equals(target) ?
                            target.Position.OwnerKind == OwnerKind.Suspended ? CellChangeKind.Suspended :
                            oldKey.Position.OwnerKind == OwnerKind.Suspended ? CellChangeKind.Restored : CellChangeKind.OwnershipTransferred :
                            CellChangeKind.StateChanged, oldKey, target, old, next));
                    if (oldKey.Position.OwnerKind != OwnerKind.Suspended && target.Position.OwnerKind != OwnerKind.Suspended &&
                        (oldKey.Position.BodyId != target.Position.BodyId || oldKey.Position.OwnerKind != target.Position.OwnerKind))
                        mappings.Add((oldKey.Position.BodyId, target.Position.BodyId));
                }
                else
                {
                    // 原键可能已撤销；只在最终占据目录中读取。
                    bool replaced = replacements.TryGetValue(oldKey, out CellInstanceHandle replacement) && instances.TryResolve(replacement, out _);
                    if (replaced)
                    {
                        instances.TryResolve(replacement, out CellKey replacementKey);
                        after.Read(replacementKey, out CellSnapshot next); consumed.Add(replacementKey);
                        cells.Add(new CellChange(CellChangeKind.Replaced, oldKey, replacementKey, old, next));
                        if (oldKey.Position.OwnerKind != OwnerKind.Suspended && replacementKey.Position.OwnerKind != OwnerKind.Suspended &&
                            (oldKey.Position.BodyId != replacementKey.Position.BodyId || oldKey.Position.OwnerKind != replacementKey.Position.OwnerKind))
                            mappings.Add((oldKey.Position.BodyId, replacementKey.Position.BodyId));
                    }
                    else
                        cells.Add(new CellChange(removals.TryGetValue(instance, out CellChangeKind reason) ? reason : CellChangeKind.Removed, oldKey, default, old, default));
                }
            }
            foreach (CellKey key in AllKeys(after))
                if (!consumed.Contains(key)) { after.Read(key, out CellSnapshot next); cells.Add(new CellChange(CellChangeKind.Spawned, default, key, default, next)); }
            var oldIds = new SortedSet<ulong>(); var newIds = new SortedSet<ulong>();
            foreach (BodySnapshot body in before.Bodies) oldIds.Add(body.BodyId);
            foreach (BodySnapshot body in after.Bodies)
            {
                newIds.Add(body.BodyId);
                foreach (BodySnapshot oldBody in before.Bodies)
                    if (oldBody.BodyId == body.BodyId && oldBody.GeometryVersion != body.GeometryVersion) mappings.Add((body.BodyId, body.BodyId));
            }
            var created = new List<ulong>(); var retired = new List<ulong>();
            foreach (ulong id in newIds) if (!oldIds.Contains(id)) { created.Add(id); bool mapped = false; foreach (var map in mappings) if (map.newId == id) mapped = true; if (!mapped) mappings.Add((0, id)); }
            foreach (ulong id in oldIds) if (!newIds.Contains(id)) { retired.Add(id); bool mapped = false; foreach (var map in mappings) if (map.oldId == id) mapped = true; if (!mapped) mappings.Add((id, 0)); }
            foreach (CellPositionKey key in writes) AddRange(ranges, key);
            foreach (BodySnapshot body in after.Bodies)
            {
                bool changed = !oldIds.Contains(body.BodyId);
                foreach (BodySnapshot old in before.Bodies) if (old.BodyId == body.BodyId && !old.Equals(body)) changed = true;
                if (changed) foreach (CellKey key in after.OccupiedCells) if (key.Position.BodyId == body.BodyId) AddRange(ranges, key.Position);
            }
            var mapArray = new BodyIdMapping[mappings.Count]; int index = 0;
            foreach (var map in mappings) mapArray[index++] = new BodyIdMapping(map.oldId, map.newId);
            var rangeArray = new DirtyCellRange[ranges.Count]; index = 0;
            foreach (var range in ranges) rangeArray[index++] = new DirtyCellRange(range.Key.kind, range.Key.id, range.Value.min, range.Value.max);
            return new TickChangeSet { _cells = cells.ToArray(), _mappings = mapArray, _created = created.ToArray(), _retired = retired.ToArray(), _ranges = rangeArray };
        }
        private static void AddRange(SortedDictionary<(OwnerKind kind, ulong id), RectInt> ranges, CellPositionKey key)
        {
            if (key.OwnerKind == OwnerKind.Suspended) return;
            var owner = (key.OwnerKind, key.BodyId);
            if (ranges.TryGetValue(owner, out RectInt bounds))
            {
                bounds.xMin = Math.Min(bounds.xMin, key.X); bounds.yMin = Math.Min(bounds.yMin, key.Y);
                bounds.xMax = Math.Max(bounds.xMax, key.X + 1); bounds.yMax = Math.Max(bounds.yMax, key.Y + 1);
                ranges[owner] = bounds;
            }
            else ranges.Add(owner, new RectInt(key.X, key.Y, 1, 1));
        }
        private static CellKey[] AllKeys(ICommittedWorldView view)
        {
            var keys = new List<CellKey>(view.OccupiedCells.ToArray());
            if (view is IFluidSuspensionView fluids)
                foreach (SuspendedFluidSnapshot record in fluids.SuspendedFluids) keys.Add(record.Key);
            return keys.ToArray();
        }
    }
}
