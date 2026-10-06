using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Structure.Connectivity
{
    // 离线只读候选夹具；不接入正式 Create/Step，不模拟材料体提交或物理。
    internal sealed class ConnectivityFixture : IWorkingWorldView
    {
        internal readonly Dictionary<CellKey, CellSnapshot> Cells = new Dictionary<CellKey, CellSnapshot>();
        internal readonly HashSet<CellKey> Fixed = new HashSet<CellKey>();
        internal CellKey[] Keys = Array.Empty<CellKey>();
        internal BodySnapshot[] BodyItems = Array.Empty<BodySnapshot>();
        internal WorldResult ReadResult = WorldResult.Success();
        internal bool Closed;
        internal int Reads;
        public WorldConfig Config { get; }
        public Vector2 Origin => Vector2.zero;
        public ulong Generation => 1;
        public ulong WorkingTick => 1;
        public ReadOnlySpan<CellKey> OccupiedCells => Closed ? throw new InvalidOperationException("夹具租约已关闭。") : Keys;
        public ReadOnlySpan<BodySnapshot> Bodies => BodyItems;

        internal ConnectivityFixture(int width = 256, int height = 256, int maxBodies = 64)
        {
            Config = new WorldConfig(1, width, height, 128, 0.1f, 0.02f, -9.81f, 1,
                new WorldLimits(65536, maxBodies, 256, 4096, 65536, 5, 180, 8, 16));
        }

        internal static CellKey Grid(int x, int y, ulong generation = 1) =>
            new CellKey(generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        internal static CellKey Body(ulong id, int x, int y) =>
            new CellKey(1, new CellPositionKey(OwnerKind.Body, id, x, y));
        internal void Put(CellKey key, ushort materialId = 104, bool isFixed = false, CellSnapshot? state = null)
        {
            Cells[key] = state ?? new CellSnapshot(materialId);
            Fixed.Remove(key);
            if (isFixed) Fixed.Add(key);
            Refresh();
        }
        internal void Remove(CellKey key) { Cells.Remove(key); Fixed.Remove(key); Refresh(); }
        internal void Refresh() { Keys = Cells.Keys.ToArray(); }
        internal void AddBody(ulong id)
        {
            BodyItems = BodyItems.Concat(new[] { new BodySnapshot(id, default, default, default, 1) }).ToArray();
        }
        public WorldResult Read(in CellKey key, out CellSnapshot state)
        {
            Reads++;
            Cells.TryGetValue(key, out state);
            return ReadResult;
        }
        public bool IsFixed(in CellKey key) => Fixed.Contains(key);

        internal static WorldLoadResult Load(Action<JObject> edit = null)
        {
            WorldSources sources = BaselineSources.Read();
            JObject materials = BaselineSources.Parse(sources.MaterialsText);
            edit?.Invoke(materials);
            var result = new WorldSourceLoader().Load(new WorldSources(materials.ToString(), sources.WorldConfigText, sources.SceneText));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            return result;
        }
    }
}
