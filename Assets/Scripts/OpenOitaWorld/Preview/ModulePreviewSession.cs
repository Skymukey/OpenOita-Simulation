using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Structure;
using UnityEngine;

namespace OpenOita.Preview
{
    // 独立模块观察器：复用真实状态/规则/结构服务，不实现 IWorld 或正式物理调度。
    // 预设把流体与动态体实验分开；不提供跨归属接触或任意旋转查询。
    internal sealed class ModulePreviewSession : IDisposable
    {
        internal const int Width = 256;
        internal const int Height = 256;
        internal const int LayoutScale = 8;
        private const int Capacity = 8192;
        private readonly WorkingWorld _world;
        private readonly IMaterialRuntimeTable _materials;
        private readonly RuleExecutionTable _rules = new RuleExecutionTable(Capacity);
        private readonly ConnectivityAnalyzer _structure = new ConnectivityAnalyzer(Capacity, 128);
        private readonly MaterialMutationPreparer _material;
        private readonly IContactQuery _contacts;
        private readonly List<TransactionCoordinator> _transactions = new List<TransactionCoordinator>();
        private IReadOnlyList<BodyGeometryPlan> _nextGeometry;
        private bool _disposed;
        internal int Scenario { get; }
        internal bool Faulted { get; private set; }
        internal string Error { get; private set; }
        internal ICommittedRenderView View => _world.Published;
        internal IReadOnlyList<BodyGeometryPlan> Geometry { get; private set; } = Array.Empty<BodyGeometryPlan>();
        internal int ChangedPositions => _world.ChangedPositions;
        // 仅记录最后一次成功 Step 已提交的水搬运，不把倒计时写入计为移动。
        internal int LastWaterMoves { get; private set; }
        internal Vector2Int ActionCell => LayoutCell(16, 12);
        internal Vector2Int AnchorCell => LayoutCell(Scenario == 2 ? 16 : 4, Scenario == 0 ? 2 : 12);

        internal static Vector2Int LayoutCell(int x, int y) => new Vector2Int(
            (Width - 32 * LayoutScale) / 2 + x * LayoutScale,
            (Height - 24 * LayoutScale) / 2 + y * LayoutScale);

        internal ModulePreviewSession(WorldSources baseline, int scenario, ulong generation)
        {
            if (scenario < 0 || scenario > 2) throw new ArgumentOutOfRangeException(nameof(scenario));
            Scenario = scenario;
            var loader = new WorldSourceLoader();
            WorldLoadResult source = loader.Load(baseline);
            Require(source.Result);
            _materials = source.Materials;
            _material = new MaterialMutationPreparer(_materials);
            Require(_rules.Validate(source.Rules));
            var limits = new WorldLimits(Capacity, 128, 256, 2048, Capacity * 4, 5, 180, 8, 16);
            var config = new WorldConfig(1, Width, Height, 128, 0.1f, 0.02f, -9.81f, 1, limits);
            SceneInitialData scene = BuildScene(scenario, _materials.MaterialSetId);
            Require(ConfigurationSerializer.Serialize(config, scene, _materials, out WorldSources sources));
            WorldLoadResult loaded = loader.Load(sources);
            Require(loaded.Result);
            Require(WorkingWorld.CreateInitial(loaded, Vector2.zero, generation, out _world));
            _contacts = new IsolatedOwnerPreviewContacts(_world);
            try
            {
                Apply(_world.PrepareInitial(), Context(TickStage.Structure), true);
                Publish();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static SceneInitialData BuildScene(int scenario, string setId)
        {
            var cells = new Dictionary<Vector2Int, ushort>();
            var fixedCells = new List<Vector2Int>();
            var burning = new List<Vector2Int>();
            void Put(int x, int y, ushort id) => cells[new Vector2Int(x, y)] = id;
            if (scenario == 0)
            {
                for (int x = 4; x <= 27; x++) Put(x, 2, 102);
                for (int y = 3; y <= 13; y++) { Put(4, y, 102); Put(27, y, 102); }
                fixedCells.Add(new Vector2Int(4, 2));
                for (int y = 17; y <= 20; y++) for (int x = 7; x <= 10; x++) Put(x, y, 101);
                for (int y = 4; y <= 5; y++) for (int x = 20; x <= 23; x++) Put(x, y, 103);
                for (int x = 7; x <= 12; x++) Put(x, 3, 104);
                burning.Add(new Vector2Int(8, 3));
            }
            else if (scenario == 1)
            {
                Put(4, 12, 102);
                fixedCells.Add(new Vector2Int(4, 12));
                for (int x = 5; x <= 26; x++) Put(x, 12, 104);
                for (int y = 10; y <= 14; y++) for (int x = 23; x <= 27; x++) Put(x, y, 104);
                burning.Add(new Vector2Int(16, 12));
            }
            else
            {
                for (int i = -6; i <= 6; i++) { Put(16 + i, 12, 104); Put(16, 12 + i, 104); }
                fixedCells.Add(new Vector2Int(16, 12));
            }
            var initial = new List<InitialCell>(cells.Count * LayoutScale * LayoutScale);
            foreach (var item in cells)
            {
                Vector2Int origin = LayoutCell(item.Key.x, item.Key.y);
                for (int y = 0; y < LayoutScale; y++)
                    for (int x = 0; x < LayoutScale; x++)
                        initial.Add(new InitialCell(origin.x + x, origin.y + y, item.Value));
            }
            List<Vector2Int> ExpandMarkers(List<Vector2Int> markers)
            {
                var expanded = new List<Vector2Int>(markers.Count * LayoutScale * LayoutScale);
                foreach (Vector2Int marker in markers)
                {
                    Vector2Int origin = LayoutCell(marker.x, marker.y);
                    for (int y = 0; y < LayoutScale; y++)
                        for (int x = 0; x < LayoutScale; x++) expanded.Add(origin + new Vector2Int(x, y));
                }
                return expanded;
            }
            return new SceneInitialData(1, setId, "materials.json", "world_config.json", initial,
                ExpandMarkers(fixedCells), ExpandMarkers(burning));
        }

        internal bool Step()
        {
            LastWaterMoves = 0;
            if (_disposed || Faulted) return false;
            try
            {
                Require(_world.BeginTick());
                CollectWet(TickStage.PreFlowContacts);
                int waterMoves = Run(_rules.Water, TickStage.Water, true);
                Run(_rules.Steam, TickStage.Steam, true);
                CollectWet(TickStage.Extinguish);
                ApplyBatch(_rules.WetContacts.Execute(_world, _materials, _world.Instances, _contacts, Context(TickStage.Extinguish)));
                Run(_rules.Burning, TickStage.Burning, false);
                Publish();
                LastWaterMoves = waterMoves;
                return true;
            }
            catch (Exception exception) { Freeze(exception); return false; }
        }

        // 调试按钮直接调用内部状态事务，不伪装为 M07A 命令令牌。
        internal bool Edit(CellKey key, bool ignite)
        {
            if (_disposed || Faulted || Scenario == 0) return false;
            WorldResult read = _world.Read(key, out CellSnapshot before);
            if (!read.IsSuccess || before.MaterialId == 0) return false;
            if (ignite && (!_materials.TryGet(before.MaterialId, out MaterialRuntimeEntry entry) ||
                !WetContactPolicy.CanIgnite(entry, before, false))) return false;
            try
            {
                Require(_world.BeginTick());
                CellSnapshot after = ignite ? WetContactPolicy.Ignite(before, _world.WorkingTick) : default;
                var context = Context(TickStage.Commands);
                Apply(_world.Prepare(new[] { new CellWrite(key, after) }, context), context, true);
                Publish();
                return true;
            }
            catch (Exception exception) { Freeze(exception); return false; }
        }

        internal bool EditAt(int x, int y, bool ignite)
        {
            foreach (CellKey key in View.OccupiedCells)
            {
                Vector2 position = CellOrigin(key, View);
                if (Mathf.RoundToInt(position.x) == x && Mathf.RoundToInt(position.y) == y)
                    return Edit(key, ignite);
            }
            return false;
        }

        internal bool RemoveRegion(Vector2Int min, int width, int height)
        {
            if (_disposed || Faulted || Scenario == 0 || width <= 0 || height <= 0) return false;
            var writes = new List<CellWrite>();
            foreach (CellKey key in View.OccupiedCells)
            {
                Vector2 point = CellOrigin(key, View);
                int x = Mathf.RoundToInt(point.x), y = Mathf.RoundToInt(point.y);
                if (x >= min.x && x < min.x + width && y >= min.y && y < min.y + height)
                    writes.Add(new CellWrite(key, default));
            }
            if (writes.Count == 0) return false;
            try
            {
                Require(_world.BeginTick());
                var context = Context(TickStage.Commands);
                Apply(_world.Prepare(writes.ToArray(), context), context, true);
                Publish();
                return true;
            }
            catch (Exception exception) { Freeze(exception); return false; }
        }

        private int Run(IRuleExecutor rule, TickStage stage, bool movement)
        {
            IRuleBatch batch = rule.Execute(_world, _materials, _world.Instances, _contacts, Context(stage));
            batch = movement ? _rules.Movement.Resolve(batch) : batch;
            int moved = 0;
            foreach (MutationIntent intent in batch.Intents)
                if (intent.Kind == MutationKind.Move) moved++;
            ApplyBatch(batch);
            return moved;
        }

        private void CollectWet(TickStage stage)
        {
            Require(_rules.WetContacts.Collect(_world, _materials, _world.Instances, _contacts, Context(stage)));
            foreach (CellInstanceHandle instance in _rules.WetContacts.WetInstances) _world.Instances.MarkWet(instance);
        }

        private void ApplyBatch(IRuleBatch batch)
        {
            Require(batch.Result);
            var context = Context(batch.Stage);
            var writes = new List<CellWrite>();
            var moves = new List<MutationIntent>();
            bool structureChanged = false;
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind == MutationKind.Move)
                {
                    // 先在阶段快照上统一裁决，再通过 M02 批次搬运保留实例身份。
                    // 仅适用于无动态体的流体预设，不是正式世界的批次调度实现。
                    if (_world.Bodies.Length != 0) throw new InvalidOperationException("流体预设不支持动态体占据，请重置。");
                    moves.Add(intent);
                }
                else
                {
                    writes.Add(new CellWrite(intent.Source, intent.State));
                    if (intent.Kind == MutationKind.Remove)
                    {
                        Require(_world.Read(intent.Source, out CellSnapshot old));
                        structureChanged |= _materials.TryGet(old.MaterialId, out MaterialRuntimeEntry entry) &&
                            (entry.Rules & RuleMask.Structure) != 0;
                    }
                }
            }
            // 链的内部格既是旧源又是新目标；统一先清源、后填目标，避免意图顺序造成丢水。
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Source, default));
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Target, move.State));
            if (writes.Count > 0) Apply(_world.Prepare(writes.ToArray(), context, moves: moves.ToArray()), context,
                structureChanged || _world.Bodies.Length != 0);
        }

        private void Apply(PreparationResult<IPreparedMutation> candidate, TransactionContext context, bool geometry)
        {
            Require(candidate.Result);
            IPreparedMutation edit = candidate.Prepared;
            TransactionCoordinator coordinator = null;
            try
            {
                if (geometry)
                {
                    StructurePlanResult plan = _world.PlanStructure(edit, _structure, context);
                    Require(plan.Result);
                    var material = _world.PrepareMaterial(edit, plan.Plan, _material, context);
                    Require(material.Result);
                    _nextGeometry = new List<BodyGeometryPlan>(material.Prepared.Geometry);
                }
                coordinator = new TransactionCoordinator();
                coordinator.Own(edit);
                Require(coordinator.ValidateAndApply(context, _world.Config.Limits, ContractDefaults.CpuBudgetBytes));
                _transactions.Add(coordinator);
            }
            catch
            {
                if (coordinator != null) coordinator.Dispose();
                else edit.Dispose();
                throw;
            }
        }

        private void Publish()
        {
            Require(_world.PublishState());
            foreach (TransactionCoordinator transaction in _transactions) transaction.MarkCommitted(_world.Published.Version);
            _transactions.Clear();
            if (_nextGeometry != null) Geometry = _nextGeometry;
            _nextGeometry = null;
        }

        private TransactionContext Context(TickStage stage) => new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        private void Freeze(Exception exception)
        {
            Faulted = true;
            Error = exception.Message;
            _world.Fault();
            foreach (TransactionCoordinator transaction in _transactions) transaction.Dispose();
            _transactions.Clear();
            _nextGeometry = null;
        }

        internal static Vector2 CellOrigin(CellKey key, ICommittedWorldView view)
        {
            Vector2 local = new Vector2(key.Position.X, key.Position.Y);
            if (key.Position.OwnerKind == OwnerKind.Grid) return local;
            foreach (BodySnapshot body in view.Bodies)
                if (body.BodyId == key.Position.BodyId)
                {
                    if (body.Pose.AngleRadians != 0) throw new InvalidOperationException("此模块预览只显示未旋转的材料体。");
                    return (body.Pose.Position - view.Origin) / view.Config.CellSize + local;
                }
            throw new InvalidOperationException("提交快照缺少材料体。");
        }

        private static void Require(WorldResult result)
        {
            if (!result.IsSuccess) throw new InvalidOperationException($"{result.ErrorCode} / {result.Diagnostic.Stage} / {result.Diagnostic.Target}：{result.Diagnostic.Message}");
        }

        // 预览的明确降级策略：各归属独立，只展示规则已有的同归属四邻接。
        // 不是 M06 接触实现，不对外提供几何分类，不用于正式 WorldSimulation。
        private sealed class IsolatedOwnerPreviewContacts : IContactQuery
        {
            public SpatialLease Lease => default;
            private readonly WorkingWorld _world;
            internal IsolatedOwnerPreviewContacts(WorkingWorld world) { _world = world; }
            public QueryResult QueryContacts(in CellKey cell, Span<CellContact> destination)
            {
                WorldResult valid = _world.Read(cell, out _);
                return new QueryResult(valid, _world.Published.Version, 0, 0);
            }
            public WorldResult Classify(in CellGeometry first, in CellGeometry second, float epsilon, out CellContact contact)
            {
                contact = default;
                return WorldResult.Failure(WorldErrorCode.UnsupportedOperation,
                    new WorldDiagnostic("模块预览", "接触", "此预览不实现跨归属几何接触。"));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (TransactionCoordinator transaction in _transactions) transaction.Dispose();
            _transactions.Clear();
            _world?.Dispose();
            Geometry = Array.Empty<BodyGeometryPlan>();
            _nextGeometry = null;
        }
    }
}
