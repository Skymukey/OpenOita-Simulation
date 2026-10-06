using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Host;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M02
{
    public sealed class HostDriverTests
    {
        // 只证明驱动时序；该探针不进入生产 Create，也不证明独立物理场景验收。
        private sealed class WorldProbe : IWorld
        {
            public WorldLifecycle Lifecycle => WorldLifecycle.Ready;
            public WorldVersion Version => new WorldVersion(1, (ulong)Steps);
            public WorldConfig Config { get; } = FixtureCatalog.Config();
            public event Action<ChangeSet> Committed { add { } remove { } }
            internal int Steps;
            internal int Disposals;
            internal bool FailStep;
            internal Action OnStep;
            public EnqueueResult Enqueue(in MaterialCommand command) => default;
            public CommandResult Retry(in CommandToken token) => default;
            public StepResult Step()
            {
                if (FailStep) return new StepResult(WorldResult.Failure(WorldErrorCode.Faulted, default), Version, null);
                Steps++;
                OnStep?.Invoke();
                return new StepResult(WorldResult.Success(), Version, null);
            }
            public PointQueryResult QueryPoint(Vector2 point) => default;
            public QueryResult QueryRegion(in WorldRect region, Span<CellHit> destination) => default;
            public QueryResult QuerySegment(Vector2 start, Vector2 end, Span<CellHit> destination) => default;
            public MaterialCountsResult QueryMaterialCounts() => new MaterialCountsResult(WorldResult.Success(), Version);
            public WorldResult Reset() { Steps = 0; return WorldResult.Success(); }
            public WorldResult Dispose() { Disposals++; return WorldResult.Success(); }
        }

        private sealed class FactoryProbe : IWorldFactory
        {
            internal readonly WorldProbe World = new();
            internal int Creates;
            public WorldCreateResult Create(WorldSources sources, Vector2 origin)
            {
                Creates++;
                return new WorldCreateResult(WorldResult.Success(), World);
            }
        }

        [Test]
        public void M02_08_AutomaticAccumulationKeepsRemainderAndManualIsExclusive()
        {
            float fixedDelta = Time.fixedDeltaTime;
            Vector2 gravity = Physics2D.gravity;
            var autoWorld = new WorldProbe();
            var driver = new FixedStepDriver(autoWorld, true);
            double step = autoWorld.Config.StepSeconds;
            Assert.That(driver.Advance(step * 10, 2).IsSuccess, Is.True);
            Assert.That(autoWorld.Steps, Is.EqualTo(2));
            Assert.That(driver.AccumulatedSeconds, Is.EqualTo(step * 8).Within(1e-8));
            Assert.That(driver.StepManual().Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            driver.Advance(0, 8);
            Assert.That(autoWorld.Steps, Is.EqualTo(10));
            var manual = new WorldProbe();
            var manualDriver = new FixedStepDriver(manual, false);
            Assert.That(manualDriver.Advance(step).ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            for (int i = 0; i < 10; i++) manualDriver.StepManual();
            Assert.That(manual.Steps, Is.EqualTo(autoWorld.Steps));
            Assert.That(Time.fixedDeltaTime, Is.EqualTo(fixedDelta));
            Assert.That(Physics2D.gravity, Is.EqualTo(gravity));
        }

        [Test]
        public void M02_08_RejectNonfiniteFramesStopOnFaultAndPreventReentry()
        {
            var world = new WorldProbe();
            var driver = new FixedStepDriver(world, true);
            foreach (double delta in new[] { double.NaN, double.PositiveInfinity, -1d })
                Assert.That(driver.Advance(delta).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(driver.AccumulatedSeconds, Is.Zero);
            world.FailStep = true;
            Assert.That(driver.Advance(1).ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(world.Steps, Is.Zero);
            world.FailStep = false;
            world.OnStep = () => Assert.That(driver.Advance(1).ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            Assert.That(driver.Advance(0, 1).IsSuccess, Is.True);
            Assert.That(world.Steps, Is.EqualTo(1));
        }

        [Test]
        public void M02_07_HostOwnsOneWorldResetAndRepeatCloseAreSafe()
        {
            var go = new GameObject("M02 Host 测试");
            go.SetActive(false);
            try
            {
                WorldHost host = go.AddComponent<WorldHost>();
                var factory = new FactoryProbe();
                Assert.That(host.CreateWorld(factory, null, Vector2.zero, false).IsSuccess, Is.True);
                Assert.That(host.CreateWorld(factory, null, Vector2.zero, false).ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                Assert.That(factory.Creates, Is.EqualTo(1));
                host.Step();
                Assert.That(factory.World.Steps, Is.EqualTo(1));
                Assert.That(host.ResetWorld().IsSuccess, Is.True);
                Assert.That(factory.World.Steps, Is.Zero);
                Assert.That(host.CloseWorld().IsSuccess, Is.True);
                Assert.That(host.CloseWorld().IsSuccess, Is.True);
                Assert.That(factory.World.Disposals, Is.EqualTo(1));
                Assert.That(host.World, Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void M02_08_HostRejectsUnsupportedTransformBeforeFactory()
        {
            var go = new GameObject("M02 Host 变换测试");
            go.SetActive(false);
            try
            {
                WorldHost host = go.AddComponent<WorldHost>();
                var factory = new FactoryProbe();
                go.transform.localScale = new Vector3(2, 1, 1);
                Assert.That(host.CreateWorld(factory, null, Vector2.zero, false).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                go.transform.localScale = Vector3.one;
                go.transform.rotation = Quaternion.Euler(0, 0, 5);
                Assert.That(host.CreateWorld(factory, null, Vector2.zero, false).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                Assert.That(factory.Creates, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
