using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode.Physics
{
    public sealed class RotationConstraintTests
    {
        private readonly List<IWorld> _worlds = new();

        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True,
            result.ErrorCode + " / " + result.Diagnostic.Target + ": " + result.Diagnostic.Message);

        private SimulationWorld Create(WorldSources sources, bool frozen = true)
        {
            var result = new WorldSimulation(rendererFactory: () => new M06Sources.CommitObserver(),
                freezeBodyRotation: frozen).Create(sources, Vector2.zero);
            Success(result.Result);
            _worlds.Add(result.World);
            return (SimulationWorld)result.World;
        }

        private static WorldSources Strip(float gravity = 0) => M06Sources.Create(new[]
        {
            new InitialCell(20, 30, 104), new InitialCell(21, 30, 104), new InitialCell(22, 30, 104)
        }, gravity: gravity);

        private static void AssertFrozen(SimulationWorld world)
        {
            foreach (BodySnapshot body in world.Runtime.State.Bodies)
            {
                Assert.That(body.Pose.AngleRadians, Is.EqualTo(0).Within(1e-6));
                Assert.That(body.Motion.AngularVelocityRadians, Is.EqualTo(0).Within(1e-6));
                Assert.That(world.Runtime.Physics.InspectBody(body.BodyId).constraints,
                    Is.EqualTo(RigidbodyConstraints2D.FreezeRotation));
            }
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (IWorld world in _worlds) Success(world.Dispose());
            _worlds.Clear();
            yield return null;
            yield return null;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OffCenterForceMovesBodyAndOnlyUnlockedWorldRotates(bool frozen)
        {
            SimulationWorld world = Create(Strip(), frozen);
            BodySnapshot before = world.Runtime.State.Bodies[0];
            Rigidbody2D rb = world.Runtime.Physics.InspectBody(before.BodyId);
            rb.AddForceAtPosition(new Vector2(0.1f, 0), rb.worldCenterOfMass + new Vector2(0, 0.1f), ForceMode2D.Impulse);
            for (int i = 0; i < 3; i++) Success(world.Step().Result);
            BodySnapshot after = world.Runtime.State.Bodies[0];
            Assert.That(after.Pose.Position.x, Is.GreaterThan(before.Pose.Position.x));
            if (frozen) AssertFrozen(world);
            else Assert.That(Mathf.Abs(after.Pose.AngleRadians), Is.GreaterThan(0.001f));
        }

        [Test]
        public void FallingBeamHitsOffCenterSupportWithoutTurningOrPassingThrough()
        {
            var cells = new List<InitialCell> { new InitialCell(20, 20, 102), new InitialCell(21, 20, 102) };
            for (int x = 20; x <= 24; x++) cells.Add(new InitialCell(x, 24, 104));
            SimulationWorld world = Create(M06Sources.Create(cells, new[] { new Vector2Int(20, 20) }, gravity: -9.81f));
            float startY = world.Runtime.State.Bodies[0].Pose.Position.y;
            for (int i = 0; i < 30; i++)
            {
                Success(world.Step().Result);
                AssertFrozen(world);
            }
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Assert.That(body.Pose.Position.y, Is.LessThan(startY));
            Assert.That(body.Pose.Position.y, Is.GreaterThanOrEqualTo(2.09f));
            Assert.That(Mathf.Abs(body.Motion.LinearVelocity.y), Is.LessThan(1e-4));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BurnoutOrRemovalSplitsIntoLockedMovingChildren(bool burnout)
        {
            SimulationWorld world = Create(Strip(-1));
            ulong original = world.Runtime.State.Bodies[0].BodyId;
            Vector2 center = new Vector2(2.15f, 3.05f);
            var hit = world.QueryPoint(center);
            Success(hit.Result);
            Assert.That(hit.HasHit, Is.True);
            if (burnout)
            {
                Success(world.Runtime.SetCellsForTest(new[]
                {
                    new CellWrite(hit.Hit.Key, new CellSnapshot(104, flags: 1, fuelTicksRemaining: 1, spreadCountdown: 10))
                }));
            }
            else
            {
                Success(world.Enqueue(new MaterialCommand(MaterialOperation.Remove,
                    new WorldRect(center - Vector2.one * 0.01f, center + Vector2.one * 0.01f), 0, world.Version.Generation)).Result);
            }
            Success(world.Step().Result);
            Assert.That(world.Runtime.State.Bodies.Length, Is.EqualTo(2));
            Assert.That(world.Runtime.State.MaterialCells, Is.EqualTo(2));
            AssertFrozen(world);
            foreach (BodySnapshot body in world.Runtime.State.Bodies)
            {
                Assert.That(body.BodyId, Is.Not.EqualTo(original));
                Assert.That(body.Motion.LinearVelocity.y, Is.LessThan(0));
                world.Runtime.Physics.InspectBody(body.BodyId).AddTorque(0.001f, ForceMode2D.Impulse);
            }
            Success(world.Step().Result);
            AssertFrozen(world);
        }

        [Test]
        public void RetainedBodyAndResetRestoreRotationConstraint()
        {
            SimulationWorld world = Create(Strip());
            ulong id = world.Runtime.State.Bodies[0].BodyId;
            Success(world.Enqueue(new MaterialCommand(MaterialOperation.Remove,
                new WorldRect(new Vector2(2.21f, 3.01f), new Vector2(2.29f, 3.09f)), 0, world.Version.Generation)).Result);
            Success(world.Step().Result);
            Assert.That(world.Runtime.State.Bodies.Length, Is.EqualTo(1));
            Assert.That(world.Runtime.State.Bodies[0].BodyId, Is.EqualTo(id));
            AssertFrozen(world);
            Success(world.Reset());
            Assert.That(world.Version.Generation, Is.EqualTo(2));
            Assert.That(world.Runtime.State.MaterialCells, Is.EqualTo(3));
            world.Runtime.Physics.InspectBody(world.Runtime.State.Bodies[0].BodyId).AddTorque(0.001f, ForceMode2D.Impulse);
            Success(world.Step().Result);
            AssertFrozen(world);
        }

        [Test]
        public void DefaultHostDisablesRotation()
        {
            var go = new GameObject("禁转Host验证");
            go.SetActive(false);
            WorldSources sources = Strip();
            var materials = new TextAsset(sources.MaterialsText);
            var config = new TextAsset(sources.WorldConfigText);
            var scene = new TextAsset(sources.SceneText);
            try
            {
                var host = go.AddComponent<WorldHost>();
                // 给默认OnEnable入口提供确定的自由木梁，避免依赖用户当前StreamingAssets布局。
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(WorldHost).GetField("materialsSource", flags).SetValue(host, materials);
                typeof(WorldHost).GetField("worldConfigSource", flags).SetValue(host, config);
                typeof(WorldHost).GetField("sceneSource", flags).SetValue(host, scene);
                go.SetActive(true);
                Success(host.LastResult);
                var world = (SimulationWorld)host.World;
                Assert.That(world.Runtime.State.Bodies.Length, Is.GreaterThan(0));
                AssertFrozen(world);
                Success(host.ResetWorld());
                AssertFrozen(world);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(materials);
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(scene);
            }
        }

#if UNITY_EDITOR
        [TestCase(true)]
        [TestCase(false)]
        public void EditorTrialUsesSelectedModeAndPreservesItOnReset(bool frozen)
        {
            Success(OpenOita.Data.SceneMaterialData.Load(Strip(), out var data));
            using var session = new OpenOita.Editor.SceneEditorSession();
            if (frozen) Success(session.BeginTrial(data, Vector2.zero, false));
            else Success(session.BeginTrial(data, Vector2.zero, false, false));
            var world = (SimulationWorld)session.Host.World;
            for (int generation = 1; generation <= 2; generation++)
            {
                Assert.That(world.Runtime.Physics.FreezeBodyRotation, Is.EqualTo(frozen));
                var body = world.Runtime.State.Bodies[0];
                world.Runtime.Physics.InspectBody(body.BodyId).AddTorque(0.001f, ForceMode2D.Impulse);
                Success(session.Host.Step().Result);
                if (frozen) AssertFrozen(world);
                else Assert.That(Mathf.Abs(world.Runtime.State.Bodies[0].Pose.AngleRadians), Is.GreaterThan(0));
                if (generation == 1) Success(session.Host.ResetWorld());
            }
        }
#endif
    }
}
