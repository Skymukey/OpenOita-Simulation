using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Commands
{
    internal sealed class CommandQueue
    {
        internal const int Capacity = 4096;
        private readonly Queue<(CommandToken token, MaterialCommand command)> _pending = new(Capacity);
        private readonly Dictionary<ulong, CommandResult> _results = new(Capacity);
        private readonly Queue<ulong> _order = new(Capacity);
        private readonly object _owner;
        internal ulong Highest { get; private set; }
        internal int Count => _pending.Count;
        internal CommandQueue(object owner) { _owner = owner; }
        internal EnqueueResult Enqueue(in MaterialCommand command)
        {
            if (Count == Capacity || Highest == ulong.MaxValue)
                return new EnqueueResult(WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("Enqueue", command.Operation.ToString(), "4096队列或令牌序号容量已满。")));
            var token = new CommandToken(_owner, command.Generation, ++Highest);
            _pending.Enqueue((token, command));
            return new EnqueueResult(WorldResult.Success(), token);
        }
        internal (CommandToken token, MaterialCommand command) Dequeue() => _pending.Dequeue();
        internal CommandResult Retry(in CommandToken token, WorldVersion version, bool faulted)
        {
            WorldResult valid = CommandTokenValidation.Validate(_owner, version.Generation, Highest, token);
            if (!valid.IsSuccess) return new CommandResult(valid, token, 0, version);
            if (_results.TryGetValue(token.Sequence, out CommandResult result)) return result;
            foreach (var item in _pending)
                if (item.token.Equals(token))
                    return new CommandResult(faulted ? WorldResult.Failure(WorldErrorCode.Faulted,
                        new WorldDiagnostic("Retry", "token", "未发布命令所属世界已冻结。")) : WorldResult.Pending(), token, 0, version);
            return new CommandResult(WorldResult.Failure(WorldErrorCode.ResultExpired,
                new WorldDiagnostic("Retry", "token", "结果已离开最近4096条缓存。")), token, 0, version);
        }
        internal void Cache(CommandResult result)
        {
            if (_order.Count == Capacity) _results.Remove(_order.Dequeue());
            _results.Add(result.Token.Sequence, result);
            _order.Enqueue(result.Token.Sequence);
        }
        internal void Clear() { _pending.Clear(); _results.Clear(); _order.Clear(); Highest = 0; }
    }
}
