using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenOita.Contracts
{
    public readonly struct TransactionContext
    {
        public readonly WorldVersion PublishedVersion;
        public readonly ulong WorkingTick;
        public readonly TickStage Stage;
        public readonly CommandToken Command;
        public TransactionContext(WorldVersion publishedVersion, ulong workingTick, TickStage stage, CommandToken command = default)
        {
            PublishedVersion = publishedVersion; WorkingTick = workingTick; Stage = stage; Command = command;
        }
    }
    public enum FailurePoint : byte { AfterStructurePrepared, AfterMaterialPrepared, AfterPhysicsPrepared, BeforeApply, DuringApply, AfterPhysicsSubstep, BeforePublish, AfterRectanglesPrepared, AfterMassPrepared, AfterBodyPrepared, AfterSpatialPrepared, AfterDisplayTilePrepared, BeforeFluidCapture, BeforeFluidRestoration }
    // M02 注入并统一调用；无注入器时正常路径不调用测试逻辑。失败保留阶段/目标/码。
    public interface IFailureInjector
    {
        WorldResult Check(in TransactionContext context, FailurePoint point);
    }
    public interface ITickInstanceMap
    {
        // 只查当前 Tick 已登记实例，不创建/复活实例；缺失、旧代次或已结束 Tick 返回 false/default。
        bool TryGetInstance(in CellKey key, out CellInstanceHandle instance);
        bool TryResolve(in CellInstanceHandle instance, out CellKey key);
        bool IsWet(in CellInstanceHandle instance);
        void MarkWet(in CellInstanceHandle instance);
        void Move(in CellInstanceHandle instance, in CellKey target);
        void Invalidate(in CellInstanceHandle instance);
        CellInstanceHandle Create(in CellKey key);
    }
    public interface ITickChangeCounter
    {
        int Count { get; }
        int ProjectedCount { get; }
        // M02 实现；同键重复写入不增数，同ID替换也写入。容量失败必须在修改前可观测。
        WorldResult Preflight(ReadOnlySpan<CellPositionKey> writes, int maxChangesPerTick);
        void Record(ReadOnlySpan<CellPositionKey> writes);
    }

    public enum StructureDisposition : byte
    {
        RetainFixedGrid = 0,
        ExtractFreeGrid = 1,
        RetainBody = 2,
        CreateChildBody = 3
    }

    public sealed class StructureComponent
    {
        public CellPositionKey OriginalMinimum { get; }
        public string ConnectionGroup { get; }
        public bool IsFixed { get; }
        public StructureDisposition Disposition { get; }
        public IReadOnlyList<CellKey> Members { get; }
        public StructureComponent(CellPositionKey originalMinimum, string connectionGroup, bool isFixed,
            StructureDisposition disposition, IEnumerable<CellKey> members)
        {
            if (disposition < StructureDisposition.RetainFixedGrid || disposition > StructureDisposition.CreateChildBody)
                throw new ArgumentOutOfRangeException(nameof(disposition));
            bool grid = originalMinimum.OwnerKind == OwnerKind.Grid;
            bool gridDisposition = disposition == StructureDisposition.RetainFixedGrid || disposition == StructureDisposition.ExtractFreeGrid;
            if (grid != gridDisposition || isFixed != (disposition == StructureDisposition.RetainFixedGrid))
                throw new ArgumentException("分类必须与原归属及有效固定结果一致。", nameof(disposition));
            if (string.IsNullOrEmpty(connectionGroup)) throw new ArgumentException("连接组不能为空。", nameof(connectionGroup));
            if (members == null) throw new ArgumentNullException(nameof(members));
            var ownedMembers = new List<CellKey>(members);
            if (ownedMembers.Count == 0) throw new ArgumentException("空体只能用撤销目录表示。", nameof(members));
            ulong generation = ownedMembers[0].Generation;
            for (int i = 0; i < ownedMembers.Count; i++)
            {
                CellKey key = ownedMembers[i];
                if (generation == 0 || key.Generation != generation || key.Position.OwnerKind != originalMinimum.OwnerKind ||
                    key.Position.BodyId != originalMinimum.BodyId || (i > 0 && ownedMembers[i - 1].Position.CompareTo(key.Position) >= 0))
                    throw new ArgumentException("成员必须同代次、同原归属且按原 y/x 严格递增。", nameof(members));
            }
            if (!originalMinimum.Equals(ownedMembers[0].Position))
                throw new ArgumentException("原最小键必须等于重定原点前的第一个成员键。", nameof(originalMinimum));
            OriginalMinimum = originalMinimum; ConnectionGroup = connectionGroup; IsFixed = isFixed;
            Disposition = disposition;
            Members = ownedMembers.AsReadOnly();
        }
    }
    public sealed class StructurePlan
    {
        public IReadOnlyList<StructureComponent> Components { get; }
        public IReadOnlyList<ulong> RetiredBodyIds { get; }
        public StructurePlan(IEnumerable<StructureComponent> components, IEnumerable<ulong> retiredBodyIds = null)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));
            var ownedComponents = new List<StructureComponent>(components);
            var retired = new SortedSet<ulong>(retiredBodyIds ?? Array.Empty<ulong>());
            if (retired.Contains(0)) throw new ArgumentException("撤销目录只能包含非零动态体 ID。", nameof(retiredBodyIds));
            var bodyCounts = new Dictionary<ulong, int>();
            ulong generation = 0;
            for (int i = 0; i < ownedComponents.Count; i++)
            {
                StructureComponent component = ownedComponents[i];
                if (component == null) throw new ArgumentException("分量不能为 null。", nameof(components));
                if (i > 0 && ownedComponents[i - 1].OriginalMinimum.CompareTo(component.OriginalMinimum) >= 0)
                    throw new ArgumentException("分量必须按原归属及最小 y/x 严格递增。", nameof(components));
                if (generation == 0) generation = component.Members[0].Generation;
                if (component.Members[0].Generation != generation)
                    throw new ArgumentException("计划不能混合代次。", nameof(components));
                if (component.OriginalMinimum.OwnerKind == OwnerKind.Body)
                {
                    ulong id = component.OriginalMinimum.BodyId;
                    bodyCounts.TryGetValue(id, out int count);
                    bodyCounts[id] = count + 1;
                }
            }
            foreach (StructureComponent component in ownedComponents)
            {
                if (component.OriginalMinimum.OwnerKind != OwnerKind.Body) continue;
                ulong id = component.OriginalMinimum.BodyId;
                bool split = bodyCounts[id] > 1;
                if (split != (component.Disposition == StructureDisposition.CreateChildBody) || split != retired.Contains(id))
                    throw new ArgumentException("旧体单分量必须保留；多分量必须全部创建子体并撤销旧体。", nameof(components));
            }
            Components = ownedComponents.AsReadOnly();
            RetiredBodyIds = new List<ulong>(retired).AsReadOnly();
        }
    }
    public readonly struct StructurePlanResult
    {
        public readonly WorldResult Result;
        public readonly StructurePlan Plan;
        public StructurePlanResult(WorldResult result, StructurePlan plan = null)
        {
            if (result.IsSuccess != (plan != null)) throw new ArgumentException("结构计划仅在完整成功时提供。");
            Result = result; Plan = plan;
        }
    }
    public interface IStructurePlanner
    {
        StructurePlanResult Plan(IWorkingWorldView world, IMaterialRuntimeTable materials, ReadOnlySpan<CellKey> changedCells);
    }

    public readonly struct PlannedCell
    {
        public readonly CellKey Source;
        public readonly CellKey Target;
        public readonly CellInstanceHandle Instance;
        public readonly CellSnapshot State;
        public PlannedCell(CellKey source, CellKey target, CellInstanceHandle instance, CellSnapshot state)
        {
            Source = source; Target = target; Instance = instance; State = state;
        }
    }
    public readonly struct CellRectangle
    {
        public readonly Vector2Int Min;
        public readonly Vector2Int MaxExclusive;
        public CellRectangle(Vector2Int min, Vector2Int maxExclusive) { Min = min; MaxExclusive = maxExclusive; }
    }
    // M05 产生不可变准备计划；局部几何原点=Pose.Position，与质心分离。
    public sealed class BodyGeometryPlan
    {
        public CellPositionKey Owner { get; }
        public BodyPose Pose { get; }
        public BodyMotion Motion { get; }
        public Vector2 LocalCenterOfMass { get; }
        public float Mass { get; }
        public float Inertia { get; }
        public float BoundingRadius { get; }
        public ulong GeometryVersion { get; }
        public IReadOnlyList<PlannedCell> Cells { get; }
        public IReadOnlyList<CellRectangle> Rectangles { get; }
        public BodyGeometryPlan(CellPositionKey owner, BodyPose pose, BodyMotion motion, Vector2 localCenterOfMass,
            float mass, float inertia, float boundingRadius, ulong geometryVersion, IEnumerable<PlannedCell> cells, IEnumerable<CellRectangle> rectangles)
        {
            Owner = owner; Pose = pose; Motion = motion; LocalCenterOfMass = localCenterOfMass;
            Mass = mass; Inertia = inertia; BoundingRadius = boundingRadius;
            if (geometryVersion == 0) throw new ArgumentOutOfRangeException(nameof(geometryVersion));
            GeometryVersion = geometryVersion;
            Cells = new List<PlannedCell>(cells).AsReadOnly(); Rectangles = new List<CellRectangle>(rectangles).AsReadOnly();
        }
    }

    public readonly struct ResourceBudget
    {
        public readonly int MaterialCells;
        public readonly int DynamicBodies;
        public readonly int StaticShapes;
        public readonly int DynamicShapes;
        public readonly int ChangedPositions;
        public readonly long CpuBytesIncludingPreparation;
        public ResourceBudget(int materialCells, int dynamicBodies, int staticShapes, int dynamicShapes, int changedPositions, long cpuBytesIncludingPreparation)
        {
            MaterialCells = materialCells; DynamicBodies = dynamicBodies; StaticShapes = staticShapes;
            DynamicShapes = dynamicShapes; ChangedPositions = changedPositions; CpuBytesIncludingPreparation = cpuBytesIncludingPreparation;
        }
    }
    public enum PreparationState : byte { Prepared, Applied, Committed, Aborted }

    // 单一拥有者=M02 协调器。Preflight 不改工作状态，Apply 只写工作状态，MarkCommitted 只在全Tick发布之后。
    // Abort/Dispose 必须幂等，释放未移交资源；Applied 后不可恢复错误交 M02 Faulted，不能宣称回滚物理。
    public interface IPreparedMutation : IDisposable
    {
        PreparationState State { get; }
        ResourceBudget Budget { get; }
        // 材料候选返回编辑后只读视图；不提供材料候选的参与者返回 null。
        // 视图由本准备对象持有，仅 Prepared 租约内可读；Apply/Abort/Dispose 或世界故障后失效。
        IWorkingWorldView CandidateWorld { get; }
        // 编辑后的实例及唯一写入键，仅 Prepared 租约可读；非材料参与者为 null/空。
        // 实例表只允许查询，所有写入方法均拒绝；失效查询返回 false/default。
        ITickInstanceMap CandidateInstances { get; }
        ReadOnlySpan<CellPositionKey> CandidateWrites { get; }
        WorldResult Preflight(in TransactionContext context);
        WorldResult Apply(in TransactionContext context);
        void MarkCommitted(WorldVersion version);
        void Abort();
    }
    public interface IPreparedMaterialMutation : IPreparedMutation
    {
        ReadOnlySpan<PlannedCell> Cells { get; }
        ReadOnlySpan<BodyIdMapping> BodyMappings { get; }
        IReadOnlyList<BodyGeometryPlan> Geometry { get; }
    }
    public interface IPreparedPhysicsMutation : IPreparedMutation { }
    public readonly struct PreparationResult<T> where T : class, IPreparedMutation
    {
        public readonly WorldResult Result;
        public readonly T Prepared;
        public PreparationResult(WorldResult result, T prepared = null)
        {
            if (result.IsSuccess != (prepared != null)) throw new ArgumentException("准备失败必须清理资源且不返回可用句柄。");
            Result = result; Prepared = prepared;
        }
    }
    // M02 持有所属 generation 的唯一序列；M05 按 StructurePlan 顺序消费。
    // 仅写前 count 项；整段预留失败不写缓冲/不耗号，成功后即使准备失败也不回收。
    // count<0 为 InvalidArgument，缓冲不足为 BufferTooSmall，号段耗尽为 CapacityExceeded。
    // 回调必需且绑定同 generation 的世界，限所属线程；不增加公开 IWorld 操作。
    public delegate WorldResult BodyIdReservation(int count, Span<ulong> destination);

    // 绑定到 M02 当前材料候选。成功时体存储所有权移交给同一个候选，返回同一准备对象；
    // 失败时不修改候选，体存储仍由 M05 释放。不会 Apply 或发布。
    public delegate PreparationResult<IPreparedMaterialMutation> MaterialOwnershipPreparation(
        ReadOnlySpan<PlannedCell> cells, ReadOnlySpan<BodyIdMapping> mappings,
        IReadOnlyList<BodyGeometryPlan> geometry, IReadOnlyList<IMaterialBodyStorage> bodies,
        ReadOnlySpan<ulong> retiredBodyIds, in TransactionContext context);

    public interface IMaterialMutationPreparer
    {
        PreparationResult<IPreparedMaterialMutation> Prepare(IPreparedMutation candidate, StructurePlan structure,
            BodyIdReservation reserveBodyIds, ITickChangeCounter changes, MaterialOwnershipPreparation prepareOwnership,
            in TransactionContext context, IFailureInjector failures);
    }
    public interface IPhysicsMutationPreparer
    {
        PreparationResult<IPreparedPhysicsMutation> Prepare(IReadOnlyList<BodyGeometryPlan> geometry,
            ReadOnlySpan<BodyIdMapping> mappings, in ResourceBudget worldBudget, in TransactionContext context, IFailureInjector failures);
    }
}
