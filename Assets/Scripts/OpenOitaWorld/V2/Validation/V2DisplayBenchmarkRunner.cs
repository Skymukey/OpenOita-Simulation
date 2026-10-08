using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.V2;
using OpenOita.V2.Render;
using OpenOita.V2.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OpenOita.V2.Validation
{
    /// <summary>
    /// Measures the real V2 display path against the selected V2BenchmarkScenario. The
    /// current40k scenario reads Resources/OpenOita/BenchmarkCurrent.asset; synthetic million
    /// cell scenarios build from StreamingAssets/OpenOitaV2 through the shared fixture.
    /// One committed world Tick is stepped per benchmark camera frame; the camera then records
    /// the normal URP RendererFeature RenderGraph into a 1920x1080 target texture. GPU timings
    /// are collected as a timestamp-deduplicated continuous window and written to a metadata
    /// sidecar; per-Tick GPU CSV cells remain NA because neither FrameTimingManager
    /// nor an asynchronous native scope should be assigned to a delayed world Tick.
    /// </summary>
    public sealed class V2DisplayBenchmarkRunner : MonoBehaviour
    {
        private const string Flag = "-openoita-v2-display-benchmark";
        private const int TargetWidth = 1920;
        private const int TargetHeight = 1080;
        private const int FrameTimingQueryCapacity = 64;
        private const int GpuDrainFrameLimit = 32;
        private const int NativeGpuReadCapacity = 128;

        // FrameTimingManager returns delayed completed frames. Keep the complete returned
        // window and deduplicate by its own CPU-clock identity; never assign a delayed GPU
        // record to the current world Tick.
        private readonly FrameTiming[] _frameTimings = new FrameTiming[FrameTimingQueryCapacity];
        private readonly List<Camera> _disabledCameras = new List<Camera>(8);
        private readonly List<bool> _cameraStates = new List<bool>(8);
        private readonly HashSet<ulong> _gpuTimingKeys = new HashSet<ulong>();
        private readonly HashSet<ulong> _gpuPositiveTimingKeys = new HashSet<ulong>();
        private readonly List<double> _gpuFrameTimes = new List<double>(10000);
        private readonly List<double> _nativeGpuTimes = new List<double>(10000);
        private readonly NativeGpuCameraTimer.NativeGpuScopeSample[] _nativeGpuReadBuffer =
            new NativeGpuCameraTimer.NativeGpuScopeSample[NativeGpuReadCapacity];
        private bool _started;
        private bool _cameraFrameCompleted;
        private bool _cameraFrameObserved;
        private Camera _camera;
        private GameObject _cameraObject;
        private RenderTexture _target;
        private MaterialWorld _world;
        private V2BenchmarkScenario _scenario;
        private int _initialMaterialCells;
        private int _initialFlowCells;
        private int _initialBodyCount;
        private StreamWriter _writer;
        private int _renderFramesObserved;
        private int _drainFramesObserved;
        private int _stepIterationsCompleted;
        private int _gpuCollectionFrames;
        private int _sampleRowsWritten;
        private int _gpuCaptureCalls;
        private int _gpuTimingRecords;
        private int _gpuTimingZeroCount;
        private int _gpuTimingUnkeyedCount;
        private string _gpuTimingError;
        private ulong _cpuTimerFrequency;
        private ulong _gpuTimerFrequency;
        private bool _gpuSampleWindowStarted;
        private ulong _gpuSampleCutoffTimestamp;
        private ulong _maxGpuTimingKey;
        private int _warmupTimingDrainFrames;
        private int _finalTimingDrainFrames;
        private NativeGpuCameraTimer _nativeGpuTimer;
        private StreamWriter _nativeGpuWriter;
        private string _nativeGpuRawPath;
        private int _nativeGpuSamples;
        private int _nativeGpuRowsWritten;
        private int _nativeGpuInvalidSamples;
        private int _nativeGpuDropCount;
        private string _nativeGpuSource = "NA";
        private string _renderMode = "automatic_camera";
        private bool _requestModeAttempted;
        private bool _requestModeSupported;
        private UniversalRenderPipeline.SingleCameraRequest _singleCameraRequest;
        private double _lastRenderRequestSubmitMs = double.NaN;
        private double _renderRequestSubmitTotalMs;
        private int _renderRequestFrames;
        private string _renderRequestError;
        private string _validationPngPath;
        private string _validationPngError;
        private bool _validationPngSaved;
        private bool _runtimeSettingsCaptured;
        private bool _previousRunInBackground;
        private int _previousVSyncCount;
        private int _previousTargetFrameRate;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasFlag()) return;
            if (FindFirstObjectByType<V2DisplayBenchmarkRunner>() != null) return;
            var host = new GameObject("OpenOitaV2DisplayBenchmark");
            host.AddComponent<V2DisplayBenchmarkRunner>();
            DontDestroyOnLoad(host);
        }

        private void Start()
        {
            if (_started || !HasFlag()) return;
            _started = true;
            StartCoroutine(RunBenchmark());
        }

        private IEnumerator RunBenchmark()
        {
            ConfigureBenchmarkRuntime();
            if (Debug.isDebugBuild || HasArgument("-openoita-v2-allocation-validation"))
            {
                ScopedAllocationProbe.Initialize(new AllocationProbeConfiguration(4096, 4096, true));
            }
            string scenarioName = Argument("-openoita-v2-display-benchmark-scenario=", "current40k");
            int warmup = PositiveArgument("-openoita-v2-display-benchmark-warmup=", 1000);
            int samples = PositiveArgument("-openoita-v2-display-benchmark-samples=", 10000);
            string outputDirectory = Argument("-openoita-v2-display-benchmark-output=",
                Path.Combine(Application.persistentDataPath, "OpenOitaV2Benchmarks"));
            outputDirectory = Path.GetFullPath(outputDirectory);

            V2RenderingResources resources = Resources.Load<V2RenderingResources>("OpenOita/V2Rendering");
            if (resources == null || !resources.IsComplete)
            {
                Debug.LogError("OpenOita V2 display benchmark: 缺少完整的 Resources/OpenOita/V2Rendering.asset。");
                RestoreBenchmarkRuntime();
                FinishAndMaybeQuit();
                yield break;
            }

            if (!TryCreateWorld(resources, scenarioName, out string setupError))
            {
                Debug.LogError("OpenOita V2 display benchmark: " + setupError);
                Cleanup();
                FinishAndMaybeQuit();
                yield break;
            }

            if (!TryCreateOutput(outputDirectory, _scenario.Name, out string outputPath, out string outputError))
            {
                Debug.LogError("OpenOita V2 display benchmark: " + outputError);
                Cleanup();
                FinishAndMaybeQuit();
                yield break;
            }

            try
            {
                DisableSceneCameras();
                CreateBenchmarkCamera();
                InitializeNativeGpuTimer(outputPath);
                RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                PrimeFrameTimingWindow();
                _writer.WriteLine("scenario,phase,tick,generation,step_ok,render_frame,step_ms,display_prepare_ms,display_prepare_allocated_bytes,draw_submit_ms,gpu_frame_ms,estimated_fire_vertices,active_tiles,pending_pixel_updates,pending_pose_updates,render_mode,render_request_submit_ms");

                bool renderAvailable = true;
                int total = warmup + samples;
                for (int index = 0; index < total; index++)
                {
                    bool sample = index >= warmup;
                    if (index == warmup && !_gpuSampleWindowStarted)
                    {
                        if (renderAvailable) yield return DrainWarmupTimingWindow();
                        if (_renderMode == "unavailable") renderAvailable = false;
                        BeginGpuSampleWindow(samples);
                    }

                    long stepStart = Stopwatch.GetTimestamp();
                    if (_scenario.RotatingBodies && _world.Physics != null)
                        for (int bodyIndex = 0; bodyIndex < _world.Physics.BodyCount; bodyIndex++)
                        {
                            BodyV2 body = _world.Physics.GetBody(bodyIndex);
                            _world.Physics.ConfigureBodyMotion(body.Id,
                                new BodyMotion(body.Motion.LinearVelocity, 0.5f), false);
                        }
                    StepResult step = _world.Step();
                    double stepMs = Milliseconds(stepStart);
                    if (!step.Result.IsSuccess)
                    {
                        Debug.LogError("OpenOita V2 display benchmark: Tick failed at " +
                            _world.Version.CommittedTick + ": " + step.Result.Diagnostic.Message);
                        break;
                    }
                    _stepIterationsCompleted++;

                    if (renderAvailable)
                    {
                        yield return RenderBenchmarkCameraFrame(true);
                        if (!_cameraFrameObserved)
                        {
                            renderAvailable = false;
                            Debug.LogWarning("OpenOita V2 display benchmark: 未收到目标相机的URP帧；显示CPU/GPU列将为NA，继续记录Tick。");
                            if (sample) WriteSample(stepMs, false, double.NaN, double.NaN);
                        }
                        else
                        {
                            _renderFramesObserved++;
                            _gpuCollectionFrames++;
                            CollectFrameTimings();
                            CollectNativeGpuSamples("sample");
                            if (sample)
                                WriteSample(stepMs, true, double.NaN, _lastRenderRequestSubmitMs);
                        }
                    }
                    else
                    {
                        // Keep the one-Tick-per-frame policy even on a headless/no-render target.
                        yield return null;
                        if (sample) WriteSample(stepMs, false, double.NaN, double.NaN);
                    }

                    if (sample && (index & 63) == 63) _writer.Flush();
                }

                if (renderAvailable)
                    yield return DrainFrameTimingWindow();

                CollectNativeGpuSamples("drain");

                _writer.Flush();
                _nativeGpuWriter?.Flush();
                SaveValidationPng(outputPath);
                WriteMetadata(outputPath, warmup, samples, renderAvailable);
                Debug.Log("OpenOita V2 display benchmark complete: " + outputPath +
                    " (resolution=1920x1080, policy=one committed Tick per camera frame, warmup=" +
                    warmup + ", scenario=" + _scenario.Name + ", samples=" + samples + ", gpuSamples=" + _gpuFrameTimes.Count +
                    ", nativeGpuSamples=" + _nativeGpuSamples + ", gpuSource=" + _nativeGpuSource +
                    ", renderMode=" + _renderMode + ", png=" + (_validationPngSaved ? _validationPngPath : "NA") +
                    ", metadata=" + outputPath + ".metadata.json)");
            }
            finally
            {
                Cleanup();
            }

            FinishAndMaybeQuit();
        }

        private bool TryCreateWorld(V2RenderingResources resources, string scenarioName, out string error)
        {
            error = null;
            try
            {
                if (!V2BenchmarkScenarioCatalog.TryCreate(scenarioName, out _scenario))
                {
                    error = "未知显示benchmark场景：" + scenarioName;
                    return false;
                }

                WorldSources sources;
                if (_scenario.UseBenchmarkCurrentAsset)
                {
                    OpenOitaMapAsset asset = Resources.Load<OpenOitaMapAsset>("OpenOita/BenchmarkCurrent");
                    if (asset == null)
                    {
                        error = "缺少 Resources/OpenOita/BenchmarkCurrent.asset。";
                        return false;
                    }
                    sources = asset.Sources;
                }
                else
                {
                    WorldResult read = new ConfigurationFileStore().ReadSources(
                        Path.Combine(Application.streamingAssetsPath, "OpenOitaV2"), out WorldSources baseline);
                    if (!read.IsSuccess)
                    {
                        error = "无法读取StreamingAssets/OpenOitaV2基线：" + read.Diagnostic.Message;
                        return false;
                    }
                    sources = _scenario.Build(baseline);
                }

                _scenario.PrepareWakeCells(sources);
                WorldLoadResult loaded = new WorldSourceLoaderV2().Load(sources);
                if (!loaded.Result.IsSuccess)
                {
                    error = "显示benchmark场景加载失败：" + loaded.Result.Diagnostic.Message;
                    return false;
                }
                if (loaded.Config.Width != _scenario.Width || loaded.Config.Height != _scenario.Height ||
                    loaded.Scene.Cells.Count != _scenario.CellCount)
                {
                    error = "显示benchmark场景初态不符：期望 " + _scenario.Width + "x" + _scenario.Height +
                        ", cells=" + _scenario.CellCount + "，实际 " + loaded.Config.Width + "x" +
                        loaded.Config.Height + ", cells=" + loaded.Scene.Cells.Count;
                    return false;
                }

                _world = new MaterialWorld(loaded, Vector2.zero, _scenario.WithPhysics);
                WorldResult initialized = _world.Initialize();
                if (!initialized.IsSuccess)
                {
                    error = "显示benchmark场景初始化失败：" + initialized.Diagnostic.Message;
                    _world.Dispose();
                    _world = null;
                    return false;
                }

                if (_scenario.HasBodyFixture && _world.Physics != null)
                {
                    if (_world.Physics.BodyCount != 64)
                    {
                        error = "旋转体显示benchmark要求64个动态体，实际 " + _world.Physics.BodyCount;
                        _world.Dispose();
                        _world = null;
                        return false;
                    }
                    for (int i = 0; i < _world.Physics.BodyCount; i++)
                    {
                        BodyV2 body = _world.Physics.GetBody(i);
                        _world.Physics.ConfigureBodyMotion(body.Id, new BodyMotion(Vector2.zero, 0.5f), false);
                    }
                }

                _initialMaterialCells = loaded.Scene.Cells.Count;
                _initialFlowCells = CountInitialFlowCells(loaded.Scene);
                _initialBodyCount = _world.Physics?.BodyCount ?? 0;
                _world.AttachDisplay(resources, 0);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                _world?.Dispose();
                _world = null;
                return false;
            }
        }

        private static int CountInitialFlowCells(SceneInitialData scene)
        {
            int count = 0;
            foreach (InitialCell cell in scene.Cells)
                if (cell.MaterialId == 101 || cell.MaterialId == 103) count++;
            return count;
        }

        private bool TryCreateOutput(string outputDirectory, string scenarioName, out string outputPath, out string error)
        {
            outputPath = null;
            error = null;
            try
            {
                Directory.CreateDirectory(outputDirectory);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                outputPath = Path.Combine(outputDirectory, "display-" + scenarioName + "-" + stamp + ".csv");
                _writer = new StreamWriter(outputPath, false, new UTF8Encoding(false), 65536);
                return true;
            }
            catch (Exception exception)
            {
                _writer?.Dispose();
                _writer = null;
                error = "无法创建显示benchmark CSV：" + exception.Message;
                return false;
            }
        }

        private void DisableSceneCameras()
        {
            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null) continue;
                _disabledCameras.Add(camera);
                _cameraStates.Add(camera.enabled);
                camera.enabled = false;
            }
        }

        private void CreateBenchmarkCamera()
        {
            _cameraObject = new GameObject("OpenOitaV2DisplayBenchmarkCamera");
            _cameraObject.transform.SetParent(transform, false);
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.aspect = TargetWidth / (float)TargetHeight;
            _camera.orthographicSize = _world.Config.Height * _world.Config.CellSize * 0.5f;
            _camera.transform.position = new Vector3(
                _world.Config.Width * _world.Config.CellSize * 0.5f,
                _world.Config.Height * _world.Config.CellSize * 0.5f, -10f);
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.cullingMask = ~0;
            _target = new RenderTexture(TargetWidth, TargetHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                filterMode = FilterMode.Point,
                name = "OpenOita V2 Display Benchmark Target"
            };
            _target.Create();
            _camera.targetTexture = _target;
            UniversalAdditionalCameraData cameraData = _cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            _camera.enabled = true;
        }

        private void ConfigureBenchmarkRuntime()
        {
            if (_runtimeSettingsCaptured) return;
            _previousRunInBackground = Application.runInBackground;
            _previousVSyncCount = QualitySettings.vSyncCount;
            _previousTargetFrameRate = Application.targetFrameRate;
            _runtimeSettingsCaptured = true;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
        }

        private void RestoreBenchmarkRuntime()
        {
            if (!_runtimeSettingsCaptured) return;
            Application.runInBackground = _previousRunInBackground;
            QualitySettings.vSyncCount = _previousVSyncCount;
            Application.targetFrameRate = _previousTargetFrameRate;
            _runtimeSettingsCaptured = false;
        }

        private IEnumerator RenderBenchmarkCameraFrame(bool allowFallback)
        {
            _lastRenderRequestSubmitMs = double.NaN;
            if (_renderMode == "single_camera_request")
            {
                yield return SubmitSingleCameraRequest();
                yield break;
            }
            if (_renderMode == "unavailable") yield break;

            yield return WaitForCameraFrame();
            if (_cameraFrameObserved || !allowFallback) yield break;
            if (!TryActivateSingleCameraRequest()) yield break;
            yield return SubmitSingleCameraRequest();
        }

        private bool TryActivateSingleCameraRequest()
        {
            if (_requestModeAttempted) return _requestModeSupported;
            _requestModeAttempted = true;
            if (_camera == null || _target == null)
            {
                _renderRequestError = "目标相机或RenderTexture不存在。";
                _renderMode = "unavailable";
                return false;
            }

            _camera.enabled = false;
            _singleCameraRequest = new UniversalRenderPipeline.SingleCameraRequest
            {
                destination = _target,
                mipLevel = 0,
                face = CubemapFace.Unknown,
                slice = 0
            };
            try
            {
                _requestModeSupported = RenderPipeline.SupportsRenderRequest(_camera, _singleCameraRequest);
                if (!_requestModeSupported)
                {
                    _renderRequestError = "当前RenderPipeline不支持UniversalRenderPipeline.SingleCameraRequest。";
                    _renderMode = "unavailable";
                    return false;
                }
                _renderMode = "single_camera_request";
                return true;
            }
            catch (Exception exception)
            {
                _renderRequestError = exception.GetType().Name + ": " + exception.Message;
                _renderMode = "unavailable";
                return false;
            }
        }

        private IEnumerator SubmitSingleCameraRequest()
        {
            _cameraFrameCompleted = false;
            _cameraFrameObserved = false;
            long start = Stopwatch.GetTimestamp();
            bool submitted = false;
            try
            {
                RenderPipeline.SubmitRenderRequest(_camera, _singleCameraRequest);
                submitted = true;
                _lastRenderRequestSubmitMs = Milliseconds(start);
                _renderRequestSubmitTotalMs += _lastRenderRequestSubmitMs;
                _renderRequestFrames++;
            }
            catch (Exception exception)
            {
                _lastRenderRequestSubmitMs = double.NaN;
                _renderRequestError = exception.GetType().Name + ": " + exception.Message;
            }

            if (submitted)
                NativeGpuCameraTimer.IssueEndAfterCamera();
            else
                NativeGpuCameraTimer.CancelActiveScope();

            for (int frame = 0; frame < 16 && !_cameraFrameCompleted; frame++) yield return null;
            _cameraFrameObserved = _cameraFrameCompleted;
            if (!_cameraFrameObserved && string.IsNullOrEmpty(_renderRequestError))
                _renderRequestError = "SingleCameraRequest在16帧内未收到endCameraRendering。";
            // One render request per engine frame lets asynchronous GPU queries progress.
            // This wait is outside the recorded CPU preparation/submission interval.
            if (_cameraFrameObserved) yield return null;
        }

        private IEnumerator WaitForCameraFrame()
        {
            _cameraFrameCompleted = false;
            _cameraFrameObserved = false;
            for (int frame = 0; frame < 16 && !_cameraFrameCompleted; frame++) yield return null;
            _cameraFrameObserved = _cameraFrameCompleted;
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != _camera) return;
            _cameraFrameCompleted = true;
            // Automatic camera mode has no SubmitRenderRequest return point. The
            // callback itself is the end-of-camera barrier in that fallback mode.
            if (_renderMode != "single_camera_request")
                NativeGpuCameraTimer.IssueEndAfterCamera();
        }

        private IEnumerator DrainFrameTimingWindow()
        {
            int framesWithoutNewTiming = 0;
            for (int i = 0; i < GpuDrainFrameLimit; i++)
            {
                yield return RenderBenchmarkCameraFrame(false);
                if (!_cameraFrameObserved) yield break;
                _drainFramesObserved++;
                _finalTimingDrainFrames++;
                _gpuCollectionFrames++;
                int before = _gpuTimingKeys.Count;
                CollectFrameTimings();
                CollectNativeGpuSamples("drain");
                framesWithoutNewTiming = before == _gpuTimingKeys.Count
                    ? framesWithoutNewTiming + 1
                    : 0;
                // Stop after the returned timing window has stopped advancing. This is a
                // convergence guard, not an assumed four-frame delay.
                if (_gpuFrameTimes.Count > 0 && framesWithoutNewTiming >= 2) yield break;
            }
        }

        private IEnumerator DrainWarmupTimingWindow()
        {
            int framesWithoutNewTiming = 0;
            int initialTimingKeyCount = _gpuTimingKeys.Count;
            for (int i = 0; i < GpuDrainFrameLimit; i++)
            {
                yield return RenderBenchmarkCameraFrame(true);
                if (!_cameraFrameObserved)
                {
                    _renderMode = "unavailable";
                    yield break;
                }
                _drainFramesObserved++;
                _warmupTimingDrainFrames++;
                _gpuCollectionFrames++;
                int before = _gpuTimingKeys.Count;
                CollectFrameTimings();
                CollectNativeGpuSamples("warmup");
                framesWithoutNewTiming = before == _gpuTimingKeys.Count
                    ? framesWithoutNewTiming + 1
                    : 0;
                if (_gpuTimingKeys.Count > initialTimingKeyCount && framesWithoutNewTiming >= 2) yield break;
            }
        }

        private void BeginGpuSampleWindow(int sampleFrames)
        {
            _gpuSampleCutoffTimestamp = _maxGpuTimingKey;
            _gpuFrameTimes.Clear();
            _gpuPositiveTimingKeys.Clear();
            _gpuSampleWindowStarted = true;
            if (_nativeGpuTimer != null && _nativeGpuTimer.IsSupported)
            {
                _nativeGpuTimer.BeginSampleWindow(sampleFrames);
                _nativeGpuTimes.Clear();
                _nativeGpuSamples = 0;
                _nativeGpuRowsWritten = 0;
                _nativeGpuInvalidSamples = 0;
            }
        }

        private void InitializeNativeGpuTimer(string outputPath)
        {
            // End is queued immediately after each SingleCameraRequest returns so
            // the scope includes URP's post-AfterPP tail without waiting for the
            // next simulation Tick.
            _nativeGpuTimer = new NativeGpuCameraTimer(_camera, true);
            _nativeGpuSource = _nativeGpuTimer.IsSupported
                ? "native_d3d11_rendergraph_timestamp_pending"
                : "NA";
            _nativeGpuRawPath = outputPath + ".gpu.csv";
            try
            {
                _nativeGpuWriter = new StreamWriter(_nativeGpuRawPath, false, new UTF8Encoding(false), 32768);
                _nativeGpuWriter.WriteLine("scope_id,gpu_ms,render_thread_event_ms,flags,phase");
            }
            catch (Exception exception)
            {
                _nativeGpuWriter?.Dispose();
                _nativeGpuWriter = null;
                _nativeGpuRawPath = null;
                _nativeGpuSource = _nativeGpuTimer.IsSupported
                    ? "native_d3d11_rendergraph_timestamp_raw_unavailable"
                    : "NA";
                Debug.LogWarning("OpenOita V2 display benchmark: 无法创建native GPU原始scope CSV：" + exception.Message);
            }
        }

        private void CollectNativeGpuSamples(string phase)
        {
            if (_nativeGpuTimer == null || !_nativeGpuTimer.IsSupported) return;
            int count = _nativeGpuTimer.ReadCompleted(_nativeGpuReadBuffer);
            for (int i = 0; i < count; i++)
            {
                NativeGpuCameraTimer.NativeGpuScopeSample sample = _nativeGpuReadBuffer[i];
                if (!_nativeGpuTimer.IsSampleScope(sample.ScopeId)) continue;
                string rowPhase = sample.Flags == 0 ? phase : phase + "_invalid";
                if (_nativeGpuWriter != null)
                {
                    _nativeGpuWriter.Write(sample.ScopeId.ToString(CultureInfo.InvariantCulture));
                    _nativeGpuWriter.Write(",");
                    _nativeGpuWriter.Write(sample.Flags == 0
                        ? sample.GpuMilliseconds.ToString("R", CultureInfo.InvariantCulture)
                        : "NA");
                    _nativeGpuWriter.Write(",");
                    _nativeGpuWriter.Write(sample.Flags == 0
                        ? sample.RenderThreadEventMilliseconds.ToString("R", CultureInfo.InvariantCulture)
                        : "NA");
                    _nativeGpuWriter.Write(",");
                    _nativeGpuWriter.Write(sample.Flags.ToString(CultureInfo.InvariantCulture));
                    _nativeGpuWriter.Write(",");
                    _nativeGpuWriter.WriteLine(rowPhase);
                    _nativeGpuRowsWritten++;
                }
                if (sample.Flags != 0 || sample.GpuMilliseconds <= 0 ||
                    double.IsNaN(sample.GpuMilliseconds) || double.IsInfinity(sample.GpuMilliseconds))
                {
                    _nativeGpuInvalidSamples++;
                    continue;
                }
                _nativeGpuTimes.Add(sample.GpuMilliseconds);
                _nativeGpuSamples++;
            }
            _nativeGpuDropCount = _nativeGpuTimer.DropCount;
            if (_nativeGpuSamples > 0)
                _nativeGpuSource = "native_d3d11_rendergraph_timestamp";
        }

        private void CollectFrameTimings()
        {
            _gpuCaptureCalls++;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                _cpuTimerFrequency = FrameTimingManager.GetCpuTimerFrequency();
                _gpuTimerFrequency = FrameTimingManager.GetGpuTimerFrequency();
                uint count = FrameTimingManager.GetLatestTimings((uint)_frameTimings.Length, _frameTimings);
                _gpuTimingRecords += (int)count;
                for (int i = 0; i < count; i++)
                {
                    FrameTiming timing = _frameTimings[i];
                    ulong key = TimingKey(timing);
                    if (key == 0)
                    {
                        // A positive timing without an identity cannot be deduplicated, so
                        // excluding it is safer than inflating the global sample count.
                        _gpuTimingUnkeyedCount++;
                        continue;
                    }
                    _gpuTimingKeys.Add(key);
                    if (key > _maxGpuTimingKey) _maxGpuTimingKey = key;
                    double gpuMs = timing.gpuFrameTime;
                    if (gpuMs <= 0 || double.IsNaN(gpuMs) || double.IsInfinity(gpuMs))
                    {
                        _gpuTimingZeroCount++;
                        continue;
                    }
                    if (!_gpuSampleWindowStarted || key <= _gpuSampleCutoffTimestamp) continue;
                    if (_gpuPositiveTimingKeys.Add(key)) _gpuFrameTimes.Add(gpuMs);
                }
            }
            catch (Exception exception)
            {
                if (string.IsNullOrEmpty(_gpuTimingError)) _gpuTimingError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        private void PrimeFrameTimingWindow()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                _cpuTimerFrequency = FrameTimingManager.GetCpuTimerFrequency();
                _gpuTimerFrequency = FrameTimingManager.GetGpuTimerFrequency();
                uint count = FrameTimingManager.GetLatestTimings((uint)_frameTimings.Length, _frameTimings);
                for (int i = 0; i < count; i++)
                {
                    ulong key = TimingKey(_frameTimings[i]);
                    if (key != 0)
                    {
                        _gpuTimingKeys.Add(key);
                        if (key > _maxGpuTimingKey) _maxGpuTimingKey = key;
                    }
                }
            }
            catch (Exception exception)
            {
                if (string.IsNullOrEmpty(_gpuTimingError)) _gpuTimingError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        private static ulong TimingKey(FrameTiming timing)
        {
            if (timing.frameStartTimestamp != 0) return timing.frameStartTimestamp;
            if (timing.firstSubmitTimestamp != 0) return timing.firstSubmitTimestamp;
            if (timing.cpuTimePresentCalled != 0) return timing.cpuTimePresentCalled;
            return timing.cpuTimeFrameComplete;
        }

        private void WriteSample(double stepMs, bool renderFrame, double gpuMs, double renderRequestSubmitMs)
        {
            _sampleRowsWritten++;
            Write(_scenario == null ? "unknown" : _scenario.Name);
            Write(",sample,");
            Write(_world.Version.CommittedTick.ToString(CultureInfo.InvariantCulture));
            Write(",");
            Write(_world.Version.Generation.ToString(CultureInfo.InvariantCulture));
            Write(",1,");
            Write(renderFrame ? "1," : "0,");
            Write(stepMs.ToString("R", CultureInfo.InvariantCulture));
            Write(",");
            Write(renderFrame ? _world.Display.LastPreparationMs.ToString("R", CultureInfo.InvariantCulture) : "NA");
            Write(",");
            Write(renderFrame && _world.Display.LastPreparationAllocationSupported
                ? _world.Display.LastPreparationAllocated.ToString(CultureInfo.InvariantCulture) : "NA");
            Write(",");
            Write(renderFrame ? _world.Display.Renderer.LastDrawSubmissionMs.ToString("R", CultureInfo.InvariantCulture) : "NA");
            Write(",");
            Write(double.IsNaN(gpuMs) ? "NA" : gpuMs.ToString("R", CultureInfo.InvariantCulture));
            Write(",");
            Write(_world.Display.Renderer.EstimatedFireVertexCount.ToString(CultureInfo.InvariantCulture));
            Write(",");
            Write(_world.Display.Renderer.ActiveTileCount.ToString(CultureInfo.InvariantCulture));
            Write(",");
            Write(_world.Display.Renderer.LastFramePixelUpdates.ToString(CultureInfo.InvariantCulture));
            Write(",");
            Write(_world.Display.Renderer.LastFramePoseUpdates.ToString(CultureInfo.InvariantCulture));
            Write(",");
            Write(_renderMode);
            Write(",");
            Write(double.IsNaN(renderRequestSubmitMs) ? "NA" : renderRequestSubmitMs.ToString("R", CultureInfo.InvariantCulture));
            _writer.WriteLine();
        }

        private void SaveValidationPng(string outputPath)
        {
            if (_target == null || !_target.IsCreated() || _renderFramesObserved + _drainFramesObserved == 0)
            {
                _validationPngError = "没有收到真实URP相机帧，跳过PNG读回。";
                return;
            }

            string path = outputPath + ".png";
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                RenderTexture.active = _target;
                readback = new Texture2D(_target.width, _target.height, TextureFormat.RGBA32, false, false)
                {
                    name = "OpenOita V2 Display Benchmark Readback"
                };
                readback.ReadPixels(new Rect(0, 0, _target.width, _target.height), 0, 0, false);
                readback.Apply(false, false);
                File.WriteAllBytes(path, readback.EncodeToPNG());
                _validationPngPath = path;
                _validationPngSaved = true;
            }
            catch (Exception exception)
            {
                _validationPngError = exception.GetType().Name + ": " + exception.Message;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) DestroyUnityObject(readback);
            }
        }

        [Serializable]
        private sealed class BenchmarkMetadata
        {
            public bool allocationSupported;
            public bool allocationInstrumented;
            public string allocationMetric, allocationStatus;
            public long allocationPositiveControlBytes, allocationEmptyScopeBytes;
            public string schema;
            public string completedUtc;
            public string csvPath;
            public string scenario;
            public int initialMaterialCells;
            public int initialFlowCells;
            public int initialWidth;
            public int initialHeight;
            public int initialBodyCount;
            public string unityVersion;
            public string applicationVersion;
            public string platform;
            public string operatingSystem;
            public string processor;
            public int processorCount;
            public int systemMemoryMb;
            public string graphicsDevice;
            public string graphicsDeviceType;
            public string graphicsDeviceVersion;
            public int graphicsMemoryMb;
            public int targetWidth;
            public int targetHeight;
            public int warmupTicks;
            public int requestedSamples;
            public int attemptedTicks;
            public int completedTicks;
            public string committedTick;
            public string generation;
            public bool renderAvailable;
            public int renderFramesObserved;
            public int drainFramesObserved;
            public int gpuCollectionFrames;
            public int sampleRowsWritten;
            public int gpuCaptureCalls;
            public int gpuTimingRecordsReturned;
            public int gpuSamples;
            public int gpuTimingZeroOrUnavailable;
            public int gpuTimingUnkeyed;
            public bool gpuFeatureEnabled;
            public bool gpuSampleWindowStarted;
            public string gpuSampleCutoffTimestamp;
            public string cpuTimerFrequencyHz;
            public string gpuTimerFrequencyHz;
            public string gpuTimingMode;
            public string gpuPerTick;
            public string gpuTimestampIdentity;
            public bool gpuWindowIncludesDrainFrames;
            public int gpuDrainFrameLimit;
            public string gpuMeanMs;
            public string gpuP50Ms;
            public string gpuP95Ms;
            public string gpuP99Ms;
            public string gpuMinMs;
            public string gpuMaxMs;
            public string gpuTimingError;
            public string gpuSource;
            public string nativeGpuPlugin;
            public bool nativeGpuSupported;
            public string nativeGpuSupportError;
            public string nativeGpuRawScopesPath;
            public int nativeGpuSamples;
            public int nativeGpuInvalidSamples;
            public int nativeGpuRowsWritten;
            public int nativeGpuDropCount;
            public string nativeGpuMeanMs;
            public string nativeGpuP50Ms;
            public string nativeGpuP95Ms;
            public string nativeGpuP99Ms;
            public string nativeGpuMinMs;
            public string nativeGpuMaxMs;
            public string nativeGpuScopeIdentity;
            public string nativeGpuScopeMode;
            public string nativeGpuScopeEndPoint;
            public string nativeGpuScopeCoverage;
            public string nativeGpuMetric;
            public bool nativeGpuMayIncludeQueueIdleOrSubmissionGap;
            public bool nativeGpuRawIncludesRenderThreadEventMs;
            public bool nativeGpuEndQueryAsyncFlush;
            public bool nativeGpuIncludesCpuPreparation;
            public bool nativeGpuIncludesCpuSubmit;
            public bool nativeGpuIncludesPostProcessing;
            public bool nativeGpuIncludesFinalPostProcessing;
            public bool nativeGpuIncludesPixelPerfectUpscale;
            public bool nativeGpuIncludesFinalBlit;
            public bool nativeGpuIncludesOverlayUi;
            public bool nativeGpuLegacyCallbackScopesExcluded;
            public int nativeGpuBeginPassExecutions;
            public int nativeGpuEndPassExecutions;
            public int nativeGpuCameraTailEndCommands;
            public string nativeGpuNextScopeId;
            public string activeRenderPipeline;
            public string activeCameraRenderer;
            public string activeRendererData;
            public bool activeNativeGpuTimingFeature;
            public int activeQualityLevel;
            public bool nativeGpuWindowIncludesDrainFrames;
            public string nativeGpuScopeStartInclusive, nativeGpuScopeEndExclusive;
            public int warmupTimingDrainFrames;
            public int finalTimingDrainFrames;
            public string renderMode;
            public bool renderRequestAttempted;
            public bool renderRequestSupported;
            public int renderRequestFrames;
            public string renderRequestSubmitTotalMs;
            public string renderRequestSubmitLastMs;
            public string renderRequestSubmitCoverage;
            public bool renderRequestSubmitIncludesDisplayPreparation;
            public string renderRequestSubmitScopeRelation;
            public string renderRequestError;
            public string validationPngPath;
            public bool validationPngSaved;
            public string validationPngError;
            public bool runInBackground;
            public int vSyncCount;
            public int targetFrameRate;
        }

        private void WriteMetadata(string outputPath, int warmup, int samples, bool renderAvailable)
        {
            if (_gpuFrameTimes.Count > 1) _gpuFrameTimes.Sort();
            if (_nativeGpuTimes.Count > 1) _nativeGpuTimes.Sort();
            if (_nativeGpuTimer != null && _nativeGpuTimer.IsSupported && _nativeGpuSamples == 0)
                _nativeGpuSource = "native_d3d11_rendergraph_no_completed_samples";
            string metadataPath = outputPath + ".metadata.json";
            BenchmarkMetadata metadata = new BenchmarkMetadata
            {
                schema = "OpenOita.V2.DisplayBenchmarkMetadata.v2",
                allocationSupported = ScopedAllocationProbe.Info.Supported,
                allocationInstrumented = ScopedAllocationProbe.Info.Instrumented,
                allocationMetric = ScopedAllocationProbe.Info.Metric,
                allocationStatus = ScopedAllocationProbe.Info.Status,
                allocationPositiveControlBytes = ScopedAllocationProbe.Info.PositiveControlBytes,
                allocationEmptyScopeBytes = ScopedAllocationProbe.Info.EmptyScopeBytes,
                completedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                csvPath = outputPath,
                scenario = _scenario == null ? "unknown" : _scenario.Name,
                initialMaterialCells = _initialMaterialCells,
                initialFlowCells = _initialFlowCells,
                initialWidth = _world == null ? 0 : _world.Config.Width,
                initialHeight = _world == null ? 0 : _world.Config.Height,
                initialBodyCount = _initialBodyCount,
                unityVersion = Application.unityVersion,
                applicationVersion = Application.version,
                platform = Application.platform.ToString(),
                operatingSystem = SystemInfo.operatingSystem,
                processor = SystemInfo.processorType,
                processorCount = SystemInfo.processorCount,
                systemMemoryMb = SystemInfo.systemMemorySize,
                graphicsDevice = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMemoryMb = SystemInfo.graphicsMemorySize,
                targetWidth = TargetWidth,
                targetHeight = TargetHeight,
                warmupTicks = warmup,
                requestedSamples = samples,
                attemptedTicks = warmup + samples,
                completedTicks = _stepIterationsCompleted,
                committedTick = _world == null ? "NA" : _world.Version.CommittedTick.ToString(CultureInfo.InvariantCulture),
                generation = _world == null ? "NA" : _world.Version.Generation.ToString(CultureInfo.InvariantCulture),
                renderAvailable = renderAvailable,
                renderFramesObserved = _renderFramesObserved,
                drainFramesObserved = _drainFramesObserved,
                gpuCollectionFrames = _gpuCollectionFrames,
                sampleRowsWritten = _sampleRowsWritten,
                gpuCaptureCalls = _gpuCaptureCalls,
                gpuTimingRecordsReturned = _gpuTimingRecords,
                gpuSamples = _gpuFrameTimes.Count,
                gpuTimingZeroOrUnavailable = _gpuTimingZeroCount,
                gpuTimingUnkeyed = _gpuTimingUnkeyedCount,
                gpuFeatureEnabled = IsFrameTimingFeatureEnabled(),
                gpuSampleWindowStarted = _gpuSampleWindowStarted,
                gpuSampleCutoffTimestamp = _gpuSampleWindowStarted && _gpuSampleCutoffTimestamp != 0
                    ? _gpuSampleCutoffTimestamp.ToString(CultureInfo.InvariantCulture)
                    : "NA",
                cpuTimerFrequencyHz = _cpuTimerFrequency.ToString(CultureInfo.InvariantCulture),
                gpuTimerFrequencyHz = _gpuTimerFrequency.ToString(CultureInfo.InvariantCulture),
                gpuTimingMode = "global_continuous_render_window",
                gpuPerTick = "NA",
                gpuTimestampIdentity = "frameStartTimestamp|firstSubmitTimestamp|cpuTimePresentCalled|cpuTimeFrameComplete",
                gpuWindowIncludesDrainFrames = _drainFramesObserved > 0,
                gpuDrainFrameLimit = GpuDrainFrameLimit,
                gpuMeanMs = MeanGpuFrameMs(),
                gpuP50Ms = PercentileGpuFrameMs(0.50),
                gpuP95Ms = PercentileGpuFrameMs(0.95),
                gpuP99Ms = PercentileGpuFrameMs(0.99),
                gpuMinMs = _gpuFrameTimes.Count == 0 ? "NA" : _gpuFrameTimes[0].ToString("R", CultureInfo.InvariantCulture),
                gpuMaxMs = _gpuFrameTimes.Count == 0 ? "NA" : _gpuFrameTimes[_gpuFrameTimes.Count - 1].ToString("R", CultureInfo.InvariantCulture),
                gpuTimingError = string.IsNullOrEmpty(_gpuTimingError) ? "NA" : _gpuTimingError,
                gpuSource = _nativeGpuSource,
                nativeGpuPlugin = "OpenOitaGpuTimer",
                nativeGpuSupported = _nativeGpuTimer != null && _nativeGpuTimer.IsSupported,
                nativeGpuSupportError = _nativeGpuTimer == null ? "NA" : _nativeGpuTimer.SupportError,
                nativeGpuRawScopesPath = _nativeGpuRawPath ?? "NA",
                nativeGpuSamples = _nativeGpuSamples,
                nativeGpuInvalidSamples = _nativeGpuInvalidSamples,
                nativeGpuRowsWritten = _nativeGpuRowsWritten,
                nativeGpuDropCount = _nativeGpuDropCount,
                nativeGpuMeanMs = MeanNativeGpuMs(),
                nativeGpuP50Ms = PercentileNativeGpuMs(0.50),
                nativeGpuP95Ms = PercentileNativeGpuMs(0.95),
                nativeGpuP99Ms = PercentileNativeGpuMs(0.99),
                nativeGpuMinMs = _nativeGpuTimes.Count == 0 ? "NA" : _nativeGpuTimes[0].ToString("R", CultureInfo.InvariantCulture),
                nativeGpuMaxMs = _nativeGpuTimes.Count == 0 ? "NA" : _nativeGpuTimes[_nativeGpuTimes.Count - 1].ToString("R", CultureInfo.InvariantCulture),
                nativeGpuScopeIdentity = "monotonic_render_scope_id_after_warmup_cutoff",
                nativeGpuScopeMode = "urp2d_rendergraph_before_rendering_to_after_camera_tail",
                nativeGpuScopeEndPoint = "after_camera_submit_graphics_execute_command_buffer",
                nativeGpuScopeCoverage = "urp2d_rendergraph_to_camera_tail_includes_pixelperfect_finalpost_finalblit_overlay_debug_gizmo_if_executed",
                nativeGpuMetric = "elapsed_device_timestamps_including_queue_idle",
                nativeGpuMayIncludeQueueIdleOrSubmissionGap = true,
                nativeGpuRawIncludesRenderThreadEventMs = true,
                nativeGpuEndQueryAsyncFlush = true,
                nativeGpuIncludesCpuPreparation = false,
                nativeGpuIncludesCpuSubmit = false,
                nativeGpuIncludesPostProcessing = true,
                nativeGpuIncludesFinalPostProcessing = true,
                nativeGpuIncludesPixelPerfectUpscale = true,
                nativeGpuIncludesFinalBlit = true,
                nativeGpuIncludesOverlayUi = true,
                nativeGpuLegacyCallbackScopesExcluded = true,
                nativeGpuBeginPassExecutions = _nativeGpuTimer == null ? 0 : _nativeGpuTimer.BeginPassExecutions,
                nativeGpuEndPassExecutions = _nativeGpuTimer == null ? 0 : _nativeGpuTimer.EndPassExecutions,
                nativeGpuCameraTailEndCommands = _nativeGpuTimer == null ? 0 : _nativeGpuTimer.CameraTailEndCommands,
                nativeGpuNextScopeId = _nativeGpuTimer == null
                    ? "NA" : _nativeGpuTimer.NextScopeId.ToString(CultureInfo.InvariantCulture),
                activeRenderPipeline = ActiveRenderPipelineName(),
                activeCameraRenderer = ActiveCameraRendererName(),
                activeRendererData = ActiveRendererDataName(),
                activeNativeGpuTimingFeature = ActiveNativeGpuTimingFeature(),
                activeQualityLevel = QualitySettings.GetQualityLevel(),
                nativeGpuWindowIncludesDrainFrames = false,
                nativeGpuScopeStartInclusive = _nativeGpuTimer == null ? "NA" : _nativeGpuTimer.SampleScopeCutoff.ToString(CultureInfo.InvariantCulture),
                nativeGpuScopeEndExclusive = _nativeGpuTimer == null ? "NA" : _nativeGpuTimer.SampleScopeEndExclusive.ToString(CultureInfo.InvariantCulture),
                warmupTimingDrainFrames = _warmupTimingDrainFrames,
                finalTimingDrainFrames = _finalTimingDrainFrames,
                renderMode = _renderMode,
                renderRequestAttempted = _requestModeAttempted,
                renderRequestSupported = _requestModeSupported,
                renderRequestFrames = _renderRequestFrames,
                renderRequestSubmitTotalMs = _renderRequestFrames == 0
                    ? "NA" : _renderRequestSubmitTotalMs.ToString("R", CultureInfo.InvariantCulture),
                renderRequestSubmitLastMs = double.IsNaN(_lastRenderRequestSubmitMs)
                    ? "NA" : _lastRenderRequestSubmitMs.ToString("R", CultureInfo.InvariantCulture),
                renderRequestSubmitCoverage = _renderMode == "single_camera_request"
                    ? "all_single_camera_request_calls_warmup_sample_drain_including_prepare_camera"
                    : "NA_automatic_camera",
                renderRequestSubmitIncludesDisplayPreparation = _renderMode == "single_camera_request",
                renderRequestSubmitScopeRelation = _renderMode == "single_camera_request"
                    ? "outer_cpu_scope_contains_material_world_display_prepare_and_rendergraph_recording;do_not_add_display_prepare_ms"
                    : "NA_automatic_camera",
                renderRequestError = string.IsNullOrEmpty(_renderRequestError) ? "NA" : _renderRequestError,
                validationPngPath = _validationPngPath ?? "NA",
                validationPngSaved = _validationPngSaved,
                validationPngError = string.IsNullOrEmpty(_validationPngError) ? "NA" : _validationPngError,
                runInBackground = Application.runInBackground,
                vSyncCount = QualitySettings.vSyncCount,
                targetFrameRate = Application.targetFrameRate
            };

            try
            {
                File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true), new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                Debug.LogError("OpenOita V2 display benchmark: 无法写入metadata " + metadataPath + ": " + exception.Message);
            }
        }

        private string MeanGpuFrameMs()
        {
            if (_gpuFrameTimes.Count == 0) return "NA";
            double total = 0;
            for (int i = 0; i < _gpuFrameTimes.Count; i++) total += _gpuFrameTimes[i];
            return (total / _gpuFrameTimes.Count).ToString("R", CultureInfo.InvariantCulture);
        }

        private string PercentileGpuFrameMs(double quantile)
        {
            if (_gpuFrameTimes.Count == 0) return "NA";
            double position = (_gpuFrameTimes.Count - 1) * quantile;
            int lower = (int)position;
            int upper = Math.Min(lower + 1, _gpuFrameTimes.Count - 1);
            double fraction = position - lower;
            double value = _gpuFrameTimes[lower] + (_gpuFrameTimes[upper] - _gpuFrameTimes[lower]) * fraction;
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private string MeanNativeGpuMs()
        {
            if (_nativeGpuTimes.Count == 0) return "NA";
            double total = 0;
            for (int i = 0; i < _nativeGpuTimes.Count; i++) total += _nativeGpuTimes[i];
            return (total / _nativeGpuTimes.Count).ToString("R", CultureInfo.InvariantCulture);
        }

        private string PercentileNativeGpuMs(double quantile)
        {
            if (_nativeGpuTimes.Count == 0) return "NA";
            double position = (_nativeGpuTimes.Count - 1) * quantile;
            int lower = (int)position;
            int upper = Math.Min(lower + 1, _nativeGpuTimes.Count - 1);
            double fraction = position - lower;
            double value = _nativeGpuTimes[lower] + (_nativeGpuTimes[upper] - _nativeGpuTimes[lower]) * fraction;
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private bool IsFrameTimingFeatureEnabled()
        {
            try { return FrameTimingManager.IsFeatureEnabled(); }
            catch { return false; }
        }

        private string ActiveRenderPipelineName()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            return pipeline == null ? "NA" : pipeline.name + "|" + pipeline.GetType().FullName;
        }

        private string ActiveCameraRendererName()
        {
            if (_camera == null) return "NA";
            UniversalAdditionalCameraData cameraData =
                _camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null) return "NA";
            try
            {
                ScriptableRenderer renderer = cameraData.scriptableRenderer;
                return renderer == null ? "NA" : renderer.GetType().FullName;
            }
            catch (Exception exception)
            {
                return "error:" + exception.GetType().Name;
            }
        }

        private string ActiveRendererDataName()
        {
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline
                as UniversalRenderPipelineAsset;
            if (pipeline == null || pipeline.rendererDataList.Length == 0 || pipeline.rendererDataList[0] == null)
                return "NA";
            ScriptableRendererData rendererData = pipeline.rendererDataList[0];
            return rendererData.name + "|" + rendererData.GetType().FullName;
        }

        private bool ActiveNativeGpuTimingFeature()
        {
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline
                as UniversalRenderPipelineAsset;
            if (pipeline == null || pipeline.rendererDataList.Length == 0 || pipeline.rendererDataList[0] == null)
                return false;
            List<ScriptableRendererFeature> features = pipeline.rendererDataList[0].rendererFeatures;
            for (int i = 0; i < features.Count; i++)
                if (features[i] is NativeGpuTimingFeature && features[i].isActive)
                    return true;
            return false;
        }

        private void Write(string value) => _writer.Write(value);

        private void Cleanup()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            _nativeGpuTimer?.Dispose();
            _nativeGpuTimer = null;
            _nativeGpuWriter?.Dispose();
            _nativeGpuWriter = null;
            if (_camera != null) _camera.targetTexture = null;
            if (_target != null)
            {
                _target.Release();
                DestroyUnityObject(_target);
                _target = null;
            }
            if (_cameraObject != null)
            {
                DestroyUnityObject(_cameraObject);
                _cameraObject = null;
                _camera = null;
            }
            for (int i = 0; i < _disabledCameras.Count; i++)
                if (_disabledCameras[i] != null) _disabledCameras[i].enabled = _cameraStates[i];
            _disabledCameras.Clear();
            _cameraStates.Clear();
            _world?.Dispose();
            _world = null;
            _writer?.Dispose();
            _writer = null;
            RestoreBenchmarkRuntime();
            if (ScopedAllocationProbe.IsInitialized) ScopedAllocationProbe.Shutdown();
        }

        private void FinishAndMaybeQuit()
        {
            enabled = false;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-openoita-v2-display-benchmark-exit") >= 0)
                Application.Quit();
        }

        private static void DestroyUnityObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        private static double Milliseconds(long start) =>
            (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        private static bool HasFlag()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool HasArgument(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Argument(string prefix, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return args[i].Substring(prefix.Length);
            return fallback;
        }

        private static int PositiveArgument(string prefix, int fallback)
        {
            string value = Argument(prefix, fallback.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
                ? result : fallback;
        }
    }
}
