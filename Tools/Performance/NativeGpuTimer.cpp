#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <mutex>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IUnityInterface.h"

namespace
{
    constexpr uint32_t kSlotCount = 256;
    constexpr int kFallbackBeginEventBase = 0x6F500000;
    constexpr int kFallbackEndEventBase = 0x6F510000;
    constexpr int kFallbackPollEvent = 0x6F4F0000;
    constexpr size_t kCompletedCapacity = 4096;
    constexpr uint32_t kFlagDisjoint = 1u;
    constexpr uint32_t kFlagInvalid = 2u;

    struct CompletedSample
    {
        uint64_t scopeId;
        double gpuMilliseconds;
        double renderThreadEventMilliseconds;
        uint32_t flags;
        uint32_t reserved;
    };

    struct Slot
    {
        ID3D11Query* disjoint = nullptr;
        ID3D11Query* begin = nullptr;
        ID3D11Query* end = nullptr;
        std::atomic<int> state{0};
        std::atomic<uint64_t> requestedScope{0};
        uint64_t activeScope = 0;
        uint64_t renderThreadBeginQpc = 0;
        uint64_t renderThreadEndQpc = 0;
    };

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    ID3D11Device* g_device = nullptr;
    ID3D11DeviceContext* g_context = nullptr;
    std::array<Slot, kSlotCount> g_slots;
    std::mutex g_completedMutex;
    std::array<CompletedSample, kCompletedCapacity> g_completed{};
    size_t g_completedRead = 0;
    size_t g_completedWrite = 0;
    size_t g_completedCount = 0;
    std::atomic<int> g_dropCount{0};
    std::atomic<bool> g_supported{false};
    int g_beginEventBase = kFallbackBeginEventBase;
    int g_endEventBase = kFallbackEndEventBase;
    int g_pollEvent = kFallbackPollEvent;
    uint64_t g_qpcFrequency = 0;

    void ReleaseQuery(ID3D11Query*& query)
    {
        if (query != nullptr)
        {
            query->Release();
            query = nullptr;
        }
    }

    void ResetSlots()
    {
        for (Slot& slot : g_slots)
        {
            slot.state.store(0, std::memory_order_release);
            slot.requestedScope.store(0, std::memory_order_release);
            slot.activeScope = 0;
            slot.renderThreadBeginQpc = 0;
            slot.renderThreadEndQpc = 0;
            ReleaseQuery(slot.disjoint);
            ReleaseQuery(slot.begin);
            ReleaseQuery(slot.end);
        }
    }

    void ReleaseDevice()
    {
        g_supported.store(false, std::memory_order_release);
        ResetSlots();
        if (g_context != nullptr)
        {
            g_context->Release();
            g_context = nullptr;
        }
        if (g_device != nullptr)
        {
            g_device->Release();
            g_device = nullptr;
        }
    }

    bool CreateQueries()
    {
        if (g_device == nullptr || g_context == nullptr) return false;

        D3D11_QUERY_DESC timestampDesc{};
        timestampDesc.Query = D3D11_QUERY_TIMESTAMP;
        D3D11_QUERY_DESC disjointDesc{};
        disjointDesc.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
        for (Slot& slot : g_slots)
        {
            if (FAILED(g_device->CreateQuery(&timestampDesc, &slot.begin)) ||
                FAILED(g_device->CreateQuery(&timestampDesc, &slot.end)) ||
                FAILED(g_device->CreateQuery(&disjointDesc, &slot.disjoint)))
            {
                ReleaseQuery(slot.disjoint);
                ReleaseQuery(slot.begin);
                ReleaseQuery(slot.end);
                ResetSlots();
                return false;
            }
        }
        return true;
    }

    void PushCompleted(const CompletedSample& sample)
    {
        std::lock_guard<std::mutex> lock(g_completedMutex);
        // Keep polling non-blocking and bounded. A full queue is an observable drop,
        // never a reason to wait for the GPU on the main thread.
        if (g_completedCount >= kCompletedCapacity)
        {
            g_dropCount.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        g_completed[g_completedWrite] = sample;
        g_completedWrite = (g_completedWrite + 1) % kCompletedCapacity;
        ++g_completedCount;
    }

    void PollOnRenderThread()
    {
        if (!g_supported.load(std::memory_order_acquire) || g_context == nullptr) return;
        for (Slot& slot : g_slots)
        {
            if (slot.state.load(std::memory_order_acquire) != 3) continue;

            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
            HRESULT disjointResult = g_context->GetData(
                slot.disjoint, &disjoint, sizeof(disjoint), D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (disjointResult != S_OK) continue;

            UINT64 beginTimestamp = 0;
            UINT64 endTimestamp = 0;
            HRESULT beginResult = g_context->GetData(
                slot.begin, &beginTimestamp, sizeof(beginTimestamp), D3D11_ASYNC_GETDATA_DONOTFLUSH);
            HRESULT endResult = g_context->GetData(
                slot.end, &endTimestamp, sizeof(endTimestamp), D3D11_ASYNC_GETDATA_DONOTFLUSH);

            // A completed disjoint query does not imply both timestamp queries
            // are readable yet. Keep the slot pending and retry next frame; only
            // a completed query with invalid data is reported as an invalid sample.
            if (beginResult != S_OK || endResult != S_OK)
                continue;

            uint64_t scopeId = slot.activeScope;
            double renderThreadEventMilliseconds = 0.0;
            if (g_qpcFrequency != 0 && slot.renderThreadEndQpc >= slot.renderThreadBeginQpc)
            {
                renderThreadEventMilliseconds = static_cast<double>(slot.renderThreadEndQpc - slot.renderThreadBeginQpc) * 1000.0 /
                    static_cast<double>(g_qpcFrequency);
            }
            if (disjoint.Disjoint || disjoint.Frequency == 0 ||
                endTimestamp < beginTimestamp)
            {
                PushCompleted({scopeId, 0.0, renderThreadEventMilliseconds,
                    disjoint.Disjoint ? kFlagDisjoint : kFlagInvalid, 0});
            }
            else
            {
                double milliseconds = static_cast<double>(endTimestamp - beginTimestamp) * 1000.0 /
                    static_cast<double>(disjoint.Frequency);
                PushCompleted({scopeId, milliseconds, renderThreadEventMilliseconds, 0, 0});
            }

            slot.activeScope = 0;
            slot.renderThreadBeginQpc = 0;
            slot.renderThreadEndQpc = 0;
            slot.requestedScope.store(0, std::memory_order_release);
            slot.state.store(0, std::memory_order_release);
        }
    }

    void BeginOnRenderThread(uint32_t slotIndex)
    {
        if (!g_supported.load(std::memory_order_acquire) || g_context == nullptr || slotIndex >= kSlotCount)
            return;
        Slot& slot = g_slots[slotIndex];
        int expected = 1;
        if (!slot.state.compare_exchange_strong(expected, 2, std::memory_order_acq_rel))
        {
            g_dropCount.fetch_add(1, std::memory_order_relaxed);
            return;
        }

        slot.activeScope = slot.requestedScope.load(std::memory_order_acquire);
        LARGE_INTEGER qpc{};
        QueryPerformanceCounter(&qpc);
        slot.renderThreadBeginQpc = static_cast<uint64_t>(qpc.QuadPart);
        g_context->Begin(slot.disjoint);
        g_context->End(slot.begin);
    }

    void EndOnRenderThread(uint32_t slotIndex)
    {
        if (!g_supported.load(std::memory_order_acquire) || g_context == nullptr || slotIndex >= kSlotCount)
            return;
        Slot& slot = g_slots[slotIndex];
        int expected = 2;
        if (!slot.state.compare_exchange_strong(expected, 3, std::memory_order_acq_rel))
        {
            // A begin event can be dropped if a device reset occurred. Free the
            // reservation so the next camera can continue without growing state.
            slot.requestedScope.store(0, std::memory_order_release);
            slot.state.store(0, std::memory_order_release);
            g_dropCount.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        g_context->End(slot.end);
        g_context->End(slot.disjoint);
        // The camera-tail event is outside Unity's camera Submit. Without submitting
        // this closing query, D3D11 can retain it until the next engine frame, causing
        // the GPU timestamp interval to include the next simulation Tick's CPU idle
        // gap. Flush submits the closed range asynchronously; it does not wait for
        // GPU completion. Polling remains DONOTFLUSH. Benchmark instrumentation only.
        g_context->Flush();
        LARGE_INTEGER qpc{};
        QueryPerformanceCounter(&qpc);
        slot.renderThreadEndQpc = static_cast<uint64_t>(qpc.QuadPart);
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        if (eventType == kUnityGfxDeviceEventShutdown || eventType == kUnityGfxDeviceEventBeforeReset)
        {
            ReleaseDevice();
            return;
        }
        if (eventType != kUnityGfxDeviceEventInitialize && eventType != kUnityGfxDeviceEventAfterReset)
            return;
        if (g_unityGraphics == nullptr || g_unityGraphics->GetRenderer() != kUnityGfxRendererD3D11)
            return;
        IUnityGraphicsD3D11* d3d11 = g_unityInterfaces == nullptr ? nullptr : g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (d3d11 == nullptr || d3d11->GetDevice == nullptr) return;
        ID3D11Device* device = d3d11->GetDevice();
        if (device == nullptr) return;
        ReleaseDevice();
        g_device = device;
        g_device->AddRef();
        g_device->GetImmediateContext(&g_context);
        if (CreateQueries()) g_supported.store(true, std::memory_order_release);
        else ReleaseDevice();
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        if (eventId == g_pollEvent)
        {
            PollOnRenderThread();
            return;
        }
        if (eventId >= g_beginEventBase && eventId < g_beginEventBase + static_cast<int>(kSlotCount))
        {
            BeginOnRenderThread(static_cast<uint32_t>(eventId - g_beginEventBase));
            return;
        }
        if (eventId >= g_endEventBase && eventId < g_endEventBase + static_cast<int>(kSlotCount))
            EndOnRenderThread(static_cast<uint32_t>(eventId - g_endEventBase));
    }
}

extern "C"
{
    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
    {
        g_unityInterfaces = unityInterfaces;
        LARGE_INTEGER qpcFrequency{};
        if (QueryPerformanceFrequency(&qpcFrequency) && qpcFrequency.QuadPart > 0)
            g_qpcFrequency = static_cast<uint64_t>(qpcFrequency.QuadPart);
        g_unityGraphics = unityInterfaces == nullptr ? nullptr : unityInterfaces->Get<IUnityGraphics>();
        if (g_unityGraphics != nullptr)
        {
            int reservedBase = -1;
            if (g_unityGraphics->ReserveEventIDRange != nullptr)
                reservedBase = g_unityGraphics->ReserveEventIDRange(
                    static_cast<int>(kSlotCount * 2 + 1));
            if (reservedBase > 0 && reservedBase <= (std::numeric_limits<int>::max)() -
                static_cast<int>(kSlotCount * 2 + 1))
            {
                g_pollEvent = reservedBase;
                g_beginEventBase = reservedBase + 1;
                g_endEventBase = g_beginEventBase + static_cast<int>(kSlotCount);
            }
            g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
            OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
        }
    }

    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
    {
        if (g_unityGraphics != nullptr)
            g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
        ReleaseDevice();
        g_unityGraphics = nullptr;
        g_unityInterfaces = nullptr;
        std::lock_guard<std::mutex> lock(g_completedMutex);
        g_completedRead = 0;
        g_completedWrite = 0;
        g_completedCount = 0;
    }

    UNITY_INTERFACE_EXPORT UnityRenderingEvent UNITY_INTERFACE_API OpenOitaGpuTimer_GetRenderEventFunc()
    {
        return OnRenderEvent;
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_IsSupported()
    {
        return g_supported.load(std::memory_order_acquire) ? 1 : 0;
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_ReserveScope(uint64_t scopeId)
    {
        if (!g_supported.load(std::memory_order_acquire) || scopeId == 0)
        {
            g_dropCount.fetch_add(1, std::memory_order_relaxed);
            return -1;
        }
        for (uint32_t index = 0; index < kSlotCount; ++index)
        {
            int expected = 0;
            if (g_slots[index].state.compare_exchange_strong(expected, 1, std::memory_order_acq_rel))
            {
                g_slots[index].requestedScope.store(scopeId, std::memory_order_release);
                return static_cast<int>(index);
            }
        }
        g_dropCount.fetch_add(1, std::memory_order_relaxed);
        return -1;
    }

    UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API OpenOitaGpuTimer_ReleaseScope(uint32_t slotIndex)
    {
        if (slotIndex >= kSlotCount) return;
        Slot& slot = g_slots[slotIndex];
        slot.requestedScope.store(0, std::memory_order_release);
        int state = slot.state.load(std::memory_order_acquire);
        if (state == 1) slot.state.store(0, std::memory_order_release);
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_ReadCompleted(
        CompletedSample* destination, int capacity)
    {
        if (destination == nullptr || capacity <= 0) return 0;
        std::lock_guard<std::mutex> lock(g_completedMutex);
        int count = 0;
        while (count < capacity && g_completedCount > 0)
        {
            destination[count++] = g_completed[g_completedRead];
            g_completedRead = (g_completedRead + 1) % kCompletedCapacity;
            --g_completedCount;
        }
        return count;
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_GetDropCount()
    {
        return g_dropCount.load(std::memory_order_relaxed);
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_GetPollEventId()
    {
        return g_pollEvent;
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_GetBeginEventId(uint32_t slotIndex)
    {
        return slotIndex < kSlotCount ? g_beginEventBase + static_cast<int>(slotIndex) : -1;
    }

    UNITY_INTERFACE_EXPORT int UNITY_INTERFACE_API OpenOitaGpuTimer_GetEndEventId(uint32_t slotIndex)
    {
        return slotIndex < kSlotCount ? g_endEventBase + static_cast<int>(slotIndex) : -1;
    }
}
