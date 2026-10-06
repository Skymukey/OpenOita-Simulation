using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Queries;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Spatial;
using UnityEngine;

namespace OpenOita.Commands
{
    // 仅准备整条命令的写入；结构/容量/物理采用交M02，公开发布仍在完整Tick之后。
    internal sealed class MaterialCommandPlanner
    {
        private readonly IMaterialRuntimeTable _materials;
        private readonly Vector2 _origin;
        private readonly ExactCellGeometry _geometry = new();
        internal MaterialCommandPlanner(IMaterialRuntimeTable materials, Vector2 origin) { _materials = materials; _origin = origin; }
        internal WorldResult Execute(WorldRuntime runtime, in MaterialCommand command, CommandToken token, out int affected)
        {
            affected = 0; var writes = new List<CellWrite>(); bool structural = false;
            runtime.Refresh(runtime.Spatial, TickStage.Commands);
            if (command.Operation == MaterialOperation.Spawn)
            {
                int minX = Math.Max(0, Mathf.CeilToInt((command.Region.Min.x - _origin.x) / runtime.State.Config.CellSize - 0.5f));
                int minY = Math.Max(0, Mathf.CeilToInt((command.Region.Min.y - _origin.y) / runtime.State.Config.CellSize - 0.5f));
                int maxX = Math.Min(runtime.State.Config.Width, Mathf.CeilToInt((command.Region.Max.x - _origin.x) / runtime.State.Config.CellSize - 0.5f));
                int maxY = Math.Min(runtime.State.Config.Height, Mathf.CeilToInt((command.Region.Max.y - _origin.y) / runtime.State.Config.CellSize - 0.5f));
                long targets = (long)(maxX - minX) * (maxY - minY);
                if (targets + runtime.State.MaterialCells > runtime.State.Config.Limits.MaxMaterialCells)
                    return Error(WorldErrorCode.CapacityExceeded, "Spawn maxMaterialCells", "整条Spawn超过世界材料容量，未分配目标列表。");
                _materials.TryGet(command.MaterialId, out MaterialRuntimeEntry material);
                for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
                {
                    var key = new CellKey(command.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
                    runtime.State.Read(key, out CellSnapshot state);
                    var square = new CellGeometry(key, new BodyPose(_origin, 0), new Vector2Int(x, y), runtime.State.Config.CellSize);
                    WorldResult overlap = runtime.Spatial.HasSolidOverlap(square, out bool solid);
                    if (!overlap.IsSuccess) return overlap;
                    if (state.MaterialId != 0 || solid) return Error(WorldErrorCode.Occupied, "spawn", "整条命令包含已有材料或任意正面积固体覆盖。");
                    writes.Add(new CellWrite(key, CellState.Create(material).Snapshot, true));
                }
                structural = (material.Rules & RuleMask.Structure) != 0;
            }
            else foreach (CellKey key in runtime.State.OccupiedCells)
            {
                CellGeometry square = CellSelection.Geometry(key, runtime.State.Origin, runtime.State.Config.CellSize, runtime.State.Bodies);
                if (!command.Region.ContainsCenter(_geometry.Center(square))) continue;
                runtime.State.Read(key, out CellSnapshot state); _materials.TryGet(state.MaterialId, out MaterialRuntimeEntry old);
                if (command.Operation == MaterialOperation.Remove)
                { writes.Add(new CellWrite(key, default)); structural |= (old.Rules & RuleMask.Structure) != 0; }
                else if (command.Operation == MaterialOperation.Replace)
                {
                    _materials.TryGet(command.MaterialId, out MaterialRuntimeEntry material);
                    if (key.Position.OwnerKind == OwnerKind.Body && (material.Kind != MaterialKind.Solid || (material.Rules & RuleMask.Structure) == 0))
                        return Error(WorldErrorCode.UnsupportedOperation, "replace", "动态体仅允许结构固体替换。");
                    writes.Add(new CellWrite(key, CellState.Create(material).Snapshot, true));
                    structural |= ((old.Rules | material.Rules) & RuleMask.Structure) != 0;
                }
                else if (WetContactPolicy.CanIgnite(old, state, runtime.State.Instances.TryGetInstance(key, out CellInstanceHandle instance) && runtime.State.Instances.IsWet(instance)))
                    writes.Add(new CellWrite(key, WetContactPolicy.Ignite(state, runtime.State.WorkingTick)));
            }
            if (writes.Count == 0) return WorldResult.Success();
            WorldResult result = runtime.Edit(writes.ToArray(), runtime.Context(TickStage.Commands, token), structural);
            if (result.IsSuccess) affected = writes.Count;
            return result;
        }

        private static WorldResult Error(WorldErrorCode code, string target, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("Commands", target, message));
    }
}
