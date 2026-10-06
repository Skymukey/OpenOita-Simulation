using System;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Structure
{
    internal static class MassPropertiesCalculator
    {
        internal static WorldResult Calculate(ReadOnlySpan<PlannedCell> cells, IMaterialRuntimeTable materials,
            float cellSize, out float mass, out Vector2 center, out float inertia, out float radius)
        {
            mass = inertia = radius = 0;
            center = default;
            if (materials == null || cells.Length == 0 || !ContractDefaults.IsFinite(cellSize) || cellSize <= 0)
                return Error(WorldErrorCode.InvalidArgument, "质量计算需要非空结构格、材料表及有限正格边长。");
            // 以首格为数值参考点，避免大局部坐标下加权和相减丢失精度。
            double referenceX = ((double)cells[0].Target.Position.X + 0.5) * cellSize;
            double referenceY = ((double)cells[0].Target.Position.Y + 0.5) * cellSize;
            double total = 0, weightedX = 0, weightedY = 0;
            foreach (PlannedCell cell in cells)
            {
                if (!materials.TryGet(cell.State.MaterialId, out MaterialRuntimeEntry material))
                    return Error(WorldErrorCode.UnknownMaterial, "质量计算发现未注册材料。");
                if (material.Kind != MaterialKind.Solid || (material.Rules & RuleMask.Structure) == 0)
                    return Error(WorldErrorCode.UnsupportedOperation, "材料体只支持结构固体。");
                double m = material.MassPerCell;
                if (!ContractDefaults.IsFinite(material.MassPerCell) || m <= 0)
                    return Error(WorldErrorCode.InvalidConfig, "每格质量必须为有限正数。");
                total += m;
                weightedX += m * (((double)cell.Target.Position.X + 0.5) * cellSize - referenceX);
                weightedY += m * (((double)cell.Target.Position.Y + 0.5) * cellSize - referenceY);
            }
            double cx = referenceX + weightedX / total;
            double cy = referenceY + weightedY / total;
            double moment = 0, radiusSquared = 0;
            foreach (PlannedCell cell in cells)
            {
                materials.TryGet(cell.State.MaterialId, out MaterialRuntimeEntry material);
                double dx = ((double)cell.Target.Position.X + 0.5) * cellSize - cx;
                double dy = ((double)cell.Target.Position.Y + 0.5) * cellSize - cy;
                moment += material.MassPerCell * ((double)cellSize * cellSize / 6 + dx * dx + dy * dy);
                double edgeX = Math.Abs(dx) + cellSize * 0.5;
                double edgeY = Math.Abs(dy) + cellSize * 0.5;
                radiusSquared = Math.Max(radiusSquared, edgeX * edgeX + edgeY * edgeY);
            }
            float candidateMass = (float)total;
            var candidateCenter = new Vector2((float)cx, (float)cy);
            float candidateInertia = (float)moment;
            float candidateRadius = (float)Math.Sqrt(radiusSquared);
            if (!ContractDefaults.IsFinite(candidateMass) || candidateMass <= 0 ||
                !ContractDefaults.IsFinite(candidateCenter) || !ContractDefaults.IsFinite(candidateInertia) ||
                candidateInertia <= 0 || !ContractDefaults.IsFinite(candidateRadius))
                return Error(WorldErrorCode.CapacityExceeded, "质量属性无法以有限单精度数表示。");
            mass = candidateMass;
            center = candidateCenter;
            inertia = candidateInertia;
            radius = candidateRadius;
            return WorldResult.Success();
        }

        // 这里只转换局部几何原点/质心，不承担 M06 的 OBB 接触或命中算法。
        internal static Vector2 Transform(in BodyPose pose, Vector2 local)
        {
            double cosine = Math.Cos(pose.AngleRadians);
            double sine = Math.Sin(pose.AngleRadians);
            return new Vector2((float)(pose.Position.x + cosine * local.x - sine * local.y),
                (float)(pose.Position.y + sine * local.x + cosine * local.y));
        }

        internal static BodyMotion Inherit(in BodySnapshot original, in BodyPose pose, Vector2 localCenter)
        {
            // 先在旧局部空间求质心差，再旋转，减少大世界位置相减的消去误差。
            double cosine = Math.Cos(original.Pose.AngleRadians);
            double sine = Math.Sin(original.Pose.AngleRadians);
            double dx = pose.Position.x - (double)original.Pose.Position.x +
                Math.Cos(pose.AngleRadians) * localCenter.x - Math.Sin(pose.AngleRadians) * localCenter.y -
                cosine * original.LocalCenterOfMass.x + sine * original.LocalCenterOfMass.y;
            double dy = pose.Position.y - (double)original.Pose.Position.y +
                Math.Sin(pose.AngleRadians) * localCenter.x + Math.Cos(pose.AngleRadians) * localCenter.y -
                sine * original.LocalCenterOfMass.x - cosine * original.LocalCenterOfMass.y;
            double omega = original.Motion.AngularVelocityRadians;
            return new BodyMotion(new Vector2((float)(original.Motion.LinearVelocity.x - omega * dy),
                (float)(original.Motion.LinearVelocity.y + omega * dx)), (float)omega);
        }

        private static WorldResult Error(WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("M05.Mass", "massProperties", message));
    }
}
