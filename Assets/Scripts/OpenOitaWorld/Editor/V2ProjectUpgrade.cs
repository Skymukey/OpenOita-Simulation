using System;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpenOita.Editor
{
    public static class V2ProjectUpgrade
    {
        public const string MapPath = "Assets/OpenOita/Maps/BasicSandbox-V2.asset";
        public const string ScenePath = "Assets/Scenes/BasicSandboxV2.unity";

        [MenuItem("OpenOita/V2/装配并另存当前基础关卡")]
        public static void UpgradeBasicSandbox()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先停止Play，再装配V2资源。");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            V2RenderingSetup.Setup();
            var source = AssetDatabase.LoadAssetAtPath<OpenOitaMapAsset>("Assets/OpenOita/Maps/BasicSandbox.asset");
            if (source == null) throw new InvalidOperationException("缺少当前BasicSandbox关卡资产。");
            WorldResult converted = WorldV2SourceConverter.Convert(source.Sources, out WorldSources sources);
            if (!converted.IsSuccess) throw new InvalidOperationException(converted.Diagnostic.Message);
            var target = AssetDatabase.LoadAssetAtPath<OpenOitaMapAsset>(MapPath);
            if (target == null)
            {
                target = ScriptableObject.CreateInstance<OpenOitaMapAsset>();
                WorldResult replaced = target.ReplaceSources(sources);
                if (!replaced.IsSuccess) throw new InvalidOperationException(replaced.Diagnostic.Message);
                AssetDatabase.CreateAsset(target, MapPath);
            }
            // 已另存的V2关卡可能有用户编辑，不覆盖；原始关卡与场景始终保留。
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                var scene = EditorSceneManager.OpenScene("Assets/Scenes/BasicSandbox.unity", OpenSceneMode.Single);
                bool bound = false;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (OpenOita.Host.OpenOitaMap map in root.GetComponentsInChildren<OpenOita.Host.OpenOitaMap>(true))
                    {
                        if (map.Level != source) continue;
                        map.SetLevel(target);
                        WorldHost host = map.GetComponent<WorldHost>();
                        if (host != null)
                        {
                            host.ConfigureDrive(true, false);
                            Camera camera = Camera.main;
                            if (camera != null) host.ConfigurePixelView(camera, 1, Vector2Int.zero);
                        }
                        EditorUtility.SetDirty(map); if (host != null) EditorUtility.SetDirty(host);
                        bound = true;
                    }
                if (!bound) throw new InvalidOperationException("基础场景中没有找到原始地图绑定。");
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("V2场景另存失败。");
            }
            AssetDatabase.SaveAssets();
            Debug.Log("OpenOita V2：显示资源已装配，关卡另存为 " + ScenePath);
        }

        // Explicit viewport operation, separate from idempotent map conversion so a
        // later build does not silently replace a user's edited camera settings.
        [MenuItem("OpenOita/V2/基础关卡摄像机设为1比1像素视图")]
        public static void ConfigureBasicSandboxViewport()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先停止Play再配置V2摄像机。");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Camera camera = Camera.main;
            if (camera == null) throw new InvalidOperationException("V2基础场景缺少已启用的Main Camera。");
            bool configured = false;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (OpenOita.Host.OpenOitaMap map in root.GetComponentsInChildren<OpenOita.Host.OpenOitaMap>(true))
                {
                    WorldHost host = map.Host;
                    if (host == null) continue;
                    host.ConfigurePixelView(camera, 1, Vector2Int.zero);
                    EditorUtility.SetDirty(host);
                    configured = true;
                }
            if (!configured) throw new InvalidOperationException("V2基础场景没有地图Host。");
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("V2摄像机配置保存失败。");
        }
    }
}
