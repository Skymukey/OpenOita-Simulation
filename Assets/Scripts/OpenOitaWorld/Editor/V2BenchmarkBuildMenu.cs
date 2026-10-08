using System;
using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenOita.Editor
{
    /// <summary>Creates and builds only the independent V2 benchmark scene.</summary>
    public static class V2BenchmarkBuildMenu
    {
        public static void BuildFinalPlayers()
        {
            BuildBenchmarkPlayer();
            BuildAllocationValidationPlayer();
            BuildPlayablePlayer();
        }

        public static void BuildTimingAndPlayablePlayers()
        {
            BuildBenchmarkPlayer();
            BuildPlayablePlayer();
        }

        private const string ScenePath = "Assets/Scenes/OpenOitaV2Benchmark.unity";
        private const string BuildPath = "Builds/V2Validation/OpenOitaV2Benchmark.exe";
        private const string AllocationBuildPath = "Builds/V2Allocation/OpenOitaV2AllocationValidation.exe";
        private const string CurrentSourcePath = "Assets/OpenOita/Maps/BasicSandbox.asset";
        private const string CurrentBenchmarkPath = "Assets/Resources/OpenOita/BenchmarkCurrent.asset";

        [MenuItem("OpenOita/V2/性能验收/创建独立验证场景", false, 90)]
        public static void CreateBenchmarkScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureBenchmarkCurrentAsset();
            SceneSetup[] previous = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                GameObject host = new GameObject("OpenOitaV2BenchmarkRunner");
                host.AddComponent<OpenOita.V2.Validation.V2BenchmarkRunner>();
                GameObject cameraObject = new GameObject("OpenOitaV2BenchmarkCamera");
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                cameraObject.transform.position = new Vector3(0, 0, -10);
                cameraObject.transform.rotation = Quaternion.identity;
                GameObject lightObject = new GameObject("OpenOitaV2BenchmarkDirectionalLight");
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.Refresh();
                Debug.Log("OpenOita V2 benchmark scene created: " + ScenePath +
                    " (camera.enabled=false, directionalLight.enabled=" + light.enabled + ")");
            }
            finally
            {
                bool canRestore = !Application.isBatchMode && previous != null && previous.Length != 0;
                if (canRestore) foreach (SceneSetup item in previous) if (string.IsNullOrEmpty(item.path)) canRestore = false;
                if (canRestore) EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        [MenuItem("OpenOita/V2/性能验收/构建Windows64验证Player", false, 91)]
        public static void BuildBenchmarkPlayer()
        {
            PlayerSettings.enableFrameTimingStats = true;
            EnsureBenchmarkCurrentAsset();
            if (!File.Exists(ScenePath)) CreateBenchmarkScene();
            string directory = Path.GetDirectoryName(BuildPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            BuildReport report;
            using (new DirectGraphicsBuildScope())
            using (V2BenchmarkRenderingSetup.ActivateForBenchmark())
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = BuildPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                });
            }
            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log("OpenOita V2 benchmark Player built: " + BuildPath + " (" + report.summary.totalSize + " bytes)");
            else
                Debug.LogError("OpenOita V2 benchmark Player build failed: " + report.summary.result);
        }

        [MenuItem("OpenOita/V2/性能验收/构建Allocation验证Development Player", false, 92)]
        public static void BuildAllocationValidationPlayer()
        {
            PlayerSettings.enableFrameTimingStats = true;
            EnsureBenchmarkCurrentAsset();
            if (!File.Exists(ScenePath)) CreateBenchmarkScene();
            string directory = Path.GetDirectoryName(AllocationBuildPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            BuildReport report;
            using (new DirectGraphicsBuildScope())
            using (V2BenchmarkRenderingSetup.ActivateForBenchmark())
            {
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = AllocationBuildPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                });
            }
            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log("OpenOita V2 allocation validation Development Player built: " + AllocationBuildPath +
                    " (" + report.summary.totalSize + " bytes)");
            else
                Debug.LogError("OpenOita V2 allocation validation Player build failed: " + report.summary.result);
        }

        [MenuItem("OpenOita/V2/性能验收/创建独立验证场景", true)]
        private static bool ValidateCreate() => !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("OpenOita/V2/性能验收/构建Windows64验证Player", true)]
        private static bool ValidateBuild() => !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("OpenOita/V2/性能验收/构建Allocation验证Development Player", true)]
        private static bool ValidateBuildAllocation() => !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("OpenOita/V2/构建基础关卡Windows64Player", false, 92)]
        public static void BuildPlayablePlayer()
        {
            using var graphicsScope = new DirectGraphicsBuildScope();
            V2ProjectUpgrade.UpgradeBasicSandbox();
            string target = "Builds/V2Playable/OpenOita.exe";
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { V2ProjectUpgrade.ScenePath },
                locationPathName = target,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("V2基础关卡Player构建失败：" + report.summary.result);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(target), "StartOpenOita.cmd"),
                "@echo off\r\n\"%~dp0OpenOita.exe\" -force-d3d11 -force-gfx-direct %*\r\n");
            Debug.Log("OpenOita V2 playable Player built: " + target);
        }

        // The small procedural V2 draw stream is faster without a separate graphics
        // command producer on the measured Windows D3D11 target. Burst simulation jobs
        // remain parallel. Scope this setting to these builds and preserve the user's
        // editor/project setting even if a build fails.
        private sealed class DirectGraphicsBuildScope : IDisposable
        {
            private readonly bool _previousMultithreaded = PlayerSettings.MTRendering;
            private readonly bool _previousGraphicsJobs = PlayerSettings.graphicsJobs;

            internal DirectGraphicsBuildScope()
            {
                PlayerSettings.MTRendering = false;
                PlayerSettings.graphicsJobs = false;
            }

            public void Dispose()
            {
                PlayerSettings.MTRendering = _previousMultithreaded;
                PlayerSettings.graphicsJobs = _previousGraphicsJobs;
            }
        }

        /// <summary>
        /// Converts the current BasicSandbox through the V2 converter and stores an
        /// independent Resources asset. The source map is loaded and saved only through
        /// Unity's AssetDatabase; no scene, YAML or source asset is overwritten.
        /// </summary>
        private static OpenOitaMapAsset EnsureBenchmarkCurrentAsset()
        {
            OpenOitaMapAsset source = AssetDatabase.LoadAssetAtPath<OpenOitaMapAsset>(CurrentSourcePath);
            if (source == null) throw new InvalidOperationException("缺少当前BasicSandbox关卡资产：" + CurrentSourcePath);
            WorldResult converted = WorldV2SourceConverter.Convert(source.Sources, out WorldSources v2Sources);
            if (!converted.IsSuccess) throw new InvalidOperationException("BasicSandbox V2转换失败：" + converted.Diagnostic.Message);

            EnsureFolder("Assets/Resources");
            EnsureFolder("Assets/Resources/OpenOita");
            OpenOitaMapAsset target = AssetDatabase.LoadAssetAtPath<OpenOitaMapAsset>(CurrentBenchmarkPath);
            if (target == null)
            {
                target = ScriptableObject.CreateInstance<OpenOitaMapAsset>();
                AssetDatabase.CreateAsset(target, CurrentBenchmarkPath);
            }
            WorldResult replaced = target.ReplaceSources(v2Sources);
            if (!replaced.IsSuccess) throw new InvalidOperationException("BenchmarkCurrent写入失败：" + replaced.Diagnostic.Message);
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(CurrentBenchmarkPath, ImportAssetOptions.ForceUpdate);
            return target;
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
