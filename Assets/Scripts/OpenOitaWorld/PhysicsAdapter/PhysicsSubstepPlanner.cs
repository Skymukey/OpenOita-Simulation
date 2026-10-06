using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.PhysicsAdapter
{
    public static class PhysicsSubstepPlanner
    {
        public static WorldResult Plan(WorldConfig config, ReadOnlySpan<BodySnapshot> bodies,
            IReadOnlyList<BodyGeometryPlan> geometry, out int count)
        {
            count = 0;
            double maximum = 0, h = config.StepSeconds;
            foreach (BodySnapshot body in bodies)
            {
                WorldResult speed = CheckSpeed(config, body.Motion);
                if (!speed.IsSuccess) return speed;
                BodyGeometryPlan shape = null;
                foreach (BodyGeometryPlan item in geometry) if (item.Owner.OwnerKind == OwnerKind.Body && item.Owner.BodyId == body.BodyId) { shape = item; break; }
                if (shape == null || shape.GeometryVersion != body.GeometryVersion || !shape.LocalCenterOfMass.Equals(body.LocalCenterOfMass)) return Failure("geometryVersion", "子步缺少同版本质心半径。");
                double v = body.Motion.LinearVelocity.magnitude, omega = Math.Abs(body.Motion.AngularVelocityRadians);
                if (v + Math.Abs(config.GravityY) * h > config.Limits.MaxLinearSpeed)
                    return Failure("maxLinearSpeed", "预测线速度超限，不能截断。");
                maximum = Math.Max(maximum, v * h + Math.Abs(config.GravityY) * h * h + omega * shape.BoundingRadius * h);
            }
            double required = Math.Max(1, Math.Ceiling(maximum / (config.CellSize / 2d)));
            if (required > config.Limits.MaxPhysicsSubsteps) return Failure("maxPhysicsSubsteps", "所需子步数超限，不能减少推进时间。");
            count = (int)required;
            return WorldResult.Success();
        }
        public static WorldResult CheckSpeed(WorldConfig config, in BodyMotion motion)
        {
            if (!ContractDefaults.IsFinite(motion.LinearVelocity) || !ContractDefaults.IsFinite(motion.AngularVelocityRadians) ||
                motion.LinearVelocity.magnitude > config.Limits.MaxLinearSpeed ||
                Math.Abs(motion.AngularVelocityRadians) > config.Limits.MaxAngularSpeedDegrees * Mathf.Deg2Rad)
                return Failure("speed", "候选速度非有限或超出预算。");
            return WorldResult.Success();
        }
        private static WorldResult Failure(string target, string message) => WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Physics", target, message));
    }
}
