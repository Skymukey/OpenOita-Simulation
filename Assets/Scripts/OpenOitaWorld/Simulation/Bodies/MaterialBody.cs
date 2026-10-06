using System;
using OpenOita.Contracts;

namespace OpenOita.Simulation.Bodies
{
    // M05 创建候选局部存储，受控准备成功后移交 M02 工作目录。
    internal sealed class MaterialBody : IMaterialBodyStorage
    {
        private CellPositionKey[] _positions;
        private CellState[] _cells;
        private BodyGeometryPlan _geometry;
        private readonly BodySnapshot _snapshot;
        public bool IsDisposed => _cells == null;
        internal int CellCount => _cells?.Length ?? 0;
        public BodySnapshot Snapshot { get { Require(); return _snapshot; } }
        public BodyGeometryPlan Geometry { get { Require(); return _geometry; } }
        public ReadOnlySpan<CellPositionKey> OccupiedPositions { get { Require(); return _positions; } }

        internal MaterialBody(BodyGeometryPlan geometry, ulong geometryVersion)
        {
            if (geometry == null || geometry.Owner.OwnerKind != OwnerKind.Body || geometryVersion == 0 || geometryVersion != geometry.GeometryVersion)
                throw new ArgumentException("候选材料体必须拥有动态几何及非零版本。");
            _geometry = geometry;
            _snapshot = new BodySnapshot(geometry.Owner.BodyId, geometry.Pose, geometry.Motion,
                geometry.LocalCenterOfMass, geometryVersion);
            _positions = new CellPositionKey[geometry.Cells.Count];
            _cells = new CellState[geometry.Cells.Count];
            for (int i = 0; i < _cells.Length; i++)
            {
                _positions[i] = geometry.Cells[i].Target.Position;
                _cells[i] = CellState.FromSnapshot(geometry.Cells[i].State);
            }
        }

        public bool TryRead(in CellPositionKey position, out CellSnapshot state)
        {
            state = default;
            if (IsDisposed) return false;
            int index = Array.BinarySearch(_positions, position);
            if (index < 0) return false;
            state = _cells[index].Snapshot;
            return true;
        }

        public void Dispose()
        {
            _positions = null;
            _cells = null;
            _geometry = null;
        }

        private void Require()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(MaterialBody));
        }
    }
}
