using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using OpenOita.Simulation.Bodies;
using UnityEngine;

namespace OpenOita.Structure
{
    internal sealed class BodyExtractionPlanner
    {
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private bool _building;

        // IDs 必须由 M02 统一预留。本方法只读实例/候选，绝不 Reserve/Create/Move/Record。
        // candidateWrites 是 M02 材料候选原有写入集，合并归属迁移后交同一个整 Tick 计数器预检。
        internal WorldResult Build(IWorkingWorldView world, StructurePlan structure, IMaterialRuntimeTable materials,
            ReadOnlySpan<ulong> reservedIds, ITickInstanceMap instances, ITickChangeCounter changes,
            ReadOnlySpan<CellPositionKey> candidateWrites, in TransactionContext context,
            out BodyExtractionPlan plan, IFailureInjector failures = null)
        {
            plan = null;
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
                return Error(context, WorldErrorCode.InvalidArgument, "thread", "必须在所属线程准备材料体。");
            if (_building) return Error(context, WorldErrorCode.Busy, "planner", "不能重入材料体准备。");
            if (world == null || structure == null || materials == null || instances == null || changes == null)
                return Error(context, WorldErrorCode.InvalidArgument, "inputs", "缺少候选、结构、材料或 M02 实例/计数服务。");
            _building = true;
            var bodies = new List<MaterialBody>();
            bool transferred = false;
            try
            {
                WorldConfig config = world.Config;
                if (config == null || world.Generation == 0 || !ContractDefaults.IsFinite(world.Origin) ||
                    !ContractDefaults.IsFinite(config.CellSize) || config.CellSize <= 0)
                    return Error(context, WorldErrorCode.InvalidArgument, "candidate", "候选配置或坐标无效。");
                if (world.Generation != context.PublishedVersion.Generation)
                    return Error(context, WorldErrorCode.StaleGeneration, "generation", "候选与事务代次不一致。");
                if (world.WorkingTick != context.WorkingTick ||
                    (context.WorkingTick != 0 && (context.PublishedVersion.CommittedTick == ulong.MaxValue ||
                    context.WorkingTick != context.PublishedVersion.CommittedTick + 1)) ||
                    (context.WorkingTick == 0 && context.PublishedVersion.CommittedTick != 0))
                    return Error(context, WorldErrorCode.InvalidArgument, "workingTick", "候选与事务 Tick 不一致。");
                if (context.WorkingTick == 0 && candidateWrites.Length != 0)
                    return Error(context, WorldErrorCode.InvalidArgument, "candidateWrites", "初态建构不接受 Tick 写入集。");

                ReadOnlySpan<CellKey> occupied = world.OccupiedCells;
                if (occupied.Length > config.Limits.MaxMaterialCells ||
                    structure.Components.Count > occupied.Length ||
                    (long)candidateWrites.Length > 2L * config.Limits.MaxMaterialCells)
                    return Error(context, WorldErrorCode.CapacityExceeded, "inputs", "候选及准备集合超过有界工作规模。");
                // 估计含读缓存、两份状态/计划、稀疏矩形集合、复制峰值及候选体；非实测内存。
                long cpuBytes = checked(4096L + occupied.Length * 1024L +
                    structure.Components.Count * 512L + candidateWrites.Length * 128L);
                if (cpuBytes > ContractDefaults.CpuBudgetBytes)
                    return Error(context, WorldErrorCode.CapacityExceeded, "cpuBytes", "M05 候选工作集估计超限。");

                var originals = new Dictionary<ulong, BodySnapshot>();
                ulong highestOriginalId = 0;
                if (world.Bodies.Length > config.Limits.MaxDynamicBodies)
                    return Error(context, WorldErrorCode.CapacityExceeded, "Bodies", "原体目录超过配置容量。");
                foreach (BodySnapshot body in world.Bodies)
                {
                    if (body.BodyId == 0 || originals.ContainsKey(body.BodyId) || body.GeometryVersion == 0 ||
                        !Finite(body.Pose, body.Motion) || !ContractDefaults.IsFinite(body.LocalCenterOfMass))
                        return Error(context, WorldErrorCode.InvalidArgument, "Bodies", "原体目录身份、质量中心或运动无效。");
                    originals.Add(body.BodyId, body);
                    highestOriginalId = Math.Max(highestOriginalId, body.BodyId);
                }
                foreach (ulong id in structure.RetiredBodyIds)
                    if (!originals.ContainsKey(id))
                        return Error(context, WorldErrorCode.InvalidArgument, "retiredBodyIds", "撤销体不属于原体目录。");

                int newCount = 0, dynamicCount = 0;
                foreach (StructureComponent component in structure.Components)
                {
                    if (component.Disposition != StructureDisposition.RetainFixedGrid) dynamicCount++;
                    if (component.Disposition == StructureDisposition.ExtractFreeGrid ||
                        component.Disposition == StructureDisposition.CreateChildBody) newCount++;
                }
                if (dynamicCount > config.Limits.MaxDynamicBodies)
                    return Error(context, WorldErrorCode.CapacityExceeded, "maxDynamicBodies", "全部结果体超限，不能截断分量。");
                if (reservedIds.Length != newCount)
                    return Error(context, WorldErrorCode.InvalidArgument, "reservedIds", "预留 ID 数必须等于全部新体数。");
                ulong previousId = highestOriginalId;
                foreach (ulong id in reservedIds)
                {
                    if (id == 0 || id <= previousId)
                        return Error(context, WorldErrorCode.InvalidArgument, "reservedIds", "新体 ID 必须为已统一预留、递增且不与原体相同的号段。");
                    previousId = id;
                }

                var states = new Dictionary<CellKey, CellSnapshot>();
                var expectedStructure = new HashSet<CellKey>();
                foreach (CellKey key in occupied)
                {
                    WorldResult valid = ValidateKey(world, key, originals, context);
                    if (!valid.IsSuccess) return valid;
                    if (states.ContainsKey(key))
                        return Error(context, WorldErrorCode.InvalidArgument, "OccupiedCells", "候选占据集合包含重复格。");
                    WorldResult read = world.Read(key, out CellSnapshot state);
                    if (!read.IsSuccess) return read;
                    if (state.MaterialId == 0)
                        return Error(context, WorldErrorCode.InvalidArgument, "OccupiedCells", "占据集合包含空格。");
                    if (!materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material))
                        return Error(context, WorldErrorCode.UnknownMaterial, "MaterialId", "候选材料未注册。");
                    bool solid = material.Kind == MaterialKind.Solid && (material.Rules & RuleMask.Structure) != 0;
                    if (key.Position.OwnerKind == OwnerKind.Body && !solid)
                        return Error(context, WorldErrorCode.UnsupportedOperation, "body.material", "动态材料体不支持非结构固体替换。");
                    states.Add(key, state);
                    if (solid) expectedStructure.Add(key);
                }

                var allCells = new List<PlannedCell>();
                var geometry = new List<BodyGeometryPlan>();
                var mappings = new List<BodyIdMapping>();
                var writes = new SortedSet<CellPositionKey>();
                foreach (CellPositionKey write in candidateWrites) writes.Add(write);
                var covered = new HashSet<CellKey>();
                var seenInstances = new HashSet<CellInstanceHandle>();
                int staticShapes = 0, dynamicShapes = 0, reservedIndex = 0;
                foreach (StructureComponent component in structure.Components)
                {
                    bool isGrid = component.OriginalMinimum.OwnerKind == OwnerKind.Grid;
                    bool fixedGrid = component.Disposition == StructureDisposition.RetainFixedGrid;
                    bool retain = component.Disposition == StructureDisposition.RetainBody;
                    BodySnapshot oldBody = default;
                    if (!isGrid && !originals.TryGetValue(component.OriginalMinimum.BodyId, out oldBody))
                        return Error(context, WorldErrorCode.InvalidArgument, "component.owner", "分量原体不存在。");
                    ulong id = fixedGrid ? 0 : retain ? oldBody.BodyId : reservedIds[reservedIndex++];
                    int minX = int.MaxValue, minY = int.MaxValue;
                    foreach (CellKey key in component.Members)
                    {
                        minX = Math.Min(minX, key.Position.X);
                        minY = Math.Min(minY, key.Position.Y);
                    }
                    // 保留 BodyId 时保留局部原点与坐标；新体以占据包围范围最小角重定原点。
                    int offsetX = fixedGrid || retain ? 0 : minX;
                    int offsetY = fixedGrid || retain ? 0 : minY;
                    BodyPose sourcePose = isGrid ? new BodyPose(world.Origin, 0) : oldBody.Pose;
                    BodyPose pose = fixedGrid || retain ? sourcePose :
                        new BodyPose(MassPropertiesCalculator.Transform(sourcePose,
                            new Vector2((float)((double)offsetX * config.CellSize),
                                (float)((double)offsetY * config.CellSize))), sourcePose.AngleRadians);
                    var cells = new PlannedCell[component.Members.Count];
                    for (int i = 0; i < cells.Length; i++)
                    {
                        CellKey source = component.Members[i];
                        if (!expectedStructure.Contains(source) || !covered.Add(source))
                            return Error(context, WorldErrorCode.InvalidArgument, "component.members", "分量引用非结构格或重复归属。");
                        CellSnapshot state = states[source];
                        materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material);
                        if (!string.Equals(component.ConnectionGroup, material.Parameters.ConnectionGroup, StringComparison.Ordinal))
                            return Error(context, WorldErrorCode.InvalidArgument, "connectionGroup", "分量连接组与候选材料不一致。");
                        if (!instances.TryGetInstance(source, out CellInstanceHandle instance) ||
                            instance.Generation != world.Generation || instance.WorkingTick != world.WorkingTick ||
                            instance.Sequence == 0 || !instances.TryResolve(instance, out CellKey resolved) ||
                            !resolved.Equals(source) || !seenInstances.Add(instance))
                            return Error(context, WorldErrorCode.NotReady, "candidate.instances", "候选实例租约缺失或不唯一，须由 M02 提供候选实例视图。");
                        int x = checked(source.Position.X - offsetX);
                        int y = checked(source.Position.Y - offsetY);
                        var target = new CellKey(world.Generation,
                            new CellPositionKey(fixedGrid ? OwnerKind.Grid : OwnerKind.Body, id, x, y));
                        cells[i] = new PlannedCell(source, target, instance, state);
                        allCells.Add(cells[i]);
                        if (context.WorkingTick != 0 && !source.Equals(target))
                        {
                            writes.Add(source.Position);
                            writes.Add(target.Position);
                        }
                    }
                    WorldResult rectanglesResult = RectangleGeometryBuilder.Build(cells, out CellRectangle[] rectangles);
                    if (!rectanglesResult.IsSuccess) return rectanglesResult;
                    WorldResult phaseFailure = failures?.Check(context, FailurePoint.AfterRectanglesPrepared) ?? WorldResult.Success();
                    if (!phaseFailure.IsSuccess) return phaseFailure;
                    if (!fixedGrid && rectangles.Length > config.Limits.MaxShapesPerBody)
                        return Error(context, WorldErrorCode.CapacityExceeded, "maxShapesPerBody", "单体真实矩形超限，不填孔或降级。");
                    if (fixedGrid) staticShapes = checked(staticShapes + rectangles.Length);
                    else dynamicShapes = checked(dynamicShapes + rectangles.Length);
                    if ((long)staticShapes + dynamicShapes > config.Limits.MaxTotalShapes)
                        return Error(context, WorldErrorCode.CapacityExceeded, "maxTotalShapes", "静态及动态材料形状总量超限。");
                    WorldResult massResult = MassPropertiesCalculator.Calculate(cells, materials, config.CellSize,
                        out float mass, out Vector2 center, out float inertia, out float radius);
                    if (!massResult.IsSuccess) return massResult;
                    phaseFailure = failures?.Check(context, FailurePoint.AfterMassPrepared) ?? WorldResult.Success();
                    if (!phaseFailure.IsSuccess) return phaseFailure;
                    BodyMotion motion = isGrid ? default : MassPropertiesCalculator.Inherit(oldBody, pose, center);
                    if (!Finite(pose, motion))
                        return Error(context, WorldErrorCode.CapacityExceeded, "motion", "派生位姿或速度不能以有限数表示。");
                    ulong geometryVersion = retain ? checked(oldBody.GeometryVersion + 1) : 1;
                    var item = new BodyGeometryPlan(new CellPositionKey(fixedGrid ? OwnerKind.Grid : OwnerKind.Body, id, 0, 0),
                        pose, motion, center, mass, inertia, radius, geometryVersion, cells, rectangles);
                    geometry.Add(item);
                    if (!fixedGrid)
                    {
                        bodies.Add(new MaterialBody(item, geometryVersion));
                        mappings.Add(new BodyIdMapping(isGrid ? 0 : oldBody.BodyId, id));
                    }
                    phaseFailure = failures?.Check(context, FailurePoint.AfterBodyPrepared) ?? WorldResult.Success();
                    if (!phaseFailure.IsSuccess) return phaseFailure;
                }
                if (covered.Count != expectedStructure.Count)
                    return Error(context, WorldErrorCode.InvalidArgument, "structure.coverage", "结构计划遗漏候选结构格。");
                // 空原体必须出现在撤销目录；拆分体的映射已由全部子体表示，不额外制造格移除。
                foreach (BodySnapshot original in world.Bodies)
                {
                    bool found = false;
                    foreach (StructureComponent component in structure.Components)
                        if (component.OriginalMinimum.OwnerKind == OwnerKind.Body &&
                            component.OriginalMinimum.BodyId == original.BodyId) { found = true; break; }
                    if (!found)
                    {
                        bool retired = false;
                        foreach (ulong retiredId in structure.RetiredBodyIds)
                            if (retiredId == original.BodyId) { retired = true; break; }
                        if (!retired)
                            return Error(context, WorldErrorCode.InvalidArgument, "retiredBodyIds", "空体必须列入完整撤销清单。");
                        mappings.Add(new BodyIdMapping(original.BodyId, 0));
                    }
                }
                mappings.Sort((a, b) => a.OldBodyId == b.OldBodyId ? a.NewBodyId.CompareTo(b.NewBodyId) : a.OldBodyId.CompareTo(b.OldBodyId));
                var writeArray = new CellPositionKey[writes.Count];
                writes.CopyTo(writeArray);
                WorldResult countResult = changes.Preflight(writeArray, config.Limits.MaxChangesPerTick);
                if (!countResult.IsSuccess) return countResult;
                var budget = new ResourceBudget(occupied.Length, dynamicCount, staticShapes, dynamicShapes, changes.ProjectedCount, cpuBytes);
                WorldResult injected = failures?.Check(context, FailurePoint.AfterMaterialPrepared) ?? WorldResult.Success();
                if (!injected.IsSuccess) return injected;
                var retiredArray = new ulong[structure.RetiredBodyIds.Count];
                for (int i = 0; i < retiredArray.Length; i++) retiredArray[i] = structure.RetiredBodyIds[i];
                plan = new BodyExtractionPlan(allCells.ToArray(), mappings.ToArray(), writeArray, geometry, bodies, retiredArray, budget);
                transferred = true;
                return WorldResult.Success();
            }
            catch (OverflowException)
            {
                return Error(context, WorldErrorCode.CapacityExceeded, "arithmetic", "坐标、版本或资源预算溢出。");
            }
            catch (InvalidOperationException exception)
            {
                return Error(context, WorldErrorCode.NotReady, "candidate.lease", exception.Message);
            }
            catch (Exception exception)
            {
                return Error(context, WorldErrorCode.Faulted, "preparation", exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (!transferred) foreach (MaterialBody body in bodies) body.Dispose();
                _building = false;
            }
        }

        private static WorldResult ValidateKey(IWorkingWorldView world, in CellKey key,
            Dictionary<ulong, BodySnapshot> originals, in TransactionContext context)
        {
            if (key.Generation != world.Generation)
                return Error(context, WorldErrorCode.StaleGeneration, "cell.generation", "候选格代次无效。");
            CellPositionKey position = key.Position;
            if (position.OwnerKind == OwnerKind.Grid && (position.BodyId != 0 || position.X < 0 || position.Y < 0 ||
                position.X >= world.Config.Width || position.Y >= world.Config.Height))
                return Error(context, WorldErrorCode.OutOfBounds, "grid.cell", "主网格格坐标越界。");
            if (position.OwnerKind == OwnerKind.Body && !originals.ContainsKey(position.BodyId))
                return Error(context, WorldErrorCode.InvalidArgument, "body.cell", "候选格引用未知原体。");
            return WorldResult.Success();
        }

        private static bool Finite(in BodyPose pose, in BodyMotion motion) =>
            ContractDefaults.IsFinite(pose.Position) && ContractDefaults.IsFinite(pose.AngleRadians) &&
            ContractDefaults.IsFinite(motion.LinearVelocity) && ContractDefaults.IsFinite(motion.AngularVelocityRadians);

        private static WorldResult Error(in TransactionContext context, WorldErrorCode code, string target, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("M05." + context.Stage, target, message));
    }
}
