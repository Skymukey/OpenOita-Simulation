using System;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Preview
{
    // 坐标单位是最终游戏输出像素；控制面板不能再给此视口施加 GUI.matrix 缩放。
    internal readonly struct PixelPreviewViewport
    {
        internal readonly Rect Bounds;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly int Scale;
        internal readonly Vector2Int Pan;

        internal PixelPreviewViewport(Rect bounds, int width, int height, int scale, Vector2Int pan)
        {
            if (width <= 0 || height <= 0 || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
            Bounds = new Rect(Mathf.Round(bounds.x), Mathf.Round(bounds.y),
                Mathf.Max(1, Mathf.Floor(bounds.width)), Mathf.Max(1, Mathf.Floor(bounds.height)));
            Width = width;
            Height = height;
            Scale = scale;
            Pan = new Vector2Int(Mathf.Clamp(pan.x, 0, Mathf.Max(0, width * scale - (int)Bounds.width)),
                Mathf.Clamp(pan.y, 0, Mathf.Max(0, height * scale - (int)Bounds.height)));
        }

        internal Rect CellRect(float x, float y, float width = 1, float height = 1) =>
            new Rect(Bounds.x - Pan.x + x * Scale,
                Bounds.y - Pan.y + (Height - y - height) * Scale, width * Scale, height * Scale);

        // 先按输出画面的半开像素矩形命中，再换算Y向上的逻辑格。
        // 在水平边界处直接 floor(Height-y) 会把上边界错误分给下一行。
        internal bool TryCell(Vector2 screenPoint, out Vector2Int cell)
        {
            cell = default;
            if (!Bounds.Contains(screenPoint)) return false;
            float x = (screenPoint.x - Bounds.x + Pan.x) / Scale;
            float topRow = (screenPoint.y - Bounds.y + Pan.y) / Scale;
            if (x < 0 || topRow < 0 || x >= Width || topRow >= Height) return false;
            cell = new Vector2Int(Mathf.FloorToInt(x), Height - 1 - Mathf.FloorToInt(topRow));
            return true;
        }

        internal bool TryHit(ICommittedRenderView view, Vector2 screenPoint, out CellKey hit)
        {
            hit = default;
            if (!Bounds.Contains(screenPoint)) return false;
            bool found = false;
            foreach (CellKey key in view.OccupiedCells)
            {
                Vector2 origin = ModulePreviewSession.CellOrigin(key, view);
                if (!CellRect(origin.x, origin.y).Contains(screenPoint)) continue;
                // 即便未来夹具出现重叠，也只返回稳定归属顺序的一个真实坐标。
                if (!found || key.Position.CompareTo(hit.Position) < 0) hit = key;
                found = true;
            }
            return found;
        }
    }
}
