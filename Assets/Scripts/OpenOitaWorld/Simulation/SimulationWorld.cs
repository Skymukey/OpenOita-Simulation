using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using OpenOita.Rules;
using OpenOita.Spatial;
using OpenOita.Commands;
using OpenOita.Queries;
using UnityEngine;

namespace OpenOita.Simulation
{
    // 正式IWorld封装；命令、查询、显示均由同一成功发布版本协调。
    internal sealed class SimulationWorld : IWorld
    {
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly WorldLoadResult _loaded;
        private readonly Vector2 _origin;
        private readonly Vector2 _worldMax;
        private readonly Func<IWorldRenderer> _rendererFactory;
        private readonly bool _freezeBodyRotation;
        private readonly CommandQueue _commands;
        private readonly QueryService _queries;
        private readonly MaterialCommandPlanner _planner;
        private readonly List<CommandResult> _current = new();
        private ulong _generation = 1;
        private bool _busy;
        private bool _notifying;
        private long _resultLease;
        private IWorldRenderer _renderer;
        internal WorldRuntime Runtime { get; private set; }
        public WorldLifecycle Lifecycle { get; private set; } = WorldLifecycle.Creating;
        public WorldVersion Version => Runtime?.View.Version ?? new WorldVersion(_generation, 0);
        public WorldConfig Config => _loaded.Config;
        public event Action<ChangeSet> Committed;
        internal SimulationWorld(WorldLoadResult loaded, Vector2 origin, Func<IWorldRenderer> rendererFactory, bool freezeBodyRotation = false)
        {
            _loaded = loaded; _origin = origin; _rendererFactory = rendererFactory;
            _freezeBodyRotation = freezeBodyRotation;
            // 存为可表示Vector2边界，避免Mono比较时保留扩展精度而误拒合法尾格。
            _worldMax = origin + new Vector2(loaded.Config.Width * loaded.Config.CellSize, loaded.Config.Height * loaded.Config.CellSize);
            _commands = new CommandQueue(this);
            _planner = new MaterialCommandPlanner(loaded.Materials, origin);
            _queries = new QueryService(loaded.Config.Limits.MaxMaterialCells);
        }

        internal WorldResult Initialize()
        {
            WorldResult result = WorldRuntime.Create(_loaded, _origin, _generation, out WorldRuntime runtime, freezeBodyRotation: _freezeBodyRotation);
            if (!result.IsSuccess) return result;
            Runtime = runtime;
            try
            {
                _renderer = _rendererFactory();
                if (_renderer == null) return Error(WorldErrorCode.NotReady, "renderer", "显示工厂没有返回服务。");
                result = _renderer.Prepare(Runtime.View);
                if (result.IsSuccess)
                {
                    Runtime.Renderer = _renderer as ITransactionalWorldRenderer;
                    Runtime.ExternalCpuBytes = _queries.ReservedBytes + 4096L * 256;
                    if (Runtime.ReservedCpuBytes + (_renderer is OpenOita.Render.CommittedWorldRenderer display ? display.ReservedCpuBytes : 0) > Runtime.CpuBudgetBytes)
                        return Error(WorldErrorCode.CapacityExceeded, "cpuBytes", "创建时模拟、查询、队列与显示保留资源合并超限。");
                    Lifecycle = WorldLifecycle.Ready;
                }
                return result;
            }
            catch (Exception exception) { return Error(WorldErrorCode.Faulted, "renderer", exception.Message); }
            finally { if (Lifecycle != WorldLifecycle.Ready) { _renderer?.Dispose(); _renderer = null; Runtime.Dispose(); Runtime = null; } }
        }
        private WorldResult Access(bool ready = true, bool mutation = false)
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread) return Error(WorldErrorCode.InvalidArgument, "thread", "操作必须在创建线程执行。");
            if (Lifecycle == WorldLifecycle.Disposed) return Error(WorldErrorCode.Disposed, "world", "世界已释放。");
            if (_busy && (mutation || !_notifying)) return Error(WorldErrorCode.Busy, "world", "当前正在推进或通知。");
            if (ready && Lifecycle != WorldLifecycle.Ready) return Error(Lifecycle == WorldLifecycle.Faulted ? WorldErrorCode.Faulted : WorldErrorCode.NotReady, "world", "世界不可执行普通材料操作。");
            return WorldResult.Success();
        }
        public EnqueueResult Enqueue(in MaterialCommand command)
        {
            WorldResult check = Access(); if (!check.IsSuccess) return new EnqueueResult(check);
            if (_busy && !_notifying) return new EnqueueResult(Error(WorldErrorCode.Busy, "commands", "推进期间不能重入。"));
            check = Validate(command); if (!check.IsSuccess) return new EnqueueResult(check);
            return _commands.Enqueue(command);
        }
        public CommandResult Retry(in CommandToken token)
        {
            WorldResult access = Access(false);
            return access.IsSuccess ? _commands.Retry(token, Version, Lifecycle == WorldLifecycle.Faulted) :
                new CommandResult(access, token, 0, Version);
        }
        public StepResult Step()
        {
            WorldResult access = Access(mutation: true); if (!access.IsSuccess) return new StepResult(access, Version, null);
            _busy = true; _current.Clear(); _resultLease++;
            try
            {
                WorldResult result = Runtime.BeginTick();
                if (result.IsSuccess)
                {
                    int count = _commands.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var item = _commands.Dequeue();
                        _current.Add(new CommandResult(WorldResult.Pending(), item.token, 0, Version));
                        try
                        {
                            WorldResult command = _planner.Execute(Runtime, item.command, item.token, out int affected);
                            _current[_current.Count - 1] = new CommandResult(command, item.token, affected, Version);
                        }
                        catch (Exception exception)
                        {
                            Runtime.Freeze(Error(WorldErrorCode.Faulted, "commands", exception.Message));
                        }
                        if (Runtime.Faulted) { result = Runtime.LastResult; break; }
                    }
                    if (result.IsSuccess) result = Runtime.CompleteTick();
                }
                if (!result.IsSuccess)
                {
                    Lifecycle = WorldLifecycle.Faulted; Runtime.Freeze(result);
                    WorldResult failed = Error(WorldErrorCode.Faulted, result.Diagnostic.Target, result.Diagnostic.Message);
                    for (int i = 0; i < _current.Count; i++) _current[i] = new CommandResult(failed, _current[i].Token, 0, Version);
                    while (_commands.Count != 0) _current.Add(new CommandResult(failed, _commands.Dequeue().token, 0, Version));
                }
                else
                {
                    for (int i = 0; i < _current.Count; i++) _current[i] = new CommandResult(_current[i].Result, _current[i].Token, _current[i].AffectedCount, Version);
                }
                foreach (CommandResult command in _current) _commands.Cache(command);
                if (result.IsSuccess)
                {
                    var changes = new ChangeSet(Version, Runtime.LastChanges);
                    Runtime.LastChanges.Open();
                    _notifying = true;
                    try { _renderer.OnCommitted(Runtime.View, changes); } catch (Exception exception) { Debug.LogWarning("OpenOita显示通知异常：" + exception.Message); }
                    if (Committed != null) foreach (Action<ChangeSet> subscriber in Committed.GetInvocationList())
                        try { subscriber(changes); } catch (Exception exception) { Debug.LogWarning("OpenOita提交通知异常：" + exception.Message); }
                    _notifying = false;
                    Runtime.LastChanges.Close();
                }
                return new StepResult(result, Version, new Results(this, _resultLease, _current.ToArray()));
            }
            finally { _notifying = false; _busy = false; }
        }
        private WorldResult Validate(in MaterialCommand command)
        {
            if (command.Generation != _generation) return Error(WorldErrorCode.StaleGeneration, "command", "命令代次过期。");
            if (command.Operation < MaterialOperation.Spawn || command.Operation > MaterialOperation.Ignite ||
                !ContractDefaults.IsFinite(command.Region.Min) || !ContractDefaults.IsFinite(command.Region.Max) ||
                command.Region.Min.x >= command.Region.Max.x || command.Region.Min.y >= command.Region.Max.y)
                return Error(WorldErrorCode.InvalidArgument, "region", "命令矩形须有限且有正面积。");
            WorldResult region = Region(command.Region);
            if (!region.IsSuccess) return WorldResult.Failure(region.ErrorCode,
                new WorldDiagnostic("Enqueue", command.Operation + " region", region.Diagnostic.Message));
            bool needsMaterial = command.Operation == MaterialOperation.Spawn || command.Operation == MaterialOperation.Replace;
            if (needsMaterial ? !_loaded.Materials.TryGet(command.MaterialId, out _) : command.MaterialId != 0)
                return Error(needsMaterial ? WorldErrorCode.UnknownMaterial : WorldErrorCode.InvalidArgument, "materialId", "命令材料参数无效。");
            return WorldResult.Success();
        }
        private WorldResult Point(Vector2 point)
        {
            if (!ContractDefaults.IsFinite(point)) return Error(WorldErrorCode.InvalidArgument, "point", "点必须有限。");
            return point.x >= _origin.x && point.y >= _origin.y && point.x <= _worldMax.x && point.y <= _worldMax.y
                ? WorldResult.Success() : Error(WorldErrorCode.OutOfBounds, "point", "查询超出世界。");
        }
        private WorldResult Region(WorldRect region)
        {
            WorldResult min = Point(region.Min); return min.IsSuccess ? Point(region.Max) : min;
        }
        public PointQueryResult QueryPoint(Vector2 point)
        {
            WorldResult result = Access(); if (result.IsSuccess) result = Point(point);
            if (result.IsSuccess && (point.x >= _worldMax.x || point.y >= _worldMax.y))
                result = Error(WorldErrorCode.OutOfBounds, "point", "点查询的世界上界不包含材料格。");
            if (!result.IsSuccess) return new PointQueryResult(result, Version);
            return _queries.Point(Runtime.View, point);
        }
        public QueryResult QueryRegion(in WorldRect region, Span<CellHit> destination)
        {
            WorldResult result = Access();
            if (result.IsSuccess && (!ContractDefaults.IsFinite(region.Min) || !ContractDefaults.IsFinite(region.Max) || region.Min.x >= region.Max.x || region.Min.y >= region.Max.y))
                result = Error(WorldErrorCode.InvalidArgument, "region", "区域须有限且有正面积。");
            if (result.IsSuccess) result = Region(region);
            if (!result.IsSuccess) return new QueryResult(result, Version, 0, 0);
            return _queries.Collect(Runtime.View, region, default, default, false, destination);
        }
        public QueryResult QuerySegment(Vector2 start, Vector2 end, Span<CellHit> destination)
        {
            WorldResult result = Access(); if (result.IsSuccess) result = Point(start); if (result.IsSuccess) result = Point(end);
            if (!result.IsSuccess) return new QueryResult(result, Version, 0, 0);
            return _queries.Collect(Runtime.View, default, start, end, true, destination);
        }
        public MaterialCountsResult QueryMaterialCounts()
        {
            WorldResult result = Access(false);
            if (result.IsSuccess && Runtime == null) result = Error(WorldErrorCode.NotReady, "counts", "没有已提交材料数量快照。");
            return result.IsSuccess ? Runtime.State.Published.MaterialCounts : new MaterialCountsResult(result, Version);
        }
        public WorldResult Reset()
        {
            WorldResult access = Access(false, true); if (!access.IsSuccess) return access;
            if (_generation == ulong.MaxValue) { Lifecycle = WorldLifecycle.Faulted; return Error(WorldErrorCode.CapacityExceeded, "generation", "代次耗尽。"); }
            _busy = true;
            try
            {
                Runtime?.Dispose(); Runtime = null;
                IWorldRenderer previousDisplay = _renderer; _renderer = null;
                _commands.Clear(); _current.Clear(); _generation++;
                _resultLease++;
                Lifecycle = WorldLifecycle.Creating;
                WorldResult result = Initialize();
                if (!result.IsSuccess) { Lifecycle = WorldLifecycle.Faulted; _renderer = previousDisplay; }
                else previousDisplay?.Dispose();
                return result;
            }
            finally { _busy = false; }
        }
        public WorldResult Dispose()
        {
            if (Lifecycle == WorldLifecycle.Disposed) return WorldResult.Success();
            WorldResult access = Access(false, true); if (!access.IsSuccess) return access;
            Runtime?.Dispose(); Runtime = null; _renderer?.Dispose(); _renderer = null;
            _commands.Clear(); _queries.Dispose(); Lifecycle = WorldLifecycle.Disposed;
            _current.Clear();
            _resultLease++;
            return WorldResult.Success();
        }
        internal void FlushFrame() { if (Lifecycle != WorldLifecycle.Disposed) _renderer?.FlushFrame(); }
        private static WorldResult Error(WorldErrorCode code, string target, string message) => WorldResult.Failure(code, new WorldDiagnostic("World", target, message));
        private sealed class Results : ICommandResultView
        {
            private readonly CommandResult[] _items;
            private readonly SimulationWorld _owner;
            private readonly long _lease;
            internal Results(SimulationWorld owner, long lease, CommandResult[] items) { _owner = owner; _lease = lease; _items = items; }
            private void Require()
            {
                if (_owner._resultLease != _lease || Thread.CurrentThread.ManagedThreadId != _owner._thread)
                    throw new InvalidOperationException("命令结果视图租约已失效。");
            }
            public int Count { get { Require(); return _items.Length; } }
            public CommandResult this[int index] { get { Require(); return _items[index]; } }
            public void CopyTo(Span<CommandResult> destination) { Require(); _items.AsSpan().CopyTo(destination); }
        }
    }
}
