using System.Collections.Generic;
using UnityEngine;

namespace OpenOita.Editor
{
    // 画布和Scene共用选格；仅UI笔刷裁到画布内，底层模型仍严格拒绝越界输入。
    public static class SceneBrushSelection
    {
        public const int MaxSize = 128;

        public static Vector2Int[] Stamp(Vector2Int center, int size, bool round, int width, int height)
            => Stroke(center, center, size, round, width, height);

        public static Vector2Int[] Stroke(Vector2Int from, Vector2Int to, int size, bool round, int width, int height)
        {
            var cells = new HashSet<Vector2Int>();
            if (width < 1 || height < 1) return System.Array.Empty<Vector2Int>();
            size = Mathf.Clamp(size, 1, MaxSize);
            from = Clamp(from, width, height); to = Clamp(to, width, height);
            int x = from.x, y = from.y, dx = Mathf.Abs(to.x - x), dy = -Mathf.Abs(to.y - y);
            int sx = x < to.x ? 1 : -1, sy = y < to.y ? 1 : -1, error = dx + dy;
            while (true)
            {
                int minX = x - (size - 1) / 2, minY = y - (size - 1) / 2;
                float centerX = minX + (size - 1) * 0.5f, centerY = minY + (size - 1) * 0.5f;
                for (int cy = Mathf.Max(0, minY); cy < Mathf.Min(height, minY + size); cy++)
                    for (int cx = Mathf.Max(0, minX); cx < Mathf.Min(width, minX + size); cx++)
                        if (!round || (cx - centerX) * (cx - centerX) + (cy - centerY) * (cy - centerY) <= size * size * 0.25f)
                            cells.Add(new Vector2Int(cx, cy));
                if (x == to.x && y == to.y) break;
                int twice = 2 * error;
                if (twice >= dy) { error += dy; x += sx; }
                if (twice <= dx) { error += dx; y += sy; }
            }
            var ordered = new List<Vector2Int>(cells);
            ordered.Sort((a, b) => a.y == b.y ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return ordered.ToArray();
        }

        public static Vector2Int[] Rectangle(Vector2Int from, Vector2Int to, int width, int height)
        {
            var cells = new List<Vector2Int>();
            Vector2Int min = Vector2Int.Max(Vector2Int.zero, Vector2Int.Min(from, to));
            Vector2Int max = Vector2Int.Min(new Vector2Int(width - 1, height - 1), Vector2Int.Max(from, to));
            for (int y = min.y; y <= max.y; y++)
                for (int x = min.x; x <= max.x; x++) cells.Add(new Vector2Int(x, y));
            return cells.ToArray();
        }

        private static Vector2Int Clamp(Vector2Int p, int width, int height)
            => new Vector2Int(Mathf.Clamp(p.x, 0, width - 1), Mathf.Clamp(p.y, 0, height - 1));
    }
}
