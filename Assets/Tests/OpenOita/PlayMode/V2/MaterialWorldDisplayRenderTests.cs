using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;
using OpenOita.V2;
using OpenOita.V2.Render;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;

namespace OpenOita.Tests.PlayMode.V2
{
    public sealed class MaterialWorldDisplayRenderTests
    {
        [Test]
        public void DisplayRemovesDeletedDynamicBodyTiles()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: true, wide: false);
            GameObject cameraObject = CreateCamera(1f, 1f, out Camera camera);
            try
            {
                Assert.That(world.Physics.BodyCount, Is.GreaterThan(0));
                world.Display.PrepareForCamera(camera);
                Assert.That(world.Display.Renderer.ActiveTileCount, Is.GreaterThan(0));
                world.Display.Renderer.CompleteFrameAfterRenderGraphRecording();

                EnqueueRemove(world, 4, 4);
                Assert.That(world.Step().Result.IsSuccess, Is.True);
                Assert.That(world.Physics.BodyCount, Is.EqualTo(0));

                world.Display.PrepareForCamera(camera);
                Assert.That(world.Display.Renderer.ActiveTileCount, Is.EqualTo(0));
            }
            finally
            {
                world.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void DisplayResetRebuildsGenerationWithoutRetainingOldTiles()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: false, wide: false);
            GameObject cameraObject = CreateCamera(1f, 1f, out Camera camera);
            try
            {
                world.Display.PrepareForCamera(camera);
                IncrementalWorldRenderer oldRenderer = world.Display.Renderer;
                Assert.That(oldRenderer.ActiveTileCount, Is.GreaterThan(0));
                oldRenderer.CompleteFrameAfterRenderGraphRecording();
                ulong generation = world.Version.Generation;

                Assert.That(world.Reset().IsSuccess, Is.True);
                Assert.That(world.Version.Generation, Is.EqualTo(generation + 1));
                Assert.That(world.Display.Renderer, Is.Not.SameAs(oldRenderer));
                Assert.That(world.Display.Renderer.ActiveTileCount, Is.EqualTo(0));

                world.Display.PrepareForCamera(camera);
                Assert.That(world.Display.Renderer.ActiveTileCount, Is.GreaterThan(0));
            }
            finally
            {
                world.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void DisplaySupportsTwoCameraRangesAndReusesCleanCache()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: false, wide: true);
            GameObject firstObject = CreateCamera(1f, 1f, out Camera firstCamera);
            GameObject secondObject = CreateCamera(103f, 1f, out Camera secondCamera);
            try
            {
                world.Display.PrepareForCamera(firstCamera);
                world.Display.PrepareForCamera(secondCamera);
                Assert.That(world.Display.Renderer.ActiveTileCount, Is.GreaterThanOrEqualTo(2));
                world.Display.Renderer.CompleteFrameAfterRenderGraphRecording();

                world.Display.PrepareForCamera(firstCamera);
                Assert.That(world.Display.Renderer.PendingPixelUpdateCount, Is.Zero);
                Assert.That(world.Display.Renderer.PendingBodyPoseUpdateCount, Is.Zero);
            }
            finally
            {
                world.Dispose();
                UnityEngine.Object.DestroyImmediate(firstObject);
                UnityEngine.Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void DisplaySecondUnchangedFrameHasNoPixelOrPoseUpload()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: false, wide: false);
            GameObject cameraObject = CreateCamera(1f, 1f, out Camera camera);
            try
            {
                world.Display.PrepareForCamera(camera);
                Assert.That(world.Display.Renderer.PendingPixelUpdateCount, Is.GreaterThan(0));
                Assert.That(world.Display.Renderer.PendingBodyPoseUpdateCount, Is.GreaterThan(0));
                world.Display.Renderer.CompleteFrameAfterRenderGraphRecording();

                world.Display.PrepareForCamera(camera);
                Assert.That(world.Display.Renderer.PendingPixelUpdateCount, Is.Zero);
                Assert.That(world.Display.Renderer.PendingBodyPoseUpdateCount, Is.Zero);
            }
            finally
            {
                world.Dispose();
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [UnityTest]
        public IEnumerator UniversalRenderGraphDrawsMaterialBlankResetAndNoTickFrame()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: false, wide: false);
            GameObject cameraObject = CreateCamera(1f, 1f, out Camera camera);
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            target.Create();
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            UniversalAdditionalCameraData cameraData = cameraObject.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null) cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            camera.enabled = true;
            try
            {
                // The camera is enabled and the test waits for the normal URP frame. It does not
                // invoke a manual render, so this exercises RendererFeature.RecordRenderGraph.
                yield return WaitForCameraFrame(camera);
                Color32 material = ReadTargetPixel(target, 55, 55);
                Assert.That(material.r + material.g + material.b, Is.GreaterThan(30),
                    "URP Renderer2D did not draw the committed material pixel.");

                ulong unchangedTick = world.Version.CommittedTick;
                yield return WaitForCameraFrame(camera);
                Color32 noTick = ReadTargetPixel(target, 55, 55);
                Assert.That(world.Version.CommittedTick, Is.EqualTo(unchangedTick));
                AssertColorEqual(material, noTick, "无新Tick的相机帧不应清空已上传材料。");

                EnqueueRemove(world, 4, 4);
                Assert.That(world.Step().Result.IsSuccess, Is.True);
                yield return WaitForCameraFrame(camera);
                Color32 blank = ReadTargetPixel(target, 55, 55);
                Assert.That(blank.r + blank.g + blank.b, Is.LessThan(10),
                    "删除后的材料像素仍被URP显示路径绘制。");

                Assert.That(world.Reset().IsSuccess, Is.True);
                yield return WaitForCameraFrame(camera);
                Color32 restored = ReadTargetPixel(target, 55, 55);
                Assert.That(restored.r + restored.g + restored.b, Is.GreaterThan(30),
                    "Reset后的新代次没有重新绘制材料像素。");
            }
            finally
            {
                world.Dispose();
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [UnityTest]
        public IEnumerator SingleCameraRequestUsesCameraViewProjectionForNonOriginWorld()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = null;
            GameObject nearCameraObject = CreateCamera(1f, 1f, out Camera nearCamera);
            GameObject farCameraObject = CreateCamera(103f, 1f, out Camera farCamera);
            nearCameraObject.AddComponent<UniversalAdditionalCameraData>().renderType = CameraRenderType.Base;
            farCameraObject.AddComponent<UniversalAdditionalCameraData>().renderType = CameraRenderType.Base;
            nearCamera.clearFlags = CameraClearFlags.SolidColor;
            farCamera.clearFlags = CameraClearFlags.SolidColor;
            nearCamera.backgroundColor = Color.black;
            farCamera.backgroundColor = Color.black;
            var nearTarget = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            var farTarget = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            nearTarget.Create();
            farTarget.Create();
            nearCamera.orthographicSize = 10f;
            farCamera.orthographicSize = 10f;
            nearCamera.enabled = false;
            farCamera.enabled = false;
            try
            {
                // Probe the request before allocating MaterialWorld/Native grids. Camera
                // requests can prepare URP outside the normal GameView loop; a headless
                // target can therefore be ignored without leaking persistent world state.
                var probe = new RenderRequestObservation();
                yield return SubmitSingleCameraRequest(nearCamera, nearTarget, probe, primePipeline: true);
                if (!probe.Completed)
                {
                    string reason = probe.Error ?? "SingleCameraRequest未产生endCameraRendering回调。";
                    DestroyRenderTestObject(ref nearTarget, ref nearCameraObject);
                    DestroyRenderTestObject(ref farTarget, ref farCameraObject);
                    if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    {
                        Assert.Ignore("Batch/Null图形设备无法执行真实SingleCameraRequest：" + reason);
                        yield break;
                    }
                    Assert.Fail(reason);
                    yield break;
                }

                world = CreateWorld(resources, dynamicBody: false, wide: true);
                // The world fixture contains one cell near x=0.4 and one near x=102.8.
                // Rendering two separate non-origin views makes an identity VP bug fail
                // deterministically for the far camera: x=102.85 is outside clip space.
                var nearResult = new RenderRequestObservation();
                yield return SubmitSingleCameraRequest(nearCamera, nearTarget, nearResult);
                Assert.That(nearResult.Completed, Is.True, nearResult.Error);
                AssertMaterialNearWorldPoint(nearTarget, nearCamera, new Vector3(0.45f, 0.45f, 0),
                    "SingleCameraRequest的近端材料没有落在Camera.WorldToViewportPoint预测位置。");

                var farResult = new RenderRequestObservation();
                yield return SubmitSingleCameraRequest(farCamera, farTarget, farResult);
                Assert.That(farResult.Completed, Is.True, farResult.Error);
                AssertMaterialNearWorldPoint(farTarget, farCamera, new Vector3(102.85f, 0.45f, 0),
                    "SingleCameraRequest的非原点材料没有落在Camera.WorldToViewportPoint预测位置。");
            }
            finally
            {
                world?.Dispose();
                DestroyRenderTestObject(ref nearTarget, ref nearCameraObject);
                DestroyRenderTestObject(ref farTarget, ref farCameraObject);
            }
        }

        [UnityTest]
        public IEnumerator UniversalRenderGraphDrawsFireWithPerTileQuad()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: false, wide: false, burning: true);
            GameObject cameraObject = CreateCamera(1f, 1f, out Camera camera);
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            target.Create();
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            UniversalAdditionalCameraData cameraData = cameraObject.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null) cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            camera.enabled = true;
            try
            {
                yield return WaitForCameraFrame(camera);
                Assert.That(world.Display.Renderer.EstimatedFireVertexCount,
                    Is.EqualTo(IncrementalWorldRenderer.FireVerticesPerTile * IncrementalWorldRenderer.LayersPerPage));
                Color32[] pixels = ReadTargetPixels(target);
                bool sawFire = false;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 pixel = pixels[i];
                    if (pixel.r > 190 && pixel.r > pixel.g + 35 && pixel.g < 210)
                    {
                        sawFire = true;
                        break;
                    }
                }
                Assert.That(sawFire, Is.True,
                    "URP Renderer2D未输出初燃像素；火焰应由每Tile四边形的shader采样生成。");
            }
            finally
            {
                world.Dispose();
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [UnityTest]
        public IEnumerator UniversalRenderGraphDrawsFireAbove128CellBoundary()
        {
            V2RenderingResources resources = RequireResources();
            MaterialWorld world = CreateWorld(resources, dynamicBody: false, wide: false,
                burning: true, burningBoundary: true);
            GameObject cameraObject = CreateCamera(1f, 13.3f, out Camera camera);
            camera.orthographicSize = 2f;
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            target.Create();
            camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            UniversalAdditionalCameraData cameraData = cameraObject.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null) cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            camera.enabled = true;
            try
            {
                yield return WaitForCameraFrame(camera);
                Color32[] pixels = ReadTargetPixels(target);
                bool sawAboveBoundaryFire = false;
                for (int y = 50; y < 76 && !sawAboveBoundaryFire; y++)
                    for (int x = 35; x < 58; x++)
                    {
                        Color32 pixel = pixels[y * target.width + x];
                        if (pixel.r > 190 && pixel.r > pixel.g + 35 && pixel.g < 210)
                        {
                            sawAboveBoundaryFire = true;
                            break;
                        }
                    }
                Assert.That(sawAboveBoundaryFire, Is.True,
                    "y=127顶部燃烧格上方未显示shader生成的两格火焰延伸。");
            }
            finally
            {
                world.Dispose();
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static V2RenderingResources RequireResources()
        {
            V2RenderingResources resources = Resources.Load<V2RenderingResources>("OpenOita/V2Rendering");
            if (resources == null) Assert.Fail("缺少 Resources/OpenOita/V2Rendering.asset。");
            if (!resources.IsComplete) Assert.Fail("V2Rendering.asset 未绑定完整的 Compute/WorldPage/WorldFire 资源。");
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("当前图形设备不支持 ComputeShader。");
            return resources;
        }

        private static MaterialWorld CreateWorld(V2RenderingResources resources, bool dynamicBody, bool wide,
            bool burning = false, bool burningBoundary = false)
        {
            WorldSources baseline = BaselineSources.Read();
            JObject materials = JObject.Parse(baseline.MaterialsText);
            JObject config = JObject.Parse(baseline.WorldConfigText);
            JObject scene = JObject.Parse(baseline.SceneText);
            materials["schemaVersion"] = 2;
            config["schemaVersion"] = 2;
            config.Remove("chunkSize");
            scene["schemaVersion"] = 2;
            if (wide)
            {
                config["width"] = 2048;
                config["height"] = 256;
            }
            ((JObject)config["limits"])["maxMaterialCells"] = 65536;

            var cells = new JArray();
            var fixedCells = new JArray();
            int fireY = burningBoundary ? 127 : 4;
            AddCell(cells, 4, fireY, burning ? (ushort)104 : (ushort)102);
            if (!dynamicBody)
            {
                AddCell(fixedCells, 4, fireY);
                if (wide)
                {
                    AddCell(cells, 1028, 4, 102);
                    AddCell(fixedCells, 1028, 4);
                }
            }
            scene["cells"] = cells;
            scene["fixedCells"] = fixedCells;
            var initialBurning = new JArray();
            if (burning) initialBurning.Add(new JObject { ["x"] = 4, ["y"] = fireY });
            scene["initialBurning"] = initialBurning;

            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(new WorldSources(
                materials.ToString(), config.ToString(), scene.ToString()));
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            var world = new MaterialWorld(loaded, Vector2.zero, true);
            Assert.That(world.Initialize().IsSuccess, Is.True);
            try
            {
                world.AttachDisplay(resources, 0);
                return world;
            }
            catch
            {
                world.Dispose();
                throw;
            }
        }

        private static void AddCell(JArray cells, int x, int y, ushort material)
        {
            cells.Add(new JObject { ["x"] = x, ["y"] = y, ["materialId"] = material });
        }

        private static void AddCell(JArray cells, int x, int y)
        {
            cells.Add(new JObject { ["x"] = x, ["y"] = y });
        }

        private static void EnqueueRemove(MaterialWorld world, int x, int y)
        {
            var region = new WorldRect(new Vector2(x * 0.1f, y * 0.1f),
                new Vector2((x + 1) * 0.1f, (y + 1) * 0.1f));
            EnqueueResult result = world.Enqueue(new MaterialCommand(
                MaterialOperation.Remove, region, 0, world.Version.Generation));
            Assert.That(result.Result.IsSuccess || result.Result.Status == ResultStatus.Pending, Is.True,
                result.Result.Diagnostic.Message);
        }

        private static Color32 ReadTargetPixel(RenderTexture target, int x, int y)
        {
            return ReadTargetPixels(target)[y * target.width + x];
        }

        private static Color32[] ReadTargetPixels(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            Color32[] pixels = image.GetPixels32();
            UnityEngine.Object.DestroyImmediate(image);
            RenderTexture.active = previous;
            return pixels;
        }

        private static IEnumerator WaitForCameraFrame(Camera camera)
        {
            int completed = 0;
            void OnEndCameraRendering(ScriptableRenderContext context, Camera renderedCamera)
            {
                if (renderedCamera == camera) completed++;
            }

            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            try
            {
                for (int frame = 0; frame < 16 && completed == 0; frame++) yield return null;
                if (completed != 0) yield break;
                if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    Assert.Ignore("Batch/Null图形设备未产生真实GPU相机帧；像素读回未验证。");
                Assert.Fail("目标相机在16帧内未收到RenderPipelineManager.endCameraRendering回调。");
            }
            finally
            {
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            }
        }

        private sealed class RenderRequestObservation
        {
            public bool Completed;
            public string Error;
        }

        private static IEnumerator SubmitSingleCameraRequest(Camera camera, RenderTexture target,
            RenderRequestObservation observation, bool primePipeline = false)
        {
            var request = new UniversalRenderPipeline.SingleCameraRequest
            {
                destination = target,
                mipLevel = 0,
                face = CubemapFace.Unknown,
                slice = 0
            };
            observation.Completed = false;
            observation.Error = null;
            int completed = 0;
            void OnEndCameraRendering(ScriptableRenderContext context, Camera renderedCamera)
            {
                if (renderedCamera == camera) completed++;
            }

            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            try
            {
                // Camera.SubmitRenderRequest is the public outside-render-loop entry
                // point and lets Unity prepare the active SRP before SupportsRenderRequest
                // would otherwise observe a null pipeline.
                try
                {
                    if (primePipeline) camera.SubmitRenderRequest(request);
                    else RenderPipeline.SubmitRenderRequest(camera, request);
                }
                catch (Exception exception)
                {
                    observation.Error = exception.GetType().Name + ": " + exception.Message;
                }
                if (observation.Error != null) yield break;
                for (int frame = 0; frame < 16 && completed == 0; frame++) yield return null;
                observation.Completed = completed != 0;
                if (!observation.Completed)
                    observation.Error = "SingleCameraRequest在16帧内未收到真实endCameraRendering回调。";
            }
            finally
            {
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            }
        }

        private static void DestroyRenderTestObject(ref RenderTexture target, ref GameObject cameraObject)
        {
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                target = null;
            }
            if (cameraObject != null)
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                cameraObject = null;
            }
        }

        private static void AssertMaterialNearWorldPoint(RenderTexture target, Camera camera,
            Vector3 worldPoint, string message)
        {
            Vector3 viewport = camera.WorldToViewportPoint(worldPoint);
            Assert.That(viewport.z, Is.GreaterThan(0), message + "（点位于相机后方）。");
            int centerX = Mathf.RoundToInt(viewport.x * (target.width - 1));
            int centerY = Mathf.RoundToInt(viewport.y * (target.height - 1));
            Color32[] pixels = ReadTargetPixels(target);
            bool found = false;
            for (int y = Mathf.Max(0, centerY - 3); y <= Mathf.Min(target.height - 1, centerY + 3) && !found; y++)
                for (int x = Mathf.Max(0, centerX - 3); x <= Mathf.Min(target.width - 1, centerX + 3); x++)
                {
                    Color32 pixel = pixels[y * target.width + x];
                    if (pixel.r + pixel.g + pixel.b > 30)
                    {
                        found = true;
                        break;
                    }
                }
            Assert.That(found, Is.True, message + " viewport=(" + viewport.x + "," + viewport.y + ").");
        }

        private static void AssertColorEqual(Color32 expected, Color32 actual, string message)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(1), message);
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(1), message);
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(1), message);
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(1), message);
        }

        private static GameObject CreateCamera(float x, float y, out Camera camera)
        {
            var gameObject = new GameObject("OpenOita V2 display test camera");
            camera = gameObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 4f;
            camera.aspect = 1f;
            camera.cullingMask = ~0;
            gameObject.transform.position = new Vector3(x, y, -10f);
            return gameObject;
        }
    }
}
