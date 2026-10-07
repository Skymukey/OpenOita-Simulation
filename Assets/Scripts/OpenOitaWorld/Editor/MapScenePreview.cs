using System.Collections.Generic;
using OpenOita.Host;
using OpenOita.Render;
using UnityEditor;
using UnityEngine;

namespace OpenOita.Editor
{
    [InitializeOnLoad]
    public static class MapScenePreview
    {
        private sealed class Preview
        {
            public CommittedWorldRenderer Renderer;
            public OpenOita.Data.OpenOitaMapAsset Asset;
            public OpenOita.Data.SceneMaterialData Data;
            public int Revision;
            public Vector2 Origin;
            public int Layer;
        }
        private static readonly Dictionary<OpenOitaMap, Preview> Previews = new();
        private static readonly Dictionary<OpenOita.Data.OpenOitaMapAsset, OpenOita.Contracts.WorldSources> PlaySources = new();
        private static double _next;
        public static int PreviewCount => Previews.Count;
        static MapScenePreview()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.playModeStateChanged += PlayChanged;
            Undo.undoRedoPerformed += UndoChanged;
            SceneView.duringSceneGui += DrawBounds;
        }
        private static void DrawBounds(SceneView view)
        {
            if (Application.isPlaying || Event.current.type != EventType.Repaint) return;
            foreach (var pair in Previews)
            {
                if (pair.Key == null || pair.Value.Data == null) continue;
                var config = pair.Value.Data.Config;
                Vector3 min = pair.Key.Origin;
                float width = config.Width * config.CellSize, height = config.Height * config.CellSize;
                Handles.DrawSolidRectangleWithOutline(new[] { min, min + Vector3.right * width,
                    min + new Vector3(width, height, 0), min + Vector3.up * height }, Color.clear, Color.gray);
            }
        }
        private static void BeforeReload() { SceneMaterialEditorWindow.EndActiveStroke(); MapEditingSession.FlushAll(); Release(); }
        private static void UndoChanged()
        {
            if (Application.isPlaying)
            {
                foreach (var pair in PlaySources)
                    if (pair.Key != null) pair.Key.ReplaceSources(pair.Value);
                Debug.LogWarning("OpenOita：Play期间不允许关卡编辑Undo，已恢复创建时的编辑初态。");
                return;
            }
            Release(); SceneMaterialEditorWindow.RefreshMapBinding(); SceneView.RepaintAll();
        }
        private static void PlayChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                SceneMaterialEditorWindow.EndActiveStroke();
                if (!MapEditingSession.FlushAll())
                {
                    Debug.LogError("OpenOita：待处理编辑未写回，已阻止Play；请检查关卡权限及编辑错误。");
                    EditorApplication.isPlaying = false;
                }
                Release();
            }
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                foreach (WorldHost host in Object.FindObjectsByType<WorldHost>(FindObjectsSortMode.None)) host.CloseWorld();
            }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                PlaySources.Clear();
                foreach (OpenOitaMap map in Resources.FindObjectsOfTypeAll<OpenOitaMap>())
                    if (map.gameObject.scene.IsValid() && map.Level != null) PlaySources[map.Level] = map.Level.Sources;
            }
            if (state == PlayModeStateChange.EnteredEditMode) { PlaySources.Clear(); MapEditingSession.ClearCaches(); SceneMaterialEditorWindow.RefreshMapBinding(); _next = 0; }
        }
        private static void Release()
        {
            foreach (Preview preview in Previews.Values) preview.Renderer?.Dispose();
            Previews.Clear();
        }
        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 0.1;
            var alive = new HashSet<OpenOitaMap>();
            foreach (OpenOitaMap map in Object.FindObjectsByType<OpenOitaMap>(FindObjectsSortMode.None))
            {
                if (!map.isActiveAndEnabled || map.Level == null || !map.ValidateTransform().IsSuccess || !map.gameObject.scene.IsValid()) continue;
                var session = MapEditingSession.For(map.Level);
                var data = session.Document.Data;
                if (data == null) continue;
                alive.Add(map);
                if (!Previews.TryGetValue(map, out Preview preview)) Previews.Add(map, preview = new Preview());
                if (preview.Renderer == null || preview.Asset != map.Level || preview.Data != data || preview.Origin != map.Origin || preview.Layer != map.gameObject.layer)
                {
                    preview.Renderer?.Dispose();
                    preview.Renderer = new CommittedWorldRenderer { DisplayLayer = map.gameObject.layer };
                    var result = preview.Renderer.Prepare(new InitialSceneRenderView(data, map.Origin));
                    if (!result.IsSuccess) { preview.Renderer.Dispose(); preview.Renderer = null; continue; }
                    preview.Renderer.DisplayRoot.hideFlags = HideFlags.HideAndDontSave;
                    preview.Asset = map.Level; preview.Data = data; preview.Origin = map.Origin; preview.Layer = map.gameObject.layer;
                    preview.Revision = data.Revision;
                }
                else if (preview.Revision != data.Revision)
                {
                    using var prepared = preview.Renderer.PrepareCommit(new InitialSceneRenderView(data, map.Origin), default);
                    if (prepared.Result.IsSuccess) { prepared.Adopt(); preview.Revision = data.Revision; }
                }
                preview.Renderer.FlushFrame();
            }
            var removed = new List<OpenOitaMap>();
            foreach (var pair in Previews) if (pair.Key == null || !alive.Contains(pair.Key)) { pair.Value.Renderer?.Dispose(); removed.Add(pair.Key); }
            foreach (var map in removed) Previews.Remove(map);
            var referenced = new HashSet<OpenOita.Data.OpenOitaMapAsset>();
            foreach (OpenOitaMap map in Resources.FindObjectsOfTypeAll<OpenOitaMap>())
                if (map.gameObject.scene.IsValid() && map.Level != null) referenced.Add(map.Level);
            MapEditingSession.TrimUnusedCaches(referenced);
        }
    }
}
