using System;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Render;
using UnityEngine;

namespace OpenOita.Editor
{
    // M08资源协调；唯一推进器仍是M02 WorldHost/FixedStepDriver。
    public sealed class SceneEditorSession : IDisposable
    {
        public const int PreviewLayer = 31;
        private CommittedWorldRenderer _preview;
        private CommittedWorldRenderer _trialRenderer;
        private int _revision = -1;
        private SceneMaterialData _previewData;
        private Vector2 _previewOrigin;
        private GameObject _hostObject;
        private Camera _camera;
        private RenderTexture _output;
        private Vector2 _trialOrigin;
        public WorldHost Host { get; private set; }
        public bool IsTrial => Host != null;
        public RenderTexture Output => _output;

        public WorldResult BeginTrial(SceneMaterialData data, Vector2 origin, bool automatic, bool freezeBodyRotation = true)
        {
            if (IsTrial) return Error(WorldErrorCode.Busy, "请先退出当前试玩。");
            if (!Application.isPlaying) return Error(WorldErrorCode.NotReady, "正式试玩需要进入Unity PlayMode。");
            WorldResult exported = data.Export(out WorldSources copy);
            if (!exported.IsSuccess) return exported;
            ReleasePreview();
            _hostObject = new GameObject("OpenOita编辑器正式试玩") { hideFlags = HideFlags.HideAndDontSave };
            _hostObject.SetActive(false);
            Host = _hostObject.AddComponent<WorldHost>();
            WorldResult result = Host.CreateWorld(new WorldSimulation(rendererFactory: () =>
                _trialRenderer = new CommittedWorldRenderer { DisplayLayer = PreviewLayer }, freezeBodyRotation: freezeBodyRotation), copy, origin, automatic);
            if (!result.IsSuccess) { EndTrial(); return result; }
            _hostObject.SetActive(true); // OnEnable看到已创建的世界，不加载另一套初态。
            _trialOrigin = origin;
            return result;
        }

        public WorldResult EndTrial()
        {
            WorldResult result = Host != null ? Host.CloseWorld() : WorldResult.Success();
            if (!result.IsSuccess) return result;
            if (_hostObject != null) UnityEngine.Object.DestroyImmediate(_hostObject);
            Host = null; _hostObject = null; _trialRenderer = null;
            return result;
        }

        public WorldResult Render(SceneMaterialData data, Vector2 origin, int width, int height, int scale, Vector2Int pan)
        {
            if (data == null || width < 1 || height < 1 || scale < 1 || !ContractDefaults.IsFinite(origin))
                return Error(WorldErrorCode.InvalidArgument, "预览输入无效。");
            EnsureCamera(width, height);
            if (!IsTrial)
            {
                if (_preview == null || _previewData != data || _previewOrigin != origin)
                {
                    ReleasePreview();
                    _preview = new CommittedWorldRenderer { DisplayLayer = PreviewLayer };
                    WorldResult ready = _preview.Prepare(new InitialSceneRenderView(data, origin));
                    if (!ready.IsSuccess) { ReleasePreview(); return ready; }
                    _previewData = data; _previewOrigin = origin; _revision = data.Revision;
                }
                else if (_revision != data.Revision)
                {
                    using IPreparedWorldDisplay prepared = _preview.PrepareCommit(new InitialSceneRenderView(data, origin), default);
                    if (!prepared.Result.IsSuccess) return prepared.Result;
                    prepared.Adopt(); _revision = data.Revision;
                }
                _preview.FlushFrame();
            }
            else
            {
                _trialRenderer.FlushFrame();
                Host.ConfigurePixelView(_camera, scale, pan);
                origin = _trialOrigin;
            }
            PixelWorldViewport.Configure(_camera, IsTrial ? Host.World.Config : data.Config, origin, scale, pan);
            _camera.Render();
            return WorldResult.Success();
        }

        private void EnsureCamera(int width, int height)
        {
            if (_camera == null)
            {
                var go = new GameObject("OpenOita编辑像素摄像机") { hideFlags = HideFlags.HideAndDontSave };
                _camera = go.AddComponent<Camera>(); _camera.enabled = false;
                _camera.cullingMask = 1 << PreviewLayer; _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.08f, 0.08f, 0.08f, 1);
            }
            if (_output != null && (_output.width != width || _output.height != height)) ReleaseOutput();
            if (_output == null)
            {
                _output = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                { name = "OpenOita编辑像素输出", filterMode = FilterMode.Point, antiAliasing = 1, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
                _output.Create(); _camera.targetTexture = _output;
            }
        }

        private void ReleaseOutput()
        {
            if (_camera != null) _camera.targetTexture = null;
            if (_output != null) { _output.Release(); UnityEngine.Object.DestroyImmediate(_output); _output = null; }
        }
        private void ReleasePreview()
        {
            _preview?.Dispose(); _preview = null; _previewData = null; _revision = -1;
        }
        public void Dispose()
        {
            EndTrial(); ReleasePreview(); ReleaseOutput();
            if (_camera != null) UnityEngine.Object.DestroyImmediate(_camera.gameObject);
            _camera = null;
        }
        private static WorldResult Error(WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("编辑器会话", "preview/trial", message));
    }
}
