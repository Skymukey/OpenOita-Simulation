using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Burst;
using OpenOita.Contracts;
using OpenOita.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Jobs;
using UnityEngine;

namespace OpenOita.V2
{
    public readonly struct MaterialCommitInfo
    {
        public readonly WorldVersion Version;
        public readonly int TotalCells, ChangedBlocks, BodyCount, BodyEvents;
        public readonly long RuleAttempts;
        public MaterialCommitInfo(WorldVersion version, int totalCells, int changedBlocks, int bodies, int bodyEvents, long ruleAttempts)
        { Version = version; TotalCells = totalCells; ChangedBlocks = changedBlocks; BodyCount = bodies; BodyEvents = bodyEvents; RuleAttempts = ruleAttempts; }
    }

    public struct MaterialTickMetrics
    {
        public double TotalMs, TimerMs, FlowMs, EventsMs, StructureMs, PhysicsMs;
        public long RuleAttempts, Moves, Sleeping;
        public int TileJobs;
    }

    public readonly struct MaterialReadLease
    {
        private readonly MaterialWorld _owner;
        private readonly long _epoch;
        public readonly WorldVersion Version;
        internal MaterialReadLease(MaterialWorld owner, long epoch) { _owner = owner; _epoch = epoch; Version = owner.Version; }
        public GridCell Read(int x, int y)
        {
            if (_owner == null || !_owner.IsReadEpoch(_epoch)) throw new InvalidOperationException("读取租约已经失效。");
            return _owner.Grid.Read(x, y);
        }
        public GridCell Read(int gridHandle, int x, int y)
        {
            RequireValid();
            MaterialGrid grid = _owner.FindGrid(gridHandle);
            if (grid == null) throw new ArgumentOutOfRangeException(nameof(gridHandle));
            return grid.Read(x, y);
        }
        public int BodyCount { get { RequireValid(); return _owner.Physics?.BodyCount ?? 0; } }
        public MaterialBodyState GetBody(int index)
        {
            RequireValid();
            if (_owner.Physics == null) throw new ArgumentOutOfRangeException(nameof(index));
            return new MaterialBodyState(_owner.Physics.GetBody(index));
        }
        public int ChangedBlockCount
        {
            get { RequireValid(); return _owner.ChangedBlockCount; }
        }
        public MaterialChangedBlock GetChangedBlock(int index)
        {
            RequireValid(); return _owner.GetChangedBlock(index);
        }
        public int BodyEventCount { get { RequireValid(); return _owner.BodyEventCount; } }
        public MaterialBodyEvent GetBodyEvent(int index) { RequireValid(); return _owner.GetBodyEvent(index); }
        private void RequireValid()
        {
            if (_owner == null || !_owner.IsReadEpoch(_epoch)) throw new InvalidOperationException("读取租约已经失效。");
        }
    }

    public readonly struct MaterialChangedBlock
    {
        public readonly int GridHandle, TileX, TileY;
        public MaterialChangedBlock(MaterialGrid grid, int tile)
        { GridHandle = grid.GridHandle; TileX = tile % grid.TileColumns; TileY = tile / grid.TileColumns; }
    }

    [Flags]
    public enum MaterialBodyChange : byte { Created = 1, Removed = 2, Pose = 4, Topology = 8 }
    public readonly struct MaterialBodyEvent
    {
        public readonly ulong BodyId;
        public readonly int GridHandle;
        public readonly MaterialBodyChange Change;
        public readonly BodyPose Pose;
        public MaterialBodyEvent(ulong id, int grid, MaterialBodyChange change, BodyPose pose)
        { BodyId = id; GridHandle = grid; Change = change; Pose = pose; }
    }
    public readonly struct MaterialBodyState
    {
        public readonly ulong BodyId;
        public readonly int GridHandle, Width, Height;
        public readonly BodyPose Pose;
        public readonly BodyMotion Motion;
        internal MaterialBodyState(BodyV2 body)
        { BodyId = body.Id; GridHandle = body.Grid.GridHandle; Width = body.Grid.Width; Height = body.Grid.Height; Pose = body.Pose; Motion = body.Motion; }
    }

    // 公共值类型沿用；运行态、规则和提交均为V2，不调用旧SimulationWorld/WorldRuntime。
    public sealed unsafe partial class MaterialWorld : IWorld, ICommandResultView, IWorldMaterialCatalog
    {
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly WorldLoadResult _loaded;
        private readonly Vector2 _origin;
        private readonly bool _withPhysics, _freezeRotation;
        private NativeArray<MaterialDefinition> _definitions;
        private TimedComponentStore _components;
        private LocalMaterialRules _rules;
        private MaterialQueries _queries;
        private MaterialWorldDisplay _display;
        private NativeList<int> _gasDue, _burnDue, _hostBurnDue, _mainBurnFallback;
        private NativeList<int> _mainIgnitionTiles, _mainVisualTiles, _mainSpreadSources, _changedTiles;
        private NativeList<int> _mainWetCandidates;
        private NativeList<int> _smokeSources;
        private NativeArray<byte> _mainVisualRegistered;
        private NativeArray<int> _timeWheelOverflow;
        private NativeArray<BodyFireEnvelope> _bodyFireEnvelopes;
        private NativeList<CellContact> _fireContacts;
        private NativeList<MaterialBodyEvent> _bodyEvents;
        private NativeParallelHashMap<ulong, BodyBefore> _bodyBefore;
        private struct BodyBefore { public int Grid; public BodyPose Pose; public byte Seen; }
        private struct BodyFireEnvelope
        {
            public Vector2 Center;
            public float ExtentX, ExtentY;
        }
        private readonly MaterialCommand[] _commands = new MaterialCommand[4096];
        private readonly MaterialCommand[] _preflightCommands = new MaterialCommand[4096];
        private readonly CommandToken[] _commandTokens = new CommandToken[4096];
        private readonly CommandResult[] _completed = new CommandResult[4096];
        private readonly CommandResult[] _current = new CommandResult[4096];
        private readonly MaterialCount[] _counts;
        private int _commandHead, _commandCount, _resultCount;
        private ulong _sequence, _generation = 1, _tick;
        private bool _busy, _notifying;
        private long _epoch;
        internal MaterialGrid Grid { get; private set; }
        internal MaterialPhysics Physics { get; private set; }
        public WorldConfig Config => _loaded.Config;
        public IMaterialRuntimeTable Materials => _loaded.Materials;
        public Vector2 Origin => _origin;
        public WorldVersion Version => new WorldVersion(_generation, _tick);
        public WorldLifecycle Lifecycle { get; private set; } = WorldLifecycle.Creating;
        public MaterialTickMetrics Metrics { get; private set; }
        public MaterialWorldDisplay Display => _display;
        public int BodyEventCount => _bodyEvents.Length;
        internal MaterialBodyEvent GetBodyEvent(int index) => _bodyEvents[index];
        public int ChangedBlockCount
        {
            get
            {
                int count = Grid.ChangedTiles.Length;
                if (Physics != null) for (int i = 0; i < Physics.BodyCount; i++) count += Physics.GetBody(i).Grid.ChangedTiles.Length;
                return count;
            }
        }
        internal MaterialChangedBlock GetChangedBlock(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (index < Grid.ChangedTiles.Length) return new MaterialChangedBlock(Grid, Grid.ChangedTiles[index]);
            index -= Grid.ChangedTiles.Length;
            if (Physics != null) for (int i = 0; i < Physics.BodyCount; i++)
            {
                MaterialGrid grid = Physics.GetBody(i).Grid;
                if (index < grid.ChangedTiles.Length) return new MaterialChangedBlock(grid, grid.ChangedTiles[index]);
                index -= grid.ChangedTiles.Length;
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        public event Action<MaterialCommitInfo> Published;
        // 不生成旧逐格历史差异；旧事件访问明确拒绝，避免静默兼容。
        event Action<ChangeSet> IWorld.Committed
        {
            add { throw new NotSupportedException("V2使用Published和读取租约，不生成全世界ChangeSet。"); }
            remove { }
        }
        public bool ParallelRules { get => _rules.Parallel; set => _rules.Parallel = value; }

        public MaterialWorld(WorldLoadResult loaded, Vector2 origin, bool physics = true, bool freezeRotation = false)
        {
            if (!loaded.Result.IsSuccess || loaded.Config.SchemaVersion != 2) throw new ArgumentException("V2需要有效schemaVersion=2配置。");
            _loaded = loaded; _origin = origin; _withPhysics = physics; _freezeRotation = freezeRotation;
            _counts = new MaterialCount[loaded.Materials.Count];
        }

        public WorldResult Initialize()
        {
            WorldResult access = Access(false);
            if (!access.IsSuccess) return access;
            if (Lifecycle != WorldLifecycle.Creating)
                return Error(WorldErrorCode.InvalidArgument, "initialize", "世界已创建；重新装载请使用Reset。");
            try
            {
                _definitions = MaterialDefinition.Build(_loaded.Materials);
                int coldCount = 0;
                foreach (InitialCell cell in _loaded.Scene.Cells)
                    if (_definitions[cell.MaterialId].IsGas || _definitions[cell.MaterialId].IsCorrosive) coldCount++;
                coldCount += _loaded.Scene.InitialBurning.Count;
                _components = new TimedComponentStore(Math.Max(1024, coldCount * 2));
                Grid = new MaterialGrid(Config, _definitions, _components) { GridHandle = 1 };
                _gasDue = new NativeList<int>(Math.Max(1024, coldCount * 2), Allocator.Persistent);
                _burnDue = new NativeList<int>(Math.Max(1024, coldCount * 2), Allocator.Persistent);
                _hostBurnDue = new NativeList<int>(Math.Max(1024, coldCount * 2), Allocator.Persistent);
                _mainBurnFallback = new NativeList<int>(Math.Max(1024, coldCount * 2), Allocator.Persistent);
                _mainIgnitionTiles = new NativeList<int>(Math.Max(256, Grid.Tiles.Length), Allocator.Persistent);
                _mainVisualTiles = new NativeList<int>(Math.Max(256, Grid.Tiles.Length), Allocator.Persistent);
                _mainSpreadSources = new NativeList<int>(Math.Max(1024, coldCount), Allocator.Persistent);
                _smokeSources = new NativeList<int>(Math.Max(1024, coldCount * 2), Allocator.Persistent);
                _mainVisualRegistered = new NativeArray<byte>(Grid.Tiles.Length, Allocator.Persistent);
                _timeWheelOverflow = new NativeArray<int>(1, Allocator.Persistent);
                _bodyFireEnvelopes = new NativeArray<BodyFireEnvelope>(
                    _withPhysics ? Math.Max(1, Config.Limits.MaxDynamicBodies) : 1, Allocator.Persistent);
                _changedTiles = new NativeList<int>(Grid.Tiles.Length, Allocator.Persistent);
                _mainWetCandidates = new NativeList<int>(checked(_components.Records.Capacity * 4), Allocator.Persistent);
                _fireContacts = new NativeList<CellContact>(4096, Allocator.Persistent);
                _bodyEvents = new NativeList<MaterialBodyEvent>(Math.Max(4, Config.Limits.MaxDynamicBodies * 2 + 2), Allocator.Persistent);
                _bodyBefore = new NativeParallelHashMap<ulong, BodyBefore>(Math.Max(4, Config.Limits.MaxDynamicBodies), Allocator.Persistent);
                var fixedCells = new HashSet<Vector2Int>(_loaded.Scene.FixedCells);
                var burning = new HashSet<Vector2Int>(_loaded.Scene.InitialBurning);
                foreach (InitialCell initial in _loaded.Scene.Cells)
                {
                    GridCell cell = Grid.CreateCell(initial.MaterialId, fixedCells.Contains(initial.Position), burning.Contains(initial.Position));
                    uint hash = math.hash(new uint3((uint)initial.Position.x, (uint)initial.Position.y, Config.Seed));
                    if ((hash & 1) == 0) cell.Flags |= GridCell.LeftFlag;
                    Grid.Write(initial.Position.x, initial.Position.y, cell);
                }
                _rules = new LocalMaterialRules(Grid);
                if (_withPhysics)
                {
                    Physics = new MaterialPhysics(Config, Grid, _origin, _freezeRotation);
                    Physics.Initialize(); Physics.RebuildStructures(0); Physics.RegisterBodyGridsForTimers();
                }
                _queries = new MaterialQueries(Grid, Physics, _origin, Config.CellSize, Config.Limits.MaxMaterialCells);
                PublishBodyEvents();
                Lifecycle = WorldLifecycle.Ready;
                return WorldResult.Success();
            }
            catch (Exception exception)
            {
                Lifecycle = WorldLifecycle.Faulted;
                return Error(WorldErrorCode.Faulted, "Create", exception.Message);
            }
        }

        private WorldResult Access(bool ready = true)
        {
            if (_thread != Thread.CurrentThread.ManagedThreadId) return Error(WorldErrorCode.InvalidArgument, "thread", "操作只能在创建线程执行。");
            if (Lifecycle == WorldLifecycle.Disposed) return Error(WorldErrorCode.Disposed, "world", "世界已释放。");
            if (_busy) return Error(WorldErrorCode.Busy, "world", "Tick正在推进。");
            if (ready && Lifecycle != WorldLifecycle.Ready) return Error(WorldErrorCode.Faulted, "world", "世界需要Reset。");
            return WorldResult.Success();
        }
        public bool IsReadEpoch(long epoch) => !_busy && Lifecycle == WorldLifecycle.Ready && _epoch == epoch;
        public MaterialReadLease AcquireReadLease()
        {
            WorldResult access = Access(); if (!access.IsSuccess) throw new InvalidOperationException(access.Diagnostic.Message);
            return new MaterialReadLease(this, _epoch);
        }
        public void AttachDisplay(Render.V2RenderingResources resources, int layer)
        {
            _display?.Dispose(); _display = new MaterialWorldDisplay(this, resources, layer);
        }

        public EnqueueResult Enqueue(in MaterialCommand command)
        {
            WorldResult access = Access(); if (!access.IsSuccess) return new EnqueueResult(access);
            if (_notifying) return new EnqueueResult(Error(WorldErrorCode.Busy, "callback", "发布回调内不能修改世界。"));
            WorldResult valid = ValidateCommand(command); if (!valid.IsSuccess) return new EnqueueResult(valid);
            if (_commandCount == _commands.Length) return new EnqueueResult(Error(WorldErrorCode.CapacityExceeded, "commands", "命令队列已满。"));
            return new EnqueueResult(WorldResult.Pending(), QueueCommand(command));
        }

        public WorldResult EnqueueBatch(ReadOnlySpan<MaterialCommand> commands, Span<CommandToken> tokens)
        {
            WorldResult access = Access(); if (!access.IsSuccess) return access;
            if (_notifying) return Error(WorldErrorCode.Busy, "callback", "发布回调内不能修改世界。");
            if (tokens.Length < commands.Length) return Error(WorldErrorCode.BufferTooSmall, "tokens", "令牌目标缓冲不足。");
            if (commands.Length > _commands.Length - _commandCount) return Error(WorldErrorCode.CapacityExceeded, "commands", "命令队列容量不足。");
            for (int i = 0; i < commands.Length; i++)
            {
                WorldResult valid = ValidateCommand(commands[i]); if (!valid.IsSuccess) return valid;
            }
            for (int i = 0; i < commands.Length; i++) tokens[i] = QueueCommand(commands[i]);
            return commands.Length == 0 ? WorldResult.Success() : WorldResult.Pending();
        }

        private WorldResult ValidateCommand(in MaterialCommand command)
        {
            if (command.Operation < MaterialOperation.Spawn || command.Operation > MaterialOperation.Ignite)
                return Error(WorldErrorCode.InvalidArgument, "operation", "未知编辑操作。");
            if (command.Generation != _generation) return Error(WorldErrorCode.StaleGeneration, "generation", "命令代次失效。");
            if (!ContractDefaults.IsFinite(command.Region.Min) || !ContractDefaults.IsFinite(command.Region.Max) || command.Region.Min.x >= command.Region.Max.x || command.Region.Min.y >= command.Region.Max.y)
                return Error(WorldErrorCode.InvalidArgument, "region", "区域须有限且非空。");
            if ((command.Operation == MaterialOperation.Spawn || command.Operation == MaterialOperation.Replace) && _definitions[command.MaterialId].Id == 0)
                return Error(WorldErrorCode.UnknownMaterial, "material", "材料未注册。");
            return WorldResult.Success();
        }
        private CommandToken QueueCommand(in MaterialCommand command)
        {
            var token = new CommandToken(this, _generation, ++_sequence);
            int slot = (_commandHead + _commandCount++) & 4095; _commands[slot] = command; _commandTokens[slot] = token;
            return token;
        }

        public CommandResult Retry(in CommandToken token)
        {
            WorldResult access = Access();
            if (!access.IsSuccess) return new CommandResult(access, token, 0, Version);
            if (!token.BelongsTo(this) || token.Generation != _generation || token.Sequence == 0 || token.Sequence > _sequence)
                return new CommandResult(Error(WorldErrorCode.InvalidToken, "token", "命令令牌失效。"), token, 0, Version);
            CommandResult result = _completed[(int)(token.Sequence & 4095)];
            if (result.Token.Equals(token)) return result;
            for (int i = 0; i < _commandCount; i++) if (_commandTokens[(_commandHead + i) & 4095].Equals(token)) return new CommandResult(WorldResult.Pending(), token, 0, Version);
            return new CommandResult(Error(WorldErrorCode.ResultExpired, "token", "命令结果已过期。"), token, 0, Version);
        }

        private void Bounds(WorldRect region, out int x0, out int y0, out int x1, out int y1)
        {
            double s = Config.CellSize;
            x0 = (int)Math.Max(0, Math.Min(Config.Width, Math.Ceiling(((double)region.Min.x - _origin.x) / s - 0.5)));
            y0 = (int)Math.Max(0, Math.Min(Config.Height, Math.Ceiling(((double)region.Min.y - _origin.y) / s - 0.5)));
            x1 = (int)Math.Max(0, Math.Min(Config.Width, Math.Ceiling(((double)region.Max.x - _origin.x) / s - 0.5)));
            y1 = (int)Math.Max(0, Math.Min(Config.Height, Math.Ceiling(((double)region.Max.y - _origin.y) / s - 0.5)));
        }

        private WorldResult ApplyCommand(MaterialCommand command, out int changed)
        {
            changed = 0; Bounds(command.Region, out int x0, out int y0, out int x1, out int y1);
            if (command.Operation == MaterialOperation.Spawn)
            {
                int requested = checked(Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0));
                if (TotalCells() + requested > Config.Limits.MaxMaterialCells) return Error(WorldErrorCode.CapacityExceeded, "cells", "材料数量超过配置容量。");
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    if (!Grid.Passable(x, y)) return Error(WorldErrorCode.Occupied, "region", "生成区域包含材料或动态固体。");
                _components.Reserve(requested); EnsureEventCapacity();
            }
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
            {
                GridCell cell = Grid.Read(x, y);
                if (command.Operation == MaterialOperation.Spawn || command.Operation == MaterialOperation.Replace && !cell.IsEmpty)
                {
                    GridCell next = Grid.CreateCell(command.MaterialId);
                    if ((math.hash(new uint3((uint)x, (uint)y, Config.Seed ^ (uint)_sequence)) & 1) == 0) next.Flags |= GridCell.LeftFlag;
                    Grid.Write(x, y, next); changed++;
                }
                else if (command.Operation == MaterialOperation.Remove && !cell.IsEmpty) { Grid.Write(x, y, default); changed++; }
                else if (command.Operation == MaterialOperation.Ignite && Ignite(Grid, x, y)) changed++;
            }
            if (Physics != null && command.Operation != MaterialOperation.Spawn)
            {
                for (int b = 0; b < Physics.BodyCount; b++)
                {
                    BodyV2 body = Physics.GetBody(b);
                    // 体内编辑属于显式命令路径，后续由局部查询范围筛选，不进入普通Tick。
                    for (int ti = 0; ti < body.Grid.AllocatedTileCount; ti++)
                    {
                        int id = body.Grid.GetAllocatedTileId(ti); MaterialTile tile = body.Grid.Tiles[id];
                        RectInt bounds = body.Grid.TileBounds(id);
                        for (int row = 0; row < 32; row++)
                        {
                            uint occupied = tile.Occupied[row];
                            while (occupied != 0)
                            {
                                int column = math.tzcnt(occupied); occupied &= occupied - 1;
                                int x = bounds.x + column, y = bounds.y + row;
                                float cosine = Mathf.Cos(body.Pose.AngleRadians), sine = Mathf.Sin(body.Pose.AngleRadians);
                                float px = (x + 0.5f) * Config.CellSize, py = (y + 0.5f) * Config.CellSize;
                                Vector2 center = body.Pose.Position + new Vector2(cosine * px - sine * py, sine * px + cosine * py);
                                if (!command.Region.ContainsCenter(center)) continue;
                                if (command.Operation == MaterialOperation.Remove) { body.Grid.Write(x, y, default); changed++; }
                                else if (command.Operation == MaterialOperation.Replace) { body.Grid.Write(x, y, body.Grid.CreateCell(command.MaterialId)); changed++; }
                                else if (command.Operation == MaterialOperation.Ignite && Ignite(body.Grid, x, y)) changed++;
                            }
                        }
                    }
                }
            }
            return WorldResult.Success();
        }

        internal MaterialGrid FindGrid(int handle)
        {
            if (handle == Grid.GridHandle) return Grid;
            return Physics != null && Physics.TryGetBodyByGridHandle(handle, out BodyV2 body) ? body.Grid : null;
        }

        private bool HasWaterNeighbour(MaterialGrid grid, int x, int y)
        {
            for (int direction = 0; direction < 4; direction++)
            {
                int nx = x + (direction == 1 ? -1 : direction == 2 ? 1 : 0);
                int ny = y + (direction == 0 ? -1 : direction == 3 ? 1 : 0);
                if (!grid.Inside(nx, ny)) continue;
                MaterialTile tile = grid.Tiles[grid.TileId(nx, ny)]; if (!tile.IsCreated) continue;
                if ((_definitions[tile.Material[MaterialGrid.Offset(nx, ny)]].Rules & RuleMask.ExtinguishesFire) != 0) return true;
            }
            return false;
        }

        private bool Ignite(MaterialGrid grid, int x, int y)
        {
            GridCell cell = grid.Read(x, y); MaterialDefinition definition = _definitions[cell.MaterialId];
            if (!definition.IsBurnable || cell.IsBurning || HasWaterNeighbour(grid, x, y) || cell.Cold.WetTick == Grid.Tick && Grid.Tick != 0) return false;
            uint remaining = cell.ComponentHandle == 0 ? definition.Fuel : cell.Cold.FuelRemaining;
            if (remaining == 0) return false;
            cell.Flags |= GridCell.BurningFlag; cell.Cold.FuelRemaining = remaining;
            cell.Cold.IgnitedTick = Grid.Tick; cell.Cold.BurnEndTick = Grid.Tick + remaining;
            cell.Cold.NextSmokeTick = definition.SmokeMaterialId != 0 ? Grid.Tick + definition.SmokeInterval : 0;
            cell.Cold.NextSpreadTick = Grid.Tick + definition.SpreadInterval;
            cell.Cold.NextVisualTick = Grid.Tick + Math.Max(1, definition.Fuel / 255);
            grid.Write(x, y, cell); Schedule(grid.Read(x, y).ComponentHandle); return true;
        }

        private void Schedule(int handle)
        {
            if (!_components.IsLive(handle)) return;
            CellCold cold = _components.Read(handle); MaterialDefinition definition = _definitions[cold.MaterialId];
            NativeTimeWheel wheel = _components.GetNativeTimeWheel();
            if (definition.IsGas) wheel.Schedule(handle, cold.ExpiryTick);
            else if (definition.IsCorrosive) wheel.Schedule(handle, cold.NextCorrosionTick);
            else
            {
                ulong due = Math.Min(cold.BurnEndTick, Math.Min(cold.NextSpreadTick, cold.NextVisualTick == 0 ? ulong.MaxValue : cold.NextVisualTick));
                if (cold.NextSmokeTick != 0) due = Math.Min(due, cold.NextSmokeTick);
                wheel.Schedule(handle, due);
            }
        }

        private void Extinguish(MaterialGrid grid, int x, int y, bool beforeBurn)
        {
            GridCell cell = grid.Read(x, y); if (!cell.IsBurning) return;
            bool wet = cell.Cold.WetTick == Grid.Tick || HasWaterNeighbour(grid, x, y);
            if (!wet) return;
            ulong consumedThrough = beforeBurn ? Grid.Tick - 1 : Grid.Tick;
            cell.Cold.FuelRemaining = (uint)Math.Min(uint.MaxValue, cell.Cold.BurnEndTick > consumedThrough ? cell.Cold.BurnEndTick - consumedThrough : 0);
            cell.Cold.WetTick = Grid.Tick; cell.Flags &= unchecked((byte)~GridCell.BurningFlag);
            _components.Unschedule(cell.ComponentHandle); grid.Write(x, y, cell);
        }

        private void CheckChangedWet(MaterialGrid grid, bool beforeBurn)
        {
            _changedTiles.Clear();
            if (_changedTiles.Capacity < grid.PendingStateTiles.Length) _changedTiles.Capacity = grid.PendingStateTiles.Length;
            _changedTiles.AddRange(grid.PendingStateTiles.AsArray()); grid.PendingStateTiles.Clear();
            if (grid.BurningCount == 0)
            {
                for (int ti = 0; ti < _changedTiles.Length; ti++)
                {
                    int id = _changedTiles[ti];
                    UnsafeUtility.MemClear(grid.Tiles[id].State, 128); grid.StateTiles[id] = 0;
                }
                return;
            }
            _mainWetCandidates.Clear();
            _timeWheelOverflow[0] = 0;
            new ChangedWetJob
            {
                Tiles = grid.Tiles, StateTiles = grid.StateTiles, Definitions = _definitions,
                ChangedTiles = _changedTiles.AsArray(), Candidates = _mainWetCandidates,
                Overflow = _timeWheelOverflow, Width = grid.Width, Height = grid.Height,
                Columns = grid.TileColumns
            }.Run();
            if (_timeWheelOverflow[0] != 0) throw new InvalidOperationException("局部湿接触缓冲超过容量。");
            for (int i = 0; i < _mainWetCandidates.Length; i++)
            {
                int handle = _mainWetCandidates[i];
                if (!_components.IsLive(handle)) continue;
                CellCold cold = _components.Read(handle);
                Extinguish(grid, cold.X, cold.Y, beforeBurn);
            }
        }

        [BurstCompile]
        private struct ChangedWetJob : IJob
        {
            public NativeArray<MaterialTile> Tiles;
            public NativeArray<int> StateTiles;
            [ReadOnly] public NativeArray<MaterialDefinition> Definitions;
            [ReadOnly] public NativeArray<int> ChangedTiles;
            public NativeList<int> Candidates;
            public NativeArray<int> Overflow;
            public int Width, Height, Columns;

            public void Execute()
            {
                for (int ti = 0; ti < ChangedTiles.Length; ti++)
                {
                    int id = ChangedTiles[ti]; StateTiles[id] = 0;
                    MaterialTile tile = Tiles[id];
                    int baseX = id % Columns * 32, baseY = id / Columns * 32;
                    for (int row = 0; row < 32; row++)
                    {
                        uint dirty = tile.State[row]; tile.State[row] = 0;
                        while (dirty != 0)
                        {
                            int column = math.tzcnt(dirty); dirty &= dirty - 1;
                            if ((Definitions[tile.Material[column + row * 32]].Rules & RuleMask.ExtinguishesFire) == 0) continue;
                            int x = baseX + column, y = baseY + row;
                            for (int d = 0; d < 4; d++)
                            {
                                int nx = x + (d == 1 ? -1 : d == 2 ? 1 : 0), ny = y + (d == 0 ? -1 : d == 3 ? 1 : 0);
                                if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                                MaterialTile near = Tiles[(ny >> 5) * Columns + (nx >> 5)];
                                int offset = (nx & 31) + (ny & 31) * 32;
                                if (!near.IsCreated || (near.Flags[offset] & GridCell.BurningFlag) == 0) continue;
                                if (Candidates.Length == Candidates.Capacity) { Overflow[0] = 1; continue; }
                                Candidates.AddNoResize(near.Component[offset]);
                            }
                        }
                    }
                }
            }
        }

        private void ExtinguishPhysicalContacts(bool beforeBurn)
        {
            if (Physics == null) return;
            NativeArray<int> wet = Physics.WetComponents;
            for (int i = 0; i < wet.Length; i++)
            {
                if (!_components.IsLive(wet[i])) continue;
                CellCold cold = _components.Read(wet[i]); MaterialGrid grid = FindGrid(cold.GridHandle);
                if (grid != null) Extinguish(grid, cold.X, cold.Y, beforeBurn);
            }
        }

        private void ExpireGas()
        {
            NativeTimeWheel wheel = _components.GetNativeTimeWheel();
            for (int i = 0; i < _gasDue.Length; i++)
            {
                int handle = _gasDue[i]; if (!_components.IsLive(handle)) continue;
                CellCold cold = _components.Read(handle); if (!_definitions[cold.MaterialId].IsGas) continue;
                if (cold.ExpiryTick > Grid.Tick) { wheel.Schedule(handle, cold.ExpiryTick); continue; }
                MaterialGrid grid = FindGrid(cold.GridHandle);
                if (grid != null && grid.Read(cold.X, cold.Y).ComponentHandle == handle) grid.Write(cold.X, cold.Y, default);
                else { Physics?.ExpireSuspended(handle); _components.Delete(handle); }
            }
        }

        private void ProcessBurnHandle(int handle)
        {
            if (!_components.IsLive(handle)) return;
            CellCold cold = _components.Read(handle); if (!_definitions[cold.MaterialId].IsBurnable) return;
            MaterialGrid grid = FindGrid(cold.GridHandle); if (grid == null) return;
            GridCell cell = grid.Read(cold.X, cold.Y); if (!cell.IsBurning || cell.ComponentHandle != handle) return;
            Extinguish(grid, cold.X, cold.Y, true); cell = grid.Read(cold.X, cold.Y); if (!cell.IsBurning) return;
            if (cold.IgnitedTick >= Grid.Tick) { Schedule(handle); return; }
            if (cold.BurnEndTick <= Grid.Tick) { grid.Write(cold.X, cold.Y, default); return; }
            MaterialDefinition definition = _definitions[cold.MaterialId];
            if (definition.SmokeMaterialId != 0 && cold.NextSmokeTick <= Grid.Tick)
            {
                _smokeSources.AddNoResize(handle);
                cold.NextSmokeTick = Grid.Tick + definition.SmokeInterval;
            }
            if (cold.NextSpreadTick <= Grid.Tick)
            {
                for (int direction = 0; direction < 4; direction++)
                {
                    int x = cold.X + (direction == 1 ? -1 : direction == 2 ? 1 : 0);
                    int y = cold.Y + (direction == 0 ? -1 : direction == 3 ? 1 : 0);
                    if (!grid.Inside(x, y)) continue;
                    MaterialTile tile = grid.Tiles[grid.TileId(x, y)]; if (!tile.IsCreated) continue;
                    int p = MaterialGrid.Offset(x, y);
                    if (_definitions[tile.Material[p]].IsBurnable && (tile.Flags[p] & GridCell.BurningFlag) == 0)
                        grid.RequestIgnition(x, y);
                }
                QueueCrossOwnerIgnition(cold, grid);
                cold.NextSpreadTick = Grid.Tick + definition.SpreadInterval;
            }
            if (cold.NextVisualTick <= Grid.Tick)
            { grid.MarkVisual(cold.X, cold.Y); cold.NextVisualTick = Grid.Tick + Math.Max(1, definition.Fuel / 255); }
            _components.Write(handle, cold); Schedule(handle);
        }

        private void QueueCrossOwnerIgnition(CellCold cold, MaterialGrid grid)
        {
            if (Physics == null || Physics.BodyCount == 0) return;
            ulong bodyId = 0; OwnerKind owner = OwnerKind.Grid;
            if (grid != Grid && Physics.TryGetBodyByGridHandle(grid.GridHandle, out BodyV2 body))
            { owner = OwnerKind.Body; bodyId = body.Id; }
            var source = new CellKey(_generation, new CellPositionKey(owner, bodyId, cold.X, cold.Y));
            _fireContacts.Clear(); Physics.CollectIgnitionTargets(source, _fireContacts);
            for (int c = 0; c < _fireContacts.Length; c++) Physics.QueueDeferredIgnition(_fireContacts[c].Second, Grid.Tick);
        }

        private void QueueCrossOwnerIgnition(int x, int y)
        {
            if (Physics == null || Physics.BodyCount == 0) return;
            var source = new CellKey(_generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
            _fireContacts.Clear(); Physics.CollectIgnitionTargets(source, _fireContacts);
            for (int c = 0; c < _fireContacts.Length; c++) Physics.QueueDeferredIgnition(_fireContacts[c].Second, Grid.Tick);
        }

        private void Burn()
        {
            _smokeSources.Clear();
            ProcessMainBurnJob(Grid.Tick);
            for (int i = 0; i < _mainBurnFallback.Length; i++) ProcessBurnHandle(_mainBurnFallback[i]);
            for (int i = 0; i < _hostBurnDue.Length; i++) ProcessBurnHandle(_hostBurnDue[i]);
            EmitSmoke();
            ApplyIgnitions(Grid);
            if (Physics != null) for (int i = 0; i < Physics.BodyCount; i++) ApplyIgnitions(Physics.GetBody(i).Grid);
        }

        private void ApplyIgnitions(MaterialGrid grid)
        {
            for (int ti = 0; ti < grid.PendingIgnitionTiles.Length; ti++)
            {
                int id = grid.PendingIgnitionTiles[ti];
                MaterialTile tile = grid.Tiles[id]; RectInt bounds = grid.TileBounds(id);
                for (int row = 0; row < 32; row++)
                {
                    uint mask = tile.Ignition[row]; tile.Ignition[row] = 0;
                    while (mask != 0) { int x = math.tzcnt(mask); mask &= mask - 1; Ignite(grid, bounds.x + x, bounds.y + row); }
                }
                grid.IgnitionTiles[id] = 0;
            }
            grid.PendingIgnitionTiles.Clear();
        }

        private void EnsureEventCapacity()
        {
            if (_gasDue.Capacity < _components.Records.Capacity) _gasDue.Capacity = _components.Records.Capacity;
            if (_burnDue.Capacity < _components.Records.Capacity) _burnDue.Capacity = _components.Records.Capacity;
            if (_hostBurnDue.Capacity < _components.Records.Capacity) _hostBurnDue.Capacity = _components.Records.Capacity;
            if (_mainBurnFallback.Capacity < _components.Records.Capacity) _mainBurnFallback.Capacity = _components.Records.Capacity;
            if (_mainSpreadSources.Capacity < _components.Records.Capacity) _mainSpreadSources.Capacity = _components.Records.Capacity;
            if (_smokeSources.Capacity < _components.Records.Capacity) _smokeSources.Capacity = _components.Records.Capacity;
            int wetCapacity = checked(_components.Records.Capacity * 4);
            if (_mainWetCandidates.Capacity < wetCapacity) _mainWetCandidates.Capacity = wetCapacity;
        }

        private void ProcessMainBurnJob(ulong tick)
        {
            _hostBurnDue.Clear(); _mainBurnFallback.Clear(); _mainIgnitionTiles.Clear();
            _mainVisualTiles.Clear(); _mainSpreadSources.Clear();
            RefreshBodyFireEnvelopes();
            var job = new MainBurnJob
            {
                BurnDue = _burnDue.AsArray(),
                Definitions = _definitions, Tiles = Grid.Tiles,
                IgnitionTiles = Grid.IgnitionTiles, VisualTiles = Grid.VisualTiles,
                VisualRegistered = _mainVisualRegistered,
                Wheel = _components.GetNativeTimeWheel(), Fallback = _mainBurnFallback,
                HostBurnDue = _hostBurnDue,
                SmokeSources = _smokeSources,
                IgnitionTileIds = _mainIgnitionTiles, VisualTileIds = _mainVisualTiles,
                SpreadSources = _mainSpreadSources, Width = Grid.Width, Height = Grid.Height,
                Columns = Grid.TileColumns, MainGridHandle = Grid.GridHandle,
                BodyEnvelopes = _bodyFireEnvelopes,
                BodyCount = Physics?.BodyCount ?? 0, CellSize = Config.CellSize,
                OriginX = _origin.x, OriginY = _origin.y,
                CollectCrossBody = Physics != null && Physics.BodyCount != 0, Tick = tick
            };
            job.Run();
            Grid.PendingIgnitionTiles.AddRange(_mainIgnitionTiles.AsArray());
            for (int i = 0; i < _mainVisualTiles.Length; i++)
            {
                int tileId = _mainVisualTiles[i];
                Grid.RegisterCommitTile(tileId);
                _mainVisualRegistered[tileId] = 0;
            }
            if (Physics != null && Physics.BodyCount != 0)
                for (int i = 0; i < _mainSpreadSources.Length; i++)
                {
                    int key = _mainSpreadSources[i];
                    QueueCrossOwnerIgnition(key % Grid.Width, key / Grid.Width);
                }
        }

        private void RefreshBodyFireEnvelopes()
        {
            if (Physics == null || !_bodyFireEnvelopes.IsCreated) return;
            float cell = Config.CellSize;
            for (int i = 0; i < Physics.BodyCount; i++)
            {
                BodyV2 body = Physics.GetBody(i);
                float localHalfWidth = body.Grid.Width * cell * 0.5f;
                float localHalfHeight = body.Grid.Height * cell * 0.5f;
                float cosineSigned = Mathf.Cos(body.Pose.AngleRadians);
                float sineSigned = Mathf.Sin(body.Pose.AngleRadians);
                float cosine = Mathf.Abs(cosineSigned);
                float sine = Mathf.Abs(sineSigned);
                _bodyFireEnvelopes[i] = new BodyFireEnvelope
                {
                    // BodyPose is the local-grid origin; move to the rotated
                    // grid center before applying the conservative half extents.
                    Center = body.Pose.Position + new Vector2(
                        cosineSigned * localHalfWidth - sineSigned * localHalfHeight,
                        sineSigned * localHalfWidth + cosineSigned * localHalfHeight),
                    ExtentX = cosine * localHalfWidth + sine * localHalfHeight + cell,
                    ExtentY = sine * localHalfWidth + cosine * localHalfHeight + cell
                };
            }
        }

        [BurstCompile]
        private unsafe struct MainBurnJob : IJob
        {
            [ReadOnly] public NativeArray<int> BurnDue;
            public NativeTimeWheel Wheel;
            public NativeArray<MaterialDefinition> Definitions;
            [NativeDisableParallelForRestriction] public NativeArray<MaterialTile> Tiles;
            [NativeDisableParallelForRestriction] public NativeArray<int> IgnitionTiles;
            [NativeDisableParallelForRestriction] public NativeArray<int> VisualTiles;
            public NativeArray<byte> VisualRegistered;
            public NativeList<int> Fallback;
            public NativeList<int> HostBurnDue;
            public NativeList<int> SmokeSources;
            public NativeList<int> IgnitionTileIds;
            public NativeList<int> VisualTileIds;
            public NativeList<int> SpreadSources;
            public int Width, Height, Columns, MainGridHandle;
            [ReadOnly] public NativeArray<BodyFireEnvelope> BodyEnvelopes;
            public int BodyCount;
            public float CellSize, OriginX, OriginY;
            public bool CollectCrossBody;
            public ulong Tick;

            private bool Inside(int x, int y) => (uint)x < Width && (uint)y < Height;
            private int TileId(int x, int y) => (x >> 5) + (y >> 5) * Columns;
            private int Offset(int x, int y) => (x & 31) + (y & 31) * 32;

            private bool NearBody(int x, int y)
            {
                float px = OriginX + (x + 0.5f) * CellSize;
                float py = OriginY + (y + 0.5f) * CellSize;
                for (int i = 0; i < BodyCount; i++)
                {
                    BodyFireEnvelope envelope = BodyEnvelopes[i];
                    if (math.abs(px - envelope.Center.x) <= envelope.ExtentX &&
                        math.abs(py - envelope.Center.y) <= envelope.ExtentY) return true;
                }
                return false;
            }

            private bool HasWaterNeighbour(int x, int y)
            {
                for (int direction = 0; direction < 4; direction++)
                {
                    int nx = x + (direction == 1 ? -1 : direction == 2 ? 1 : 0);
                    int ny = y + (direction == 0 ? -1 : direction == 3 ? 1 : 0);
                    if (!Inside(nx, ny)) continue;
                    MaterialTile near = Tiles[TileId(nx, ny)];
                    if (!near.IsCreated) continue;
                    ushort material = near.Material[Offset(nx, ny)];
                    if ((Definitions[material].Rules & RuleMask.ExtinguishesFire) != 0) return true;
                }
                return false;
            }

            private void RequestIgnition(int x, int y)
            {
                if (!Inside(x, y)) return;
                int tileId = TileId(x, y); MaterialTile tile = Tiles[tileId];
                if (!tile.IsCreated) return;
                int position = Offset(x, y); ushort material = tile.Material[position];
                MaterialDefinition definition = Definitions[material];
                uint bit = 1u << (x & 31); int row = y & 31;
                if (!definition.IsBurnable || (tile.Flags[position] & GridCell.BurningFlag) != 0) return;
                tile.Ignition[row] |= bit;
                if (IgnitionTiles[tileId] == 0)
                {
                    IgnitionTiles[tileId] = 1;
                    IgnitionTileIds.AddNoResize(tileId);
                }
            }

            private void MarkVisual(int x, int y)
            {
                int tileId = TileId(x, y); MaterialTile tile = Tiles[tileId];
                uint bit = 1u << (x & 31); tile.Visual[y & 31] |= bit; VisualTiles[tileId] = 1;
                if (VisualRegistered[tileId] == 0)
                {
                    VisualRegistered[tileId] = 1;
                    VisualTileIds.AddNoResize(tileId);
                }
            }

            private void ScheduleNext(int handle, in CellCold state)
            {
                ulong due = state.BurnEndTick;
                if (state.NextSpreadTick < due) due = state.NextSpreadTick;
                if (state.NextVisualTick != 0 && state.NextVisualTick < due) due = state.NextVisualTick;
                if (state.NextSmokeTick != 0 && state.NextSmokeTick < due) due = state.NextSmokeTick;
                Wheel.Schedule(handle, due);
            }

            public void Execute()
            {
                for (int i = 0; i < BurnDue.Length; i++)
                {
                    int handle = BurnDue[i];
                    if (handle <= 0 || handle > Wheel.Records.Length) continue;
                    int componentIndex = handle - 1; CellCold state = Wheel.Records[componentIndex];
                    if (state.MaterialId == 0) continue;
                    if (state.GridHandle != MainGridHandle)
                    {
                        if (Definitions[state.MaterialId].IsBurnable) HostBurnDue.AddNoResize(handle);
                        continue;
                    }
                    if (!Inside(state.X, state.Y)) continue;
                    int tileId = TileId(state.X, state.Y), position = Offset(state.X, state.Y);
                    MaterialTile tile = Tiles[tileId];
                    if (!tile.IsCreated || tile.Component[position] != handle ||
                        (tile.Flags[position] & GridCell.BurningFlag) == 0) continue;
                    MaterialDefinition definition = Definitions[state.MaterialId];
                    if (state.WetTick == Tick || HasWaterNeighbour(state.X, state.Y) || state.BurnEndTick <= Tick)
                    {
                        Fallback.AddNoResize(handle);
                        continue;
                    }
                    if (state.IgnitedTick >= Tick)
                    {
                        ScheduleNext(handle, state);
                        continue;
                    }
                    if (definition.SmokeMaterialId != 0 && state.NextSmokeTick <= Tick)
                    {
                        SmokeSources.AddNoResize(handle);
                        state.NextSmokeTick = Tick + definition.SmokeInterval;
                    }
                    if (state.NextSpreadTick <= Tick)
                    {
                        RequestIgnition(state.X - 1, state.Y); RequestIgnition(state.X + 1, state.Y);
                        RequestIgnition(state.X, state.Y - 1); RequestIgnition(state.X, state.Y + 1);
                        state.NextSpreadTick = Tick + definition.SpreadInterval;
                        if (CollectCrossBody && NearBody(state.X, state.Y))
                            SpreadSources.AddNoResize(state.X + state.Y * Width);
                    }
                    if (state.NextVisualTick <= Tick)
                    {
                        uint interval = definition.Fuel / 255u; if (interval == 0) interval = 1;
                        state.NextVisualTick = Tick + interval;
                        MarkVisual(state.X, state.Y);
                    }
                    Wheel.Records[componentIndex] = state;
                    ScheduleNext(handle, state);
                }
            }
        }

        private void CaptureBodyState()
        {
            _bodyBefore.Clear(); _bodyEvents.Clear();
            if (Physics == null) return;
            for (int i = 0; i < Physics.BodyCount; i++)
            {
                BodyV2 body = Physics.GetBody(i);
                _bodyBefore.Add(body.Id, new BodyBefore { Grid = body.Grid.GridHandle, Pose = body.Pose });
            }
        }

        private void PublishBodyEvents()
        {
            if (Physics == null) return;
            for (int i = 0; i < Physics.BodyCount; i++)
            {
                BodyV2 body = Physics.GetBody(i);
                MaterialBodyChange change = 0;
                if (!_bodyBefore.TryGetValue(body.Id, out BodyBefore old)) change = MaterialBodyChange.Created;
                else
                {
                    if (old.Grid != body.Grid.GridHandle || body.Grid.ChangedTiles.Length != 0) change |= MaterialBodyChange.Topology;
                    if (old.Pose.Position.x != body.Pose.Position.x || old.Pose.Position.y != body.Pose.Position.y ||
                        old.Pose.AngleRadians != body.Pose.AngleRadians) change |= MaterialBodyChange.Pose;
                    old.Seen = 1; _bodyBefore[body.Id] = old;
                }
                if (change != 0) _bodyEvents.Add(new MaterialBodyEvent(body.Id, body.Grid.GridHandle, change, body.Pose));
            }
            foreach (var pair in _bodyBefore)
                if (pair.Value.Seen == 0) _bodyEvents.Add(new MaterialBodyEvent(pair.Key, pair.Value.Grid, MaterialBodyChange.Removed, pair.Value.Pose));
        }

        public StepResult Step()
        {
            WorldResult access = Access(); if (!access.IsSuccess) return new StepResult(access, Version, null);
            if (_notifying) return new StepResult(Error(WorldErrorCode.Busy, "callback", "发布回调内不能推进世界。"), Version, null);
            _busy = true; _epoch++; _resultCount = 0; ulong nextTick = _tick + 1; Grid.Tick = nextTick;
            MaterialTickMetrics metrics = default; long start = Stopwatch.GetTimestamp();
            try
            {
                Grid.BeginCommit();
                CaptureBodyState();
                if (Physics != null) for (int i = 0; i < Physics.BodyCount; i++)
                { MaterialGrid bodyGrid = Physics.GetBody(i).Grid; bodyGrid.Tick = nextTick; bodyGrid.BeginCommit(); }
                int commands = _commandCount;
                WorldResult batch = WorldResult.Success();
                if (Physics != null && commands != 0)
                {
                    for (int i = 0; i < commands; i++) _preflightCommands[i] = _commands[(_commandHead + i) & 4095];
                    batch = Physics.PreflightCommands(_preflightCommands.AsSpan(0, commands), out _);
                }
                for (int i = 0; i < commands; i++)
                {
                    int slot = _commandHead; _commandHead = (_commandHead + 1) & 4095; _commandCount--;
                    int changed = 0;
                    WorldResult command = batch.IsSuccess ? ApplyCommand(_commands[slot], out changed) : batch;
                    CommandToken token = _commandTokens[slot]; var result = new CommandResult(command, token, changed, new WorldVersion(_generation, nextTick));
                    _current[_resultCount++] = result; _completed[(int)(token.Sequence & 4095)] = result;
                }
                long phase = Stopwatch.GetTimestamp();
                EnsureEventCapacity();
                _timeWheelOverflow[0] = 0;
                var timeWheelJob = new NativeTimeWheelAdvanceJob
                {
                    Wheel = _components.GetNativeTimeWheel(), Definitions = _definitions,
                    GasDue = _gasDue, BurningDue = _burnDue,
                    Overflow = _timeWheelOverflow, Tick = nextTick
                };
                timeWheelJob.Run();
                if (_timeWheelOverflow[0] != 0) throw new InvalidOperationException("原生时间轮事件缓冲超过容量。");
                _components.CommitNativeTick(nextTick);
                metrics.TimerMs = Milliseconds(phase);
                ProcessCorrosion();
                CheckChangedWet(Grid, true); Physics?.CollectWet(nextTick, true);
                ExtinguishPhysicalContacts(true);
                phase = Stopwatch.GetTimestamp();
                _rules.Execute(nextTick, false); RuleWork water = _rules.LastWork;
                metrics.FlowMs = Milliseconds(phase);
                phase = Stopwatch.GetTimestamp();
                ExpireGas();
                metrics.TimerMs += Milliseconds(phase);
                phase = Stopwatch.GetTimestamp();
                _rules.Execute(nextTick, true); RuleWork gas = _rules.LastWork;
                metrics.FlowMs += Milliseconds(phase); metrics.RuleAttempts = water.Attempts + gas.Attempts;
                metrics.Moves = water.Moves + gas.Moves; metrics.Sleeping = water.Sleeping + gas.Sleeping;
                metrics.TileJobs = (int)(water.TileJobs + gas.TileJobs);
                phase = Stopwatch.GetTimestamp(); CheckChangedWet(Grid, true); Burn(); metrics.EventsMs = Milliseconds(phase);
                phase = Stopwatch.GetTimestamp(); Physics?.RebuildStructures(nextTick); Physics?.RegisterBodyGridsForTimers(); metrics.StructureMs = Milliseconds(phase);
                phase = Stopwatch.GetTimestamp(); Physics?.Step(nextTick); Physics?.CollectWet(nextTick, false);
                if (Physics != null)
                {
                    ExtinguishPhysicalContacts(false);
                    for (int t = 0; t < Grid.ChangedTiles.Length; t++) Physics.WakeSuspendedTile(Grid.ChangedTiles[t]);
                    Physics.RestoreFluids(nextTick, 4096);
                }
                metrics.PhysicsMs = Milliseconds(phase);
                PublishBodyEvents();
                _tick = nextTick; Lifecycle = WorldLifecycle.Ready;
                metrics.TotalMs = Milliseconds(start); Metrics = metrics;
                _busy = false;
                _notifying = true;
                Published?.Invoke(new MaterialCommitInfo(Version, TotalCells(), ChangedBlockCount, Physics?.BodyCount ?? 0, BodyEventCount, metrics.RuleAttempts));
                return new StepResult(WorldResult.Success(), Version, this);
            }
            catch (Exception exception)
            {
                Lifecycle = WorldLifecycle.Faulted; metrics.TotalMs = Milliseconds(start); Metrics = metrics;
                return new StepResult(Error(WorldErrorCode.Faulted, "Tick", exception.Message), Version, this);
            }
            finally { _busy = false; _notifying = false; }
        }

        private static double Milliseconds(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        private int TotalCells()
        {
            int count = Grid.CellCount;
            if (Physics != null) { for (int i = 0; i < Physics.BodyCount; i++) count += Physics.GetBody(i).Grid.CellCount; count += Physics.SuspendedCount; }
            return count;
        }

        public PointQueryResult QueryPoint(Vector2 point)
        { WorldResult access = Access(); return access.IsSuccess ? _queries.QueryPoint(Version, point) : new PointQueryResult(access, Version); }
        public QueryResult QueryRegion(in WorldRect region, Span<CellHit> destination)
        { WorldResult access = Access(); return access.IsSuccess ? _queries.QueryRegion(Version, region, destination) : new QueryResult(access, Version, 0, 0); }
        public QueryResult QuerySegment(Vector2 start, Vector2 end, Span<CellHit> destination)
        { WorldResult access = Access(); return access.IsSuccess ? _queries.QuerySegment(Version, start, end, destination) : new QueryResult(access, Version, 0, 0); }
        public MaterialCountsResult QueryMaterialCounts()
        {
            WorldResult access = Access(); if (!access.IsSuccess) return new MaterialCountsResult(access, Version);
            int gridCount = 0, bodies = 0, water = 0, gas = 0;
            for (int index = 0; index < _counts.Length; index++)
            {
                ushort id = _loaded.Materials.GetByCompactIndex((ushort)(index + 1)).Id;
                int grid = Grid.Counts[id], body = 0, suspended = 0;
                if (Physics != null)
                {
                    for (int b = 0; b < Physics.BodyCount; b++) body += Physics.GetBody(b).Grid.Counts[id];
                    for (int s = 0; s < Physics.SuspendedCount; s++) if (Physics.GetSuspended(s).Cell.MaterialId == id) suspended++;
                }
                _counts[index] = new MaterialCount(id, grid, body, suspended); gridCount += grid; bodies += body;
                if (_definitions[id].IsGas) gas += suspended; else water += suspended;
            }
            return new MaterialCountsResult(WorldResult.Success(), Version, _counts, gridCount, bodies, water, gas);
        }

        public WorldResult Reset()
        {
            WorldResult access = Access(false); if (!access.IsSuccess) return access;
            if (_notifying) return Error(WorldErrorCode.Busy, "callback", "发布回调内不能Reset。");
            ReleaseRuntime(); _generation++; _tick = 0; _epoch++; _commandHead = _commandCount = _resultCount = 0;
            Array.Clear(_completed, 0, _completed.Length); Lifecycle = WorldLifecycle.Creating;
            WorldResult result = Initialize(); if (result.IsSuccess) _display?.ResetDisplay(); return result;
        }
        public WorldResult Dispose()
        {
            if (_thread != Thread.CurrentThread.ManagedThreadId)
                return Error(WorldErrorCode.InvalidArgument, "thread", "操作只能在创建线程执行。");
            if (Lifecycle == WorldLifecycle.Disposed) return WorldResult.Success();
            if (_busy || _notifying) return Error(WorldErrorCode.Busy, "dispose", "Tick或发布回调执行中不能释放。");
            _display?.Dispose(); _display = null;
            ReleaseRuntime(); Lifecycle = WorldLifecycle.Disposed; _epoch++; return WorldResult.Success();
        }
        private void ReleaseRuntime()
        {
            _queries?.Dispose(); _queries = null; _rules?.Dispose(); _rules = null;
            Physics?.Dispose(); Physics = null; Grid?.Dispose(); Grid = null; _components?.Dispose(); _components = null;
            if (_definitions.IsCreated) _definitions.Dispose();
            if (_gasDue.IsCreated) _gasDue.Dispose(); if (_burnDue.IsCreated) _burnDue.Dispose();
            if (_hostBurnDue.IsCreated) _hostBurnDue.Dispose(); if (_mainBurnFallback.IsCreated) _mainBurnFallback.Dispose();
            if (_mainIgnitionTiles.IsCreated) _mainIgnitionTiles.Dispose();
            if (_mainVisualTiles.IsCreated) _mainVisualTiles.Dispose(); if (_mainSpreadSources.IsCreated) _mainSpreadSources.Dispose();
            if (_mainVisualRegistered.IsCreated) _mainVisualRegistered.Dispose();
            if (_timeWheelOverflow.IsCreated) _timeWheelOverflow.Dispose();
            if (_bodyFireEnvelopes.IsCreated) _bodyFireEnvelopes.Dispose();
            if (_changedTiles.IsCreated) _changedTiles.Dispose();
            if (_mainWetCandidates.IsCreated) _mainWetCandidates.Dispose();
            if (_smokeSources.IsCreated) _smokeSources.Dispose();
            if (_fireContacts.IsCreated) _fireContacts.Dispose();
            if (_bodyEvents.IsCreated) _bodyEvents.Dispose();
            if (_bodyBefore.IsCreated) _bodyBefore.Dispose();
        }
        private static WorldResult Error(WorldErrorCode code, string target, string message) => WorldResult.Failure(code, new WorldDiagnostic("V2", target, message));
        int ICommandResultView.Count => _resultCount;
        CommandResult ICommandResultView.this[int index] => (uint)index < _resultCount ? _current[index] : throw new ArgumentOutOfRangeException(nameof(index));
        void ICommandResultView.CopyTo(Span<CommandResult> destination)
        { if (destination.Length < _resultCount) throw new ArgumentException("结果缓冲不足。"); _current.AsSpan(0, _resultCount).CopyTo(destination); }
    }
}
