using System;
using OpenOita.Contracts;

namespace OpenOita.Rules
{
    // 规则私有的复用存储；对外始终使用 M00 的 MutationIntent/IRuleBatch。
    internal sealed class RuleIntent : IRuleBatch
    {
        private readonly MutationIntent[] _items;
        private int _count;
        public WorldResult Result { get; private set; }
        public TickStage Stage { get; private set; }
        public ReadOnlySpan<MutationIntent> Intents => Result.IsSuccess ? _items.AsSpan(0, _count) : ReadOnlySpan<MutationIntent>.Empty;
        internal RuleIntent(int capacity) { _items = new MutationIntent[capacity]; }
        internal void Begin(TickStage stage)
        {
            _count = 0;
            Stage = stage;
            Result = WorldResult.Success();
        }
        internal bool Add(in MutationIntent intent)
        {
            if (!Result.IsSuccess) return false;
            if (_count == _items.Length)
            {
                Fail(RuleBatchContext.Error(Stage, WorldErrorCode.CapacityExceeded, intent.Source, "规则意图缓冲不足，整批拒绝。"));
                return false;
            }
            _items[_count++] = intent;
            return true;
        }
        internal void Fail(WorldResult result) { _count = 0; Result = result; }
    }
}
