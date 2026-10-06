using System;
using OpenOita.Contracts;

namespace OpenOita.Structure
{
    // 复用既有 M05 纯计算；所有世界写入/实例迁移和存储采用交回 M02 原候选。
    public sealed class MaterialMutationPreparer : IMaterialMutationPreparer
    {
        private readonly IMaterialRuntimeTable _materials;
        private readonly BodyExtractionPlanner _planner = new();
        public MaterialMutationPreparer(IMaterialRuntimeTable materials)
        {
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        }

        public PreparationResult<IPreparedMaterialMutation> Prepare(IPreparedMutation candidate, StructurePlan structure,
            BodyIdReservation reserveBodyIds, ITickChangeCounter changes, MaterialOwnershipPreparation prepareOwnership,
            in TransactionContext context, IFailureInjector failures)
        {
            if (candidate == null || structure == null || reserveBodyIds == null || changes == null || prepareOwnership == null)
                return Failure("缺少编辑候选、结构或M02受控服务。");
            WorldResult check = candidate.Preflight(context);
            if (!check.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(check);
            if (candidate.CandidateWorld == null || candidate.CandidateInstances == null) return Failure("材料候选必须提供正确实例租约。");
            int count = 0, dynamicBodies = 0;
            foreach (StructureComponent component in structure.Components)
            {
                if (component.Disposition != StructureDisposition.RetainFixedGrid) dynamicBodies++;
                if (component.Disposition == StructureDisposition.ExtractFreeGrid || component.Disposition == StructureDisposition.CreateChildBody) count++;
            }
            if (dynamicBodies > candidate.CandidateWorld.Config.Limits.MaxDynamicBodies)
                return new PreparationResult<IPreparedMaterialMutation>(WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("M05.Prepare", "maxDynamicBodies", "全部结果体超限，不能截断分量。")));
            var ids = new ulong[count];
            check = reserveBodyIds(count, ids);
            if (!check.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(check);
            check = _planner.Build(candidate.CandidateWorld, structure, _materials, ids, candidate.CandidateInstances,
                changes, candidate.CandidateWrites, context, out BodyExtractionPlan plan, failures);
            if (!check.IsSuccess) return new PreparationResult<IPreparedMaterialMutation>(check);
            using (plan)
            {
                var bodies = new IMaterialBodyStorage[plan.Bodies.Count];
                for (int i = 0; i < bodies.Length; i++) bodies[i] = plan.Bodies[i];
                var result = prepareOwnership(plan.Cells, plan.BodyMappings, plan.Geometry, bodies, plan.RetiredBodyIds, context);
                if (result.Result.IsSuccess) plan.ReleaseBodyOwnership();
                return result;
            }
        }

        private static PreparationResult<IPreparedMaterialMutation> Failure(string message) =>
            new PreparationResult<IPreparedMaterialMutation>(WorldResult.Failure(WorldErrorCode.InvalidArgument,
                new WorldDiagnostic("M05.Prepare", "inputs", message)));
    }
}
