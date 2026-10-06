using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.PhysicsAdapter;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Physics
{
    public sealed class PhysicsSubstepPlannerTests
    {
        [TestCase(0f, 0f, 0f, 0.07f, 1)]
        [TestCase(4f, 0f, 0f, 0.07f, 2)]
        [TestCase(0f, 2f, 0f, 2f, 2)]
        [TestCase(0f, 0f, -9.81f, 0.07f, 1)]
        public void M06_T8_01_D08UsesComRadiusAndCompleteTick(float speed, float omega, float gravity, float radius, int expected)
        {
            WorldConfig config = Config(gravity);
            var body = new BodySnapshot(1, new BodyPose(new Vector2(4, 8), 0), new BodyMotion(new Vector2(speed, 0), omega), new Vector2(12, 13), 7);
            var geometry = new BodyGeometryPlan(new CellPositionKey(OwnerKind.Body, 1, 0, 0), body.Pose, body.Motion, body.LocalCenterOfMass,
                1, 1, radius, 7, Array.Empty<PlannedCell>(), Array.Empty<CellRectangle>());
            Assert.That(PhysicsSubstepPlanner.Plan(config, new[] { body }, new[] { geometry }, out int n).IsSuccess, Is.True);
            Assert.That(n, Is.EqualTo(expected)); Assert.That(config.StepSeconds / n * n, Is.EqualTo(0.02f).Within(1e-8));
        }
        [Test]
        public void M06_T5_03_GravityPredictedSpeedFailsAndGeometryMismatchIsRejected()
        {
            var body = new BodySnapshot(1, default, new BodyMotion(new Vector2(4.99f, 0), 0), Vector2.zero, 1);
            var geometry = new BodyGeometryPlan(new CellPositionKey(OwnerKind.Body, 1, 0, 0), default, body.Motion, Vector2.zero,
                1, 1, 0.07f, 1, Array.Empty<PlannedCell>(), Array.Empty<CellRectangle>());
            Assert.That(PhysicsSubstepPlanner.Plan(Config(-9.81f), new[] { body }, new[] { geometry }, out int count).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(count, Is.Zero);
            var wrong = new BodySnapshot(1, default, default, Vector2.zero, 2);
            Assert.That(PhysicsSubstepPlanner.Plan(Config(0), new[] { wrong }, new[] { geometry }, out count).IsSuccess, Is.False);
        }
        private static WorldConfig Config(float gravity) => new WorldConfig(1, 256, 256, 128, 0.1f, 0.02f, gravity, 1,
            new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 16));
    }
}
