using System.Collections;
using System.Reflection;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Editor;
using OpenOita.Tests.Fixtures;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenOita.Tests.EditMode.M08
{
    public sealed class ScenePainterWindowTests
    {
        private SceneMaterialEditorWindow _window;
        private SceneEditingDocument _document;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private void Set(string field, object value) => typeof(SceneMaterialEditorWindow).GetField(field, Private).SetValue(_window, value);
        private T Get<T>(string field) => (T)typeof(SceneMaterialEditorWindow).GetField(field, Private).GetValue(_window);
        private void Invoke(string method) => typeof(SceneMaterialEditorWindow).GetMethod(method, Private).Invoke(_window, null);

        private IEnumerator Open()
        {
            _window = ScriptableObject.CreateInstance<SceneMaterialEditorWindow>();
            _window.titleContent = new GUIContent("画笔交互验证");
            _window.position = new Rect(160, 100, 720, 800);
            _document = Get<SceneEditingDocument>("_document");
            Assert.That(_document.CreateEmpty(BaselineSources.Read(), 128, 128, 0.1f).IsSuccess, Is.True);
            Set("_creationExpanded", false); Set("_referenceHost", null); Set("_scale", 3);
            Set("_brushSize", 1); Set("_roundBrush", false); Invoke("BuildPalette");
            // Utility窗口没有可停靠窗口的标签栏坐标偏移，SendEvent与OnGUI使用相同原点。
            _window.ShowUtility(); _window.Focus(); _window.Repaint();
            yield return null; yield return null;
        }

        [TearDown] public void Close() { if (_window != null) _window.Close(); }

        private void Mouse(EventType type, int x, int y, bool alt = false)
        {
            Rect rect = Get<Rect>("_previewRect");
            float step = 3 / EditorGUIUtility.pixelsPerPoint;
            _window.SendEvent(new Event { type = type, button = 0, alt = alt,
                mousePosition = new Vector2(rect.x + (x + 0.5f) * step, rect.yMax - (y + 0.5f) * step) });
        }

        [UnityTest] public IEnumerator WindowFastStrokeAndEraseUseContinuousSelection()
        {
            yield return Open();
            Mouse(EventType.MouseDown, 5, 5); Mouse(EventType.MouseDrag, 30, 5); Mouse(EventType.MouseUp, 30, 5);
            Assert.That(_document.Data.Count, Is.EqualTo(26));
            for (int x = 5; x <= 30; x++) Assert.That(_document.Data.MaterialAt(new Vector2Int(x, 5)), Is.Not.Zero,
                "实际选格：" + string.Join(",", System.Linq.Enumerable.Select(_document.Data.Snapshot().Cells, c => c.Position.ToString())));
            Set("_operation", SceneEditOperation.Erase);
            Mouse(EventType.MouseDown, 30, 5); Mouse(EventType.MouseDrag, 5, 5); Mouse(EventType.MouseUp, 5, 5);
            Assert.That(_document.Data.Count, Is.Zero);
            Assert.That(Get<bool>("_dragging"), Is.False);
        }

        [UnityTest] public IEnumerator WindowRectangleCommitsOnReleaseAndEscapeCancels()
        {
            yield return Open(); Set("_rectangle", true); _window.Repaint(); yield return null;
            Mouse(EventType.MouseDown, 10, 10); Mouse(EventType.MouseDrag, 15, 13);
            Assert.That(_document.Data.Count, Is.Zero);
            Mouse(EventType.MouseUp, 15, 13);
            Assert.That(_document.Data.Count, Is.EqualTo(24));
            Mouse(EventType.MouseDown, 30, 10); Mouse(EventType.MouseDrag, 40, 20);
            _window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
            Mouse(EventType.MouseUp, 40, 20);
            Assert.That(_document.Data.Count, Is.EqualTo(24));
            Assert.That(Get<bool>("_dragging"), Is.False);
            Set("_operation", SceneEditOperation.Erase);
            Mouse(EventType.MouseDown, 15, 13); Mouse(EventType.MouseUp, 10, 10);
            Assert.That(_document.Data.Count, Is.Zero);
        }

        [UnityTest] public IEnumerator WindowOutsideReleaseCancelsRectangleAndAltDoesNotPaint()
        {
            yield return Open(); Set("_rectangle", true); _window.Repaint(); yield return null;
            Mouse(EventType.MouseDown, 10, 10); Mouse(EventType.MouseDrag, 20, 20);
            _window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(-5, -5) });
            Assert.That(_document.Data.Count, Is.Zero);
            Assert.That(Get<bool>("_dragging"), Is.False);
            Set("_rectangle", false); _window.Repaint(); yield return null;
            Mouse(EventType.MouseDown, 10, 10, true); Mouse(EventType.MouseUp, 10, 10, true);
            Assert.That(_document.Data.Count, Is.Zero);
        }
    }
}
