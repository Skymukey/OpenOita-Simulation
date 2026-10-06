using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Simulation.Bodies;

namespace OpenOita.Structure
{
    // 私有计算结果，公共 DTO 继续使用 M00 的 PlannedCell/BodyGeometryPlan/BodyIdMapping。
    // 没有 Apply/发布入口；由材料准备适配器经 M02 受控入口移交存储。
    internal sealed class BodyExtractionPlan : IDisposable
    {
        private PlannedCell[] _cells;
        private BodyIdMapping[] _mappings;
        private CellPositionKey[] _writes;
        private IReadOnlyList<BodyGeometryPlan> _geometry;
        private IReadOnlyList<MaterialBody> _bodies;
        private ulong[] _retired;
        private bool _ownsBodies = true;
        internal ResourceBudget Budget { get; }
        internal bool IsDisposed => _cells == null;
        internal ReadOnlySpan<PlannedCell> Cells { get { Require(); return _cells; } }
        internal ReadOnlySpan<BodyIdMapping> BodyMappings { get { Require(); return _mappings; } }
        internal ReadOnlySpan<CellPositionKey> Writes { get { Require(); return _writes; } }
        internal ReadOnlySpan<ulong> RetiredBodyIds { get { Require(); return _retired; } }
        internal IReadOnlyList<BodyGeometryPlan> Geometry { get { Require(); return _geometry; } }
        internal IReadOnlyList<MaterialBody> Bodies { get { Require(); return _bodies; } }

        internal BodyExtractionPlan(PlannedCell[] cells, BodyIdMapping[] mappings, CellPositionKey[] writes,
            List<BodyGeometryPlan> geometry, List<MaterialBody> bodies, ulong[] retired, ResourceBudget budget)
        {
            _cells = cells;
            _mappings = mappings;
            _writes = writes;
            _geometry = geometry.AsReadOnly();
            _bodies = bodies.AsReadOnly();
            _retired = retired;
            Budget = budget;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            if (_ownsBodies) foreach (MaterialBody body in _bodies) body.Dispose();
            _cells = null;
            _mappings = null;
            _writes = null;
            _geometry = null;
            _bodies = null;
            _retired = null;
        }

        internal void ReleaseBodyOwnership()
        {
            Require();
            _ownsBodies = false;
        }

        private void Require()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(BodyExtractionPlan));
        }
    }
}
