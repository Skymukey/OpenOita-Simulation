using System;
using UnityEngine;

namespace OpenOita.Contracts
{
    public enum MaterialKind : byte { Solid, Liquid, Gas }
    public enum RuleId : byte { Structure = 1, LiquidFlow = 2, GasDrift = 3, Burnable = 4, ExtinguishesFire = 5, Corrosive = 6, Corrodible = 7 }
    [Flags]
    public enum RuleMask : ulong { None = 0, Structure = 1, LiquidFlow = 2, GasDrift = 4, Burnable = 8, ExtinguishesFire = 16, Corrosive = 32, Corrodible = 64 }
    public enum TickStage : byte
    {
        Commands = 1, PreFlowContacts, Water, Steam, Extinguish, Burning, Structure, Physics, Publish, Notify
    }
    public enum RuleTrigger : byte { MaterialChanged, EachMovementPhase, EachTick, BurningCells, ContactCapability }
    public enum MoveCandidateTier : byte { Vertical = 0, PreferredDiagonal, OtherDiagonal, PreferredHorizontal, OtherHorizontal }
    public enum MutationKind : byte { Move, WriteState, Ignite, Remove }
    public enum RemovalReason : byte { Explicit, BurnedOut, LifetimeExpired }

    public readonly struct RuleParameters
    {
        public readonly string ConnectionGroup;
        public readonly uint MoveIntervalTicks;
        public readonly uint LifetimeTicks;
        public readonly uint FuelTicks;
        public readonly uint SpreadIntervalTicks;
        public readonly ushort SmokeMaterialId;
        public readonly uint SmokeIntervalTicks;
        public readonly uint CorrosionIntervalTicks;
        public RuleParameters(string connectionGroup, uint moveIntervalTicks, uint lifetimeTicks, uint fuelTicks, uint spreadIntervalTicks,
            ushort smokeMaterialId = 0, uint smokeIntervalTicks = 0, uint corrosionIntervalTicks = 0)
        {
            ConnectionGroup = connectionGroup; MoveIntervalTicks = moveIntervalTicks; LifetimeTicks = lifetimeTicks;
            FuelTicks = fuelTicks; SpreadIntervalTicks = spreadIntervalTicks;
            SmokeMaterialId = smokeMaterialId; SmokeIntervalTicks = smokeIntervalTicks; CorrosionIntervalTicks = corrosionIntervalTicks;
        }
    }

    public readonly struct MaterialRuntimeEntry
    {
        public readonly ushort Id;
        public readonly ushort CompactIndex;
        public readonly string Name;
        public readonly MaterialKind Kind;
        public readonly Color32 Color;
        public readonly float MassPerCell;
        public readonly RuleMask Rules;
        public readonly RuleParameters Parameters;
        public MaterialRuntimeEntry(ushort id, ushort compactIndex, string name, MaterialKind kind, Color32 color,
            float massPerCell, RuleMask rules, RuleParameters parameters)
        {
            Id = id; CompactIndex = compactIndex; Name = name; Kind = kind; Color = color;
            MassPerCell = massPerCell; Rules = rules; Parameters = parameters;
        }
    }

    public readonly struct RuleDescriptor
    {
        public readonly RuleId Id;
        public readonly string Tag;
        public readonly MaterialKind Kind;
        public readonly TickStage Stage;
        public readonly RuleTrigger Trigger;
        public RuleDescriptor(RuleId id, string tag, MaterialKind kind, TickStage stage, RuleTrigger trigger)
        {
            Id = id; Tag = tag; Kind = kind; Stage = stage; Trigger = trigger;
        }
    }

    // M01 拥有只读表；index0 保留空，加载失败不泄漏部分表。描述不持有 M03 委托。
    public interface IMaterialRuntimeTable
    {
        string MaterialSetId { get; }
        int Count { get; }
        bool TryGet(ushort id, out MaterialRuntimeEntry entry);
        MaterialRuntimeEntry GetByCompactIndex(ushort compactIndex);
    }
    public interface IRuleRegistry
    {
        int Count { get; }
        bool TryGet(RuleId id, out RuleDescriptor descriptor);
    }

    public readonly struct MutationIntent
    {
        public readonly MutationKind Kind;
        public readonly CellInstanceHandle Instance;
        public readonly CellKey Source;
        public readonly CellKey Target;
        public readonly CellSnapshot State;
        public readonly MoveCandidateTier Tier;
        public readonly RemovalReason RemovalReason;
        public MutationIntent(MutationKind kind, CellInstanceHandle instance, CellKey source, CellKey target,
            CellSnapshot state, MoveCandidateTier tier = MoveCandidateTier.Vertical, RemovalReason removalReason = RemovalReason.Explicit)
        {
            Kind = kind; Instance = instance; Source = source; Target = target; State = state; Tier = tier; RemovalReason = removalReason;
        }
    }

    // M03 拥有批次缓冲，有效到下一次该执行器执行；M02 裁决前不得释放。
    public interface IRuleBatch
    {
        WorldResult Result { get; }
        TickStage Stage { get; }
        ReadOnlySpan<MutationIntent> Intents { get; }
    }
    public interface IRuleExecutor
    {
        RuleId RuleId { get; }
        // M02 传入本 Tick 同一实例表；规则只调用查询/IsWet，实例写入和标湿由 M02 受控阶段负责。
        // 实例句柄须与 snapshot.Generation/WorkingTick 和 context 对应；不以坐标生成平行身份。
        IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context);
    }
}
