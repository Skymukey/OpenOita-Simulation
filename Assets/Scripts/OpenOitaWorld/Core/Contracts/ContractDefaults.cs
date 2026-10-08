using System;
using UnityEngine;

namespace OpenOita.Contracts
{
    // 只包含冻结合同的值判定，不实现规则/结构/物理算法。
    public static class ContractDefaults
    {
        public const int ChunkSize = 128;
        // 逻辑画布边长上限；材料及实际区块仍受独立容量和内存预算约束。
        public const int MaxWorldDimension = 16384;
        public const int MaxPendingCommands = 4096;
        public const int CompletedResultCapacity = 4096;
        public const int BoundaryShapes = 4;
        public const float ContactEpsilonFactor = 1e-4f;
        public const float PhysicsPenetrationFactor = 0.1f;
        // 当前关闭默认CPU内存预算拒绝；启用后恢复512 MiB。显式传入的小预算仍用于测试/宿主约束。
        public const bool CpuMemoryBudgetEnabled = false;
        public const long ConfiguredCpuBudgetBytes = 512L * 1024 * 1024;
        public const long CpuBudgetBytes = CpuMemoryBudgetEnabled ? ConfiguredCpuBudgetBytes : long.MaxValue;
        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);
        public static bool PreferLeft(ulong tick, int x, int y, uint seed) => ((tick & 1UL) ^ (uint)(x & 1) ^ (uint)(y & 1) ^ (seed & 1U)) == 0;
        public static bool RetainBodyId(OwnerKind originalOwner, int remainingComponents) => originalOwner == OwnerKind.Body && remainingComponents == 1;
        public static bool IsEffectiveContact(ContactFeature feature, float distance, float cellSize) =>
            feature == ContactFeature.PositiveAreaOverlap || ((feature == ContactFeature.EdgeEdge || feature == ContactFeature.VertexEdge) && distance <= ContactEpsilonFactor * cellSize);

        public static WorldResult Advance(ulong value, out ulong next)
        {
            next = value;
            if (value == ulong.MaxValue) return Error(WorldErrorCode.CapacityExceeded, "identity", "Tick/代次/身份溢出。");
            next = value + 1;
            return WorldResult.Success();
        }
        public static WorldResult ValidatePoint(Vector2 point, in WorldRect world)
        {
            if (!IsFinite(point)) return Error(WorldErrorCode.InvalidArgument, "point", "坐标必须有限。");
            return world.ContainsCenter(point) ? WorldResult.Success() : Error(WorldErrorCode.OutOfBounds, "point", "点超出左闭右开世界范围。");
        }
        public static WorldResult ValidateRegion(in WorldRect region, in WorldRect world)
        {
            if (!IsFinite(region.Min) || !IsFinite(region.Max) || region.Max.x <= region.Min.x || region.Max.y <= region.Min.y)
                return Error(WorldErrorCode.InvalidArgument, "region", "区域必须有限且为正面积。");
            return ContainsClosed(world, region.Min) && ContainsClosed(world, region.Max)
                ? WorldResult.Success() : Error(WorldErrorCode.OutOfBounds, "region", "区域不裁切。");
        }
        public static WorldResult ValidateSegment(Vector2 start, Vector2 end, in WorldRect world)
        {
            if (!IsFinite(start) || !IsFinite(end)) return Error(WorldErrorCode.InvalidArgument, "segment", "端点必须有限。");
            return ContainsClosed(world, start) && ContainsClosed(world, end)
                ? WorldResult.Success() : Error(WorldErrorCode.OutOfBounds, "segment", "线段不裁切。");
        }
        public static int CompareSegmentHits(in SegmentCellHit first, in SegmentCellHit second)
        {
            int result = first.FirstIntersectionT.CompareTo(second.FirstIntersectionT);
            return result == 0 ? first.Hit.Position.CompareTo(second.Hit.Position) : result;
        }
        public static int CompareMoveCandidates(in MutationIntent first, in MutationIntent second)
        {
            int result = first.Tier.CompareTo(second.Tier);
            if (result == 0) result = first.Target.Position.CompareTo(second.Target.Position);
            return result == 0 ? first.Source.Position.CompareTo(second.Source.Position) : result;
        }
        public static WorldResult CheckShapes(int staticShapes, ReadOnlySpan<int> dynamicShapes, WorldLimits limits)
        {
            if (staticShapes < 0) return Error(WorldErrorCode.InvalidArgument, "staticShapes", "形状数不能为负。");
            long total = staticShapes;
            foreach (int count in dynamicShapes)
            {
                if (count < 0) return Error(WorldErrorCode.InvalidArgument, "dynamicShapes", "形状数不能为负。");
                if (count > limits.MaxShapesPerBody) return Error(WorldErrorCode.CapacityExceeded, "body.shapes", "单动态体形状超限。");
                total += count;
            }
            return total <= limits.MaxTotalShapes ? WorldResult.Success() : Error(WorldErrorCode.CapacityExceeded, "totalShapes", "材料全局形状超限（边界四形状另计）。");
        }
        // D08：用 double 累计，不通过裁剪或整数转换掩盖超限。
        public static WorldResult EstimateSubsteps(Vector2 velocity, float angularVelocityRadians, float radius, WorldConfig config, out int substeps)
        {
            substeps = 0;
            if (!IsFinite(velocity) || !IsFinite(angularVelocityRadians) || !IsFinite(radius) || radius < 0)
                return Error(WorldErrorCode.InvalidArgument, "motion", "运动输入必须有限，半径不能为负。");
            double speed = Math.Sqrt((double)velocity.x * velocity.x + (double)velocity.y * velocity.y);
            float angularLimitRadians = (float)(config.Limits.MaxAngularSpeedDegrees * Math.PI / 180);
            if (speed > config.Limits.MaxLinearSpeed || Math.Abs(angularVelocityRadians) > angularLimitRadians)
                return Error(WorldErrorCode.Faulted, "motion", "预测速度超限。");
            double h = config.StepSeconds;
            double displacement = speed * h + Math.Abs((double)config.GravityY) * h * h + Math.Abs((double)angularVelocityRadians) * radius * h;
            double count = Math.Max(1, Math.Ceiling(displacement / (config.CellSize / 2.0)));
            if (double.IsNaN(count) || double.IsInfinity(count) || count > config.Limits.MaxPhysicsSubsteps)
                return Error(WorldErrorCode.Faulted, "substeps", "预测子步数超限。");
            substeps = (int)count;
            return WorldResult.Success();
        }

        private static bool ContainsClosed(WorldRect world, Vector2 point) => point.x >= world.Min.x && point.x <= world.Max.x && point.y >= world.Min.y && point.y <= world.Max.y;
        private static WorldResult Error(WorldErrorCode code, string target, string message) => WorldResult.Failure(code, new WorldDiagnostic("Contract", target, message));
    }
}
