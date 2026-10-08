using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace OpenOita.V2.Validation
{
    /// <summary>
    /// Benchmark-only D3D11 timestamp scope around one real URP RenderGraph camera.
    ///
    /// The timing feature calls <see cref="IssueBegin(CommandBuffer)"/> from a
    /// RenderGraph render function. In the benchmark's default tail mode, the end
    /// event is queued in a reused Graphics CommandBuffer immediately after
    /// SingleCameraRequest returns, so the scope reaches the renderer tail without
    /// relying on the next simulation Tick. The legacy <see
    /// cref="IssueEnd(CommandBuffer)"/> path remains available for a feature that
    /// explicitly uses an AfterRendering RenderGraph pass. Neither path forces an
    /// explicit ScriptableRenderContext.Submit or a CPU wait.
    /// </summary>
    public sealed class NativeGpuCameraTimer : IDisposable
    {
        private const string PluginName = "OpenOitaGpuTimer";
        private const int ReadCapacity = 128;
        private static NativeGpuCameraTimer _active;

        private readonly Camera _camera;
        private readonly bool _endAtCameraTail;
        private readonly NativeGpuScopeSample[] _readBuffer = new NativeGpuScopeSample[ReadCapacity];
        private CommandBuffer _cameraTailCommandBuffer;
        private IntPtr _renderEventFunction;
        private int _pollEventId;
        private bool _disposed;
        private int _activeSlot = -1;
        private ulong _activeScopeId;
        private ulong _nextScopeId = 1;
        private ulong _sampleScopeCutoff;
        private ulong _sampleScopeEnd;
        private bool _sampleWindowStarted;
        private int _beginPassExecutions;
        private int _endPassExecutions;
        private int _cameraTailEndCommands;
        private string _supportError;

        public NativeGpuCameraTimer(Camera camera, bool endAtCameraTail = true)
        {
            _camera = camera;
            _endAtCameraTail = endAtCameraTail;
            if (camera == null)
            {
                _supportError = "目标相机为空。";
                return;
            }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                _supportError = "原生D3D11计时仅支持Direct3D11；当前为 " + SystemInfo.graphicsDeviceType + "。";
                return;
            }

            try
            {
                if (Native_IsSupported() == 0)
                {
                    _supportError = "OpenOitaGpuTimer.dll未加载，或Unity图形设备尚未提供D3D11查询。";
                    return;
                }
                _renderEventFunction = Native_GetRenderEventFunc();
                _pollEventId = Native_GetPollEventId();
                if (_renderEventFunction == IntPtr.Zero || _pollEventId < 0)
                {
                    _supportError = "原生计时器事件函数不可用。";
                    return;
                }

                // There is one benchmark camera/timer at a time. Never silently
                // redirect the RenderGraph feature to a second timer instance.
                if (_active != null && !_active._disposed && !ReferenceEquals(_active, this))
                {
                    _supportError = "已有活动的V2原生GPU RenderGraph计时器。";
                    return;
                }
                _active = this;
                IsSupported = true;
            }
            catch (DllNotFoundException exception)
            {
                _supportError = exception.Message;
            }
            catch (EntryPointNotFoundException exception)
            {
                _supportError = exception.Message;
            }
            catch (BadImageFormatException exception)
            {
                _supportError = exception.Message;
            }
        }

        public bool IsSupported { get; }
        public string SupportError => _supportError ?? "NA";
        public ulong SampleScopeCutoff => _sampleScopeCutoff;
        public ulong SampleScopeEndExclusive => _sampleScopeEnd;
        public ulong NextScopeId => _nextScopeId;
        public int BeginPassExecutions => _beginPassExecutions;
        public int EndPassExecutions => _endPassExecutions;
        public int CameraTailEndCommands => _cameraTailEndCommands;
        public bool EndAtCameraTail => _endAtCameraTail;
        public static bool ActiveUsesCameraTail => _active != null && _active._endAtCameraTail;
        public static Camera ActiveTargetCamera => _active != null && !_active._disposed
            ? _active._camera : null;
        public static bool HasActiveTimer => _active != null && !_active._disposed && _active.IsSupported;

        public int DropCount
        {
            get
            {
                if (!IsSupported) return 0;
                try { return Native_GetDropCount(); }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Marks the first scope ID in the measured window. Warmup scopes which
        /// complete after this call are rejected by ID, without assuming queue depth.
        /// </summary>
        public void BeginSampleWindow(int sampleFrames)
        {
            if (!IsSupported) return;
            _sampleScopeCutoff = _nextScopeId;
            _sampleScopeEnd = checked(_sampleScopeCutoff + (ulong)sampleFrames);
            _sampleWindowStarted = true;
            ReadCompleted(_readBuffer);
        }

        public int ReadCompleted(NativeGpuScopeSample[] destination)
        {
            if (!IsSupported || destination == null || destination.Length == 0) return 0;
            int capacity = Math.Min(destination.Length, ReadCapacity);
            try
            {
                return Native_ReadCompleted(destination, capacity);
            }
            catch (Exception exception) when (exception is DllNotFoundException ||
                                              exception is EntryPointNotFoundException ||
                                              exception is BadImageFormatException)
            {
                return 0;
            }
        }

        public bool IsSampleScope(ulong scopeId)
        {
            return _sampleWindowStarted && scopeId >= _sampleScopeCutoff && scopeId < _sampleScopeEnd;
        }

        /// <summary>
        /// Called from a RenderGraph UnsafeGraphContext render function. This only
        /// appends plugin events to the graph command buffer; it never submits or waits.
        /// </summary>
        public static void IssueBegin(CommandBuffer commandBuffer)
        {
            _active?.IssueBeginInternal(commandBuffer);
        }

        /// <summary>
        /// Called from the matching AfterRendering RenderGraph pass when the timer
        /// was explicitly constructed with endAtCameraTail=false. The benchmark's
        /// default tail mode uses IssueEndAfterCamera instead.
        /// </summary>
        public static void IssueEnd(CommandBuffer commandBuffer)
        {
            _active?.IssueEndInternal(commandBuffer);
        }

        /// <summary>
        /// Queues the matching end event after a SingleCameraRequest has returned.
        /// Graphics.ExecuteCommandBuffer schedules the command without an explicit
        /// ScriptableRenderContext.Submit or a CPU wait. A persistent command buffer
        /// avoids per-frame managed allocation.
        /// </summary>
        public static void IssueEndAfterCamera()
        {
            _active?.IssueEndAfterCameraInternal();
        }

        public static void CancelActiveScope()
        {
            _active?.CancelScope();
        }

        private void IssueBeginInternal(CommandBuffer commandBuffer)
        {
            _beginPassExecutions++;
            if (_disposed || !IsSupported || commandBuffer == null || _activeSlot >= 0) return;

            // Polling is a render-thread event and uses DONOTFLUSH in the native
            // implementation. It observes older scopes before opening this one.
            commandBuffer.IssuePluginEvent(_renderEventFunction, _pollEventId);

            ulong scopeId = _nextScopeId++;
            int slot;
            try { slot = Native_ReserveScope(scopeId); }
            catch { slot = -1; }
            if (slot < 0) return;

            int beginEvent;
            try { beginEvent = Native_GetBeginEventId((uint)slot); }
            catch { beginEvent = -1; }
            if (beginEvent < 0)
            {
                try { Native_ReleaseScope((uint)slot); }
                catch { }
                return;
            }

            commandBuffer.IssuePluginEvent(_renderEventFunction, beginEvent);
            _activeSlot = slot;
            _activeScopeId = scopeId;
        }

        private void IssueEndInternal(CommandBuffer commandBuffer)
        {
            _endPassExecutions++;
            if (_endAtCameraTail || _disposed || !IsSupported || commandBuffer == null || _activeSlot < 0) return;

            int slot = _activeSlot;
            int endEvent;
            try { endEvent = Native_GetEndEventId((uint)slot); }
            catch { endEvent = -1; }
            if (endEvent >= 0)
            {
                commandBuffer.IssuePluginEvent(_renderEventFunction, endEvent);
                // The query may still be in flight; the plugin's poll path never
                // flushes and later frames can collect it asynchronously.
                commandBuffer.IssuePluginEvent(_renderEventFunction, _pollEventId);
            }
            else
            {
                try { Native_ReleaseScope((uint)slot); }
                catch { }
            }
            _activeSlot = -1;
            _activeScopeId = 0;
        }

        private void IssueEndAfterCameraInternal()
        {
            if (!_endAtCameraTail || _disposed || !IsSupported || _activeSlot < 0) return;

            int endEvent;
            try { endEvent = Native_GetEndEventId((uint)_activeSlot); }
            catch { endEvent = -1; }
            if (endEvent < 0)
            {
                CancelScope();
                return;
            }

            try
            {
                if (_cameraTailCommandBuffer == null)
                {
                    _cameraTailCommandBuffer = new CommandBuffer
                    {
                        name = "OpenOita V2 Native GPU Timestamp Camera Tail"
                    };
                }
                _cameraTailCommandBuffer.Clear();
                _cameraTailCommandBuffer.IssuePluginEvent(_renderEventFunction, endEvent);
                _cameraTailCommandBuffer.IssuePluginEvent(_renderEventFunction, _pollEventId);
                Graphics.ExecuteCommandBuffer(_cameraTailCommandBuffer);
                _cameraTailEndCommands++;
                _activeSlot = -1;
                _activeScopeId = 0;
            }
            catch
            {
                CancelScope();
            }
        }

        private void CancelScope()
        {
            if (_activeSlot < 0 || !IsSupported) return;
            try { Native_ReleaseScope((uint)_activeSlot); }
            catch { }
            _activeSlot = -1;
            _activeScopeId = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_active, this)) _active = null;
            CancelScope();
            if (_cameraTailCommandBuffer != null)
            {
                _cameraTailCommandBuffer.Release();
                _cameraTailCommandBuffer = null;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NativeGpuScopeSample
        {
            public ulong ScopeId;
            public double GpuMilliseconds;
            public double RenderThreadEventMilliseconds;
            public uint Flags;
            public uint Reserved;
        }

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_IsSupported", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_IsSupported();

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_GetRenderEventFunc", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr Native_GetRenderEventFunc();

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_GetPollEventId", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_GetPollEventId();

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_ReserveScope", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_ReserveScope(ulong scopeId);

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_ReleaseScope", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_ReleaseScope(uint slotIndex);

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_GetBeginEventId", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_GetBeginEventId(uint slotIndex);

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_GetEndEventId", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_GetEndEventId(uint slotIndex);

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_ReadCompleted", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_ReadCompleted([Out] NativeGpuScopeSample[] destination, int capacity);

        [DllImport(PluginName, EntryPoint = "OpenOitaGpuTimer_GetDropCount", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_GetDropCount();
    }
}
