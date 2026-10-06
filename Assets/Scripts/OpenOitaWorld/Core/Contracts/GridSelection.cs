using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenOita.Contracts
{
    // M00：编辑预览与执行共用同一坐标/格中心选择函数。
    public static class GridSelection
    {
        public static WorldRect Bounds(WorldConfig config, Vector2 origin) =>
            new WorldRect(origin, origin + new Vector2(config.Width * config.CellSize, config.Height * config.CellSize));

        public static Vector2 Center(WorldConfig config, Vector2 origin, Vector2Int cell) =>
            origin + new Vector2((cell.x + 0.5f) * config.CellSize, (cell.y + 0.5f) * config.CellSize);

        public static WorldResult Point(WorldConfig config, Vector2 origin, Vector2 point, out Vector2Int cell)
        {
            cell = default;
            WorldResult valid = ContractDefaults.ValidatePoint(point, Bounds(config, origin));
            if (!valid.IsSuccess) return valid;
            // 比较实际可表示的格边界；避免非零origin相减的舍入使整数边界落入前格。
            cell = new Vector2Int(Axis(point.x, origin.x, config.CellSize, config.Width),
                Axis(point.y, origin.y, config.CellSize, config.Height));
            return WorldResult.Success();
        }

        private static int Axis(float point, float origin, float size, int count)
        {
            int index = Math.Min(count - 1, Math.Max(0, (int)Math.Floor(((double)point - origin) / size)));
            while (index > 0 && point < Boundary(origin, size, index)) index--;
            while (index + 1 < count && point >= Boundary(origin, size, index + 1)) index++;
            return index;
        }

        // Vector2存储强制到与Unity点坐标相同的float精度，避免Mono扩展精度比较。
        private static float Boundary(float origin, float size, int index) => new Vector2(origin + index * size, 0).x;

        public static WorldResult Region(WorldConfig config, Vector2 origin, WorldRect region, out Vector2Int[] cells)
        {
            cells = null;
            WorldResult valid = ContractDefaults.ValidateRegion(region, Bounds(config, origin));
            if (!valid.IsSuccess) return valid;
            var selected = new List<Vector2Int>();
            int minX = Math.Max(0, (int)Math.Floor(((double)region.Min.x - origin.x) / config.CellSize) - 1);
            int minY = Math.Max(0, (int)Math.Floor(((double)region.Min.y - origin.y) / config.CellSize) - 1);
            int maxX = Math.Min(config.Width, (int)Math.Ceiling(((double)region.Max.x - origin.x) / config.CellSize) + 1);
            int maxY = Math.Min(config.Height, (int)Math.Ceiling(((double)region.Max.y - origin.y) / config.CellSize) + 1);
            for (int y = minY; y < maxY; y++)
                for (int x = minX; x < maxX; x++)
                {
                    var position = new Vector2Int(x, y);
                    if (region.ContainsCenter(Center(config, origin, position))) selected.Add(position);
                }
            cells = selected.ToArray();
            return WorldResult.Success();
        }
    }
}
