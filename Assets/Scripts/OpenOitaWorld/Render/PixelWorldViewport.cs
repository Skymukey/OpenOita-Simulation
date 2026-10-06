using System;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Render
{
    // 只改摄像机。边界对齐主网格，窗口不足时裁剪，倍率/平移不改模拟配置。
    public static class PixelWorldViewport
    {
        public static void Configure(Camera camera, WorldConfig config, Vector2 origin, int scale = 1, Vector2Int pan = default)
        {
            if (camera == null || scale < 1) throw new ArgumentException("摄像机及整数倍率必须有效。");
            int width = camera.targetTexture != null ? camera.targetTexture.width : camera.pixelWidth;
            int height = camera.targetTexture != null ? camera.targetTexture.height : camera.pixelHeight;
            camera.orthographic = true;
            camera.orthographicSize = height * config.CellSize / (2f * scale);
            camera.aspect = width / (float)height;
            camera.transform.SetPositionAndRotation(new Vector3(origin.x + (pan.x + width / (2f * scale)) * config.CellSize,
                origin.y + (pan.y + height / (2f * scale)) * config.CellSize, -10), Quaternion.identity);
            camera.allowMSAA = false;
            camera.allowHDR = false;
        }
    }
}
