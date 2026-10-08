using System;
using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.V2;
using Unity.Collections;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed unsafe class MaterialPagePoolTests
    {
        [Test]
        public void RecycledPageIsClearedAndReused()
        {
            var definitions = CreateDefinitions();
            var pool = new MaterialPagePool();
            try
            {
                IntPtr firstAddress;
                var first = new MaterialGrid(32, 32, definitions, null, pool);
                first.Write(0, 0, new GridCell { MaterialId = 1, Flags = GridCell.FixedFlag, NextMoveTick = 99 });
                firstAddress = (IntPtr)first.Tiles[0].Material;
                Assert.That(pool.InUsePageCount, Is.EqualTo(1));
                first.Dispose();
                Assert.That(pool.InUsePageCount, Is.Zero);
                Assert.That(pool.FreePageCount, Is.EqualTo(MaterialPagePool.PagesPerSlab));

                var second = new MaterialGrid(32, 32, definitions, null, pool);
                second.EnsureTile(0);
                Assert.That((IntPtr)second.Tiles[0].Material, Is.EqualTo(firstAddress));
                Assert.That(second.Read(0, 0).IsEmpty, Is.True);
                second.Dispose();
            }
            finally
            {
                pool.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void BorrowedChildReturnsPagesWithoutFreeingParentPool()
        {
            var definitions = CreateDefinitions();
            var pool = new MaterialPagePool();
            var parent = new MaterialGrid(64, 32, definitions, null, pool);
            try
            {
                parent.Write(0, 0, new GridCell { MaterialId = 1 });
                var child = new MaterialGrid(4, 4, definitions, parent.ColdStore, parent.PagePool);
                child.Write(1, 1, new GridCell { MaterialId = 1 });
                Assert.That(pool.InUsePageCount, Is.EqualTo(2));
                child.Dispose();
                Assert.That(pool.InUsePageCount, Is.EqualTo(1));
                Assert.That(parent.Read(0, 0).MaterialId, Is.EqualTo((ushort)1));
            }
            finally
            {
                parent.Dispose();
                pool.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void IndependentGridOwnsAndReleasesItsPagePool()
        {
            var definitions = CreateDefinitions();
            var grid = new MaterialGrid(32, 32, definitions);
            MaterialPagePool pool = grid.PagePool;
            try
            {
                grid.Write(0, 0, new GridCell { MaterialId = 1 });
                Assert.That(pool.SlabCount, Is.EqualTo(1));
                Assert.That(pool.InUsePageCount, Is.EqualTo(1));
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
            Assert.That(pool.InUsePageCount, Is.Zero);
        }

        [Test]
        public void ForeignPoolCannotReclaimOrClearAnotherPoolsPage()
        {
            var first = new MaterialPagePool();
            var second = new MaterialPagePool();
            try
            {
                IntPtr page = first.Rent();
                Assert.Throws<ArgumentException>(() => second.Return(page));
                Assert.That(first.InUsePageCount, Is.EqualTo(1));
                Assert.That(second.InUsePageCount, Is.Zero);
                first.Return(page);
                Assert.That(first.FreePageCount, Is.EqualTo(MaterialPagePool.PagesPerSlab));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        private static NativeArray<CellMaterialDefinition> CreateDefinitions()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition
            {
                Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1
            };
            return definitions;
        }
    }
}
