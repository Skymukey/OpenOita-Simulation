using System;
using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Host;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenOita.Editor
{
    public static class OpenOitaMapCreation
    {
        public static OpenOitaMap Create(Scene scene, GameObject parent = null, int width = 256, int height = 256, float cellSize = 0.1f, string assetFolder = "Assets/OpenOita/Maps")
        {
            WorldResult result = new ConfigurationFileStore().ReadSources(Path.Combine(Application.streamingAssetsPath, "OpenOita"), out WorldSources template);
            var document = new SceneEditingDocument();
            if (result.IsSuccess) result = document.CreateEmpty(template, width, height, cellSize);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic.Message);
            document.Data.Export(out WorldSources sources);
            OpenOitaMapAsset asset = CreateAsset(sources, "新地图", assetFolder);
            GameObject go = null;
            try
            {
                go = new GameObject("OpenOita地图");
                go.SetActive(false);
                if (scene.IsValid()) SceneManager.MoveGameObjectToScene(go, scene);
                else scene = go.scene; // 使用Unity Hierarchy当前创建目标，包含非活动的加法场景。
                if (parent != null)
                {
                    if (parent.transform.rotation != Quaternion.identity || parent.transform.lossyScale != Vector3.one || Mathf.Abs(parent.transform.position.z) > 0.00001f)
                        Debug.LogWarning("父对象变换不支持，地图已放在目标场景根部。", parent);
                    else go.transform.SetParent(parent.transform, false);
                }
                var map = go.AddComponent<OpenOitaMap>();
                map.EnsureHost();
                map.SetLevel(asset);
                bool hasRunner = false;
                foreach (OpenOitaMap other in UnityEngine.Object.FindObjectsByType<OpenOitaMap>(FindObjectsSortMode.None))
                    if (other != map && other.isActiveAndEnabled && other.Participate) hasRunner = true;
                map.SetParticipation(!hasRunner);
                map.Host.ConfigureMapDisplay(null);
                map.Host.ConfigureMapShaders(Shader.Find("OpenOita/ChunkDisplay"), Shader.Find("OpenOita/CommittedFlame"));
                go.SetActive(true);
                Undo.RegisterCreatedObjectUndo(go, "创建OpenOita地图");
                EditorSceneManager.MarkSceneDirty(scene);
                Selection.activeGameObject = go;
                return map;
            }
            catch (Exception ex)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                throw new InvalidOperationException("对象创建失败，已创建关卡保留在 " + AssetDatabase.GetAssetPath(asset) + "：" + ex.Message, ex);
            }
        }

        public static OpenOitaMapAsset CreateAsset(WorldSources sources, string name, string destination = "Assets/OpenOita/Maps")
        {
            string folder = "Assets";
            if (!destination.StartsWith("Assets/", StringComparison.Ordinal) || destination.Contains("..")) throw new ArgumentException("关卡路径必须位于Assets。");
            foreach (string segment in destination.Substring(7).Split('/'))
            {
                string next = folder + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(folder, segment);
                folder = next;
            }
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".asset");
            var asset = ScriptableObject.CreateInstance<OpenOitaMapAsset>();
            WorldResult result = asset.ReplaceSources(sources);
            if (!result.IsSuccess) { UnityEngine.Object.DestroyImmediate(asset); throw new InvalidOperationException(result.Diagnostic.Message); }
            try
            {
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssetIfDirty(asset);
                if (!File.Exists(path)) throw new IOException("关卡没有成功写入磁盘。");
                return asset;
            }
            catch (Exception ex)
            {
                if (asset != null && !EditorUtility.IsPersistent(asset)) UnityEngine.Object.DestroyImmediate(asset);
                throw new IOException("关卡创建失败，请检查并保留路径 " + path + "：" + ex.Message, ex);
            }
        }

        [MenuItem("GameObject/OpenOita/地图", false, 10)]
        private static void CreateFromMenu(MenuCommand command)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            GameObject parent = command.context as GameObject;
            Scene scene = parent != null ? parent.scene : default;
            SceneAsset sceneAsset = command.context as SceneAsset ?? Selection.activeObject as SceneAsset;
            if (parent == null && sceneAsset != null) scene = SceneManager.GetSceneByPath(AssetDatabase.GetAssetPath(sceneAsset));
            try { Create(scene, parent); }
            catch (Exception ex) { EditorUtility.DisplayDialog("地图创建失败", ex.Message, "确定"); }
        }
    }

    [CustomEditor(typeof(OpenOitaMap))]
    public sealed class OpenOitaMapEditor : UnityEditor.Editor
    {
        private string _feedback;
        private int _width = 256, _height = 256;
        private float _cellSize = 0.1f;
        private void OnEnable()
        {
            var map = target as OpenOitaMap;
            if (map != null && map.Level != null && map.Level.BuildEditingData(out var data).IsSuccess)
            { _width = data.Config.Width; _height = data.Config.Height; _cellSize = data.Config.CellSize; }
        }
        public override void OnInspectorGUI()
        {
            var map = (OpenOitaMap)target;
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_level"), new GUIContent("关卡资产"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_participate"), new GUIContent("参与运行"));
                if (EditorGUI.EndChangeCheck()) SceneMaterialEditorWindow.EndActiveStroke();
                serializedObject.ApplyModifiedProperties();
                if (map.Level == null)
                {
                    EditorGUILayout.HelpBox("请绑定合法关卡资产。不会自动读取旧三文件模板。", MessageType.Warning);
                    if (GUILayout.Button("创建并绑定空白关卡"))
                    {
                        var document = new SceneEditingDocument();
                        WorldResult result = new ConfigurationFileStore().ReadSources(Path.Combine(Application.streamingAssetsPath, "OpenOita"), out WorldSources source);
                        if (result.IsSuccess) result = document.CreateEmpty(source, 256, 256, 0.1f);
                        if (result.IsSuccess)
                        {
                            document.Data.Export(out source);
                            try { Assign(map, OpenOitaMapCreation.CreateAsset(source, "新地图")); }
                            catch (Exception ex) { _feedback = ex.Message; }
                        }
                        else Report(result);
                    }
                }
                else
                {
                    var session = MapEditingSession.For(map.Level);
                    if (session.Document.Data != null)
                    {
                        var data = session.Document.Data;
                        EditorGUILayout.LabelField($"{data.Config.Width}×{data.Config.Height}格 ｜ cellSize={data.Config.CellSize} ｜ 材料{data.Count}格");
                        if (data.Count == 0)
                        {
                            _width = EditorGUILayout.IntField("空白画布宽（格）", _width);
                            _height = EditorGUILayout.IntField("空白画布高（格）", _height);
                            _cellSize = EditorGUILayout.FloatField("每格世界尺寸", _cellSize);
                            if (GUILayout.Button("应用空白画布尺寸"))
                            {
                                var empty = new SceneEditingDocument();
                                WorldResult result = empty.CreateEmpty(map.Level.Sources, _width, _height, _cellSize);
                                if (result.IsSuccess) { empty.Data.Export(out var resized); result = session.Import(resized); }
                                Report(result);
                            }
                        }
                    }
                    else Report(session.LastResult);
                    EditorGUILayout.HelpBox("复制地图对象默认共享关卡：绘制和Undo会同时改变所有引用者。独立编辑请先“复制为独立关卡”。", MessageType.Info);
                    EditorGUILayout.LabelField("关卡文件", AssetDatabase.GetAssetPath(map.Level));
                    EditorGUILayout.LabelField("磁盘状态", EditorUtility.IsDirty(map.Level) || session.Pending ? "有未保存修改" : "已保存");
                    if (GUILayout.Button("编辑地图")) SceneMaterialEditorWindow.EditMap(map);
                    if (GUILayout.Button("保存关卡")) { SceneMaterialEditorWindow.EndActiveStroke(); Report(session.Save()); }
                    if (GUILayout.Button("从磁盘重新读取"))
                    {
                        SceneMaterialEditorWindow.EndActiveStroke();
                        if (session.ConfirmReplacement()) Report(session.ReloadFromDisk());
                    }
                    if (GUILayout.Button("导入JSON")) Import(session);
                    if (GUILayout.Button("导出JSON到新目录")) Export(session, false);
                    if (GUILayout.Button("更新已有JSON导出")) Export(session, true);
                    if (GUILayout.Button("复制为独立关卡"))
                    {
                        SceneMaterialEditorWindow.EndActiveStroke();
                        WorldResult result = session.Commit();
                        if (result.IsSuccess)
                        {
                            try { Assign(map, OpenOitaMapCreation.CreateAsset(map.Level.Sources, map.Level.name + "副本")); }
                            catch (Exception ex) { _feedback = ex.Message; }
                        }
                        else Report(result);
                    }
                }
                if (GUILayout.Button("正式试玩（使用本对象Host）")) StartTrial(map);
            }
            WorldResult valid = map.ValidateTransform();
            EditorGUILayout.HelpBox(valid.IsSuccess ? $"地图原点 {map.Origin}；Play创建后固定。" : valid.Diagnostic.Message,
                valid.IsSuccess ? MessageType.Info : MessageType.Warning);
            EditorGUILayout.HelpBox("关卡内容保存在.asset；引用与位置保存在.unity。请同时保存关卡和场景。Host像素视图仅调整显式绑定摄像机；已有Main Camera不会自动移动。旧独立Host仍按原三文件设置启动。", MessageType.Info);
            if (Application.isPlaying && map.Host.World != null && GUILayout.Button("Reset")) Report(map.Host.ResetWorld());
            if (!string.IsNullOrEmpty(_feedback)) EditorGUILayout.HelpBox(_feedback, MessageType.Info);
        }

        public static void Assign(OpenOitaMap map, OpenOitaMapAsset asset)
        {
            SceneMaterialEditorWindow.EndActiveStroke();
            Undo.RecordObject(map, "更换地图关卡"); map.SetLevel(asset);
            PrefabUtility.RecordPrefabInstancePropertyModifications(map);
            EditorSceneManager.MarkSceneDirty(map.gameObject.scene);
        }

        public static void StartTrial(OpenOitaMap map)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            SceneMaterialEditorWindow.EndActiveStroke();
            if (!MapEditingSession.FlushAll()) { EditorUtility.DisplayDialog("无法试玩", "有待处理编辑未写回关卡，请检查错误后重试。", "确定"); return; }
            WorldResult valid = map.ValidateInput(out _);
            if (!valid.IsSuccess) { EditorUtility.DisplayDialog("无法试玩", valid.Diagnostic.Message, "确定"); return; }
            if (!map.Participate || !OpenOitaMap.ValidateSingleRunner().IsSuccess)
            {
                if (!EditorUtility.DisplayDialog("选择唯一运行地图", "将当前地图设为唯一参与运行的地图？", "设为唯一并试玩", "取消")) return;
                foreach (OpenOitaMap other in UnityEngine.Object.FindObjectsByType<OpenOitaMap>(FindObjectsSortMode.None))
                {
                    Undo.RecordObject(other, "选择运行地图"); other.SetParticipation(other == map);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(other);
                    EditorSceneManager.MarkSceneDirty(other.gameObject.scene);
                }
            }
            Selection.activeGameObject = map.gameObject;
            EditorApplication.isPlaying = true;
        }

        private void Import(MapEditingSession session)
        {
            SceneMaterialEditorWindow.EndActiveStroke();
            string directory = EditorUtility.OpenFolderPanel("导入固定三文件目录（复制内容，不自动同步）", Application.dataPath, "");
            if (string.IsNullOrEmpty(directory) || !session.ConfirmReplacement()) return;
            WorldResult result = new ConfigurationFileStore().ReadSources(directory, out WorldSources sources);
            Report(result.IsSuccess ? session.Import(sources) : result);
        }

        private void Export(MapEditingSession session, bool update)
        {
            SceneMaterialEditorWindow.EndActiveStroke();
            WorldResult result = session.Commit();
            if (!result.IsSuccess) { Report(result); return; }
            string path = update ? EditorUtility.OpenFolderPanel("选择匹配的已导出目录", Application.dataPath, "") :
                EditorUtility.SaveFolderPanel("指定不存在的新目录（已存在则拒绝）", Application.dataPath, session.Asset.name);
            if (string.IsNullOrEmpty(path)) return;
            Report(update ? session.Document.Save(path) : session.Document.SaveNewDirectory(path));
            string project = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path) + Path.DirectorySeparatorChar;
            if (full.StartsWith(project, StringComparison.OrdinalIgnoreCase))
                foreach (string name in new[] { "materials.json", "world_config.json", "scene.json" })
                    if (File.Exists(Path.Combine(path, name))) AssetDatabase.ImportAsset("Assets/" + full.Substring(project.Length).Replace('\\', '/') + name);
        }

        private void Report(WorldResult result) { _feedback = result.IsSuccess ? "操作成功。场景引用和位置请另保存场景；JSON导出是独立操作。" : result.ErrorCode + " / " + result.Diagnostic.FileName + " / " + result.Diagnostic.Message; }
    }

    [CustomEditor(typeof(WorldHost))]
    public sealed class MapWorldHostEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var host = (WorldHost)target;
            if (host.GetComponent<OpenOitaMap>() == null) { DrawDefaultInspector(); return; }
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                Draw("automatic", "自动推进"); Draw("freezeBodyRotation", "禁止刚体旋转（创建时生效）");
                Draw("displayCamera", "显示摄像机"); Draw("pixelView", "精确像素视图（调整绑定摄像机）");
                Draw("pixelScale", "整数显示倍率"); Draw("pixelPan", "平移（逻辑格）");
                Draw("committedMaterialShader", "材料显示Shader"); Draw("committedFlameShader", "火焰显示Shader");
            }
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("来源和原点由地图组件提供。绑定摄像机后才会按像素视图调整它；未绑定时沿用场景的自由摄像机视角。", MessageType.Info);
            if (Application.isPlaying)
                EditorGUILayout.LabelField(host.World != null ? $"{host.World.Lifecycle} ｜ generation={host.World.Version.Generation} ｜ Tick={host.World.Version.CommittedTick}" : host.LastResult.Diagnostic.Message ?? "不参与运行");
        }
        private void Draw(string field, string label) => EditorGUILayout.PropertyField(serializedObject.FindProperty(field), new GUIContent(label));
    }
}
