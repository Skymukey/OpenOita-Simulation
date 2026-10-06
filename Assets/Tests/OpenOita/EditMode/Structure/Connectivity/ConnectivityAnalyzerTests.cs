using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Structure;

namespace OpenOita.Tests.EditMode.Structure.Connectivity
{
    public sealed class ConnectivityAnalyzerTests
    {
        private IMaterialRuntimeTable _materials;
        private ConnectivityAnalyzer _analyzer;

        [SetUp]
        public void SetUp()
        {
            _materials = ConnectivityFixture.Load().Materials;
            _analyzer = new ConnectivityAnalyzer(1024, 64);
        }

        private StructurePlan Analyze(ConnectivityFixture view, params CellKey[] changed)
        {
            StructurePlanResult result = _analyzer.Plan(view, _materials, changed);
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            AssertPartition(view, result.Plan);
            return result.Plan;
        }

        private void AssertPartition(ConnectivityFixture view, StructurePlan plan)
        {
            CellKey[] expected = view.Cells.Where(pair => _materials.TryGet(pair.Value.MaterialId, out var entry) &&
                (entry.Rules & RuleMask.Structure) != 0).Select(pair => pair.Key).ToArray();
            CellKey[] actual = plan.Components.SelectMany(component => component.Members).ToArray();
            Assert.That(actual.Distinct().Count(), Is.EqualTo(actual.Length), "不得重复。");
            Assert.That(actual, Is.EquivalentTo(expected), "不得遗漏结构格。");
            ulong[] retired = view.BodyItems.Where(body => plan.Components.Count(item => item.OriginalMinimum.BodyId == body.BodyId) != 1)
                .Select(body => body.BodyId).OrderBy(id => id).ToArray();
            Assert.That(plan.RetiredBodyIds, Is.EqualTo(retired));
            CellPositionKey? previous = null;
            foreach (StructureComponent component in plan.Components)
            {
                Assert.That(component.Members.Count, Is.GreaterThan(0));
                Assert.That(component.OriginalMinimum, Is.EqualTo(component.Members[0].Position));
                if (previous.HasValue) Assert.That(previous.Value.CompareTo(component.OriginalMinimum), Is.LessThan(0));
                previous = component.OriginalMinimum;
                for (int i = 1; i < component.Members.Count; i++)
                    Assert.That(component.Members[i - 1].Position.CompareTo(component.Members[i].Position), Is.LessThan(0));
                Assert.That(component.IsFixed, Is.EqualTo(component.Members.Any(key => view.Fixed.Contains(key))));
                StructureDisposition disposition = component.OriginalMinimum.OwnerKind == OwnerKind.Grid
                    ? component.IsFixed ? StructureDisposition.RetainFixedGrid : StructureDisposition.ExtractFreeGrid
                    : plan.Components.Count(item => item.OriginalMinimum.BodyId == component.OriginalMinimum.BodyId) == 1
                        ? StructureDisposition.RetainBody : StructureDisposition.CreateChildBody;
                Assert.That(component.Disposition, Is.EqualTo(disposition));
            }
        }

        internal static string Describe(StructurePlan plan)
        {
            var output = new StringBuilder();
            foreach (StructureComponent component in plan.Components)
            {
                CellPositionKey min = component.OriginalMinimum;
                output.Append(min.OwnerKind).Append('/').Append(min.BodyId).Append(':').Append(component.ConnectionGroup)
                    .Append(':').Append(component.Disposition)
                    .Append(component.IsFixed ? ":固定[" : ":自由[");
                foreach (CellKey key in component.Members) output.Append(key.Position.X).Append(',').Append(key.Position.Y).Append(';');
                output.Append("]\n");
            }
            output.Append("撤销[").Append(string.Join(",", plan.RetiredBodyIds)).Append("]\n");
            return output.ToString();
        }

        [Test]
        public void M04_T3_01_MixedFixedChainThenRemoveBridge()
        {
            var view = new ConnectivityFixture();
            view.Put(ConnectivityFixture.Grid(10, 10), 102, true);
            view.Put(ConnectivityFixture.Grid(11, 10));
            view.Put(ConnectivityFixture.Grid(12, 10));
            StructurePlan before = Analyze(view);
            Assert.That(before.Components.Count, Is.EqualTo(1));
            Assert.That(before.Components[0].IsFixed, Is.True);
            Assert.That(before.Components[0].Members.Count, Is.EqualTo(3));
            view.Remove(ConnectivityFixture.Grid(11, 10));
            StructurePlan after = Analyze(view, ConnectivityFixture.Grid(11, 10));
            Assert.That(after.Components.Count, Is.EqualTo(2));
            Assert.That(after.Components.Select(item => item.IsFixed), Is.EqualTo(new[] { true, false }));
            TestContext.WriteLine(Describe(after));
        }

        [TestCase("building", true)]
        [TestCase("Building", false)]
        [TestCase("other", false)]
        public void M04_T3_02_ConnectionGroupsUseOrdinalCaseSensitiveComparison(string group, bool connects)
        {
            _materials = ConnectivityFixture.Load(root => root["materials"][3]["ruleParameters"]["structure"]["connectionGroup"] = group).Materials;
            var view = new ConnectivityFixture();
            view.Put(ConnectivityFixture.Grid(0, 0), 102, true);
            view.Put(ConnectivityFixture.Grid(1, 0));
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Count, Is.EqualTo(connects ? 1 : 2));
            TestContext.WriteLine(Describe(plan));
        }

        [Test]
        public void M04_T3_02_DiagonalAndFluidCellsDoNotConnect()
        {
            var view = new ConnectivityFixture();
            view.Put(ConnectivityFixture.Grid(0, 0), 102, true);
            view.Put(ConnectivityFixture.Grid(1, 1));
            view.Put(ConnectivityFixture.Grid(1, 0), 101);
            view.Put(ConnectivityFixture.Grid(0, 1), 103);
            Assert.That(Analyze(view).Components.Count, Is.EqualTo(2));
            view.Put(ConnectivityFixture.Grid(1, 0));
            Assert.That(Analyze(view).Components.Count, Is.EqualTo(1));
        }

        [Test]
        public void M04_T3_03_CrossChunkAndNonMultipleTail()
        {
            var view = new ConnectivityFixture(129, 130);
            view.Put(ConnectivityFixture.Grid(126, 10), 102, true);
            view.Put(ConnectivityFixture.Grid(127, 10));
            view.Put(ConnectivityFixture.Grid(128, 10));
            view.Put(ConnectivityFixture.Grid(128, 129));
            Assert.That(Analyze(view).Components.Select(item => item.Members.Count), Is.EqualTo(new[] { 3, 1 }));
            view.Remove(ConnectivityFixture.Grid(127, 10));
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Select(item => item.IsFixed), Is.EqualTo(new[] { true, false, false }));
            view.Put(ConnectivityFixture.Grid(129, 129));
            AssertFailure(view, WorldErrorCode.OutOfBounds);
            view.Remove(ConnectivityFixture.Grid(129, 129));
            Assert.That(Analyze(view).Components.Count, Is.EqualTo(3));
            TestContext.WriteLine(Describe(plan));
        }

        [Test]
        public void M04_T3_04_LastValidFixedInstanceControlsWholeComponent()
        {
            var view = new ConnectivityFixture();
            for (int x = 1; x <= 4; x++) view.Put(ConnectivityFixture.Grid(x, 1), 104, x == 1 || x == 4);
            Assert.That(Analyze(view).Components[0].IsFixed, Is.True);
            view.Remove(ConnectivityFixture.Grid(1, 1));
            Assert.That(Analyze(view).Components[0].IsFixed, Is.True);
            view.Put(ConnectivityFixture.Grid(4, 1)); // 同ID新实例，候选固定集已撤销。
            Assert.That(Analyze(view).Components[0].IsFixed, Is.False);
        }

        [Test]
        public void M04_T4_01_T4_02_T4_03_AllFourArmsRemainInStableOriginalOrderAtBodyLimitThree()
        {
            var view = new ConnectivityFixture(maxBodies: 3);
            foreach (var point in new[] { (6, 4), (4, 6), (3, 4), (4, 3), (5, 4), (4, 5), (2, 4), (4, 2) })
                view.Put(ConnectivityFixture.Grid(point.Item1, point.Item2));
            StructurePlan plan = Analyze(view, ConnectivityFixture.Grid(4, 4));
            Assert.That(plan.Components.Count, Is.EqualTo(4));
            Assert.That(plan.Components.Select(item => item.Members.Count), Is.EqualTo(new[] { 2, 2, 2, 2 }));
            Assert.That(plan.Components.Select(item => (item.OriginalMinimum.X, item.OriginalMinimum.Y)),
                Is.EqualTo(new[] { (4, 2), (2, 4), (5, 4), (4, 5) }));
            Assert.That(plan.Components.All(item => !item.IsFixed && item.OriginalMinimum.BodyId == 0), Is.True);
            string expected = Describe(plan);
            Array.Reverse(view.Keys);
            Assert.That(Describe(Analyze(view)), Is.EqualTo(expected));
            TestContext.WriteLine("仅完整四分量计划通过；容量3事务拒绝和ID预留未测。\n" + expected);
        }

        [TestCase(true, 2)]
        [TestCase(false, 1)]
        public void M04_T5_01_DynamicMiddleAndEndEditsPreserveOriginalOwnerReferences(bool middle, int components)
        {
            var view = new ConnectivityFixture();
            view.AddBody(17);
            for (int x = 10; x <= 12; x++) view.Put(ConnectivityFixture.Body(17, x, -5));
            view.Remove(ConnectivityFixture.Body(17, middle ? 11 : 12, -5));
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Count, Is.EqualTo(components));
            Assert.That(plan.Components.All(item => !item.IsFixed && item.OriginalMinimum.BodyId == 17), Is.True);
            TestContext.WriteLine((middle ? "M05应撤销17并新建全部子体。" : "M05应保留17并更新几何。") + "\n" + Describe(plan));
        }

        [Test]
        public void M04_T5_01_EmptyBodyHasRetirementWithoutFakeComponent()
        {
            var view = new ConnectivityFixture();
            view.AddBody(17);
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components, Is.Empty);
            Assert.That(plan.RetiredBodyIds, Is.EqualTo(new[] { 17UL }));
            TestContext.WriteLine(Describe(plan));
        }

        [Test]
        public void M04_T5_01_AllDispositionKindsAndRetirementsRemainStableAcrossBodyTransitions()
        {
            var view = new ConnectivityFixture();
            view.AddBody(ulong.MaxValue);
            view.AddBody(9);
            view.AddBody(3);
            view.Put(ConnectivityFixture.Grid(4, 4), 102, true);
            view.Put(ConnectivityFixture.Grid(20, 20));
            view.Put(ConnectivityFixture.Body(3, -10, -4));
            view.Put(ConnectivityFixture.Body(3, -9, -4));
            view.Put(ConnectivityFixture.Body(9, 10, -5));
            view.Put(ConnectivityFixture.Body(9, 12, -5));
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Select(component => component.Disposition), Is.EqualTo(new[] {
                StructureDisposition.RetainFixedGrid, StructureDisposition.ExtractFreeGrid,
                StructureDisposition.RetainBody, StructureDisposition.CreateChildBody, StructureDisposition.CreateChildBody }));
            Assert.That(plan.RetiredBodyIds, Is.EqualTo(new[] { 9UL, ulong.MaxValue }));
            string original = Describe(plan);
            Array.Reverse(view.Keys);
            Array.Reverse(view.BodyItems);
            Assert.That(Describe(Analyze(view)), Is.EqualTo(original));

            view.Put(ConnectivityFixture.Body(9, 11, -5)); // 原体恢复为一个分量，旧撤销结果不能残留。
            StructurePlan connected = Analyze(view);
            Assert.That(connected.Components.Where(component => component.OriginalMinimum.BodyId == 9)
                .Single().Disposition, Is.EqualTo(StructureDisposition.RetainBody));
            Assert.That(connected.RetiredBodyIds, Is.EqualTo(new[] { ulong.MaxValue }));

            for (int x = 10; x <= 12; x++) view.Remove(ConnectivityFixture.Body(9, x, -5));
            StructurePlan empty = Analyze(view);
            Assert.That(empty.RetiredBodyIds, Is.EqualTo(new[] { 9UL, ulong.MaxValue }));
            Assert.That(empty.Components.Any(component => component.OriginalMinimum.BodyId == 9), Is.False);
            Assert.That(Describe(plan), Is.EqualTo(original), "旧计划的成员、分类和撤销目录必须独立。");
            TestContext.WriteLine("乱序初态：\n" + original + "\n连接后：\n" + Describe(connected) + "\n删空后：\n" + Describe(empty));
        }

        [Test]
        public void M04_T7_01_FailedBodyAnalysisDoesNotLeakRetirementsIntoNextPlan()
        {
            var view = new ConnectivityFixture();
            view.AddBody(7);
            view.Put(ConnectivityFixture.Body(7, 0, 0));
            view.Put(ConnectivityFixture.Body(7, 2, 0));
            string original = Describe(Analyze(view));
            CellKey invalid = ConnectivityFixture.Body(7, 3, 0);
            view.Put(invalid, 101);
            AssertFailure(view, WorldErrorCode.UnsupportedOperation);
            view.Remove(invalid);
            Assert.That(Describe(Analyze(view)), Is.EqualTo(original));
            Assert.That(_analyzer.Plan(new ConnectivityFixture(), _materials, Array.Empty<CellKey>()).Plan.RetiredBodyIds, Is.Empty);
            TestContext.WriteLine("成功拆分→无效体内流体整拒→成功重试→空目录；无残留撤销ID。\n" + original);
        }

        [Test]
        public void M04_T5_02_NoCrossOwnerWeldingOrDynamicRefixing()
        {
            var view = new ConnectivityFixture();
            view.AddBody(ulong.MaxValue);
            view.AddBody(2);
            view.Put(ConnectivityFixture.Body(ulong.MaxValue, 1, 0));
            view.Put(ConnectivityFixture.Body(2, 0, 0));
            view.Put(ConnectivityFixture.Grid(0, 0), 102, true);
            view.Put(ConnectivityFixture.Grid(1, 0)); // Spawn 仅连到主网格。
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Select(item => item.OriginalMinimum.BodyId), Is.EqualTo(new[] { 0UL, 2UL, ulong.MaxValue }));
            Assert.That(plan.Components.Select(item => item.IsFixed), Is.EqualTo(new[] { true, false, false }));
            TestContext.WriteLine(Describe(plan));
        }

        [Test]
        public void M04_T5_01_ChangingDynamicConnectionGroupSplitsWithoutRemovingCells()
        {
            _materials = ConnectivityFixture.Load(root => root["materials"][3]["ruleParameters"]["structure"]["connectionGroup"] = "Building").Materials;
            var view = new ConnectivityFixture();
            view.AddBody(9);
            view.Put(ConnectivityFixture.Body(9, 0, 0));
            view.Put(ConnectivityFixture.Body(9, 1, 0), 102);
            view.Put(ConnectivityFixture.Body(9, 2, 0));
            Assert.That(Analyze(view).Components.Count, Is.EqualTo(3));
        }

        [Test]
        public void M04_T3_06_SingleCellAndRingRetainEveryMemberAndHole()
        {
            var view = new ConnectivityFixture();
            view.Put(ConnectivityFixture.Grid(1, 1));
            for (int y = 20; y < 23; y++)
                for (int x = 20; x < 23; x++)
                    if (x != 21 || y != 21) view.Put(ConnectivityFixture.Grid(x, y));
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Select(item => item.Members.Count), Is.EqualTo(new[] { 1, 8 }));
            Assert.That(plan.Components.All(item => !item.IsFixed && item.OriginalMinimum.BodyId == 0), Is.True);
            Assert.That(plan.Components.SelectMany(item => item.Members), Has.No.Member(ConnectivityFixture.Grid(21, 21)));
            TestContext.WriteLine("全部主网格自由分量需M05分配新非零ID。\n" + Describe(plan));
        }

        [Test]
        public void M04_T7_01_CapacityFailureHasNoPartialPlanAndWorkspaceCanBeReused()
        {
            var view = new ConnectivityFixture();
            for (int x = 0; x < 3; x++) view.Put(ConnectivityFixture.Grid(x, 0));
            _analyzer = new ConnectivityAnalyzer(2, 1);
            AssertFailure(view, WorldErrorCode.CapacityExceeded);
            Assert.That(view.Reads, Is.Zero);
            Assert.That(view.Cells.Count, Is.EqualTo(3));
            view.Remove(ConnectivityFixture.Grid(2, 0));
            Assert.That(Analyze(view).Components[0].Members.Count, Is.EqualTo(2));
        }

        [Test]
        public void M04_T7_01_BodyDirectoryCapacityFailureIsAtomic()
        {
            var view = new ConnectivityFixture();
            view.AddBody(1);
            view.Put(ConnectivityFixture.Body(1, 0, 0));
            _analyzer = new ConnectivityAnalyzer(4, 0);
            AssertFailure(view, WorldErrorCode.CapacityExceeded);
            Assert.That(view.Cells.Count, Is.EqualTo(1));
        }

        [TestCase("duplicate", WorldErrorCode.InvalidArgument)]
        [TestCase("stale", WorldErrorCode.StaleGeneration)]
        [TestCase("empty", WorldErrorCode.InvalidArgument)]
        [TestCase("unknown", WorldErrorCode.UnknownMaterial)]
        [TestCase("negativeGrid", WorldErrorCode.OutOfBounds)]
        [TestCase("missingBody", WorldErrorCode.InvalidArgument)]
        [TestCase("duplicateBody", WorldErrorCode.InvalidArgument)]
        [TestCase("zeroBody", WorldErrorCode.InvalidArgument)]
        [TestCase("fixedBody", WorldErrorCode.InvalidArgument)]
        [TestCase("fixedFluid", WorldErrorCode.InvalidArgument)]
        [TestCase("bodyFluid", WorldErrorCode.UnsupportedOperation)]
        public void M04_T7_01_InvalidReferencesAndFixedBindingsFailWholeAnalysis(string mode, WorldErrorCode code)
        {
            var view = new ConnectivityFixture();
            CellKey grid = ConnectivityFixture.Grid(1, 1);
            view.Put(grid);
            switch (mode)
            {
                case "duplicate": view.Keys = new[] { grid, grid }; break;
                case "stale": view.Keys = new[] { ConnectivityFixture.Grid(1, 1, 2) }; break;
                case "empty": view.Cells[grid] = default; break;
                case "unknown": view.Cells[grid] = new CellSnapshot(65535); break;
                case "negativeGrid": view.Put(ConnectivityFixture.Grid(-1, 0)); break;
                case "missingBody": view.Put(ConnectivityFixture.Body(7, 0, 0)); break;
                case "duplicateBody": view.AddBody(7); view.AddBody(7); break;
                case "zeroBody": view.AddBody(0); break;
                case "fixedBody": view.AddBody(7); view.Put(ConnectivityFixture.Body(7, 0, 0), 104, true); break;
                case "fixedFluid": view.Put(grid, 101, true); break;
                case "bodyFluid": view.AddBody(7); view.Put(ConnectivityFixture.Body(7, 0, 0), 101); break;
            }
            int count = view.Cells.Count;
            AssertFailure(view, code);
            Assert.That(view.Cells.Count, Is.EqualTo(count));
        }

        [Test]
        public void M04_T7_01_ReadFailureAndClosedLeaseReturnNoPlan()
        {
            var view = new ConnectivityFixture();
            view.Put(ConnectivityFixture.Grid(0, 0));
            view.ReadResult = WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("State", "fixture", "读取失败探针。"));
            AssertFailure(view, WorldErrorCode.Faulted);
            view.ReadResult = WorldResult.Success();
            view.Closed = true;
            AssertFailure(view, WorldErrorCode.NotReady);
            view.Closed = false;
            Assert.That(Analyze(view).Components.Count, Is.EqualTo(1));
        }

        [Test]
        public void M04_T7_01_ChangedRemovedKeysAreValidButOldGenerationAndOutOfBoundsAreRejected()
        {
            var view = new ConnectivityFixture();
            Assert.That(_analyzer.Plan(view, _materials, new[] { ConnectivityFixture.Grid(5, 5) }).Result.IsSuccess, Is.True);
            Assert.That(_analyzer.Plan(view, _materials, new[] { ConnectivityFixture.Grid(5, 5, 2) }).Result.ErrorCode,
                Is.EqualTo(WorldErrorCode.StaleGeneration));
            Assert.That(_analyzer.Plan(view, _materials, new[] { ConnectivityFixture.Grid(256, 5) }).Plan, Is.Null);
        }

        [Test]
        public void M04_T7_01_PublicPlanCopiesWorkspaceAndRejectsOffThreadCalls()
        {
            var view = new ConnectivityFixture();
            view.Put(ConnectivityFixture.Grid(10, 10));
            StructurePlan old = Analyze(view);
            string original = Describe(old);
            view.Remove(ConnectivityFixture.Grid(10, 10));
            view.Put(ConnectivityFixture.Grid(0, 0));
            Analyze(view);
            Assert.That(Describe(old), Is.EqualTo(original));
            var result = Task.Run(() => _analyzer.Plan(view, _materials, Array.Empty<CellKey>())).GetAwaiter().GetResult();
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(result.Plan, Is.Null);
        }

        [Test]
        public void M04_T7_01_ExtremeLocalCoordinatesNeverWrapIntoNeighbours()
        {
            var view = new ConnectivityFixture();
            view.AddBody(1);
            view.Put(ConnectivityFixture.Body(1, int.MaxValue, 0));
            view.Put(ConnectivityFixture.Body(1, int.MinValue, 0));
            view.Put(ConnectivityFixture.Body(1, int.MinValue + 1, 0));
            Assert.That(Analyze(view).Components.Select(item => item.Members.Count), Is.EqualTo(new[] { 2, 1 }));
        }

        [Test]
        public void M04_T7_01_Full65536CellComponentUsesIterativeSearch()
        {
            var view = new ConnectivityFixture();
            // 大夹具直接一次建索引，避免夹具自身逐格重建影响执行时间。
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++) view.Cells.Add(ConnectivityFixture.Grid(x, y), new CellSnapshot(102));
            view.Refresh();
            view.Fixed.Add(ConnectivityFixture.Grid(0, 0));
            _analyzer = new ConnectivityAnalyzer(65536, 64);
            StructurePlan plan = Analyze(view);
            Assert.That(plan.Components.Count, Is.EqualTo(1));
            Assert.That(plan.Components[0].Members.Count, Is.EqualTo(65536));
            Assert.That(plan.Components[0].IsFixed, Is.True);
            TestContext.WriteLine("65536格完整固定分量；显式队列搜索成功。此项不是Step性能验收。");
        }

        [Test]
        public void M04_T7_01_RandomLayoutsMatchIndependentFloodFillAcrossInsertionOrders()
        {
            _materials = ConnectivityFixture.Load(root => root["materials"][3]["ruleParameters"]["structure"]["connectionGroup"] = "Building").Materials;
            for (int seed = 0; seed < 40; seed++)
            {
                var random = new Random(seed);
                var view = new ConnectivityFixture();
                for (int y = 0; y < 12; y++)
                    for (int x = 0; x < 12; x++)
                        if (random.Next(3) != 0) view.Put(ConnectivityFixture.Grid(x, y), (ushort)(random.Next(2) == 0 ? 102 : 104), random.Next(20) == 0);
                string expected = Describe(Oracle(view));
                Assert.That(Describe(Analyze(view)), Is.EqualTo(expected), "种子=" + seed);
                for (int i = view.Keys.Length - 1; i > 0; i--)
                {
                    int target = random.Next(i + 1);
                    (view.Keys[i], view.Keys[target]) = (view.Keys[target], view.Keys[i]);
                }
                Assert.That(Describe(Analyze(view)), Is.EqualTo(expected), "乱序种子=" + seed);
            }
            TestContext.WriteLine("40个固定种子、80次候选枚举，与独立集合洪泛结果完全一致。");
        }

        private StructurePlan Oracle(ConnectivityFixture view)
        {
            var remaining = new HashSet<CellKey>(view.Cells.Keys);
            var components = new List<StructureComponent>();
            while (remaining.Count > 0)
            {
                CellKey first = remaining.OrderBy(key => key.Position).First();
                var members = new List<CellKey> { first };
                remaining.Remove(first);
                string group = Group(first);
                for (int head = 0; head < members.Count; head++)
                {
                    CellKey key = members[head];
                    foreach (CellKey candidate in remaining.ToArray())
                    {
                        int distance = Math.Abs(candidate.Position.X - key.Position.X) + Math.Abs(candidate.Position.Y - key.Position.Y);
                        if (distance != 1 || Group(candidate) != group) continue;
                        remaining.Remove(candidate);
                        members.Add(candidate);
                    }
                }
                members.Sort((firstKey, secondKey) => firstKey.Position.CompareTo(secondKey.Position));
                bool isFixed = members.Any(view.Fixed.Contains);
                components.Add(new StructureComponent(first.Position, group, isFixed,
                    isFixed ? StructureDisposition.RetainFixedGrid : StructureDisposition.ExtractFreeGrid, members));
            }
            return new StructurePlan(components);
            string Group(CellKey key) { _materials.TryGet(view.Cells[key].MaterialId, out var entry); return entry.Parameters.ConnectionGroup; }
        }

        private StructurePlanResult AssertFailure(ConnectivityFixture view, WorldErrorCode code)
        {
            StructurePlanResult result = _analyzer.Plan(view, _materials, Array.Empty<CellKey>());
            Assert.That(result.Result.ErrorCode, Is.EqualTo(code), result.Result.Diagnostic.Message);
            Assert.That(result.Plan, Is.Null);
            Assert.That(result.Result.Diagnostic.Stage, Is.EqualTo("Structure"));
            return result;
        }
    }
}
