using System;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace OpenOita.V2.Diagnostics
{
    /// <summary>
    /// Configuration for the allocation probe.  The positive-control buffer and the
    /// recorder sample ring are created during initialization, outside measured scopes.
    /// </summary>
    public readonly struct AllocationProbeConfiguration
    {
        public readonly int PositiveControlBytes;
        public readonly int RecorderSampleCapacity;
        public readonly bool AllowInstrumentedFallback;

        public AllocationProbeConfiguration(int positiveControlBytes = 4096, int recorderSampleCapacity = 64,
            bool allowInstrumentedFallback = false)
        {
            PositiveControlBytes = positiveControlBytes < 64 ? 64 : positiveControlBytes;
            RecorderSampleCapacity = recorderSampleCapacity < 16 ? 16 : recorderSampleCapacity;
            AllowInstrumentedFallback = allowInstrumentedFallback;
        }
    }

    /// <summary>Immutable result of the one-time backend validation.</summary>
    public readonly struct AllocationProbeInfo
    {
        public readonly bool Supported;
        public readonly string Status;
        public readonly string Metric;
        public readonly long PositiveControlBytes;
        public readonly long EmptyScopeBytes;
        public readonly int RecorderSampleCapacity;
        public readonly bool Instrumented;

        public AllocationProbeInfo(bool supported, string status, string metric,
            long positiveControlBytes, long emptyScopeBytes, int recorderSampleCapacity, bool instrumented)
        {
            Supported = supported; Status = status; Metric = metric;
            PositiveControlBytes = positiveControlBytes; EmptyScopeBytes = emptyScopeBytes;
            RecorderSampleCapacity = recorderSampleCapacity; Instrumented = instrumented;
        }
    }

    /// <summary>One measured scope. Unsupported/overflow scopes carry Usable=false and Bytes=-1.</summary>
    public readonly struct AllocationProbeMeasurement
    {
        public readonly bool Usable;
        public readonly long Bytes;
        public readonly string Status;
        public readonly string Metric;

        public AllocationProbeMeasurement(bool usable, long bytes, string status, string metric)
        {
            Usable = usable; Bytes = bytes; Status = status; Metric = metric;
        }
    }

    /// <summary>
    /// A no-heap-sampling allocation scope.  It prefers the managed thread allocation
    /// counter, and only falls back to instrumented ProfilerRecorder/Mono callbacks when
    /// explicitly enabled by the allocation-validation build. The class is deliberately
    /// static: there is one recorder and one active scope per benchmark thread.
    /// </summary>
    public static class ScopedAllocationProbe
    {
        public const string StatusSupported = "supported";
        public const string StatusUnsupported = "unsupported";
        public const string StatusUnreliable = "unreliable_positive_control";
        public const string StatusOverflow = "overflow";
        public const string StatusCounterFailure = "counter_failure";
        public const string StatusInstrumentationDisabled = "instrumentation_disabled";
        public const string StatusNativeUnsupported = "native_mono_allocation_profiler_unsupported";
        public const string MetricThread = "GC.GetAllocatedBytesForCurrentThread";
        public const string MetricProfiler = "Unity.Profiling.ProfilerRecorder:GC.Alloc";
        public const string MetricNative = "native_mono_allocation_profiler";
        private const string NativePluginName = "OpenOitaAllocationProbe";

        private static bool _initialized;
        private static bool _active;
        private static bool _threadBackend;
        private static bool _nativeBackend;
        private static bool _nativeLoaded;
        private static bool _recorderValid;
        private static bool _allowInstrumentedFallback;
        private static bool _profilerWasEnabled;
        private static bool _profilerChanged;
        private static long _scopeStart;
        private static long _emptyScopeBytes;
        private static int _sampleCapacity;
        private static ProfilerRecorder _recorder;
        private static AllocationProbeInfo _info;

        public static bool IsInitialized => _initialized;
        public static bool IsSupported => _initialized && _info.Supported;
        public static bool IsActive => _active;
        public static AllocationProbeInfo Info => _info;

        public static AllocationProbeInfo Initialize()
        {
            bool instrumented = Debug.isDebugBuild ||
                Array.IndexOf(Environment.GetCommandLineArgs(), "-openoita-v2-allocation-validation") >= 0;
            return Initialize(new AllocationProbeConfiguration(4096, 4096, instrumented));
        }

        public static AllocationProbeInfo Initialize(AllocationProbeConfiguration configuration)
        {
            if (_active) throw new InvalidOperationException("不能在AllocationProbe作用域内重新初始化。");
            if (_initialized) return _info;

            _sampleCapacity = Math.Max(16, configuration.RecorderSampleCapacity);
            int positiveBytes = Math.Max(64, configuration.PositiveControlBytes);
            _allowInstrumentedFallback = configuration.AllowInstrumentedFallback;
            if (TryValidateThreadCounter(positiveBytes,
                out long threadPositive, out long threadEmpty))
            {
                _threadBackend = true;
                _emptyScopeBytes = threadEmpty;
                _info = new AllocationProbeInfo(true, StatusSupported, MetricThread,
                    threadPositive, threadEmpty, _sampleCapacity, false);
                _initialized = true;
                return _info;
            }

            if (!_allowInstrumentedFallback)
            {
                _info = new AllocationProbeInfo(false, StatusInstrumentationDisabled, "none",
                    threadPositive, threadEmpty, _sampleCapacity, false);
                _initialized = true;
                return _info;
            }

            EnableProfilerForValidation();
            if (TryValidateProfilerRecorder(positiveBytes,
                out long profilerPositive, out long profilerEmpty, out string profilerStatus))
            {
                _threadBackend = false;
                _emptyScopeBytes = profilerEmpty;
                _info = new AllocationProbeInfo(true, StatusSupported, MetricProfiler,
                    profilerPositive, profilerEmpty, _sampleCapacity, true);
                _initialized = true;
                return _info;
            }

            DisposeRecorder();
            bool nativeValid = TryValidateNativeProfiler(positiveBytes,
                out long nativePositive, out long nativeEmpty, out string nativeStatus);
            RestoreProfilerState();
            if (nativeValid)
                nativeValid = TryValidateNativeProfiler(positiveBytes, out nativePositive,
                    out nativeEmpty, out nativeStatus);
            if (nativeValid)
            {
                _threadBackend = false;
                _nativeBackend = true;
                _emptyScopeBytes = nativeEmpty;
                _info = new AllocationProbeInfo(true, StatusSupported, MetricNative,
                    nativePositive, nativeEmpty, _sampleCapacity, true);
                _initialized = true;
                return _info;
            }

            DisposeRecorder();
            _info = new AllocationProbeInfo(false,
                nativeStatus == StatusNativeUnsupported && profilerStatus != StatusUnsupported
                    ? profilerStatus : nativeStatus,
                nativeStatus == StatusNativeUnsupported && profilerStatus != StatusUnsupported
                    ? MetricProfiler : MetricNative,
                nativePositive != 0 ? nativePositive : profilerPositive,
                nativePositive != 0 ? nativeEmpty : profilerEmpty,
                _sampleCapacity,
                profilerStatus != StatusUnsupported || nativeStatus != StatusNativeUnsupported);
            _initialized = true;
            return _info;
        }

        /// <summary>Begins one scope. Nested scopes are rejected instead of corrupting deltas.</summary>
        public static void Begin()
        {
            if (!_initialized) throw new InvalidOperationException("AllocationProbe尚未Initialize。");
            if (!_info.Supported) throw new InvalidOperationException("AllocationProbe不支持当前运行时。");
            if (_active) throw new InvalidOperationException("AllocationProbe不支持嵌套作用域。");

            if (_threadBackend)
            {
                _scopeStart = GC.GetAllocatedBytesForCurrentThread();
                _active = true;
                return;
            }

            if (_nativeBackend)
            {
                Native_Begin();
                _active = true;
                return;
            }

            if (!_recorderValid || !_recorder.Valid)
                throw new InvalidOperationException("GC.Alloc ProfilerRecorder不可用。");
            _recorder.Reset();
            _recorder.Start();
            _active = true;
        }

        /// <summary>
        /// Ends the active scope. ProfilerRecorder samples are read directly from its
        /// native ring; no managed sample list or heap/total-memory query is used.
        /// </summary>
        public static AllocationProbeMeasurement End()
        {
            if (!_active) throw new InvalidOperationException("AllocationProbe没有活动作用域。");
            _active = false;
            if (_threadBackend)
            {
                long end = GC.GetAllocatedBytesForCurrentThread();
                long delta = end - _scopeStart;
                if (delta < 0) return new AllocationProbeMeasurement(false, -1, StatusCounterFailure, MetricThread);
                delta -= _emptyScopeBytes;
                if (delta < 0) delta = 0;
                return new AllocationProbeMeasurement(true, delta, StatusSupported, MetricThread);
            }

            if (_nativeBackend)
            {
                ulong nativeBytes = Native_End();
                if (nativeBytes > long.MaxValue)
                    return new AllocationProbeMeasurement(false, -1, StatusOverflow, MetricNative);
                long delta = (long)nativeBytes - _emptyScopeBytes;
                if (delta < 0) delta = 0;
                return new AllocationProbeMeasurement(true, delta, StatusSupported, MetricNative);
            }

            _recorder.Stop();
            if (!_recorder.Valid)
                return new AllocationProbeMeasurement(false, -1, StatusUnsupported, MetricProfiler);
            long total = ReadRecorderSamples(out bool overflow);
            if (overflow) return new AllocationProbeMeasurement(false, -1, StatusOverflow, MetricProfiler);
            total -= _emptyScopeBytes;
            if (total < 0) total = 0;
            return new AllocationProbeMeasurement(true, total, StatusSupported, MetricProfiler);
        }

        /// <summary>Stops and releases the native recorder. A later Initialize may revalidate it.</summary>
        public static void Shutdown()
        {
            if (_active) throw new InvalidOperationException("不能在AllocationProbe作用域内Shutdown。");
            if (_nativeLoaded)
            {
                try { Native_Shutdown(); } catch (Exception) { }
            }
            if (_recorderValid)
            {
                _recorder.Stop();
                _recorder.Dispose();
            }
            _recorder = default;
            _recorderValid = false;
            _initialized = false;
            _threadBackend = false;
            _nativeBackend = false;
            _nativeLoaded = false;
            _allowInstrumentedFallback = false;
            _scopeStart = 0;
            _emptyScopeBytes = 0;
            _sampleCapacity = 0;
            _info = default;
            RestoreProfilerState();
        }

        private static bool TryValidateThreadCounter(int positiveBytes, out long positive, out long empty)
        {
            positive = 0; empty = 0;
            try
            {
                // Warm the API call path before taking the validation deltas.
                GC.GetAllocatedBytesForCurrentThread();
                GC.GetAllocatedBytesForCurrentThread();
                long emptyStart = GC.GetAllocatedBytesForCurrentThread();
                Noop();
                empty = GC.GetAllocatedBytesForCurrentThread() - emptyStart;
                long positiveStart = GC.GetAllocatedBytesForCurrentThread();
                byte[] allocation = new byte[positiveBytes];
                allocation[0] = 1;
                GC.KeepAlive(allocation);
                positive = GC.GetAllocatedBytesForCurrentThread() - positiveStart;
                return positive >= positiveBytes && positive > empty;
            }
            catch (Exception)
            {
                positive = 0; empty = 0;
                return false;
            }
        }

        private static bool TryValidateProfilerRecorder(int positiveBytes, out long positive,
            out long empty, out string status)
        {
            positive = 0; empty = 0; status = StatusUnsupported;
            try
            {
                _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc",
                    _sampleCapacity, ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                _recorderValid = _recorder.Valid;
                if (!_recorderValid) return false;

                positive = CaptureRecorderAllocation(positiveBytes, out bool positiveOverflow);
                if (positiveOverflow) { status = StatusOverflow; return false; }
                empty = CaptureRecorderEmpty(out bool emptyOverflow);
                if (emptyOverflow) { status = StatusOverflow; return false; }
                if (positive < positiveBytes || positive <= empty)
                {
                    status = StatusUnreliable;
                    return false;
                }
                status = StatusSupported;
                return true;
            }
            catch (Exception)
            {
                status = StatusUnsupported;
                return false;
            }
        }

        private static bool TryValidateNativeProfiler(int positiveBytes, out long positive,
            out long empty, out string status)
        {
            positive = 0; empty = 0; status = StatusNativeUnsupported;
            try
            {
                _nativeLoaded = true;
                if (Native_Initialize() == 0 || Native_IsSupported() == 0) return false;
                empty = CaptureNativeEmpty();
                positive = CaptureNativeAllocation(positiveBytes);
                if (positive < positiveBytes || positive <= empty)
                {
                    status = StatusUnreliable;
                    Native_Shutdown();
                    return false;
                }
                status = StatusSupported;
                return true;
            }
            catch (DllNotFoundException)
            {
                _nativeLoaded = false;
                status = StatusNativeUnsupported;
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                status = StatusNativeUnsupported;
                return false;
            }
            catch (BadImageFormatException)
            {
                status = StatusNativeUnsupported;
                return false;
            }
            catch (Exception)
            {
                status = StatusNativeUnsupported;
                return false;
            }
        }

        private static long CaptureNativeAllocation(int bytes)
        {
            Native_Begin();
            byte[] allocation = new byte[bytes];
            allocation[0] = 1;
            GC.KeepAlive(allocation);
            ulong value = Native_End();
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }

        private static long CaptureNativeEmpty()
        {
            Native_Begin();
            Noop();
            ulong value = Native_End();
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }

        private static void EnableProfilerForValidation()
        {
            try
            {
                _profilerWasEnabled = Profiler.enabled;
                if (!_profilerWasEnabled)
                {
                    Profiler.enabled = true;
                    _profilerChanged = true;
                }
            }
            catch (Exception)
            {
                _profilerWasEnabled = false;
                _profilerChanged = false;
            }
        }

        private static void RestoreProfilerState()
        {
            if (!_profilerChanged) return;
            try { Profiler.enabled = _profilerWasEnabled; } catch (Exception) { }
            _profilerChanged = false;
            _profilerWasEnabled = false;
        }

        private static void DisposeRecorder()
        {
            if (!_recorderValid) return;
            try { _recorder.Stop(); } catch (Exception) { }
            _recorder.Dispose();
            _recorderValid = false;
        }

        private static long CaptureRecorderAllocation(int bytes, out bool overflow)
        {
            _recorder.Reset();
            _recorder.Start();
            byte[] allocation = new byte[bytes];
            allocation[0] = 1;
            GC.KeepAlive(allocation);
            _recorder.Stop();
            long result = ReadRecorderSamples(out overflow);
            return result;
        }

        private static long CaptureRecorderEmpty(out bool overflow)
        {
            _recorder.Reset();
            _recorder.Start();
            Noop();
            _recorder.Stop();
            return ReadRecorderSamples(out overflow);
        }

        private static long ReadRecorderSamples(out bool overflow)
        {
            int count = _recorder.Count;
            overflow = count >= _sampleCapacity;
            long total = 0;
            if (overflow) return total;
            for (int i = 0; i < count; i++)
            {
                long value = _recorder.GetSample(i).Value;
                if (value > 0) total += value;
            }
            return total;
        }

        private static void Noop() { }

        [DllImport(NativePluginName, EntryPoint = "OpenOitaAllocationProbe_Initialize",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int Native_Initialize();

        [DllImport(NativePluginName, EntryPoint = "OpenOitaAllocationProbe_IsSupported",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int Native_IsSupported();

        [DllImport(NativePluginName, EntryPoint = "OpenOitaAllocationProbe_Begin",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void Native_Begin();

        [DllImport(NativePluginName, EntryPoint = "OpenOitaAllocationProbe_End",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong Native_End();

        [DllImport(NativePluginName, EntryPoint = "OpenOitaAllocationProbe_Shutdown",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void Native_Shutdown();
    }
}
