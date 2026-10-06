using System.Runtime.InteropServices;
using OpenOita.Contracts;

[StructLayout(LayoutKind.Sequential)]
public struct CellState
{
    public ushort MaterialId;
    public ushort Flags;
    public uint FuelTicksRemaining;
    public uint SpreadCountdown;
    public uint LifetimeTicksRemaining;
    public uint MoveCountdown;
    public ulong IgnitedTick;

    public readonly CellSnapshot Snapshot => new CellSnapshot(MaterialId, Flags, FuelTicksRemaining,
        SpreadCountdown, LifetimeTicksRemaining, MoveCountdown, IgnitedTick);

    internal static CellState FromSnapshot(in CellSnapshot state) => new CellState
    {
        MaterialId = state.MaterialId, Flags = state.Flags, FuelTicksRemaining = state.FuelTicksRemaining,
        SpreadCountdown = state.SpreadCountdown, LifetimeTicksRemaining = state.LifetimeTicksRemaining,
        MoveCountdown = state.MoveCountdown, IgnitedTick = state.IgnitedTick
    };

    internal static CellState Create(in MaterialRuntimeEntry material, bool burning = false, ulong tick = 0)
    {
        bool burnable = (material.Rules & RuleMask.Burnable) != 0;
        bool gas = (material.Rules & RuleMask.GasDrift) != 0;
        return new CellState
        {
            MaterialId = material.Id,
            Flags = (ushort)(burnable && burning ? 1 : 0),
            FuelTicksRemaining = burnable ? material.Parameters.FuelTicks : 0,
            SpreadCountdown = burnable ? material.Parameters.SpreadIntervalTicks : 0,
            LifetimeTicksRemaining = gas ? material.Parameters.LifetimeTicks : 0,
            IgnitedTick = burnable && burning ? tick : 0
        };
    }
}
