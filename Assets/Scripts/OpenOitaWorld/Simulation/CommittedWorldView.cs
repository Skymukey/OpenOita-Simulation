using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Simulation
{
    // 有界复制的只读租约。数组只含已提交数据，绝不别名到 NativeArray 工作存储。
    internal sealed class CommittedWorldView : ICommittedRenderView, IFluidSuspensionView, IDisposable
    {
        private CellKey[] _keys;
        private CellSnapshot[] _cells;
        private BodySnapshot[] _bodies;
        private SuspendedFluidSnapshot[] _suspended;
        private MaterialCountsResult _counts;
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        public WorldVersion Version { get; }
        public WorldConfig Config { get; }
        public Vector2 Origin { get; }
        public IMaterialRuntimeTable Materials { get; }
        public ReadOnlySpan<CellKey> OccupiedCells { get { RequireValid(); return _keys; } }
        public ReadOnlySpan<BodySnapshot> Bodies { get { RequireValid(); return _bodies; } }
        public ReadOnlySpan<SuspendedFluidSnapshot> SuspendedFluids { get { RequireValid(); return _suspended; } }
        internal MaterialCountsResult MaterialCounts { get { RequireValid(); return _counts; } }
        internal long StorageBytes => _keys == null ? 0 : 256L + _keys.Length * 72L + _bodies.Length * 80L + _suspended.Length * 80L + _counts.Counts.Length * 32L;

        internal CommittedWorldView(IWorkingWorldView world, IMaterialRuntimeTable materials, WorldVersion version)
        {
            Version = version;
            Config = world.Config;
            Origin = world.Origin;
            Materials = materials;
            _keys = world.OccupiedCells.ToArray();
            _bodies = world.Bodies.ToArray();
            _cells = new CellSnapshot[_keys.Length];
            _suspended = world is IFluidSuspensionView fluids ? fluids.SuspendedFluids.ToArray() : Array.Empty<SuspendedFluidSnapshot>();
            for (int i = 0; i < _keys.Length; i++)
            {
                WorldResult result = world.Read(_keys[i], out _cells[i]);
                if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic.Message);
            }
            var counts = new SortedDictionary<ushort, int[]>();
            int grid = 0, bodies = 0, water = 0, steam = 0;
            for (int i = 0; i < _keys.Length; i++)
            {
                ushort id = _cells[i].MaterialId;
                if (!counts.TryGetValue(id, out int[] values)) counts.Add(id, values = new int[3]);
                bool body = _keys[i].Position.OwnerKind == OwnerKind.Body;
                values[body ? 1 : 0]++;
                if (body) bodies++; else grid++;
            }
            foreach (SuspendedFluidSnapshot record in _suspended)
            {
                ushort id = record.State.MaterialId;
                if (!counts.TryGetValue(id, out int[] values)) counts.Add(id, values = new int[3]);
                values[2]++;
                if (materials.TryGet(id, out MaterialRuntimeEntry material) && material.Kind == MaterialKind.Gas) steam++;
                else water++;
            }
            var valuesByMaterial = new MaterialCount[counts.Count];
            int countIndex = 0;
            foreach (var pair in counts) valuesByMaterial[countIndex++] = new MaterialCount(pair.Key, pair.Value[0], pair.Value[1], pair.Value[2]);
            _counts = new MaterialCountsResult(WorldResult.Success(), version, valuesByMaterial, grid, bodies, water, steam);
        }

        public WorldResult Read(in CellKey key, out CellSnapshot cell)
        {
            cell = default;
            if (_keys == null) return Error(WorldErrorCode.Disposed, "提交视图已释放。");
            if (Thread.CurrentThread.ManagedThreadId != _threadId) return Error(WorldErrorCode.InvalidArgument, "提交视图只供所属线程读取。");
            if (key.Generation != Version.Generation) return Error(WorldErrorCode.StaleGeneration, "提交视图代次不一致。");
            if (key.Position.OwnerKind == OwnerKind.Suspended)
            {
                foreach (SuspendedFluidSnapshot record in _suspended)
                    if (record.RecordId == key.Position.BodyId) { cell = record.State; break; }
                return WorldResult.Success();
            }
            if (key.Position.OwnerKind == OwnerKind.Body)
            {
                bool exists = false;
                foreach (BodySnapshot body in _bodies) if (body.BodyId == key.Position.BodyId) { exists = true; break; }
                if (!exists) return Error(WorldErrorCode.InvalidArgument, "材料体不属于已提交目录。");
            }
            else if (key.Position.X < 0 || key.Position.Y < 0 || key.Position.X >= Config.Width || key.Position.Y >= Config.Height)
                return Error(WorldErrorCode.OutOfBounds, "坐标超出实际宽高。");
            int low = 0;
            int high = _keys.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                int order = _keys[middle].Position.CompareTo(key.Position);
                if (order == 0) { cell = _cells[middle]; return WorldResult.Success(); }
                if (order < 0) low = middle + 1;
                else high = middle - 1;
            }
            return WorldResult.Success();
        }

        public void Dispose() { _keys = null; _cells = null; _bodies = null; _suspended = null; _counts = default; }
        private void RequireValid()
        {
            if (_keys == null) throw new ObjectDisposedException(nameof(CommittedWorldView));
            if (Thread.CurrentThread.ManagedThreadId != _threadId) throw new InvalidOperationException("提交视图只供所属线程读取。");
        }
        private static WorldResult Error(WorldErrorCode code, string message) => WorldResult.Failure(code,
            new WorldDiagnostic("CommittedRead", "cell", message));
    }
}
