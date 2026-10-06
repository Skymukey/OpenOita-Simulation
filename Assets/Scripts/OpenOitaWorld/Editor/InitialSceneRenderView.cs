using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEngine;

namespace OpenOita.Editor
{
    // 明确标为编辑初态的只读副本；不是Ready世界/运行发布，不提取或推进。
    internal sealed class InitialSceneRenderView : ICommittedRenderView
    {
        private readonly CellKey[] _keys;
        private readonly Dictionary<CellPositionKey, CellSnapshot> _cells = new();
        public WorldVersion Version { get; }
        public WorldConfig Config { get; }
        public Vector2 Origin { get; }
        public IMaterialRuntimeTable Materials { get; }
        public ReadOnlySpan<CellKey> OccupiedCells => _keys;
        public ReadOnlySpan<BodySnapshot> Bodies => ReadOnlySpan<BodySnapshot>.Empty;

        internal InitialSceneRenderView(SceneMaterialData data, Vector2 origin)
        {
            Config = data.Config; Origin = origin; Materials = data.Materials;
            Version = new WorldVersion(1, (ulong)data.Revision);
            SceneInitialData copy = data.Snapshot();
            _keys = new CellKey[copy.Cells.Count];
            for (int i = 0; i < _keys.Length; i++)
            {
                InitialCell cell = copy.Cells[i];
                var position = new CellPositionKey(OwnerKind.Grid, 0, cell.Position.x, cell.Position.y);
                _keys[i] = new CellKey(1, position);
                Materials.TryGet(cell.MaterialId, out MaterialRuntimeEntry entry);
                _cells.Add(position, CellState.Create(entry, data.IsInitiallyBurning(cell.Position)).Snapshot);
            }
        }

        public WorldResult Read(in CellKey key, out CellSnapshot cell)
        {
            _cells.TryGetValue(key.Position, out cell);
            return WorldResult.Success();
        }
    }
}
