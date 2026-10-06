using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Simulation
{
    internal sealed partial class WorkingWorld
    {
        internal PreparationResult<IPreparedMutation> PrepareInitial()
        {
            var context = new TransactionContext(Published.Version, 0, TickStage.Structure);
            return Prepare(ReadOnlySpan<CellWrite>.Empty, context);
        }

        // 接入已有材料准备合同；返回的材料参与者就是原编辑候选，不能将两者重复 Own。
        internal PreparationResult<IPreparedMaterialMutation> PrepareMaterial(IPreparedMutation candidate,
            StructurePlan structure, IMaterialMutationPreparer preparer, in TransactionContext context,
            IFailureInjector failures = null)
        {
            WorldResult access = CheckAccess();
            if (!access.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(access);
            if (candidate == null || !ReferenceEquals(candidate, _pending) || preparer == null ||
                structure == null || !ReferenceEquals(structure, _pending._structure))
                return new PreparationResult<IPreparedMaterialMutation>(Error(WorldErrorCode.InvalidArgument,
                    "MaterialPrepare", "candidate", "必须使用本世界当前候选及其完整结构计划。"));
            PreparedGridMutation edit = _pending;
            if (edit._ownershipPrepared)
                return new PreparationResult<IPreparedMaterialMutation>(Error(WorldErrorCode.Busy,
                    "MaterialPrepare", "candidate", "同一候选不能重复准备归属。"));
            PreparationResult<IPreparedMaterialMutation> result;
            try
            {
                WorldResult check = edit.Preflight(context);
                result = check.IsSuccess ? preparer.Prepare(edit, structure, ReserveBodyIds,
                    new CandidateChangeCounter(edit), edit.PrepareOwnership, context, failures)
                    : new PreparationResult<IPreparedMaterialMutation>(check);
                if (result.Result.IsSuccess && (!ReferenceEquals(result.Prepared, edit) || !edit._ownershipPrepared))
                    result = new PreparationResult<IPreparedMaterialMutation>(Error(WorldErrorCode.InvalidArgument,
                        "MaterialPrepare", "participant", "材料结果必须采用同一候选的受控归属准备。"));
            }
            catch (Exception exception)
            {
                result = new PreparationResult<IPreparedMaterialMutation>(Error(WorldErrorCode.Faulted,
                    "MaterialPrepare", "preparer", exception.GetType().Name + ": " + exception.Message));
            }
            if (!result.Result.IsSuccess)
            {
                edit.Abort();
                if (context.Stage != TickStage.Commands || result.Result.ErrorCode == WorldErrorCode.Faulted) Fault();
            }
            return result;
        }

        private sealed class CandidateChangeCounter : ITickChangeCounter
        {
            private readonly PreparedGridMutation _candidate;
            internal CandidateChangeCounter(PreparedGridMutation candidate) { _candidate = candidate; }
            public int Count { get { _candidate.RequireLease(); return _candidate._owner._changes.Count; } }
            public int ProjectedCount { get { _candidate.RequireLease(); return _candidate._owner._changes.ProjectedCount; } }
            public WorldResult Preflight(ReadOnlySpan<CellPositionKey> writes, int maxChangesPerTick)
            {
                WorldResult lease = _candidate.CheckLease();
                return lease.IsSuccess ? _candidate._owner._changes.Preflight(writes, maxChangesPerTick) : lease;
            }
            public void Record(ReadOnlySpan<CellPositionKey> writes) =>
                throw new InvalidOperationException("候选计数服务仅供预检，实际计数由M02统一采用。");
        }

        private sealed partial class PreparedGridMutation
        {
            private readonly CandidateInstanceView _instanceView;
            internal BodySnapshot[] _bodySnapshots;
            internal StructurePlan _structure;
            private PlannedCell[] _plannedCells = Array.Empty<PlannedCell>();
            private BodyIdMapping[] _mappings = Array.Empty<BodyIdMapping>();
            private IReadOnlyList<BodyGeometryPlan> _geometry = Array.Empty<BodyGeometryPlan>();
            internal SortedDictionary<ulong, IMaterialBodyStorage> _ownedBodies;
            internal bool _ownershipPrepared;
            private ulong _firstReserved;
            private ulong _lastReserved;
            private TickChangeCounter _recordedChanges;
            private int _recordedBaseCount = -1;
            private long _nextRevision;
            public ReadOnlySpan<PlannedCell> Cells { get { RequireLease(); return _plannedCells; } }
            public ReadOnlySpan<BodyIdMapping> BodyMappings { get { RequireLease(); return _mappings; } }
            public IReadOnlyList<BodyGeometryPlan> Geometry { get { RequireLease(); return _geometry; } }

            internal void TrackReservedIds(ulong first, ulong last)
            {
                if (_firstReserved == 0) _firstReserved = first;
                _lastReserved = last;
            }

            private PreparationResult<IPreparedMaterialMutation> OwnershipFailure(WorldErrorCode code, string message) =>
                new PreparationResult<IPreparedMaterialMutation>(Error(code, "OwnershipPrepare", "plan", message));

            internal PreparationResult<IPreparedMaterialMutation> PrepareOwnership(ReadOnlySpan<PlannedCell> cells,
                ReadOnlySpan<BodyIdMapping> mappings, IReadOnlyList<BodyGeometryPlan> geometry,
                IReadOnlyList<IMaterialBodyStorage> bodies, ReadOnlySpan<ulong> retiredBodyIds,
                in TransactionContext context)
            {
                WorldResult check = Preflight(context);
                if (!check.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(check);
                if (_ownershipPrepared || _structure == null || geometry == null || bodies == null)
                    return OwnershipFailure(WorldErrorCode.InvalidArgument, "归属只准备一次，须具备全部结构计划、几何及存储。");
                ChunkStore chunks = null;
                TickInstanceMap instances = null;
                try
                {
                    if (retiredBodyIds.Length != _structure.RetiredBodyIds.Count)
                        return OwnershipFailure(WorldErrorCode.InvalidArgument, "撤销清单与当前结构计划不一致。");
                    for (int i = 0; i < retiredBodyIds.Length; i++)
                        if (retiredBodyIds[i] != _structure.RetiredBodyIds[i])
                            return OwnershipFailure(WorldErrorCode.InvalidArgument, "撤销清单与当前结构计划不一致。");
                    var sources = new Dictionary<CellKey, PlannedCell>();
                    var targets = new Dictionary<CellPositionKey, PlannedCell>();
                    foreach (PlannedCell cell in cells)
                    {
                        if (cell.Source.Generation != _owner.Generation || cell.Target.Generation != _owner.Generation)
                            return OwnershipFailure(WorldErrorCode.StaleGeneration, "归属格代次不一致。");
                        if (!Read(cell.Source, out CellSnapshot state).IsSuccess || state.MaterialId == 0 ||
                            !SameState(state, cell.State) || !_instances.TryGetInstance(cell.Source, out CellInstanceHandle instance) ||
                            !instance.Equals(cell.Instance) || sources.ContainsKey(cell.Source) || targets.ContainsKey(cell.Target.Position))
                            return OwnershipFailure(WorldErrorCode.InvalidArgument, "源状态、候选实例、源/目标唯一性无效。");
                        sources.Add(cell.Source, cell);
                        targets.Add(cell.Target.Position, cell);
                    }
                    int expectedCells = 0;
                    foreach (CellKey key in _orderedKeys)
                    {
                        Read(key, out CellSnapshot state);
                        _owner._materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material);
                        if ((material.Rules & RuleMask.Structure) != 0)
                        {
                            expectedCells++;
                            if (!sources.ContainsKey(key)) return OwnershipFailure(WorldErrorCode.InvalidArgument, "归属结果遗漏结构材料。");
                        }
                    }
                    if (expectedCells != cells.Length || geometry.Count != _structure.Components.Count)
                        return OwnershipFailure(WorldErrorCode.InvalidArgument, "归属结果必须恰好覆盖全部结构分量。");

                    var expectedMappings = new List<BodyIdMapping>();
                    var usedBodyIds = new HashSet<ulong>();
                    ulong previousNewId = 0;
                    for (int i = 0; i < _structure.Components.Count; i++)
                    {
                        StructureComponent component = _structure.Components[i];
                        BodyGeometryPlan item = geometry[i];
                        if (item == null || item.Cells.Count != component.Members.Count || item.Rectangles.Count == 0 ||
                            !ContractDefaults.IsFinite(item.Pose.Position) || !ContractDefaults.IsFinite(item.Pose.AngleRadians) ||
                            !ContractDefaults.IsFinite(item.Motion.LinearVelocity) || !ContractDefaults.IsFinite(item.Motion.AngularVelocityRadians) ||
                            !ContractDefaults.IsFinite(item.LocalCenterOfMass) || !ContractDefaults.IsFinite(item.Mass) || item.Mass <= 0 ||
                            !ContractDefaults.IsFinite(item.Inertia) || item.Inertia <= 0 || !ContractDefaults.IsFinite(item.BoundingRadius) || item.BoundingRadius <= 0)
                            return OwnershipFailure(WorldErrorCode.InvalidArgument, "几何与分量不一致或包含无效质量/运动。");
                        bool fixedGrid = component.Disposition == StructureDisposition.RetainFixedGrid;
                        bool retain = component.Disposition == StructureDisposition.RetainBody;
                        ulong id = item.Owner.BodyId;
                        if (fixedGrid)
                        {
                            if (item.Owner.OwnerKind != OwnerKind.Grid || id != 0)
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "固定分量必须保留主网格。");
                        }
                        else
                        {
                            if (item.Owner.OwnerKind != OwnerKind.Body || id == 0 || !usedBodyIds.Add(id))
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "每个动态结果必须具有唯一非零ID。");
                            if (retain)
                            {
                                if (id != component.OriginalMinimum.BodyId)
                                    return OwnershipFailure(WorldErrorCode.InvalidArgument, "单分量必须保留原BodyId。");
                            }
                            else if (_firstReserved == 0 || id < _firstReserved || id > _lastReserved || id <= previousNewId ||
                                _owner._bodies.ContainsKey(id))
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "新ID必须来自当前候选预留并按D03消费。");
                            else previousNewId = id;
                            expectedMappings.Add(new BodyIdMapping(component.OriginalMinimum.BodyId, id));
                        }
                        var covered = new HashSet<CellPositionKey>();
                        for (int c = 0; c < component.Members.Count; c++)
                        {
                            if (!sources.TryGetValue(component.Members[c], out PlannedCell cell) ||
                                !SamePlannedCell(cell, item.Cells[c]) || cell.Target.Position.OwnerKind != item.Owner.OwnerKind ||
                                cell.Target.Position.BodyId != id || !covered.Add(cell.Target.Position))
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "几何与源/目标归属映射不一致。");
                            if ((fixedGrid || retain) && !cell.Source.Equals(cell.Target))
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "保留分量不得改变原局部坐标。");
                        }
                        var rectangles = new HashSet<CellPositionKey>();
                        foreach (CellRectangle rectangle in item.Rectangles)
                        {
                            long width = (long)rectangle.MaxExclusive.x - rectangle.Min.x;
                            long height = (long)rectangle.MaxExclusive.y - rectangle.Min.y;
                            if (width <= 0 || height <= 0 || width > covered.Count || height > covered.Count || width * height > covered.Count)
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "矩形面积或边界无效。");
                            for (int y = rectangle.Min.y; y < rectangle.MaxExclusive.y; y++)
                                for (int x = rectangle.Min.x; x < rectangle.MaxExclusive.x; x++)
                                {
                                    var key = new CellPositionKey(item.Owner.OwnerKind, id, x, y);
                                    if (!covered.Contains(key) || !rectangles.Add(key))
                                        return OwnershipFailure(WorldErrorCode.InvalidArgument, "矩形填孔、重叠或覆盖额外格。");
                                }
                        }
                        if (rectangles.Count != covered.Count) return OwnershipFailure(WorldErrorCode.InvalidArgument, "矩形遗漏真实材料格。");
                    }
                    foreach (ulong retired in retiredBodyIds)
                    {
                        bool hasChildren = false;
                        foreach (BodyIdMapping mapping in expectedMappings) if (mapping.OldBodyId == retired) { hasChildren = true; break; }
                        if (!hasChildren) expectedMappings.Add(new BodyIdMapping(retired, 0));
                    }
                    expectedMappings.Sort((a, b) => a.OldBodyId == b.OldBodyId ? a.NewBodyId.CompareTo(b.NewBodyId) : a.OldBodyId.CompareTo(b.OldBodyId));
                    if (mappings.Length != expectedMappings.Count) return OwnershipFailure(WorldErrorCode.InvalidArgument, "旧新体映射不完整。");
                    for (int i = 0; i < mappings.Length; i++)
                        if (mappings[i].OldBodyId != expectedMappings[i].OldBodyId || mappings[i].NewBodyId != expectedMappings[i].NewBodyId)
                            return OwnershipFailure(WorldErrorCode.InvalidArgument, "旧新体映射与分量/撤销计划不一致。");

                    var ownedBodies = new SortedDictionary<ulong, IMaterialBodyStorage>();
                    foreach (IMaterialBodyStorage body in bodies)
                    {
                        if (body == null || body.IsDisposed) return OwnershipFailure(WorldErrorCode.InvalidArgument, "结果体存储已释放或缺失。");
                        BodySnapshot snapshot = body.Snapshot;
                        ulong id = snapshot.BodyId;
                        if (!usedBodyIds.Contains(id) || ownedBodies.ContainsKey(id))
                            return OwnershipFailure(WorldErrorCode.InvalidArgument, "结果体目录出现额外或重复ID。");
                        ulong version = _owner._bodies.TryGetValue(id, out IMaterialBodyStorage old) ? checked(old.Snapshot.GeometryVersion + 1) : 1;
                        BodyGeometryPlan bodyGeometry = null;
                        foreach (BodyGeometryPlan item in geometry)
                            if (item.Owner.OwnerKind == OwnerKind.Body && item.Owner.BodyId == id) { bodyGeometry = item; break; }
                        if (!ReferenceEquals(body.Geometry, bodyGeometry) || snapshot.GeometryVersion != version || bodyGeometry.GeometryVersion != version ||
                            !snapshot.Pose.Position.Equals(bodyGeometry.Pose.Position) || snapshot.Pose.AngleRadians != bodyGeometry.Pose.AngleRadians ||
                            !snapshot.Motion.LinearVelocity.Equals(bodyGeometry.Motion.LinearVelocity) ||
                            snapshot.Motion.AngularVelocityRadians != bodyGeometry.Motion.AngularVelocityRadians ||
                            !snapshot.LocalCenterOfMass.Equals(bodyGeometry.LocalCenterOfMass) || body.OccupiedPositions.Length != bodyGeometry.Cells.Count)
                            return OwnershipFailure(WorldErrorCode.InvalidArgument, "体存储的位姿、几何版本或占据与计划不一致。");
                        CellPositionKey previous = default;
                        bool first = true;
                        foreach (CellPositionKey position in body.OccupiedPositions)
                        {
                            if (position.OwnerKind != OwnerKind.Body || position.BodyId != id || (!first && previous.CompareTo(position) >= 0) ||
                                !targets.TryGetValue(position, out PlannedCell cell) || !body.TryRead(position, out CellSnapshot state) || !SameState(state, cell.State))
                                return OwnershipFailure(WorldErrorCode.InvalidArgument, "体存储不能遗漏、重复或改变完整材料状态。");
                            previous = position;
                            first = false;
                        }
                        ownedBodies.Add(id, body);
                    }
                    if (ownedBodies.Count != usedBodyIds.Count || ownedBodies.Count > _owner.Config.Limits.MaxDynamicBodies)
                        return OwnershipFailure(WorldErrorCode.CapacityExceeded, "结果体不完整或超出容量。");
                    int staticShapes = 0, dynamicShapes = 0;
                    foreach (BodyGeometryPlan item in geometry)
                    {
                        if (item.Owner.OwnerKind == OwnerKind.Grid) staticShapes = checked(staticShapes + item.Rectangles.Count);
                        else
                        {
                            if (item.Rectangles.Count > _owner.Config.Limits.MaxShapesPerBody)
                                return OwnershipFailure(WorldErrorCode.CapacityExceeded, "单体形状超限。");
                            dynamicShapes = checked(dynamicShapes + item.Rectangles.Count);
                        }
                    }
                    if ((long)staticShapes + dynamicShapes > _owner.Config.Limits.MaxTotalShapes)
                        return OwnershipFailure(WorldErrorCode.CapacityExceeded, "材料形状总量超限。");

                    chunks = _candidate.Clone();
                    instances = _instances.Clone();
                    var occupied = new SortedSet<CellPositionKey>(_occupied);
                    var fixedCells = new HashSet<CellPositionKey>(_fixed);
                    var bodyCells = new Dictionary<CellPositionKey, CellSnapshot>();
                    var writes = new SortedSet<CellPositionKey>(_positions);
                    foreach (PlannedCell cell in cells)
                    {
                        occupied.Remove(cell.Source.Position);
                        if (!cell.Source.Equals(cell.Target))
                        {
                            instances.Move(cell.Instance, cell.Target);
                            fixedCells.Remove(cell.Source.Position);
                            if (cell.Source.Position.OwnerKind == OwnerKind.Grid)
                            {
                                WorldResult cleared = chunks.Write(cell.Source.Position.X, cell.Source.Position.Y, default);
                                if (!cleared.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(cleared);
                            }
                            if (_context.WorkingTick != 0) { writes.Add(cell.Source.Position); writes.Add(cell.Target.Position); }
                        }
                        if (cell.Target.Position.OwnerKind == OwnerKind.Body) bodyCells.Add(cell.Target.Position, cell.State);
                    }
                    foreach (PlannedCell cell in cells) occupied.Add(cell.Target.Position);
                    var positions = new CellPositionKey[writes.Count];
                    writes.CopyTo(positions);
                    WorldResult capacity = _owner._changes.Preflight(positions, _owner.Config.Limits.MaxChangesPerTick);
                    if (!capacity.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(capacity);
                    int materialCount = occupied.Count + _suspended.Count;
                    long cpuBytes = checked(_owner.EstimateBytes(chunks.ChunkCount, materialCount, true) +
                        cells.Length * 384L + geometry.Count * 512L + positions.Length * 128L + (staticShapes + dynamicShapes) * 128L);
                    if (materialCount > _owner.Config.Limits.MaxMaterialCells || cpuBytes > _owner._memoryLimit)
                        return OwnershipFailure(WorldErrorCode.CapacityExceeded, "完整候选材料或保留/准备工作集超限。");
                    var keys = new CellKey[occupied.Count];
                    int index = 0;
                    foreach (CellPositionKey position in occupied) keys[index++] = new CellKey(_owner.Generation, position);
                    var snapshots = new BodySnapshot[ownedBodies.Count];
                    index = 0;
                    foreach (IMaterialBodyStorage body in ownedBodies.Values) snapshots[index++] = body.Snapshot;
                    PlannedCell[] ownedCells = cells.ToArray();
                    BodyIdMapping[] ownedMappings = mappings.ToArray();
                    var ownedGeometry = new List<BodyGeometryPlan>(geometry).AsReadOnly();
                    var recorded = _owner._changes.PrepareRecord(positions);
                    ChunkStore oldChunks = _candidate;
                    TickInstanceMap oldInstances = _instances;
                    _candidate = chunks;
                    chunks = null;
                    _instances = instances;
                    instances = null;
                    _occupied = occupied;
                    _fixed = fixedCells;
                    _bodyCells = bodyCells;
                    _positions = positions;
                    _orderedKeys = keys;
                    _bodySnapshots = snapshots;
                    _plannedCells = ownedCells;
                    _mappings = ownedMappings;
                    _geometry = ownedGeometry;
                    _recordedChanges = recorded;
                    _recordedBaseCount = _owner._changes.Count;
                    _ownedBodies = ownedBodies;
                    _ownershipPrepared = true;
                    Budget = new ResourceBudget(materialCount, ownedBodies.Count, staticShapes, dynamicShapes, recorded.Count, cpuBytes);
                    oldChunks.Dispose();
                    oldInstances.Close();
                    return new PreparationResult<IPreparedMaterialMutation>(WorldResult.Success(), this);
                }
                catch (OverflowException) { return OwnershipFailure(WorldErrorCode.CapacityExceeded, "版本或容量运算溢出。"); }
                catch (Exception exception) { return OwnershipFailure(WorldErrorCode.InvalidArgument, exception.GetType().Name + ": " + exception.Message); }
                finally { chunks?.Dispose(); instances?.Close(); }
            }

            private static bool SamePlannedCell(in PlannedCell a, in PlannedCell b) =>
                a.Source.Equals(b.Source) && a.Target.Equals(b.Target) && a.Instance.Equals(b.Instance) && SameState(a.State, b.State);

            private sealed class CandidateInstanceView : ITickInstanceMap
            {
                private readonly PreparedGridMutation _candidate;
                internal CandidateInstanceView(PreparedGridMutation candidate) { _candidate = candidate; }
                public bool TryGetInstance(in CellKey key, out CellInstanceHandle instance)
                {
                    instance = default;
                    return _candidate.CheckLease().IsSuccess && _candidate._instances.TryGetInstance(key, out instance);
                }
                public bool TryResolve(in CellInstanceHandle instance, out CellKey key)
                {
                    key = default;
                    return _candidate.CheckLease().IsSuccess && _candidate._instances.TryResolve(instance, out key);
                }
                public bool IsWet(in CellInstanceHandle instance) =>
                    _candidate.CheckLease().IsSuccess && _candidate._instances.IsWet(instance);
                public void MarkWet(in CellInstanceHandle instance) => throw ReadOnly();
                public void Move(in CellInstanceHandle instance, in CellKey target) => throw ReadOnly();
                public void Invalidate(in CellInstanceHandle instance) => throw ReadOnly();
                public CellInstanceHandle Create(in CellKey key) => throw ReadOnly();
                private static InvalidOperationException ReadOnly() => new InvalidOperationException("候选实例租约只允许查询，修改由M02统一准备与采用。");
            }
        }
    }
}
