using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Bodies
{
    // 只读离线候选。身份登记及预留只用于测试，不接入正式 Create/Step。
    internal sealed class BodyFixture : IWorkingWorldView
    {
        internal readonly Dictionary<CellKey, CellSnapshot> Cells = new Dictionary<CellKey, CellSnapshot>();
        internal readonly HashSet<CellKey> Fixed = new HashSet<CellKey>();
        internal readonly TickInstanceMap Instances = new TickInstanceMap(1, 1);
        internal readonly TickChangeCounter Changes = new TickChangeCounter(65536);
        internal readonly IMaterialRuntimeTable Materials;
        internal CellKey[] Keys = Array.Empty<CellKey>();
        internal BodySnapshot[] BodyItems = Array.Empty<BodySnapshot>();
        internal bool Closed;
        public WorldConfig Config { get; }
        public Vector2 Origin { get; set; }
        public ulong Generation => 1;
        public ulong WorkingTick => 1;
        public ReadOnlySpan<CellKey> OccupiedCells => Closed ? throw new InvalidOperationException("候选租约已关闭。") : Keys;
        public ReadOnlySpan<BodySnapshot> Bodies => BodyItems;
        internal TransactionContext Context(TickStage stage = TickStage.Commands) => new TransactionContext(new WorldVersion(1, 0), 1, stage);

        internal BodyFixture(int maxBodies = 64, int maxBodyShapes = 256, int maxShapes = 4096, int maxChanges = 65536)
        {
            var loaded = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Materials = loaded.Materials;
            Config = new WorldConfig(1, 256, 256, 128, 0.1f, 0.02f, -9.81f, 1,
                new WorldLimits(65536, maxBodies, maxBodyShapes, maxShapes, maxChanges, 5, 180, 8, 16));
        }
        internal static CellKey Grid(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        internal static CellKey Body(ulong id, int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Body, id, x, y));
        internal void Put(CellKey key, ushort materialId = 104, bool fixedCell = false, CellSnapshot? state = null)
        {
            Cells[key] = state ?? new CellSnapshot(materialId);
            if (!Instances.TryGetInstance(key, out _)) Instances.Create(key);
            Fixed.Remove(key);
            if (fixedCell) Fixed.Add(key);
            Keys = Cells.Keys.ToArray();
        }
        internal void Remove(CellKey key)
        {
            Cells.Remove(key);
            Fixed.Remove(key);
            if (Instances.TryGetInstance(key, out CellInstanceHandle instance)) Instances.Invalidate(instance);
            Keys = Cells.Keys.ToArray();
        }
        internal void AddStrip(ulong id = 7)
        {
            BodyItems = new[] { new BodySnapshot(id, new BodyPose(new Vector2(2, 3), (float)(Math.PI / 6)),
                new BodyMotion(new Vector2(0.25f, 0.1f), 0.5f), new Vector2(0.15f, 0.05f), 5) };
            for (int x = 0; x < 3; x++) Put(Body(id, x, 0));
        }
        internal StructurePlan Analyze()
        {
            var result = new ConnectivityAnalyzer(65536, 64).Plan(this, Materials, ReadOnlySpan<CellKey>.Empty);
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            return result.Plan;
        }
        internal WorldResult Build(out BodyExtractionPlan plan, ulong[] ids = null, IFailureInjector failures = null,
            TickStage stage = TickStage.Commands, BodyExtractionPlanner planner = null, StructurePlan structure = null,
            CellPositionKey[] candidateWrites = null, IMaterialRuntimeTable materials = null)
        {
            return (planner ?? new BodyExtractionPlanner()).Build(this, structure ?? Analyze(), materials ?? Materials,
                ids ?? Array.Empty<ulong>(), Instances, Changes, candidateWrites ?? Array.Empty<CellPositionKey>(), Context(stage), out plan, failures);
        }
        public WorldResult Read(in CellKey key, out CellSnapshot cell)
        {
            cell = default;
            if (Closed) return WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("Fixture", "lease", "候选失效。"));
            Cells.TryGetValue(key, out cell);
            return WorldResult.Success();
        }
        public bool IsFixed(in CellKey key) => Fixed.Contains(key);
        internal static BodyExtractionPlan Success(BodyFixture world, ulong[] ids = null)
        {
            WorldResult result = world.Build(out BodyExtractionPlan plan, ids);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Target + ": " + result.Diagnostic.Message);
            return plan;
        }
        internal static void StateEquals(CellSnapshot expected, CellSnapshot actual)
        {
            Assert.That(actual.MaterialId, Is.EqualTo(expected.MaterialId));
            Assert.That(actual.Flags, Is.EqualTo(expected.Flags));
            Assert.That(actual.FuelTicksRemaining, Is.EqualTo(expected.FuelTicksRemaining));
            Assert.That(actual.SpreadCountdown, Is.EqualTo(expected.SpreadCountdown));
            Assert.That(actual.LifetimeTicksRemaining, Is.EqualTo(expected.LifetimeTicksRemaining));
            Assert.That(actual.MoveCountdown, Is.EqualTo(expected.MoveCountdown));
            Assert.That(actual.IgnitedTick, Is.EqualTo(expected.IgnitedTick));
        }
    }
}
