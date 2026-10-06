using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Spatial
{
    // 保留既有类型名；F01将即时排开替换为覆盖收集及最终位姿下的有界恢复。
    public sealed class FluidDisplacementPlanner
    {
        internal static long EstimateCpuBytes(WorldConfig config, int cells)
        {
            long radius = config.Limits.FluidDisplacementRadius;
            long visited = Math.Min((long)config.Width * config.Height, 1 + 2 * radius * (radius + 1));
            return 4096L + visited * 128L + cells * 768L;
        }

        public WorldResult CollectCovered(IWorkingWorldView world, IMaterialRuntimeTable materials,
            WorldOccupancyIndex end, out CellKey[] sources)
        {
            sources = Array.Empty<CellKey>();
            WorldResult valid = Validate(world, end);
            if (!valid.IsSuccess) return valid;
            var covered = new List<CellKey>();
            foreach (CellKey key in world.OccupiedCells)
            {
                if (key.Position.OwnerKind != OwnerKind.Grid) continue;
                valid = world.Read(key, out CellSnapshot state); if (!valid.IsSuccess) return valid;
                if (!materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material) ||
                    (material.Rules & (RuleMask.LiquidFlow | RuleMask.GasDrift)) == 0) continue;
                valid = end.HasSolidOverlap(Grid(world, key.Position.X, key.Position.Y), out bool overlap);
                if (!valid.IsSuccess) return valid;
                if (overlap) covered.Add(key);
            }
            covered.Sort((a, b) => a.Position.CompareTo(b.Position));
            sources = covered.ToArray();
            return WorldResult.Success();
        }

        public WorldResult PlanRestoration(IWorkingWorldView world, ITickInstanceMap instances,
            WorldOccupancyIndex final, out MutationIntent[] moves)
        {
            moves = Array.Empty<MutationIntent>();
            WorldResult valid = Validate(world, final);
            if (!valid.IsSuccess) return valid;
            if (world is not IFluidSuspensionView fluids) return WorldResult.Success();
            var result = new List<MutationIntent>();
            var reserved = new HashSet<CellPositionKey>();
            // 工作视图负责按进入Tick/原y/x/记录ID排序，规划器再排序防止其他提供者依赖容器顺序。
            SuspendedFluidSnapshot[] records = fluids.SuspendedFluids.ToArray();
            Array.Sort(records, Compare);
            foreach (SuspendedFluidSnapshot record in records)
            {
                if (record.Generation != world.Generation) return Error(WorldErrorCode.StaleGeneration, "暂存记录代次不一致。");
                if (record.SuspendedTick >= world.WorkingTick) continue;
                CellGeometry origin = Grid(world, record.OriginalPosition.x, record.OriginalPosition.y);
                valid = final.HasStaticSolidOverlap(origin, out bool staticBlocked); if (!valid.IsSuccess) return valid;
                if (staticBlocked) continue;
                bool found = false;
                CellKey target = default;
                var seen = new HashSet<Vector2Int>();
                var layer = new List<Vector2Int> { record.OriginalPosition };
                seen.Add(record.OriginalPosition);
                for (int distance = 0; distance <= world.Config.Limits.FluidDisplacementRadius && layer.Count != 0 && !found; distance++)
                {
                    layer.Sort((a, b) => a.y == b.y ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                    foreach (Vector2Int point in layer)
                    {
                        var key = new CellKey(world.Generation, new CellPositionKey(OwnerKind.Grid, 0, point.x, point.y));
                        valid = final.HasSolidOverlap(Grid(world, point.x, point.y), out bool blocked); if (!valid.IsSuccess) return valid;
                        valid = world.Read(key, out CellSnapshot destination); if (!valid.IsSuccess) return valid;
                        if (!blocked && destination.MaterialId == 0 && !reserved.Contains(key.Position))
                        { target = key; found = true; break; }
                    }
                    if (found || distance == world.Config.Limits.FluidDisplacementRadius) break;
                    var next = new List<Vector2Int>();
                    foreach (Vector2Int point in layer)
                    {
                        foreach (Vector2Int offset in Directions)
                        {
                            Vector2Int candidate = point + offset;
                            if (candidate.x < 0 || candidate.y < 0 || candidate.x >= world.Config.Width || candidate.y >= world.Config.Height || !seen.Add(candidate)) continue;
                            valid = final.HasSolidOverlap(Grid(world, candidate.x, candidate.y), out bool blocked); if (!valid.IsSuccess) return valid;
                            if (!blocked) next.Add(candidate);
                        }
                    }
                    layer = next;
                }
                if (!found) continue;
                if (!instances.TryGetInstance(record.Key, out CellInstanceHandle instance))
                    return Error(WorldErrorCode.NotReady, "恢复源实例租约失效。");
                reserved.Add(target.Position);
                result.Add(new MutationIntent(MutationKind.Move, instance, record.Key, target, record.State));
            }
            moves = result.ToArray();
            return WorldResult.Success();
        }

        private static int Compare(SuspendedFluidSnapshot a, SuspendedFluidSnapshot b)
        {
            int order = a.SuspendedTick.CompareTo(b.SuspendedTick);
            if (order == 0) order = a.OriginalPosition.y.CompareTo(b.OriginalPosition.y);
            if (order == 0) order = a.OriginalPosition.x.CompareTo(b.OriginalPosition.x);
            return order == 0 ? a.RecordId.CompareTo(b.RecordId) : order;
        }
        private static WorldResult Validate(IWorkingWorldView world, WorldOccupancyIndex index)
        {
            WorldResult valid = index.Check(world.Generation);
            if (!valid.IsSuccess) return valid;
            TickStage expected = world.WorkingTick == 0 ? TickStage.Structure : TickStage.Physics;
            return index.Lease.WorkingTick == world.WorkingTick && index.Lease.Stage == expected
                ? WorldResult.Success() : Error(WorldErrorCode.NotReady, "暂存/恢复须使用当前阶段的最终占据租约。");
        }
        private static readonly Vector2Int[] Directions = { Vector2Int.down, Vector2Int.left, Vector2Int.right, Vector2Int.up };
        private static CellGeometry Grid(IWorkingWorldView world, int x, int y) => new CellGeometry(
            new CellKey(world.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y)), new BodyPose(world.Origin, 0), new Vector2Int(x, y), world.Config.CellSize);
        private static WorldResult Error(WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("Physics", "fluidRestoration", message));
    }
}