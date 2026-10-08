using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.V2;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class MaterialGridTests
    {
        private NativeArray<CellMaterialDefinition> _definitions;
        private MaterialGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _definitions = CreateDefinitions();
        }

        [TearDown]
        public void TearDown()
        {
            if (_grid != null) _grid.Dispose();
            if (_definitions.IsCreated) _definitions.Dispose();
        }

        [Test]
        public void ReadAndPassableOnMissingTileDoNotAllocateAPage()
        {
            _grid = new MaterialGrid(65, 65, _definitions);

            Assert.That(_grid.AllocatedTileCount, Is.Zero);
            Assert.That(_grid.Read(0, 0).IsEmpty, Is.True);
            Assert.That(_grid.Read(64, 64).IsEmpty, Is.True);
            Assert.That(_grid.Read(-1, 0).IsEmpty, Is.True);
            Assert.That(_grid.Passable(0, 0), Is.True);
            Assert.That(_grid.IsDynamic(64, 64), Is.False);
            Assert.That(_grid.AllocatedTileCount, Is.Zero);
        }

        [Test]
        public void TileAndOffsetUseThirtyTwoCellBoundaries()
        {
            _grid = new MaterialGrid(33, 33, _definitions);

            Assert.That(_grid.TileColumns, Is.EqualTo(2));
            Assert.That(_grid.TileRows, Is.EqualTo(2));
            Assert.That(_grid.TileId(31, 31), Is.EqualTo(0));
            Assert.That(_grid.TileId(32, 0), Is.EqualTo(1));
            Assert.That(_grid.TileId(0, 32), Is.EqualTo(2));
            Assert.That(_grid.TileId(32, 32), Is.EqualTo(3));
            Assert.That(MaterialGrid.Offset(0, 0), Is.EqualTo(0));
            Assert.That(MaterialGrid.Offset(31, 31), Is.EqualTo(1023));
            Assert.That(MaterialGrid.Offset(32, 32), Is.EqualTo(0));

            GridCell firstCorner = _grid.CreateCell(102);
            _grid.Write(31, 31, firstCorner);
            GridCell corner = _grid.CreateCell(102);
            _grid.Write(32, 32, corner);
            Assert.That(_grid.AllocatedTileCount, Is.EqualTo(2));
            Assert.That(_grid.Read(31, 31).MaterialId, Is.EqualTo((ushort)102));
            Assert.That(_grid.Read(32, 32).MaterialId, Is.EqualTo((ushort)102));
            Assert.That(_grid.Read(32, 31).IsEmpty, Is.True);
        }

        [Test]
        public void TakeAndWriteMoveOneColdHandleWithoutResettingState()
        {
            _grid = new MaterialGrid(64, 32, _definitions) { Tick = 10, GridHandle = 7 };
            GridCell original = _grid.CreateCell(103);
            _grid.Write(1, 1, original);
            GridCell before = _grid.Read(1, 1);
            int handle = before.ComponentHandle;
            CellCold coldBefore = _grid.ColdStore.Read(handle);

            GridCell moved = _grid.Take(1, 1);
            Assert.That(_grid.Read(1, 1).IsEmpty, Is.True);
            _grid.Write(32, 1, moved);
            GridCell after = _grid.Read(32, 1);
            CellCold coldAfter = _grid.ColdStore.Read(handle);

            Assert.That(after.ComponentHandle, Is.EqualTo(handle));
            Assert.That(after.Cold.ExpiryTick, Is.EqualTo(coldBefore.ExpiryTick));
            Assert.That(after.Cold.FuelRemaining, Is.EqualTo(coldBefore.FuelRemaining));
            Assert.That(coldAfter.X, Is.EqualTo(32));
            Assert.That(coldAfter.Y, Is.EqualTo(1));
            Assert.That(coldAfter.GridHandle, Is.EqualTo(7));
            Assert.That(coldAfter.ExpiryTick, Is.EqualTo(coldBefore.ExpiryTick));
            Assert.That(coldAfter.FuelRemaining, Is.EqualTo(coldBefore.FuelRemaining));
        }

        [Test]
        public void DeleteInvalidatesOldTimerAndReusesHandleWithNewGeneration()
        {
            _grid = new MaterialGrid(8, 8, _definitions) { Tick = 0 };
            GridCell gas = _grid.CreateCell(103);
            _grid.Write(0, 0, gas);
            GridCell first = _grid.Read(0, 0);
            int oldHandle = first.ComponentHandle;
            uint oldGeneration = _grid.ColdStore.Read(oldHandle).Generation;
            GridCell empty = default;
            _grid.Write(0, 0, empty);
            Assert.That(_grid.ColdStore.IsLive(oldHandle), Is.False);

            _grid.Tick = 10;
            GridCell replacementCell = _grid.CreateCell(103);
            _grid.Write(1, 0, replacementCell);
            GridCell replacement = _grid.Read(1, 0);
            Assert.That(replacement.ComponentHandle, Is.EqualTo(oldHandle));
            Assert.That(_grid.ColdStore.Read(oldHandle).Generation, Is.GreaterThan(oldGeneration));
            Assert.That(_grid.ColdStore.Read(oldHandle).X, Is.EqualTo(1));
            Assert.That(_grid.ColdStore.Read(oldHandle).Y, Is.Zero);
        }

        [Test]
        public void FixedFlagChangeMarksTopologyAndIsConsumedOnce()
        {
            _grid = new MaterialGrid(32, 32, _definitions);
            GridCell structure = _grid.CreateCell(102);
            _grid.Write(4, 4, structure);
            using (var dirty = new NativeList<int>(4, Allocator.Persistent))
            {
                _grid.TakeTopologyDirtyTiles(dirty);
                Assert.That(dirty.Length, Is.EqualTo(1));

                GridCell fixedCell = _grid.Read(4, 4);
                fixedCell.Flags |= GridCell.FixedFlag;
                _grid.Write(4, 4, fixedCell);
                _grid.TakeTopologyDirtyTiles(dirty);
                Assert.That(dirty.Length, Is.EqualTo(1));

                _grid.Write(4, 4, fixedCell);
                _grid.TakeTopologyDirtyTiles(dirty);
                Assert.That(dirty.Length, Is.Zero);
            }
        }

        private static NativeArray<CellMaterialDefinition> CreateDefinitions()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[101] = new CellMaterialDefinition
            {
                Id = 101, Kind = MaterialKind.Liquid, Rules = RuleMask.LiquidFlow, MoveInterval = 1
            };
            definitions[102] = new CellMaterialDefinition
            {
                Id = 102, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 1
            };
            definitions[103] = new CellMaterialDefinition
            {
                Id = 103, Kind = MaterialKind.Gas, Rules = RuleMask.GasDrift, Lifetime = 5
            };
            return definitions;
        }
    }
}
