using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace OpenOita.V2.Validation
{
    /// <summary>Explicit CLI validation of the existing scene/Host, including hidden-window runs.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class HostedRenderValidation : MonoBehaviour
    {
        private const string Flag = "-openoita-v2-validate-render";
        private WorldHost _host;
        private Camera _camera;
        private RenderTexture _target;
        private RenderTexture _previousTarget;
        private UniversalRenderPipeline.SingleCameraRequest _request;
        private bool _captured;
        private bool _failed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Flag) < 0) return;
            new GameObject("OpenOita Hosted Render Validation").AddComponent<HostedRenderValidation>();
        }

        private void LateUpdate()
        {
            if (_failed) return;
            if (_host == null) _host = FindFirstObjectByType<WorldHost>();
            if (_host == null || !(_host.World is MaterialWorld world) || world.Display == null) return;
            try
            {
                if (_target == null)
                {
                    _camera = Camera.main;
                    if (_camera == null) throw new InvalidOperationException("正式场景缺少Main Camera。");
                    _previousTarget = _camera.targetTexture;
                    _target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32)
                    {
                        antiAliasing = 1,
                        filterMode = FilterMode.Point,
                        name = "OpenOita Formal Host Validation Target"
                    };
                    _target.Create();
                    _camera.targetTexture = _target;
                    _request = new UniversalRenderPipeline.SingleCameraRequest { destination = _target };
                }
                // Render the scene's actual camera and registered Host world. No new
                // fixture/world/pipeline is created. Hidden swapchains may skip automatic
                // frames, so explicitly submit this offscreen camera request for validation.
                _camera.SubmitRenderRequest(_request);
                if (!_captured && world.Version.CommittedTick >= 1000)
                {
                    if (world.Display.Renderer.VisibleVersion == 0 || world.Display.Renderer.ActiveTileCount == 0)
                        throw new InvalidOperationException("正式Host尚未上传可见材料。");
                    SaveImage();
                    _captured = true;
                    Debug.Log($"OpenOita V2 formal render validated: tick={world.Version.CommittedTick}, uploaded={world.Display.Renderer.VisibleVersion}, tiles={world.Display.Renderer.ActiveTileCount}, camera={_camera.name}");
                }
            }
            catch (Exception exception)
            {
                _failed = true;
                Debug.LogError("OpenOita V2 formal render validation failed: " + exception);
            }
        }

        private void SaveImage()
        {
            string output = Path.Combine(Application.persistentDataPath, "OpenOita-FormalHost.png");
            const string prefix = "-openoita-v2-validate-render-output=";
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.StartsWith(prefix, StringComparison.Ordinal)) output = argument.Substring(prefix.Length);
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            RenderTexture previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                RenderTexture.active = _target;
                image = new Texture2D(_target.width, _target.height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, _target.width, _target.height), 0, 0);
                image.Apply();
                File.WriteAllBytes(output, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                if (image != null) Destroy(image);
            }
            Debug.Log("OpenOita V2 formal validation PNG: " + output);
        }

        private void OnDestroy()
        {
            if (_camera != null) _camera.targetTexture = _previousTarget;
            if (_target != null) { _target.Release(); Destroy(_target); }
        }
    }
}
