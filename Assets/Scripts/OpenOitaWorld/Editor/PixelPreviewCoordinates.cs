using UnityEngine;

namespace OpenOita.Editor
{
    // GUI点、DPI与实际输出像素分开；屏幕倍率不改变逻辑选格。
    public static class PixelPreviewCoordinates
    {
        public static bool TryPoint(Rect rect, Vector2 guiPoint, float pixelsPerPoint, int outputHeight,
            int scale, Vector2Int pan, out Vector2Int cell)
        {
            cell = default;
            if (!rect.Contains(guiPoint) || pixelsPerPoint <= 0 || outputHeight < 1 || scale < 1) return false;
            Vector2 pixel = (guiPoint - rect.position) * pixelsPerPoint;
            int x = Mathf.FloorToInt(pixel.x);
            int y = outputHeight - 1 - Mathf.FloorToInt(pixel.y);
            cell = pan + new Vector2Int(Mathf.FloorToInt(x / (float)scale), Mathf.FloorToInt(y / (float)scale));
            return true;
        }
    }
}
