using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Data
{
    public enum SceneEditOperation { Paint, Erase, Fixed, InitialBurning }

    // M08：仅编辑初态。运行状态、BodyId和运行快照不会进入此模型。
    public sealed class SceneMaterialData
    {
        private readonly Dictionary<Vector2Int, ushort> _cells = new();
        private readonly HashSet<Vector2Int> _fixed = new();
        private readonly HashSet<Vector2Int> _burning = new();
        public WorldConfig Config { get; }
        public IMaterialRuntimeTable Materials { get; }
        public int Revision { get; private set; }
        public int Count => _cells.Count;

        private SceneMaterialData(WorldLoadResult loaded)
        {
            Config = loaded.Config;
            Materials = loaded.Materials;
            foreach (InitialCell cell in loaded.Scene.Cells) _cells.Add(cell.Position, cell.MaterialId);
            foreach (Vector2Int cell in loaded.Scene.FixedCells) _fixed.Add(cell);
            foreach (Vector2Int cell in loaded.Scene.InitialBurning) _burning.Add(cell);
        }

        public static WorldResult Load(WorldSources sources, out SceneMaterialData data)
        {
            data = null;
            WorldLoadResult loaded = new WorldSourceLoader().Load(sources);
            if (!loaded.Result.IsSuccess) return loaded.Result;
            data = new SceneMaterialData(loaded);
            return loaded.Result;
        }

        public ushort MaterialAt(Vector2Int cell) => _cells.TryGetValue(cell, out ushort id) ? id : (ushort)0;
        public bool IsFixed(Vector2Int cell) => _fixed.Contains(cell);
        public bool IsInitiallyBurning(Vector2Int cell) => _burning.Contains(cell);

        public SceneInitialData Snapshot()
        {
            var cells = new List<InitialCell>(_cells.Count);
            foreach (var item in _cells) cells.Add(new InitialCell(item.Key.x, item.Key.y, item.Value));
            return new SceneInitialData(1, Materials.MaterialSetId, "materials.json", "world_config.json", cells, _fixed, _burning);
        }

        public WorldResult Export(out WorldSources sources) => ConfigurationSerializer.Serialize(Config, Snapshot(), Materials, out sources);

        // 先验证整组选格，再修改；标记不适用或容量超限时不留下部分笔触。
        public WorldResult Apply(IReadOnlyList<Vector2Int> selection, SceneEditOperation operation, ushort materialId = 0, bool mark = true)
        {
            if (selection == null || !Enum.IsDefined(typeof(SceneEditOperation), operation))
                return Error(WorldErrorCode.InvalidArgument, "selection", "选格或工具无效。");
            if (operation == SceneEditOperation.Paint && (materialId == 0 || !Materials.TryGet(materialId, out _)))
                return Error(WorldErrorCode.UnknownMaterial, "materialId", "请选择已校验的非空材料。");
            var unique = new HashSet<Vector2Int>();
            int additions = 0;
            foreach (Vector2Int cell in selection)
            {
                if (cell.x < 0 || cell.y < 0 || cell.x >= Config.Width || cell.y >= Config.Height)
                    return Error(WorldErrorCode.OutOfBounds, cell.ToString(), "选格超出逻辑尺寸，不裁切。");
                if (!unique.Add(cell)) continue;
                ushort old = MaterialAt(cell);
                if (operation == SceneEditOperation.Paint && old == 0) additions++;
                if (mark && (operation == SceneEditOperation.Fixed || operation == SceneEditOperation.InitialBurning))
                {
                    RuleMask rule = operation == SceneEditOperation.Fixed ? RuleMask.Structure : RuleMask.Burnable;
                    if (!Materials.TryGet(old, out MaterialRuntimeEntry entry) || (entry.Rules & rule) == 0)
                        return Error(WorldErrorCode.IncompatibleRule, cell.ToString(),
                            operation == SceneEditOperation.Fixed ? "固定标记只能设置在structure材料上。" : "初燃标记只能设置在burnable材料上。");
                }
            }
            if ((long)Count + additions > Config.Limits.MaxMaterialCells)
                return Error(WorldErrorCode.CapacityExceeded, "cells", "画笔整组超过材料格上限。");
            foreach (Vector2Int cell in unique)
            {
                switch (operation)
                {
                    case SceneEditOperation.Paint:
                        _cells[cell] = materialId; _fixed.Remove(cell); _burning.Remove(cell); break;
                    case SceneEditOperation.Erase:
                        _cells.Remove(cell); _fixed.Remove(cell); _burning.Remove(cell); break;
                    case SceneEditOperation.Fixed:
                        if (mark) _fixed.Add(cell); else _fixed.Remove(cell); break;
                    case SceneEditOperation.InitialBurning:
                        if (mark) _burning.Add(cell); else _burning.Remove(cell); break;
                }
            }
            if (unique.Count > 0) Revision++;
            return WorldResult.Success();
        }

        private static WorldResult Error(WorldErrorCode code, string target, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("初态编辑", target, message, "scene.json"));
    }
}
