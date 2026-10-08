#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <atomic>
#include <cstdint>

// These are the exact public Mono profiler declarations shipped with the Unity
// editor used to build this helper.  The DLL is still loaded dynamically: no
// import library or static link to a particular Unity Mono build is required.
#include <mono/metadata/profiler.h>
#include <mono/metadata/object.h>

namespace
{
    using MonoProfilerCreate = MonoProfilerHandle (*)(MonoProfiler*);
    using MonoProfilerSetGcAllocation = void (*)(MonoProfilerHandle, MonoProfilerGCAllocationCallback);
    using MonoProfilerEnableAllocations = mono_bool (*)();
    using MonoObjectGetSize = unsigned int (*)(MonoObject*);

    HMODULE g_mono = nullptr;
    MonoProfilerHandle g_handle = nullptr;
    MonoProfilerSetGcAllocation g_setAllocation = nullptr;
    MonoObjectGetSize g_objectSize = nullptr;
    std::atomic<bool> g_supported{false};
    std::atomic<bool> g_allocationsEnabled{false};
    std::atomic<bool> g_scopeEnabled{false};
    std::atomic<DWORD> g_scopeThread{0};
    std::atomic<uint64_t> g_scopeBytes{0};

    template <typename T>
    T FindExport(const char* name)
    {
        return g_mono == nullptr ? nullptr : reinterpret_cast<T>(GetProcAddress(g_mono, name));
    }

    void OnAllocation(MonoProfiler*, MonoObject* object)
    {
        if (!g_scopeEnabled.load(std::memory_order_relaxed) ||
            g_scopeThread.load(std::memory_order_relaxed) != GetCurrentThreadId() ||
            object == nullptr || g_objectSize == nullptr)
            return;
        unsigned int size = g_objectSize(object);
        g_scopeBytes.fetch_add(static_cast<uint64_t>(size), std::memory_order_relaxed);
    }

    HMODULE FindMonoModule()
    {
        HMODULE module = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
        if (module == nullptr) module = GetModuleHandleW(L"mono-2.0-sgen.dll");
        return module;
    }
}

extern "C"
{
    __declspec(dllexport) int __cdecl OpenOitaAllocationProbe_Initialize()
    {
        if (g_supported.load(std::memory_order_acquire)) return 1;
        if (g_mono == nullptr) g_mono = FindMonoModule();
        if (g_mono == nullptr) return 0;

        MonoProfilerCreate create = FindExport<MonoProfilerCreate>("mono_profiler_create");
        g_setAllocation = FindExport<MonoProfilerSetGcAllocation>("mono_profiler_set_gc_allocation_callback");
        MonoProfilerEnableAllocations enable = FindExport<MonoProfilerEnableAllocations>("mono_profiler_enable_allocations");
        g_objectSize = FindExport<MonoObjectGetSize>("mono_object_get_size");
        if (create == nullptr || g_setAllocation == nullptr || enable == nullptr || g_objectSize == nullptr)
            return 0;

        // The handle is retained for the process lifetime. Mono exposes no public
        // destroy operation; Shutdown only removes our callback and disables scope
        // accounting, restoring the pre-probe callback state.
        if (g_handle == nullptr) g_handle = create(nullptr);
        if (g_handle == nullptr) return 0;
        if (!g_allocationsEnabled.exchange(true, std::memory_order_acq_rel))
        {
            // Mono can reject late enablement even when Unity already enabled allocation
            // instrumentation at startup. A zero return therefore does not prove that the
            // callback cannot receive events. Install our callback in either case; the
            // managed positive control decides whether events actually reach it before
            // trusting bytes.
            (void)enable();
        }

        g_setAllocation(g_handle, OnAllocation);
        g_scopeBytes.store(0, std::memory_order_release);
        g_scopeThread.store(0, std::memory_order_release);
        g_scopeEnabled.store(false, std::memory_order_release);
        g_supported.store(true, std::memory_order_release);
        return 1;
    }

    __declspec(dllexport) int __cdecl OpenOitaAllocationProbe_IsSupported()
    {
        return g_supported.load(std::memory_order_acquire) ? 1 : 0;
    }

    __declspec(dllexport) void __cdecl OpenOitaAllocationProbe_Begin()
    {
        if (!g_supported.load(std::memory_order_acquire)) return;
        g_scopeBytes.store(0, std::memory_order_release);
        g_scopeThread.store(GetCurrentThreadId(), std::memory_order_release);
        g_scopeEnabled.store(true, std::memory_order_release);
    }

    __declspec(dllexport) uint64_t __cdecl OpenOitaAllocationProbe_End()
    {
        g_scopeEnabled.store(false, std::memory_order_release);
        g_scopeThread.store(0, std::memory_order_release);
        return g_scopeBytes.load(std::memory_order_acquire);
    }

    __declspec(dllexport) uint64_t __cdecl OpenOitaAllocationProbe_GetBytes()
    {
        return g_scopeBytes.load(std::memory_order_acquire);
    }

    __declspec(dllexport) void __cdecl OpenOitaAllocationProbe_Shutdown()
    {
        g_scopeEnabled.store(false, std::memory_order_release);
        g_scopeThread.store(0, std::memory_order_release);
        if (g_setAllocation != nullptr && g_handle != nullptr)
            g_setAllocation(g_handle, nullptr);
        g_supported.store(false, std::memory_order_release);
    }
}
