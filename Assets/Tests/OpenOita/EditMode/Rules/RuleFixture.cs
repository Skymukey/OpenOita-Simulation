using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Rules
{
    // 明确的内存阶段/提交夹具；不进入正式 WorldSimulation，不能代替 M03-B/C。
    internal sealed class RuleFixture : IWorkingWorldView
    {
        internal readonly Dictionary<CellKey, CellSnapshot> Cells = new Dictionary<CellKey, CellSnapshot>();
        internal readonly IMaterialRuntimeTable Materials;
        internal readonly RuleExecutionTable Rules;
        internal TickInstanceMap Instances;
        internal IContactQuery Contacts;
        internal BodySnapshot[] BodyItems = Array.Empty<BodySnapshot>();
        private CellKey[] _keys = Array.Empty<CellKey>();
        private readonly LiquidChainValidation _chains;
        public WorldConfig Config { get; }
        public Vector2 Origin => Vector2.zero;
        public ulong Generation => 1;
        public ulong WorkingTick { get; private set; }
        public ReadOnlySpan<CellKey> OccupiedCells => _keys;
        public ReadOnlySpan<BodySnapshot> Bodies => BodyItems;
        internal int BurnedOut;
        internal int Expired;

        internal RuleFixture(uint seed = 1, int height = 256, Action<JObject> editMaterials = null,
            IOccupancyView occupancy = null, int capacity = 1024)
        {
            WorldSources sources = BaselineSources.Read();
            JObject material = BaselineSources.Parse(BaselineSources.ShortBurningMaterials(sources.MaterialsText));
            editMaterials?.Invoke(material);
            JObject world = BaselineSources.Parse(sources.WorldConfigText);
            world["seed"] = seed;
            world["height"] = height;
            JObject scene = BaselineSources.Parse(sources.SceneText);
            scene["cells"] = new JArray();
            scene["fixedCells"] = new JArray();
            scene["initialBurning"] = new JArray();
            WorldLoadResult loaded = new WorldSourceLoader().Load(new WorldSources(material.ToString(), world.ToString(), scene.ToString()));
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Config = loaded.Config;
            Materials = loaded.Materials;
            Rules = new RuleExecutionTable(capacity, occupancy);
            _chains = new LiquidChainValidation(capacity);
        }

        internal static CellKey Grid(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        internal static CellKey Body(ulong body, int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Body, body, x, y));
        internal void Put(int x, int y, ushort id, bool burning = false) => Put(Grid(x, y), id, burning);
        internal void Put(CellKey key, ushort id, bool burning = false)
        {
            Assert.That(Materials.TryGet(id, out MaterialRuntimeEntry entry), Is.True);
            Cells[key] = CellState.Create(entry, burning).Snapshot;
        }
        internal CellSnapshot State(int x, int y) => State(Grid(x, y));
        internal CellSnapshot State(CellKey key) => Cells.TryGetValue(key, out CellSnapshot state) ? state : default;
        internal void Begin(bool reverse = false)
        {
            Instances?.Close();
            WorkingTick++;
            Instances = new TickInstanceMap(Generation, WorkingTick);
            Refresh(reverse);
            foreach (CellKey key in _keys) Instances.Create(key);
        }
        internal void Refresh(bool reverse = false)
        {
            _keys = Cells.Keys.OrderBy(k => k.Position).ToArray();
            if (reverse) Array.Reverse(_keys);
        }
        internal TransactionContext Context(TickStage stage) => new TransactionContext(new WorldVersion(1, WorkingTick - 1), WorkingTick, stage);
        internal IRuleBatch Run(IRuleExecutor executor, TickStage stage) => executor.Execute(this, Materials, Instances, Contacts, Context(stage));
        internal void Apply(IRuleBatch batch)
        {
            Assert.That(batch.Result.IsSuccess, Is.True, batch.Result.Diagnostic.Message);
            _chains.Validate(this, Materials, batch);
            foreach (MutationIntent intent in batch.Intents)
            {
                Assert.That(Instances.TryResolve(intent.Instance, out CellKey key), Is.True);
                Assert.That(key, Is.EqualTo(intent.Source));
            }
            foreach (int index in _chains.TailFirstOrder)
            {
                MutationIntent intent = batch.Intents[index];
                Instances.Move(intent.Instance, intent.Target);
            }
            foreach (MutationIntent intent in batch.Intents)
                if (intent.Kind == MutationKind.Move) Cells.Remove(intent.Source);
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind == MutationKind.Move)
                {
                    Cells[intent.Target] = intent.State;
                    Assert.That(Instances.TryResolve(intent.Instance, out CellKey moved), Is.True);
                    Assert.That(moved, Is.EqualTo(intent.Target));
                }
                else if (intent.Kind == MutationKind.Remove)
                {
                    Cells.Remove(intent.Source);
                    Instances.Invalidate(intent.Instance);
                    if (intent.RemovalReason == RemovalReason.BurnedOut) BurnedOut++;
                    if (intent.RemovalReason == RemovalReason.LifetimeExpired) Expired++;
                }
                else Cells[intent.Target] = intent.State;
            }
            Refresh();
        }
        internal void Collect(TickStage stage)
        {
            WorldResult result = Rules.WetContacts.Collect(this, Materials, Instances, Contacts, Context(stage));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            foreach (CellInstanceHandle instance in Rules.WetContacts.WetInstances) Instances.MarkWet(instance);
        }
        internal void Water() => Apply(Rules.Movement.Resolve(Run(Rules.Water, TickStage.Water)));
        internal void Steam() => Apply(Rules.Movement.Resolve(Run(Rules.Steam, TickStage.Steam)));
        internal void Burn() => Apply(Run(Rules.Burning, TickStage.Burning));
        internal void Extinguish(TickStage stage = TickStage.Extinguish) => Apply(Rules.WetContacts.Execute(this, Materials, Instances, Contacts, Context(stage)));
        internal void Step()
        {
            Begin(); Collect(TickStage.PreFlowContacts); Water(); Steam(); Collect(TickStage.Extinguish); Extinguish(); Burn();
        }
        public WorldResult Read(in CellKey key, out CellSnapshot cell)
        {
            cell = default;
            if (key.Generation != Generation) return Error(WorldErrorCode.StaleGeneration);
            if (key.Position.OwnerKind == OwnerKind.Grid && (key.Position.X < 0 || key.Position.Y < 0 ||
                key.Position.X >= Config.Width || key.Position.Y >= Config.Height)) return Error(WorldErrorCode.OutOfBounds);
            Cells.TryGetValue(key, out cell);
            return WorldResult.Success();
        }
        public bool IsFixed(in CellKey key) => false;
        internal static WorldResult Error(WorldErrorCode code) => WorldResult.Failure(code, new WorldDiagnostic("Fixture", "cell", "内存夹具诊断。"));
    }

    internal sealed class ContactFixture : IContactQuery
    {
        public SpatialLease Lease => default;
        internal readonly List<CellContact> Items = new List<CellContact>();
        internal WorldVersion Version = new WorldVersion(1, 0);
        public WorldResult Classify(in CellGeometry first, in CellGeometry second, float epsilon, out CellContact contact)
        {
            contact = default;
            throw new NotSupportedException("夹具只提供预分类结果，不实现 M06 几何。");
        }
        public QueryResult QueryContacts(in CellKey cell, Span<CellContact> destination)
        {
            int count = 0;
            foreach (CellContact item in Items) if (item.First.Equals(cell) || item.Second.Equals(cell)) count++;
            if (count > destination.Length) return new QueryResult(RuleFixture.Error(WorldErrorCode.BufferTooSmall), Version, count, 0);
            int index = 0;
            foreach (CellContact item in Items)
                if (item.First.Equals(cell) || item.Second.Equals(cell)) destination[index++] = item;
            return new QueryResult(WorldResult.Success(), Version, count, count);
        }
    }

    internal sealed class OccupancyFixture : IOccupancyView
    {
        public SpatialLease Lease => default;
        public WorldVersion Version { get; set; } = new WorldVersion(1, 0);
        internal readonly HashSet<CellKey> Blocked = new HashSet<CellKey>();
        internal WorldResult Result = WorldResult.Success();
        public WorldResult HasSolidOverlap(in CellGeometry cell, out bool overlaps)
        {
            overlaps = Blocked.Contains(cell.Key);
            return Result;
        }
    }

    internal sealed class ReadOnlyInstances : ITickInstanceMap
    {
        private readonly ITickInstanceMap _inner;
        internal ReadOnlyInstances(ITickInstanceMap inner) { _inner = inner; }
        public bool TryGetInstance(in CellKey key, out CellInstanceHandle instance) => _inner.TryGetInstance(key, out instance);
        public bool TryResolve(in CellInstanceHandle instance, out CellKey key) => _inner.TryResolve(instance, out key);
        public bool IsWet(in CellInstanceHandle instance) => _inner.IsWet(instance);
        public void MarkWet(in CellInstanceHandle instance) => throw new AssertionException("规则不能标湿。");
        public void Move(in CellInstanceHandle instance, in CellKey target) => throw new AssertionException("规则不能移动实例。");
        public void Invalidate(in CellInstanceHandle instance) => throw new AssertionException("规则不能失效实例。");
        public CellInstanceHandle Create(in CellKey key) => throw new AssertionException("规则不能创建实例。");
    }
}
