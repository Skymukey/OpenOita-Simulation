using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Simulation
{
    internal sealed partial class WorkingWorld
    {
        private static SuspendedFluidSnapshot[] OrderSuspended(SortedDictionary<ulong, SuspendedFluidSnapshot> records)
        {
            var ordered = new SuspendedFluidSnapshot[records.Count];
            records.Values.CopyTo(ordered, 0);
            Array.Sort(ordered, (a, b) =>
            {
                int order = a.SuspendedTick.CompareTo(b.SuspendedTick);
                if (order == 0) order = a.OriginalPosition.y.CompareTo(b.OriginalPosition.y);
                if (order == 0) order = a.OriginalPosition.x.CompareTo(b.OriginalPosition.x);
                return order == 0 ? a.RecordId.CompareTo(b.RecordId) : order;
            });
            return ordered;
        }

        private PreparationResult<IPreparedMutation> PrepareCapture(ReadOnlySpan<CellKey> sources, in TransactionContext context)
        {
            if (sources.Length > _occupied.Count || sources.Length + _suspended.Count > Config.Limits.MaxMaterialCells)
                return Rejected(Error(WorldErrorCode.CapacityExceeded, "Physics", "suspendedCapacity", "整批暂存记录超过材料容量。"));
            var ids = new ulong[sources.Length];
            WorldResult reserved = _fluidIds.Reserve(sources.Length, ids);
            if (!reserved.IsSuccess) return Rejected(reserved);
            var records = new SuspendedFluidSnapshot[sources.Length];
            var moves = new MutationIntent[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                CellKey source = sources[i];
                if (source.Position.OwnerKind != OwnerKind.Grid ||
                    !Read(source, out CellSnapshot state).IsSuccess || state.MaterialId == 0 ||
                    !_materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material) ||
                    (material.Rules & (RuleMask.LiquidFlow | RuleMask.GasDrift)) == 0 ||
                    !_instances.TryGetInstance(source, out CellInstanceHandle instance))
                    return Rejected(Error(WorldErrorCode.InvalidArgument, "Physics", "suspendedSource", "暂存源必须是活动主网格水汽及有效实例。"));
                records[i] = new SuspendedFluidSnapshot(Generation, ids[i], new Vector2Int(source.Position.X, source.Position.Y), WorkingTick, state);
                moves[i] = new MutationIntent(MutationKind.Move, instance, source, records[i].Key, state);
            }
            return PrepareFluidMoves(moves, context, records);
        }

        internal PreparationResult<IPreparedMutation> PrepareFluidMoves(ReadOnlySpan<MutationIntent> moves,
            in TransactionContext context, ReadOnlySpan<SuspendedFluidSnapshot> records = default)
        {
            var writes = new CellWrite[moves.Length * 2];
            for (int i = 0; i < moves.Length; i++)
            {
                writes[i] = new CellWrite(moves[i].Source, default);
                writes[i + moves.Length] = new CellWrite(moves[i].Target, moves[i].State);
            }
            return Prepare(writes, context, moves: moves, newSuspensions: records);
        }
    }
}
