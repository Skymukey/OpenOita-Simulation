using System;
using OpenOita.Contracts;
using OpenOita.Spatial;
using UnityEngine;

namespace OpenOita.Queries
{
    internal sealed class QueryService
    {
        private SegmentCellHit[] _scratch;
        private readonly ExactCellGeometry _geometry = new();
        internal long ReservedBytes => 128L + _scratch.Length * 96L;
        internal void Dispose() { _scratch = Array.Empty<SegmentCellHit>(); }
        internal QueryService(int capacity) { _scratch = new SegmentCellHit[capacity]; }

        internal PointQueryResult Point(ICommittedWorldView view, Vector2 point)
        {
            foreach (CellKey key in view.OccupiedCells)
                if (_geometry.ContainsPoint(CellSelection.Geometry(key, view.Origin, view.Config.CellSize, view.Bodies), point))
                {
                    view.Read(key, out CellSnapshot state);
                    return new PointQueryResult(WorldResult.Success(), view.Version, true, new CellHit(view.Version, key.Position, state));
                }
            return new PointQueryResult(WorldResult.Success(), view.Version);
        }

        internal QueryResult Collect(ICommittedWorldView view, WorldRect region, Vector2 start, Vector2 end, bool segment, Span<CellHit> destination)
        {
            int count = 0;
            bool point = segment && start == end;
            foreach (CellKey key in view.OccupiedCells)
            {
                CellGeometry square = CellSelection.Geometry(key, view.Origin, view.Config.CellSize, view.Bodies);
                double t = 0;
                bool hit = !segment ? region.ContainsCenter(_geometry.Center(square)) :
                    point ? _geometry.ContainsPoint(square, start) : _geometry.IntersectSegment(square, start, end, out t);
                if (!hit) continue;
                view.Read(key, out CellSnapshot state);
                _scratch[count++] = new SegmentCellHit(new CellHit(view.Version, key.Position, state), t);
            }
            if (segment) Array.Sort(_scratch, 0, count, Comparer.Instance);
            if (count > destination.Length)
                return new QueryResult(WorldResult.Failure(WorldErrorCode.BufferTooSmall,
                    new WorldDiagnostic("Query", "buffer", "缓冲不足；未写入部分结果。")), view.Version, count, 0);
            for (int i = 0; i < count; i++) destination[i] = _scratch[i].Hit;
            return new QueryResult(WorldResult.Success(), view.Version, count, count);
        }
        private sealed class Comparer : System.Collections.Generic.IComparer<SegmentCellHit>
        {
            internal static readonly Comparer Instance = new();
            public int Compare(SegmentCellHit a, SegmentCellHit b)
            {
                int order = a.FirstIntersectionT.CompareTo(b.FirstIntersectionT);
                return order == 0 ? a.Hit.Position.CompareTo(b.Hit.Position) : order;
            }
        }
    }
}
