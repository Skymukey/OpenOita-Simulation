using System;
using OpenOita.V2.Render;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace OpenOita.Editor
{
    /// <summary>
    /// Editor-only, idempotent setup for the V2 display resources and the existing Renderer2D asset.
    /// It uses Unity serialization APIs so no scene, YAML, or meta file is edited directly.
    /// </summary>
    public static class V2RenderingSetup
    {
        private const string MenuPath = "OpenOita/V2/装配显示资源与Renderer2D";
        private const string ResourceDirectory = "Assets/Resources/OpenOita";
        private const string ResourcePath = ResourceDirectory + "/V2Rendering.asset";
        private const string RendererDataPath = "Assets/Settings/Renderer2D.asset";
        private const string UpdateShaderPath = "Assets/Shaders/V2/IncrementalWorldUpdates.compute";
        private const string TileShaderPath = "Assets/Shaders/V2/WorldPage.shader";
        private const string FireShaderPath = "Assets/Shaders/V2/WorldFire.shader";

        [MenuItem(MenuPath, false, 90)]
        public static void Setup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Busy: V2显示装配必须在非Play模式执行。");

            ComputeShader updateShader = LoadRequired<ComputeShader>(UpdateShaderPath);
            Shader tileShader = LoadRequired<Shader>(TileShaderPath);
            Shader fireShader = LoadRequired<Shader>(FireShaderPath);
            V2RenderingResources resources = GetOrCreateResources();
            AssignResources(resources, updateShader, tileShader, fireShader);
            AttachFeature(resources);
            EditorUtility.SetDirty(resources);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("V2资源缺失：" + path);
            return asset;
        }

        private static V2RenderingResources GetOrCreateResources()
        {
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "OpenOita");
            V2RenderingResources resources = AssetDatabase.LoadAssetAtPath<V2RenderingResources>(ResourcePath);
            if (resources != null) return resources;

            resources = ScriptableObject.CreateInstance<V2RenderingResources>();
            resources.name = "V2Rendering";
            AssetDatabase.CreateAsset(resources, ResourcePath);
            return resources;
        }

        private static void AssignResources(V2RenderingResources resources, ComputeShader updateShader,
            Shader tileShader, Shader fireShader)
        {
            SerializedObject serialized = new SerializedObject(resources);
            serialized.FindProperty("_updateShader").objectReferenceValue = updateShader;
            serialized.FindProperty("_tileShader").objectReferenceValue = tileShader;
            serialized.FindProperty("_fireShader").objectReferenceValue = fireShader;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AttachFeature(V2RenderingResources resources)
        {
            ScriptableRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererDataPath);
            if (rendererData == null) throw new InvalidOperationException("Renderer2D资源缺失：" + RendererDataPath);

            WorldRenderFeatureV2 feature = FindFeature(RendererDataPath);
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<WorldRenderFeatureV2>();
                feature.name = "WorldRenderFeatureV2";
                feature.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(feature, rendererData);
            }

            SerializedObject featureSerialized = new SerializedObject(feature);
            SerializedProperty resourceProperty = featureSerialized.FindProperty("_resources");
            if (resourceProperty != null) resourceProperty.objectReferenceValue = resources;
            featureSerialized.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject rendererSerialized = new SerializedObject(rendererData);
            SerializedProperty features = rendererSerialized.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray)
                throw new InvalidOperationException("Renderer2D缺少m_RendererFeatures序列化字段。");

            int index = FindFeatureIndex(features, feature);
            if (index < 0)
            {
                index = features.arraySize;
                features.arraySize++;
                features.GetArrayElementAtIndex(index).objectReferenceValue = feature;
            }

            long featureLocalId = 0;
            bool hasFeatureLocalId = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature,
                out _, out featureLocalId);
            if (!hasFeatureLocalId)
                throw new InvalidOperationException("无法取得WorldRenderFeatureV2的持久localFileID。");
            SerializedProperty featureMap = rendererSerialized.FindProperty("m_RendererFeatureMap");
            if (featureMap != null && featureMap.isArray)
            {
                if (featureMap.arraySize != features.arraySize) featureMap.arraySize = features.arraySize;
                featureMap.GetArrayElementAtIndex(index).longValue = featureLocalId;
            }
            rendererSerialized.ApplyModifiedPropertiesWithoutUndo();
            SerializedObject verify = new SerializedObject(rendererData);
            SerializedProperty verifyFeatures = verify.FindProperty("m_RendererFeatures");
            if (verifyFeatures == null || index >= verifyFeatures.arraySize ||
                verifyFeatures.GetArrayElementAtIndex(index).objectReferenceValue != feature)
                throw new InvalidOperationException("Renderer2D未持久化WorldRenderFeatureV2引用。");
            SerializedProperty verifyMap = verify.FindProperty("m_RendererFeatureMap");
            if (verifyMap == null || !verifyMap.isArray || index >= verifyMap.arraySize)
                throw new InvalidOperationException("Renderer2D未持久化WorldRenderFeatureV2 localFileID映射。");
            if (verifyMap.GetArrayElementAtIndex(index).longValue != featureLocalId)
                throw new InvalidOperationException("Renderer2D的WorldRenderFeatureV2 localFileID映射错误。");
            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(feature);
        }

        private static WorldRenderFeatureV2 FindFeature(string rendererPath)
        {
            UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(rendererPath);
            foreach (UnityEngine.Object subAsset in subAssets)
                if (subAsset is WorldRenderFeatureV2 feature) return feature;
            return null;
        }

        private static int FindFeatureIndex(SerializedProperty features, WorldRenderFeatureV2 feature)
        {
            for (int i = 0; i < features.arraySize; i++)
                if (features.GetArrayElementAtIndex(i).objectReferenceValue == feature) return i;
            return -1;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, child);
        }
    }
}
