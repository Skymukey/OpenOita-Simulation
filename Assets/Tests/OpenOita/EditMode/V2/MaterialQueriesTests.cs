using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;
using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.V2;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class MaterialQueriesTests
    {
        private NativeArray<CellMaterialDefinition> _definitions;
        private MaterialGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            _definitions[101] = new CellMaterialDefinition { Id = 101, Kind = MaterialKind.Liquid, Rules = RuleMask.LiquidFlow, Fuel = 17 };
            _grid = new MaterialGrid(65, 65, _definitions) { Tick = 10 };
        }

        [TearDown]
        public void TearDown()
        {
            if (_grid != null) _grid.Dispose();
            if (_definitions.IsCreated) _definitions.Dispose();
        }

        [Test]
        public void PointUsesCellCoordinatesWithoutAllocatingMissingTiles()
        {
            _grid.Write(32, 32, _grid.CreateCell(101));
            using (var queries = new MaterialQueries(_grid, null, Vector2.zero, 1, 16))
            {
                PointQueryResult hit = queries.QueryPoint(new WorldVersion(3, 10), new Vector2(32.5f, 32.5f));
                Assert.That(hit.Result.IsSuccess, Is.True);
                Assert.That(hit.HasHit, Is.True);
                Assert.That(hit.Hit.Position.OwnerKind, Is.EqualTo(OwnerKind.Grid));
                Assert.That(hit.Hit.Position.X, Is.EqualTo(32));
                Assert.That(hit.Hit.State.FuelTicksRemaining, Is.EqualTo(17));
                Assert.That(_grid.AllocatedTileCount, Is.EqualTo(1));

                PointQueryResult empty = queries.QueryPoint(new WorldVersion(3, 10), new Vector2(.5f, .5f));
                Assert.That(empty.HasHit, Is.False);
                Assert.That(_grid.AllocatedTileCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void RegionUsesOccupiedRowsAndReportsBufferShortageWithoutPartialWrites()
        {
            _grid.Write(0, 0, _grid.CreateCell(101));
            _grid.Write(32, 32, _grid.CreateCell(101));
            _grid.Write(64, 64, _grid.CreateCell(101));
            using (var queries = new MaterialQueries(_grid, null, Vector2.zero, 1, 16))
            {
                CellHit[] destination = { new CellHit(new WorldVersion(9, 9), new CellPositionKey(OwnerKind.Grid, 0, 9, 9), default) };
                QueryResult shortResult = queries.QueryRegion(new WorldVersion(3, 10), new WorldRect(Vector2.zero, new Vector2(65, 65)), destination);
                Assert.That(shortResult.Result.ErrorCode, Is.EqualTo(WorldErrorCode.BufferTooSmall));
                Assert.That(shortResult.RequiredCount, Is.EqualTo(3));
                Assert.That(shortResult.WrittenCount, Is.Zero);
                Assert.That(destination[0].Position.X, Is.EqualTo(9));

                CellHit[] all = new CellHit[3];
                QueryResult result = queries.QueryRegion(new WorldVersion(3, 10), new WorldRect(Vector2.zero, new Vector2(65, 65)), all);
                Assert.That(result.Result.IsSuccess, Is.True);
                Assert.That(result.WrittenCount, Is.EqualTo(3));
                Assert.That(all[0].Position.CompareTo(all[1].Position), Is.LessThan(0));
                Assert.That(all[1].Position.CompareTo(all[2].Position), Is.LessThan(0));
            }
        }

        [Test]
        public void SegmentDdaReturnsOnlyOccupiedCellsInIntersectionOrder()
        {
            _grid.Write(1, 0, _grid.CreateCell(101));
            _grid.Write(33, 0, _grid.CreateCell(101));
            using (var queries = new MaterialQueries(_grid, null, Vector2.zero, 1, 16))
            {
                CellHit[] destination = new CellHit[2];
                QueryResult result = queries.QuerySegment(new WorldVersion(3, 10), new Vector2(0, .5f), new Vector2(65, .5f), destination);
                Assert.That(result.Result.IsSuccess, Is.True);
                Assert.That(result.WrittenCount, Is.EqualTo(2));
                Assert.That(destination[0].Position.X, Is.EqualTo(1));
                Assert.That(destination[1].Position.X, Is.EqualTo(33));
            }
        }

        [Test]
        public void RegionSizingGrowsPastInitialScratchAndReturnsFullRequiredCount()
        {
            _grid.Dispose();
            _grid = new MaterialGrid(64, 32, _definitions);
            GridCell material = _grid.CreateCell(101);
            for (int y = 0; y < _grid.Height; y++)
                for (int x = 0; x < _grid.Width; x++) _grid.Write(x, y, material);

            using (var queries = new MaterialQueries(_grid, null, Vector2.zero, 1, 1024))
            {
                QueryResult sizing = queries.QueryRegion(new WorldVersion(3, 10), new WorldRect(Vector2.zero, new Vector2(64, 32)), Span<CellHit>.Empty);
                Assert.That(sizing.Result.ErrorCode, Is.EqualTo(WorldErrorCode.BufferTooSmall));
                Assert.That(sizing.RequiredCount, Is.EqualTo(2048));
                Assert.That(sizing.WrittenCount, Is.Zero);
            }
        }

        [Test]
        public void PointRejectsNaNAndInfinityWithoutReadingTheGrid()
        {
            using (var queries = new MaterialQueries(_grid, null, Vector2.zero, 1, 16))
            {
                PointQueryResult nan = queries.QueryPoint(new WorldVersion(3, 10), new Vector2(float.NaN, .5f));
                PointQueryResult infinity = queries.QueryPoint(new WorldVersion(3, 10), new Vector2(float.PositiveInfinity, .5f));
                Assert.That(nan.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                Assert.That(infinity.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                Assert.That(_grid.AllocatedTileCount, Is.Zero);
            }
        }

        [Test]
        public void SnapshotUsesAbsoluteDeadlinesAndBurnEndForFuel()
        {
            CellMaterialDefinition definition = new CellMaterialDefinition { Id = 101, Fuel = 90 };
            GridCell cell = new GridCell
            {
                MaterialId = 101,
                Flags = GridCell.BurningFlag,
                NextMoveTick = 17,
                Cold = new CellCold { BurnEndTick = 25, NextSpreadTick = 20, ExpiryTick = 30, IgnitedTick = 12, FuelRemaining = 2 }
            };
            CellSnapshot snapshot = MaterialQueries.Snapshot(cell, definition, 15);
            Assert.That(snapshot.FuelTicksRemaining, Is.EqualTo(10));
            Assert.That(snapshot.SpreadCountdown, Is.EqualTo(5));
            Assert.That(snapshot.LifetimeTicksRemaining, Is.EqualTo(15));
            Assert.That(snapshot.MoveCountdown, Is.EqualTo(2));
            Assert.That(snapshot.IgnitedTick, Is.EqualTo(12));
        }
    }
}
