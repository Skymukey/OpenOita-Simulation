using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode
{
    public sealed class FixtureCatalogTests
    {
        [Test]
        public void M00_03_ConnectionsStraddle127And128AndCrossHasNineExactCells()
        {
            var crossChunk = FixtureCatalog.Connection(true);
            Assert.That(crossChunk.Scene.Cells.Select(c => c.Position.x), Is.EqualTo(new[] { 127, 128, 129 }));
            Assert.That(crossChunk.Scene.FixedCells, Is.EqualTo(new[] { new Vector2Int(127, 10) }));
            Assert.That(crossChunk.Commands[0].Command.Region.Min.x, Is.EqualTo(12.8f).Within(1e-5));
            var cross = FixtureCatalog.Cross();
            Assert.That(cross.Scene.Cells.Select(c => c.Position), Is.EquivalentTo(new[]
            {
                new Vector2Int(4, 4), new Vector2Int(2, 4), new Vector2Int(3, 4), new Vector2Int(5, 4), new Vector2Int(6, 4),
                new Vector2Int(4, 2), new Vector2Int(4, 3), new Vector2Int(4, 5), new Vector2Int(4, 6)
            }));
            Assert.That(cross.Scene.FixedCells.Single(), Is.EqualTo(new Vector2Int(4, 4)));
            Assert.That(FixtureCatalog.Cross(true).Config.Limits.MaxDynamicBodies, Is.EqualTo(3));
            Assert.That(cross.Config.ChunkSize, Is.EqualTo(128));
        }
        [Test]
        public void M00_03_InitialDataOwnsCopyAndNormalizesGenerationOrder()
        {
            var original = FixtureCatalog.Cross().Scene;
            var reversed = original.Cells.Reverse().ToList();
            var copy = FixtureCatalog.Scene(reversed, original.FixedCells, original.InitialBurning);
            reversed.Clear();
            Assert.That(copy.Cells.Select(c => c.Position), Is.EqualTo(original.Cells.Select(c => c.Position)));
            Assert.Throws<NotSupportedException>(() => ((IList<InitialCell>)copy.Cells).Clear());
        }
        [Test]
        public void M00_04_RotationCommandsSelectOnlyIntendedLocalCenter()
        {
            foreach (bool end in new[] { false, true })
            {
                var fixture = FixtureCatalog.RotatingStrip(end);
                var state = fixture.BodiesAfterCreate.Single();
                Assert.That(state.Pose.AngleRadians, Is.EqualTo(Math.PI / 6).Within(1e-6));
                Assert.That(state.Motion.LinearVelocity, Is.EqualTo(new Vector2(0.25f, -0.1f)));
                for (int x = 0; x < 3; x++)
                    Assert.That(fixture.Commands.Single().Command.Region.ContainsCenter(FixtureCatalog.LocalCenter(state.Pose, x, 0)), Is.EqualTo(x == (end ? 2 : 1)));
                Assert.That(fixture.Observations[0].Stage, Is.EqualTo(TickStage.Structure));
            }
            Assert.That(FixtureCatalog.Ring().Scene.Cells.Count, Is.EqualTo(8));
            Assert.That(FixtureCatalog.Ring().Scene.Cells.Any(c => c.Position == new Vector2Int(21, 21)), Is.False);
        }
        [Test]
        public void M00_04_ContactPosesAndBurningCommandOrderAreReproducible()
        {
            var edge = FixtureCatalog.RotatedContact().BodiesAfterCreate.Single();
            var vertex = FixtureCatalog.RotatedContact(true).BodiesAfterCreate.Single();
            Assert.That(edge.Pose.AngleRadians, Is.EqualTo(Math.PI / 4).Within(1e-6));
            Assert.That(edge.Pose.Position, Is.EqualTo(new Vector2(1.05f, 1)));
            Assert.That(vertex.Pose.Position, Is.EqualTo(new Vector2(1.1f, 1)));
            var partial = FixtureCatalog.Burning("Partial");
            Assert.That(partial.Commands.Select(c => c.Tick), Is.EqualTo(new ulong[] { 3, 4, 4, 4 }));
            Assert.That(partial.Commands.Select(c => c.Command.Operation), Is.EqualTo(new[] { MaterialOperation.Spawn, MaterialOperation.Remove, MaterialOperation.Ignite, MaterialOperation.Ignite }));
        }
        [Test]
        public void M00_04_BurningSplitAndCollisionAndClosedDisplacementFixturesAreExplicit()
        {
            var split = FixtureCatalog.BurningSplit();
            Assert.That(split.CellsAfterCreate.Count, Is.EqualTo(3));
            Assert.That(split.CellsAfterCreate.All(c => c.State.FuelTicksRemaining == 3 && c.State.IsBurning), Is.True);
            Assert.That(FixtureCatalog.CollisionProbe("HighSpeed").BodiesAfterCreate.Single().Motion.LinearVelocity.y, Is.EqualTo(-5));
            Assert.That(FixtureCatalog.CollisionProbe("Corner").BodiesAfterCreate.Single().Pose.AngleRadians, Is.GreaterThan(0));
            var sealedWater = FixtureCatalog.DisplacementFailure();
            Assert.That(sealedWater.Scene.FixedCells.Count, Is.EqualTo(8));
            Assert.That(sealedWater.Scene.Cells.Count, Is.EqualTo(10));
            Assert.That(FixtureCatalog.DrawingSave(BaselineSources.Read()).Scene.Cells.Count, Is.EqualTo(175));
        }
        [Test]
        public void M00_04_ExportIncludesAllScenarioFamiliesAndReplaysWithoutGlobalSettings()
        {
            var exported = BaselineSources.Parse(FixtureExporter.Export(BaselineSources.Read()));
            var scenarios = (Newtonsoft.Json.Linq.JArray)exported["scenarios"];
            Assert.That(scenarios.Count, Is.EqualTo(31));
            Assert.That(scenarios.Select(s => (string)s["id"]).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(31));
            foreach (string id in new[] { "T1", "T3", "T4", "T5-Split", "T6-Water", "T7", "T8", "T9-Spread", "P2", "P3", "P4" })
                Assert.That(scenarios.Any(s => (string)s["id"] == id), Is.True, id);
            Assert.That((int)exported["T9材料副本"]["materials"][3]["ruleParameters"]["burnable"]["fuelTicks"], Is.EqualTo(5));
            Assert.That((int)exported["生命周期轮次"], Is.EqualTo(20));
            Assert.That(scenarios.All(s => (int)s["config"]["chunkSize"] == 128), Is.True);
        }
        [Test]
        public void M00_06_P1Has16384OfEachMaterial()
        {
            var ids = FixtureCatalog.P1();
            Assert.That(ids.Length, Is.EqualTo(65536));
            for (ushort id = 101; id <= 104; id++) Assert.That(ids.Count(value => value == id), Is.EqualTo(16384));
        }
        [Test]
        public void M00_06_P2MatchesEveryRangeAndMarker()
        {
            var fixture = FixtureCatalog.P2();
            Assert.That(fixture.Scene.Cells.Count, Is.EqualTo(11520));
            Assert.That(fixture.Scene.FixedCells.Count, Is.EqualTo(1280));
            Assert.That(fixture.Scene.InitialBurning.Count, Is.EqualTo(128));
            Assert.That(fixture.Scene.InitialBurning.All(p => p.y == 68 && p.x % 2 == 0), Is.True);
            Assert.That(fixture.Scene.Cells.Count(c => c.MaterialId == 101 && c.Position.x <= 127 && c.Position.y >= 4 && c.Position.y <= 67), Is.EqualTo(8192));
            Assert.That(fixture.Scene.Cells.Count(c => c.MaterialId == 103 && c.Position.x >= 128 && c.Position.x <= 191 && c.Position.y >= 4 && c.Position.y <= 35), Is.EqualTo(2048));
            Assert.That(fixture.ResetEveryTicks, Is.EqualTo(200));
        }
        [Test]
        public void M00_06_P3Has64UnfixedBlocksAndExactVelocitySigns()
        {
            var fixture = FixtureCatalog.P3();
            Assert.That(fixture.Config.GravityY, Is.Zero);
            Assert.That(fixture.Scene.Cells.Count, Is.EqualTo(2048));
            Assert.That(fixture.Scene.FixedCells.Count, Is.EqualTo(1024));
            Assert.That(fixture.BodiesAfterCreate.Count, Is.EqualTo(64));
            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++)
            {
                var body = fixture.BodiesAfterCreate[j * 8 + i];
                Assert.That(body.OriginalMinimum, Is.EqualTo(new Vector2Int(8 + 28 * i, 32 + 24 * j)));
                Assert.That(body.Motion.LinearVelocity.x, Is.EqualTo(i % 2 == 0 ? 0.25f : -0.25f));
                Assert.That(body.Motion.AngularVelocityRadians, Is.EqualTo((j % 2 == 0 ? 1 : -1) * Math.PI / 6).Within(1e-6));
            }
        }
        [Test]
        public void M00_06_P4Queues64IndividualOddCellsInAscendingOrder()
        {
            var fixture = FixtureCatalog.P4();
            Assert.That(fixture.Scene.Cells.Count, Is.EqualTo(128));
            Assert.That(fixture.Scene.FixedCells.Single(), Is.EqualTo(new Vector2Int(17, 100)));
            Assert.That(fixture.Commands.Count, Is.EqualTo(64));
            for (int index = 0; index < 64; index++)
            {
                Assert.That(fixture.Commands[index].Tick, Is.EqualTo(1));
                Assert.That(fixture.Commands[index].Command.Operation, Is.EqualTo(MaterialOperation.Remove));
                Assert.That(fixture.Commands[index].Command.Region.Min.x, Is.EqualTo((17 + index * 2) * 0.1f));
            }
        }
    }
}
