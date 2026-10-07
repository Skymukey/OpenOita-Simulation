using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Simulation
{
    internal sealed partial class WorkingWorld
    {
        private long _materialRevision;
        private long _geometryRevision;
        private RuleSourceDirectory _ruleSources = new();
        internal long MaterialRevision => _materialRevision;
        internal long GeometryRevision => _geometryRevision;
        internal RuleSourceDirectory RuleSources => _ruleSources;

        private sealed partial class PreparedGridMutation
        {
            private CellPositionKey[] _derivedPositions;
            private BodySnapshot[] _derivedBodies;
            private RuleSourceDirectory _ruleSources;
            private long _nextMaterialRevision;
            private long _nextGeometryRevision;

            private void PrepareInputRevisions()
            {
                if (ReferenceEquals(_derivedPositions, _positions) && ReferenceEquals(_derivedBodies, _bodySnapshots)) return;
                using var timing = WorldStepMetrics.Measure(WorldStepMetrics.Timing.InputRevisions);
                bool geometryChanged = _structure != null;
                List<CellPositionKey> membershipChanges = null;
                foreach (CellPositionKey position in _positions)
                {
                    var key = new CellKey(_owner.Generation, position);
                    _owner.Read(key, out CellSnapshot before);
                    Read(key, out CellSnapshot after);
                    WorldStepMetrics.Add(WorldStepMetrics.Work.RevisionCells);
                    if (before.MaterialId != after.MaterialId || before.IsBurning != after.IsBurning)
                    {
                        membershipChanges ??= new List<CellPositionKey>();
                        membershipChanges.Add(position);
                    }
                    if (position.OwnerKind != OwnerKind.Suspended) geometryChanged |= before.MaterialId != after.MaterialId;
                }
                ReadOnlySpan<BodySnapshot> oldBodies = _owner._bodySnapshots;
                geometryChanged |= oldBodies.Length != _bodySnapshots.Length;
                if (!geometryChanged)
                    for (int i = 0; i < oldBodies.Length; i++)
                    {
                        BodySnapshot before = oldBodies[i], after = _bodySnapshots[i];
                        // 位姿由索引逐项精确比较；这样已验证候选采用后可复用同一套桶。
                        if (before.BodyId != after.BodyId || before.GeometryVersion != after.GeometryVersion)
                        { geometryChanged = true; break; }
                    }
                _nextMaterialRevision = checked(_owner._materialRevision + (_positions.Length != 0 || _structure != null ? 1 : 0));
                _nextGeometryRevision = checked(_owner._geometryRevision + (geometryChanged ? 1 : 0));
                if (_context.WorkingTick == 0 && _structure != null)
                {
                    // Tick0提取不计D02写入，能力目录须从最终归属建立。
                    var initialPositions = new CellPositionKey[_orderedKeys.Length];
                    for (int i = 0; i < initialPositions.Length; i++) initialPositions[i] = _orderedKeys[i].Position;
                    _ruleSources = new RuleSourceDirectory().Prepare(this, _owner._materials, initialPositions);
                }
                else _ruleSources = membershipChanges == null ? _owner._ruleSources :
                    _owner._ruleSources.Prepare(this, _owner._materials, membershipChanges.ToArray());
                _derivedPositions = _positions;
                _derivedBodies = _bodySnapshots;
            }
        }
    }
}
