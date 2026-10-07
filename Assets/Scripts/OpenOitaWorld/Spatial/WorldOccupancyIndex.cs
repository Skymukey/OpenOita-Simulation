using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;
using OpenOita.Simulation;

namespace OpenOita.Spatial
{
    public sealed class WorldOccupancyIndex : IContactQuery, IOccupancyView, IDisposable
    {
        private readonly SortedDictionary<CellPositionKey, Entry> _entries = new();
        private readonly Dictionary<Vector2Int, Bin> _bins = new();
        private readonly Stack<Bin> _binPool = new();
        private readonly Dictionary<ulong, BodySnapshot> _bodies = new();
        private readonly HashSet<CellPositionKey> _candidateSet = new();
        private readonly List<CellPositionKey> _candidateKeys = new();
        private readonly List<CellContact> _contacts = new();
        private IWorkingWorldView _cachedWorld;
        private IMaterialRuntimeTable _cachedMaterials;
        private ITickInstanceMap _cachedInstances;
        private bool _hasCandidatePoses;
        private long _cachedGeometryRevision;
        internal int RebuildCount { get; private set; }
        private Func<bool> _valid;
        private float _size;
        private Vector2 _origin;
        private bool _disposed;
        private int _reservedEntries;
        private int _reservedBins;
        private int _reservedCandidates;
        private int _reservedBodies;
        private long _binSlotCapacity;
        public SpatialLease Lease { get; private set; }
        public WorldVersion Version { get; private set; }
        public long ReservedCpuBytes => 4096L + _reservedEntries * 1024L + _reservedBins * 384L +
            _reservedCandidates * 64L + _candidateKeys.Capacity * 32L + _contacts.Capacity * 256L +
            _reservedBodies * 128L + _reservedBins * 16L + _binSlotCapacity * 32L;
        internal static long EstimateCpuBytes(int cells) => 4096L + cells * 8192L;

        [Flags]
        internal enum QueryCategory : byte
        {
            GridSolid = 1,
            BodySolid = 2,
            Other = 4,
            Solid = GridSolid | BodySolid,
            All = Solid | Other
        }

        // 每个候选在桶内只存一次；按查询能力分组，不绑定具体材料 ID。
        private sealed class Bin
        {
            internal readonly List<CellPositionKey> GridSolids = new();
            internal readonly List<CellPositionKey> BodySolids = new();
            internal readonly List<CellPositionKey> Others = new();
            internal List<CellPositionKey> Items(QueryCategory category) => category == QueryCategory.GridSolid ? GridSolids :
                category == QueryCategory.BodySolid ? BodySolids : Others;
            internal void Clear() { GridSolids.Clear(); BodySolids.Clear(); Others.Clear(); }
        }
        internal readonly struct Entry
        {
            internal readonly CellGeometry Geometry;
            internal readonly bool Solid;
            internal Entry(CellGeometry geometry, bool solid)
            { Geometry = geometry; Solid = solid; }
        }
        internal IEnumerable<Entry> Entries => _entries.Values;

        internal bool CanRebind(IWorkingWorldView world, IMaterialRuntimeTable materials, ITickInstanceMap instances,
            in TransactionContext context, long revision, ReadOnlySpan<BodySnapshot> poses = default)
        {
            if (_disposed || _valid == null || !ReferenceEquals(world, _cachedWorld) ||
                !ReferenceEquals(materials, _cachedMaterials) || Lease.Generation != context.PublishedVersion.Generation) return false;
            // 正式世界的几何修订不包含状态/实例；每次仍绑定新的工作修订租约。
            if (world is WorkingWorld state)
                return ReferenceEquals(instances, state.Instances) && state.IsLeaseValid(revision, context.WorkingTick) && state.GeometryRevision == _cachedGeometryRevision &&
                    SamePoses(poses.IsEmpty ? world.Bodies : poses);
            // 外部视图没有受控修订承诺，保留旧的保守路径。
            return poses.IsEmpty && !_hasCandidatePoses && ReferenceEquals(instances, _cachedInstances) &&
                Lease.WorkingTick == context.WorkingTick && Lease.Revision == revision && _valid();
        }

        private bool SamePoses(ReadOnlySpan<BodySnapshot> poses)
        {
            if (poses.Length != _bodies.Count) return false;
            ulong previous = 0;
            foreach (BodySnapshot body in poses)
            {
                if (body.BodyId <= previous || !_bodies.TryGetValue(body.BodyId, out BodySnapshot cached) ||
                    body.GeometryVersion != cached.GeometryVersion || !body.Pose.Position.Equals(cached.Pose.Position) ||
                    !body.Pose.AngleRadians.Equals(cached.Pose.AngleRadians)) return false;
                previous = body.BodyId;
            }
            return true;
        }

        public WorldResult Refresh(IWorkingWorldView world, IMaterialRuntimeTable materials, ITickInstanceMap instances,
            in TransactionContext context, long revision, Func<bool> valid, ReadOnlySpan<BodySnapshot> poses = default)
        {
            if (_disposed || world == null || materials == null || instances == null || valid == null)
            {
                Invalidate();
                return Error(WorldErrorCode.NotReady, "占据提供者或输入不可用。");
            }
            if (world.Generation != context.PublishedVersion.Generation || world.WorkingTick != context.WorkingTick)
            {
                Invalidate();
                return Error(WorldErrorCode.StaleGeneration, "占据阶段与工作状态不一致。");
            }
            // 修订未变时仅重新绑定阶段租约。候选位姿必须独立重建，不能复用旧物理结果。
            if (CanRebind(world, materials, instances, context, revision, poses))
            {
                Version = context.PublishedVersion;
                Lease = new SpatialLease(world.Generation, world.WorkingTick, context.Stage, revision);
                _valid = valid;
                _cachedInstances = instances;
                _hasCandidatePoses = !poses.IsEmpty;
                return WorldResult.Success();
            }
            Invalidate();
            using var timing = WorldStepMetrics.Measure(WorldStepMetrics.Timing.SpatialRebuild);
            try
            {
                _size = world.Config.CellSize; _origin = world.Origin;
                foreach (BodySnapshot body in poses.IsEmpty ? world.Bodies : poses) _bodies.Add(body.BodyId, body);
                foreach (CellKey key in world.OccupiedCells)
                {
                    WorldResult read = world.Read(key, out CellSnapshot state);
                    if (!read.IsSuccess) return read;
                    if (!materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material) || !instances.TryGetInstance(key, out CellInstanceHandle instance))
                        return Error(WorldErrorCode.InvalidArgument, "占据缺少材料或当前实例。");
                    BodyPose pose = key.Position.OwnerKind == OwnerKind.Grid ? new BodyPose(world.Origin, 0) : _bodies[key.Position.BodyId].Pose;
                    var geometry = new CellGeometry(key, pose, new Vector2Int(key.Position.X, key.Position.Y), _size);
                    _entries.Add(key.Position, new Entry(geometry, material.Kind == MaterialKind.Solid));
                    QueryCategory category = material.Kind != MaterialKind.Solid ? QueryCategory.Other :
                        key.Position.OwnerKind == OwnerKind.Grid ? QueryCategory.GridSolid : QueryCategory.BodySolid;
                    Bounds(geometry, 1e-4f * _size, out Vector2Int min, out Vector2Int max);
                    for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                    {
                        var bin = new Vector2Int(x, y);
                        if (!_bins.TryGetValue(bin, out Bin bucket))
                            _bins.Add(bin, bucket = _binPool.Count == 0 ? new Bin() : _binPool.Pop());
                        List<CellPositionKey> items = bucket.Items(category);
                        int oldCapacity = items.Capacity;
                        items.Add(key.Position);
                        _binSlotCapacity += items.Capacity - oldCapacity;
                    }
                }
                Version = context.PublishedVersion;
                Lease = new SpatialLease(world.Generation, world.WorkingTick, context.Stage, revision);
                _valid = valid;
                _cachedWorld = world; _cachedMaterials = materials; _cachedInstances = instances;
                _cachedGeometryRevision = world is WorkingWorld working ? working.GeometryRevision : revision;
                _hasCandidatePoses = !poses.IsEmpty;
                RebuildCount++;
                WorldStepMetrics.Add(WorldStepMetrics.Work.SpatialRebuilds);
                WorldStepMetrics.Add(WorldStepMetrics.Work.SpatialEntries, _entries.Count);
                return WorldResult.Success();
            }
            catch (Exception exception) { return Error(WorldErrorCode.InvalidArgument, exception.Message); }
            finally
            {
                _reservedEntries = Math.Max(_reservedEntries, _entries.Count);
                _reservedBins = Math.Max(_reservedBins, _bins.Count);
                _reservedBodies = Math.Max(_reservedBodies, _bodies.Count);
                if (_valid == null) Invalidate();
            }
        }

        private void Bounds(in CellGeometry geometry, float extra, out Vector2Int min, out Vector2Int max)
        {
            Vector2 low = ExactCellGeometry.Corner(geometry, 0), high = low;
            for (int i = 1; i < 4; i++) { Vector2 v = ExactCellGeometry.Corner(geometry, i); low = Vector2.Min(low, v); high = Vector2.Max(high, v); }
            min = new Vector2Int(Mathf.FloorToInt((low.x - extra - _origin.x) / _size), Mathf.FloorToInt((low.y - extra - _origin.y) / _size));
            max = new Vector2Int(Mathf.FloorToInt((high.x + extra - _origin.x) / _size), Mathf.FloorToInt((high.y + extra - _origin.y) / _size));
        }

        // 只在同步查询期间借用；下一次候选查询会覆盖。调用者不得跨查询持有。
        internal List<CellPositionKey> Candidates(in CellGeometry geometry, QueryCategory categories = QueryCategory.All)
        {
            _candidateSet.Clear(); _candidateKeys.Clear();
            Bounds(geometry, 1e-4f * _size, out Vector2Int min, out Vector2Int max);
            for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                if (_bins.TryGetValue(new Vector2Int(x, y), out Bin bin))
                {
                    if ((categories & QueryCategory.GridSolid) != 0) AddCandidates(bin.GridSolids);
                    if ((categories & QueryCategory.BodySolid) != 0) AddCandidates(bin.BodySolids);
                    if ((categories & QueryCategory.Other) != 0) AddCandidates(bin.Others);
                }
            _candidateKeys.Sort();
            _reservedCandidates = Math.Max(_reservedCandidates, _candidateKeys.Count);
            return _candidateKeys;
        }

        private void AddCandidates(List<CellPositionKey> items)
        {
            foreach (CellPositionKey key in items) if (_candidateSet.Add(key)) _candidateKeys.Add(key);
        }

        internal bool TryGet(CellPositionKey key, out Entry entry) => _entries.TryGetValue(key, out entry);
        public WorldResult HasSolidOverlap(in CellGeometry cell, out bool overlaps)
            => HasOverlap(cell, false, out overlaps);

        // 同一主网格格的正面积静态交叠等价于该格材料为固体。
        // 仅此离散查询分路；旋转体、任意连续几何和接触仍用原精确判定。
        internal WorldResult HasGridSolidOverlap(in CellKey key, out bool overlaps)
        {
            WorldStepMetrics.Add(WorldStepMetrics.Work.SolidOverlapCalls);
            overlaps = false;
            WorldResult check = Check(key.Generation);
            if (!check.IsSuccess) return check;
            if (key.Position.OwnerKind != OwnerKind.Grid || key.Position.BodyId != 0)
                return Error(WorldErrorCode.InvalidArgument, "离散固体查询必须使用主网格键。");
            WorldStepMetrics.Add(WorldStepMetrics.Work.GridReads);
            WorldResult read = _cachedWorld.Read(key, out CellSnapshot state);
            if (!read.IsSuccess) return read;
            if (state.MaterialId != 0)
            {
                if (!_cachedMaterials.TryGet(state.MaterialId, out MaterialRuntimeEntry material))
                    return Error(WorldErrorCode.UnknownMaterial, "网格材料不存在。");
                if (material.Kind == MaterialKind.Solid) { overlaps = true; return WorldResult.Success(); }
            }
            // 无动态目录时省去动态宽阶段；不依赖材料 ID 或全世界固体数量。
            if (_bodies.Count == 0) return WorldResult.Success();
            WorldStepMetrics.Add(WorldStepMetrics.Work.DynamicQueries);
            var cell = new CellGeometry(key, new BodyPose(_origin, 0), new Vector2Int(key.Position.X, key.Position.Y), _size);
            Bounds(cell, 0, out Vector2Int min, out Vector2Int max);
            for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                if (_bins.TryGetValue(new Vector2Int(x, y), out Bin bin) && AnyOverlap(cell, bin.BodySolids))
                { overlaps = true; return WorldResult.Success(); }
            return WorldResult.Success();
        }

        internal WorldResult HasStaticSolidOverlap(in CellGeometry cell, out bool overlaps)
            => HasOverlap(cell, true, out overlaps);

        private WorldResult HasOverlap(in CellGeometry cell, bool staticOnly, out bool overlaps)
        {
            WorldStepMetrics.Add(WorldStepMetrics.Work.SolidOverlapCalls);
            overlaps = false;
            WorldResult check = Check(cell.Key.Generation);
            if (!check.IsSuccess) return check;
            // 存在性查询无需生成完整候选集或排序；命中立即结束。
            // 跨桶重复候选允许重复精确判定，避免为每次布尔查询清理/填充哈希集合。
            Bounds(cell, 0, out Vector2Int min, out Vector2Int max);
            for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
            {
                if (!_bins.TryGetValue(new Vector2Int(x, y), out Bin bin)) continue;
                if (AnyOverlap(cell, bin.GridSolids) || (!staticOnly && AnyOverlap(cell, bin.BodySolids)))
                {
                    overlaps = true;
                    return WorldResult.Success();
                }
            }
            return WorldResult.Success();
        }

        private bool AnyOverlap(in CellGeometry cell, List<CellPositionKey> items)
        {
            foreach (CellPositionKey key in items)
            {
                WorldStepMetrics.Add(WorldStepMetrics.Work.ExactSolidTests);
                if (ExactCellGeometry.PositiveOverlap(cell, _entries[key].Geometry)) return true;
            }
            return false;
        }

        public WorldResult Classify(in CellGeometry first, in CellGeometry second, float epsilon, out CellContact contact)
        {
            contact = default;
            if (!ContractDefaults.IsFinite(epsilon) || epsilon < 0 || first.CellSize <= 0 || second.CellSize <= 0)
                return Error(WorldErrorCode.InvalidArgument, "几何参数无效。");
            contact = ExactCellGeometry.Contact(first, second, epsilon);
            return WorldResult.Success();
        }

        public QueryResult QueryContacts(in CellKey cell, Span<CellContact> destination)
        {
            WorldResult check = Check(cell.Generation);
            if (!check.IsSuccess) return new QueryResult(check, Version, 0, 0);
            if (!_entries.TryGetValue(cell.Position, out Entry source)) return new QueryResult(WorldResult.Success(), Version, 0, 0);
            _contacts.Clear();
            foreach (CellPositionKey key in Candidates(source.Geometry))
            {
                if (key.OwnerKind == cell.Position.OwnerKind && key.BodyId == cell.Position.BodyId) continue;
                CellContact contact = ExactCellGeometry.Contact(source.Geometry, _entries[key].Geometry, 1e-4f * _size);
                if (contact.Feature != ContactFeature.Separated && contact.Feature != ContactFeature.VertexVertex) _contacts.Add(contact);
            }
            if (destination.Length < _contacts.Count) return new QueryResult(Error(WorldErrorCode.BufferTooSmall, "完整接触缓冲不足。"), Version, _contacts.Count, 0);
            for (int i = 0; i < _contacts.Count; i++) destination[i] = _contacts[i];
            return new QueryResult(WorldResult.Success(), Version, _contacts.Count, _contacts.Count);
        }

        public WorldResult Check(ulong generation)
        {
            if (_disposed) return Error(WorldErrorCode.Disposed, "占据索引已释放。");
            if (_valid == null || !_valid()) return Error(WorldErrorCode.NotReady, "占据租约已失效，必须重新绑定当前阶段。");
            return generation == Lease.Generation ? WorldResult.Success() : Error(WorldErrorCode.StaleGeneration, "占据代次不一致。");
        }
        public void Invalidate()
        {
            _valid = null; _cachedWorld = null; _cachedMaterials = null; _cachedInstances = null;
            _hasCandidatePoses = false; _entries.Clear(); _bodies.Clear();
            foreach (Bin bin in _bins.Values) { bin.Clear(); _binPool.Push(bin); }
            _bins.Clear(); Lease = default;
        }
        public void Dispose()
        {
            Invalidate(); _bins.TrimExcess(); _binPool.Clear(); _binPool.TrimExcess(); _bodies.TrimExcess();
            _candidateSet.Clear(); _candidateSet.TrimExcess(); _candidateKeys.Clear(); _candidateKeys.TrimExcess();
            _contacts.Clear(); _contacts.TrimExcess(); _reservedEntries = 0; _reservedBins = 0;
            _reservedCandidates = 0; _reservedBodies = 0; _binSlotCapacity = 0; _disposed = true;
        }
        private static WorldResult Error(WorldErrorCode code, string message) => WorldResult.Failure(code, new WorldDiagnostic("Spatial", "lease/geometry", message));
    }
}
