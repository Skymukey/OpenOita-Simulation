using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Spatial;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenOita.V2
{
    /// <summary>
    /// V2 physics owner. Material grids remain the authority; Unity objects are a persistent solver bridge.
    /// </summary>
    public sealed unsafe class MaterialPhysics : IDisposable
    {
        private readonly WorldConfig _config;
        private readonly MaterialGrid _grid;
        private readonly Vector2 _origin;
        private readonly bool _freezeRotation;
        private readonly List<GameObject> _boundaries = new List<GameObject>(4);
        private readonly Dictionary<int, List<BoxCollider2D>> _fixedTileColliders = new Dictionary<int, List<BoxCollider2D>>();
        private readonly Dictionary<int, int> _fixedTileShapeCounts = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _logicalFixedTileShapeCounts = new Dictionary<int, int>();
        private readonly HashSet<int> _activeFixedTiles = new HashSet<int>();
        private readonly HashSet<int> _desiredFixedTiles = new HashSet<int>();
        private readonly HashSet<int> _fixedDirtyTiles = new HashSet<int>();
        private readonly List<int> _fixedTilesToDisable = new List<int>(128);
        private readonly List<BodyRuntime> _runtimeBodies = new List<BodyRuntime>();
        private readonly Dictionary<ulong, BodyV2> _bodiesById = new Dictionary<ulong, BodyV2>(64);
        private readonly Dictionary<int, BodyV2> _bodiesByGridHandle = new Dictionary<int, BodyV2>(64);
        private readonly Dictionary<ulong, BodyRuntime> _runtimeById = new Dictionary<ulong, BodyRuntime>(64);
        private readonly NativeBodySpatialIndex _bodySpatialIndex;
        private NativeList<ulong> _bodyCandidateIds;
        private NativeList<int> _dirtyTiles;
        private NativeList<WetContactV2> _wetContacts;
        private NativeParallelHashSet<ulong> _wetKeys;
        private readonly MaterialConnectivity _connectivity;
        private NativeList<ushort> _wetMaterialIds;
        private NativeList<int> _componentRoots;
        private NativeList<int> _groupRoots;
        private NativeList<int> _componentCells;
        private NativeList<int> _queryTiles;
        private NativeList<SuspendedFluidV2> _suspended;
        private NativeList<int> _wetComponents;
        private NativeParallelHashSet<int> _wetComponentKeys;
        private NativeParallelHashMap<int, int> _coverageCounts;
        private int _mainWetCellCount;
        private NativeList<int> _restoreQueue;
        private NativeList<int> _restoreDistances;
        private NativeParallelHashSet<int> _restoreVisited;
        private NativeList<SuspendedScheduleV2> _restoreSchedule;
        private NativeList<ulong> _restoreReadyIds;
        private NativeParallelHashSet<ulong> _restoreReadySet;
        // Suspension records are indexed by stable id.  Bucket links are stored in each
        // record, so waking/removing a record never scans the suspended list.
        private NativeParallelHashMap<ulong, int> _suspendedSlots;
        private NativeParallelHashMap<int, ulong> _suspendedBucketHeads;
        private NativeParallelHashMap<int, ulong> _suspendedComponentRecords;
        private NativeParallelHashMap<ulong, int> _restoreScheduleSlots;
        private int _restoreScheduleCursor;
        private NativeParallelHashSet<ulong> _ignitionKeys;
        private readonly HashSet<ulong> _extractedBodyIds = new HashSet<ulong>();
        // Reused by dynamic-body fracture.  These are managed containers only for
        // root/cell query results; the persistent connectivity graph itself lives on
        // BodyRuntime so an unchanged body never allocates or rebuilds it.
        private readonly HashSet<int> _splitVisitedRoots = new HashSet<int>();
        private readonly List<BodyV2> _splitReplacements = new List<BodyV2>(8);
        private readonly HashSet<int> _fixedRoots = new HashSet<int>();
        private readonly HashSet<int> _usedGridHandles = new HashSet<int>();
        private ulong _nextBodyId = 1;
        private int _nextGridHandle = 2;
        private bool _mainHandleRegistered;
        private PhysicsMaterial2D _surface;
        private GameObject _fixedTerrain;
        private int _fixedShapeCount;
        private int _logicalFixedShapeCount;
        private ulong _nextSuspendedId = 1;
        private Scene _scene;
        private PhysicsScene2D _physics;
        private bool _initialized;
        private bool _structuresBuilt;
        private bool _inStepCapture;
        private ulong _lastPostWetCaptureTick = ulong.MaxValue;
        private bool _disposed;

        private struct CachedSolidCell
        {
            // Keep integer local coordinates. ExactCellGeometry multiplies the integer
            // coordinate by the float cell size in double precision before rounding each
            // transformed corner to float; storing pre-multiplied float2 values here would
            // create a second geometry convention at large offsets.
            public int X, Y;
            public CachedSolidCell(int x, int y) { X = x; Y = y; }
        }

        public List<BodyV2> Bodies { get; } = new List<BodyV2>();
        public int BodyCount => Bodies.Count;
        public NativeArray<WetContactV2> WetContacts => _wetContacts.AsArray();
        public NativeArray<int> WetComponents => _wetComponents.AsArray();
        public int SuspendedCount => _suspended.Length;
        public int ActiveFixedTerrainShapeCount => _fixedShapeCount;
        public int LogicalFixedTerrainShapeCount => _logicalFixedShapeCount;
        public bool FreezeBodyRotation => _freezeRotation;
        // Cumulative counters since the owning connectivity instance was created or
        // its work counters were reset. Units are logical structure cells/graph
        // adjacency accesses, not unique cells/edges.
        internal long StructureCellVisits => _connectivity.CellVisits;
        internal long StructureEdgeVisits => _connectivity.EdgeVisits;
        internal long BodyStructureCellVisits
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _runtimeBodies.Count; i++) total += _runtimeBodies[i].Connectivity.CellVisits;
                return total;
            }
        }
        internal long BodyStructureEdgeVisits
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _runtimeBodies.Count; i++) total += _runtimeBodies[i].Connectivity.EdgeVisits;
                return total;
            }
        }
        // Cumulative candidate iterations performed by the existing coverage and
        // wet-contact loops. Coverage counts clamped main-grid cells; wet counts
        // occupied material bits tested after the AABB page/row filter.
        internal long CoverageCandidateVisits { get; private set; }
        internal long WetCandidateVisits { get; private set; }

        public SuspendedFluidV2 GetSuspended(int index) => _suspended[index];

        public bool TryGetBody(ulong id, out BodyV2 body)
        {
            if (_bodiesById.TryGetValue(id, out body)) return true;
            body = null;
            return false;
        }

        public bool TryGetBodyByGridHandle(int gridHandle, out BodyV2 body)
        {
            if (_bodiesByGridHandle.TryGetValue(gridHandle, out body)) return true;
            body = null;
            return false;
        }

        /// <summary>
        /// Returns body IDs whose conservative world-cell AABB intersects the supplied
        /// world-space rectangle. The conversion is clamped to the configured world
        /// envelope so a finite but enormous query cannot walk billions of empty buckets.
        /// Results are sorted and deduplicated by NativeBodySpatialIndex.
        /// </summary>
        internal bool QueryBodyCandidates(Vector2 min, Vector2 max, NativeList<ulong> destination)
        {
            EnsureInitialized();
            if (!destination.IsCreated) throw new ArgumentException("查询目标列表未创建。", nameof(destination));
            if (!ContractDefaults.IsFinite(min) || !ContractDefaults.IsFinite(max))
                throw new ArgumentOutOfRangeException(nameof(min), "刚体候选查询范围必须为有限值。");
            if (destination.Capacity < Bodies.Count)
                destination.Capacity = Math.Max(1, Bodies.Count);
            if (!TryGetSpatialQueryRect(min, max, out int minX, out int minY, out int maxX, out int maxY))
            {
                destination.Clear();
                return true;
            }
            bool success = _bodySpatialIndex.QueryRect(minX, minY, maxX, maxY, destination, out int requiredCount);
            if (!success)
                throw new InvalidOperationException("刚体空间索引候选容量不足，需要 " + requiredCount + " 个结果。");
            return true;
        }

        public bool TryReadBodyCell(int gridHandle, int x, int y, out GridCell cell)
        {
            if (TryGetBodyByGridHandle(gridHandle, out BodyV2 body) && body.Grid.Inside(x, y))
            {
                cell = body.Grid.Read(x, y);
                return true;
            }
            cell = default;
            return false;
        }

        /// <summary>Assigns stable, unique handles used by shared timed components.</summary>
        public void RegisterBodyGridsForTimers()
        {
            if (_grid.GridHandle == 0) _grid.GridHandle = 1;
            if (!_mainHandleRegistered)
            {
                for (int i = 0; i < _grid.ColdStore.Records.Length; i++)
                {
                    CellCold state = _grid.ColdStore.Records[i];
                    if (state.MaterialId == 0 || state.GridHandle != 0) continue;
                    state.GridHandle = _grid.GridHandle;
                    _grid.ColdStore.Records[i] = state;
                }
                _mainHandleRegistered = true;
            }
            _usedGridHandles.Clear();
            _usedGridHandles.Add(_grid.GridHandle);
            for (int i = 0; i < Bodies.Count; i++)
            {
                MaterialGrid child = Bodies[i].Grid;
                if (child.GridHandle == 0 || _usedGridHandles.Contains(child.GridHandle))
                {
                    while (_usedGridHandles.Contains(_nextGridHandle)) _nextGridHandle++;
                    child.GridHandle = _nextGridHandle++;
                }
                _usedGridHandles.Add(child.GridHandle);
            }
            RebuildBodyLookups();
        }

        public MaterialPhysics(WorldConfig config, MaterialGrid grid, Vector2 origin, bool freezeRotation)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _origin = origin;
            _freezeRotation = freezeRotation;
            _dirtyTiles = new NativeList<int>(256, Allocator.Persistent);
            _wetContacts = new NativeList<WetContactV2>(4096, Allocator.Persistent);
            _wetKeys = new NativeParallelHashSet<ulong>(4096, Allocator.Persistent);
            _connectivity = new MaterialConnectivity(_grid);
            _wetMaterialIds = new NativeList<ushort>(64, Allocator.Persistent);
            for (int id = 1; id < _grid.Definitions.Length; id++)
                if (IsWetMaterial(_grid, (ushort)id)) _wetMaterialIds.Add((ushort)id);
            _componentRoots = new NativeList<int>(256, Allocator.Persistent);
            _groupRoots = new NativeList<int>(64, Allocator.Persistent);
            _componentCells = new NativeList<int>(1024, Allocator.Persistent);
            _queryTiles = new NativeList<int>(256, Allocator.Persistent);
            _suspended = new NativeList<SuspendedFluidV2>(256, Allocator.Persistent);
            _wetComponents = new NativeList<int>(4096, Allocator.Persistent);
            _wetComponentKeys = new NativeParallelHashSet<int>(4096, Allocator.Persistent);
            _coverageCounts = new NativeParallelHashMap<int, int>(4096, Allocator.Persistent);
            _restoreQueue = new NativeList<int>(4096, Allocator.Persistent);
            _restoreDistances = new NativeList<int>(4096, Allocator.Persistent);
            _restoreVisited = new NativeParallelHashSet<int>(4096, Allocator.Persistent);
            _restoreSchedule = new NativeList<SuspendedScheduleV2>(256, Allocator.Persistent);
            _restoreReadyIds = new NativeList<ulong>(4096, Allocator.Persistent);
            _restoreReadySet = new NativeParallelHashSet<ulong>(4096, Allocator.Persistent);
            _suspendedSlots = new NativeParallelHashMap<ulong, int>(4096, Allocator.Persistent);
            _suspendedBucketHeads = new NativeParallelHashMap<int, ulong>(4096, Allocator.Persistent);
            _suspendedComponentRecords = new NativeParallelHashMap<int, ulong>(4096, Allocator.Persistent);
            _restoreScheduleSlots = new NativeParallelHashMap<ulong, int>(4096, Allocator.Persistent);
            _restoreScheduleCursor = 0;
            _ignitionKeys = new NativeParallelHashSet<ulong>(4096, Allocator.Persistent);
            int bodyCapacity = Math.Max(1, _config.Limits.MaxDynamicBodies);
            _bodySpatialIndex = new NativeBodySpatialIndex(bodyCapacity,
                Math.Max(256, bodyCapacity * 16), bodyCapacity);
            _bodyCandidateIds = new NativeList<ulong>(bodyCapacity, Allocator.Persistent);
        }

        public BodyV2 GetBody(int index) => Bodies[index];

        /// <summary>Applies a benchmark/host motion command to both the model and its solver body.</summary>
        public bool ConfigureBodyMotion(ulong bodyId, BodyMotion motion, bool sleep)
        {
            if (!ContractDefaults.IsFinite(motion.LinearVelocity) ||
                !ContractDefaults.IsFinite(motion.AngularVelocityRadians) ||
                motion.LinearVelocity.magnitude > _config.Limits.MaxLinearSpeed ||
                Math.Abs(motion.AngularVelocityRadians) > _config.Limits.MaxAngularSpeedDegrees * Mathf.Deg2Rad)
                return false;
            if (!TryGetBody(bodyId, out BodyV2 body)) return false;
            int runtimeIndex = FindRuntime(body);
            if (runtimeIndex < 0) return false;
            BodyRuntime runtime = _runtimeBodies[runtimeIndex];
            body.Motion = motion;
            runtime.Rigidbody.linearVelocity = motion.LinearVelocity;
            runtime.Rigidbody.angularVelocity = _freezeRotation ? 0 : motion.AngularVelocityRadians * Mathf.Rad2Deg;
            if (sleep) runtime.Rigidbody.Sleep();
            else runtime.Rigidbody.WakeUp();
            return true;
        }

        /// <summary>Returns the native solver sleep state used by the performance runner.</summary>
        public bool IsBodySleeping(ulong bodyId)
        {
            if (!TryGetBody(bodyId, out BodyV2 body)) return false;
            int runtimeIndex = FindRuntime(body);
            return runtimeIndex >= 0 && _runtimeBodies[runtimeIndex].Rigidbody.IsSleeping();
        }

        /// <summary>
        /// Collects only burnable cells which touch one burning source cell.  The broad phase is
        /// a world-cell AABB transformed into each candidate body's local range; exact SAT/contact
        /// is then evaluated per local cell.  No material-wide scan is performed.
        /// </summary>
        public int CollectIgnitionTargets(in CellKey source, NativeList<CellContact> destination, int budget = 4096)
        {
            EnsureInitialized();
            if (!destination.IsCreated) throw new ArgumentException("目标缓冲必须已创建。", nameof(destination));
            if (budget < 0 || budget > 4096) throw new ArgumentOutOfRangeException(nameof(budget));
            _ignitionKeys.Clear();
            if (budget == 0) return 0;

            if (source.Position.OwnerKind == OwnerKind.Grid)
            {
                GridCell sourceCell = _grid.Read(source.Position.X, source.Position.Y);
                if (!sourceCell.IsBurning) return 0;
                CellGeometry sourceGeometry = MainGeometry(source);
                GetWorldAabb(sourceGeometry, out float sourceMinX, out float sourceMinY,
                    out float sourceMaxX, out float sourceMaxY);
                QueryBodyCandidates(new Vector2(sourceMinX, sourceMinY),
                    new Vector2(sourceMaxX, sourceMaxY), _bodyCandidateIds);
                int written = 0;
                for (int i = 0; i < _bodyCandidateIds.Length && written < budget; i++)
                {
                    ulong bodyId = _bodyCandidateIds[i];
                    if (!_runtimeById.TryGetValue(bodyId, out BodyRuntime candidate)) continue;
                    written = CollectBodyTargets(sourceGeometry, source.Generation, candidate, destination, written, budget);
                }
                return written;
            }

            if (source.Position.OwnerKind != OwnerKind.Body || !TryGetBody(source.Position.BodyId, out BodyV2 sourceBody))
                return 0;
            if (!sourceBody.Grid.Inside(source.Position.X, source.Position.Y)) return 0;
            GridCell sourceCellInBody = sourceBody.Grid.Read(source.Position.X, source.Position.Y);
            if (!sourceCellInBody.IsBurning) return 0;
            CellGeometry bodySource = BodyGeometry(source, sourceBody);
            int count = CollectMainGridTargets(bodySource, source.Generation, destination, 0, budget);
            GetWorldAabb(bodySource, out float bodySourceMinX, out float bodySourceMinY,
                out float bodySourceMaxX, out float bodySourceMaxY);
            QueryBodyCandidates(new Vector2(bodySourceMinX, bodySourceMinY),
                new Vector2(bodySourceMaxX, bodySourceMaxY), _bodyCandidateIds);
            for (int i = 0; i < _bodyCandidateIds.Length && count < budget; i++)
            {
                ulong candidateId = _bodyCandidateIds[i];
                if (candidateId == sourceBody.Id || !_runtimeById.TryGetValue(candidateId, out BodyRuntime candidate)) continue;
                count = CollectBodyTargets(bodySource, source.Generation, candidate, destination, count, budget);
            }
            return count;
        }

        /// <summary>
        /// Marks a valid burnable target for the next MaterialWorld ignition pass.  This only sets
        /// the per-tile deferred bit; it does not alter fuel, burning flags or timed state.
        /// </summary>
        public bool QueueDeferredIgnition(in CellKey target, ulong tick)
        {
            EnsureInitialized();
            MaterialGrid owner = null;
            if (target.Position.OwnerKind == OwnerKind.Grid) owner = _grid;
            else if (target.Position.OwnerKind == OwnerKind.Body && TryGetBody(target.Position.BodyId, out BodyV2 body)) owner = body.Grid;
            if (owner == null || !owner.Inside(target.Position.X, target.Position.Y)) return false;
            GridCell cell = owner.Read(target.Position.X, target.Position.Y);
            if (!IsIgnitionTarget(owner, cell)) return false;
            int tileId = owner.TileId(target.Position.X, target.Position.Y);
            MaterialTile tile = owner.Tiles[tileId];
            if (!tile.IsCreated) return false;
            int row = target.Position.Y & 31;
            uint bit = 1u << (target.Position.X & 31);
            bool wasQueued = (tile.Ignition[row] & bit) != 0;
            owner.RequestIgnition(target.Position.X, target.Position.Y);
            return !wasQueued;
        }

        public StructureCapacityV2 CurrentStructureCapacity()
        {
            int dynamicShapes = 0, maxBodyShapes = 0;
            for (int i = 0; i < Bodies.Count; i++)
            {
                int shapes = Math.Max(0, Bodies[i].ShapeCount);
                dynamicShapes += shapes;
                maxBodyShapes = Math.Max(maxBodyShapes, shapes);
            }
            // StaticShapes is the logical fixed-terrain budget.  _fixedShapeCount is only
            // the currently materialized Unity bridge subset around dynamic bodies.
            return new StructureCapacityV2(Bodies.Count, _logicalFixedShapeCount, dynamicShapes, maxBodyShapes);
        }

        /// <summary>Checks projected counts without touching grids, bodies or collider pools.</summary>
        public WorldResult PreflightStructureCapacity(in StructureCapacityV2 projected)
        {
            if (projected.DynamicBodies < 0 || projected.StaticShapes < 0 || projected.DynamicShapes < 0 || projected.MaxBodyShapes < 0)
                return WorldResult.Failure(WorldErrorCode.InvalidArgument,
                    new WorldDiagnostic("V2Physics", "structure.preflight", "结构容量预检输入不能为负。"));
            if (projected.DynamicBodies > _config.Limits.MaxDynamicBodies)
                return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("V2Physics", "structure.bodies", "局部结构草稿会超过动态体容量。"));
            if (projected.MaxBodyShapes > _config.Limits.MaxShapesPerBody)
                return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("V2Physics", "structure.bodyShapes", "局部结构草稿会超过单动态体形状容量。"));
            if (projected.TotalShapes > _config.Limits.MaxTotalShapes)
                return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("V2Physics", "structure.totalShapes", "局部结构草稿会超过全局形状容量。"));
            return WorldResult.Success();
        }

        /// <summary>
        /// Counts horizontal collider runs in a caller-owned local draft.  It is an upper bound
        /// for fixed terrain (the fixed builder may merge vertically) and an exact count for the
        /// independent-body tile builder.  The draft is read only.
        /// </summary>
        public WorldResult PreflightStructureDraft(NativeArray<GridCell> draft, int width, int height,
            bool dynamicBody, out StructureCapacityV2 estimate)
        {
            estimate = default;
            if (!draft.IsCreated || width <= 0 || height <= 0 || (long)width * height != draft.Length)
                return WorldResult.Failure(WorldErrorCode.InvalidArgument,
                    new WorldDiagnostic("V2Physics", "structure.draft", "结构草稿尺寸或缓冲无效。"));
            int shapes = 0;
            for (int y = 0; y < height; y++)
            {
                bool inRun = false;
                for (int x = 0; x < width; x++)
                {
                    GridCell cell = draft[x + y * width];
                    bool solid = cell.MaterialId != 0 && IsSolid(_grid, cell.MaterialId);
                    if (solid)
                    {
                        if (!inRun) { shapes++; inRun = true; }
                    }
                    else inRun = false;
                }
            }
            estimate = dynamicBody
                ? new StructureCapacityV2(1, 0, shapes, shapes)
                : new StructureCapacityV2(0, shapes, 0, 0);
            StructureCapacityV2 current = CurrentStructureCapacity();
            StructureCapacityV2 projected = new StructureCapacityV2(
                current.DynamicBodies + estimate.DynamicBodies,
                current.StaticShapes + estimate.StaticShapes,
                current.DynamicShapes + estimate.DynamicShapes,
                Math.Max(current.MaxBodyShapes, estimate.MaxBodyShapes));
            return PreflightStructureCapacity(projected);
        }

        /// <summary>
        /// Preflights a structural command against only the affected connected components.  A
        /// remove/replace is projected in a private dictionary, so a support removal which splits
        /// one free component into N bodies is charged as N bodies before the real grid is touched.
        /// </summary>
        public WorldResult PreflightCommand(in MaterialCommand command, out StructureCapacityV2 projected)
        {
            Span<MaterialCommand> one = stackalloc MaterialCommand[1];
            one[0] = command;
            return PreflightCommands(one, out projected);
        }

        /// <summary>Preflights all commands in order over one local projected component map.</summary>
        public WorldResult PreflightCommands(ReadOnlySpan<MaterialCommand> commands, out StructureCapacityV2 projected)
        {
            EnsureInitialized();
            projected = CurrentStructureCapacity();
            if (!_structuresBuilt)
                return WorldResult.Failure(WorldErrorCode.NotReady,
                    new WorldDiagnostic("V2Physics", "structure.commands", "结构命令预检需要已建立的结构目录。"));
            if (commands.Length == 0) return WorldResult.Success();

            var main = new Dictionary<int, GridCell>(256);
            var mainRanges = new List<CommandRange>(commands.Length);
            var bodyRanges = new List<BodyCommandRange>(_runtimeBodies.Count);
            int initialMappedCells = 0;
            for (int i = 0; i < commands.Length; i++)
            {
                MaterialCommand command = commands[i];
                if ((command.Operation == MaterialOperation.Spawn || command.Operation == MaterialOperation.Replace) &&
                    (command.MaterialId == 0 || _grid.Definitions[command.MaterialId].Id == 0))
                    return WorldResult.Failure(WorldErrorCode.UnknownMaterial,
                        new WorldDiagnostic("V2Physics", "structure.commands", "结构命令材料未注册。"));
                if (!TryGetGridCommandRange(command.Region, out int gx0, out int gy0, out int gx1, out int gy1))
                    return WorldResult.Failure(WorldErrorCode.InvalidArgument,
                        new WorldDiagnostic("V2Physics", "structure.commands", "结构命令区域无效。"));
                mainRanges.Add(new CommandRange(gx0, gy0, gx1, gy1));
                if (gx0 <= gx1 && gy0 <= gy1)
                {
                    CollectAffectedStructure(_grid, gx0, gy0, gx1, gy1, main);
                    EnsureRangeCells(_grid, gx0, gy0, gx1, gy1, main, null, default);
                }
            }
            initialMappedCells += CountNonEmpty(main);

            for (int b = 0; b < _runtimeBodies.Count; b++)
            {
                BodyV2 body = _runtimeBodies[b].Body;
                var plan = new BodyCommandRange(body, commands.Length);
                for (int i = 0; i < commands.Length; i++)
                {
                    if (commands[i].Operation == MaterialOperation.Spawn ||
                        !TryGetBodyCommandRange(body, commands[i].Region, out int bx0, out int by0, out int bx1, out int by1) ||
                        bx0 > bx1 || by0 > by1)
                    {
                        plan.Ranges.Add(default);
                        continue;
                    }
                    plan.Ranges.Add(new CommandRange(bx0, by0, bx1, by1));
                    CollectAffectedStructure(body.Grid, bx0, by0, bx1, by1, plan.Draft);
                    EnsureRangeCells(body.Grid, bx0, by0, bx1, by1, plan.Draft, body, commands[i].Region);
                }
                plan.InitialMappedCells = CountNonEmpty(plan.Draft);
                CountProjectedComponents(body.Grid, plan.Draft, out plan.InitialBodyCount,
                    out _, out _, out plan.InitialStaticShapes);
                initialMappedCells += plan.InitialMappedCells;
                bodyRanges.Add(plan);
            }

            CountProjectedComponents(_grid, main, out _, out _, out _, out int beforeMainStaticShapes);
            int workingCells = TotalMaterialCells();
            for (int i = 0; i < commands.Length; i++)
            {
                MaterialCommand command = commands[i];
                CommandRange range = mainRanges[i];
                if (command.Operation == MaterialOperation.Spawn)
                {
                    long requested = range.IsEmpty ? 0L : (long)range.Width * range.Height;
                    if (workingCells + requested > _config.Limits.MaxMaterialCells)
                        return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                            new WorldDiagnostic("V2Physics", "structure.commands.cells", "批量生成超过材料容量。"));
                }
                if (!range.IsEmpty && !ApplyProjectedCommand(_grid, command, range.X0, range.Y0, range.X1, range.Y1, main, null))
                    return WorldResult.Failure(WorldErrorCode.Occupied,
                        new WorldDiagnostic("V2Physics", "structure.commands", "命令预检发现生成区域已有材料或动态覆盖。"));
                for (int b = 0; b < bodyRanges.Count; b++)
                {
                    BodyCommandRange body = bodyRanges[b];
                    CommandRange bodyRange = body.Ranges[i];
                    if (bodyRange.IsEmpty) continue;
                    if (!ApplyProjectedCommand(body.Body.Grid, command, bodyRange.X0, bodyRange.Y0,
                        bodyRange.X1, bodyRange.Y1, body.Draft, body.Body))
                        return WorldResult.Failure(WorldErrorCode.UnsupportedOperation,
                            new WorldDiagnostic("V2Physics", "structure.commands", "动态体结构命令预检失败。"));
                }
                int currentMapped = CountNonEmpty(main);
                for (int b = 0; b < bodyRanges.Count; b++) currentMapped += CountNonEmpty(bodyRanges[b].Draft);
                workingCells = TotalMaterialCells() - initialMappedCells + currentMapped;
                if (workingCells > _config.Limits.MaxMaterialCells)
                    return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                        new WorldDiagnostic("V2Physics", "structure.commands.cells", "批量编辑超过材料容量。"));
            }

            CountProjectedComponents(_grid, main, out int mainBodies, out int mainShapes, out int mainMaxShapes, out int mainStaticShapes);
            int dynamicBodies = projected.DynamicBodies + mainBodies;
            int dynamicShapes = projected.DynamicShapes + mainShapes;
            int maxBodyShapes = Math.Max(projected.MaxBodyShapes, mainMaxShapes);
            int staticShapes = Math.Max(0, projected.StaticShapes + mainStaticShapes - beforeMainStaticShapes);
            for (int b = 0; b < bodyRanges.Count; b++)
            {
                BodyCommandRange body = bodyRanges[b];
                CountProjectedComponents(body.Body.Grid, body.Draft,
                    out int afterBodies, out int afterShapes, out int afterMax, out int afterStaticShapes);
                if (body.InitialBodyCount != 0 || afterBodies != 0)
                {
                    dynamicBodies += afterBodies - Math.Max(1, body.InitialBodyCount);
                    dynamicShapes += afterShapes - Math.Max(0, body.Body.ShapeCount);
                    maxBodyShapes = Math.Max(maxBodyShapes, afterMax);
                }
                staticShapes = Math.Max(0, staticShapes + afterStaticShapes - body.InitialStaticShapes);
            }
            projected = new StructureCapacityV2(dynamicBodies, staticShapes, dynamicShapes, maxBodyShapes);
            return PreflightStructureCapacity(projected);
        }

        private bool TryGetGridCommandRange(in WorldRect region, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (!Finite(region.Min) || !Finite(region.Max) || region.Max.x <= region.Min.x || region.Max.y <= region.Min.y)
                return false;
            float size = _config.CellSize;
            int maxXExclusive = Mathf.CeilToInt((region.Max.x - _origin.x) / size - 0.5f);
            int maxYExclusive = Mathf.CeilToInt((region.Max.y - _origin.y) / size - 0.5f);
            x0 = Mathf.Clamp(Mathf.CeilToInt((region.Min.x - _origin.x) / size - 0.5f), 0, _grid.Width);
            y0 = Mathf.Clamp(Mathf.CeilToInt((region.Min.y - _origin.y) / size - 0.5f), 0, _grid.Height);
            x1 = Mathf.Clamp(maxXExclusive - 1, -1, _grid.Width - 1);
            y1 = Mathf.Clamp(maxYExclusive - 1, -1, _grid.Height - 1);
            return true;
        }

        private bool TryGetBodyCommandRange(BodyV2 body, in WorldRect region,
            out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (!Finite(region.Min) || !Finite(region.Max) || region.Max.x <= region.Min.x || region.Max.y <= region.Min.y)
                return false;
            Vector2 a = Inverse(body.Pose, region.Min), b = Inverse(body.Pose, new Vector2(region.Min.x, region.Max.y));
            Vector2 c = Inverse(body.Pose, new Vector2(region.Max.x, region.Min.y)), d = Inverse(body.Pose, region.Max);
            float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
            float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
            float minY = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y));
            float maxY = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
            x0 = Mathf.Clamp(Mathf.CeilToInt(minX / _config.CellSize - 0.5f), 0, body.Grid.Width);
            y0 = Mathf.Clamp(Mathf.CeilToInt(minY / _config.CellSize - 0.5f), 0, body.Grid.Height);
            x1 = Mathf.Clamp(Mathf.CeilToInt(maxX / _config.CellSize - 0.5f) - 1, -1, body.Grid.Width - 1);
            y1 = Mathf.Clamp(Mathf.CeilToInt(maxY / _config.CellSize - 0.5f) - 1, -1, body.Grid.Height - 1);
            return true;
        }

        private static bool Finite(Vector2 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y);

        private void CollectAffectedStructure(MaterialGrid owner, int x0, int y0, int x1, int y1,
            Dictionary<int, GridCell> destination)
        {
            int minX = Math.Max(0, x0 - 1), minY = Math.Max(0, y0 - 1);
            int maxX = Math.Min(owner.Width - 1, x1 + 1), maxY = Math.Min(owner.Height - 1, y1 + 1);
            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
            {
                int seed = x + y * owner.Width;
                if (!visited.Add(seed) || !IsSolid(owner, owner.Read(x, y).MaterialId)) continue;
                queue.Enqueue(seed);
                while (queue.Count != 0)
                {
                    int key = queue.Dequeue();
                    int cx = key % owner.Width, cy = key / owner.Width;
                    GridCell source = owner.Read(cx, cy);
                    destination[key] = source;
                    TryAddStructureNeighbour(owner, source, cx - 1, cy, visited, queue);
                    TryAddStructureNeighbour(owner, source, cx + 1, cy, visited, queue);
                    TryAddStructureNeighbour(owner, source, cx, cy - 1, visited, queue);
                    TryAddStructureNeighbour(owner, source, cx, cy + 1, visited, queue);
                }
            }
        }

        private void EnsureRangeCells(MaterialGrid owner, int x0, int y0, int x1, int y1,
            Dictionary<int, GridCell> destination, BodyV2 body, in WorldRect region)
        {
            if (x0 > x1 || y0 > y1) return;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                if (body != null)
                {
                    float cosine = Mathf.Cos(body.Pose.AngleRadians), sine = Mathf.Sin(body.Pose.AngleRadians);
                    float px = (x + 0.5f) * _config.CellSize, py = (y + 0.5f) * _config.CellSize;
                    Vector2 center = body.Pose.Position + new Vector2(cosine * px - sine * py, sine * px + cosine * py);
                    if (!region.ContainsCenter(center)) continue;
                }
                int key = x + y * owner.Width;
                if (!destination.ContainsKey(key))
                {
                    GridCell cell = owner.Read(x, y);
                    if (!cell.IsEmpty) destination.Add(key, cell);
                }
            }
        }

        private static void TryAddStructureNeighbour(MaterialGrid owner, GridCell source, int x, int y,
            HashSet<int> visited, Queue<int> queue)
        {
            if (!owner.Inside(x, y) || !IsSolid(owner, owner.Read(x, y).MaterialId) ||
                !SameConnectionGroup(owner, source.MaterialId, owner.Read(x, y).MaterialId)) return;
            int key = x + y * owner.Width;
            if (visited.Add(key)) queue.Enqueue(key);
        }

        private static bool SameConnectionGroup(MaterialGrid owner, ushort first, ushort second)
        {
            return first != 0 && second != 0 &&
                owner.Definitions[first].ConnectionGroup == owner.Definitions[second].ConnectionGroup;
        }

        private static int CountNonEmpty(Dictionary<int, GridCell> draft)
        {
            int count = 0;
            foreach (KeyValuePair<int, GridCell> item in draft)
                if (!item.Value.IsEmpty) count++;
            return count;
        }

        private int TotalMaterialCells()
        {
            int count = _grid.CellCount;
            for (int i = 0; i < Bodies.Count; i++) count += Bodies[i].Grid.CellCount;
            return count;
        }

        private bool ApplyProjectedCommand(MaterialGrid owner, in MaterialCommand command,
            int x0, int y0, int x1, int y1, Dictionary<int, GridCell> draft, BodyV2 body)
        {
            if (command.Operation == MaterialOperation.Spawn && body != null) return true;
            if (x0 > x1 || y0 > y1) return true;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                int key = x + y * owner.Width;
                GridCell current = draft.TryGetValue(key, out GridCell copied) ? copied : owner.Read(x, y);
                if (body != null)
                {
                    float cosine = Mathf.Cos(body.Pose.AngleRadians), sine = Mathf.Sin(body.Pose.AngleRadians);
                    float px = (x + 0.5f) * _config.CellSize, py = (y + 0.5f) * _config.CellSize;
                    Vector2 center = body.Pose.Position + new Vector2(cosine * px - sine * py, sine * px + cosine * py);
                    if (!command.Region.ContainsCenter(center) || current.IsEmpty) continue;
                }
                if (command.Operation == MaterialOperation.Spawn)
                {
                    if (!current.IsEmpty || !owner.Passable(x, y)) return false;
                    draft[key] = owner.CreateCell(command.MaterialId);
                }
                else if (command.Operation == MaterialOperation.Remove)
                {
                    if (!current.IsEmpty) draft[key] = default;
                }
                else if (command.Operation == MaterialOperation.Replace)
                {
                    if (current.IsEmpty) continue;
                    draft[key] = owner.CreateCell(command.MaterialId);
                }
            }
            return true;
        }

        private static void CountProjectedComponents(MaterialGrid owner, Dictionary<int, GridCell> draft,
            out int bodies, out int shapes, out int maxShapes, out int staticShapes)
        {
            bodies = shapes = maxShapes = staticShapes = 0;
            var visited = new HashSet<int>();
            var component = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (KeyValuePair<int, GridCell> item in draft)
            {
                if (!IsSolid(owner, item.Value.MaterialId) || !visited.Add(item.Key)) continue;
                component.Clear(); queue.Enqueue(item.Key); bool fixedComponent = false;
                while (queue.Count != 0)
                {
                    int key = queue.Dequeue(); component.Add(key);
                    GridCell cell = draft[key]; fixedComponent |= cell.IsFixed;
                    int x = key % owner.Width, y = key / owner.Width;
                    TryQueueProjected(owner, draft, cell, x - 1, y, visited, queue);
                    TryQueueProjected(owner, draft, cell, x + 1, y, visited, queue);
                    TryQueueProjected(owner, draft, cell, x, y - 1, visited, queue);
                    TryQueueProjected(owner, draft, cell, x, y + 1, visited, queue);
                }
                int componentShapes = 0;
                foreach (int key in component)
                {
                    int x = key % owner.Width, y = key / owner.Width;
                    if ((x & 31) == 0 || !component.Contains(key - 1)) componentShapes++;
                }
                if (fixedComponent) staticShapes += componentShapes;
                else
                {
                    bodies++;
                    shapes += componentShapes;
                    maxShapes = Math.Max(maxShapes, componentShapes);
                }
            }
        }

        private static void TryQueueProjected(MaterialGrid owner, Dictionary<int, GridCell> draft,
            GridCell source, int x, int y, HashSet<int> visited, Queue<int> queue)
        {
            if (!owner.Inside(x, y)) return;
            int key = x + y * owner.Width;
            if (draft.TryGetValue(key, out GridCell cell) && IsSolid(owner, cell.MaterialId) &&
                SameConnectionGroup(owner, source.MaterialId, cell.MaterialId) && visited.Add(key)) queue.Enqueue(key);
        }

        private int CollectMainGridTargets(in CellGeometry source, ulong generation,
            NativeList<CellContact> destination, int written, int budget)
        {
            GetWorldAabb(source, out float minX, out float minY, out float maxX, out float maxY);
            GetGridRange(minX, minY, maxX, maxY, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1 && written < budget; y++) for (int x = x0; x <= x1 && written < budget; x++)
            {
                GridCell cell = _grid.Read(x, y);
                if (!IsIgnitionTarget(_grid, cell)) continue;
                CellKey key = new CellKey(generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
                CellGeometry target = new CellGeometry(key, new BodyPose(_origin, 0), new Vector2Int(x, y), _config.CellSize);
                CellContact contact = ExactCellGeometry.Contact(source, target, 0);
                if (AcceptIgnitionContact(contact) && TryAddIgnitionContact(contact, destination)) written++;
            }
            return written;
        }

        private int CollectBodyTargets(in CellGeometry source, ulong generation, BodyRuntime runtime,
            NativeList<CellContact> destination, int written, int budget)
        {
            GetWorldAabb(source, out float sourceMinX, out float sourceMinY, out float sourceMaxX, out float sourceMaxY);
            GetBodyWorldAabb(runtime.Body, out float bodyMinX, out float bodyMinY, out float bodyMaxX, out float bodyMaxY);
            if (sourceMaxX < bodyMinX || sourceMinX > bodyMaxX || sourceMaxY < bodyMinY || sourceMinY > bodyMaxY)
                return written;
            GetBodyLocalRange(runtime.Body, source, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1 && written < budget; y++) for (int x = x0; x <= x1 && written < budget; x++)
            {
                GridCell cell = runtime.Body.Grid.Read(x, y);
                if (!IsIgnitionTarget(runtime.Body.Grid, cell)) continue;
                CellKey key = new CellKey(generation, new CellPositionKey(OwnerKind.Body, runtime.Body.Id, x, y));
                CellGeometry target = new CellGeometry(key, runtime.Body.Pose, new Vector2Int(x, y), _config.CellSize);
                CellContact contact = ExactCellGeometry.Contact(source, target, 0);
                if (AcceptIgnitionContact(contact) && TryAddIgnitionContact(contact, destination)) written++;
            }
            return written;
        }

        private void GetBodyWorldAabb(BodyV2 body, out float minX, out float minY, out float maxX, out float maxY)
        {
            Vector2 first = body.Pose.Position;
            minX = maxX = first.x; minY = maxY = first.y;
            AddWorldBounds(Transform(body.Pose, new Vector2(body.Grid.Width * _config.CellSize, 0)),
                ref minX, ref minY, ref maxX, ref maxY);
            AddWorldBounds(Transform(body.Pose, new Vector2(body.Grid.Width * _config.CellSize, body.Grid.Height * _config.CellSize)),
                ref minX, ref minY, ref maxX, ref maxY);
            AddWorldBounds(Transform(body.Pose, new Vector2(0, body.Grid.Height * _config.CellSize)),
                ref minX, ref minY, ref maxX, ref maxY);
        }

        private bool TryGetSpatialQueryRect(Vector2 min, Vector2 max,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            float worldMinX = _origin.x;
            float worldMinY = _origin.y;
            float worldMaxX = worldMinX + _grid.Width * _config.CellSize;
            float worldMaxY = worldMinY + _grid.Height * _config.CellSize;
            float left = Mathf.Min(min.x, max.x), right = Mathf.Max(min.x, max.x);
            float bottom = Mathf.Min(min.y, max.y), top = Mathf.Max(min.y, max.y);
            if (right < worldMinX || left > worldMaxX || top < worldMinY || bottom > worldMaxY)
            {
                minX = minY = maxX = maxY = 0;
                return false;
            }
            left = Mathf.Clamp(left, worldMinX, worldMaxX);
            right = Mathf.Clamp(right, worldMinX, worldMaxX);
            bottom = Mathf.Clamp(bottom, worldMinY, worldMaxY);
            top = Mathf.Clamp(top, worldMinY, worldMaxY);
            double invCell = 1d / _config.CellSize;
            long x0 = (long)Math.Floor((left - worldMinX) * invCell) - 1L;
            long x1 = (long)Math.Ceiling((right - worldMinX) * invCell) + 1L;
            long y0 = (long)Math.Floor((bottom - worldMinY) * invCell) - 1L;
            long y1 = (long)Math.Ceiling((top - worldMinY) * invCell) + 1L;
            minX = (int)Math.Max(-1L, Math.Min(_grid.Width + 1L, x0));
            maxX = (int)Math.Max(-1L, Math.Min(_grid.Width + 1L, x1));
            minY = (int)Math.Max(-1L, Math.Min(_grid.Height + 1L, y0));
            maxY = (int)Math.Max(-1L, Math.Min(_grid.Height + 1L, y1));
            if (maxX <= minX) maxX = Math.Min(_grid.Width + 1, minX + 1);
            if (maxY <= minY) maxY = Math.Min(_grid.Height + 1, minY + 1);
            return maxX > minX && maxY > minY;
        }

        private bool TryGetBodySpatialAabb(BodyV2 body,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            GetBodyWorldAabb(body, out float worldMinX, out float worldMinY,
                out float worldMaxX, out float worldMaxY);
            float worldLeft = _origin.x;
            float worldBottom = _origin.y;
            float worldRight = worldLeft + _grid.Width * _config.CellSize;
            float worldTop = worldBottom + _grid.Height * _config.CellSize;
            if (worldMaxX < worldLeft || worldMinX > worldRight ||
                worldMaxY < worldBottom || worldMinY > worldTop)
            {
                minX = minY = maxX = maxY = 0;
                return false;
            }
            double invCell = 1d / _config.CellSize;
            long x0 = (long)Math.Floor((worldMinX - worldLeft) * invCell) - 1L;
            long x1 = (long)Math.Ceiling((worldMaxX - worldLeft) * invCell) + 1L;
            long y0 = (long)Math.Floor((worldMinY - worldBottom) * invCell) - 1L;
            long y1 = (long)Math.Ceiling((worldMaxY - worldBottom) * invCell) + 1L;
            minX = (int)Math.Max(-1L, Math.Min(_grid.Width + 1L, x0));
            maxX = (int)Math.Max(-1L, Math.Min(_grid.Width + 1L, x1));
            minY = (int)Math.Max(-1L, Math.Min(_grid.Height + 1L, y0));
            maxY = (int)Math.Max(-1L, Math.Min(_grid.Height + 1L, y1));
            if (maxX <= minX) maxX = Math.Min(_grid.Width + 1, minX + 1);
            if (maxY <= minY) maxY = Math.Min(_grid.Height + 1, minY + 1);
            return maxX > minX && maxY > minY;
        }

        private void ReserveBodySpatialIndex(int minX, int minY, int maxX, int maxY)
        {
            long bucketWidth = ((long)maxX - minX + NativeBodySpatialIndex.BucketSize - 1) /
                NativeBodySpatialIndex.BucketSize;
            long bucketHeight = ((long)maxY - minY + NativeBodySpatialIndex.BucketSize - 1) /
                NativeBodySpatialIndex.BucketSize;
            long bodyBuckets = checked(bucketWidth * bucketHeight);
            long requestedBuckets = checked((long)_bodySpatialIndex.BucketEntryCount + bodyBuckets +
                Math.Max(4, Bodies.Count));
            if (requestedBuckets > int.MaxValue)
                throw new InvalidOperationException("刚体空间索引范围超过整数容量。");
            int bodyCapacity = Math.Max(1, Math.Max(Bodies.Count, _bodySpatialIndex.BodyCapacity));
            int queryCapacity = Math.Max(bodyCapacity, _bodySpatialIndex.QueryCapacity);
            _bodySpatialIndex.Reserve(bodyCapacity, Math.Max(1, (int)requestedBuckets), queryCapacity);
            if (_bodyCandidateIds.Capacity < bodyCapacity) _bodyCandidateIds.Capacity = bodyCapacity;
        }

        private void RefreshBodySpatialIndex(BodyRuntime runtime, bool force = false)
        {
            if (!TryGetBodySpatialAabb(runtime.Body, out int minX, out int minY, out int maxX, out int maxY))
            {
                if (runtime.SpatialIndexed) _bodySpatialIndex.Remove(runtime.Body.Id);
                runtime.SpatialIndexed = false;
                return;
            }
            if (!force && runtime.SpatialIndexed && runtime.SpatialMinX == minX &&
                runtime.SpatialMinY == minY && runtime.SpatialMaxX == maxX && runtime.SpatialMaxY == maxY)
                return;
            ReserveBodySpatialIndex(minX, minY, maxX, maxY);
            _bodySpatialIndex.UpdateAabb(runtime.Body.Id, minX, minY, maxX, maxY);
            runtime.SpatialMinX = minX;
            runtime.SpatialMinY = minY;
            runtime.SpatialMaxX = maxX;
            runtime.SpatialMaxY = maxY;
            runtime.SpatialIndexed = true;
        }

        private void RemoveBodySpatialIndex(BodyV2 body)
        {
            _bodySpatialIndex.Remove(body.Id);
            _runtimeById.Remove(body.Id);
            _bodiesById.Remove(body.Id);
            if (body.Grid.GridHandle != 0) _bodiesByGridHandle.Remove(body.Grid.GridHandle);
        }

        private void RebuildBodyLookups()
        {
            _bodiesById.Clear();
            _bodiesByGridHandle.Clear();
            for (int i = 0; i < Bodies.Count; i++)
            {
                BodyV2 body = Bodies[i];
                if (body == null || body.Id == 0 || body.Grid == null)
                    throw new InvalidOperationException("动态体目录包含无效主体。");
                if (_bodiesById.ContainsKey(body.Id))
                    throw new InvalidOperationException("BodyV2 ID 必须唯一。");
                _bodiesById.Add(body.Id, body);
                if (body.Grid.GridHandle != 0) _bodiesByGridHandle[body.Grid.GridHandle] = body;
            }
        }

        private static Vector2 Transform(BodyPose pose, Vector2 localCells)
        {
            float c = Mathf.Cos(pose.AngleRadians), s = Mathf.Sin(pose.AngleRadians);
            return pose.Position + new Vector2(c * localCells.x - s * localCells.y, s * localCells.x + c * localCells.y);
        }

        private static void AddWorldBounds(Vector2 value, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            minX = Mathf.Min(minX, value.x); minY = Mathf.Min(minY, value.y);
            maxX = Mathf.Max(maxX, value.x); maxY = Mathf.Max(maxY, value.y);
        }

        private bool TryAddIgnitionContact(in CellContact contact, NativeList<CellContact> destination)
        {
            ulong key = MixIgnitionKey(contact.Second);
            if (!_ignitionKeys.Add(key)) return false;
            destination.Add(contact);
            return true;
        }

        private static bool AcceptIgnitionContact(in CellContact contact) =>
            contact.Feature == ContactFeature.PositiveAreaOverlap ||
            contact.Feature == ContactFeature.EdgeEdge || contact.Feature == ContactFeature.VertexEdge;

        private static bool IsIgnitionTarget(MaterialGrid grid, in GridCell cell)
        {
            return cell.MaterialId != 0 && !cell.IsBurning && grid.Definitions[cell.MaterialId].IsBurnable;
        }

        private CellGeometry MainGeometry(in CellKey key) =>
            new CellGeometry(key, new BodyPose(_origin, 0), new Vector2Int(key.Position.X, key.Position.Y), _config.CellSize);

        private CellGeometry BodyGeometry(in CellKey key, BodyV2 body) =>
            new CellGeometry(key, body.Pose, new Vector2Int(key.Position.X, key.Position.Y), _config.CellSize);

        private void GetBodyLocalRange(BodyV2 body, in CellGeometry worldCell,
            out int x0, out int y0, out int x1, out int y1)
        {
            GetWorldAabb(worldCell, out float minX, out float minY, out float maxX, out float maxY);
            float localMinX = float.PositiveInfinity, localMinY = float.PositiveInfinity;
            float localMaxX = float.NegativeInfinity, localMaxY = float.NegativeInfinity;
            UpdateLocalBounds(body.Pose, new Vector2(minX, minY), ref localMinX, ref localMinY, ref localMaxX, ref localMaxY);
            UpdateLocalBounds(body.Pose, new Vector2(minX, maxY), ref localMinX, ref localMinY, ref localMaxX, ref localMaxY);
            UpdateLocalBounds(body.Pose, new Vector2(maxX, minY), ref localMinX, ref localMinY, ref localMaxX, ref localMaxY);
            UpdateLocalBounds(body.Pose, new Vector2(maxX, maxY), ref localMinX, ref localMinY, ref localMaxX, ref localMaxY);
            x0 = Mathf.Clamp(Mathf.FloorToInt(localMinX / _config.CellSize) - 1, 0, body.Grid.Width - 1);
            y0 = Mathf.Clamp(Mathf.FloorToInt(localMinY / _config.CellSize) - 1, 0, body.Grid.Height - 1);
            x1 = Mathf.Clamp(Mathf.CeilToInt(localMaxX / _config.CellSize) + 1, 0, body.Grid.Width - 1);
            y1 = Mathf.Clamp(Mathf.CeilToInt(localMaxY / _config.CellSize) + 1, 0, body.Grid.Height - 1);
        }

        private static void UpdateLocalBounds(BodyPose pose, Vector2 world, ref float minX, ref float minY,
            ref float maxX, ref float maxY)
        {
            Vector2 local = Inverse(pose, world);
            minX = Mathf.Min(minX, local.x); minY = Mathf.Min(minY, local.y);
            maxX = Mathf.Max(maxX, local.x); maxY = Mathf.Max(maxY, local.y);
        }

        private void GetGridRange(float minX, float minY, float maxX, float maxY,
            out int x0, out int y0, out int x1, out int y1)
        {
            x0 = Mathf.Clamp(Mathf.FloorToInt((minX - _origin.x) / _config.CellSize) - 1, 0, _grid.Width - 1);
            y0 = Mathf.Clamp(Mathf.FloorToInt((minY - _origin.y) / _config.CellSize) - 1, 0, _grid.Height - 1);
            x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - _origin.x) / _config.CellSize) + 1, 0, _grid.Width - 1);
            y1 = Mathf.Clamp(Mathf.CeilToInt((maxY - _origin.y) / _config.CellSize) + 1, 0, _grid.Height - 1);
        }

        private static void GetWorldAabb(in CellGeometry cell, out float minX, out float minY, out float maxX, out float maxY)
        {
            Vector2 first = ExactCellGeometry.Corner(cell, 0);
            minX = maxX = first.x; minY = maxY = first.y;
            for (int i = 1; i < 4; i++)
            {
                Vector2 corner = ExactCellGeometry.Corner(cell, i);
                minX = Mathf.Min(minX, corner.x); minY = Mathf.Min(minY, corner.y);
                maxX = Mathf.Max(maxX, corner.x); maxY = Mathf.Max(maxY, corner.y);
            }
        }

        private static Vector2 Inverse(BodyPose pose, Vector2 world)
        {
            Vector2 delta = world - pose.Position;
            float c = Mathf.Cos(pose.AngleRadians), s = Mathf.Sin(pose.AngleRadians);
            return new Vector2(c * delta.x + s * delta.y, -s * delta.x + c * delta.y);
        }

        private static ulong MixIgnitionKey(in CellKey key)
        {
            unchecked
            {
                ulong value = (ulong)key.Position.OwnerKind * 0x9E3779B97F4A7C15UL + key.Position.BodyId;
                value ^= (uint)key.Position.X + 0x9E3779B9u + (value << 6) + (value >> 2);
                value ^= (uint)key.Position.Y + 0x9E3779B9u + (value << 6) + (value >> 2);
                return value;
            }
        }

        public void AddBody(BodyV2 body)
        {
            if (_disposed || body == null) throw new ObjectDisposedException(nameof(MaterialPhysics));
            if (body.Id == 0 || body.Grid == null) throw new ArgumentException("BodyV2 必须具有非零 ID 和局部网格。", nameof(body));
            foreach (BodyV2 existing in Bodies) if (existing.Id == body.Id) throw new InvalidOperationException("BodyV2 ID 必须唯一。");
            if (body.Id >= _nextBodyId) _nextBodyId = body.Id + 1;
            Bodies.Add(body);
            _bodiesById[body.Id] = body;
            if (_initialized)
            {
                BodyRuntime runtime = new BodyRuntime(body, CreateBodyObjects(body));
                _runtimeBodies.Add(runtime);
                _runtimeById[body.Id] = runtime;
                ReserveCoverageCapacity(runtime);
                RegisterBodyGridsForTimers();
                RefreshBodySpatialIndex(runtime, true);
            }
        }

        public void Initialize()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterialPhysics));
            if (_initialized) return;
            _scene = SceneManager.CreateScene("OpenOitaV2Physics-" + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics2D));
            _physics = _scene.GetPhysicsScene2D();
            _surface = new PhysicsMaterial2D("OpenOitaV2Surface") { friction = 0, bounciness = 0 };
            try
            {
                CreateBoundary(_origin + new Vector2(-_config.CellSize / 2, _config.Height * _config.CellSize / 2),
                    new Vector2(_config.CellSize, _config.Height * _config.CellSize + 2 * _config.CellSize));
                CreateBoundary(_origin + new Vector2(_config.Width * _config.CellSize + _config.CellSize / 2, _config.Height * _config.CellSize / 2),
                    new Vector2(_config.CellSize, _config.Height * _config.CellSize + 2 * _config.CellSize));
                CreateBoundary(_origin + new Vector2(_config.Width * _config.CellSize / 2, -_config.CellSize / 2),
                    new Vector2(_config.Width * _config.CellSize, _config.CellSize));
                CreateBoundary(_origin + new Vector2(_config.Width * _config.CellSize / 2, _config.Height * _config.CellSize + _config.CellSize / 2),
                    new Vector2(_config.Width * _config.CellSize, _config.CellSize));
                _fixedTerrain = new GameObject("OpenOitaV2FixedTerrain");
                SceneManager.MoveGameObjectToScene(_fixedTerrain, _scene);
                _fixedTerrain.transform.position = _origin;
                RebuildBodyLookups();
                _runtimeById.Clear();
                for (int i = 0; i < Bodies.Count; i++)
                {
                    BodyRuntime runtime = new BodyRuntime(Bodies[i], CreateBodyObjects(Bodies[i]));
                    _runtimeBodies.Add(runtime);
                    _runtimeById.Add(Bodies[i].Id, runtime);
                    ReserveCoverageCapacity(runtime);
                }
                RegisterBodyGridsForTimers();
                for (int i = 0; i < _runtimeBodies.Count; i++) RefreshBodySpatialIndex(_runtimeBodies[i], true);
                _initialized = true;
            }
            catch { Dispose(); throw; }
        }

        public void RebuildStructures(ulong tick)
        {
            EnsureInitialized();
            _dirtyTiles.Clear();
            _grid.TakeTopologyDirtyTiles(_dirtyTiles);
            bool topologyChanged = !_structuresBuilt || _dirtyTiles.Length != 0;
            bool full = !_structuresBuilt;
            if (!_structuresBuilt || _dirtyTiles.Length != 0)
            {
                if (!full)
                {
                    PrepareAffectedFixedRoots();
                    _connectivity.RebuildAffected(_dirtyTiles);
                }
                else _connectivity.RebuildAll();
                if (full) ExtractUnfixedComponents(null);
                else ExtractUnfixedComponents(_connectivity.AffectedTiles);
                RebuildLogicalFixedTerrain(full, _connectivity.AffectedTiles);
                WakeBodiesForFixedChanges(_connectivity.AffectedTiles);
                _structuresBuilt = true;
            }
            RegisterBodyGridsForTimers();
            for (int i = 0; i < _runtimeBodies.Count; i++)
            {
                BodyRuntime body = _runtimeBodies[i];
                _dirtyTiles.Clear();
                body.Body.Grid.TakeTopologyDirtyTiles(_dirtyTiles);
                bool geometryDirty = _dirtyTiles.Length != 0;
                if (geometryDirty && SplitDynamicBody(body.Body)) { i--; continue; }
                if (geometryDirty) RemoveBodyCoverage(body.Body);
                if (!body.HasColliders) RebuildBodyColliders(body);
                else if (geometryDirty) RebuildBodyAffectedColliders(body, _dirtyTiles.AsArray());
            }
            RetireEmptyBodies();
            for (int i = 0; i < _runtimeBodies.Count; i++) RefreshBodySpatialIndex(_runtimeBodies[i]);
            // Unity terrain colliders are focused after extraction and body updates.  Pose-only
            // ticks keep existing tile geometry and only change the active focus set.
            RefreshFixedTerrainFocus(topologyChanged ? _connectivity.AffectedTiles : default);
            int totalShapes = _logicalFixedShapeCount;
            for (int i = 0; i < _runtimeBodies.Count; i++) totalShapes += _runtimeBodies[i].Body.ShapeCount;
            if (totalShapes > _config.Limits.MaxTotalShapes)
                throw new InvalidOperationException("材料物理形状超过总预算。");
        }

        private void RetireEmptyBodies()
        {
            for (int i = _runtimeBodies.Count - 1; i >= 0; i--)
            {
                BodyRuntime runtime = _runtimeBodies[i];
                BodyV2 body = runtime.Body;
                if (body.Grid.CellCount != 0) continue;
                RemoveBodyCoverage(body);
                RemoveBodySpatialIndex(body);
                Release(runtime.GameObject);
                runtime.Dispose();
                _runtimeBodies.RemoveAt(i);
                Bodies.Remove(body);
                bool extracted = _extractedBodyIds.Remove(body.Id);
                if (extracted)
                {
                    ClearGridTimers(body.Grid);
                    body.Grid.Dispose();
                }
            }
        }

        /// <summary>
        /// Extracts every connected structure group that has no fixed cell. Components that
        /// touch a fixed cell stay in the root grid, including mixed fixed/free groups.
        /// </summary>
        private void ExtractUnfixedComponents(NativeArray<int>? affectedTiles)
        {
            if (!affectedTiles.HasValue) _fixedRoots.Clear();
            if (!affectedTiles.HasValue) _connectivity.GetComponentRoots(_componentRoots);
            else _connectivity.GetRootsInTiles(affectedTiles.Value, _componentRoots);
            HashSet<int> visited = new HashSet<int>();
            for (int i = 0; i < _componentRoots.Length; i++)
            {
                int root = _componentRoots[i];
                if (!visited.Add(root)) continue;
                _connectivity.GetConnectedRoots(root, _groupRoots);
                for (int g = 0; g < _groupRoots.Length; g++) visited.Add(_groupRoots[g]);
                _connectivity.GetCells(_groupRoots, _componentCells);
                if (_componentCells.Length == 0) continue;

                bool hasFixed = false;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                for (int c = 0; c < _componentCells.Length; c++)
                {
                    int key = _componentCells[c];
                    int x = key % _grid.Width, y = key / _grid.Width;
                    GridCell cell = _grid.Read(x, y);
                    hasFixed |= cell.IsFixed;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
                if (hasFixed)
                {
                    for (int g = 0; g < _groupRoots.Length; g++) _fixedRoots.Add(_groupRoots[g]);
                    continue;
                }

                int width = maxX - minX + 1, height = maxY - minY + 1;
                MaterialGrid child = new MaterialGrid(width, height, _grid.Definitions, _grid.ColdStore, _grid.PagePool)
                {
                    GridHandle = _nextGridHandle++
                };
                for (int c = 0; c < _componentCells.Length; c++)
                {
                    int key = _componentCells[c];
                    int x = key % _grid.Width, y = key / _grid.Width;
                    GridCell moved = _grid.Take(x, y);
                    child.Write(x - minX, y - minY, moved);
                }
                child.TakeTopologyDirtyTiles(_dirtyTiles);
                var pose = new BodyPose(_origin + new Vector2(minX * _config.CellSize, minY * _config.CellSize), 0);
                var body = new BodyV2(_nextBodyId++, child, pose, new BodyMotion(Vector2.zero, 0));
                _extractedBodyIds.Add(body.Id);
                AddBody(body);
            }
        }

        private void RemoveExtractedBodies()
        {
            for (int i = Bodies.Count - 1; i >= 0; i--)
            {
                BodyV2 body = Bodies[i];
                if (!_extractedBodyIds.Contains(body.Id)) continue;
                int runtimeIndex = FindRuntime(body);
                if (runtimeIndex >= 0)
                {
                    RemoveBodyCoverage(body);
                    RemoveBodySpatialIndex(body);
                    Release(_runtimeBodies[runtimeIndex].GameObject);
                    _runtimeBodies[runtimeIndex].Dispose();
                    _runtimeBodies.RemoveAt(runtimeIndex);
                }
                else RemoveBodySpatialIndex(body);
                ClearGridTimers(body.Grid);
                body.Grid.Dispose();
                Bodies.RemoveAt(i);
            }
            _extractedBodyIds.Clear();
        }

        private void PrepareAffectedFixedRoots()
        {
            _queryTiles.Clear();
            for (int i = 0; i < _dirtyTiles.Length; i++)
            {
                int tile = _dirtyTiles[i];
                AddQueryTile(tile);
                int tx = tile % _grid.TileColumns, ty = tile / _grid.TileColumns;
                if (tx > 0) AddQueryTile(tile - 1);
                if (tx + 1 < _grid.TileColumns) AddQueryTile(tile + 1);
                if (ty > 0) AddQueryTile(tile - _grid.TileColumns);
                if ((ty + 1) * MaterialConnectivity.TileSize < _grid.Height) AddQueryTile(tile + _grid.TileColumns);
            }
            _connectivity.GetRootsInTiles(_queryTiles.AsArray(), _componentRoots);
            for (int i = 0; i < _componentRoots.Length; i++)
            {
                _connectivity.GetConnectedRoots(_componentRoots[i], _groupRoots);
                for (int g = 0; g < _groupRoots.Length; g++) _fixedRoots.Remove(_groupRoots[g]);
            }
        }

        private void AddQueryTile(int tile)
        {
            for (int i = 0; i < _queryTiles.Length; i++) if (_queryTiles[i] == tile) return;
            _queryTiles.Add(tile);
        }

        private bool SplitDynamicBody(BodyV2 body)
        {
            int runtimeIndex = FindRuntime(body);
            if (runtimeIndex < 0) return false;
            _splitVisitedRoots.Clear();
            _splitReplacements.Clear();
            BodyRuntime runtime = _runtimeBodies[runtimeIndex];
            MaterialConnectivity connectivity = runtime.Connectivity;
            if (!runtime.ConnectivityBuilt)
            {
                // The first dirty observation establishes the persistent graph.  Later
                // edits only rebuild the dirty tiles plus their boundary neighbours.
                connectivity.RebuildAll();
                runtime.ConnectivityBuilt = true;
            }
            else
            {
                connectivity.RebuildAffected(_dirtyTiles);
            }

            // ComponentCount is global across tile-local labels and the boundary graph.
            // Looking at the number of local roots is incorrect for a single component
            // crossing a 32-cell tile boundary: that was the material-loss bug fixed here.
            if (connectivity.ComponentCount <= 1) return false;

            bool adopted = false;
            try
            {
                connectivity.GetComponentRoots(_componentRoots);
                for (int i = 0; i < _componentRoots.Length; i++)
                {
                    int root = _componentRoots[i];
                    if (!_splitVisitedRoots.Add(root)) continue;
                    connectivity.GetConnectedRoots(root, _groupRoots);
                    for (int g = 0; g < _groupRoots.Length; g++) _splitVisitedRoots.Add(_groupRoots[g]);
                    connectivity.GetCells(_groupRoots, _componentCells);
                    if (_componentCells.Length == 0) continue;
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                    for (int c = 0; c < _componentCells.Length; c++)
                    {
                        int key = _componentCells[c], x = key % body.Grid.Width, y = key / body.Grid.Width;
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                    MaterialGrid child = new MaterialGrid(maxX - minX + 1, maxY - minY + 1,
                        body.Grid.Definitions, body.Grid.ColdStore, _grid.PagePool) { GridHandle = _nextGridHandle++ };
                    bool registered = false;
                    try
                    {
                        for (int c = 0; c < _componentCells.Length; c++)
                        {
                            int key = _componentCells[c], x = key % body.Grid.Width, y = key / body.Grid.Width;
                            child.Write(x - minX, y - minY, body.Grid.Take(x, y));
                        }
                        child.TakeTopologyDirtyTiles(_dirtyTiles);
                        float angle = body.Pose.AngleRadians, cs = Mathf.Cos(angle), sn = Mathf.Sin(angle);
                        Vector2 offset = new Vector2(minX * _config.CellSize, minY * _config.CellSize);
                        Vector2 worldOffset = new Vector2(cs * offset.x - sn * offset.y, sn * offset.x + cs * offset.y);
                        ulong id = _splitReplacements.Count == 0 ? body.Id : _nextBodyId++;
                        _splitReplacements.Add(new BodyV2(id, child,
                            new BodyPose(body.Pose.Position + worldOffset, angle), body.Motion));
                        registered = true;
                    }
                    finally
                    {
                        if (!registered) child.Dispose();
                    }
                }
                if (_splitReplacements.Count <= 1) return false;

                RemoveBodyCoverage(body);
                RemoveBodySpatialIndex(body);
                Release(runtime.GameObject);
                runtime.Dispose();
                _runtimeBodies.RemoveAt(runtimeIndex);
                Bodies.Remove(body);
                _extractedBodyIds.Remove(body.Id);
                body.Grid.Dispose();
                for (int i = 0; i < _splitReplacements.Count; i++)
                {
                    _extractedBodyIds.Add(_splitReplacements[i].Id);
                    AddBody(_splitReplacements[i]);
                }
                adopted = true;
                return true;
            }
            finally
            {
                if (!adopted)
                {
                    for (int i = 0; i < _splitReplacements.Count; i++)
                        if (FindRuntime(_splitReplacements[i]) < 0) _splitReplacements[i].Grid.Dispose();
                }
                _splitReplacements.Clear();
                _splitVisitedRoots.Clear();
            }
        }

        private int FindRuntime(BodyV2 body)
        {
            for (int i = 0; i < _runtimeBodies.Count; i++) if (ReferenceEquals(_runtimeBodies[i].Body, body)) return i;
            return -1;
        }

        private static void ClearGridTimers(MaterialGrid grid)
        {
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                    if (!grid.Read(x, y).IsEmpty) grid.Write(x, y, default);
        }

        private void RebuildLogicalFixedTerrain(bool full, NativeArray<int> affectedTiles)
        {
            if (full)
            {
                _logicalFixedShapeCount = 0;
                _logicalFixedTileShapeCounts.Clear();
                for (int i = 0; i < _grid.AllocatedTileCount; i++)
                {
                    int tileId = _grid.GetAllocatedTileId(i);
                    int count = CountLogicalFixedTile(tileId);
                    _logicalFixedTileShapeCounts[tileId] = count;
                    _logicalFixedShapeCount += count;
                }
            }
            else
            {
                for (int i = 0; i < affectedTiles.Length; i++)
                {
                    int tileId = affectedTiles[i];
                    _logicalFixedTileShapeCounts.TryGetValue(tileId, out int previous);
                    int current = CountLogicalFixedTile(tileId);
                    _logicalFixedTileShapeCounts[tileId] = current;
                    _logicalFixedShapeCount += current - previous;
                }
            }
            if (_logicalFixedShapeCount > _config.Limits.MaxTotalShapes)
                throw new InvalidOperationException("固定材料逻辑形状超过总预算。");
        }

        private int CountLogicalFixedTile(int tileId)
        {
            if (tileId < 0 || tileId >= _grid.TileColumns * _grid.TileRows || !_grid.Tiles[tileId].IsCreated)
                return 0;
            int tileX = tileId % _grid.TileColumns, tileY = tileId / _grid.TileColumns;
            int minX = tileX * MaterialConnectivity.TileSize, minY = tileY * MaterialConnectivity.TileSize;
            int width = Math.Min(MaterialConnectivity.TileSize, _grid.Width - minX);
            int height = Math.Min(MaterialConnectivity.TileSize, _grid.Height - minY);
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                bool inRun = false;
                for (int x = 0; x < width; x++)
                {
                    bool solid = IsFixedSolid(minX + x, minY + y);
                    if (solid && !inRun) { count++; inRun = true; }
                    else if (!solid) inRun = false;
                }
            }
            return count;
        }

        private void RefreshFixedTerrainFocus(NativeArray<int> dirtyTiles)
        {
            if (_fixedTerrain == null) return;
            _fixedDirtyTiles.Clear();
            if (dirtyTiles.IsCreated)
                for (int i = 0; i < dirtyTiles.Length; i++) _fixedDirtyTiles.Add(dirtyTiles[i]);

            _desiredFixedTiles.Clear();
            if (_runtimeBodies.Count == 0)
            {
                _fixedTilesToDisable.Clear();
                foreach (int tileId in _activeFixedTiles) _fixedTilesToDisable.Add(tileId);
                for (int i = 0; i < _fixedTilesToDisable.Count; i++) DisableFixedTile(_fixedTilesToDisable[i]);
                _fixedTerrain.SetActive(false);
                return;
            }

            _fixedTerrain.SetActive(true);
            for (int i = 0; i < _runtimeBodies.Count; i++) AddBodyFocusTiles(_runtimeBodies[i].Body);

            _fixedTilesToDisable.Clear();
            foreach (int tileId in _activeFixedTiles)
                if (!_desiredFixedTiles.Contains(tileId)) _fixedTilesToDisable.Add(tileId);
            for (int i = 0; i < _fixedTilesToDisable.Count; i++) DisableFixedTile(_fixedTilesToDisable[i]);

            foreach (int tileId in _desiredFixedTiles)
            {
                if (!_logicalFixedTileShapeCounts.TryGetValue(tileId, out int logicalCount) || logicalCount == 0)
                {
                    if (_activeFixedTiles.Contains(tileId)) DisableFixedTile(tileId);
                    continue;
                }
                bool wasActive = _activeFixedTiles.Contains(tileId);
                if (!wasActive) _activeFixedTiles.Add(tileId);
                if (!wasActive || _fixedDirtyTiles.Contains(tileId)) RebuildFixedTile(tileId);
            }
            if (_fixedShapeCount > _config.Limits.MaxTotalShapes)
                throw new InvalidOperationException("当前活动固定桥接形状超过总预算。");
        }

        private void AddBodyFocusTiles(BodyV2 body)
        {
            GetBodyBoundsAtPose(body, body.Pose, out int minX, out int minY, out int maxX, out int maxY);
            float seconds = _config.StepSeconds;
            Vector2 nextPosition = body.Pose.Position + body.Motion.LinearVelocity * seconds;
            nextPosition.y += _config.GravityY * seconds * seconds;
            float nextAngle = body.Pose.AngleRadians + body.Motion.AngularVelocityRadians * seconds;
            GetBodyBoundsAtPose(body, new BodyPose(nextPosition, nextAngle),
                out int nextMinX, out int nextMinY, out int nextMaxX, out int nextMaxY);
            minX = Math.Min(minX, nextMinX); minY = Math.Min(minY, nextMinY);
            maxX = Math.Max(maxX, nextMaxX); maxY = Math.Max(maxY, nextMaxY);
            minX = Mathf.Clamp(minX, 0, _grid.Width - 1); minY = Mathf.Clamp(minY, 0, _grid.Height - 1);
            maxX = Mathf.Clamp(maxX, 0, _grid.Width - 1); maxY = Mathf.Clamp(maxY, 0, _grid.Height - 1);
            if (minX > maxX || minY > maxY) return;
            int minTileX = Mathf.Clamp(Mathf.FloorToInt(minX / (float)MaterialConnectivity.TileSize) - 1, 0, _grid.TileColumns - 1);
            int minTileY = Mathf.Clamp(Mathf.FloorToInt(minY / (float)MaterialConnectivity.TileSize) - 1, 0, _grid.TileRows - 1);
            int maxTileX = Mathf.Clamp(Mathf.FloorToInt(maxX / (float)MaterialConnectivity.TileSize) + 1, 0, _grid.TileColumns - 1);
            int maxTileY = Mathf.Clamp(Mathf.FloorToInt(maxY / (float)MaterialConnectivity.TileSize) + 1, 0, _grid.TileRows - 1);
            for (int ty = minTileY; ty <= maxTileY; ty++)
                for (int tx = minTileX; tx <= maxTileX; tx++)
                    _desiredFixedTiles.Add(tx + ty * _grid.TileColumns);
        }

        private void DisableFixedTile(int tileId)
        {
            if (_fixedTileShapeCounts.TryGetValue(tileId, out int previous))
            {
                _fixedShapeCount -= previous;
                _fixedTileShapeCounts[tileId] = 0;
            }
            if (_fixedTileColliders.TryGetValue(tileId, out List<BoxCollider2D> pool))
                for (int i = 0; i < pool.Count; i++) pool[i].enabled = false;
            _activeFixedTiles.Remove(tileId);
        }

        private void GetBodyBoundsAtPose(BodyV2 body, in BodyPose pose,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            float radiusX = body.Grid.Width * _config.CellSize;
            float radiusY = body.Grid.Height * _config.CellSize;
            Vector2 center = pose.Position - _origin;
            float c = Mathf.Abs(Mathf.Cos(pose.AngleRadians)), s = Mathf.Abs(Mathf.Sin(pose.AngleRadians));
            float extentX = c * radiusX + s * radiusY, extentY = s * radiusX + c * radiusY;
            minX = Mathf.FloorToInt((center.x - extentX) / _config.CellSize) - 1;
            minY = Mathf.FloorToInt((center.y - extentY) / _config.CellSize) - 1;
            maxX = Mathf.FloorToInt((center.x + extentX) / _config.CellSize) + 1;
            maxY = Mathf.FloorToInt((center.y + extentY) / _config.CellSize) + 1;
        }

        private void RebuildFixedTile(int tileId)
        {
            if (tileId < 0 || tileId >= _grid.TileColumns * _grid.TileRows) return;
            if (!_fixedTileColliders.TryGetValue(tileId, out List<BoxCollider2D> pool))
            {
                pool = new List<BoxCollider2D>(16);
                _fixedTileColliders.Add(tileId, pool);
            }
            for (int i = 0; i < pool.Count; i++) pool[i].enabled = false;
            _fixedTileShapeCounts.TryGetValue(tileId, out int previous);
            _fixedShapeCount -= previous;
            int tileX = tileId % _grid.TileColumns, tileY = tileId / _grid.TileColumns;
            int minX = tileX * MaterialConnectivity.TileSize, minY = tileY * MaterialConnectivity.TileSize;
            int width = Math.Min(MaterialConnectivity.TileSize, _grid.Width - minX);
            int height = Math.Min(MaterialConnectivity.TileSize, _grid.Height - minY);
            if (width <= 0 || height <= 0) { _fixedTileShapeCounts[tileId] = 0; return; }
            bool[] solid = new bool[width * height];
            bool[] visited = new bool[solid.Length];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                solid[x + y * width] = IsFixedSolid(minX + x, minY + y);
            var rectangles = new List<FixedRectV2>(16);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int offset = x + y * width;
                if (!solid[offset] || visited[offset]) continue;
                int rectWidth = 0;
                while (x + rectWidth < width && solid[x + rectWidth + y * width] && !visited[x + rectWidth + y * width]) rectWidth++;
                int rectHeight = 1;
                while (y + rectHeight < height)
                {
                    bool row = true;
                    for (int rx = 0; rx < rectWidth; rx++)
                        if (!solid[x + rx + (y + rectHeight) * width] || visited[x + rx + (y + rectHeight) * width]) { row = false; break; }
                    if (!row) break;
                    rectHeight++;
                }
                for (int ry = 0; ry < rectHeight; ry++) for (int rx = 0; rx < rectWidth; rx++) visited[x + rx + (y + ry) * width] = true;
                rectangles.Add(new FixedRectV2(x, y, rectWidth, rectHeight));
            }
            for (int i = 0; i < rectangles.Count; i++)
            {
                if (i == pool.Count) pool.Add(_fixedTerrain.AddComponent<BoxCollider2D>());
                BoxCollider2D collider = pool[i];
                FixedRectV2 rectangle = rectangles[i];
                collider.enabled = true;
                collider.size = new Vector2(rectangle.Width * _config.CellSize, rectangle.Height * _config.CellSize);
                collider.offset = new Vector2((minX + rectangle.X + rectangle.Width * 0.5f) * _config.CellSize,
                    (minY + rectangle.Y + rectangle.Height * 0.5f) * _config.CellSize);
                collider.sharedMaterial = _surface;
            }
            _fixedTileShapeCounts[tileId] = rectangles.Count;
            _fixedShapeCount += rectangles.Count;
        }

        private bool IsFixedSolid(int x, int y)
        {
            GridCell cell = _grid.Read(x, y);
            if (cell.MaterialId == 0 || !IsSolid(_grid, cell.MaterialId)) return false;
            if (cell.IsFixed) return true;
            return _connectivity.TryGetComponent(x, y, out int root) && _fixedRoots.Contains(root);
        }

        private void ClampBodyVelocity(BodyRuntime runtime)
        {
            Rigidbody2D rb = runtime.Rigidbody;
            Vector2 velocity = rb.linearVelocity;
            float maxLinear = Math.Max(0, _config.Limits.MaxLinearSpeed);
            bool changed = false;
            if (velocity.sqrMagnitude > maxLinear * maxLinear && velocity.sqrMagnitude > 0)
            {
                velocity = velocity.normalized * maxLinear;
                changed = true;
            }
            float angularDegrees = _freezeRotation ? 0 : rb.angularVelocity;
            float maxAngularDegrees = Math.Max(0, _config.Limits.MaxAngularSpeedDegrees);
            if (Math.Abs(angularDegrees) > maxAngularDegrees)
            {
                angularDegrees = Mathf.Sign(angularDegrees) * maxAngularDegrees;
                changed = true;
            }
            if (_freezeRotation && rb.angularVelocity != 0) changed = true;
            if (changed)
            {
                rb.linearVelocity = velocity;
                rb.angularVelocity = angularDegrees;
            }
            runtime.Body.Motion = new BodyMotion(velocity, angularDegrees * Mathf.Deg2Rad);
        }

        private void WakeBodiesForFixedChanges(NativeArray<int> affectedTiles)
        {
            if (!affectedTiles.IsCreated || affectedTiles.Length == 0) return;
            for (int i = 0; i < _runtimeBodies.Count; i++)
            {
                BodyRuntime runtime = _runtimeBodies[i];
                if (!runtime.Rigidbody.IsSleeping()) continue;
                GetBodyBoundsAtPose(runtime.Body, runtime.Body.Pose,
                    out int minX, out int minY, out int maxX, out int maxY);
                float seconds = _config.StepSeconds;
                Vector2 nextPosition = runtime.Body.Pose.Position + runtime.Body.Motion.LinearVelocity * seconds;
                nextPosition.y += _config.GravityY * seconds * seconds;
                GetBodyBoundsAtPose(runtime.Body,
                    new BodyPose(nextPosition, runtime.Body.Pose.AngleRadians + runtime.Body.Motion.AngularVelocityRadians * seconds),
                    out int nextMinX, out int nextMinY, out int nextMaxX, out int nextMaxY);
                minX = Math.Min(minX, nextMinX); minY = Math.Min(minY, nextMinY);
                maxX = Math.Max(maxX, nextMaxX); maxY = Math.Max(maxY, nextMaxY);
                bool affected = false;
                for (int t = 0; t < affectedTiles.Length && !affected; t++)
                {
                    int tileId = affectedTiles[t];
                    if (tileId < 0 || tileId >= _grid.TileColumns * _grid.TileRows) continue;
                    RectInt tile = _grid.TileBounds(tileId);
                    affected = maxX >= tile.xMin && minX < tile.xMax && maxY >= tile.yMin && minY < tile.yMax;
                }
                if (affected) runtime.Rigidbody.WakeUp();
            }
        }

        public void Step(ulong tick)
        {
            EnsureInitialized();
            for (int i = 0; i < _runtimeBodies.Count; i++) ClampBodyVelocity(_runtimeBodies[i]);
            RebuildStructures(tick);
            // Clamp any host/imported velocity before planning the next substep as well as
            // after each solver substep.  This prevents a stale high velocity from making the
            // planner fault before the first simulation call.
            for (int i = 0; i < _runtimeBodies.Count; i++) ClampBodyVelocity(_runtimeBodies[i]);
            int substeps = CalculateSubsteps();
            float seconds = _config.StepSeconds / substeps;
            for (int substep = 0; substep < substeps; substep++)
            {
                for (int i = 0; i < _runtimeBodies.Count; i++)
                {
                    BodyRuntime body = _runtimeBodies[i];
                    Rigidbody2D rb = body.Rigidbody;
                    if (!rb.IsSleeping()) rb.AddForce(new Vector2(0, rb.mass * _config.GravityY), ForceMode2D.Force);
                }
                if (!_physics.Simulate(seconds)) throw new InvalidOperationException("独立 PhysicsScene2D 推进失败。");
                for (int i = 0; i < _runtimeBodies.Count; i++)
                {
                    BodyRuntime body = _runtimeBodies[i];
                    Rigidbody2D rb = body.Rigidbody;
                    ClampBodyVelocity(body);
                    body.Body.Pose = new BodyPose(rb.position, rb.rotation * Mathf.Deg2Rad);
                }
                UpdateDynamicCoverage();
                _inStepCapture = true;
                try { CollectWet(tick, false); }
                finally { _inStepCapture = false; }
            }
        }

        private int CalculateSubsteps()
        {
            double maximum = 0;
            double seconds = _config.StepSeconds;
            for (int i = 0; i < _runtimeBodies.Count; i++)
            {
                BodyV2 body = _runtimeBodies[i].Body;
                Vector2 velocity = body.Motion.LinearVelocity;
                double radius = 0.5 * Math.Sqrt(body.Grid.Width * body.Grid.Width + body.Grid.Height * body.Grid.Height) * _config.CellSize;
                double displacement = velocity.magnitude * seconds + Math.Abs(_config.GravityY) * seconds * seconds +
                    Math.Abs(body.Motion.AngularVelocityRadians) * radius * seconds;
                maximum = Math.Max(maximum, displacement);
            }
            double required = Math.Max(1, Math.Ceiling(maximum / (_config.CellSize * 0.5d)));
            if (required > _config.Limits.MaxPhysicsSubsteps)
                throw new InvalidOperationException("物理子步需求超过配置上限。");
            return (int)required;
        }

        private void UpdateDynamicCoverage()
        {
            for (int i = 0; i < _runtimeBodies.Count; i++)
            {
                BodyRuntime runtime = _runtimeBodies[i];
                BodyPose pose = runtime.Body.Pose;
                if (runtime.HasCoveragePose && runtime.CoveragePose.Position == pose.Position &&
                    runtime.CoveragePose.AngleRadians == pose.AngleRadians) continue;
                // The old and new swept coverage can differ by a whole body width. Wake
                // records by affected 32x32 pages, rather than by the pose origin only.
                if (runtime.HasCoveragePose) WakeSuspendedTiles(runtime.CoveredTiles);
                runtime.CoverageScratchCells.Clear();
                runtime.CoverageScratchCellSet.Clear();
                runtime.CoverageScratchTiles.Clear();
                runtime.CoverageScratchTileSet.Clear();
                GetBodyBounds(runtime.Body, out int minX, out int minY, out int maxX, out int maxY);
                UpdateBodyPoseCache(runtime);
                RefreshBodySpatialIndex(runtime);
                int clampedMinX = Math.Max(0, minX), clampedMaxX = Math.Min(_grid.Width - 1, maxX);
                int clampedMinY = Math.Max(0, minY), clampedMaxY = Math.Min(_grid.Height - 1, maxY);
                long coverageArea = (long)Math.Max(0, clampedMaxX - clampedMinX + 1) * Math.Max(0, clampedMaxY - clampedMinY + 1);
                if (coverageArea > 0)
                {
                    if (coverageArea > int.MaxValue) throw new InvalidOperationException("动态覆盖范围超过整数容量。");
                    CoverageCandidateVisits += coverageArea;
                    if (runtime.CoverageScratchCells.Capacity < (int)coverageArea)
                        throw new InvalidOperationException("动态覆盖候选容量未按最大旋转包围圆预留。");
                    var coverageJob = new BodyCoverageJob
                    {
                        SolidCells = runtime.SolidCells.AsReadOnly(), CoveredCells = runtime.CoverageScratchCells,
                        MainWidth = _grid.Width, MainHeight = _grid.Height,
                        BodyWidth = runtime.Body.Grid.Width, BodyHeight = runtime.Body.Grid.Height,
                        MinWorldX = minX, MaxWorldX = maxX, MinWorldY = minY, MaxWorldY = maxY,
                        CellSize = _config.CellSize, OriginX = _origin.x, OriginY = _origin.y,
                        BodyPositionX = runtime.PosePosition.x, BodyPositionY = runtime.PosePosition.y,
                        PoseCos = runtime.PoseCos, PoseSin = runtime.PoseSin
                    };
                    coverageJob.Run();
                }
                for (int c = 0; c < runtime.CoverageScratchCells.Length; c++)
                {
                    int linear = runtime.CoverageScratchCells[c];
                    if (!runtime.CoverageScratchCellSet.Add(linear)) continue;
                    int x = linear % _grid.Width, y = linear / _grid.Width;
                    int tileId = _grid.TileId(x, y);
                    if (runtime.CoverageScratchTileSet.Add(tileId)) runtime.CoverageScratchTiles.Add(tileId);
                }
                for (int old = 0; old < runtime.CoveredCells.Length; old++)
                {
                    int linear = runtime.CoveredCells[old];
                    if (runtime.CoverageScratchCellSet.Contains(linear)) continue;
                    if (!_coverageCounts.TryGetValue(linear, out int count)) continue;
                    if (count <= 1)
                    {
                        _coverageCounts.Remove(linear);
                        SetDynamicKey(linear, false);
                    }
                    else _coverageCounts[linear] = count - 1;
                }
                for (int current = 0; current < runtime.CoverageScratchCells.Length; current++)
                {
                    int linear = runtime.CoverageScratchCells[current];
                    if (runtime.CoveredCellSet.Contains(linear)) continue;
                    if (_coverageCounts.TryGetValue(linear, out int count)) _coverageCounts[linear] = count + 1;
                    else
                    {
                        if (!_coverageCounts.TryAdd(linear, 1)) throw new InvalidOperationException("动态覆盖位图超过4096预算。");
                        SetDynamicKey(linear, true);
                    }
                }

                NativeList<int> cells = runtime.CoveredCells;
                runtime.CoveredCells = runtime.CoverageScratchCells;
                runtime.CoverageScratchCells = cells;
                NativeParallelHashSet<int> cellSet = runtime.CoveredCellSet;
                runtime.CoveredCellSet = runtime.CoverageScratchCellSet;
                runtime.CoverageScratchCellSet = cellSet;
                NativeList<int> tiles = runtime.CoveredTiles;
                runtime.CoveredTiles = runtime.CoverageScratchTiles;
                runtime.CoverageScratchTiles = tiles;
                NativeParallelHashSet<int> tileSet = runtime.CoveredTileSet;
                runtime.CoveredTileSet = runtime.CoverageScratchTileSet;
                runtime.CoverageScratchTileSet = tileSet;
                runtime.CoveragePose = pose;
                runtime.HasCoveragePose = true;
                WakeSuspendedTiles(runtime.CoveredTiles);
            }
        }

        private void SetDynamicKey(int linear, bool occupied)
        {
            int x = linear % _grid.Width, y = linear / _grid.Width;
            _grid.SetDynamic(x, y, occupied);
        }

        [BurstCompile]
        private struct BodyCoverageJob : IJob
        {
            [ReadOnly] public NativeParallelHashMap<int, CachedSolidCell>.ReadOnly SolidCells;
            public NativeList<int> CoveredCells;
            public int MainWidth, MainHeight, BodyWidth, BodyHeight;
            public int MinWorldX, MaxWorldX, MinWorldY, MaxWorldY;
            public float CellSize, OriginX, OriginY;
            public float BodyPositionX, BodyPositionY;
            public double PoseCos, PoseSin;

            private static double Min4(float2 a0, float2 a1, float2 a2, float2 a3,
                double ax, double ay)
            {
                return Math.Min(Math.Min((double)a0.x * ax + a0.y * ay, (double)a1.x * ax + a1.y * ay),
                    Math.Min((double)a2.x * ax + a2.y * ay, (double)a3.x * ax + a3.y * ay));
            }

            private static double Max4(float2 a0, float2 a1, float2 a2, float2 a3,
                double ax, double ay)
            {
                return Math.Max(Math.Max((double)a0.x * ax + a0.y * ay, (double)a1.x * ax + a1.y * ay),
                    Math.Max((double)a2.x * ax + a2.y * ay, (double)a3.x * ax + a3.y * ay));
            }

            private static bool OverlapAxis(float2 a0, float2 a1, float2 a2, float2 a3,
                float2 b0, float2 b1, float2 b2, float2 b3, double axisX, double axisY)
            {
                double amin = Min4(a0, a1, a2, a3, axisX, axisY);
                double amax = Max4(a0, a1, a2, a3, axisX, axisY);
                double bmin = Min4(b0, b1, b2, b3, axisX, axisY);
                double bmax = Max4(b0, b1, b2, b3, axisX, axisY);
                return Math.Min(amax - bmin, bmax - amin) > 0d;
            }

            private float2 ToWorld(double localX, double localY)
            {
                return new float2((float)(BodyPositionX + PoseCos * localX - PoseSin * localY),
                    (float)(BodyPositionY + PoseSin * localX + PoseCos * localY));
            }

            private bool PositiveOverlap(in CachedSolidCell bodyCell, float2 w0, float2 w1, float2 w2, float2 w3)
            {
                double x0 = (double)bodyCell.X * CellSize, y0 = (double)bodyCell.Y * CellSize;
                double x1 = (double)(bodyCell.X + 1) * CellSize, y1 = (double)(bodyCell.Y + 1) * CellSize;
                float2 b0 = ToWorld(x0, y0), b1 = ToWorld(x1, y0);
                float2 b2 = ToWorld(x1, y1), b3 = ToWorld(x0, y1);
                return OverlapAxis(b0, b1, b2, b3, w0, w1, w2, w3, PoseCos, PoseSin) &&
                    OverlapAxis(b0, b1, b2, b3, w0, w1, w2, w3, -PoseSin, PoseCos) &&
                    OverlapAxis(b0, b1, b2, b3, w0, w1, w2, w3, 1d, 0d) &&
                    OverlapAxis(b0, b1, b2, b3, w0, w1, w2, w3, 0d, 1d);
            }

            public void Execute()
            {
                int minX = math.max(0, MinWorldX), maxX = math.min(MainWidth - 1, MaxWorldX);
                int minY = math.max(0, MinWorldY), maxY = math.min(MainHeight - 1, MaxWorldY);
                if (minX > maxX || minY > maxY) return;
                double halfExtent = CellSize * 0.5d * (Math.Abs(PoseCos) + Math.Abs(PoseSin));
                for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
                {
                    double centerX = (double)OriginX + (x + 0.5d) * CellSize;
                    double centerY = (double)OriginY + (y + 0.5d) * CellSize;
                    double deltaX = centerX - BodyPositionX, deltaY = centerY - BodyPositionY;
                    double localCenterX = PoseCos * deltaX + PoseSin * deltaY;
                    double localCenterY = -PoseSin * deltaX + PoseCos * deltaY;
                    int localMinX = (int)Math.Floor((localCenterX - halfExtent) / CellSize);
                    int localMinY = (int)Math.Floor((localCenterY - halfExtent) / CellSize);
                    int localMaxX = (int)Math.Floor((localCenterX + halfExtent) / CellSize);
                    int localMaxY = (int)Math.Floor((localCenterY + halfExtent) / CellSize);
                    localMinX = math.max(0, localMinX); localMinY = math.max(0, localMinY);
                    localMaxX = math.min(BodyWidth - 1, localMaxX); localMaxY = math.min(BodyHeight - 1, localMaxY);
                    if (localMinX > localMaxX || localMinY > localMaxY) continue;
                    float left = (float)((double)OriginX + (double)x * CellSize);
                    float right = (float)((double)OriginX + (double)(x + 1) * CellSize);
                    float bottom = (float)((double)OriginY + (double)y * CellSize);
                    float top = (float)((double)OriginY + (double)(y + 1) * CellSize);
                    float2 w0 = new float2(left, bottom), w1 = new float2(right, bottom);
                    float2 w2 = new float2(right, top), w3 = new float2(left, top);
                    bool covered = false;
                    for (int by = localMinY; by <= localMaxY && !covered; by++)
                        for (int bx = localMinX; bx <= localMaxX; bx++)
                        {
                            if (!SolidCells.TryGetValue(bx + by * BodyWidth, out CachedSolidCell bodyCell)) continue;
                            if (PositiveOverlap(bodyCell, w0, w1, w2, w3)) { covered = true; break; }
                        }
                    if (covered) CoveredCells.AddNoResize(x + y * MainWidth);
                }
            }
        }

        private void RemoveBodyCoverage(BodyV2 body)
        {
            int runtimeIndex = FindRuntime(body);
            if (runtimeIndex < 0) return;
            BodyRuntime runtime = _runtimeBodies[runtimeIndex];
            WakeSuspendedTiles(runtime.CoveredTiles);
            for (int i = 0; i < runtime.CoveredCells.Length; i++)
            {
                int key = runtime.CoveredCells[i];
                if (!_coverageCounts.TryGetValue(key, out int count)) continue;
                if (count <= 1) { _coverageCounts.Remove(key); SetDynamicKey(key, false); }
                else _coverageCounts[key] = count - 1;
            }
            runtime.CoveredCells.Clear();
            runtime.CoverageScratchCells.Clear();
            runtime.CoveredCellSet.Clear();
            runtime.CoverageScratchCellSet.Clear();
            runtime.CoveredTiles.Clear();
            runtime.CoverageScratchTiles.Clear();
            runtime.CoveredTileSet.Clear();
            runtime.CoverageScratchTileSet.Clear();
            runtime.HasCoveragePose = false;
        }

        public void CollectWet(ulong tick, bool beforeBurn)
        {
            EnsureInitialized();
            if (!beforeBurn && !_inStepCapture && _lastPostWetCaptureTick == tick) return;
            _wetContacts.Clear();
            _wetKeys.Clear();
            _wetComponents.Clear();
            _wetComponentKeys.Clear();
            if (_runtimeBodies.Count == 0)
            {
                _mainWetCellCount = 0;
                if (!beforeBurn) _lastPostWetCaptureTick = tick;
                return;
            }
            RefreshMainWetCount();
            if (_mainWetCellCount == 0)
            {
                if (!beforeBurn) _lastPostWetCaptureTick = tick;
                return;
            }
            for (int b = 0; b < _runtimeBodies.Count; b++)
            {
                BodyRuntime body = _runtimeBodies[b];
                EnsureBodyPoseCache(body);
                GetBodyBounds(body.Body, out int minX, out int minY, out int maxX, out int maxY);
                int scanMinX = Math.Max(0, minX), scanMaxX = Math.Min(_grid.Width - 1, maxX);
                int scanMinY = Math.Max(0, minY), scanMaxY = Math.Min(_grid.Height - 1, maxY);
                if (scanMinX > scanMaxX || scanMinY > scanMaxY) continue;
                int tileMinX = scanMinX >> 5, tileMaxX = scanMaxX >> 5;
                for (int y = scanMinY; y <= scanMaxY; y++) for (int tileX = tileMinX; tileX <= tileMaxX; tileX++)
                {
                    int tileId = tileX + (y >> 5) * _grid.TileColumns;
                    MaterialTile waterTile = _grid.Tiles[tileId];
                    if (!waterTile.IsCreated) continue;
                    int localMinX = tileX == tileMinX ? (scanMinX & 31) : 0;
                    int localMaxX = tileX == tileMaxX ? (scanMaxX & 31) : 31;
                    int row = y & 31;
                    uint lowMask = 1u << localMinX;
                    uint highMask = localMaxX == 31 ? uint.MaxValue : (1u << (localMaxX + 1)) - 1u;
                    uint occupied = waterTile.Occupied[row] & highMask & ~(lowMask - 1u);
                    while (occupied != 0)
                    {
                        int column = math.tzcnt(occupied);
                        occupied &= occupied - 1;
                        int x = (tileX << 5) + column;
                        WetCandidateVisits++;
                        int waterOffset = column + row * MaterialGrid.Side;
                        ushort waterMaterial = waterTile.Material[waterOffset];
                        if (!IsWetMaterial(waterMaterial)) continue;
                        int waterComponent = waterTile.Component[waterOffset];
                        CellKey waterKey = new CellKey(0, new CellPositionKey(OwnerKind.Grid, 0, x, y));
                        CellGeometry waterGeometry = new CellGeometry(waterKey, new BodyPose(_origin, 0), new Vector2Int(x, y), _config.CellSize);
                        if (!BodyContactsCell(body, waterGeometry)) continue;
                        ulong key = MixWetKey(body.Body.Id, x, y);
                        if (_wetContacts.Length >= 4096 && !_wetKeys.Contains(key))
                            throw new InvalidOperationException("湿接触候选超过4096容量。");
                        if (_wetKeys.Add(key))
                        {
                            _wetContacts.Add(new WetContactV2(body.Body.Id, x, y, tick, beforeBurn));
                            if (waterComponent != 0 && _grid.ColdStore.IsLive(waterComponent))
                            {
                                CellCold state = _grid.ColdStore.Read(waterComponent);
                                state.WetTick = tick;
                                _grid.ColdStore.Write(waterComponent, state);
                                if (_wetComponentKeys.Add(waterComponent)) _wetComponents.Add(waterComponent);
                            }
                        }
                    }
                }
            }
            if (!beforeBurn)
            {
                SuspendWetFluids(tick, beforeBurn);
                // Suspension removes the source cells after the contact pass. Fold
                // those writes into the counter before the next Tick clears ChangedTiles.
                RefreshMainWetCount();
                _lastPostWetCaptureTick = tick;
            }
        }

        private bool BodyContactsCell(BodyRuntime runtime, in CellGeometry worldCell)
        {
            BodyV2 body = runtime.Body;
            if (!runtime.SolidCells.IsCreated) return false;
            Vector2 worldCenter = new Vector2(worldCell.Pose.Position.x + (worldCell.LocalCell.x + 0.5f) * worldCell.CellSize,
                worldCell.Pose.Position.y + (worldCell.LocalCell.y + 0.5f) * worldCell.CellSize);
            double deltaX = (double)worldCenter.x - runtime.PosePosition.x;
            double deltaY = (double)worldCenter.y - runtime.PosePosition.y;
            double c = runtime.PoseCos, s = runtime.PoseSin;
            double localCenterX = c * deltaX + s * deltaY;
            double localCenterY = -s * deltaX + c * deltaY;
            double halfExtent = worldCell.CellSize * 0.5d * (Math.Abs(c) + Math.Abs(s));
            int minX = Math.Max(0, (int)Math.Floor((localCenterX - halfExtent) / _config.CellSize));
            int minY = Math.Max(0, (int)Math.Floor((localCenterY - halfExtent) / _config.CellSize));
            int maxX = Math.Min(body.Grid.Width - 1, (int)Math.Floor((localCenterX + halfExtent) / _config.CellSize));
            int maxY = Math.Min(body.Grid.Height - 1, (int)Math.Floor((localCenterY + halfExtent) / _config.CellSize));
            for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
            {
                if (!runtime.SolidCells.TryGetValue(x + y * body.Grid.Width, out _)) continue;
                CellKey key = new CellKey(0, new CellPositionKey(OwnerKind.Body, body.Id, x, y));
                CellGeometry local = new CellGeometry(key, body.Pose, new Vector2Int(x, y), _config.CellSize);
                CellContact contact = ExactCellGeometry.Contact(local, worldCell, 0);
                if (contact.Feature == ContactFeature.PositiveAreaOverlap ||
                    contact.Feature == ContactFeature.EdgeEdge || contact.Feature == ContactFeature.VertexEdge) return true;
            }
            return false;
        }

        private void SuspendWetFluids(ulong tick, bool beforeBurn)
        {
            for (int i = 0; i < _wetContacts.Length; i++)
            {
                WetContactV2 contact = _wetContacts[i];
                if (!_grid.Inside(contact.X, contact.Y)) continue;
                GridCell cell = _grid.Read(contact.X, contact.Y);
                if (cell.MaterialId == 0 || !IsWetMaterial(cell.MaterialId)) continue;
                if (_suspended.Length >= 4096) throw new InvalidOperationException("暂存水汽超过4096预算。");
                GridCell moved = _grid.Take(contact.X, contact.Y);
                CellCold state = moved.Cold;
                if (moved.ComponentHandle != 0 && _grid.ColdStore.IsLive(moved.ComponentHandle))
                {
                    state = _grid.ColdStore.Read(moved.ComponentHandle);
                    state.GridHandle = -1;
                    state.X = contact.X;
                    state.Y = contact.Y;
                    state.WetTick = tick;
                    _grid.ColdStore.Write(moved.ComponentHandle, state);
                }
                moved.Cold = state;
                ulong recordId = _nextSuspendedId++;
                ulong nextAttempt = tick == ulong.MaxValue ? ulong.MaxValue : tick + 1;
                _suspended.Add(new SuspendedFluidV2(recordId, moved, contact.X, contact.Y, tick,
                    beforeBurn, nextAttempt));
                int slot = _suspended.Length - 1;
                _suspendedSlots.TryAdd(recordId, slot);
                AddSuspendedBucket(recordId, slot, _grid.TileId(contact.X, contact.Y));
                if (moved.ComponentHandle != 0)
                    AddSuspendedComponent(recordId, slot, moved.ComponentHandle);
                AddRestoreSchedule(recordId, nextAttempt);
            }
        }

        /// <summary>Attempts deterministic four-neighbour restoration within the configured radius.</summary>
        public int RestoreFluids(ulong tick, int budget = 4096)
        {
            EnsureInitialized();
            if (budget < 0 || budget > 4096) throw new ArgumentOutOfRangeException(nameof(budget));
            PromoteRestoreSchedule(tick);
            int restored = 0;
            while (_restoreReadyIds.Length != 0 && budget > 0)
            {
                int readyIndex = _restoreReadyIds.Length - 1;
                ulong recordId = _restoreReadyIds[readyIndex];
                _restoreReadyIds.RemoveAt(readyIndex);
                _restoreReadySet.Remove(recordId);
                if (!_suspendedSlots.TryGetValue(recordId, out int i)) continue;
                SuspendedFluidV2 pending = _suspended[i];
                if (TryFindRestoreTarget(pending.X, pending.Y, ref budget, out int target, out bool exhausted))
                {
                    int x = target % _grid.Width, y = target / _grid.Width;
                    GridCell cell = pending.Cell;
                    if (cell.ComponentHandle != 0 && _grid.ColdStore.IsLive(cell.ComponentHandle))
                    {
                        CellCold state = _grid.ColdStore.Read(cell.ComponentHandle);
                        state.GridHandle = _grid.GridHandle;
                        state.X = x; state.Y = y;
                        cell.Cold = state;
                        _grid.ColdStore.Write(cell.ComponentHandle, state);
                    }
                    _grid.Write(x, y, cell);
                    _grid.WakeNeighborhood(x, y);
                    RemoveRestoreSchedule(recordId);
                    RemoveSuspendedAt(i);
                    restored++;
                    WakeSuspendedAround(x, y);
                }
                else if (!exhausted)
                {
                    pending.NextAttemptTick = ulong.MaxValue;
                    _suspended[i] = pending;
                }
                else QueueRestoreId(recordId);
            }
            return restored;
        }

        private void PromoteRestoreSchedule(ulong tick)
        {
            // Waiting records are promoted by a rotating, bounded cursor.  A tick never
            // traverses the entire wait list, even when thousands of bodies are blocked.
            int checks = Math.Min(_restoreSchedule.Length, 4096);
            for (int checkedCount = 0; checkedCount < checks && _restoreSchedule.Length != 0; checkedCount++)
            {
                if (_restoreScheduleCursor >= _restoreSchedule.Length) _restoreScheduleCursor = 0;
                int index = _restoreScheduleCursor;
                SuspendedScheduleV2 item = _restoreSchedule[index];
                if (item.DueTick <= tick)
                {
                    RemoveRestoreSchedule(item.RecordId);
                    QueueRestoreId(item.RecordId);
                    // RemoveAtSwapBack leaves the swapped entry at this cursor.
                    if (_restoreSchedule.Length != 0 && _restoreScheduleCursor >= _restoreSchedule.Length)
                        _restoreScheduleCursor = 0;
                }
                else
                {
                    _restoreScheduleCursor++;
                    if (_restoreScheduleCursor >= _restoreSchedule.Length) _restoreScheduleCursor = 0;
                }
            }
        }

        private void QueueRestoreId(ulong recordId)
        {
            if (!_suspendedSlots.ContainsKey(recordId)) return;
            if (_restoreReadySet.Add(recordId)) _restoreReadyIds.Add(recordId);
        }

        private void AddRestoreSchedule(ulong recordId, ulong dueTick)
        {
            int index = _restoreSchedule.Length;
            _restoreSchedule.Add(new SuspendedScheduleV2(recordId, dueTick));
            _restoreScheduleSlots.TryAdd(recordId, index);
        }

        private void RemoveRestoreSchedule(ulong recordId)
        {
            if (!_restoreScheduleSlots.TryGetValue(recordId, out int index)) return;
            int last = _restoreSchedule.Length - 1;
            _restoreScheduleSlots.Remove(recordId);
            if (index != last)
            {
                SuspendedScheduleV2 moved = _restoreSchedule[last];
                _restoreSchedule[index] = moved;
                _restoreScheduleSlots[moved.RecordId] = index;
            }
            _restoreSchedule.RemoveAtSwapBack(last);
            if (_restoreSchedule.Length == 0) _restoreScheduleCursor = 0;
            else
            {
                if (index < _restoreScheduleCursor) _restoreScheduleCursor--;
                if (_restoreScheduleCursor >= _restoreSchedule.Length) _restoreScheduleCursor = 0;
            }
        }

        private void AddSuspendedBucket(ulong recordId, int slot, int tileId)
        {
            SuspendedFluidV2 value = _suspended[slot];
            value.BucketTileId = tileId;
            value.BucketPreviousRecordId = 0;
            value.BucketNextRecordId = _suspendedBucketHeads.TryGetValue(tileId, out ulong head) ? head : 0;
            _suspended[slot] = value;
            if (value.BucketNextRecordId != 0 && _suspendedSlots.TryGetValue(value.BucketNextRecordId, out int headSlot))
            {
                SuspendedFluidV2 oldHead = _suspended[headSlot];
                oldHead.BucketPreviousRecordId = recordId;
                _suspended[headSlot] = oldHead;
            }
            if (_suspendedBucketHeads.ContainsKey(tileId)) _suspendedBucketHeads[tileId] = recordId;
            else if (!_suspendedBucketHeads.TryAdd(tileId, recordId))
                throw new InvalidOperationException("暂存水汽页索引容量不足。");
        }

        private void UnlinkSuspendedBucket(in SuspendedFluidV2 value)
        {
            if (value.BucketPreviousRecordId != 0 && _suspendedSlots.TryGetValue(value.BucketPreviousRecordId, out int previousSlot))
            {
                SuspendedFluidV2 previous = _suspended[previousSlot];
                previous.BucketNextRecordId = value.BucketNextRecordId;
                _suspended[previousSlot] = previous;
            }
            else if (_suspendedBucketHeads.TryGetValue(value.BucketTileId, out ulong head) && head == value.RecordId)
            {
                if (value.BucketNextRecordId == 0) _suspendedBucketHeads.Remove(value.BucketTileId);
                else _suspendedBucketHeads[value.BucketTileId] = value.BucketNextRecordId;
            }
            if (value.BucketNextRecordId != 0 && _suspendedSlots.TryGetValue(value.BucketNextRecordId, out int nextSlot))
            {
                SuspendedFluidV2 next = _suspended[nextSlot];
                next.BucketPreviousRecordId = value.BucketPreviousRecordId;
                _suspended[nextSlot] = next;
            }
        }

        private void AddSuspendedComponent(ulong recordId, int slot, int componentHandle)
        {
            SuspendedFluidV2 value = _suspended[slot];
            value.ComponentPreviousRecordId = 0;
            value.ComponentNextRecordId = _suspendedComponentRecords.TryGetValue(componentHandle, out ulong head) ? head : 0;
            _suspended[slot] = value;
            if (value.ComponentNextRecordId != 0 && _suspendedSlots.TryGetValue(value.ComponentNextRecordId, out int headSlot))
            {
                SuspendedFluidV2 oldHead = _suspended[headSlot];
                oldHead.ComponentPreviousRecordId = recordId;
                _suspended[headSlot] = oldHead;
            }
            if (_suspendedComponentRecords.ContainsKey(componentHandle)) _suspendedComponentRecords[componentHandle] = recordId;
            else if (!_suspendedComponentRecords.TryAdd(componentHandle, recordId))
                throw new InvalidOperationException("暂存水汽组件索引容量不足。");
        }

        private void UnlinkSuspendedComponent(in SuspendedFluidV2 value)
        {
            int componentHandle = value.Cell.ComponentHandle;
            if (componentHandle == 0) return;
            if (value.ComponentPreviousRecordId != 0 && _suspendedSlots.TryGetValue(value.ComponentPreviousRecordId, out int previousSlot))
            {
                SuspendedFluidV2 previous = _suspended[previousSlot];
                previous.ComponentNextRecordId = value.ComponentNextRecordId;
                _suspended[previousSlot] = previous;
            }
            else if (_suspendedComponentRecords.TryGetValue(componentHandle, out ulong head) && head == value.RecordId)
            {
                if (value.ComponentNextRecordId == 0) _suspendedComponentRecords.Remove(componentHandle);
                else _suspendedComponentRecords[componentHandle] = value.ComponentNextRecordId;
            }
            if (value.ComponentNextRecordId != 0 && _suspendedSlots.TryGetValue(value.ComponentNextRecordId, out int nextSlot))
            {
                SuspendedFluidV2 next = _suspended[nextSlot];
                next.ComponentPreviousRecordId = value.ComponentPreviousRecordId;
                _suspended[nextSlot] = next;
            }
        }

        private void RemoveSuspendedAt(int slot)
        {
            SuspendedFluidV2 value = _suspended[slot];
            UnlinkSuspendedBucket(value);
            UnlinkSuspendedComponent(value);
            _suspendedSlots.Remove(value.RecordId);
            int last = _suspended.Length - 1;
            if (slot != last)
            {
                SuspendedFluidV2 moved = _suspended[last];
                _suspended[slot] = moved;
                _suspendedSlots[moved.RecordId] = slot;
            }
            _suspended.RemoveAtSwapBack(last);
        }

        private bool TryFindRestoreTarget(int sourceX, int sourceY, ref int budget, out int target, out bool exhausted)
        {
            target = -1; exhausted = false;
            if (!_grid.Inside(sourceX, sourceY)) return false;
            int radius = Math.Max(0, _config.Limits.FluidDisplacementRadius);
            _restoreQueue.Clear(); _restoreDistances.Clear(); _restoreVisited.Clear();
            int source = sourceX + sourceY * _grid.Width;
            _restoreQueue.Add(source); _restoreDistances.Add(0); _restoreVisited.Add(source);
            for (int head = 0; head < _restoreQueue.Length; head++)
            {
                if (budget-- <= 0) { exhausted = true; return false; }
                int key = _restoreQueue[head], distance = _restoreDistances[head];
                int x = key % _grid.Width, y = key / _grid.Width;
                if (_grid.Passable(x, y)) { target = key; return true; }
                if (distance >= radius) continue;
                TryQueueRestore(x - 1, y, distance + 1, radius);
                TryQueueRestore(x + 1, y, distance + 1, radius);
                TryQueueRestore(x, y - 1, distance + 1, radius);
                TryQueueRestore(x, y + 1, distance + 1, radius);
            }
            return false;
        }

        private void TryQueueRestore(int x, int y, int distance, int radius)
        {
            if (distance > radius || !_grid.Inside(x, y)) return;
            int key = x + y * _grid.Width;
            if (_restoreVisited.Contains(key) || !_grid.Passable(x, y)) return;
            _restoreVisited.Add(key);
            _restoreQueue.Add(key); _restoreDistances.Add(distance);
        }

        /// <summary>Wakes only records whose bounded search neighborhood changed.</summary>
        public void WakeSuspendedRestoration(int x, int y) => WakeSuspendedAround(x, y);

        /// <summary>
        /// Wakes suspended records near one changed main-grid tile.  Only the tile bucket and
        /// its radius-four neighboring buckets are visited; records outside the expanded tile
        /// rectangle are rejected without touching the global suspended list.
        /// </summary>
        public void WakeSuspendedTile(int tileId)
        {
            if (tileId < 0 || tileId >= _grid.TileColumns * _grid.TileRows) return;
            RectInt tile = _grid.TileBounds(tileId);
            int radius = Math.Max(1, _config.Limits.FluidDisplacementRadius);
            WakeSuspendedRegion(tile.xMin - radius, tile.yMin - radius,
                tile.xMax - 1 + radius, tile.yMax - 1 + radius);
        }

        private void WakeSuspendedTiles(NativeList<int> tileIds)
        {
            for (int i = 0; i < tileIds.Length; i++) WakeSuspendedTile(tileIds[i]);
        }

        private void WakeSuspendedAround(int x, int y)
        {
            int radius = Math.Max(1, _config.Limits.FluidDisplacementRadius);
            WakeSuspendedRegion(x - radius, y - radius, x + radius, y + radius);
        }

        private void WakeSuspendedRegion(int minX, int minY, int maxX, int maxY)
        {
            if (_suspended.Length == 0) return;
            int minTileX = Math.Max(0, minX >> 5), minTileY = Math.Max(0, minY >> 5);
            int maxTileX = Math.Min(_grid.TileColumns - 1, Math.Max(0, maxX) >> 5);
            int maxTileY = Math.Min(_grid.TileRows - 1, Math.Max(0, maxY) >> 5);
            for (int ty = minTileY; ty <= maxTileY; ty++) for (int tx = minTileX; tx <= maxTileX; tx++)
            {
                int tileId = tx + ty * _grid.TileColumns;
                if (!_suspendedBucketHeads.TryGetValue(tileId, out ulong recordId)) continue;
                while (recordId != 0)
                {
                    if (!_suspendedSlots.TryGetValue(recordId, out int slot)) break;
                    SuspendedFluidV2 value = _suspended[slot];
                    ulong nextRecordId = value.BucketNextRecordId;
                    if (value.X >= minX && value.X <= maxX && value.Y >= minY && value.Y <= maxY)
                    {
                        value.NextAttemptTick = 0;
                        _suspended[slot] = value;
                        QueueRestoreId(value.RecordId);
                    }
                    recordId = nextRecordId;
                }
            }
        }

        public bool ExpireSuspended(int componentHandle)
        {
            if (componentHandle == 0 || !_suspendedComponentRecords.TryGetValue(componentHandle, out ulong recordId))
                return false;
            bool expired = false;
            while (recordId != 0 && _suspendedSlots.TryGetValue(recordId, out int slot))
            {
                // RemoveSuspendedAt updates the component head; read that new head after
                // unlinking so swap-back removal cannot invalidate a saved slot.
                _restoreReadySet.Remove(recordId);
                RemoveRestoreSchedule(recordId);
                RemoveSuspendedAt(slot);
                expired = true;
                if (!_suspendedComponentRecords.TryGetValue(componentHandle, out recordId)) break;
            }
            if (expired) _grid.ColdStore.Delete(componentHandle);
            return expired;
        }

        private static ulong MixWetKey(ulong bodyId, int x, int y)
        {
            unchecked
            {
                ulong key = bodyId + 0x9E3779B97F4A7C15UL;
                key ^= (uint)x + 0x9E3779B9u + (key << 6) + (key >> 2);
                key ^= (uint)y + 0x9E3779B9u + (key << 6) + (key >> 2);
                return key;
            }
        }

        private bool IsWetMaterial(ushort id)
        {
            return IsWetMaterial(_grid, id);
        }

        private static bool IsWetMaterial(MaterialGrid grid, ushort id)
        {
            MaterialDefinition definition = grid.Definitions[id];
            return definition.Kind == MaterialKind.Liquid && definition.IsWater;
        }

        private void RefreshMainWetCount()
        {
            long count = 0;
            for (int i = 0; i < _wetMaterialIds.Length; i++)
                count += _grid.Counts[_wetMaterialIds[i]];
            _mainWetCellCount = count > int.MaxValue ? int.MaxValue : (int)count;
        }

        private static bool IsSolid(MaterialGrid grid, ushort id)
        {
            if (id == 0) return false;
            MaterialDefinition definition = grid.Definitions[id];
            return definition.Kind == MaterialKind.Solid && definition.IsStructure;
        }

        private void GetBodyBounds(BodyV2 body, out int minX, out int minY, out int maxX, out int maxY)
        {
            GetBodyBoundsAtPose(body, body.Pose, out minX, out minY, out maxX, out maxY);
        }

        private void UpdateBodyPoseCache(BodyRuntime runtime)
        {
            BodyPose pose = runtime.Body.Pose;
            runtime.PosePosition = pose.Position;
            runtime.PoseCos = Math.Cos(pose.AngleRadians);
            runtime.PoseSin = Math.Sin(pose.AngleRadians);
            runtime.PoseAngle = pose.AngleRadians;
            runtime.HasPoseCache = true;
        }

        private void EnsureBodyPoseCache(BodyRuntime runtime)
        {
            BodyPose pose = runtime.Body.Pose;
            if (!runtime.HasPoseCache || runtime.PosePosition != pose.Position || runtime.PoseAngle != pose.AngleRadians)
                UpdateBodyPoseCache(runtime);
        }

        private static void EnsureSolidCacheCapacity(BodyRuntime runtime)
        {
            int needed = Math.Max(256, runtime.Body.Grid.CellCount * 2 + 16);
            if (runtime.SolidCells.Capacity < needed) runtime.SolidCells.Capacity = needed;
        }

        private void ReserveCoverageCapacity(BodyRuntime runtime)
        {
            double width = runtime.Body.Grid.Width;
            double height = runtime.Body.Grid.Height;
            // GetBodyBoundsAtPose uses the full local extent on each axis and adds a
            // one-cell conservative halo, so reserve the diameter of the rotation
            // bounding circle plus both halo cells rather than the current angle's AABB.
            long side = checked((long)Math.Ceiling(2d * Math.Sqrt(width * width + height * height)) + 4L);
            long capacity = checked(side * side);
            long worldArea = (long)_grid.Width * _grid.Height;
            capacity = Math.Min(capacity, worldArea);
            if (capacity > int.MaxValue)
                throw new InvalidOperationException("动态覆盖候选容量超过整数上限。");
            int needed = (int)Math.Max(256L, capacity);
            if (runtime.CoveredCells.Capacity < needed) runtime.CoveredCells.Capacity = needed;
            if (runtime.CoverageScratchCells.Capacity < needed) runtime.CoverageScratchCells.Capacity = needed;
            if (runtime.CoveredCellSet.Capacity < needed) runtime.CoveredCellSet.Capacity = needed;
            if (runtime.CoverageScratchCellSet.Capacity < needed) runtime.CoverageScratchCellSet.Capacity = needed;
            long tileSide = (side + MaterialGrid.Side - 1L) / MaterialGrid.Side + 2L;
            long tileCapacity = Math.Min(tileSide * tileSide,
                (long)_grid.TileColumns * _grid.TileRows);
            if (tileCapacity > int.MaxValue) throw new InvalidOperationException("动态覆盖页容量超过整数上限。");
            int tileNeeded = (int)Math.Max(32L, tileCapacity);
            if (runtime.CoveredTiles.Capacity < tileNeeded) runtime.CoveredTiles.Capacity = tileNeeded;
            if (runtime.CoverageScratchTiles.Capacity < tileNeeded) runtime.CoverageScratchTiles.Capacity = tileNeeded;
            if (runtime.CoveredTileSet.Capacity < tileNeeded) runtime.CoveredTileSet.Capacity = tileNeeded;
            if (runtime.CoverageScratchTileSet.Capacity < tileNeeded) runtime.CoverageScratchTileSet.Capacity = tileNeeded;
        }

        private void RefreshBodySolidTile(BodyRuntime runtime, int tileId)
        {
            MaterialGrid grid = runtime.Body.Grid;
            if (tileId < 0 || tileId >= grid.TileColumns * grid.TileRows) return;
            int tileX = tileId % grid.TileColumns, tileY = tileId / grid.TileColumns;
            int minX = tileX * MaterialGrid.Side, minY = tileY * MaterialGrid.Side;
            int maxX = Math.Min(minX + MaterialGrid.Side, grid.Width);
            int maxY = Math.Min(minY + MaterialGrid.Side, grid.Height);
            for (int y = minY; y < maxY; y++)
                for (int x = minX; x < maxX; x++) runtime.SolidCells.Remove(x + y * grid.Width);
            MaterialTile tile = grid.Tiles[tileId];
            if (!tile.IsCreated) return;
            EnsureSolidCacheCapacity(runtime);
            int width = maxX - minX;
            for (int row = 0; row < maxY - minY; row++)
            {
                uint occupied = tile.Occupied[row];
                if (width < MaterialGrid.Side) occupied &= (1u << width) - 1u;
                while (occupied != 0)
                {
                    int column = math.tzcnt(occupied); occupied &= occupied - 1;
                    ushort material = tile.Material[column + row * MaterialGrid.Side];
                    if (!IsSolid(grid, material)) continue;
                    int x = minX + column, y = minY + row;
                    var cached = new CachedSolidCell(x, y);
                    if (!runtime.SolidCells.TryAdd(x + y * grid.Width, cached))
                        throw new InvalidOperationException("动态体精确几何缓存容量不足。");
                }
            }
        }

        private void RebuildBodyColliders(BodyRuntime body)
        {
            EnsureSolidCacheCapacity(body);
            body.SolidCells.Clear();
            body.OccupiedTiles.Clear();
            body.OccupiedTileSet.Clear();
            body.Body.ShapeCount = 0;
            foreach (List<BoxCollider2D> pool in body.TileColliders.Values)
                for (int i = 0; i < pool.Count; i++) pool[i].enabled = false;
            body.TileShapeCounts.Clear();
            for (int i = 0; i < body.Body.Grid.AllocatedTileCount; i++)
                RebuildBodyTile(body, body.Body.Grid.GetAllocatedTileId(i));
            FinalizeBodyColliders(body);
            UpdateBodyPoseCache(body);
        }

        private void RebuildBodyAffectedColliders(BodyRuntime body, NativeArray<int> dirtyTiles)
        {
            var affected = new HashSet<int>();
            for (int i = 0; i < dirtyTiles.Length; i++)
            {
                int tile = dirtyTiles[i];
                affected.Add(tile);
                int tx = tile % body.Body.Grid.TileColumns, ty = tile / body.Body.Grid.TileColumns;
                if (tx > 0) affected.Add(tile - 1);
                if (tx + 1 < body.Body.Grid.TileColumns) affected.Add(tile + 1);
                if (ty > 0) affected.Add(tile - body.Body.Grid.TileColumns);
                if ((ty + 1) * MaterialConnectivity.TileSize < body.Body.Grid.Height) affected.Add(tile + body.Body.Grid.TileColumns);
            }
            foreach (int tile in affected) RebuildBodyTile(body, tile);
            FinalizeBodyColliders(body);
            UpdateBodyPoseCache(body);
        }

        private void RebuildBodyTile(BodyRuntime body, int tileId)
        {
            if (tileId < 0 || tileId >= body.Body.Grid.TileColumns * body.Body.Grid.TileRows) return;
            RefreshBodySolidTile(body, tileId);
            if (!body.TileColliders.TryGetValue(tileId, out List<BoxCollider2D> pool))
            {
                pool = new List<BoxCollider2D>(16);
                body.TileColliders.Add(tileId, pool);
            }
            for (int i = 0; i < pool.Count; i++) pool[i].enabled = false;
            body.TileShapeCounts.TryGetValue(tileId, out int previous);
            int tileX = tileId % body.Body.Grid.TileColumns, tileY = tileId / body.Body.Grid.TileColumns;
            int minX = tileX * MaterialConnectivity.TileSize, minY = tileY * MaterialConnectivity.TileSize;
            int maxX = Math.Min(minX + MaterialConnectivity.TileSize, body.Body.Grid.Width);
            int maxY = Math.Min(minY + MaterialConnectivity.TileSize, body.Body.Grid.Height);
            int shapeCount = 0;
            MaterialTile sourceTile = body.Body.Grid.Tiles[tileId];
            if (sourceTile.IsCreated) for (int y = minY; y < maxY; y++)
            {
                int x = minX;
                while (x < maxX)
                {
                    int row = y & 31;
                    while (x < maxX && !IsSolid(body.Body.Grid, sourceTile.Material[(x & 31) + row * MaterialGrid.Side])) x++;
                    int start = x;
                    while (x < maxX && IsSolid(body.Body.Grid, sourceTile.Material[(x & 31) + row * MaterialGrid.Side])) x++;
                    if (x == start) continue;
                    if (shapeCount == pool.Count) pool.Add(body.GameObject.AddComponent<BoxCollider2D>());
                    BoxCollider2D collider = pool[shapeCount++];
                    collider.enabled = true;
                    collider.size = new Vector2((x - start) * _config.CellSize, _config.CellSize);
                    collider.offset = new Vector2((start + (x - start) * 0.5f) * _config.CellSize,
                        (y + 0.5f) * _config.CellSize);
                    collider.sharedMaterial = _surface;
                }
            }
            for (int i = shapeCount; i < pool.Count; i++) pool[i].enabled = false;
            if (previous != shapeCount)
            {
                if (previous != 0) body.Body.ShapeCount -= previous;
                body.Body.ShapeCount += shapeCount;
            }
            body.TileShapeCounts[tileId] = shapeCount;
            if (shapeCount != 0)
            {
                if (body.OccupiedTileSet.Add(tileId)) body.OccupiedTiles.Add(tileId);
            }
            else if (body.OccupiedTileSet.Remove(tileId))
            {
                for (int i = body.OccupiedTiles.Length - 1; i >= 0; i--)
                    if (body.OccupiedTiles[i] == tileId) { body.OccupiedTiles.RemoveAt(i); break; }
            }
        }

        private void FinalizeBodyColliders(BodyRuntime body)
        {
            int count = body.Body.ShapeCount;
            body.Body.ShapeCount = count;
            if (count > _config.Limits.MaxShapesPerBody)
                throw new InvalidOperationException("单体形状超过配置预算。");
            body.HasColliders = true;
            body.Rigidbody.simulated = true;
            RecomputeBodyMassProperties(body);
            body.Rigidbody.constraints = _freezeRotation ? RigidbodyConstraints2D.FreezeRotation : RigidbodyConstraints2D.None;
        }

        private void RecomputeBodyMassProperties(BodyRuntime body)
        {
            double total = 0, sumX = 0, sumY = 0;
            float size = _config.CellSize;
            for (int y = 0; y < body.Body.Grid.Height; y++) for (int x = 0; x < body.Body.Grid.Width; x++)
            {
                GridCell cell = body.Body.Grid.Read(x, y);
                if (cell.MaterialId == 0 || !IsSolid(body.Body.Grid, cell.MaterialId)) continue;
                float mass = body.Body.Grid.Definitions[cell.MaterialId].Mass;
                if (!(mass > 0) || float.IsNaN(mass) || float.IsInfinity(mass)) mass = 1;
                total += mass;
                sumX += mass * (x + 0.5) * size;
                sumY += mass * (y + 0.5) * size;
            }
            if (total <= 0) { body.Rigidbody.mass = 0.0001f; body.Rigidbody.centerOfMass = Vector2.zero; body.Rigidbody.inertia = 0.0001f; return; }
            Vector2 center = new Vector2((float)(sumX / total), (float)(sumY / total));
            double inertia = 0;
            for (int y = 0; y < body.Body.Grid.Height; y++) for (int x = 0; x < body.Body.Grid.Width; x++)
            {
                GridCell cell = body.Body.Grid.Read(x, y);
                if (cell.MaterialId == 0 || !IsSolid(body.Body.Grid, cell.MaterialId)) continue;
                float mass = body.Body.Grid.Definitions[cell.MaterialId].Mass;
                if (!(mass > 0) || float.IsNaN(mass) || float.IsInfinity(mass)) mass = 1;
                Vector2 delta = new Vector2((x + 0.5f) * size, (y + 0.5f) * size) - center;
                inertia += mass * (size * size / 6d + delta.sqrMagnitude);
            }
            body.Rigidbody.mass = (float)Math.Max(0.0001, total);
            body.Rigidbody.centerOfMass = center;
            body.Rigidbody.inertia = (float)Math.Max(0.0001, inertia);
        }

        private BodyObjects CreateBodyObjects(BodyV2 body)
        {
            GameObject go = new GameObject("OpenOitaV2Body-" + body.Id);
            SceneManager.MoveGameObjectToScene(go, _scene);
            go.transform.SetPositionAndRotation(body.Pose.Position, Quaternion.Euler(0, 0, body.Pose.AngleRadians * Mathf.Rad2Deg));
            Rigidbody2D rb = go.AddComponent<Rigidbody2D>();
            rb.simulated = false; rb.useAutoMass = false; rb.gravityScale = 0; rb.linearDamping = 0; rb.angularDamping = 0;
            rb.sleepMode = RigidbodySleepMode2D.StartAwake; rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.linearVelocity = body.Motion.LinearVelocity;
            rb.angularVelocity = _freezeRotation ? 0 : body.Motion.AngularVelocityRadians * Mathf.Rad2Deg;
            return new BodyObjects(go, rb);
        }

        private void CreateBoundary(Vector2 center, Vector2 size)
        {
            GameObject go = new GameObject("OpenOitaV2Boundary");
            SceneManager.MoveGameObjectToScene(go, _scene);
            go.transform.position = center;
            BoxCollider2D collider = go.AddComponent<BoxCollider2D>();
            collider.size = size; collider.sharedMaterial = _surface;
            _boundaries.Add(go);
        }

        private void EnsureInitialized()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterialPhysics));
            if (!_initialized) Initialize();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _runtimeBodies.Count; i++)
            {
                RemoveBodyCoverage(_runtimeBodies[i].Body);
                Release(_runtimeBodies[i].GameObject);
                if (_extractedBodyIds.Contains(_runtimeBodies[i].Body.Id))
                {
                    ClearGridTimers(_runtimeBodies[i].Body.Grid);
                    _runtimeBodies[i].Body.Grid.Dispose();
                }
                _runtimeBodies[i].Dispose();
            }
            for (int i = 0; i < _boundaries.Count; i++) Release(_boundaries[i]);
            Release(_fixedTerrain);
            _fixedTerrain = null;
            _fixedTileColliders.Clear();
            _fixedTileShapeCounts.Clear();
            _runtimeBodies.Clear(); _runtimeById.Clear(); _bodiesById.Clear(); _bodiesByGridHandle.Clear(); _boundaries.Clear();
            if (_bodyCandidateIds.IsCreated) _bodyCandidateIds.Dispose();
            _bodySpatialIndex.Dispose();
            if (_surface != null) Release(_surface);
            if (_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
            if (_dirtyTiles.IsCreated) _dirtyTiles.Dispose();
            if (_wetContacts.IsCreated) _wetContacts.Dispose();
            if (_wetKeys.IsCreated) _wetKeys.Dispose();
            if (_wetMaterialIds.IsCreated) _wetMaterialIds.Dispose();
            if (_suspended.IsCreated) _suspended.Dispose();
            if (_wetComponents.IsCreated) _wetComponents.Dispose();
            if (_wetComponentKeys.IsCreated) _wetComponentKeys.Dispose();
            if (_coverageCounts.IsCreated) _coverageCounts.Dispose();
            if (_restoreQueue.IsCreated) _restoreQueue.Dispose();
            if (_restoreDistances.IsCreated) _restoreDistances.Dispose();
            if (_restoreVisited.IsCreated) _restoreVisited.Dispose();
            if (_restoreSchedule.IsCreated) _restoreSchedule.Dispose();
            if (_restoreReadyIds.IsCreated) _restoreReadyIds.Dispose();
            if (_restoreReadySet.IsCreated) _restoreReadySet.Dispose();
            if (_suspendedSlots.IsCreated) _suspendedSlots.Dispose();
            if (_suspendedBucketHeads.IsCreated) _suspendedBucketHeads.Dispose();
            if (_suspendedComponentRecords.IsCreated) _suspendedComponentRecords.Dispose();
            if (_restoreScheduleSlots.IsCreated) _restoreScheduleSlots.Dispose();
            if (_ignitionKeys.IsCreated) _ignitionKeys.Dispose();
            if (_componentRoots.IsCreated) _componentRoots.Dispose();
            if (_groupRoots.IsCreated) _groupRoots.Dispose();
            if (_componentCells.IsCreated) _componentCells.Dispose();
            if (_queryTiles.IsCreated) _queryTiles.Dispose();
            _connectivity.Dispose();
            _activeFixedTiles.Clear();
            _desiredFixedTiles.Clear();
            _fixedDirtyTiles.Clear();
            _fixedTilesToDisable.Clear();
            _logicalFixedTileShapeCounts.Clear();
        }

        private static void Release(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }

        private sealed class BodyRuntime
        {
            internal readonly BodyV2 Body;
            // Kept with the body for its whole lifetime.  Rebuilding this graph per
            // geometry edit both allocates and loses cross-tile connectivity state.
            internal readonly MaterialConnectivity Connectivity;
            internal bool ConnectivityBuilt;
            internal readonly GameObject GameObject;
            internal readonly Rigidbody2D Rigidbody;
            internal readonly List<BoxCollider2D> Colliders = new List<BoxCollider2D>(32);
            internal readonly Dictionary<int, List<BoxCollider2D>> TileColliders = new Dictionary<int, List<BoxCollider2D>>();
            internal readonly Dictionary<int, int> TileShapeCounts = new Dictionary<int, int>();
            internal NativeList<int> OccupiedTiles;
            internal NativeParallelHashSet<int> OccupiedTileSet;
            internal NativeList<int> CoveredCells;
            internal NativeList<int> CoverageScratchCells;
            internal NativeParallelHashSet<int> CoveredCellSet;
            internal NativeParallelHashSet<int> CoverageScratchCellSet;
            internal NativeList<int> CoveredTiles;
            internal NativeList<int> CoverageScratchTiles;
            internal NativeParallelHashSet<int> CoveredTileSet;
            internal NativeParallelHashSet<int> CoverageScratchTileSet;
            internal NativeParallelHashMap<int, CachedSolidCell> SolidCells;
            internal BodyPose CoveragePose;
            internal bool HasCoveragePose;
            internal Vector2 PosePosition;
            internal double PoseCos, PoseSin;
            internal float PoseAngle;
            internal bool HasPoseCache;
            internal bool HasColliders;
            internal bool SpatialIndexed;
            internal int SpatialMinX, SpatialMinY, SpatialMaxX, SpatialMaxY;
            internal BodyRuntime(BodyV2 body, BodyObjects objects)
            {
                Body = body; Connectivity = new MaterialConnectivity(body.Grid); ConnectivityBuilt = false;
                GameObject = objects.GameObject; Rigidbody = objects.Rigidbody;
                int tileCapacity = Math.Max(32, body.Grid.TileColumns * body.Grid.TileRows);
                OccupiedTiles = new NativeList<int>(tileCapacity, Allocator.Persistent);
                OccupiedTileSet = new NativeParallelHashSet<int>(tileCapacity, Allocator.Persistent);
                CoveredCells = new NativeList<int>(256, Allocator.Persistent);
                CoverageScratchCells = new NativeList<int>(256, Allocator.Persistent);
                CoveredCellSet = new NativeParallelHashSet<int>(256, Allocator.Persistent);
                CoverageScratchCellSet = new NativeParallelHashSet<int>(256, Allocator.Persistent);
                CoveredTiles = new NativeList<int>(tileCapacity, Allocator.Persistent);
                CoverageScratchTiles = new NativeList<int>(tileCapacity, Allocator.Persistent);
                CoveredTileSet = new NativeParallelHashSet<int>(tileCapacity, Allocator.Persistent);
                CoverageScratchTileSet = new NativeParallelHashSet<int>(tileCapacity, Allocator.Persistent);
                SolidCells = new NativeParallelHashMap<int, CachedSolidCell>(Math.Max(256, body.Grid.CellCount * 2), Allocator.Persistent);
            }
            internal void Dispose()
            {
                OccupiedTiles.Dispose(); OccupiedTileSet.Dispose(); CoveredCells.Dispose(); CoverageScratchCells.Dispose();
                CoveredCellSet.Dispose(); CoverageScratchCellSet.Dispose(); CoveredTiles.Dispose(); CoverageScratchTiles.Dispose();
                CoveredTileSet.Dispose(); CoverageScratchTileSet.Dispose(); SolidCells.Dispose(); Connectivity.Dispose();
            }
        }

        private readonly struct BodyObjects
        {
            internal readonly GameObject GameObject;
            internal readonly Rigidbody2D Rigidbody;
            internal BodyObjects(GameObject gameObject, Rigidbody2D rigidbody)
            { GameObject = gameObject; Rigidbody = rigidbody; }
        }

        private readonly struct FixedRectV2
        {
            internal readonly int X, Y, Width, Height;
            internal FixedRectV2(int x, int y, int width, int height)
            { X = x; Y = y; Width = width; Height = height; }
        }

        private readonly struct CommandRange
        {
            internal readonly int X0, Y0, X1, Y1;
            internal bool IsEmpty => X0 > X1 || Y0 > Y1;
            internal int Width => IsEmpty ? 0 : X1 - X0 + 1;
            internal int Height => IsEmpty ? 0 : Y1 - Y0 + 1;
            internal CommandRange(int x0, int y0, int x1, int y1)
            { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        }

        private sealed class BodyCommandRange
        {
            internal readonly BodyV2 Body;
            internal readonly List<CommandRange> Ranges;
            internal readonly Dictionary<int, GridCell> Draft;
            internal int InitialMappedCells;
            internal int InitialBodyCount;
            internal int InitialStaticShapes;

            internal BodyCommandRange(BodyV2 body, int commandCount)
            {
                Body = body;
                Ranges = new List<CommandRange>(commandCount);
                Draft = new Dictionary<int, GridCell>(128);
            }
        }
    }

    public sealed class BodyV2
    {
        public ulong Id { get; }
        public MaterialGrid Grid { get; }
        public BodyPose Pose;
        public BodyMotion Motion;
        public int ShapeCount { get; internal set; }
        public BodyV2(ulong id, MaterialGrid grid, BodyPose pose, BodyMotion motion)
        { Id = id; Grid = grid ?? throw new ArgumentNullException(nameof(grid)); Pose = pose; Motion = motion; }
    }

    public readonly struct WetContactV2
    {
        public readonly ulong BodyId;
        public readonly int X;
        public readonly int Y;
        public readonly ulong Tick;
        public readonly bool BeforeBurn;
        public WetContactV2(ulong bodyId, int x, int y, ulong tick, bool beforeBurn)
        { BodyId = bodyId; X = x; Y = y; Tick = tick; BeforeBurn = beforeBurn; }
    }

    public struct SuspendedFluidV2
    {
        public ulong RecordId;
        public GridCell Cell;
        public int X, Y;
        public ulong EnterTick;
        public bool BeforeBurn;
        public ulong NextAttemptTick;
        // Intrusive indexes keep bucket and component removal O(1); these are implementation
        // fields and are deliberately carried with the record across swap-back moves.
        internal int BucketTileId;
        internal ulong BucketPreviousRecordId, BucketNextRecordId;
        internal ulong ComponentPreviousRecordId, ComponentNextRecordId;

        public SuspendedFluidV2(ulong recordId, GridCell cell, int x, int y, ulong enterTick,
            bool beforeBurn, ulong nextAttemptTick)
        {
            RecordId = recordId; Cell = cell; X = x; Y = y; EnterTick = enterTick;
            BeforeBurn = beforeBurn; NextAttemptTick = nextAttemptTick;
            BucketTileId = -1; BucketPreviousRecordId = 0; BucketNextRecordId = 0;
            ComponentPreviousRecordId = 0; ComponentNextRecordId = 0;
        }
    }

    public readonly struct SuspendedScheduleV2
    {
        public readonly ulong RecordId;
        public readonly ulong DueTick;
        public SuspendedScheduleV2(ulong recordId, ulong dueTick)
        { RecordId = recordId; DueTick = dueTick; }
    }

    /// <summary>Projected structure resource use. Values describe the complete post-edit projection.</summary>
    public readonly struct StructureCapacityV2
    {
        public readonly int DynamicBodies;
        public readonly int StaticShapes;
        public readonly int DynamicShapes;
        public readonly int MaxBodyShapes;
        public int TotalShapes => StaticShapes + DynamicShapes;

        public StructureCapacityV2(int dynamicBodies, int staticShapes, int dynamicShapes, int maxBodyShapes)
        {
            DynamicBodies = dynamicBodies;
            StaticShapes = staticShapes;
            DynamicShapes = dynamicShapes;
            MaxBodyShapes = maxBodyShapes;
        }
    }
}
