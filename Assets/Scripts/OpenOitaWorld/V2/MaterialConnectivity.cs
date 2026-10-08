using System;
using Unity.Collections;
using UnityEngine;
using OpenOita.Contracts;

namespace OpenOita.V2
{
    /// <summary>
    /// Tile-local structure components plus the small graph made by tile boundaries.
    /// Fluid and gas cells never enter the component or boundary stores.
    /// </summary>
    public sealed class MaterialConnectivity : IDisposable
    {
        public const int TileSize = 32;
        private const int CellsPerTile = TileSize * TileSize;

        private readonly MaterialGrid _grid;
        private NativeParallelHashMap<int, int> _components;
        // Persistent indexes.  Components are tile-local roots; these maps avoid
        // re-enumerating the whole label map when a caller asks for one component.
        private NativeParallelMultiHashMap<int, int> _componentMembers;
        private NativeParallelMultiHashMap<int, int> _adjacentRoots;
        private NativeList<BoundaryEdgeV2> _boundaryEdges;
        private NativeParallelHashMap<ulong, int> _boundarySlots;
        private NativeParallelMultiHashMap<int, ulong> _boundaryKeysByRoot;
        private NativeList<int> _affectedTiles;
        private NativeList<int> _queue;
        private NativeList<int> _componentCells;
        private NativeParallelHashSet<int> _affectedComponentIds;
        private NativeList<int> _affectedComponentList;
        private NativeParallelHashSet<int> _componentRoots;
        // Full-world connected-component counting has its own visited set.  The
        // query set below must stay sized to the largest local query instead of
        // retaining a table sized for every root in the world.
        private NativeParallelHashSet<int> _countVisited;
        private NativeParallelHashSet<int> _queryRoots;
        private NativeList<int> _queryVisitedRoots;
        private NativeList<int> _oldGroupRootList;
        private NativeList<int> _adjacencyScratch;
        private NativeList<int> _adjacencyValuesScratch;
        private NativeList<ulong> _boundaryKeyScratch;
        private bool _disposed;
        private bool _hasBeenBuilt;
        private bool _fullRebuildRequested;

        public int ComponentCount { get; private set; }
        public int LocalComponentCount => _componentRoots.IsCreated ? _componentRoots.Count() : 0;
        public int LastGetCellsVisited { get; private set; }
        public int LastConnectedRootsEdgeVisits { get; private set; }
        public int LastCountVisited { get; private set; }
        /// <summary>
        /// Cumulative logical-cell access count since the last ResetWorkCounters call.
        /// One count is added for each cell in GetRootsInTiles, GetCells, and the
        /// outer tile scans of RemoveTile, BuildTile, and BuildBoundaryEdges. The
        /// neighbour probes used to grow a local component are not counted again as
        /// cells; boundary/adjacency work is reported by EdgeVisits.
        /// </summary>
        public long CellVisits { get; private set; }

        /// <summary>
        /// Cumulative structure-graph access count since the last ResetWorkCounters
        /// call. One count is added per boundary candidate checked or per adjacency
        /// value enumerated by connected-component queries, counting, and adjacency
        /// removal. Local flood-fill neighbour probes are deliberately excluded. It
        /// is an access count, not a count of unique graph edges.
        /// </summary>
        public long EdgeVisits { get; private set; }

        public int QueryScratchCapacity => _queryRoots.IsCreated ? _queryRoots.Capacity : 0;
        public int CountVisitedCapacity => _countVisited.IsCreated ? _countVisited.Capacity : 0;
        public int BoundaryCount => _boundaryEdges.IsCreated ? _boundaryEdges.Length : 0;
        public NativeArray<int> AffectedTiles => _affectedTiles.AsArray();
        public NativeArray<BoundaryEdgeV2> BoundaryEdges => _boundaryEdges.AsArray();

        /// <summary>Resets cumulative work counters only; structure state is unchanged.</summary>
        public void ResetWorkCounters()
        {
            EnsureUsable();
            CellVisits = 0;
            EdgeVisits = 0;
        }

        public MaterialConnectivity(MaterialGrid grid)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _components = new NativeParallelHashMap<int, int>(1024, Allocator.Persistent);
            _componentMembers = new NativeParallelMultiHashMap<int, int>(1024, Allocator.Persistent);
            _adjacentRoots = new NativeParallelMultiHashMap<int, int>(512, Allocator.Persistent);
            _boundaryEdges = new NativeList<BoundaryEdgeV2>(256, Allocator.Persistent);
            _boundarySlots = new NativeParallelHashMap<ulong, int>(256, Allocator.Persistent);
            _boundaryKeysByRoot = new NativeParallelMultiHashMap<int, ulong>(512, Allocator.Persistent);
            _affectedTiles = new NativeList<int>(256, Allocator.Persistent);
            _queue = new NativeList<int>(CellsPerTile, Allocator.Persistent);
            _componentCells = new NativeList<int>(CellsPerTile, Allocator.Persistent);
            _affectedComponentIds = new NativeParallelHashSet<int>(256, Allocator.Persistent);
            _affectedComponentList = new NativeList<int>(256, Allocator.Persistent);
            _componentRoots = new NativeParallelHashSet<int>(256, Allocator.Persistent);
            _countVisited = new NativeParallelHashSet<int>(256, Allocator.Persistent);
            _queryRoots = new NativeParallelHashSet<int>(256, Allocator.Persistent);
            _queryVisitedRoots = new NativeList<int>(256, Allocator.Persistent);
            _oldGroupRootList = new NativeList<int>(256, Allocator.Persistent);
            _adjacencyScratch = new NativeList<int>(256, Allocator.Persistent);
            _adjacencyValuesScratch = new NativeList<int>(256, Allocator.Persistent);
            _boundaryKeyScratch = new NativeList<ulong>(256, Allocator.Persistent);
        }

        /// <summary>Rebuild all allocated tiles. Call only at creation or an explicit full reset.</summary>
        public void RebuildAll()
        {
            EnsureUsable();
            EnsureTileScratch();
            _affectedTiles.Clear();
            for (int i = 0; i < _grid.AllocatedTileCount; i++)
                _affectedTiles.Add(_grid.GetAllocatedTileId(i));
            _fullRebuildRequested = true;
            try { RebuildAffectedInternal(); }
            finally { _fullRebuildRequested = false; }
        }

        /// <summary>
        /// Rebuild dirty tiles and their four-neighbour boundary graph. The caller owns the dirty list.
        /// </summary>
        public void RebuildAffected(NativeList<int> dirtyTiles)
        {
            EnsureUsable();
            EnsureTileScratch();
            _affectedTiles.Clear();
            for (int i = 0; i < dirtyTiles.Length; i++) AddAffectedNeighbours(dirtyTiles[i]);
            RebuildAffectedInternal();
        }

        private void RebuildAffectedInternal()
        {
            if (_affectedTiles.Length == 0) return;

            bool fullCount = _fullRebuildRequested || !_hasBeenBuilt;
            ClearAffectedComponents();
            int oldGroupCount = 0;
            if (!fullCount)
            {
                CollectAffectedRoots();
                oldGroupCount = CaptureOldGroupCount();
            }
            for (int i = 0; i < _affectedTiles.Length; i++) RemoveTile(_affectedTiles[i]);
            RemoveBoundaryEdgesForAffectedComponents();
            for (int i = 0; i < _affectedTiles.Length; i++) BuildTile(_affectedTiles[i]);
            BuildBoundaryEdges();
            if (fullCount)
            {
                ComponentCount = CountConnectedComponents();
                _hasBeenBuilt = true;
                return;
            }

            int newGroupCount = CountNewGroupCount();
            ComponentCount += newGroupCount - oldGroupCount;
        }

        public void RebuildAffected(NativeArray<int> dirtyTiles)
        {
            EnsureUsable();
            EnsureTileScratch();
            _affectedTiles.Clear();
            for (int i = 0; i < dirtyTiles.Length; i++) AddAffectedNeighbours(dirtyTiles[i]);
            RebuildAffectedInternal();
        }

        public bool TryGetComponent(int x, int y, out int componentId)
        {
            componentId = 0;
            if (!_grid.Inside(x, y)) return false;
            return _components.TryGetValue(x + y * _grid.Width, out componentId);
        }

        /// <summary>
        /// Returns tile-local component roots currently represented by structure cells.
        /// This is a query over the structure label map only; fluid and gas cells never enter it.
        /// </summary>
        public void GetComponentRoots(NativeList<int> destination)
        {
            EnsureUsable();
            destination.Clear();
            foreach (int root in _componentRoots) destination.Add(root);
        }

        /// <summary>Gets roots touching the supplied tile set without enumerating unrelated cells.</summary>
        public void GetRootsInTiles(NativeArray<int> tiles, NativeList<int> destination)
        {
            EnsureUsable();
            destination.Clear();
            BeginQueryRoots();
            try
            {
                for (int i = 0; i < tiles.Length; i++)
                {
                    int tileId = tiles[i];
                    int tileX = tileId % _grid.TileColumns, tileY = tileId / _grid.TileColumns;
                    int minX = tileX * TileSize, minY = tileY * TileSize;
                    int maxX = Math.Min(minX + TileSize, _grid.Width), maxY = Math.Min(minY + TileSize, _grid.Height);
                    for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
                    {
                        CellVisits++;
                        if (_components.TryGetValue(x + y * _grid.Width, out int root) && AddQueryRoot(root))
                            destination.Add(root);
                    }
                }
            }
            finally { EndQueryRoots(); }
        }

        /// <summary>
        /// Expands a tile-local root through the boundary graph into one connected group.
        /// The caller owns the destination list.
        /// </summary>
        public void GetConnectedRoots(int root, NativeList<int> destination)
        {
            EnsureUsable();
            destination.Clear();
            LastConnectedRootsEdgeVisits = 0;
            if (root == 0) return;
            BeginQueryRoots();
            _queue.Clear();
            try
            {
                AddQueryRoot(root);
                _queue.Add(root);
                for (int head = 0; head < _queue.Length; head++)
                {
                    int current = _queue[head];
                    destination.Add(current);
                    NativeParallelMultiHashMapIterator<int> iterator;
                    if (!_adjacentRoots.TryGetFirstValue(current, out int next, out iterator)) continue;
                    do
                    {
                        EdgeVisits++;
                        LastConnectedRootsEdgeVisits++;
                        if (next != 0 && AddQueryRoot(next)) _queue.Add(next);
                    } while (_adjacentRoots.TryGetNextValue(out next, ref iterator));
                }
            }
            finally { EndQueryRoots(); }
        }

        /// <summary>Copies keys belonging to any root in <paramref name="roots"/>.</summary>
        public void GetCells(NativeList<int> roots, NativeList<int> destination)
        {
            EnsureUsable();
            destination.Clear();
            LastGetCellsVisited = 0;
            BeginQueryRoots();
            try
            {
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == 0 || !AddQueryRoot(roots[i])) continue;
                    NativeParallelMultiHashMapIterator<int> iterator;
                    if (!_componentMembers.TryGetFirstValue(roots[i], out int cell, out iterator)) continue;
                    do
                    {
                        destination.Add(cell);
                        CellVisits++;
                        LastGetCellsVisited++;
                    } while (_componentMembers.TryGetNextValue(out cell, ref iterator));
                }
            }
            finally { EndQueryRoots(); }
        }

        public bool AreConnected(int firstComponent, int secondComponent)
        {
            if (firstComponent == secondComponent) return firstComponent != 0;
            if (firstComponent == 0 || secondComponent == 0) return false;
            _queue.Clear();
            BeginQueryRoots();
            try
            {
                _queue.Add(firstComponent);
                AddQueryRoot(firstComponent);
                for (int head = 0; head < _queue.Length; head++)
                {
                    int current = _queue[head];
                    NativeParallelMultiHashMapIterator<int> iterator;
                    if (!_adjacentRoots.TryGetFirstValue(current, out int next, out iterator)) continue;
                    do
                    {
                        EdgeVisits++;
                        if (next != 0 && next == secondComponent) return true;
                        if (next != 0 && AddQueryRoot(next)) _queue.Add(next);
                    } while (_adjacentRoots.TryGetNextValue(out next, ref iterator));
                }
            }
            finally { EndQueryRoots(); }
            return false;
        }

        private void BuildTile(int tileId)
        {
            int tileX = tileId % _grid.TileColumns;
            int tileY = tileId / _grid.TileColumns;
            int minX = tileX * TileSize;
            int minY = tileY * TileSize;
            int maxX = Math.Min(minX + TileSize, _grid.Width);
            int maxY = Math.Min(minY + TileSize, _grid.Height);
            for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
            {
                CellVisits++;
                if (!IsStructure(x, y) || _components.ContainsKey(x + y * _grid.Width)) continue;
                int component = x + y * _grid.Width + 1;
                _queue.Clear(); _componentCells.Clear();
                _queue.Add(x + y * _grid.Width);
                _components.TryAdd(x + y * _grid.Width, component);
                _componentRoots.Add(component);
                AddAffectedComponent(component);
                for (int head = 0; head < _queue.Length; head++)
                {
                    int key = _queue[head];
                    _componentCells.Add(key);
                    _componentMembers.Add(component, key);
                    int cx = key % _grid.Width, cy = key / _grid.Width;
                    AddLocal(cx - 1, cy, component, x, y, minX, minY, maxX, maxY);
                    AddLocal(cx + 1, cy, component, x, y, minX, minY, maxX, maxY);
                    AddLocal(cx, cy - 1, component, x, y, minX, minY, maxX, maxY);
                    AddLocal(cx, cy + 1, component, x, y, minX, minY, maxX, maxY);
                }
            }
        }

        private void CollectAffectedRoots()
        {
            for (int i = 0; i < _affectedTiles.Length; i++)
            {
                int tileId = _affectedTiles[i];
                int tileX = tileId % _grid.TileColumns, tileY = tileId / _grid.TileColumns;
                int minX = tileX * TileSize, minY = tileY * TileSize;
                int maxX = Math.Min(minX + TileSize, _grid.Width), maxY = Math.Min(minY + TileSize, _grid.Height);
                for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
                {
                    CellVisits++;
                    if (_components.TryGetValue(x + y * _grid.Width, out int root)) AddAffectedComponent(root);
                }
            }
        }

        private int CaptureOldGroupCount()
        {
            _oldGroupRootList.Clear();
            int count = 0;
            BeginQueryRoots();
            try
            {
                foreach (int seed in _affectedComponentIds)
                {
                    if (!AddQueryRoot(seed)) continue;
                    _oldGroupRootList.Add(seed);
                    count++;
                    WalkConnectedRoots(seed, true);
                }
                return count;
            }
            finally { EndQueryRoots(); }
        }

        private int CountNewGroupCount()
        {
            int count = 0;
            BeginQueryRoots();
            try
            {
                for (int i = 0; i < _oldGroupRootList.Length; i++)
                {
                    int root = _oldGroupRootList[i];
                    if (!_componentRoots.Contains(root) || !AddQueryRoot(root)) continue;
                    count++;
                    WalkConnectedRoots(root, false);
                }
                for (int i = 0; i < _affectedComponentList.Length; i++)
                {
                    int root = _affectedComponentList[i];
                    if (!_componentRoots.Contains(root) || !AddQueryRoot(root)) continue;
                    count++;
                    WalkConnectedRoots(root, false);
                }
                return count;
            }
            finally { EndQueryRoots(); }
        }

        private void WalkConnectedRoots(int seed, bool retainRoots)
        {
            _queue.Clear();
            _queue.Add(seed);
            for (int head = 0; head < _queue.Length; head++)
            {
                int current = _queue[head];
                NativeParallelMultiHashMapIterator<int> iterator;
                if (!_adjacentRoots.TryGetFirstValue(current, out int next, out iterator)) continue;
                do
                {
                    EdgeVisits++;
                    if (next != 0 && AddQueryRoot(next))
                    {
                        if (retainRoots) _oldGroupRootList.Add(next);
                        _queue.Add(next);
                    }
                } while (_adjacentRoots.TryGetNextValue(out next, ref iterator));
            }
        }

        private void AddLocal(int x, int y, int component, int originX, int originY,
            int minX, int minY, int maxX, int maxY)
        {
            if (x < minX || x >= maxX || y < minY || y >= maxY ||
                !IsStructure(x, y) || !SameConnectionGroup(originX, originY, x, y)) return;
            int key = x + y * _grid.Width;
            if (!_components.ContainsKey(key))
            {
                _components.TryAdd(key, component);
                _queue.Add(key);
            }
        }

        private void BuildBoundaryEdges()
        {
            for (int i = 0; i < _affectedTiles.Length; i++)
            {
                int tileId = _affectedTiles[i];
                int tileX = tileId % _grid.TileColumns, tileY = tileId / _grid.TileColumns;
                int minX = tileX * TileSize, minY = tileY * TileSize;
                int maxX = Math.Min(minX + TileSize, _grid.Width), maxY = Math.Min(minY + TileSize, _grid.Height);
                for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
                {
                    CellVisits++;
                    if (!TryGetComponent(x, y, out int first)) continue;
                    AddBoundaryCandidate(first, x, y, x - 1, y);
                    AddBoundaryCandidate(first, x, y, x + 1, y);
                    AddBoundaryCandidate(first, x, y, x, y - 1);
                    AddBoundaryCandidate(first, x, y, x, y + 1);
                }
            }
        }

        private void AddBoundaryCandidate(int first, int x, int y, int neighbourX, int neighbourY)
        {
            EdgeVisits++;
            if (!TryGetComponent(neighbourX, neighbourY, out int second) || first == second ||
                !SameConnectionGroup(x, y, neighbourX, neighbourY)) return;
            AddBoundary(first, second, x, y);
        }

        private bool SameConnectionGroup(int firstX, int firstY, int secondX, int secondY)
        {
            if (!_grid.Inside(firstX, firstY) || !_grid.Inside(secondX, secondY)) return false;
            ushort first = ReadMaterial(firstX, firstY), second = ReadMaterial(secondX, secondY);
            if (first == 0 || second == 0) return false;
            return _grid.Definitions[first].ConnectionGroup == _grid.Definitions[second].ConnectionGroup;
        }

        private void AddBoundary(int first, int second, int x, int y)
        {
            if (!_affectedComponentIds.Contains(first) && !_affectedComponentIds.Contains(second)) return;
            ulong key = ((ulong)(uint)Math.Min(first, second) << 32) | (uint)Math.Max(first, second);
            if (_boundarySlots.ContainsKey(key)) return;
            int slot = _boundaryEdges.Length;
            if (!_boundarySlots.TryAdd(key, slot))
                throw new InvalidOperationException("结构边界索引容量不足。");
            _boundaryEdges.Add(new BoundaryEdgeV2(first, second, x, y));
            _boundaryKeysByRoot.Add(first, key);
            _boundaryKeysByRoot.Add(second, key);
            _adjacentRoots.Add(first, second);
            _adjacentRoots.Add(second, first);
        }

        private void RemoveTile(int tileId)
        {
            int tileX = tileId % _grid.TileColumns, tileY = tileId / _grid.TileColumns;
            int minX = tileX * TileSize, minY = tileY * TileSize;
            int maxX = Math.Min(minX + TileSize, _grid.Width), maxY = Math.Min(minY + TileSize, _grid.Height);
            for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
            {
                CellVisits++;
                int key = x + y * _grid.Width;
                if (_components.TryGetValue(key, out int component)) AddAffectedComponent(component);
                _components.Remove(key);
            }
            foreach (int component in _affectedComponentIds)
            {
                _componentRoots.Remove(component);
                _componentMembers.Remove(component);
            }
        }

        private void RemoveBoundaryEdgesForAffectedComponents()
        {
            // Remove both outgoing and reverse adjacency entries by root.  The query
            // path never needs to inspect the full boundary list.
            foreach (int component in _affectedComponentIds) RemoveAdjacencyForComponent(component);
        }

        private void RemoveAdjacencyForComponent(int component)
        {
            _adjacencyScratch.Clear();
            _boundaryKeyScratch.Clear();
            NativeParallelMultiHashMapIterator<int> iterator;
            if (_adjacentRoots.TryGetFirstValue(component, out int neighbour, out iterator))
            {
                do { _adjacencyScratch.Add(neighbour); EdgeVisits++; }
                while (_adjacentRoots.TryGetNextValue(out neighbour, ref iterator));
            }
            NativeParallelMultiHashMapIterator<int> edgeIterator;
            if (_boundaryKeysByRoot.TryGetFirstValue(component, out ulong edgeKey, out edgeIterator))
            {
                do { _boundaryKeyScratch.Add(edgeKey); EdgeVisits++; }
                while (_boundaryKeysByRoot.TryGetNextValue(out edgeKey, ref edgeIterator));
            }
            _adjacentRoots.Remove(component);
            for (int i = 0; i < _adjacencyScratch.Length; i++)
                RemoveAdjacencyValue(_adjacencyScratch[i], component);
            for (int i = 0; i < _boundaryKeyScratch.Length; i++)
                RemoveBoundaryEdge(_boundaryKeyScratch[i]);
        }

        private void RemoveBoundaryEdge(ulong key)
        {
            if (!_boundarySlots.TryGetValue(key, out int slot)) return;
            BoundaryEdgeV2 edge = _boundaryEdges[slot];
            _boundarySlots.Remove(key);
            RemoveBoundaryMembership(edge.FirstComponent, key);
            RemoveBoundaryMembership(edge.SecondComponent, key);
            int last = _boundaryEdges.Length - 1;
            if (slot != last)
            {
                BoundaryEdgeV2 moved = _boundaryEdges[last];
                _boundaryEdges[slot] = moved;
                _boundarySlots[moved.Key] = slot;
            }
            _boundaryEdges.ResizeUninitialized(last);
        }

        private void RemoveBoundaryMembership(int component, ulong key)
        {
            NativeParallelMultiHashMapIterator<int> iterator;
            if (!_boundaryKeysByRoot.TryGetFirstValue(component, out ulong value, out iterator)) return;
            do
            {
                if (value == key)
                {
                    _boundaryKeysByRoot.Remove(iterator);
                    return;
                }
            } while (_boundaryKeysByRoot.TryGetNextValue(out value, ref iterator));
        }

        private void RemoveAdjacencyValue(int component, int value)
        {
            _adjacencyValuesScratch.Clear();
            NativeParallelMultiHashMapIterator<int> iterator;
            if (_adjacentRoots.TryGetFirstValue(component, out int neighbour, out iterator))
            {
                do
                {
                    EdgeVisits++;
                    if (neighbour != value) _adjacencyValuesScratch.Add(neighbour);
                } while (_adjacentRoots.TryGetNextValue(out neighbour, ref iterator));
            }
            _adjacentRoots.Remove(component);
            for (int i = 0; i < _adjacencyValuesScratch.Length; i++)
                _adjacentRoots.Add(component, _adjacencyValuesScratch[i]);
        }

        private int CountConnectedComponents()
        {
            int requiredCapacity = Math.Max(256, _componentRoots.Count());
            if (_countVisited.Capacity < requiredCapacity) _countVisited.Capacity = requiredCapacity;
            _countVisited.Clear();
            LastCountVisited = 0;
            int count = 0;
            foreach (int root in _componentRoots)
            {
                if (!_countVisited.Add(root)) continue;
                LastCountVisited++;
                count++;
                _queue.Clear();
                _queue.Add(root);
                for (int head = 0; head < _queue.Length; head++)
                {
                    int current = _queue[head];
                    NativeParallelMultiHashMapIterator<int> iterator;
                    if (!_adjacentRoots.TryGetFirstValue(current, out int next, out iterator)) continue;
                    do
                    {
                        EdgeVisits++;
                        if (next != 0 && _countVisited.Add(next)) _queue.Add(next);
                    } while (_adjacentRoots.TryGetNextValue(out next, ref iterator));
                }
            }
            return count;
        }

        private void AddAffectedNeighbours(int tileId)
        {
            if (tileId < 0) return;
            AddTile(tileId);
            int x = tileId % _grid.TileColumns, y = tileId / _grid.TileColumns;
            if (x > 0) AddTile(tileId - 1);
            if (x + 1 < _grid.TileColumns) AddTile(tileId + 1);
            if (y > 0) AddTile(tileId - _grid.TileColumns);
            if ((y + 1) * TileSize < _grid.Height) AddTile(tileId + _grid.TileColumns);
        }

        private void AddTile(int tileId)
        {
            for (int i = 0; i < _affectedTiles.Length; i++) if (_affectedTiles[i] == tileId) return;
            _affectedTiles.Add(tileId);
        }

        private bool IsStructure(int x, int y)
        {
            if (!_grid.Inside(x, y)) return false;
            ushort material = ReadMaterial(x, y);
            if (material == 0) return false;
            MaterialDefinition definition = _grid.Definitions[material];
            return definition.Kind == MaterialKind.Solid && (definition.Rules & RuleMask.Structure) != 0;
        }

        private unsafe ushort ReadMaterial(int x, int y)
        {
            MaterialTile tile = _grid.Tiles[(x >> 5) + (y >> 5) * _grid.TileColumns];
            return tile.IsCreated ? tile.Material[(x & 31) + (y & 31) * 32] : (ushort)0;
        }

        private void BeginQueryRoots()
        {
            // Remove only keys touched by the previous query. NativeParallelHashSet.Clear
            // can walk a capacity grown for a much larger historical query.
            for (int i = 0; i < _queryVisitedRoots.Length; i++) _queryRoots.Remove(_queryVisitedRoots[i]);
            _queryVisitedRoots.Clear();
        }

        private bool AddQueryRoot(int root)
        {
            if (!_queryRoots.Add(root)) return false;
            _queryVisitedRoots.Add(root);
            return true;
        }

        private void EndQueryRoots()
        {
            for (int i = 0; i < _queryVisitedRoots.Length; i++) _queryRoots.Remove(_queryVisitedRoots[i]);
            _queryVisitedRoots.Clear();
        }

        private void ClearAffectedComponents()
        {
            for (int i = 0; i < _affectedComponentList.Length; i++)
                _affectedComponentIds.Remove(_affectedComponentList[i]);
            _affectedComponentList.Clear();
        }

        private bool AddAffectedComponent(int component)
        {
            if (!_affectedComponentIds.Add(component)) return false;
            _affectedComponentList.Add(component);
            return true;
        }

        private void EnsureTileScratch()
        {
            if (_affectedTiles.Capacity < Math.Max(256, _grid.AllocatedTileCount * 5))
                _affectedTiles.Capacity = Math.Max(256, _grid.AllocatedTileCount * 5);
            if (_components.Capacity < Math.Max(1024, _grid.AllocatedTileCount * CellsPerTile))
                _components.Capacity = Math.Max(1024, _grid.AllocatedTileCount * CellsPerTile);
            if (_componentMembers.Capacity < Math.Max(1024, _grid.AllocatedTileCount * CellsPerTile))
                _componentMembers.Capacity = Math.Max(1024, _grid.AllocatedTileCount * CellsPerTile);
            if (_adjacentRoots.Capacity < Math.Max(512, _grid.AllocatedTileCount * 128))
                _adjacentRoots.Capacity = Math.Max(512, _grid.AllocatedTileCount * 128);
            if (_boundarySlots.Capacity < Math.Max(256, _grid.AllocatedTileCount * 128))
                _boundarySlots.Capacity = Math.Max(256, _grid.AllocatedTileCount * 128);
            if (_boundaryKeysByRoot.Capacity < Math.Max(512, _grid.AllocatedTileCount * 256))
                _boundaryKeysByRoot.Capacity = Math.Max(512, _grid.AllocatedTileCount * 256);
            if (_queue.Capacity < Math.Max(CellsPerTile, _grid.AllocatedTileCount * 2))
                _queue.Capacity = Math.Max(CellsPerTile, _grid.AllocatedTileCount * 2);
            if (_affectedComponentIds.Capacity < Math.Max(256, _grid.AllocatedTileCount * 2))
                _affectedComponentIds.Capacity = Math.Max(256, _grid.AllocatedTileCount * 2);
            if (_affectedComponentList.Capacity < Math.Max(256, _grid.AllocatedTileCount * 2))
                _affectedComponentList.Capacity = Math.Max(256, _grid.AllocatedTileCount * 2);
            if (_queryVisitedRoots.Capacity < 256) _queryVisitedRoots.Capacity = 256;
            if (_adjacencyScratch.Capacity < Math.Max(256, _grid.AllocatedTileCount * 4))
                _adjacencyScratch.Capacity = Math.Max(256, _grid.AllocatedTileCount * 4);
            if (_adjacencyValuesScratch.Capacity < Math.Max(256, _grid.AllocatedTileCount * 4))
                _adjacencyValuesScratch.Capacity = Math.Max(256, _grid.AllocatedTileCount * 4);
            if (_boundaryKeyScratch.Capacity < Math.Max(256, _grid.AllocatedTileCount * 4))
                _boundaryKeyScratch.Capacity = Math.Max(256, _grid.AllocatedTileCount * 4);
        }

        private void EnsureUsable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterialConnectivity));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_components.IsCreated) _components.Dispose();
            if (_componentMembers.IsCreated) _componentMembers.Dispose();
            if (_adjacentRoots.IsCreated) _adjacentRoots.Dispose();
            if (_boundaryEdges.IsCreated) _boundaryEdges.Dispose();
            if (_boundarySlots.IsCreated) _boundarySlots.Dispose();
            if (_boundaryKeysByRoot.IsCreated) _boundaryKeysByRoot.Dispose();
            if (_affectedTiles.IsCreated) _affectedTiles.Dispose();
            if (_queue.IsCreated) _queue.Dispose();
            if (_componentCells.IsCreated) _componentCells.Dispose();
            if (_affectedComponentIds.IsCreated) _affectedComponentIds.Dispose();
            if (_affectedComponentList.IsCreated) _affectedComponentList.Dispose();
            if (_componentRoots.IsCreated) _componentRoots.Dispose();
            if (_countVisited.IsCreated) _countVisited.Dispose();
            if (_queryRoots.IsCreated) _queryRoots.Dispose();
            if (_queryVisitedRoots.IsCreated) _queryVisitedRoots.Dispose();
            if (_oldGroupRootList.IsCreated) _oldGroupRootList.Dispose();
            if (_adjacencyScratch.IsCreated) _adjacencyScratch.Dispose();
            if (_adjacencyValuesScratch.IsCreated) _adjacencyValuesScratch.Dispose();
            if (_boundaryKeyScratch.IsCreated) _boundaryKeyScratch.Dispose();
        }
    }

    public readonly struct BoundaryEdgeV2
    {
        public readonly int FirstComponent;
        public readonly int SecondComponent;
        public readonly int X;
        public readonly int Y;
        public ulong Key => ((ulong)(uint)Math.Min(FirstComponent, SecondComponent) << 32) | (uint)Math.Max(FirstComponent, SecondComponent);
        public BoundaryEdgeV2(int firstComponent, int secondComponent, int x, int y)
        { FirstComponent = firstComponent; SecondComponent = secondComponent; X = x; Y = y; }
    }
}
