using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;

namespace OpenOita.Tests.EditMode.M02
{
    public sealed class TransactionCoordinatorTests
    {
        private static TransactionContext Context => new TransactionContext(new WorldVersion(1, 0), 1, TickStage.Commands);

        private sealed class PreparedProbe : IPreparedMutation
        {
            public IWorkingWorldView CandidateWorld => null;
            public ITickInstanceMap CandidateInstances => null;
            public ReadOnlySpan<CellPositionKey> CandidateWrites => ReadOnlySpan<CellPositionKey>.Empty;
            private readonly string _name;
            private readonly List<string> _trace;
            public PreparationState State { get; private set; } = PreparationState.Prepared;
            public ResourceBudget Budget { get; set; } = new ResourceBudget(1, 1, 1, 1, 1, 100);
            internal bool FailPreflight;
            internal bool FailApply;
            internal bool ThrowAbort;
            internal int Disposals;
            internal PreparedProbe(string name, List<string> trace) { _name = name; _trace = trace; }
            public WorldResult Preflight(in TransactionContext context)
            {
                _trace.Add(_name + ".preflight");
                return FailPreflight ? Failure() : WorldResult.Success();
            }
            public WorldResult Apply(in TransactionContext context)
            {
                _trace.Add(_name + ".apply");
                State = PreparationState.Applied;
                return FailApply ? Failure() : WorldResult.Success();
            }
            public void MarkCommitted(WorldVersion version) { _trace.Add(_name + ".commit"); State = PreparationState.Committed; }
            public void Abort()
            {
                _trace.Add(_name + ".abort");
                State = PreparationState.Aborted;
                if (ThrowAbort) throw new InvalidOperationException("测试清理异常");
            }
            public void Dispose() { _trace.Add(_name + ".dispose"); Disposals++; }
        }

        private sealed class FailureProbe : IFailureInjector
        {
            private readonly FailurePoint _point;
            internal FailureProbe(FailurePoint point) { _point = point; }
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point == _point ? Failure() : WorldResult.Success();
        }

        private static WorldResult Failure() => WorldResult.Failure(WorldErrorCode.CapacityExceeded,
            new WorldDiagnostic("Structure", "probe", "测试专用故障"));

        [Test]
        public void M02_05_AllPreflightBeforeAnyApplyAndCommitWaitsForPublish()
        {
            var trace = new List<string>();
            var a = new PreparedProbe("material", trace);
            var b = new PreparedProbe("physics", trace);
            using var transaction = new TransactionCoordinator();
            transaction.Own(a);
            transaction.Own(b);
            Assert.That(transaction.ValidateAndApply(Context, FixtureCatalog.Config().Limits, 1000).IsSuccess, Is.True);
            Assert.That(trace, Is.EqualTo(new[] { "material.preflight", "physics.preflight", "material.apply", "physics.apply" }));
            Assert.That(a.State, Is.EqualTo(PreparationState.Applied));
            transaction.MarkCommitted(new WorldVersion(1, 1));
            Assert.That(a.State, Is.EqualTo(PreparationState.Committed));
            Assert.That(b.State, Is.EqualTo(PreparationState.Committed));
            Assert.That(trace, Does.Not.Contain("material.abort"));
            Assert.That(transaction.RequiresFault, Is.False);
            Assert.That(a.Disposals, Is.EqualTo(1));
        }

        [TestCase(FailurePoint.BeforeApply, false)]
        [TestCase(FailurePoint.DuringApply, true)]
        public void M02_05_InjectFailureRecordsAtomicBoundary(FailurePoint point, bool fault)
        {
            var trace = new List<string>();
            var a = new PreparedProbe("a", trace);
            var b = new PreparedProbe("b", trace);
            using var transaction = new TransactionCoordinator();
            transaction.Own(a);
            transaction.Own(b);
            WorldResult result = transaction.ValidateAndApply(Context, FixtureCatalog.Config().Limits, 1000, new FailureProbe(point));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Diagnostic.Target, Is.EqualTo("probe"));
            Assert.That(transaction.RequiresFault, Is.EqualTo(fault));
            Assert.That(trace, Is.EqualTo(new[] { "a.preflight", "b.preflight", "b.abort", "b.dispose", "a.abort", "a.dispose" }));
            transaction.Dispose();
            Assert.That(a.Disposals, Is.EqualTo(1));
        }

        [Test]
        public void M02_05_SecondPreflightFailureNeverAppliesFirst()
        {
            var trace = new List<string>();
            using var transaction = new TransactionCoordinator();
            transaction.Own(new PreparedProbe("a", trace));
            transaction.Own(new PreparedProbe("b", trace) { FailPreflight = true });
            Assert.That(transaction.ValidateAndApply(Context, FixtureCatalog.Config().Limits, 1000).IsSuccess, Is.False);
            Assert.That(trace, Does.Not.Contain("a.apply"));
            Assert.That(transaction.RequiresFault, Is.False);
        }

        [Test]
        public void M02_05_ApplyFailureRequiresWorldFaultAndCleanupContinuesAfterException()
        {
            var trace = new List<string>();
            using var transaction = new TransactionCoordinator();
            var a = new PreparedProbe("a", trace);
            var b = new PreparedProbe("b", trace) { FailApply = true, ThrowAbort = true };
            transaction.Own(a);
            transaction.Own(b);
            Assert.That(transaction.ValidateAndApply(Context, FixtureCatalog.Config().Limits, 1000).IsSuccess, Is.False);
            Assert.That(transaction.RequiresFault, Is.True);
            Assert.That(transaction.CleanupDiagnostics.Count, Is.EqualTo(1));
            Assert.That(a.Disposals, Is.EqualTo(1));
            Assert.That(b.Disposals, Is.EqualTo(1));
            Assert.That(trace, Does.Contain("a.abort"));
        }

        [TestCase("materials")]
        [TestCase("bodies")]
        [TestCase("shapes")]
        [TestCase("changes")]
        [TestCase("cpu")]
        [TestCase("negative")]
        public void M02_03_GlobalBudgetsAreCheckedBeforeApply(string target)
        {
            var trace = new List<string>();
            var a = new PreparedProbe("a", trace);
            var limits = FixtureCatalog.Config().Limits;
            a.Budget = new ResourceBudget(target == "materials" ? 65537 : 1, target == "bodies" ? 65 : 1,
                target == "shapes" ? 4096 : 1, 1, target == "changes" ? 65537 : 1,
                target == "cpu" ? 1001 : target == "negative" ? -1 : 100);
            using var transaction = new TransactionCoordinator();
            transaction.Own(a);
            WorldResult result = transaction.ValidateAndApply(Context, limits, 1000);
            Assert.That(result.ErrorCode, Is.EqualTo(target == "negative" ? WorldErrorCode.InvalidArgument : WorldErrorCode.CapacityExceeded));
            Assert.That(trace, Does.Not.Contain("a.apply"));
        }

        [Test]
        public void M02_03_PreparationCpuBytesAreCombinedAndOwnershipIsUnique()
        {
            var trace = new List<string>();
            using var transaction = new TransactionCoordinator();
            var a = new PreparedProbe("a", trace);
            transaction.Own(a);
            Assert.Throws<InvalidOperationException>(() => transaction.Own(a));
            transaction.Own(new PreparedProbe("b", trace));
            Assert.That(transaction.ValidateAndApply(Context, FixtureCatalog.Config().Limits, 150).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(trace, Does.Not.Contain("a.apply"));
        }

        [Test]
        public void M02_03_AutomaticBatchBudgetFailureRequiresFaultEvenBeforeApply()
        {
            var trace = new List<string>();
            using var transaction = new TransactionCoordinator();
            transaction.Own(new PreparedProbe("rules", trace) { FailPreflight = true });
            var context = new TransactionContext(new WorldVersion(1, 0), 1, TickStage.Structure);
            Assert.That(transaction.ValidateAndApply(context, FixtureCatalog.Config().Limits, 1000).IsSuccess, Is.False);
            Assert.That(transaction.RequiresFault, Is.True);
            Assert.That(trace, Does.Not.Contain("rules.apply"));
        }
    }
}
