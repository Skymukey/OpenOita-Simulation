using System.Runtime.InteropServices;

namespace OpenOita.Contracts
{
    // 只读边界 DTO；权威存储继续由唯一的全局 CellState 承担，M02 迁移其字段。
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CellSnapshot
    {
        public readonly ushort MaterialId;
        public readonly ushort Flags;
        public readonly uint FuelTicksRemaining;
        public readonly uint SpreadCountdown;
        public readonly uint LifetimeTicksRemaining;
        public readonly uint MoveCountdown;
        public readonly ulong IgnitedTick;
        public bool IsBurning => (Flags & 1) != 0;

        public CellSnapshot(ushort materialId, ushort flags = 0, uint fuelTicksRemaining = 0,
            uint spreadCountdown = 0, uint lifetimeTicksRemaining = 0, uint moveCountdown = 0, ulong ignitedTick = 0)
        {
            MaterialId = materialId;
            Flags = flags;
            FuelTicksRemaining = fuelTicksRemaining;
            SpreadCountdown = spreadCountdown;
            LifetimeTicksRemaining = lifetimeTicksRemaining;
            MoveCountdown = moveCountdown;
            IgnitedTick = ignitedTick;
        }
    }

    public readonly struct CellHit
    {
        public readonly WorldVersion Version;
        public readonly CellPositionKey Position;
        public readonly CellSnapshot State;
        public CellKey Key => new CellKey(Version.Generation, Position);
        public ushort MaterialId => State.MaterialId;
        public CellHit(WorldVersion version, CellPositionKey position, CellSnapshot state)
        {
            Version = version;
            Position = position;
            State = state;
        }
    }
}
