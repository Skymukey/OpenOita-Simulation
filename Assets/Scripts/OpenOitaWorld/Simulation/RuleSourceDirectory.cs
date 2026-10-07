using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Simulation
{
    // 事务候选的派生能力目录。只复制成员实际变化的集合，倒计时不复制目录。
    internal sealed class RuleSourceDirectory
    {
        private readonly SortedSet<CellPositionKey>[] _sets;
        private static readonly RuleMask[] Masks = { RuleMask.LiquidFlow, RuleMask.GasDrift,
            RuleMask.Burnable, RuleMask.ExtinguishesFire };
        internal RuleSourceDirectory()
        {
            _sets = new SortedSet<CellPositionKey>[6];
            for (int i = 0; i < _sets.Length; i++) _sets[i] = new SortedSet<CellPositionKey>();
        }
        private RuleSourceDirectory(RuleSourceDirectory source) => _sets = (SortedSet<CellPositionKey>[])source._sets.Clone();
        internal IEnumerable<CellPositionKey> Sources(RuleMask mask, bool burning = false) => _sets[Slot(mask, burning)];
        internal int Count(RuleMask mask, bool burning = false) => _sets[Slot(mask, burning)].Count;
        internal int SuspendedGasCount => _sets[5].Count;
        private static int Slot(RuleMask mask, bool burning)
        {
            if (burning) return 4;
            for (int i = 0; i < Masks.Length; i++) if (Masks[i] == mask) return i;
            throw new ArgumentOutOfRangeException(nameof(mask));
        }
        internal RuleSourceDirectory Prepare(IWorkingWorldView candidate, IMaterialRuntimeTable materials,
            ReadOnlySpan<CellPositionKey> positions)
        {
            RuleSourceDirectory next = null;
            int copied = 0;
            foreach (CellPositionKey position in positions)
            {
                var key = new CellKey(candidate.Generation, position);
                WorldResult read = candidate.Read(key, out CellSnapshot state);
                WorldStepMetrics.Add(WorldStepMetrics.Work.RuleSourceReads);
                // 已撤销体的源键只从目录移除；其余读取失败不能伪装成空格。
                if (!read.IsSuccess && !(position.OwnerKind == OwnerKind.Body && read.ErrorCode == WorldErrorCode.InvalidArgument))
                    throw new InvalidOperationException(read.Diagnostic.Message);
                RuleMask rules = RuleMask.None;
                if (state.MaterialId != 0)
                {
                    if (!materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material))
                        throw new InvalidOperationException("能力目录材料不存在。");
                    rules = material.Rules;
                }
                bool active = position.OwnerKind != OwnerKind.Suspended;
                for (int slot = 0; slot < _sets.Length; slot++)
                {
                    bool present = slot < 4 ? active && (rules & Masks[slot]) != 0 :
                        slot == 4 ? active && (rules & RuleMask.Burnable) != 0 && state.IsBurning :
                        !active && (rules & RuleMask.GasDrift) != 0;
                    if (_sets[slot].Contains(position) == present) continue;
                    next ??= new RuleSourceDirectory(this);
                    if ((copied & (1 << slot)) == 0)
                    {
                        next._sets[slot] = new SortedSet<CellPositionKey>(_sets[slot]);
                        copied |= 1 << slot;
                    }
                    if (present) next._sets[slot].Add(position); else next._sets[slot].Remove(position);
                    WorldStepMetrics.Add(WorldStepMetrics.Work.RuleSourceChanges);
                }
            }
            return next ?? this;
        }
    }
}
