using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Simulation
{
    // M02 的内部写入描述，不是跨模块的第二套 MutationIntent。
    internal readonly struct CellWrite
    {
        internal readonly CellKey Key;
        internal readonly CellSnapshot State;
        internal readonly bool NewInstance;
        internal CellWrite(CellKey key, CellSnapshot state, bool newInstance = false)
        {
            Key = key; State = state; NewInstance = newInstance;
        }
    }

    // 仅状态核心，不代表完整 Ready 世界；正式 Create 必须继续 M04–M07B 准备。
    internal sealed partial class WorkingWorld : IWorkingWorldView, IFluidSuspensionView, IDisposable
    {
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly long _memoryLimit;
        private readonly IMaterialRuntimeTable _materials;
        private readonly IdentitySequence _bodyIds = new();
        private readonly IdentitySequence _fluidIds = new();
        private SortedDictionary<ulong, SuspendedFluidSnapshot> _suspended = new();
        private SuspendedFluidSnapshot[] _suspendedSnapshots = Array.Empty<SuspendedFluidSnapshot>();
        private SortedDictionary<ulong, IMaterialBodyStorage> _bodies = new();
        private BodySnapshot[] _bodySnapshots = Array.Empty<BodySnapshot>();
        private ChunkStore _chunks;
        private SortedSet<CellPositionKey> _occupied = new();
        private HashSet<CellPositionKey> _fixed = new();
        private CellKey[] _orderedKeys = Array.Empty<CellKey>();
        private TickInstanceMap _instances;
        private TickChangeCounter _changes;
        private long _revision;
        private bool _tickActive;
        private bool _disposed;
        private bool _faulted;
        private int _staticShapes;
        private int _dynamicShapes;
        private PreparedGridMutation _pending;
        internal CommittedWorldView Published { get; private set; }
        internal SceneInitialData Initial { get; }
        public WorldConfig Config { get; }
        public Vector2 Origin { get; }
        public ulong Generation { get; }
        public ulong WorkingTick { get; private set; }
        public ReadOnlySpan<CellKey> OccupiedCells { get { RequireAccess(); return _orderedKeys; } }
        public ReadOnlySpan<BodySnapshot> Bodies { get { RequireAccess(); return _bodySnapshots; } }
        public ReadOnlySpan<SuspendedFluidSnapshot> SuspendedFluids { get { RequireAccess(); return _suspendedSnapshots; } }
        internal TickInstanceMap Instances => _instances;
        internal int ChangedPositions => _changes?.Count ?? 0;
        internal IEnumerable<CellPositionKey> TickWrites => _changes.Writes;
        internal int MaterialCells => _occupied.Count + _suspended.Count;
        internal int ChunkCount => _chunks.ChunkCount;
        internal long EstimatedCpuBytes => EstimateBytes(_chunks.ChunkCount, MaterialCells, false);

        private WorkingWorld(WorldLoadResult loaded, Vector2 origin, ulong generation, long memoryLimit)
        {
            Config = loaded.Config;
            _materials = loaded.Materials;
            Origin = origin;
            Generation = generation;
            _memoryLimit = memoryLimit;
            Initial = new SceneInitialData(loaded.Scene.SchemaVersion, loaded.Scene.MaterialSetId,
                loaded.Scene.MaterialsFile, loaded.Scene.WorldConfigFile, loaded.Scene.Cells, loaded.Scene.FixedCells, loaded.Scene.InitialBurning);
            _chunks = new ChunkStore(Config.Width, Config.Height, memoryLimit);
        }

        internal static WorldResult CreateInitial(WorldLoadResult loaded, Vector2 origin, ulong generation,
            out WorkingWorld world, long memoryLimit = ContractDefaults.CpuBudgetBytes)
        {
            world = null;
            if (!loaded.Result.IsSuccess) return loaded.Result;
            if (generation == 0 || !ContractDefaults.IsFinite(origin) || memoryLimit < 0)
                return Error(WorldErrorCode.InvalidArgument, "CreateState", "origin/generation", "初态参数无效。");
            WorkingWorld candidate = null;
            try
            {
                candidate = new WorkingWorld(loaded, origin, generation, memoryLimit);
                var chunks = new HashSet<ChunkCoord>();
                foreach (InitialCell cell in candidate.Initial.Cells) chunks.Add(ChunkCoord.FromCell(cell.Position.x, cell.Position.y));
                if (candidate.Initial.Cells.Count > candidate.Config.Limits.MaxMaterialCells ||
                    candidate.EstimateBytes(chunks.Count, candidate.Initial.Cells.Count, false) > memoryLimit)
                    return Error(WorldErrorCode.CapacityExceeded, "CreateState", "cpuBytes", "初态及全部保留工作缓冲超限。");
                var burning = new HashSet<Vector2Int>(candidate.Initial.InitialBurning);
                foreach (InitialCell cell in candidate.Initial.Cells)
                {
                    if (!candidate._materials.TryGet(cell.MaterialId, out MaterialRuntimeEntry material))
                        return Error(WorldErrorCode.UnknownMaterial, "CreateState", "cell", "初态材料不存在。");
                    var state = CellState.Create(material, burning.Contains(cell.Position));
                    WorldResult written = candidate._chunks.Write(cell.Position.x, cell.Position.y, state);
                    if (!written.IsSuccess) return written;
                    candidate._occupied.Add(new CellPositionKey(OwnerKind.Grid, 0, cell.Position.x, cell.Position.y));
                }
                foreach (Vector2Int point in candidate.Initial.FixedCells)
                    candidate._fixed.Add(new CellPositionKey(OwnerKind.Grid, 0, point.x, point.y));
                candidate.RebuildOrder();
                var sourcePositions = new CellPositionKey[candidate._orderedKeys.Length];
                for (int i = 0; i < sourcePositions.Length; i++) sourcePositions[i] = candidate._orderedKeys[i].Position;
                candidate._ruleSources = candidate._ruleSources.Prepare(candidate, candidate._materials, sourcePositions);
                candidate._instances = new TickInstanceMap(generation, 0);
                foreach (CellKey key in candidate._orderedKeys) candidate._instances.Create(key);
                candidate._changes = new TickChangeCounter(candidate.Config.Limits.MaxChangesPerTick);
                candidate.Published = new CommittedWorldView(candidate, candidate._materials, new WorldVersion(generation, 0));
                world = candidate;
                return WorldResult.Success();
            }
            catch (Exception exception)
            {
                return Error(WorldErrorCode.CapacityExceeded, "CreateState", "allocation", exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (world == null) candidate?.Dispose();
            }
        }

        public WorldResult Read(in CellKey key, out CellSnapshot cell)
        {
            cell = default;
            WorldResult check = CheckKey(key);
            if (!check.IsSuccess) return check;
            if (key.Position.OwnerKind == OwnerKind.Suspended)
            {
                if (_suspended.TryGetValue(key.Position.BodyId, out SuspendedFluidSnapshot record)) cell = record.State;
                return WorldResult.Success();
            }
            if (key.Position.OwnerKind == OwnerKind.Body)
            {
                _bodies[key.Position.BodyId].TryRead(key.Position, out cell);
                return WorldResult.Success();
            }
            WorldResult result = _chunks.Read(key.Position.X, key.Position.Y, out CellState state);
            cell = state.Snapshot;
            return result;
        }

        public bool IsFixed(in CellKey key) => CheckKey(key).IsSuccess && _fixed.Contains(key.Position);

        // 属于整个 generation，不能随 Tick、候选复制/Abort 或体撤销重建。
        // M02 将此方法绑定为 BodyIdReservation 传入材料准备入口，不暴露可写世界。
        internal WorldResult ReserveBodyIds(int count, Span<ulong> destination)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return access;
            WorldResult result = _bodyIds.Reserve(count, destination);
            if (result.IsSuccess && count > 0) _pending?.TrackReservedIds(destination[0], destination[count - 1]);
            return result;
        }

        internal WorldResult BeginTick()
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return access;
            if (_tickActive) return Error(WorldErrorCode.Busy, "BeginTick", "tick", "Tick 已开始，不能重置整步计数。");
            if (_pending != null) return Error(WorldErrorCode.Busy, "BeginTick", "prepared", "初态候选未结束，不能开始Tick。");
            WorldResult advance = ContractDefaults.Advance(WorkingTick, out ulong next);
            if (!advance.IsSuccess) return advance;
            _instances?.Close();
            _instances = new TickInstanceMap(Generation, next);
            foreach (CellKey key in _orderedKeys) _instances.Create(key);
            foreach (SuspendedFluidSnapshot record in _suspendedSnapshots) _instances.Create(record.Key);
            _changes = new TickChangeCounter(Config.Limits.MaxChangesPerTick);
            WorkingTick = next;
            _tickActive = true;
            return WorldResult.Success();
        }

        internal PreparationResult<IPreparedMutation> PrepareReplace(in CellKey key, ushort materialId, in TransactionContext context)
        {
            if (!_materials.TryGet(materialId, out MaterialRuntimeEntry material))
                return Rejected(Error(WorldErrorCode.UnknownMaterial, context.Stage.ToString(), "materialId", "替换材料必须为已定义非零 ID。"));
            if (key.Position.OwnerKind == OwnerKind.Body &&
                (material.Kind != MaterialKind.Solid || (material.Rules & RuleMask.Structure) == 0))
                return Rejected(Error(WorldErrorCode.UnsupportedOperation, context.Stage.ToString(), "body.material", "动态体只能替换为结构固体。"));
            var writes = new[] { new CellWrite(key, CellState.Create(material).Snapshot, true) };
            return Prepare(writes, context);
        }

        internal PreparationResult<IPreparedMutation> PrepareMove(in CellKey source, in CellKey target, in TransactionContext context)
        {
            WorldResult check = Read(source, out CellSnapshot state);
            if (!check.IsSuccess) return Rejected(check);
            check = Read(target, out CellSnapshot destination);
            if (!check.IsSuccess) return Rejected(check);
            if (state.MaterialId == 0 || source.Equals(target))
                return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "move", "源必须非空且与目标不同。"));
            if (destination.MaterialId != 0)
                return Rejected(Error(WorldErrorCode.Occupied, context.Stage.ToString(), "move", "搬运目标必须为空。"));
            return Prepare(new[] { new CellWrite(source, default), new CellWrite(target, state) }, context, source, target);
        }

        internal StructurePlanResult PlanInitialStructure(IStructurePlanner planner)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return new StructurePlanResult(access);
            if (planner == null || _tickActive || WorkingTick != 0)
                return new StructurePlanResult(Error(WorldErrorCode.NotReady, "Structure", "initial", "初态分析需要有效规划器及未推进的状态。"));
            return planner.Plan(this, _materials, ReadOnlySpan<CellKey>.Empty);
        }

        // 裁决后的整批写入已准备，材料尚未 Apply；返回完整计划供 M05/M06 继续准备。
        internal StructurePlanResult PlanStructure(IPreparedMutation prepared, IStructurePlanner planner,
            in TransactionContext context, IFailureInjector failures = null)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return new StructurePlanResult(access);
            if (planner == null || prepared == null || !ReferenceEquals(prepared, _pending))
                return new StructurePlanResult(Error(WorldErrorCode.InvalidArgument, "Structure", "candidate", "必须使用本世界拥有的当前候选和有效规划器。"));
            if (_pending._ownershipPrepared)
                return new StructurePlanResult(Error(WorldErrorCode.Busy, "Structure", "candidate", "归属准备完成后不能覆盖其结构计划。"));
            WorldResult valid = prepared.Preflight(context);
            if (!valid.IsSuccess) return new StructurePlanResult(valid);
            PreparedGridMutation candidate = _pending;
            StructurePlanResult result;
            try
            {
                result = planner.Plan(candidate.CandidateWorld, _materials, candidate.ChangedCells);
                if (result.Result.IsSuccess)
                {
                    candidate.SetStructureBudget(result.Plan);
                    WorldResult injected = failures?.Check(context, FailurePoint.AfterStructurePrepared) ?? WorldResult.Success();
                    if (!injected.IsSuccess) result = new StructurePlanResult(injected);
                }
            }
            catch (Exception exception)
            {
                result = new StructurePlanResult(Error(WorldErrorCode.Faulted, "Structure", "planner", exception.GetType().Name + ": " + exception.Message));
            }
            if (!result.Result.IsSuccess)
            {
                candidate.Abort();
                if (context.Stage != TickStage.Commands || result.Result.ErrorCode == WorldErrorCode.Faulted) Fault();
            }
            return result;
        }

        internal PreparationResult<IPreparedMutation> Prepare(ReadOnlySpan<CellWrite> writes, in TransactionContext context,
            CellKey? moveSource = null, CellKey? moveTarget = null, ReadOnlySpan<MutationIntent> moves = default,
            ReadOnlySpan<SuspendedFluidSnapshot> newSuspensions = default)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return Rejected(access);
            bool initial = !_tickActive && WorkingTick == 0 && context.WorkingTick == 0 && context.Stage == TickStage.Structure;
            if ((!_tickActive && !initial) || !context.PublishedVersion.Equals(Published.Version) || context.WorkingTick != WorkingTick)
                return Rejected(Error(WorldErrorCode.NotReady, context.Stage.ToString(), "tick", "事务不属于当前工作 Tick。"));
            if (initial && writes.Length != 0)
                return Rejected(Error(WorldErrorCode.InvalidArgument, "Structure", "initial", "初态准备不接受编辑写入。"));
            if (_pending != null) return Rejected(Error(WorldErrorCode.Busy, context.Stage.ToString(), "prepared", "同一状态仅允许一个未应用候选。"));
            var final = new SortedDictionary<CellPositionKey, CellWrite>();
            foreach (CellWrite write in writes)
            {
                WorldResult keyCheck = CheckKey(write.Key);
                if (!keyCheck.IsSuccess) return Rejected(keyCheck);
                WorldResult stateCheck = ValidateState(write.State, context.Stage);
                if (!stateCheck.IsSuccess) return Rejected(stateCheck);
                if (final.TryGetValue(write.Key.Position, out CellWrite previous))
                    final[write.Key.Position] = new CellWrite(write.Key, write.State, write.NewInstance || previous.NewInstance);
                else final.Add(write.Key.Position, write);
            }
            // 普通搬运仍要求阶段空目标。水链可占据同批下一源，整链沿邻格下降到原空终点。
            // 先验证完整有向路径，再在候选副本内按尾到头搬运，保留实例和湿标记。
            var orderedMoves = new List<MutationIntent>(moves.Length);
            HashSet<CellPositionKey> movementPositions = null;
            if (moves.Length > 0)
            {
                if (moveSource.HasValue || moveTarget.HasValue)
                    return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "不能混用单格和批次搬运。"));
                var sources = new Dictionary<CellKey, MutationIntent>();
                var targets = new HashSet<CellKey>();
                movementPositions = new HashSet<CellPositionKey>();
                foreach (MutationIntent move in moves)
                {
                    if (move.Kind != MutationKind.Move || move.Source.Equals(move.Target) ||
                        sources.ContainsKey(move.Source) || !targets.Add(move.Target) ||
                        !_instances.TryResolve(move.Instance, out CellKey source) || !source.Equals(move.Source) ||
                        !Read(move.Source, out CellSnapshot before).IsSuccess || before.MaterialId == 0 ||
                        move.State.MaterialId != before.MaterialId)
                        return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "移动源实例或唯一目标无效。"));
                    sources.Add(move.Source, move);
                    movementPositions.Add(move.Source.Position);
                    movementPositions.Add(move.Target.Position);
                }
                foreach (MutationIntent move in moves)
                {
                    if (!Read(move.Target, out CellSnapshot destination).IsSuccess ||
                        (destination.MaterialId != 0 && (!sources.ContainsKey(move.Target) ||
                            destination.MaterialId != move.State.MaterialId)) ||
                        !final.TryGetValue(move.Source.Position, out CellWrite clear) || !clear.Key.Equals(move.Source) || clear.NewInstance ||
                        (!targets.Contains(move.Source) && clear.State.MaterialId != 0) ||
                        !final.TryGetValue(move.Target.Position, out CellWrite fill) || !fill.Key.Equals(move.Target) ||
                        !SameState(fill.State, move.State) || fill.NewInstance)
                        return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "移动目标未释放、异材或配套最终写入无效。"));
                }
                var visited = new HashSet<CellKey>();
                var path = new List<MutationIntent>();
                foreach (MutationIntent move in moves)
                {
                    if (targets.Contains(move.Source)) continue;
                    path.Clear();
                    MutationIntent current = move;
                    while (true)
                    {
                        if (!visited.Add(current.Source))
                            return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "水链包含重复路径或循环。"));
                        path.Add(current);
                        if (!sources.TryGetValue(current.Target, out current)) break;
                    }
                    if (path.Count > 1)
                    {
                        CellKey root = path[0].Source;
                        CellKey tail = path[path.Count - 1].Target;
                        if (context.Stage != TickStage.Water || root.Position.Y <= tail.Position.Y ||
                            !_materials.TryGet(path[0].State.MaterialId, out MaterialRuntimeEntry liquid) ||
                            (liquid.Rules & RuleMask.LiquidFlow) == 0)
                            return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "占据目标只允许水阶段同材液体链严格下降到空终点。"));
                        foreach (MutationIntent link in path)
                        {
                            int dx = link.Target.Position.X - link.Source.Position.X;
                            int dy = link.Target.Position.Y - link.Source.Position.Y;
                            if (link.Source.Position.OwnerKind != OwnerKind.Grid || link.Target.Position.OwnerKind != OwnerKind.Grid ||
                                link.State.MaterialId != liquid.Id || dy > 0 || Math.Abs((long)dx) + Math.Abs((long)dy) != 1)
                                return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "水链只能在主网格内沿同材的正交下/左/右邻格移动。"));
                        }
                    }
                    for (int i = path.Count - 1; i >= 0; i--) orderedMoves.Add(path[i]);
                }
                if (visited.Count != moves.Length)
                    return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "moves", "搬运批次包含没有原空终点的环。"));
            }
            var changed = new List<CellPositionKey>();
            var addedChunks = new HashSet<ChunkCoord>();
            int materialCount = MaterialCells;
            foreach (CellWrite write in final.Values)
            {
                Read(write.Key, out CellSnapshot before);
                if (before.MaterialId != 0 && write.State.MaterialId != 0 && before.MaterialId != write.State.MaterialId && !write.NewInstance)
                    return Rejected(Error(WorldErrorCode.InvalidArgument, context.Stage.ToString(), "instance", "材料替换必须显式创建新实例。"));
                if (write.NewInstance || !SameState(before, write.State) ||
                    (movementPositions != null && movementPositions.Contains(write.Key.Position))) changed.Add(write.Key.Position);
                materialCount += (write.State.MaterialId == 0 ? 0 : 1) - (before.MaterialId == 0 ? 0 : 1);
                if (write.Key.Position.OwnerKind == OwnerKind.Grid)
                {
                    ChunkCoord coord = ChunkCoord.FromCell(write.Key.Position.X, write.Key.Position.Y);
                    if (write.State.MaterialId != 0 && !_chunks.HasChunk(coord)) addedChunks.Add(coord);
                }
            }
            CellPositionKey[] positions = changed.ToArray();
            WorldResult capacity = _changes.Preflight(positions, Config.Limits.MaxChangesPerTick);
            if (!capacity.IsSuccess) return Rejected(capacity);
            int changedPositions = _changes.ProjectedCount;
            long cpuBytes = EstimateBytes(_chunks.ChunkCount + addedChunks.Count, materialCount, true);
            if (materialCount > Config.Limits.MaxMaterialCells || cpuBytes > _memoryLimit)
                return Rejected(Error(WorldErrorCode.CapacityExceeded, context.Stage.ToString(), "stateBudget", "材料、双缓冲和候选工作集超过预算。"));
            ChunkStore candidate = null;
            TickInstanceMap instances = null;
            try
            {
                var suspended = new SortedDictionary<ulong, SuspendedFluidSnapshot>(_suspended);
                foreach (SuspendedFluidSnapshot record in newSuspensions)
                {
                    if (context.Stage != TickStage.Physics || record.Generation != Generation || record.SuspendedTick != WorkingTick ||
                        record.OriginalPosition.x < 0 || record.OriginalPosition.y < 0 || record.OriginalPosition.x >= Config.Width || record.OriginalPosition.y >= Config.Height ||
                        suspended.ContainsKey(record.RecordId) || !_materials.TryGet(record.State.MaterialId, out MaterialRuntimeEntry fluid) ||
                        (fluid.Rules & (RuleMask.LiquidFlow | RuleMask.GasDrift)) == 0 ||
                        !final.TryGetValue(record.Key.Position, out CellWrite fill) || !SameState(fill.State, record.State))
                        throw new InvalidOperationException("暂存新增记录与完整流体迁移批次不一致。");
                    suspended.Add(record.RecordId, record);
                }
                candidate = _chunks.Clone();
                instances = _instances.Clone();
                // 状态倒计时不改变占据/固定目录；首次实际增删时才分离集合。
                var occupied = _occupied;
                var fixedCells = _fixed;
                void SetOccupied(CellPositionKey position, bool present)
                {
                    if (occupied.Contains(position) == present) return;
                    if (ReferenceEquals(occupied, _occupied))
                    {
                        using var copyTiming = WorldStepMetrics.Measure(WorldStepMetrics.Timing.Copy);
                        occupied = new SortedSet<CellPositionKey>(_occupied);
                        WorldStepMetrics.Add(WorldStepMetrics.Work.DirectoryCopies);
                        WorldStepMetrics.Add(WorldStepMetrics.Work.DirectoryEntries, _occupied.Count);
                        WorldStepMetrics.Add(WorldStepMetrics.Work.CopiedPayloadBytes,
                            (long)_occupied.Count * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CellPositionKey>());
                    }
                    if (present) occupied.Add(position); else occupied.Remove(position);
                }
                bool RemoveFixed(CellPositionKey position)
                {
                    if (!fixedCells.Contains(position)) return false;
                    if (ReferenceEquals(fixedCells, _fixed))
                    {
                        using var copyTiming = WorldStepMetrics.Measure(WorldStepMetrics.Timing.Copy);
                        fixedCells = new HashSet<CellPositionKey>(_fixed);
                        WorldStepMetrics.Add(WorldStepMetrics.Work.DirectoryCopies);
                        WorldStepMetrics.Add(WorldStepMetrics.Work.DirectoryEntries, _fixed.Count);
                        WorldStepMetrics.Add(WorldStepMetrics.Work.CopiedPayloadBytes,
                            (long)_fixed.Count * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CellPositionKey>());
                    }
                    return fixedCells.Remove(position);
                }
                var bodyCells = new Dictionary<CellPositionKey, CellSnapshot>();
                if (_bodies.Count != 0) foreach (CellKey key in _orderedKeys)
                    if (key.Position.OwnerKind == OwnerKind.Body)
                    {
                        Read(key, out CellSnapshot state);
                        bodyCells.Add(key.Position, state);
                    }
                if (moveSource.HasValue)
                {
                    if (!instances.TryGetInstance(moveSource.Value, out CellInstanceHandle instance))
                        throw new InvalidOperationException("搬运源实例不存在。");
                    instances.Move(instance, moveTarget.Value);
                    if (RemoveFixed(moveSource.Value.Position)) fixedCells.Add(moveTarget.Value.Position);
                }
                foreach (MutationIntent move in orderedMoves)
                {
                    instances.Move(move.Instance, move.Target);
                    if (RemoveFixed(move.Source.Position)) fixedCells.Add(move.Target.Position);
                }
                foreach (CellWrite write in final.Values)
                {
                    bool exists = instances.TryGetInstance(write.Key, out CellInstanceHandle old);
                    if (write.NewInstance || write.State.MaterialId == 0)
                    {
                        if (exists) instances.Invalidate(old);
                        RemoveFixed(write.Key.Position);
                        exists = false;
                    }
                    if (write.State.MaterialId == 0) SetOccupied(write.Key.Position, false);
                    else
                    {
                        if (write.Key.Position.OwnerKind != OwnerKind.Suspended) SetOccupied(write.Key.Position, true);
                        if (!exists) instances.Create(write.Key);
                    }
                    if (write.Key.Position.OwnerKind == OwnerKind.Suspended)
                    {
                        if (write.State.MaterialId == 0) suspended.Remove(write.Key.Position.BodyId);
                        else if (suspended.TryGetValue(write.Key.Position.BodyId, out SuspendedFluidSnapshot record))
                            suspended[record.RecordId] = record.WithState(write.State);
                        else throw new InvalidOperationException("暂存状态写入缺少记录身份与锚点。");
                    }
                    else if (write.Key.Position.OwnerKind == OwnerKind.Grid)
                    {
                        CellState state = CellState.FromSnapshot(write.State);
                        WorldResult written = candidate.Write(write.Key.Position.X, write.Key.Position.Y, state);
                        if (!written.IsSuccess) return Rejected(written);
                    }
                    else if (write.State.MaterialId == 0) bodyCells.Remove(write.Key.Position);
                    else bodyCells[write.Key.Position] = write.State;
                }
                _pending = new PreparedGridMutation(this, candidate, occupied, fixedCells, instances, bodyCells, suspended, positions,
                    new ResourceBudget(materialCount, _bodies.Count, _staticShapes, _dynamicShapes, changedPositions, cpuBytes), _revision, context);
                candidate = null;
                instances = null;
                return new PreparationResult<IPreparedMutation>(WorldResult.Success(), _pending);
            }
            catch (Exception exception)
            {
                return Rejected(Error(WorldErrorCode.CapacityExceeded, context.Stage.ToString(), "prepare", exception.GetType().Name + ": " + exception.Message));
            }
            finally
            {
                candidate?.Dispose();
                instances?.Close();
            }
        }

        // 只供状态核心/测试使用；完整 IWorld 必须在物理与全部下游成功后才调用。
        internal WorldResult PublishState(CommittedWorldView preparedView = null)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return access;
            if ((!_tickActive && WorkingTick != 0) || _pending != null) return Error(WorldErrorCode.Busy, "Publish", "tick", "存在未应用候选或没有活动 Tick。");
            CommittedWorldView next = null;
            try
            {
                next = preparedView ?? new CommittedWorldView(this, _materials, new WorldVersion(Generation, WorkingTick));
                Published.Dispose();
                Published = next;
                _instances.Close();
                _tickActive = false;
                return WorldResult.Success();
            }
            catch (Exception exception)
            {
                next?.Dispose();
                Fault();
                return Error(WorldErrorCode.Faulted, "Publish", "snapshot", exception.GetType().Name + ": " + exception.Message);
            }
        }

        internal void Fault()
        {
            _faulted = true;
            _instances?.Close();
            _pending?.Abort();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Abort();
            _instances?.Close();
            Published?.Dispose();
            _chunks.Dispose();
            foreach (IMaterialBodyStorage body in _bodies.Values) body.Dispose();
            _bodies.Clear();
            _bodySnapshots = Array.Empty<BodySnapshot>();
            _occupied.Clear();
            _fixed.Clear();
            _orderedKeys = Array.Empty<CellKey>();
            _suspended.Clear();
            _suspendedSnapshots = Array.Empty<SuspendedFluidSnapshot>();
            _ruleSources = new RuleSourceDirectory();
        }

        private long EstimateBytes(int chunkCount, int cells, bool preparation) => checked(
            131072L + _materials.Count * 256L + chunkCount * WorldChunk.StorageBytes * (preparation ? 2 : 1) +
            Math.Max(cells, MaterialCells) * (preparation ? 1024L : 512L) + Config.Limits.MaxChangesPerTick * 192L +
            Config.Limits.MaxMaterialCells * 640L + Config.Limits.MaxDynamicBodies * 256L + Config.Limits.MaxTotalShapes * 128L +
            (Published?.StorageBytes ?? 0) + Math.Max(cells, MaterialCells) * (preparation ? 1152L : 576L) + 5120L);

        private void RebuildOrder()
        {
            _orderedKeys = new CellKey[_occupied.Count];
            int index = 0;
            foreach (CellPositionKey position in _occupied) _orderedKeys[index++] = new CellKey(Generation, position);
        }

        private WorldResult CheckKey(in CellKey key)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return access;
            if (key.Generation != Generation) return Error(WorldErrorCode.StaleGeneration, "Read", "key", "材料键代次不一致。");
            if (key.Position.OwnerKind == OwnerKind.Suspended) return WorldResult.Success();
            if (key.Position.OwnerKind == OwnerKind.Body)
                return _bodies.ContainsKey(key.Position.BodyId) ? WorldResult.Success() :
                    Error(WorldErrorCode.InvalidArgument, "Read", "body", "材料体 ID 不属于当前工作目录。");
            return _chunks.Contains(key.Position.X, key.Position.Y) ? WorldResult.Success() :
                Error(WorldErrorCode.OutOfBounds, "Read", "cell", "坐标超出实际宽高。");
        }

        private WorldResult CheckAccess()
        {
            if (_disposed) return Error(WorldErrorCode.Disposed, "State", "world", "状态已释放。");
            if (Thread.CurrentThread.ManagedThreadId != _threadId) return Error(WorldErrorCode.InvalidArgument, "State", "thread", "操作必须在所属线程执行。");
            if (_faulted) return Error(WorldErrorCode.Faulted, "State", "world", "工作状态已冻结。");
            return WorldResult.Success();
        }

        private void RequireAccess()
        {
            WorldResult result = CheckAccess();
            if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic.Message);
        }

        private WorldResult ValidateState(in CellSnapshot state, TickStage stage)
        {
            if (state.MaterialId == 0)
                return SameState(state, default) ? WorldResult.Success() : Error(WorldErrorCode.InvalidArgument, stage.ToString(), "empty", "空格必须清空全部字段。");
            if (!_materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material))
                return Error(WorldErrorCode.UnknownMaterial, stage.ToString(), "materialId", "材料不存在。");
            bool burnable = (material.Rules & RuleMask.Burnable) != 0;
            bool gas = (material.Rules & RuleMask.GasDrift) != 0;
            if ((state.Flags & ~1) != 0 || (!burnable && (state.Flags != 0 || state.FuelTicksRemaining != 0 || state.SpreadCountdown != 0 || state.IgnitedTick != 0)) ||
                (!gas && state.LifetimeTicksRemaining != 0) || (!state.IsBurning && state.IgnitedTick != 0) || state.IgnitedTick > WorkingTick ||
                (burnable && (state.FuelTicksRemaining == 0 || state.FuelTicksRemaining > material.Parameters.FuelTicks || state.SpreadCountdown > material.Parameters.SpreadIntervalTicks)) ||
                (gas && (state.LifetimeTicksRemaining == 0 || state.LifetimeTicksRemaining > material.Parameters.LifetimeTicks)) ||
                state.MoveCountdown > material.Parameters.MoveIntervalTicks)
                return Error(WorldErrorCode.InvalidArgument, stage.ToString(), "state", "状态字段与材料能力或当前 Tick 不一致。");
            return WorldResult.Success();
        }

        private static bool SameState(in CellSnapshot a, in CellSnapshot b) => a.MaterialId == b.MaterialId && a.Flags == b.Flags &&
            a.FuelTicksRemaining == b.FuelTicksRemaining && a.SpreadCountdown == b.SpreadCountdown &&
            a.LifetimeTicksRemaining == b.LifetimeTicksRemaining && a.MoveCountdown == b.MoveCountdown && a.IgnitedTick == b.IgnitedTick;
        private static PreparationResult<IPreparedMutation> Rejected(WorldResult result) => new PreparationResult<IPreparedMutation>(result);
        private static WorldResult Error(WorldErrorCode code, string stage, string target, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic(stage, target, message));

        private sealed partial class PreparedGridMutation : IPreparedMaterialMutation, IWorkingWorldView, IFluidSuspensionView
        {
            internal readonly WorkingWorld _owner;
            private ChunkStore _candidate;
            private SortedSet<CellPositionKey> _occupied;
            private HashSet<CellPositionKey> _fixed;
            internal TickInstanceMap _instances;
            private CellPositionKey[] _positions;
            private CellKey[] _orderedKeys;
            private Dictionary<CellPositionKey, CellSnapshot> _bodyCells;
            private SortedDictionary<ulong, SuspendedFluidSnapshot> _suspended;
            private SuspendedFluidSnapshot[] _suspendedSnapshots;
            private readonly CellKey[] _changedCells;
            private readonly long _revision;
            private readonly TransactionContext _context;
            public PreparationState State { get; private set; } = PreparationState.Prepared;
            public ResourceBudget Budget { get; internal set; }
            public IWorkingWorldView CandidateWorld => this;
            public ITickInstanceMap CandidateInstances => _instanceView;
            public ReadOnlySpan<CellPositionKey> CandidateWrites { get { RequireLease(); return _positions; } }
            internal ReadOnlySpan<CellKey> ChangedCells => _changedCells;
            public WorldConfig Config { get { RequireLease(); return _owner.Config; } }
            public Vector2 Origin { get { RequireLease(); return _owner.Origin; } }
            public ulong Generation { get { RequireLease(); return _owner.Generation; } }
            public ulong WorkingTick { get { RequireLease(); return _context.WorkingTick; } }
            public ReadOnlySpan<CellKey> OccupiedCells { get { RequireLease(); return _orderedKeys; } }
            // 编辑阶段保留原目录及删空体；归属准备后提供完整结果目录。
            public ReadOnlySpan<BodySnapshot> Bodies { get { RequireLease(); return _bodySnapshots; } }
            public ReadOnlySpan<SuspendedFluidSnapshot> SuspendedFluids { get { RequireLease(); return _suspendedSnapshots; } }

            internal PreparedGridMutation(WorkingWorld owner, ChunkStore candidate, SortedSet<CellPositionKey> occupied,
                HashSet<CellPositionKey> fixedCells, TickInstanceMap instances, Dictionary<CellPositionKey, CellSnapshot> bodyCells,
                SortedDictionary<ulong, SuspendedFluidSnapshot> suspended, CellPositionKey[] positions,
                ResourceBudget budget, long revision, TransactionContext context)
            {
                _owner = owner; _candidate = candidate; _occupied = occupied; _fixed = fixedCells;
                _instances = instances; _positions = positions; Budget = budget; _revision = revision; _context = context;
                _bodyCells = bodyCells;
                _suspended = suspended;
                _suspendedSnapshots = OrderSuspended(suspended);
                _bodySnapshots = owner._bodySnapshots;
                _instanceView = new CandidateInstanceView(this);
                if (ReferenceEquals(occupied, owner._occupied)) _orderedKeys = owner._orderedKeys;
                else
                {
                    _orderedKeys = new CellKey[occupied.Count];
                    int index = 0;
                    foreach (CellPositionKey position in occupied) _orderedKeys[index++] = new CellKey(owner.Generation, position);
                }
                _changedCells = new CellKey[positions.Length];
                for (int i = 0; i < positions.Length; i++) _changedCells[i] = new CellKey(owner.Generation, positions[i]);
            }

            internal WorldResult CheckLease()
            {
                WorldResult access = _owner.CheckAccess();
                if (!access.IsSuccess) return access;
                return State == PreparationState.Prepared && _owner._revision == _revision && ReferenceEquals(_owner._pending, this)
                    ? WorldResult.Success() : Error(WorldErrorCode.NotReady, "Structure", "candidate", "候选只在当前 Prepared 租约内可读。" );
            }

            internal void RequireLease()
            {
                WorldResult valid = CheckLease();
                if (!valid.IsSuccess) throw new InvalidOperationException(valid.Diagnostic.Message);
            }

            public WorldResult Read(in CellKey key, out CellSnapshot cell)
            {
                cell = default;
                WorldResult valid = CheckLease();
                if (!valid.IsSuccess) return valid;
                if (key.Generation != _owner.Generation) return Error(WorldErrorCode.StaleGeneration, "CandidateRead", "key", "候选格代次不一致。");
                if (key.Position.OwnerKind == OwnerKind.Suspended)
                {
                    if (_suspended.TryGetValue(key.Position.BodyId, out SuspendedFluidSnapshot record)) cell = record.State;
                    return WorldResult.Success();
                }
                if (key.Position.OwnerKind == OwnerKind.Body)
                {
                    bool exists = false;
                    foreach (BodySnapshot body in _bodySnapshots) if (body.BodyId == key.Position.BodyId) { exists = true; break; }
                    if (!exists) return Error(WorldErrorCode.InvalidArgument, "CandidateRead", "body", "材料体不属于候选目录。");
                    _bodyCells.TryGetValue(key.Position, out cell);
                    return WorldResult.Success();
                }
                WorldResult read = _candidate.Read(key.Position.X, key.Position.Y, out CellState state);
                cell = state.Snapshot;
                return read;
            }

            public bool IsFixed(in CellKey key) => CheckLease().IsSuccess && _owner.CheckKey(key).IsSuccess && _fixed.Contains(key.Position);

            internal void SetStructureBudget(StructurePlan plan)
            {
                _structure = plan;
                int bodies = 0;
                foreach (StructureComponent component in plan.Components)
                    if (component.Disposition != StructureDisposition.RetainFixedGrid) bodies++;
                Budget = new ResourceBudget(Budget.MaterialCells, bodies, Budget.StaticShapes, Budget.DynamicShapes,
                    Budget.ChangedPositions, Budget.CpuBytesIncludingPreparation);
            }

            public WorldResult Preflight(in TransactionContext context)
            {
                WorldResult access = _owner.CheckAccess();
                if (!access.IsSuccess) return access;
                if (State != PreparationState.Prepared || _owner._revision != _revision ||
                    !ReferenceEquals(_owner._pending, this) ||
                    !context.PublishedVersion.Equals(_context.PublishedVersion) || context.WorkingTick != _context.WorkingTick ||
                    context.Stage != _context.Stage || !context.Command.Equals(_context.Command))
                    return Error(WorldErrorCode.Busy, context.Stage.ToString(), "candidate", "候选资源或事务租约已失效。");
                WorldResult capacity = _owner._changes.Preflight(_positions, _owner.Config.Limits.MaxChangesPerTick);
                if (!capacity.IsSuccess) return capacity;
                try
                {
                    if (_recordedChanges == null || _recordedBaseCount != _owner._changes.Count)
                    {
                        _recordedChanges = _owner._changes.PrepareRecord(_positions);
                        _recordedBaseCount = _owner._changes.Count;
                    }
                    _nextRevision = checked(_owner._revision + 1);
                    PrepareInputRevisions();
                    if (_ownershipPrepared)
                        foreach (IMaterialBodyStorage body in _ownedBodies.Values)
                            if (body.IsDisposed) return Error(WorldErrorCode.NotReady, "Preflight", "body", "候选体存储已被提前释放。");
                    return WorldResult.Success();
                }
                catch (Exception exception)
                {
                    return Error(WorldErrorCode.CapacityExceeded, "Preflight", "allocation", exception.Message);
                }
            }

            public WorldResult Apply(in TransactionContext context)
            {
                WorldResult preflight = Preflight(context);
                if (!preflight.IsSuccess) return preflight;
                if (!_ownershipPrepared && _owner._bodies.Count != 0)
                    return Error(WorldErrorCode.NotReady, "Apply", "ownership", "动态材料编辑必须先完成统一归属准备。");
                ChunkStore old = _owner._chunks;
                SortedDictionary<ulong, IMaterialBodyStorage> oldBodies = _ownershipPrepared ? _owner._bodies : null;
                _owner._chunks = _candidate;
                _candidate = null;
                _owner._occupied = _occupied;
                _owner._fixed = _fixed;
                _owner._suspended = _suspended;
                _owner._suspendedSnapshots = _suspendedSnapshots;
                _suspended = null;
                _suspendedSnapshots = null;
                _owner._instances.Adopt(_instances);
                _instances = null;
                _owner._changes.Adopt(_recordedChanges);
                if (_ownershipPrepared)
                {
                    _owner._bodies = _ownedBodies;
                    _ownedBodies = null;
                    _owner._bodySnapshots = _bodySnapshots;
                }
                _owner._orderedKeys = _orderedKeys;
                _owner._staticShapes = Budget.StaticShapes;
                _owner._dynamicShapes = Budget.DynamicShapes;
                _owner._revision = _nextRevision;
                _owner._materialRevision = _nextMaterialRevision;
                _owner._geometryRevision = _nextGeometryRevision;
                _owner._ruleSources = _ruleSources;
                _ruleSources = null;
                _owner._pending = null;
                State = PreparationState.Applied;
                try
                {
                    old.Dispose();
                    if (oldBodies != null) foreach (IMaterialBodyStorage body in oldBodies.Values) body.Dispose();
                }
                catch (Exception exception)
                {
                    _owner.Fault();
                    return Error(WorldErrorCode.Faulted, "Apply", "release", exception.Message);
                }
                return WorldResult.Success();
            }

            public void MarkCommitted(WorldVersion version)
            {
                if (State != PreparationState.Applied || !version.Equals(_owner.Published.Version) || version.CommittedTick != _context.WorkingTick)
                    throw new InvalidOperationException("候选只能在完整 Tick 发布后标记提交。");
                State = PreparationState.Committed;
            }

            public void Abort()
            {
                if (State == PreparationState.Committed || State == PreparationState.Aborted) return;
                _candidate?.Dispose();
                _candidate = null;
                _instances?.Close();
                _instances = null;
                if (_ownedBodies != null) foreach (IMaterialBodyStorage body in _ownedBodies.Values) body.Dispose();
                _ownedBodies = null;
                _suspended = null;
                _suspendedSnapshots = null;
                if (ReferenceEquals(_owner._pending, this)) _owner._pending = null;
                State = PreparationState.Aborted;
            }
            public void Dispose() { if (State != PreparationState.Committed) Abort(); }
        }
    }
}
