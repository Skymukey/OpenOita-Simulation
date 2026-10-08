using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.V2;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class LocalMaterialRulesTests
    {
        [TestCase(1)]
        [TestCase(4)]
        public void MixedFlowMatchesSerialForSixtyFourTicksAcrossSimulationAndDisplayBoundaries(int workerCount)
        {
            int previousWorkers = JobsUtility.JobWorkerCount;
            try
            {
                JobsUtility.JobWorkerCount = System.Math.Min(workerCount, JobsUtility.JobWorkerMaximumCount);
                using (var definitions = CreateDefinitions(2, 200))
                using (var serialGrid = CreateMixedBoundaryGrid(definitions))
                using (var jobsGrid = CreateMixedBoundaryGrid(definitions))
                using (var serial = new LocalMaterialRules(serialGrid) { Parallel = false })
                using (var jobs = new LocalMaterialRules(jobsGrid) { Parallel = true })
                {
                    for (uint tick = 1; tick <= 64; tick++)
                    {
                        serial.Execute(tick, false); jobs.Execute(tick, false);
                        Assert.That(jobs.LastWork.Moves, Is.EqualTo(serial.LastWork.Moves), $"water tick {tick}");
                        serial.Execute(tick, true); jobs.Execute(tick, true);
                        Assert.That(jobs.LastWork.Moves, Is.EqualTo(serial.LastWork.Moves), $"gas tick {tick}");
                        for (int y = 0; y < serialGrid.Height; y++)
                            for (int x = 0; x < serialGrid.Width; x++)
                            {
                                GridCell expected = serialGrid.Read(x, y), actual = jobsGrid.Read(x, y);
                                if (actual.MaterialId != expected.MaterialId || actual.Flags != expected.Flags ||
                                    actual.NextMoveTick != expected.NextMoveTick || actual.ProcessedTick != expected.ProcessedTick ||
                                    actual.ComponentHandle != expected.ComponentHandle)
                                    Assert.Fail($"worker={workerCount}, tick={tick}, cell=({x},{y})");
                                if (actual.ComponentHandle != 0 &&
                                    (actual.Cold.X != expected.Cold.X || actual.Cold.Y != expected.Cold.Y ||
                                     actual.Cold.ExpiryTick != expected.Cold.ExpiryTick))
                                    Assert.Fail($"cold component mismatch worker={workerCount}, tick={tick}, cell=({x},{y})");
                            }
                        Assert.That(jobsGrid.CellCount, Is.EqualTo(serialGrid.CellCount));
                    }
                }
            }
            finally { JobsUtility.JobWorkerCount = previousWorkers; }
        }

        private static MaterialGrid CreateMixedBoundaryGrid(NativeArray<CellMaterialDefinition> definitions)
        {
            var grid = new MaterialGrid(160, 96, definitions);
            GridCell wall = grid.CreateCell(102);
            for (int x = 0; x < grid.Width; x++) grid.Write(x, 0, wall);
            for (int y = 25; y < 80; y++) grid.Write(80, y, wall);
            foreach (int boundaryX in new[] { 32, 64, 128 })
                foreach (int boundaryY in new[] { 32, 64 })
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            grid.Write(boundaryX + dx, boundaryY + dy,
                                grid.CreateCell((ushort)((dx + dy & 1) == 0 ? 101 : 103)));
            return grid;
        }

        [Test]
        public void SerialAndJobsAgreeAcrossThirtyOneThirtyTwoSixtyThreeSixtyFour()
        {
            using (var definitions = CreateDefinitions(1, 20))
            using (var serialGrid = CreateBoundaryWaterGrid(definitions))
            using (var jobsGrid = CreateBoundaryWaterGrid(definitions))
            using (var serial = new LocalMaterialRules(serialGrid) { Parallel = false })
            using (var jobs = new LocalMaterialRules(jobsGrid) { Parallel = true })
            {
                serial.Execute(1, false);
                jobs.Execute(1, false);

                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 96; x++)
                    {
                        GridCell left = serialGrid.Read(x, y);
                        GridCell right = jobsGrid.Read(x, y);
                        Assert.That(right.MaterialId, Is.EqualTo(left.MaterialId), $"material ({x},{y})");
                        Assert.That(right.Flags, Is.EqualTo(left.Flags), $"flags ({x},{y})");
                        Assert.That(right.NextMoveTick, Is.EqualTo(left.NextMoveTick), $"due ({x},{y})");
                        Assert.That(right.ProcessedTick, Is.EqualTo(left.ProcessedTick), $"stamp ({x},{y})");
                    }
                Assert.That(jobsGrid.CellCount, Is.EqualTo(serialGrid.CellCount));
                Assert.That(jobs.LastWork.Moves, Is.EqualTo(serial.LastWork.Moves));
                Assert.That(jobs.LastWork.Attempts, Is.EqualTo(serial.LastWork.Attempts));
            }
        }

        [Test]
        public void MoveIntervalAndStampDeferTheNextAttempt()
        {
            using (var definitions = CreateDefinitions(2, 20))
            using (var grid = new MaterialGrid(4, 8, definitions))
            using (var rules = new LocalMaterialRules(grid) { Parallel = false })
            {
                GridCell water = grid.CreateCell(101);
                grid.Write(1, 6, water);
                rules.Execute(1, false);
                Assert.That(grid.Read(1, 5).MaterialId, Is.EqualTo((ushort)101));
                Assert.That(grid.Read(1, 5).NextMoveTick, Is.EqualTo(3u));
                Assert.That(grid.Read(1, 5).ProcessedTick, Is.EqualTo(1u));

                rules.Execute(2, false);
                Assert.That(grid.Read(1, 5).MaterialId, Is.EqualTo((ushort)101));
                Assert.That(rules.LastWork.Moves, Is.Zero);

                rules.Execute(3, false);
                Assert.That(grid.Read(1, 4).MaterialId, Is.EqualTo((ushort)101));
                Assert.That(grid.Read(1, 4).ProcessedTick, Is.EqualTo(3u));
            }
        }

        [Test]
        public void WaterDoesNotCrossAClosedWallAndCellCountIsConserved()
        {
            using (var definitions = CreateDefinitions(1, 20))
            using (var grid = new MaterialGrid(5, 5, definitions))
            using (var rules = new LocalMaterialRules(grid) { Parallel = false })
            {
                GridCell wall = grid.CreateCell(102);
                grid.Write(0, 2, wall); grid.Write(2, 2, wall);
                grid.Write(0, 1, wall); grid.Write(1, 1, wall); grid.Write(2, 1, wall);
                grid.Write(0, 3, wall); grid.Write(1, 3, wall); grid.Write(2, 3, wall);
                GridCell water = grid.CreateCell(101);
                grid.Write(1, 2, water);
                int before = Count(grid, 101);

                rules.Execute(1, false);

                Assert.That(Count(grid, 101), Is.EqualTo(before));
                Assert.That(grid.Read(1, 2).MaterialId, Is.EqualTo((ushort)101));
                Assert.That(grid.Read(1, 1).MaterialId, Is.EqualTo((ushort)102));
                Assert.That(rules.LastWork.Moves, Is.Zero);
            }
        }

        [Test]
        public void OpeningAClosedCellWakesSleepingWater()
        {
            using (var definitions = CreateDefinitions(1, 20))
            using (var grid = new MaterialGrid(5, 5, definitions))
            using (var rules = new LocalMaterialRules(grid) { Parallel = false })
            {
                GridCell wall = grid.CreateCell(102);
                grid.Write(0, 2, wall); grid.Write(2, 2, wall); grid.Write(1, 1, wall);
                GridCell water = grid.CreateCell(101);
                grid.Write(1, 2, water);
                rules.Execute(1, false);
                Assert.That(rules.LastWork.Sleeping, Is.GreaterThan(0));
                Assert.That(grid.Read(1, 2).MaterialId, Is.EqualTo((ushort)101));

                GridCell empty = default;
                grid.Write(1, 1, empty);
                rules.Execute(2, false);
                Assert.That(grid.Read(1, 1).MaterialId, Is.EqualTo((ushort)101));
                Assert.That(grid.Read(1, 2).IsEmpty, Is.True);
            }
        }

        [Test]
        public void GasMoveKeepsColdHandleAndExpiry()
        {
            using (var definitions = CreateDefinitions(1, 20))
            using (var grid = new MaterialGrid(8, 8, definitions) { Tick = 10, GridHandle = 4 })
            using (var rules = new LocalMaterialRules(grid) { Parallel = false })
            {
                GridCell gas = grid.CreateCell(103);
                grid.Write(3, 1, gas);
                GridCell before = grid.Read(3, 1);
                int handle = before.ComponentHandle;
                CellCold cold = grid.ColdStore.Read(handle);
                rules.Execute(11, true);
                GridCell after = grid.Read(3, 2);
                CellCold moved = grid.ColdStore.Read(handle);

                Assert.That(after.MaterialId, Is.EqualTo((ushort)103));
                Assert.That(after.ComponentHandle, Is.EqualTo(handle));
                Assert.That(moved.ExpiryTick, Is.EqualTo(cold.ExpiryTick));
                Assert.That(moved.FuelRemaining, Is.EqualTo(cold.FuelRemaining));
                Assert.That(moved.GridHandle, Is.EqualTo(4));
                Assert.That(moved.X, Is.EqualTo(3));
                Assert.That(moved.Y, Is.EqualTo(2));
            }
        }

        private static NativeArray<CellMaterialDefinition> CreateDefinitions(uint moveInterval, uint gasLifetime)
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[101] = new CellMaterialDefinition
            {
                Id = 101, Kind = MaterialKind.Liquid, Rules = RuleMask.LiquidFlow, MoveInterval = moveInterval
            };
            definitions[102] = new CellMaterialDefinition
            {
                Id = 102, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 1
            };
            definitions[103] = new CellMaterialDefinition
            {
                Id = 103, Kind = MaterialKind.Gas, Rules = RuleMask.GasDrift, Lifetime = gasLifetime
            };
            return definitions;
        }

        private static MaterialGrid CreateBoundaryWaterGrid(NativeArray<CellMaterialDefinition> definitions)
        {
            var grid = new MaterialGrid(96, 8, definitions);
            GridCell water = grid.CreateCell(101);
            grid.Write(31, 6, water); grid.Write(32, 6, water);
            grid.Write(63, 6, water); grid.Write(64, 6, water);
            return grid;
        }

        private static int Count(MaterialGrid grid, ushort materialId)
        {
            int count = 0;
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                    if (grid.Read(x, y).MaterialId == materialId) count++;
            return count;
        }
    }
}
