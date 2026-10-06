using System;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Spatial
{
    public static class PhysicsGeometryValidator
    {
        public static WorldResult Validate(WorldConfig config, Vector2 origin, WorldOccupancyIndex before, WorldOccupancyIndex after)
        {
            WorldResult lease = before.Check(before.Lease.Generation); if (!lease.IsSuccess) return lease;
            lease = after.Check(before.Lease.Generation); if (!lease.IsSuccess) return lease;
            float tolerance = config.CellSize * 0.1f, maximumStep = config.CellSize / 2;
            Vector2 maximum = origin + new Vector2(config.Width * config.CellSize, config.Height * config.CellSize);
            foreach (WorldOccupancyIndex.Entry entry in after.Entries)
            {
                if (!entry.Solid) continue;
                CellGeometry square = entry.Geometry;
                if (!before.TryGet(square.Key.Position, out WorldOccupancyIndex.Entry previous)) return Failure("directory", "子步改变了材料归属。");
                for (int v = 0; v < 4; v++)
                {
                    Vector2 point = ExactCellGeometry.Corner(square, v);
                    if (!ContractDefaults.IsFinite(point)) return Failure("pose", "候选完整几何包含非有限值。");
                    if (Math.Max(Math.Max(origin.x - point.x, point.x - maximum.x), Math.Max(origin.y - point.y, point.y - maximum.y)) > tolerance + 1e-7f * config.CellSize)
                        return Failure("boundary", "完整固体几何越界超过0.1格。");
                    if (Vector2.Distance(point, ExactCellGeometry.Corner(previous.Geometry, v)) > maximumStep + 1e-6f * config.CellSize)
                        return Failure("edgeDisplacement", "实际格顶点子步位移超过半格。");
                }
                foreach (CellPositionKey key in after.Candidates(square))
                {
                    if (key.CompareTo(square.Key.Position) <= 0 || (key.OwnerKind == square.Key.Position.OwnerKind && key.BodyId == square.Key.Position.BodyId)) continue;
                    after.TryGet(key, out WorldOccupancyIndex.Entry other);
                    if (other.Solid && ExactCellGeometry.Penetration(square, other.Geometry) > tolerance + 1e-7f * config.CellSize)
                        return Failure("solidOverlap", "跨归属固体完整分离深度超过0.1格。");
                }
            }
            return WorldResult.Success();
        }
        private static WorldResult Failure(string target, string message) => WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("Physics", target, message));
    }
}
