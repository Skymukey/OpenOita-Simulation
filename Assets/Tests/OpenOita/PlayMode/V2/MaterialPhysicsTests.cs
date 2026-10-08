using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;
using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Spatial;
using OpenOita.V2;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.Tests.PlayMode.V2
{
    public sealed class MaterialPhysicsTests
    {
        [Test]
        public void ConcentratedCrossTileSupportRemovalDetachesExactlyTwoCompleteBlocks()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid,
                Rules = RuleMask.Structure, ConnectionGroup = 3, Mass = 1 };
            var config = new WorldConfig(2, 512, 256, 32, 1f, 0.02f, 0, 1,
                new WorldLimits(16384, 64, 16384, 100000, 16384, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, false);
            try
            {
                for (int y = 112; y < 144; y++)
                {
                    for (int x = 64; x < 192; x++) grid.Write(x, y, new GridCell { MaterialId = 1 });
                    for (int x = 193; x < 321; x++) grid.Write(x, y, new GridCell { MaterialId = 1 });
                }
                grid.Write(192, 128, new GridCell { MaterialId = 1, Flags = GridCell.FixedFlag });
                physics.RebuildStructures(0);
                Assert.That(physics.BodyCount, Is.Zero);
                Assert.That(grid.CellCount, Is.EqualTo(8193));

                grid.Write(192, 128, default);
                physics.RebuildStructures(1);
                Assert.That(physics.BodyCount, Is.EqualTo(2));
                Assert.That(grid.CellCount, Is.Zero, "All unsupported material must leave the static grid.");
                for (int i = 0; i < 2; i++)
                    Assert.That(physics.GetBody(i).Grid.CellCount, Is.EqualTo(4096));
            }
            finally { physics.Dispose(); grid.Dispose(); definitions.Dispose(); }
        }

        [Test]
        public void RebuildStructures_ExtractsFreeGroupAndKeepsFixedTerrain()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 64, 32, 128, 0.1f, 0.02f, -9.81f, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 16));
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                grid.Write(2, 2, new GridCell { MaterialId = 1 });
                grid.Write(3, 2, new GridCell { MaterialId = 1 });
                grid.Write(20, 2, new GridCell { MaterialId = 1, Flags = GridCell.FixedFlag });
                grid.Write(21, 2, new GridCell { MaterialId = 1 });
                physics.RebuildStructures(0);

                Assert.That(physics.BodyCount, Is.EqualTo(1));
                BodyV2 body = physics.GetBody(0);
                Assert.That(body.ShapeCount, Is.EqualTo(1));
                Assert.That(grid.Read(2, 2).IsEmpty, Is.True);
                Assert.That(grid.Read(3, 2).IsEmpty, Is.True);
                Assert.That(grid.Read(20, 2).IsFixed, Is.True);
                Assert.That(grid.Read(21, 2).MaterialId, Is.EqualTo(1));
                Assert.That(body.Grid.GridHandle, Is.Not.EqualTo(grid.GridHandle));
                Assert.That(physics.TryReadBodyCell(body.Grid.GridHandle, 0, 0, out GridCell moved), Is.True);
                Assert.That(moved.MaterialId, Is.EqualTo(1));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void FixedTerrainBridge_FocusesOnlyNearDynamicBodyAndReusesLogicalBudget()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 128, 64, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                grid.Write(10, 10, new GridCell { MaterialId = 1, Flags = GridCell.FixedFlag });
                physics.RebuildStructures(0);
                Assert.That(physics.LogicalFixedTerrainShapeCount, Is.GreaterThan(0));
                Assert.That(physics.ActiveFixedTerrainShapeCount, Is.EqualTo(0));

                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                var body = new BodyV2(77, bodyGrid, new BodyPose(new Vector2(10, 10), 0), default);
                physics.AddBody(body);
                physics.RebuildStructures(1);
                Assert.That(physics.ActiveFixedTerrainShapeCount, Is.GreaterThan(0));

                body.Pose = new BodyPose(new Vector2(100, 40), 0);
                physics.RebuildStructures(2);
                Assert.That(physics.ActiveFixedTerrainShapeCount, Is.EqualTo(0));
                Assert.That(physics.LogicalFixedTerrainShapeCount, Is.GreaterThan(0));
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void ConfigureBodyMotion_UpdatesSolverAndSleepState()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(78, bodyGrid, new BodyPose(Vector2.zero, 0), default));
                physics.RebuildStructures(0);
                Assert.That(physics.ConfigureBodyMotion(78, new BodyMotion(new Vector2(1, 0), 0.5f), false), Is.True);
                Assert.That(physics.GetBody(0).Motion.LinearVelocity.x, Is.EqualTo(1));
                Assert.That(physics.IsBodySleeping(78), Is.False);
                Assert.That(physics.ConfigureBodyMotion(78, default, true), Is.True);
                Assert.That(physics.IsBodySleeping(78), Is.True);
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void Step_ClampsFreeFallVelocityAfterEveryPhysicsSubstep()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, -100f, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 1f, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(79, bodyGrid, new BodyPose(new Vector2(2, 4), 0), default));
                physics.RebuildStructures(0);
                physics.Step(1);
                BodyMotion motion = physics.GetBody(0).Motion;
                Assert.That(motion.LinearVelocity.magnitude, Is.LessThanOrEqualTo(1.0001f));
                Assert.That(Mathf.Abs(motion.AngularVelocityRadians), Is.LessThanOrEqualTo(180f * Mathf.Deg2Rad));
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void DynamicCoverage_UsesStrictPositiveAreaAndRefreshesAfterPoseOnlyMove()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 1f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                var body = new BodyV2(82, bodyGrid, new BodyPose(new Vector2(1, 0), 0), default);
                physics.AddBody(body);
                physics.RebuildStructures(0);
                physics.Step(1);

                // The body [1,2]x[0,1] only edge-touches main cell [0,1]x[0,1].
                Assert.That(grid.IsDynamic(0, 0), Is.False);
                Assert.That(grid.IsDynamic(1, 0), Is.True);

                Assert.That(physics.ConfigureBodyMotion(82, new BodyMotion(new Vector2(1, 0), 0), false), Is.True);
                physics.Step(2);
                Assert.That(grid.IsDynamic(1, 0), Is.False);
                Assert.That(grid.IsDynamic(2, 0), Is.True);
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void DynamicCoverage_MatchesExactPositiveOverlapAcrossAnglesAndLargeCoordinates()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 4096, 4096, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            BodyPose[] poses =
            {
                new BodyPose(new Vector2(8.25f, 8.75f), 0.125f),
                new BodyPose(new Vector2(12.375f, 14.625f), Mathf.PI * 0.25f),
                new BodyPose(new Vector2(1630.25f, 1630.5f), 1.0471976f),
                new BodyPose(new Vector2(1630.75f, 1631.125f), Mathf.PI * 0.75f)
            };
            try
            {
                for (int caseIndex = 0; caseIndex < poses.Length; caseIndex++)
                {
                    var grid = new MaterialGrid(config, definitions);
                    var bodyGrid = new MaterialGrid(2, 2, definitions);
                    var physics = new MaterialPhysics(config, grid, Vector2.zero, false);
                    var body = new BodyV2((ulong)(1000 + caseIndex), bodyGrid, poses[caseIndex], default);
                    try
                    {
                        for (int y = 0; y < 2; y++)
                            for (int x = 0; x < 2; x++)
                                bodyGrid.Write(x, y, new GridCell { MaterialId = 1 });
                        physics.AddBody(body);
                        physics.RebuildStructures(0);
                        physics.Step(1);

                        BodyPose actualPose = body.Pose;
                        int minX = Math.Max(0, Mathf.FloorToInt(actualPose.Position.x) - 5);
                        int maxX = Math.Min(config.Width - 1, Mathf.CeilToInt(actualPose.Position.x) + 8);
                        int minY = Math.Max(0, Mathf.FloorToInt(actualPose.Position.y) - 5);
                        int maxY = Math.Min(config.Height - 1, Mathf.CeilToInt(actualPose.Position.y) + 8);
                        for (int y = minY; y <= maxY; y++)
                            for (int x = minX; x <= maxX; x++)
                            {
                                var worldCell = new CellGeometry(
                                    new CellKey(0, new CellPositionKey(OwnerKind.Grid, 0, x, y)),
                                    new BodyPose(Vector2.zero, 0), new Vector2Int(x, y), config.CellSize);
                                bool expected = false;
                                for (int by = 0; by < 2 && !expected; by++)
                                    for (int bx = 0; bx < 2; bx++)
                                    {
                                        var bodyCell = new CellGeometry(
                                            new CellKey(0, new CellPositionKey(OwnerKind.Body, body.Id, bx, by)),
                                            actualPose, new Vector2Int(bx, by), config.CellSize);
                                        expected = ExactCellGeometry.PositiveOverlap(bodyCell, worldCell);
                                        if (expected) break;
                                    }
                                Assert.That(grid.IsDynamic(x, y), Is.EqualTo(expected),
                                    $"case={caseIndex}, cell=({x},{y}), pose={actualPose.Position}/{actualPose.AngleRadians}");
                            }
                    }
                    finally
                    {
                        physics.Dispose();
                        bodyGrid.Dispose();
                        grid.Dispose();
                    }
                }
            }
            finally
            {
                definitions.Dispose();
            }
        }

        [Test]
        public void DynamicCoverage_DeltaRefreshKeepsSharedCellWhenOneBodyMicroMoves()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var firstGrid = new MaterialGrid(1, 1, definitions);
            var secondGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                firstGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                secondGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(83, firstGrid, new BodyPose(new Vector2(2, 2), 0), default));
                physics.AddBody(new BodyV2(84, secondGrid, new BodyPose(new Vector2(2.99f, 2), 0), default));
                physics.RebuildStructures(0);
                physics.Step(1);
                Assert.That(grid.IsDynamic(2, 2), Is.True);

                Assert.That(physics.ConfigureBodyMotion(83, new BodyMotion(new Vector2(0.01f, 0), 0), false), Is.True);
                physics.Step(2);
                Assert.That(grid.IsDynamic(2, 2), Is.True);
            }
            finally
            {
                physics.Dispose();
                firstGrid.Dispose();
                secondGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RemovingFixedSupport_WakesSleepingBodyInAffectedPage()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                grid.Write(0, 0, new GridCell { MaterialId = 1, Flags = GridCell.FixedFlag });
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(80, bodyGrid, new BodyPose(new Vector2(0, 1), 0), default));
                physics.RebuildStructures(0);
                Assert.That(physics.ConfigureBodyMotion(80, default, true), Is.True);
                Assert.That(physics.IsBodySleeping(80), Is.True);

                grid.Write(0, 0, default);
                physics.RebuildStructures(1);
                Assert.That(physics.IsBodySleeping(80), Is.False);
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RemovingLastDynamicCell_RetiresBodyAndReleasesRuntimeBridge()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(81, bodyGrid, new BodyPose(Vector2.zero, 0), default));
                physics.RebuildStructures(0);
                Assert.That(physics.BodyCount, Is.EqualTo(1));
                bodyGrid.Write(0, 0, default);
                physics.RebuildStructures(1);
                Assert.That(physics.BodyCount, Is.EqualTo(0));
            }
            finally
            {
                physics.Dispose();
                // This explicitly supplied grid remains caller-owned when the body retires.
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void TopologyChange_SplitsDisconnectedDynamicBody()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = new WorldConfig(1, 64, 32, 128, 0.1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 16));
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                grid.Write(5, 5, new GridCell { MaterialId = 1 });
                grid.Write(6, 5, new GridCell { MaterialId = 1 });
                grid.Write(7, 5, new GridCell { MaterialId = 1 });
                physics.RebuildStructures(0);
                Assert.That(physics.BodyCount, Is.EqualTo(1));
                BodyV2 body = physics.GetBody(0);
                body.Grid.Write(1, 0, default);
                physics.RebuildStructures(1);
                Assert.That(physics.BodyCount, Is.EqualTo(2));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void DynamicBody_CrossTileSingleComponentSurvivesLocalEdit()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = CrossTileBodyConfig();
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                FillCrossTileBar(grid);
                physics.RebuildStructures(0);
                Assert.That(physics.BodyCount, Is.EqualTo(1));
                BodyV2 body = physics.GetBody(0);
                Assert.That(body.Grid.CellCount, Is.EqualTo(64));

                long beforeEditVisits = physics.BodyStructureCellVisits;
                body.Grid.Write(63, 0, default);
                physics.RebuildStructures(1);

                // The bar crosses the 32-cell boundary but remains one connected
                // component.  A local-root count would incorrectly throw it away.
                Assert.That(physics.BodyCount, Is.EqualTo(1));
                Assert.That(physics.GetBody(0).Grid.CellCount, Is.EqualTo(63));
                Assert.That(physics.GetBody(0).Grid.Read(62, 0).MaterialId, Is.EqualTo(1));
                Assert.That(physics.BodyStructureCellVisits, Is.GreaterThan(beforeEditVisits));

                long stableVisits = physics.BodyStructureCellVisits;
                physics.RebuildStructures(2);
                Assert.That(physics.BodyStructureCellVisits, Is.EqualTo(stableVisits));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void DynamicBody_CrossTileSplitPreservesAllMaterial()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = CrossTileBodyConfig();
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                FillCrossTileBar(grid);
                physics.RebuildStructures(0);
                Assert.That(physics.BodyCount, Is.EqualTo(1));
                BodyV2 body = physics.GetBody(0);
                body.Grid.Write(31, 0, default);

                physics.RebuildStructures(1);

                Assert.That(physics.BodyCount, Is.EqualTo(2));
                int totalCells = 0;
                for (int i = 0; i < physics.BodyCount; i++) totalCells += physics.GetBody(i).Grid.CellCount;
                Assert.That(totalCells, Is.EqualTo(63));
                Assert.That(physics.GetBody(0).Grid.CellCount, Is.GreaterThan(0));
                Assert.That(physics.GetBody(1).Grid.CellCount, Is.GreaterThan(0));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void DynamicBody_NoTopologyChangeDoesNotRebuildPersistentConnectivity()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var config = CrossTileBodyConfig();
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                FillCrossTileBar(grid);
                physics.RebuildStructures(0);
                long cells = physics.BodyStructureCellVisits;
                long edges = physics.BodyStructureEdgeVisits;

                physics.RebuildStructures(1);

                Assert.That(physics.BodyStructureCellVisits, Is.EqualTo(cells));
                Assert.That(physics.BodyStructureEdgeVisits, Is.EqualTo(edges));
                Assert.That(physics.BodyCount, Is.EqualTo(1));
                Assert.That(physics.GetBody(0).Grid.CellCount, Is.EqualTo(64));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        private static WorldConfig CrossTileBodyConfig()
        {
            return new WorldConfig(1, 64, 2, 32, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 16));
        }

        private static void FillCrossTileBar(MaterialGrid grid)
        {
            for (int x = 0; x < 64; x++) grid.Write(x, 0, new GridCell { MaterialId = 1 });
        }

        [Test]
        public void CollectWet_SuspendsWaterAndRestoresAfterNearbyOccupancyWakesIt()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            definitions[2] = new CellMaterialDefinition { Id = 2, Kind = MaterialKind.Liquid, Rules = RuleMask.LiquidFlow, Mass = 1 };
            var config = new WorldConfig(1, 8, 8, 128, 0.1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions) { GridHandle = 1 };
            var bodyGrid = new MaterialGrid(1, 1, definitions, grid.ColdStore) { GridHandle = 2 };
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                int handle = grid.ColdStore.Create(new CellCold { MaterialId = 2, ExpiryTick = 50 });
                grid.Write(0, 0, new GridCell { MaterialId = 2, ComponentHandle = handle,
                    Cold = grid.ColdStore.Read(handle) });
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(1, bodyGrid, new BodyPose(Vector2.zero, 0), new BodyMotion(Vector2.zero, 0)));
                physics.RebuildStructures(0);

                physics.CollectWet(1, true);
                Assert.That(physics.WetContacts.Length, Is.EqualTo(1));
                Assert.That(physics.WetComponents.Length, Is.EqualTo(1));
                Assert.That(physics.SuspendedCount, Is.EqualTo(0));
                physics.CollectWet(1, false);
                Assert.That(physics.SuspendedCount, Is.EqualTo(1));
                Assert.That(grid.Read(0, 0).IsEmpty, Is.True);
                Assert.That(grid.ColdStore.Read(handle).GridHandle, Is.EqualTo(-1));

                for (int y = 0; y <= 4; y++) for (int x = 0; x <= 4; x++) grid.SetDynamic(x, y, true);
                Assert.That(physics.RestoreFluids(2, 4096), Is.EqualTo(0));
                for (int y = 0; y <= 4; y++) for (int x = 0; x <= 4; x++) grid.SetDynamic(x, y, false);
                physics.WakeSuspendedTile(grid.TileId(0, 0));
                Assert.That(physics.RestoreFluids(3, 4096), Is.EqualTo(1));
                Assert.That(physics.SuspendedCount, Is.EqualTo(0));
                Assert.That(grid.Read(0, 0).MaterialId, Is.EqualTo(2));
                Assert.That(grid.ColdStore.Read(handle).GridHandle, Is.EqualTo(1));
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void ExpireSuspended_UsesComponentIndexAndRemovesRecord()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            definitions[2] = new CellMaterialDefinition { Id = 2, Kind = MaterialKind.Liquid, Rules = RuleMask.LiquidFlow, Mass = 1 };
            var config = new WorldConfig(1, 4, 4, 128, 0.1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions) { GridHandle = 1 };
            var bodyGrid = new MaterialGrid(1, 1, definitions, grid.ColdStore) { GridHandle = 2 };
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                int handle = grid.ColdStore.Create(new CellCold { MaterialId = 2, ExpiryTick = 10 });
                grid.Write(0, 0, new GridCell { MaterialId = 2, ComponentHandle = handle,
                    Cold = grid.ColdStore.Read(handle) });
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(1, bodyGrid, new BodyPose(Vector2.zero, 0), default));
                physics.RebuildStructures(0);
                physics.CollectWet(1, false);
                Assert.That(physics.SuspendedCount, Is.EqualTo(1));
                Assert.That(physics.ExpireSuspended(handle), Is.True);
                Assert.That(physics.SuspendedCount, Is.EqualTo(0));
                Assert.That(grid.ColdStore.IsLive(handle), Is.False);
                Assert.That(physics.ExpireSuspended(handle), Is.False);
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void CollectIgnitionTargets_UsesLocalCrossOwnerContactsAndDeferredBits()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Burnable, Fuel = 20 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 64, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var firstGrid = new MaterialGrid(1, 1, definitions);
            var secondGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            var contacts = new NativeList<CellContact>(16, Allocator.Persistent);
            try
            {
                grid.Write(0, 0, new GridCell { MaterialId = 1, Flags = GridCell.BurningFlag });
                grid.Write(1, 0, new GridCell { MaterialId = 1 });
                firstGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                secondGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(1, firstGrid, new BodyPose(Vector2.zero, 0), default));
                physics.AddBody(new BodyV2(2, secondGrid, new BodyPose(Vector2.zero, 0), default));
                physics.RebuildStructures(0);

                CellKey source = new CellKey(7, new CellPositionKey(OwnerKind.Grid, 0, 0, 0));
                Assert.That(physics.CollectIgnitionTargets(source, contacts), Is.EqualTo(2));
                Assert.That(contacts[0].First.Position.OwnerKind, Is.EqualTo(OwnerKind.Grid));
                Assert.That(contacts[0].Second.Position.OwnerKind, Is.EqualTo(OwnerKind.Body));
                for (int i = 0; i < contacts.Length; i++) Assert.That(physics.QueueDeferredIgnition(contacts[i].Second, 1), Is.True);
                Assert.That(physics.QueueDeferredIgnition(contacts[0].Second, 1), Is.False);

                firstGrid.Write(0, 0, new GridCell { MaterialId = 1, Flags = GridCell.BurningFlag });
                contacts.Clear();
                CellKey bodySource = new CellKey(7, new CellPositionKey(OwnerKind.Body, 1, 0, 0));
                Assert.That(physics.CollectIgnitionTargets(bodySource, contacts), Is.EqualTo(2));
                bool sawGrid = false, sawOtherBody = false;
                for (int i = 0; i < contacts.Length; i++)
                {
                    sawGrid |= contacts[i].Second.Position.OwnerKind == OwnerKind.Grid;
                    sawOtherBody |= contacts[i].Second.Position.OwnerKind == OwnerKind.Body && contacts[i].Second.Position.BodyId == 2;
                }
                Assert.That(sawGrid, Is.True);
                Assert.That(sawOtherBody, Is.True);
            }
            finally
            {
                contacts.Dispose();
                physics.Dispose();
                firstGrid.Dispose();
                secondGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void PreflightStructureDraft_RejectsCapacityWithoutMutatingWorld()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 1, 1, 1, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            var draft = new NativeArray<GridCell>(4, Allocator.Persistent);
            try
            {
                draft[0] = new GridCell { MaterialId = 1 };
                draft[2] = new GridCell { MaterialId = 1 };
                WorldResult result = physics.PreflightStructureDraft(draft, 4, 1, true, out StructureCapacityV2 estimate);
                Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                Assert.That(estimate.DynamicShapes, Is.EqualTo(2));
                Assert.That(physics.BodyCount, Is.EqualTo(0));
                Assert.That(grid.CellCount, Is.EqualTo(0));
                Assert.That(draft[0].MaterialId, Is.EqualTo(1));
                Assert.That(draft[2].MaterialId, Is.EqualTo(1));
            }
            finally
            {
                draft.Dispose();
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void PreflightCommand_RemoveFixedSupportRejectsPotentialSplitWithoutMutation()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 0, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                grid.Write(0, 0, new GridCell { MaterialId = 1, Flags = GridCell.FixedFlag });
                grid.Write(0, 1, new GridCell { MaterialId = 1 });
                physics.RebuildStructures(0);
                var command = new MaterialCommand(MaterialOperation.Remove,
                    new WorldRect(Vector2.zero, Vector2.one), 0, 1);

                WorldResult result = physics.PreflightCommand(command, out StructureCapacityV2 projected);
                Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                Assert.That(projected.DynamicBodies, Is.EqualTo(1));
                Assert.That(physics.BodyCount, Is.EqualTo(0));
                Assert.That(grid.Read(0, 0).IsFixed, Is.True);
                Assert.That(grid.Read(0, 1).MaterialId, Is.EqualTo(1));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void PreflightCommand_ReplaceMatchesOccupiedBodyCenterOutsideMainGrid()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 3 };
            var config = new WorldConfig(1, 8, 8, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 4, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var bodyGrid = new MaterialGrid(1, 1, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                bodyGrid.Write(0, 0, new GridCell { MaterialId = 1 });
                physics.AddBody(new BodyV2(9, bodyGrid, new BodyPose(new Vector2(100, 100), 0), default));
                physics.RebuildStructures(0);
                var command = new MaterialCommand(MaterialOperation.Replace,
                    new WorldRect(new Vector2(100, 100), new Vector2(101, 101)), 1, 1);

                WorldResult result = physics.PreflightCommand(command, out StructureCapacityV2 projected);
                Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
                Assert.That(projected.DynamicBodies, Is.EqualTo(1));
                Assert.That(grid.CellCount, Is.EqualTo(0));
                Assert.That(bodyGrid.Read(0, 0).MaterialId, Is.EqualTo(1));
            }
            finally
            {
                physics.Dispose();
                bodyGrid.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void PreflightCommands_ChargesMultipleSpawnComponentsTogether()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 7 };
            var config = new WorldConfig(1, 8, 4, 128, 1f, 0.02f, 0, 1,
                new WorldLimits(65536, 1, 256, 4096, 65536, 5, 180, 8, 4));
            var grid = new MaterialGrid(config, definitions);
            var physics = new MaterialPhysics(config, grid, Vector2.zero, true);
            try
            {
                var commands = new[]
                {
                    new MaterialCommand(MaterialOperation.Spawn, new WorldRect(Vector2.zero, Vector2.one), 1, 1),
                    new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(2, 0), new Vector2(3, 1)), 1, 1)
                };
                physics.RebuildStructures(0);
                WorldResult result = physics.PreflightCommands(commands, out StructureCapacityV2 projected);
                Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                Assert.That(projected.DynamicBodies, Is.EqualTo(2));
                Assert.That(grid.CellCount, Is.EqualTo(0));
                Assert.That(physics.BodyCount, Is.EqualTo(0));
            }
            finally
            {
                physics.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }
    }
}
