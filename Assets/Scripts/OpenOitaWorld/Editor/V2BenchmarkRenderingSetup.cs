using System;
using OpenOita.V2.Validation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OpenOita.Editor
{
    /// <summary>
    /// Installs the benchmark-only timestamp feature into an isolated Renderer2D and
    /// URP asset. The production Renderer2D/UniversalRP assets are never modified.
    /// The returned scope temporarily activates the isolated pipeline for a build,
    /// overrides every configured QualitySettings level, and restores the editor's
    /// previous pipeline and each quality-level override on Dispose.
    /// </summary>
    public static class V2BenchmarkRenderingSetup
    {
        private const string MainRendererPath = "Assets/Settings/Renderer2D.asset";
        private const string MainPipelinePath = "Assets/Settings/UniversalRP.asset";
        private const string BenchmarkRendererPath = "Assets/Settings/V2BenchmarkRenderer2D.asset";
        private const string BenchmarkPipelinePath = "Assets/Settings/V2BenchmarkUniversalRP.asset";

        public static IDisposable ActivateForBenchmark()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Busy: benchmark RendererGraph装配必须在非Play模式执行。");

            ScriptableRendererData rendererData = EnsureBenchmarkRendererData();
            UniversalRenderPipelineAsset pipeline = EnsureBenchmarkPipeline(rendererData);
            string previousDefault = AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline);
            string previousQuality = AssetDatabase.GetAssetPath(QualitySettings.renderPipeline);
            int previousQualityLevel = QualitySettings.GetQualityLevel();
            string[] qualityNames = QualitySettings.names;
            string[] previousQualityPipelines =
                new string[qualityNames == null ? 0 : qualityNames.Length];
            for (int i = 0; i < previousQualityPipelines.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                previousQualityPipelines[i] = AssetDatabase.GetAssetPath(QualitySettings.renderPipeline);
            }
            QualitySettings.SetQualityLevel(previousQualityLevel, false);

            var scope = new PipelineScope(previousDefault, previousQuality, previousQualityLevel,
                previousQualityPipelines);
            try
            {
                scope.Activate(pipeline);
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        private static ScriptableRendererData EnsureBenchmarkRendererData()
        {
            ScriptableRendererData source = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(MainRendererPath);
            if (source == null) throw new InvalidOperationException("Renderer2D资源缺失：" + MainRendererPath);
            if (AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(BenchmarkRendererPath) == null &&
                !AssetDatabase.CopyAsset(MainRendererPath, BenchmarkRendererPath))
                throw new InvalidOperationException("无法复制隔离Renderer2D资源：" + BenchmarkRendererPath);

            ScriptableRendererData benchmark =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(BenchmarkRendererPath);
            if (benchmark == null) throw new InvalidOperationException("隔离Renderer2D资源加载失败：" + BenchmarkRendererPath);
            NativeGpuTimingFeature feature = FindFeature<NativeGpuTimingFeature>(BenchmarkRendererPath);
            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<NativeGpuTimingFeature>();
                feature.name = "NativeGpuTimingFeature";
                feature.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(feature, benchmark);
            }
            feature.SetActive(true);
            AttachFeature(benchmark, feature);
            // Serialized feature-list edits must invalidate the renderer instance;
            // otherwise an already-loaded URP asset can keep the pre-attach pass set.
            benchmark.SetDirty();
            EditorUtility.SetDirty(benchmark);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(BenchmarkRendererPath, ImportAssetOptions.ForceUpdate);
            return benchmark;
        }

        private static UniversalRenderPipelineAsset EnsureBenchmarkPipeline(ScriptableRendererData rendererData)
        {
            if (AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(BenchmarkPipelinePath) == null &&
                !AssetDatabase.CopyAsset(MainPipelinePath, BenchmarkPipelinePath))
                throw new InvalidOperationException("无法复制隔离UniversalRP资源：" + BenchmarkPipelinePath);

            UniversalRenderPipelineAsset pipeline =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(BenchmarkPipelinePath);
            if (pipeline == null) throw new InvalidOperationException("隔离UniversalRP资源加载失败：" + BenchmarkPipelinePath);
            SerializedObject serialized = new SerializedObject(pipeline);
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            if (list == null || !list.isArray)
                throw new InvalidOperationException("UniversalRP缺少m_RendererDataList序列化字段。");
            list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = rendererData;
            SerializedProperty defaultIndex = serialized.FindProperty("m_DefaultRendererIndex");
            if (defaultIndex != null) defaultIndex.intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(BenchmarkPipelinePath, ImportAssetOptions.ForceUpdate);
            return pipeline;
        }

        private static void AttachFeature(ScriptableRendererData rendererData, NativeGpuTimingFeature feature)
        {
            SerializedObject serialized = new SerializedObject(rendererData);
            SerializedProperty features = serialized.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray)
                throw new InvalidOperationException("隔离Renderer2D缺少m_RendererFeatures序列化字段。");
            int index = FindFeatureIndex(features, feature);
            if (index < 0)
            {
                index = features.arraySize;
                features.arraySize++;
                features.GetArrayElementAtIndex(index).objectReferenceValue = feature;
            }

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
                throw new InvalidOperationException("无法取得NativeGpuTimingFeature的持久localFileID。");
            SerializedProperty featureMap = serialized.FindProperty("m_RendererFeatureMap");
            if (featureMap == null || !featureMap.isArray)
                throw new InvalidOperationException("隔离Renderer2D缺少m_RendererFeatureMap长整型数组。");
            if (featureMap.arraySize != features.arraySize) featureMap.arraySize = features.arraySize;
            featureMap.GetArrayElementAtIndex(index).longValue = localId;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject verify = new SerializedObject(rendererData);
            SerializedProperty verifyFeatures = verify.FindProperty("m_RendererFeatures");
            SerializedProperty verifyMap = verify.FindProperty("m_RendererFeatureMap");
            if (verifyFeatures == null || verifyMap == null || !verifyMap.isArray ||
                index >= verifyFeatures.arraySize || index >= verifyMap.arraySize ||
                verifyFeatures.GetArrayElementAtIndex(index).objectReferenceValue != feature ||
                verifyMap.GetArrayElementAtIndex(index).longValue != localId)
                throw new InvalidOperationException("隔离Renderer2D未持久化NativeGpuTimingFeature引用或localFileID。");
        }

        private static T FindFeature<T>(string rendererPath) where T : ScriptableRendererFeature
        {
            UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(rendererPath);
            foreach (UnityEngine.Object subAsset in subAssets)
                if (subAsset is T feature) return feature;
            return null;
        }

        private static int FindFeatureIndex(SerializedProperty features, UnityEngine.Object feature)
        {
            for (int i = 0; i < features.arraySize; i++)
                if (features.GetArrayElementAtIndex(i).objectReferenceValue == feature) return i;
            return -1;
        }

        private sealed class PipelineScope : IDisposable
        {
            private readonly string _previousDefault;
            private readonly string _previousQuality;
            private readonly int _previousQualityLevel;
            private readonly string[] _previousQualityPipelines;
            private bool _disposed;

            public PipelineScope(string previousDefault, string previousQuality,
                int previousQualityLevel, string[] previousQualityPipelines)
            {
                _previousDefault = previousDefault;
                _previousQuality = previousQuality;
                _previousQualityLevel = previousQualityLevel;
                _previousQualityPipelines = previousQualityPipelines;
            }

            public void Activate(UniversalRenderPipelineAsset pipeline)
            {
                for (int i = 0; i < _previousQualityPipelines.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = pipeline;
                }
                QualitySettings.SetQualityLevel(_previousQualityLevel, false);
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                // BuildPlayer can unload assets. A saved UnityEngine.Object reference
                // may then compare as null; reload by persistent path instead of
                // accidentally replacing every production quality pipeline with null.
                for (int i = 0; i < _previousQualityPipelines.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = RestoreAsset(_previousQualityPipelines[i]);
                }
                QualitySettings.SetQualityLevel(_previousQualityLevel, false);
                GraphicsSettings.defaultRenderPipeline = RestoreAsset(_previousDefault);
                QualitySettings.renderPipeline = RestoreAsset(_previousQuality);
            }

            private static RenderPipelineAsset RestoreAsset(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                RenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                if (asset == null) throw new InvalidOperationException("无法恢复渲染管线资源：" + path);
                return asset;
            }
        }
    }
}
