using System;
using UnityEngine;

namespace OpenOita.Contracts
{
    // 记录身份与BodyId分域；Key仅用于内部实例映射/写入计数，不产生CellHit。
    public readonly struct SuspendedFluidSnapshot
    {
        public readonly ulong Generation;
        public readonly ulong RecordId;
        public readonly Vector2Int OriginalPosition;
        public readonly ulong SuspendedTick;
        public readonly CellSnapshot State;
        public CellKey Key => new CellKey(Generation, new CellPositionKey(OwnerKind.Suspended, RecordId, 0, 0));

        public SuspendedFluidSnapshot(ulong generation, ulong recordId, Vector2Int originalPosition,
            ulong suspendedTick, CellSnapshot state)
        {
            if (generation == 0 || recordId == 0 || state.MaterialId == 0)
                throw new ArgumentException("暂存记录须具有有效代次、非零身份和完整材料状态。");
            Generation = generation; RecordId = recordId; OriginalPosition = originalPosition;
            SuspendedTick = suspendedTick; State = state;
        }
        public SuspendedFluidSnapshot WithState(CellSnapshot state) =>
            new SuspendedFluidSnapshot(Generation, RecordId, OriginalPosition, SuspendedTick, state);
    }

    // 可选工作/提交视图，OccupiedCells仍仅包含活动材料；租约与所属视图一致。
    public interface IFluidSuspensionView
    {
        ReadOnlySpan<SuspendedFluidSnapshot> SuspendedFluids { get; }
    }

    public readonly struct MaterialCount
    {
        public readonly ushort MaterialId;
        public readonly int GridCells, BodyCells, SuspendedCells;
        public int TotalCells => GridCells + BodyCells + SuspendedCells;
        public MaterialCount(ushort materialId, int gridCells, int bodyCells, int suspendedCells)
        { MaterialId = materialId; GridCells = gridCells; BodyCells = bodyCells; SuspendedCells = suspendedCells; }
    }

    // 提交时构造的不可变值快照，允许保存；内部数组不对调用者开放写入。
    public readonly struct MaterialCountsResult
    {
        private readonly MaterialCount[] _counts;
        public readonly WorldResult Result;
        public readonly WorldVersion Version;
        public readonly int GridCells, BodyCells, SuspendedWaterCells, SuspendedSteamCells;
        public int ActiveCells => GridCells + BodyCells;
        public int SuspendedCells => SuspendedWaterCells + SuspendedSteamCells;
        public int TotalCells => ActiveCells + SuspendedCells;
        public ReadOnlySpan<MaterialCount> Counts => _counts;
        internal MaterialCountsResult(WorldResult result, WorldVersion version, MaterialCount[] counts = null,
            int gridCells = 0, int bodyCells = 0, int suspendedWaterCells = 0, int suspendedSteamCells = 0)
        {
            Result = result; Version = version; _counts = counts;
            GridCells = gridCells; BodyCells = bodyCells;
            SuspendedWaterCells = suspendedWaterCells; SuspendedSteamCells = suspendedSteamCells;
        }
    }
}
