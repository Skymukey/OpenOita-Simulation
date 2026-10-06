using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Structure
{
    internal static class RectangleGeometryBuilder
    {
        // 稀疏占据计算，不为局部包围盒中的孔洞分配数组。
        internal static WorldResult Build(ReadOnlySpan<PlannedCell> cells, out CellRectangle[] rectangles)
        {
            rectangles = null;
            var remaining = new HashSet<Vector2Int>();
            var ordered = new Vector2Int[cells.Length];
            try
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    PlannedCell cell = cells[i];
                    var point = new Vector2Int(cell.Target.Position.X, cell.Target.Position.Y);
                    if (cell.State.MaterialId == 0 || !remaining.Add(point))
                        return Error(WorldErrorCode.InvalidArgument, "矩形输入必须为唯一非空格。");
                    if (i > 0 && (cell.Target.Generation != cells[0].Target.Generation ||
                        cell.Target.Position.OwnerKind != cells[0].Target.Position.OwnerKind ||
                        cell.Target.Position.BodyId != cells[0].Target.Position.BodyId))
                        return Error(WorldErrorCode.InvalidArgument, "矩形不能跨归属合并。");
                    ordered[i] = point;
                }
                Array.Sort(ordered, (a, b) => a.y == b.y ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                var result = new List<CellRectangle>();
                foreach (Vector2Int start in ordered)
                {
                    if (!remaining.Contains(start)) continue;
                    int endX = checked(start.x + 1);
                    while (remaining.Contains(new Vector2Int(endX, start.y))) endX = checked(endX + 1);
                    int endY = checked(start.y + 1);
                    while (HasRow(remaining, start.x, endX, endY)) endY = checked(endY + 1);
                    for (int y = start.y; y < endY; y++)
                        for (int x = start.x; x < endX; x++) remaining.Remove(new Vector2Int(x, y));
                    result.Add(new CellRectangle(start, new Vector2Int(endX, endY)));
                }
                rectangles = result.ToArray();
                return WorldResult.Success();
            }
            catch (OverflowException)
            {
                return Error(WorldErrorCode.CapacityExceeded, "矩形半开边界坐标溢出。");
            }
        }

        private static bool HasRow(HashSet<Vector2Int> remaining, int startX, int endX, int y)
        {
            for (int x = startX; x < endX; x++)
                if (!remaining.Contains(new Vector2Int(x, y))) return false;
            return true;
        }

        private static WorldResult Error(WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("M05.Geometry", "rectangles", message));
    }
}
