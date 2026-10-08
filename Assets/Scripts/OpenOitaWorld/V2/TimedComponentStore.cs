using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace OpenOita.V2
{
    // 组件槽随移动保持，时间轮只在主线程屏障操作；移动任务只改组件坐标。
    public sealed class TimedComponentStore : IDisposable
    {
        public NativeList<CellCold> Records;
        private NativeArray<int> _heads;
        private NativeList<int> _free;
        private ulong _tick;

        public ulong Tick => _tick;
        public NativeArray<int> Heads => _heads;
        public int RecordCapacity => Records.Capacity;

        public TimedComponentStore(int capacity = 1024)
        {
            Records = new NativeList<CellCold>(Math.Max(16, capacity), Allocator.Persistent);
            _free = new NativeList<int>(Math.Max(16, capacity), Allocator.Persistent);
            _heads = new NativeArray<int>(1024, Allocator.Persistent);
            for (int i = 0; i < _heads.Length; i++) _heads[i] = -1;
        }

        public int Create(CellCold value)
        {
            int index;
            if (_free.Length != 0)
            {
                index = _free[_free.Length - 1]; _free.RemoveAt(_free.Length - 1);
                value.Generation = Records[index].Generation + 1;
                Records[index] = value;
            }
            else { index = Records.Length; value.Generation = 1; Records.Add(value); }
            value.Next = value.Previous = value.Bucket = -1; value.Due = ulong.MaxValue;
            Records[index] = value;
            return index + 1;
        }

        public void Reserve(int extra)
        {
            int needed = Records.Length + extra;
            if (Records.Capacity < needed) Records.Capacity = Math.Max(needed, Records.Capacity * 2);
            if (_free.Capacity < needed) _free.Capacity = Records.Capacity;
        }

        public void Delete(int handle)
        {
            if (!IsLive(handle)) return;
            Unschedule(handle);
            CellCold value = Records[handle - 1]; value.MaterialId = 0;
            Records[handle - 1] = value; _free.Add(handle - 1);
        }

        public bool IsLive(int handle) => handle > 0 && handle <= Records.Length && Records[handle - 1].MaterialId != 0;
        public CellCold Read(int handle) => IsLive(handle) ? Records[handle - 1] : default;
        public void Write(int handle, CellCold value)
        {
            if (!IsLive(handle)) throw new InvalidOperationException("计时组件已失效。");
            CellCold links = Records[handle - 1];
            value.Next = links.Next; value.Previous = links.Previous; value.Bucket = links.Bucket;
            value.Due = links.Due; value.Generation = links.Generation;
            Records[handle - 1] = value;
        }

        public void Unschedule(int handle)
        {
            int index = handle - 1; CellCold value = Records[index];
            if (value.Bucket < 0) return;
            if (value.Previous < 0) _heads[value.Bucket] = value.Next;
            else { CellCold previous = Records[value.Previous]; previous.Next = value.Next; Records[value.Previous] = previous; }
            if (value.Next >= 0) { CellCold next = Records[value.Next]; next.Previous = value.Previous; Records[value.Next] = next; }
            value.Next = value.Previous = value.Bucket = -1; Records[index] = value;
        }

        public void Schedule(int handle, ulong due)
        {
            if (!IsLive(handle)) return;
            Unschedule(handle);
            if (due == ulong.MaxValue) return;
            due = Math.Max(due, _tick);
            ulong delta = due - _tick;
            int level = delta < 256 ? 0 : delta < 65536 ? 1 : delta < 16777216 ? 2 : 3;
            int bucket = level * 256 + (int)((due >> (level * 8)) & 255);
            int index = handle - 1; CellCold value = Records[index];
            value.Due = due; value.Bucket = bucket; value.Previous = -1; value.Next = _heads[bucket];
            if (value.Next >= 0) { CellCold next = Records[value.Next]; next.Previous = index; Records[value.Next] = next; }
            Records[index] = value; _heads[bucket] = index;
        }

        private void Cascade(int bucket)
        {
            int node = _heads[bucket];
            while (node >= 0)
            {
                CellCold value = Records[node]; int next = value.Next;
                Unschedule(node + 1); Schedule(node + 1, value.Due); node = next;
            }
        }

        public void Advance(ulong tick, NativeList<int> due)
        {
            if (tick != _tick + 1) throw new InvalidOperationException("时间轮须按固定Tick前进。");
            NativeTimeWheel wheel = GetNativeTimeWheel();
            if (!wheel.TryAdvance(tick, due)) throw new InvalidOperationException("时间轮须按固定Tick前进。");
            _tick = tick;
        }

        /// <summary>
        /// Returns a native-only view for a Burst job. Records must be reserved before
        /// creating the view; the job never changes record capacity or output capacity.
        /// </summary>
        public NativeTimeWheel GetNativeTimeWheel() => new NativeTimeWheel(Records.AsArray(), _heads, _tick);

        /// <summary>Commits the tick after a native advance job has completed.</summary>
        public void CommitNativeTick(ulong tick)
        {
            if (tick != _tick + 1) throw new InvalidOperationException("原生时间轮须按固定Tick提交。");
            _tick = tick;
        }

        public void Dispose()
        {
            if (Records.IsCreated) Records.Dispose();
            if (_heads.IsCreated) _heads.Dispose();
            if (_free.IsCreated) _free.Dispose();
        }
    }

    /// <summary>
    /// Burst-safe 1024-bucket time-wheel view. The owning store retains lifetime and
    /// capacity ownership; this value only aliases its native arrays.
    /// </summary>
    public struct NativeTimeWheel
    {
        public const int BucketCount = 1024;
        public NativeArray<CellCold> Records;
        public NativeArray<int> Heads;
        public ulong CurrentTick;

        public NativeTimeWheel(NativeArray<CellCold> records, NativeArray<int> heads, ulong currentTick)
        {
            Records = records;
            Heads = heads;
            CurrentTick = currentTick;
        }

        public bool IsLive(int handle)
        {
            return handle > 0 && handle <= Records.Length && Records[handle - 1].MaterialId != 0;
        }

        public void Unschedule(int handle)
        {
            if (!IsLive(handle)) return;
            int index = handle - 1;
            CellCold value = Records[index];
            if (value.Bucket < 0) return;
            if (value.Previous < 0) Heads[value.Bucket] = value.Next;
            else
            {
                CellCold previous = Records[value.Previous];
                previous.Next = value.Next;
                Records[value.Previous] = previous;
            }
            if (value.Next >= 0)
            {
                CellCold next = Records[value.Next];
                next.Previous = value.Previous;
                Records[value.Next] = next;
            }
            value.Next = value.Previous = value.Bucket = -1;
            Records[index] = value;
        }

        public void Schedule(int handle, ulong due)
        {
            if (!IsLive(handle)) return;
            Unschedule(handle);
            if (due == ulong.MaxValue) return;
            if (due < CurrentTick) due = CurrentTick;
            ulong delta = due - CurrentTick;
            int level = delta < 256 ? 0 : delta < 65536 ? 1 : delta < 16777216 ? 2 : 3;
            int bucket = level * 256 + (int)((due >> (level * 8)) & 255);
            int index = handle - 1;
            CellCold value = Records[index];
            value.Due = due;
            value.Bucket = bucket;
            value.Previous = -1;
            value.Next = Heads[bucket];
            if (value.Next >= 0)
            {
                CellCold next = Records[value.Next];
                next.Previous = index;
                Records[value.Next] = next;
            }
            Records[index] = value;
            Heads[bucket] = index;
        }

        private void Cascade(int bucket)
        {
            int node = Heads[bucket];
            while (node >= 0)
            {
                CellCold value = Records[node];
                int next = value.Next;
                Unschedule(node + 1);
                Schedule(node + 1, value.Due);
                node = next;
            }
        }

        public bool TryAdvance(ulong tick, NativeList<int> due)
        {
            if (tick != CurrentTick + 1) return false;
            CurrentTick = tick;
            due.Clear();
            if ((tick & 0xffffff) == 0) Cascade(768 + (int)((tick >> 24) & 255));
            if ((tick & 0xffff) == 0) Cascade(512 + (int)((tick >> 16) & 255));
            if ((tick & 255) == 0) Cascade(256 + (int)((tick >> 8) & 255));
            int node = Heads[(int)(tick & 255)];
            while (node >= 0)
            {
                CellCold value = Records[node];
                int next = value.Next;
                Unschedule(node + 1);
                if (value.MaterialId != 0 && value.Due <= tick) due.Add(node + 1);
                else if (value.MaterialId != 0) Schedule(node + 1, value.Due);
                node = next;
            }
            return true;
        }

        public bool TryAdvanceClassified(ulong tick, NativeArray<MaterialDefinition> definitions,
            NativeList<int> gasDue, NativeList<int> burningDue, NativeArray<int> overflow)
        {
            if (tick != CurrentTick + 1) return false;
            CurrentTick = tick;
            gasDue.Clear();
            burningDue.Clear();
            if (overflow.IsCreated && overflow.Length != 0) overflow[0] = 0;
            if ((tick & 0xffffff) == 0) Cascade(768 + (int)((tick >> 24) & 255));
            if ((tick & 0xffff) == 0) Cascade(512 + (int)((tick >> 16) & 255));
            if ((tick & 255) == 0) Cascade(256 + (int)((tick >> 8) & 255));
            int node = Heads[(int)(tick & 255)];
            while (node >= 0)
            {
                CellCold value = Records[node];
                int next = value.Next;
                Unschedule(node + 1);
                if (value.MaterialId != 0 && value.Due <= tick)
                {
                    if (definitions[value.MaterialId].IsGas)
                        AppendNoResize(ref gasDue, node + 1, overflow);
                    else
                        AppendNoResize(ref burningDue, node + 1, overflow);
                }
                else if (value.MaterialId != 0) Schedule(node + 1, value.Due);
                node = next;
            }
            return true;
        }

        private static void AppendNoResize(ref NativeList<int> destination, int value, NativeArray<int> overflow)
        {
            if (destination.Length < destination.Capacity) destination.AddNoResize(value);
            else if (overflow.IsCreated && overflow.Length != 0) overflow[0] = 1;
        }
    }

    /// <summary>Single-thread Burst job for one deterministic time-wheel advance.</summary>
    [BurstCompile]
    public struct NativeTimeWheelAdvanceJob : IJob
    {
        public NativeTimeWheel Wheel;
        [ReadOnly] public NativeArray<MaterialDefinition> Definitions;
        public NativeList<int> GasDue;
        public NativeList<int> BurningDue;
        public NativeArray<int> Overflow;
        public ulong Tick;

        public void Execute()
        {
            Wheel.TryAdvanceClassified(Tick, Definitions, GasDue, BurningDue, Overflow);
        }
    }
}
