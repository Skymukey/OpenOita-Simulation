using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.EditMode.Structure.Connectivity;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode
{
    public sealed class StructureContractTests
    {
        private static CellKey Key(ulong bodyId, int x, int y = 0)
        {
            return new CellKey(1, new CellPositionKey(bodyId == 0 ? OwnerKind.Grid : OwnerKind.Body, bodyId, x, y));
        }

        private static StructureComponent Component(ulong bodyId, int x, StructureDisposition disposition, bool isFixed = false)
        {
            CellKey key = Key(bodyId, x);
            return new StructureComponent(key.Position, "building", isFixed, disposition, new[] { key });
        }

        [Test]
        public void M00_02_M04_I01_EmptyRetirementIsOwnedSortedUniqueAndHasNoComponent()
        {
            var input = new List<ulong> { ulong.MaxValue, 9, 2, 9 };
            var plan = new StructurePlan(Array.Empty<StructureComponent>(), input);
            input.Clear();
            Assert.That(plan.Components, Is.Empty);
            Assert.That(plan.RetiredBodyIds, Is.EqualTo(new ulong[] { 2, 9, ulong.MaxValue }));
            Assert.Throws<NotSupportedException>(() => ((IList<ulong>)plan.RetiredBodyIds).Add(7));
            Assert.Throws<ArgumentException>(() => new StructurePlan(Array.Empty<StructureComponent>(), new ulong[] { 0 }));
            Assert.That(new StructurePlan(Array.Empty<StructureComponent>()).RetiredBodyIds, Is.Empty);
        }

        [Test]
        public void M00_02_M04_I02_PlanCopiesCollectionsAndKeepsOriginalCoordinates()
        {
            CellKey original = Key(12, -7, -9);
            var members = new List<CellKey> { original };
            var component = new StructureComponent(original.Position, "building", false, StructureDisposition.RetainBody, members);
            var components = new List<StructureComponent> { component };
            var plan = new StructurePlan(components);
            members.Clear();
            components.Clear();
            Assert.That(plan.Components.Count, Is.EqualTo(1));
            Assert.That(plan.Components[0].Members, Is.EqualTo(new[] { original }));
            Assert.That(plan.Components[0].OriginalMinimum, Is.EqualTo(original.Position));
            Assert.That(plan.Components[0].Disposition, Is.EqualTo(StructureDisposition.RetainBody));
            Assert.Throws<NotSupportedException>(() => ((IList<CellKey>)component.Members).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<StructureComponent>)plan.Components).Clear());
            Assert.That(typeof(StructureComponent).GetProperty(nameof(StructureComponent.Disposition)).CanWrite, Is.False);
        }

        [Test]
        public void M00_02_M04_I02_InvalidComponentClassificationOrEmptyMembersIsRejected()
        {
            Assert.Throws<ArgumentException>(() => Component(0, 0, StructureDisposition.RetainBody));
            Assert.Throws<ArgumentException>(() => Component(5, 0, StructureDisposition.ExtractFreeGrid));
            Assert.Throws<ArgumentException>(() => Component(0, 0, StructureDisposition.RetainFixedGrid));
            Assert.Throws<ArgumentException>(() => Component(5, 0, StructureDisposition.RetainBody, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => Component(0, 0, (StructureDisposition)255));
            Assert.Throws<ArgumentException>(() => new StructureComponent(Key(5, 0).Position, "building", false,
                StructureDisposition.RetainBody, Array.Empty<CellKey>()));
        }

        [Test]
        public void M00_05_M04_I01_I02_RetirementAndSplitClassificationMustAgree()
        {
            var retained = Component(5, 0, StructureDisposition.RetainBody);
            Assert.Throws<ArgumentException>(() => new StructurePlan(new[] { retained }, new ulong[] { 5 }));
            var children = new[] { Component(5, 0, StructureDisposition.CreateChildBody), Component(5, 2, StructureDisposition.CreateChildBody) };
            Assert.Throws<ArgumentException>(() => new StructurePlan(children));
            Assert.Throws<ArgumentException>(() => new StructurePlan(new[] { children[0] }, new ulong[] { 5 }));
            var plan = new StructurePlan(children, new ulong[] { 5 });
            Assert.That(plan.Components.All(item => item.Disposition == StructureDisposition.CreateChildBody), Is.True);
            Assert.That(plan.RetiredBodyIds, Is.EqualTo(new ulong[] { 5 }));
        }

        [Test]
        public void M00_02_M04_I01_I02_RealAnalyzerProvidesAllFourDispositionsAndEmptyRetirement()
        {
            var view = new ConnectivityFixture();
            view.Put(Key(0, 0), 102, true);
            view.Put(Key(0, 2));
            view.AddBody(12);
            view.AddBody(7);
            view.AddBody(3);
            view.Put(Key(3, -5));
            view.Put(Key(7, 0));
            view.Put(Key(7, 2));
            IStructurePlanner planner = new ConnectivityAnalyzer(16, 8);
            StructurePlanResult result = planner.Plan(view, ConnectivityFixture.Load().Materials, Array.Empty<CellKey>());
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            Assert.That(result.Plan.Components.Select(item => item.Disposition), Is.EqualTo(new[]
            {
                StructureDisposition.RetainFixedGrid, StructureDisposition.ExtractFreeGrid,
                StructureDisposition.RetainBody, StructureDisposition.CreateChildBody, StructureDisposition.CreateChildBody
            }));
            Assert.That(result.Plan.RetiredBodyIds, Is.EqualTo(new ulong[] { 7, 12 }));
            Assert.That(result.Plan.Components.All(item => item.Members.Count > 0), Is.True);
            Assert.That(result.Plan.Components.SelectMany(item => item.Members), Is.EquivalentTo(view.Keys));
            view.Cells.Clear();
            view.Refresh();
            StructurePlanResult empty = planner.Plan(view, ConnectivityFixture.Load().Materials, Array.Empty<CellKey>());
            Assert.That(empty.Result.IsSuccess, Is.True, empty.Result.Diagnostic.Message);
            Assert.That(empty.Plan.Components, Is.Empty);
            Assert.That(empty.Plan.RetiredBodyIds, Is.EqualTo(new ulong[] { 3, 7, 12 }));
            Assert.That(result.Plan.RetiredBodyIds, Is.EqualTo(new ulong[] { 7, 12 }), "后续分析不能改写已返回计划。");
        }

        [Test]
        public void M00_02_M04_I03_PreparedCandidateUsesExistingReadOnlyWorldContract()
        {
            var property = typeof(IPreparedMutation).GetProperty(nameof(IPreparedMutation.CandidateWorld));
            Assert.That(property.PropertyType, Is.EqualTo(typeof(IWorkingWorldView)));
            Assert.That(property.CanRead, Is.True);
            Assert.That(property.CanWrite, Is.False);
            Assert.That(typeof(IStructurePlanner).GetMethod(nameof(IStructurePlanner.Plan)).GetParameters()[0].ParameterType,
                Is.EqualTo(property.PropertyType));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void M00_05_M04_I03_RealCandidateIsReadBeforeApplyAndExpiresWithPreparation(bool apply)
        {
            WorldLoadResult baseline = ConnectivityFixture.Load();
            var scene = FixtureCatalog.Scene(new[] { new InitialCell(0, 0, 102), new InitialCell(1, 0, 104) },
                new[] { new Vector2Int(0, 0) });
            var loaded = new WorldLoadResult(WorldResult.Success(), baseline.Config, scene, baseline.Materials, baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                Assert.That(world.BeginTick().IsSuccess, Is.True);
                var context = new TransactionContext(world.Published.Version, world.WorkingTick, TickStage.Commands);
                CellKey replaced = Key(0, 0);
                var result = world.PrepareReplace(replaced, 102, context);
                Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
                using (IPreparedMutation prepared = result.Prepared)
                {
                    IWorkingWorldView candidate = prepared.CandidateWorld;
                    Assert.That(candidate, Is.Not.Null);
                    Assert.That(candidate.Generation, Is.EqualTo(world.Generation));
                    Assert.That(candidate.WorkingTick, Is.EqualTo(world.WorkingTick));
                    Assert.That(candidate.OccupiedCells.Length, Is.EqualTo(2));
                    Assert.That(candidate.Read(replaced, out CellSnapshot cell).IsSuccess, Is.True);
                    Assert.That(cell.MaterialId, Is.EqualTo(102));
                    Assert.That(candidate.IsFixed(replaced), Is.False, "同 ID Replace 的候选必须撤销旧固定实例。");
                    Assert.That(world.IsFixed(replaced), Is.True, "准备阶段不能应用候选。");
                    IStructurePlanner planner = new ConnectivityAnalyzer(16, 8);
                    StructurePlanResult structure = planner.Plan(candidate, loaded.Materials, new[] { replaced });
                    Assert.That(structure.Result.IsSuccess, Is.True, structure.Result.Diagnostic.Message);
                    Assert.That(structure.Plan.Components.Single().Disposition, Is.EqualTo(StructureDisposition.ExtractFreeGrid));
                    Assert.That(world.Published.Version.CommittedTick, Is.Zero);
                    if (apply) Assert.That(prepared.Apply(context).IsSuccess, Is.True);
                    else prepared.Abort();
                    Assert.That(candidate.Read(replaced, out CellSnapshot expired).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
                    Assert.That(expired, Is.EqualTo(default(CellSnapshot)));
                    Assert.Throws<InvalidOperationException>(() => { int count = candidate.OccupiedCells.Length; });
                    Assert.That(world.IsFixed(replaced), Is.EqualTo(!apply));
                    Assert.That(world.Published.Version.CommittedTick, Is.Zero);
                }
            }
        }
    }
}
