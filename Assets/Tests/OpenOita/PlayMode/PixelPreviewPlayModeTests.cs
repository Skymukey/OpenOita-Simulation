using System.Collections;
using NUnit.Framework;
using OpenOita.Preview;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode
{
    // 只验独立模块预览的真实输出，不代替正式M07B、M08或Windows Player验收。
    public sealed class PixelPreviewPlayModeTests
    {
        private GameObject _owner;
        private Texture2D _capture;

        [UnityTest]
        public IEnumerator MaterialPixelsHaveExactOneAndThreeTimesCoverageWithoutSeams()
        {
            if (Application.isBatchMode)
                Assert.Ignore("实际画面读回需要可渲染的Game View；批处理只运行状态及纹素测试。");
#if UNITY_EDITOR
            var gameView = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
            UnityEditor.EditorWindow.GetWindow(gameView).Focus();
#endif
            _owner = new GameObject("像素显示验收");
            var preview = _owner.AddComponent<M05PreviewController>();
            preview.ShowEffects = false;
            preview.ShowHover = false;
            Assert.That(preview.LastError, Is.Null);
            Assert.That(preview.DisplayScale, Is.EqualTo(1));
            Assert.That(preview.ShowGrid, Is.False);
            Assert.That(preview.ShowGeometry, Is.False);
            Assert.That(preview.IsPaused, Is.True);
            Assert.That(preview.LogicalSize, Is.EqualTo(new Vector2Int(256, 256)));
            int initialCount = preview.CellCount;
            Assert.That(initialCount, Is.EqualTo(76 * 64));

            foreach (int scale in new[] { 1, 3 })
            {
                preview.SetDisplayScale(scale);
                // 初态水块为逻辑x=56..87、y=168..199，移动视口确保整个块可见。
                preview.SetDisplayPan(new Vector2Int(56 * scale, 56 * scale));
                yield return null;
                yield return new WaitForEndOfFrame();
                Assert.That(preview.ViewportBounds.width, Is.GreaterThanOrEqualTo(96));
                Assert.That(preview.ViewportBounds.height, Is.GreaterThanOrEqualTo(96));
                Assert.That(preview.LogicalWorldRect.width, Is.EqualTo(256 * scale));
                Assert.That(preview.LogicalWorldRect.height, Is.EqualTo(256 * scale));
                _capture = ScreenCapture.CaptureScreenshotAsTexture();
                Assert.That(_capture.width, Is.EqualTo(Screen.width));
                Assert.That(_capture.height, Is.EqualTo(Screen.height));
                int left = Mathf.RoundToInt(preview.LogicalWorldRect.x) + 56 * scale;
                int top = Mathf.RoundToInt(preview.LogicalWorldRect.y) + 56 * scale;
                int bottom = _capture.height - top - 32 * scale;
                int size = 32 * scale;
                Color32[] pixels = _capture.GetPixels32();
                Color32 water = pixels[(bottom + size / 2) * _capture.width + left + size / 2];
                Assert.That(water.b, Is.GreaterThan(water.r));
                Assert.That(water.b, Is.GreaterThan(water.g));
                for (int y = bottom; y < bottom + size; y++)
                    for (int x = left; x < left + size; x++)
                        Assert.That(pixels[y * _capture.width + x], Is.EqualTo(water),
                            $"{scale}倍水块内部({x},{y})应完整填色，无格间缝或插值。");
                Assert.That(pixels[(bottom + size / 2) * _capture.width + left + size], Is.Not.EqualTo(water));
                Assert.That(pixels[(bottom - 1) * _capture.width + left + size / 2], Is.Not.EqualTo(water));
                Assert.That(preview.CurrentTick, Is.Zero);
                Assert.That(preview.CellCount, Is.EqualTo(initialCount));
                TestContext.WriteLine($"实际输出{_capture.width}×{_capture.height}，倍率{scale}，32×32水像素覆盖{size}×{size}输出像素，材料数{initialCount}，Tick0。");
                Object.Destroy(_capture);
                _capture = null;
            }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTearDown]
        public IEnumerator ReleasePreview()
        {
            if (_capture != null) Object.Destroy(_capture);
            if (_owner != null) Object.Destroy(_owner);
            _capture = null;
            _owner = null;
            yield return null;
        }
    }
}
