using System;

namespace OpenOita.Contracts
{
    public enum OwnerKind : byte { Grid = 0, Body = 1, Suspended = 2 }

    public readonly struct WorldVersion : IEquatable<WorldVersion>
    {
        public readonly ulong Generation;
        public readonly ulong CommittedTick;

        public WorldVersion(ulong generation, ulong committedTick)
        {
            Generation = generation;
            CommittedTick = committedTick;
        }

        public bool Equals(WorldVersion other) => Generation == other.Generation && CommittedTick == other.CommittedTick;
        public override bool Equals(object obj) => obj is WorldVersion other && Equals(other);
        public override int GetHashCode() => unchecked(Generation.GetHashCode() * 397 ^ CommittedTick.GetHashCode());
    }

    // 一 Tick 内的权威写入计数键；不含位姿或派生缓存。
    public readonly struct CellPositionKey : IEquatable<CellPositionKey>, IComparable<CellPositionKey>
    {
        public readonly OwnerKind OwnerKind;
        public readonly ulong BodyId;
        public readonly int X;
        public readonly int Y;

        public CellPositionKey(OwnerKind ownerKind, ulong bodyId, int x, int y)
        {
            if (ownerKind != OwnerKind.Grid && ownerKind != OwnerKind.Body && ownerKind != OwnerKind.Suspended)
                throw new ArgumentOutOfRangeException(nameof(ownerKind));
            if ((ownerKind == OwnerKind.Grid && bodyId != 0) || (ownerKind != OwnerKind.Grid && bodyId == 0))
                throw new ArgumentException("主网格 BodyId 必须为0；动态体 BodyId 必须非0。", nameof(bodyId));
            if (ownerKind == OwnerKind.Suspended && (x != 0 || y != 0))
                throw new ArgumentException("暂存计数键使用记录ID，坐标必须为0。");
            OwnerKind = ownerKind;
            BodyId = bodyId;
            X = x;
            Y = y;
        }

        public int CompareTo(CellPositionKey other)
        {
            int value = OwnerKind.CompareTo(other.OwnerKind);
            if (value == 0) value = BodyId.CompareTo(other.BodyId);
            if (value == 0) value = Y.CompareTo(other.Y);
            return value == 0 ? X.CompareTo(other.X) : value;
        }

        public bool Equals(CellPositionKey other) => OwnerKind == other.OwnerKind && BodyId == other.BodyId && X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is CellPositionKey other && Equals(other);
        public override int GetHashCode() => unchecked((((int)OwnerKind * 397 ^ BodyId.GetHashCode()) * 397 ^ Y) * 397 ^ X);
    }

    public readonly struct CellKey : IEquatable<CellKey>
    {
        public readonly ulong Generation;
        public readonly CellPositionKey Position;
        public CellKey(ulong generation, CellPositionKey position) { Generation = generation; Position = position; }
        public bool Equals(CellKey other) => Generation == other.Generation && Position.Equals(other.Position);
        public override bool Equals(object obj) => obj is CellKey other && Equals(other);
        public override int GetHashCode() => unchecked(Generation.GetHashCode() * 397 ^ Position.GetHashCode());
    }

    // D01：仅在所属 Tick 内有效，随移动/提取/拆分映射；不是永久 CellState 字段。
    public readonly struct CellInstanceHandle : IEquatable<CellInstanceHandle>
    {
        public readonly ulong Generation;
        public readonly ulong WorkingTick;
        public readonly ulong Sequence;
        internal CellInstanceHandle(ulong generation, ulong workingTick, ulong sequence)
        {
            Generation = generation;
            WorkingTick = workingTick;
            Sequence = sequence;
        }
        public bool Equals(CellInstanceHandle other) => Generation == other.Generation && WorkingTick == other.WorkingTick && Sequence == other.Sequence;
        public override bool Equals(object obj) => obj is CellInstanceHandle other && Equals(other);
        public override int GetHashCode() => unchecked((Generation.GetHashCode() * 397 ^ WorkingTick.GetHashCode()) * 397 ^ Sequence.GetHashCode());
    }

    public readonly struct CommandToken : IEquatable<CommandToken>
    {
        private readonly object _owner;
        public readonly ulong Generation;
        public readonly ulong Sequence;
        internal CommandToken(object owner, ulong generation, ulong sequence)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Generation = generation;
            Sequence = sequence;
        }
        internal bool BelongsTo(object owner) => _owner != null && ReferenceEquals(_owner, owner);
        public bool Equals(CommandToken other) => ReferenceEquals(_owner, other._owner) && Generation == other.Generation && Sequence == other.Sequence;
        public override bool Equals(object obj) => obj is CommandToken other && Equals(other);
        public override int GetHashCode() => unchecked(((_owner == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_owner)) * 397 ^ Generation.GetHashCode()) * 397 ^ Sequence.GetHashCode());
    }

    // M02 在容量预检之后调用；预留 BodyId 即使事务失败也不得回退此计数器。
    internal sealed class IdentitySequence
    {
        private ulong _last;
        internal IdentitySequence(ulong last = 0) { _last = last; }
        internal WorldResult Reserve(out ulong value)
        {
            Span<ulong> reserved = stackalloc ulong[1];
            WorldResult result = Reserve(1, reserved);
            value = result.IsSuccess ? reserved[0] : 0;
            return result;
        }

        // 整段预检后才写入；失败不消耗序号，成功预留不提供回收入口。
        internal WorldResult Reserve(int count, Span<ulong> destination)
        {
            if (count < 0)
                return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("Identity", "count", "预留数量不能为负。"));
            if (destination.Length < count)
                return WorldResult.Failure(WorldErrorCode.BufferTooSmall, new WorldDiagnostic("Identity", "destination", "缓冲不足以接收全部预留身份。"));
            if ((ulong)count > ulong.MaxValue - _last)
                return WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Identity", "sequence", "身份序号已耗尽，不能预留完整号段。"));
            for (int i = 0; i < count; i++) destination[i] = _last + (ulong)i + 1;
            _last += (ulong)count;
            return WorldResult.Success();
        }
    }

    internal static class CommandTokenValidation
    {
        internal static WorldResult Validate(object owner, ulong generation, ulong highestIssued, in CommandToken token)
        {
            WorldErrorCode code = WorldErrorCode.None;
            if (!token.BelongsTo(owner)) code = WorldErrorCode.InvalidToken;
            else if (token.Generation != generation) code = WorldErrorCode.StaleGeneration;
            else if (token.Sequence == 0 || token.Sequence > highestIssued) code = WorldErrorCode.InvalidToken;
            return code == WorldErrorCode.None ? WorldResult.Success() : WorldResult.Failure(code,
                new WorldDiagnostic("Retry", "token", "令牌拥有者、代次或序号无效。"));
        }
    }
}
