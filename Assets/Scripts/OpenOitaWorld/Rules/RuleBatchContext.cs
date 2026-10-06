using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Rules
{
    // 一个执行器拥有一个缓冲，阶段内借用视图；不创建或修改材料实例。
    internal sealed class RuleBatchContext
    {
        private sealed class KeyComparer : IComparer<CellKey>
        {
            internal static readonly KeyComparer Instance = new KeyComparer();
            public int Compare(CellKey a, CellKey b) => a.Position.CompareTo(b.Position);
        }

        private readonly Dictionary<CellKey, int> _indices;
        private readonly CellContact[] _contacts;
        private readonly int[] _neighbourSeen;
        private int _neighbourStamp;
        private readonly int[] _neighbours;
        internal readonly CellKey[] Keys;
        internal readonly CellSnapshot[] States;
        internal readonly MaterialRuntimeEntry[] Materials;
        internal readonly CellInstanceHandle[] Instances;
        internal IWorkingWorldView Snapshot { get; private set; }
        internal ITickInstanceMap InstanceMap { get; private set; }
        internal IContactQuery Contacts { get; private set; }
        internal TransactionContext Transaction { get; private set; }
        internal int Count { get; private set; }

        internal RuleBatchContext(int cellCapacity, int contactCapacity)
        {
            if (cellCapacity < 1 || contactCapacity < 0) throw new ArgumentOutOfRangeException(nameof(cellCapacity));
            Keys = new CellKey[cellCapacity];
            States = new CellSnapshot[cellCapacity];
            Materials = new MaterialRuntimeEntry[cellCapacity];
            Instances = new CellInstanceHandle[cellCapacity];
            _indices = new Dictionary<CellKey, int>(cellCapacity);
            _contacts = new CellContact[contactCapacity];
            _neighbourSeen = new int[cellCapacity];
            _neighbours = new int[cellCapacity];
        }

        internal WorldResult Begin(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext transaction)
        {
            Count = 0;
            _indices.Clear();
            Snapshot = snapshot;
            InstanceMap = instances;
            Contacts = contacts;
            Transaction = transaction;
            if (snapshot == null || materials == null || instances == null || snapshot.Config == null)
                return Error(transaction.Stage, WorldErrorCode.InvalidArgument, default, "规则输入缺少快照、材料表或实例表。");
            if (snapshot.Generation != transaction.PublishedVersion.Generation || snapshot.WorkingTick != transaction.WorkingTick ||
                transaction.PublishedVersion.CommittedTick == ulong.MaxValue ||
                transaction.WorkingTick != transaction.PublishedVersion.CommittedTick + 1)
                return Error(transaction.Stage, WorldErrorCode.StaleGeneration, default, "阶段视图与工作 Tick/代次不一致。");
            if (contacts != null && contacts.Lease.Generation != 0 && (contacts.Lease.Generation != snapshot.Generation ||
                contacts.Lease.WorkingTick != snapshot.WorkingTick || contacts.Lease.Stage != transaction.Stage))
                return Error(transaction.Stage, WorldErrorCode.NotReady, default, "接触提供者没有绑定当前阶段。");
            ReadOnlySpan<CellKey> occupied = snapshot.OccupiedCells;
            if (occupied.Length > Keys.Length || occupied.Length > snapshot.Config.Limits.MaxMaterialCells)
                return Error(transaction.Stage, WorldErrorCode.CapacityExceeded, default, "规则扫描缓冲或材料容量不足。");
            occupied.CopyTo(Keys);
            // 正式工作视图已按位置排序；仅兼容未排序的外部/测试视图时重排。
            for (int i = 1; i < occupied.Length; i++)
                if (Keys[i - 1].Position.CompareTo(Keys[i].Position) > 0)
                {
                    Array.Sort(Keys, 0, occupied.Length, KeyComparer.Instance);
                    break;
                }
            for (int i = 0; i < occupied.Length; i++)
            {
                CellKey key = Keys[i];
                if (key.Generation != snapshot.Generation || _indices.ContainsKey(key))
                    return Error(transaction.Stage, WorldErrorCode.InvalidArgument, key, "阶段占据键重复或代次无效。");
                WorldResult read = snapshot.Read(key, out CellSnapshot state);
                if (!read.IsSuccess) return AtStage(read, transaction.Stage, key);
                if (state.MaterialId == 0 || !materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material))
                    return Error(transaction.Stage, WorldErrorCode.UnknownMaterial, key, "占据集合包含空格或未注册材料。");
                if (!instances.TryGetInstance(key, out CellInstanceHandle instance) || instance.Sequence == 0 ||
                    instance.Generation != snapshot.Generation || instance.WorkingTick != snapshot.WorkingTick ||
                    !instances.TryResolve(instance, out CellKey resolved) || !resolved.Equals(key))
                    return Error(transaction.Stage, WorldErrorCode.InvalidArgument, key, "占据源缺少有效的当前 Tick 材料实例。");
                _indices.Add(key, i);
                States[i] = state;
                Materials[i] = material;
                Instances[i] = instance;
            }
            Count = occupied.Length;
            return WorldResult.Success();
        }

        internal WorldResult Passable(int x, int y, IOccupancyView occupancy, out bool passable)
        {
            passable = false;
            WorldConfig config = Snapshot.Config;
            if (x < 0 || y < 0 || x >= config.Width || y >= config.Height) return WorldResult.Success();
            var key = new CellKey(Snapshot.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
            WorldResult read = Snapshot.Read(key, out CellSnapshot state);
            if (!read.IsSuccess) return AtStage(read, Transaction.Stage, key);
            if (state.MaterialId != 0) return WorldResult.Success();
            return ClearOfSolids(x, y, occupancy, out passable);
        }

        internal bool TryGetGridIndex(int x, int y, out int index)
        {
            var key = new CellKey(Snapshot.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
            return _indices.TryGetValue(key, out index);
        }

        // 原子水链可进入同批会腾空的水格，但仍须独立核对真实固体占据。
        internal WorldResult ClearOfSolids(int x, int y, IOccupancyView occupancy, out bool passable)
        {
            if (occupancy != null && occupancy.Lease.Generation != 0 && (occupancy.Lease.Generation != Snapshot.Generation ||
                occupancy.Lease.WorkingTick != Snapshot.WorkingTick || occupancy.Lease.Stage != Transaction.Stage))
            {
                passable = false;
                return Error(Transaction.Stage, WorldErrorCode.NotReady, default, "占据提供者没有绑定当前阶段。");
            }
            passable = false;
            WorldConfig config = Snapshot.Config;
            if (x < 0 || y < 0 || x >= config.Width || y >= config.Height) return WorldResult.Success();
            var key = new CellKey(Snapshot.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
            if (occupancy == null)
            {
                if (Snapshot.Bodies.Length != 0)
                    return Error(Transaction.Stage, WorldErrorCode.NotReady, key, "存在动态体，必须注入 M06 的真实固体占据视图。");
            }
            else
            {
                if (!occupancy.Version.Equals(Transaction.PublishedVersion))
                    return Error(Transaction.Stage, WorldErrorCode.StaleGeneration, key, "固体占据视图的版本租约不一致。");
                var geometry = new CellGeometry(key, new BodyPose(Snapshot.Origin, 0), new Vector2Int(x, y), config.CellSize);
                WorldResult result = occupancy.HasSolidOverlap(geometry, out bool overlaps);
                if (!result.IsSuccess) return AtStage(result, Transaction.Stage, key);
                if (overlaps) return WorldResult.Success();
            }
            passable = true;
            return WorldResult.Success();
        }

        // 同归属严格四邻接；只把跨归属真实格接触交给 M06。
        internal WorldResult Neighbours(int source, out ReadOnlySpan<int> neighbours)
        {
            neighbours = ReadOnlySpan<int>.Empty;
            if (_neighbourStamp == int.MaxValue)
            {
                Array.Clear(_neighbourSeen, 0, _neighbourSeen.Length);
                _neighbourStamp = 0;
            }
            _neighbourStamp++;
            int count = 0;
            CellKey key = Keys[source];
            AddAdjacent(key, -1, 0, ref count);
            AddAdjacent(key, 1, 0, ref count);
            AddAdjacent(key, 0, -1, ref count);
            AddAdjacent(key, 0, 1, ref count);
            if (Snapshot.Bodies.Length != 0)
            {
                if (Contacts == null)
                    return Error(Transaction.Stage, WorldErrorCode.NotReady, key, "跨归属邻域需要 M06 精确接触查询。");
                QueryResult query = Contacts.QueryContacts(key, _contacts);
                if (!query.Result.IsSuccess) return AtStage(query.Result, Transaction.Stage, key);
                if (query.Version.Generation != Snapshot.Generation || query.WrittenCount != query.RequiredCount || query.WrittenCount > _contacts.Length)
                    return Error(Transaction.Stage, WorldErrorCode.InvalidArgument, key, "接触查询返回过期或不完整的成功批次。");
                for (int i = 0; i < query.WrittenCount; i++)
                {
                    CellContact contact = _contacts[i];
                    CellKey other;
                    if (contact.First.Equals(key)) other = contact.Second;
                    else if (contact.Second.Equals(key)) other = contact.First;
                    else return Error(Transaction.Stage, WorldErrorCode.InvalidArgument, key, "接触结果不包含所查询的格。");
                    if (other.Generation != Snapshot.Generation)
                        return Error(Transaction.Stage, WorldErrorCode.StaleGeneration, other, "接触目标代次不一致。");
                    if (SameOwner(key, other)) continue;
                    if (!ContractDefaults.IsEffectiveContact(contact.Feature, contact.Distance, Snapshot.Config.CellSize)) continue;
                    if (!_indices.TryGetValue(other, out int target))
                        return Error(Transaction.Stage, WorldErrorCode.InvalidArgument, other, "有效接触目标不在阶段占据集合中。");
                    AddNeighbour(target, ref count);
                }
            }
            Array.Sort(_neighbours, 0, count);
            neighbours = _neighbours.AsSpan(0, count);
            return WorldResult.Success();
        }

        private void AddAdjacent(CellKey key, int dx, int dy, ref int count)
        {
            long x = (long)key.Position.X + dx, y = (long)key.Position.Y + dy;
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue) return;
            var other = new CellKey(key.Generation, new CellPositionKey(key.Position.OwnerKind, key.Position.BodyId, (int)x, (int)y));
            if (_indices.TryGetValue(other, out int index)) AddNeighbour(index, ref count);
        }
        private void AddNeighbour(int index, ref int count)
        {
            if (_neighbourSeen[index] == _neighbourStamp) return;
            _neighbourSeen[index] = _neighbourStamp;
            _neighbours[count++] = index;
        }

        internal static bool SameOwner(CellKey a, CellKey b) => a.Position.OwnerKind == b.Position.OwnerKind && a.Position.BodyId == b.Position.BodyId;
        internal static bool SameState(in CellSnapshot a, in CellSnapshot b) => a.MaterialId == b.MaterialId && a.Flags == b.Flags &&
            a.FuelTicksRemaining == b.FuelTicksRemaining && a.SpreadCountdown == b.SpreadCountdown &&
            a.LifetimeTicksRemaining == b.LifetimeTicksRemaining && a.MoveCountdown == b.MoveCountdown && a.IgnitedTick == b.IgnitedTick;
        internal static WorldResult Error(TickStage stage, WorldErrorCode code, CellKey key, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic(stage.ToString(), $"{key.Position.OwnerKind}/{key.Position.BodyId}/{key.Position.X},{key.Position.Y}", message));
        internal static WorldResult AtStage(WorldResult result, TickStage stage, CellKey key) => Error(stage,
            result.ErrorCode == WorldErrorCode.None ? WorldErrorCode.InvalidArgument : result.ErrorCode, key, result.Diagnostic.Message);
    }
}
