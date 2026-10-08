using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.V2;
using Unity.Collections;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class MaterialConnectivityStructureTests
    {
        [Test]
        public void RebuildAll_ConnectsAcrossTileBoundary_AndExcludesFluid()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            definitions[2] = new CellMaterialDefinition { Id = 2, Kind = MaterialKind.Liquid, Rules = RuleMask.LiquidFlow, Mass = 1 };
            var grid = new MaterialGrid(64, 32, definitions);
            try
            {
                grid.Write(31, 4, new GridCell { MaterialId = 1 });
                grid.Write(32, 4, new GridCell { MaterialId = 1 });
                grid.Write(40, 4, new GridCell { MaterialId = 2 });
                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    Assert.That(connectivity.TryGetComponent(31, 4, out int first), Is.True);
                    Assert.That(connectivity.TryGetComponent(32, 4, out int second), Is.True);
                    Assert.That(connectivity.AreConnected(first, second), Is.True);
                    Assert.That(connectivity.TryGetComponent(40, 4, out _), Is.False);
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(1));
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RebuildAffected_RemovesDeletedStructureWithoutScanningFluid()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var grid = new MaterialGrid(64, 32, definitions);
            try
            {
                grid.Write(2, 2, new GridCell { MaterialId = 1 });
                grid.Write(40, 2, new GridCell { MaterialId = 1 });
                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(2));
                    grid.Write(2, 2, default);
                    var dirty = new NativeList<int>(4, Allocator.Temp);
                    try { grid.TakeTopologyDirtyTiles(dirty); connectivity.RebuildAffected(dirty); }
                    finally { dirty.Dispose(); }
                    Assert.That(connectivity.TryGetComponent(2, 2, out _), Is.False);
                    Assert.That(connectivity.TryGetComponent(40, 2, out _), Is.True);
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(1));
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RebuildAffected_CentralHorizontalRemovalPreservesIncomingLeftBoundary()
        {
            var definitions = CreateStructureDefinitions();
            var grid = new MaterialGrid(128, 32, definitions);
            try
            {
                for (int x = 0; x < 128; x++) grid.Write(x, 8, new GridCell { MaterialId = 1 });
                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    ClearInitialTopologyDirty(grid);
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(1));

                    grid.Write(66, 8, default);
                    var dirty = new NativeList<int>(4, Allocator.Temp);
                    try
                    {
                        grid.TakeTopologyDirtyTiles(dirty);
                        connectivity.RebuildAffected(dirty);
                    }
                    finally { dirty.Dispose(); }

                    Assert.That(connectivity.ComponentCount, Is.EqualTo(2));
                    Assert.That(connectivity.TryGetComponent(4, 8, out int left), Is.True);
                    Assert.That(connectivity.TryGetComponent(60, 8, out int leftEnd), Is.True);
                    Assert.That(connectivity.TryGetComponent(68, 8, out int right), Is.True);
                    Assert.That(connectivity.AreConnected(left, leftEnd), Is.True);
                    Assert.That(connectivity.AreConnected(left, right), Is.False);
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RebuildAffected_CentralVerticalRemovalPreservesIncomingDownBoundary()
        {
            var definitions = CreateStructureDefinitions();
            var grid = new MaterialGrid(32, 128, definitions);
            try
            {
                for (int y = 0; y < 128; y++) grid.Write(8, y, new GridCell { MaterialId = 1 });
                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    ClearInitialTopologyDirty(grid);
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(1));

                    grid.Write(8, 66, default);
                    var dirty = new NativeList<int>(4, Allocator.Temp);
                    try
                    {
                        grid.TakeTopologyDirtyTiles(dirty);
                        connectivity.RebuildAffected(dirty);
                    }
                    finally { dirty.Dispose(); }

                    Assert.That(connectivity.ComponentCount, Is.EqualTo(2));
                    Assert.That(connectivity.TryGetComponent(8, 4, out int bottom), Is.True);
                    Assert.That(connectivity.TryGetComponent(8, 60, out int bottomEnd), Is.True);
                    Assert.That(connectivity.TryGetComponent(8, 68, out int top), Is.True);
                    Assert.That(connectivity.AreConnected(bottom, bottomEnd), Is.True);
                    Assert.That(connectivity.AreConnected(bottom, top), Is.False);
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RebuildAffected_RightEndRemovalKeepsUnchangedLeftTilesConnected()
        {
            var definitions = CreateStructureDefinitions();
            var grid = new MaterialGrid(128, 32, definitions);
            try
            {
                for (int x = 0; x < 95; x++) grid.Write(x, 12, new GridCell { MaterialId = 1 });
                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    ClearInitialTopologyDirty(grid);
                    grid.Write(94, 12, default);
                    var dirty = new NativeList<int>(4, Allocator.Temp);
                    try
                    {
                        grid.TakeTopologyDirtyTiles(dirty);
                        connectivity.RebuildAffected(dirty);
                    }
                    finally { dirty.Dispose(); }

                    Assert.That(connectivity.ComponentCount, Is.EqualTo(1));
                    Assert.That(connectivity.TryGetComponent(4, 12, out int first), Is.True);
                    Assert.That(connectivity.TryGetComponent(92, 12, out int last), Is.True);
                    Assert.That(connectivity.AreConnected(first, last), Is.True);
                    Assert.That(connectivity.TryGetComponent(94, 12, out _), Is.False);
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RebuildAll_RequiresMatchingConnectionGroup_ButSharedGroupMaterialsConnect()
        {
            var definitions = CreateStructureDefinitions();
            definitions[2] = new CellMaterialDefinition
            {
                Id = 2, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 1, Mass = 1
            };
            definitions[3] = new CellMaterialDefinition
            {
                Id = 3, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 2, Mass = 1
            };
            var grid = new MaterialGrid(64, 32, definitions);
            try
            {
                grid.Write(30, 8, new GridCell { MaterialId = 1 });
                grid.Write(31, 8, new GridCell { MaterialId = 2 });
                grid.Write(32, 8, new GridCell { MaterialId = 3 });
                grid.Write(33, 8, new GridCell { MaterialId = 1 });
                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(3));
                    Assert.That(connectivity.TryGetComponent(30, 8, out int first), Is.True);
                    Assert.That(connectivity.TryGetComponent(31, 8, out int shared), Is.True);
                    Assert.That(connectivity.TryGetComponent(32, 8, out int different), Is.True);
                    Assert.That(connectivity.TryGetComponent(33, 8, out int last), Is.True);
                    Assert.That(connectivity.AreConnected(first, shared), Is.True);
                    Assert.That(connectivity.AreConnected(shared, different), Is.False);
                    Assert.That(connectivity.AreConnected(different, last), Is.False);
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void QueriesVisitOnlyRequestedComponentWithManyUnrelatedStructures()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var grid = new MaterialGrid(256, 256, definitions);
            try
            {
                // The requested component crosses a tile boundary and has two cells.
                grid.Write(31, 4, new GridCell { MaterialId = 1 });
                grid.Write(32, 4, new GridCell { MaterialId = 1 });
                // Populate many unrelated tile-local roots so a full component-label
                // or boundary-edge scan is visible in the diagnostic visit counters.
                for (int tileY = 0; tileY < 8; tileY++)
                    for (int tileX = 0; tileX < 8; tileX++)
                    {
                        int x = tileX * 32 + 8, y = tileY * 32 + 8;
                        if (x == 8 && y == 8) continue;
                        grid.Write(x, y, new GridCell { MaterialId = 1 });
                    }

                using (var connectivity = new MaterialConnectivity(grid))
                using (var roots = new NativeList<int>(8, Allocator.Persistent))
                using (var connected = new NativeList<int>(8, Allocator.Persistent))
                using (var cells = new NativeList<int>(8, Allocator.Persistent))
                {
                    connectivity.RebuildAll();
                    Assert.That(connectivity.ComponentCount, Is.EqualTo(64));
                    Assert.That(connectivity.LocalComponentCount, Is.EqualTo(65));
                    Assert.That(connectivity.TryGetComponent(31, 4, out int first), Is.True);
                    connectivity.GetConnectedRoots(first, connected);
                    Assert.That(connected.Length, Is.EqualTo(2));
                    Assert.That(connectivity.LastConnectedRootsEdgeVisits, Is.EqualTo(2));
                    for (int i = 0; i < connected.Length; i++) roots.Add(connected[i]);
                    roots.Add(connected[0]);
                    connectivity.GetCells(roots, cells);
                    Assert.That(cells.Length, Is.EqualTo(2));
                    Assert.That(connectivity.LastGetCellsVisited, Is.EqualTo(2));
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void LocalQueryScratchDoesNotRetainFullWorldCountVisitedCapacity()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            const int tilesPerAxis = 17;
            var grid = new MaterialGrid(tilesPerAxis * 32, tilesPerAxis * 32, definitions);
            try
            {
                for (int tileY = 0; tileY < tilesPerAxis; tileY++)
                    for (int tileX = 0; tileX < tilesPerAxis; tileX++)
                        grid.Write(tileX * 32 + 1, tileY * 32 + 1, new GridCell { MaterialId = 1 });

                using (var connectivity = new MaterialConnectivity(grid))
                using (var connected = new NativeList<int>(4, Allocator.Persistent))
                using (var roots = new NativeList<int>(4, Allocator.Persistent))
                using (var cells = new NativeList<int>(4, Allocator.Persistent))
                {
                    connectivity.RebuildAll();
                    Assert.That(connectivity.LastCountVisited, Is.EqualTo(tilesPerAxis * tilesPerAxis));
                    Assert.That(connectivity.CountVisitedCapacity, Is.GreaterThan(connectivity.QueryScratchCapacity));
                    Assert.That(connectivity.QueryScratchCapacity, Is.LessThanOrEqualTo(256));

                    Assert.That(connectivity.TryGetComponent(1, 1, out int root), Is.True);
                    connectivity.GetConnectedRoots(root, connected);
                    Assert.That(connected.Length, Is.EqualTo(1));
                    roots.Add(root);
                    connectivity.GetCells(roots, cells);
                    Assert.That(cells.Length, Is.EqualTo(1));
                    Assert.That(connectivity.LastGetCellsVisited, Is.EqualTo(1));
                    Assert.That(connectivity.QueryScratchCapacity, Is.LessThanOrEqualTo(256));
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void LocalRemovalSplitsOnlyAffectedGroup_AndUpdatesComponentCount()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var grid = new MaterialGrid(256, 256, definitions);
            try
            {
                grid.Write(65, 65, new GridCell { MaterialId = 1 });
                grid.Write(66, 65, new GridCell { MaterialId = 1 });
                grid.Write(67, 65, new GridCell { MaterialId = 1 });
                for (int tileY = 0; tileY < 8; tileY++)
                    for (int tileX = 0; tileX < 8; tileX++)
                    {
                        int x = tileX * 32 + 4, y = tileY * 32 + 4;
                        if (x == 68 && y == 68) continue;
                        grid.Write(x, y, new GridCell { MaterialId = 1 });
                    }

                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    var initialDirty = new NativeList<int>(64, Allocator.Temp);
                    try { grid.TakeTopologyDirtyTiles(initialDirty); }
                    finally { initialDirty.Dispose(); }
                    int before = connectivity.ComponentCount;
                    connectivity.ResetWorkCounters();
                    grid.Write(66, 65, default);
                    var dirty = new NativeList<int>(4, Allocator.Temp);
                    try
                    {
                        grid.TakeTopologyDirtyTiles(dirty);
                        connectivity.RebuildAffected(dirty);
                    }
                    finally { dirty.Dispose(); }

                    Assert.That(connectivity.ComponentCount, Is.EqualTo(before + 1));
                    Assert.That(connectivity.CellVisits, Is.GreaterThan(0));
                    Assert.That(connectivity.CellVisits, Is.LessThan((long)grid.Width * grid.Height / 2));
                    Assert.That(connectivity.TryGetComponent(65, 65, out _), Is.True);
                    Assert.That(connectivity.TryGetComponent(67, 65, out _), Is.True);
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void LocalInsertionMergesAffectedGroups_AndNoDirtyRebuildDoesNotIncreaseVisits()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition { Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, Mass = 1 };
            var grid = new MaterialGrid(256, 256, definitions);
            try
            {
                grid.Write(31, 96, new GridCell { MaterialId = 1 });
                grid.Write(33, 96, new GridCell { MaterialId = 1 });
                for (int tileY = 0; tileY < 8; tileY++)
                    for (int tileX = 0; tileX < 8; tileX++)
                    {
                        int x = tileX * 32 + 6, y = tileY * 32 + 6;
                        if (x == 38 && y == 102) continue;
                        grid.Write(x, y, new GridCell { MaterialId = 1 });
                    }

                using (var connectivity = new MaterialConnectivity(grid))
                {
                    connectivity.RebuildAll();
                    var initialDirty = new NativeList<int>(64, Allocator.Temp);
                    try { grid.TakeTopologyDirtyTiles(initialDirty); }
                    finally { initialDirty.Dispose(); }
                    int before = connectivity.ComponentCount;
                    connectivity.ResetWorkCounters();
                    grid.Write(32, 96, new GridCell { MaterialId = 1 });
                    var dirty = new NativeList<int>(4, Allocator.Temp);
                    try
                    {
                        grid.TakeTopologyDirtyTiles(dirty);
                        connectivity.RebuildAffected(dirty);
                    }
                    finally { dirty.Dispose(); }

                    Assert.That(connectivity.ComponentCount, Is.EqualTo(before - 1));
                    long visitsAfterMerge = connectivity.CellVisits;
                    connectivity.ResetWorkCounters();
                    var noDirty = new NativeList<int>(1, Allocator.Temp);
                    try { connectivity.RebuildAffected(noDirty); }
                    finally { noDirty.Dispose(); }
                    Assert.That(connectivity.CellVisits, Is.Zero);
                    Assert.That(connectivity.EdgeVisits, Is.Zero);
                    Assert.That(visitsAfterMerge, Is.GreaterThan(0));
                }
            }
            finally
            {
                grid.Dispose();
                definitions.Dispose();
            }
        }

        private static NativeArray<CellMaterialDefinition> CreateStructureDefinitions()
        {
            var definitions = new NativeArray<CellMaterialDefinition>(65536, Allocator.Persistent);
            definitions[1] = new CellMaterialDefinition
            {
                Id = 1, Kind = MaterialKind.Solid, Rules = RuleMask.Structure, ConnectionGroup = 1, Mass = 1
            };
            return definitions;
        }

        private static void ClearInitialTopologyDirty(MaterialGrid grid)
        {
            var dirty = new NativeList<int>(8, Allocator.Temp);
            try { grid.TakeTopologyDirtyTiles(dirty); }
            finally { dirty.Dispose(); }
        }
    }
}
