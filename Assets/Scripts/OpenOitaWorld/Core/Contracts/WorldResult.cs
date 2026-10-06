using System;

namespace OpenOita.Contracts
{
    public enum ResultStatus : byte { Uninitialized = 0, Succeeded, Pending, Failed }
    public enum WorldLifecycle : byte { Creating = 0, Ready, Faulted, Disposed }
    public enum WorldErrorCode : byte
    {
        None = 0, InvalidConfig, InvalidArgument, UnsupportedVersion, UnknownMaterial,
        UnknownRule, IncompatibleRule, OutOfBounds, Occupied, CapacityExceeded,
        UnsupportedOperation, InvalidToken, ResultExpired, StaleGeneration,
        BufferTooSmall, Busy, NotReady, Faulted, Disposed
    }

    public readonly struct WorldDiagnostic
    {
        public readonly string Stage;
        public readonly string Target;
        public readonly string Message;
        public readonly string FileName;
        public WorldDiagnostic(string stage, string target, string message, string fileName = null)
        {
            Stage = stage;
            Target = target;
            Message = message;
            FileName = fileName;
        }
    }

    public readonly struct WorldResult
    {
        public readonly ResultStatus Status;
        public readonly WorldErrorCode ErrorCode;
        public readonly WorldDiagnostic Diagnostic;
        public bool IsSuccess => Status == ResultStatus.Succeeded;
        private WorldResult(ResultStatus status, WorldErrorCode code, WorldDiagnostic diagnostic)
        {
            Status = status; ErrorCode = code; Diagnostic = diagnostic;
        }
        public static WorldResult Success() => new WorldResult(ResultStatus.Succeeded, WorldErrorCode.None, default);
        public static WorldResult Pending() => new WorldResult(ResultStatus.Pending, WorldErrorCode.None, default);
        public static WorldResult Failure(WorldErrorCode code, WorldDiagnostic diagnostic)
        {
            if (code == WorldErrorCode.None) throw new ArgumentException("失败结果必须带错误码。", nameof(code));
            return new WorldResult(ResultStatus.Failed, code, diagnostic);
        }
    }

    public readonly struct WorldCreateResult
    {
        public readonly WorldResult Result;
        public readonly IWorld World;
        public WorldCreateResult(WorldResult result, IWorld world = null)
        {
            if (result.IsSuccess != (world != null)) throw new ArgumentException("仅成功创建可携带世界实例。");
            Result = result; World = world;
        }
    }

    public readonly struct EnqueueResult
    {
        public readonly WorldResult Result;
        public readonly CommandToken Token;
        public EnqueueResult(WorldResult result, CommandToken token = default) { Result = result; Token = token; }
    }

    public readonly struct CommandResult
    {
        public readonly WorldResult Result;
        public readonly CommandToken Token;
        public readonly int AffectedCount;
        public readonly WorldVersion Version;
        public CommandResult(WorldResult result, CommandToken token, int affectedCount, WorldVersion version)
        {
            Result = result; Token = token; AffectedCount = affectedCount; Version = version;
        }
    }

    // M02 拥有借用缓冲；到下一次 Step/Reset 失效。需要长存时调用 CopyTo。
    public interface ICommandResultView
    {
        int Count { get; }
        CommandResult this[int index] { get; }
        void CopyTo(Span<CommandResult> destination);
    }

    public readonly struct StepResult
    {
        public readonly WorldResult Result;
        public readonly WorldVersion Version;
        public readonly ICommandResultView Commands;
        public StepResult(WorldResult result, WorldVersion version, ICommandResultView commands)
        {
            Result = result; Version = version; Commands = commands;
        }
    }

    public readonly struct PointQueryResult
    {
        public readonly WorldResult Result;
        public readonly WorldVersion Version;
        public readonly bool HasHit;
        public readonly CellHit Hit;
        public PointQueryResult(WorldResult result, WorldVersion version, bool hasHit = false, CellHit hit = default)
        {
            Result = result; Version = version; HasHit = hasHit; Hit = hit;
        }
    }

    public readonly struct QueryResult
    {
        public readonly WorldResult Result;
        public readonly WorldVersion Version;
        public readonly int RequiredCount;
        public readonly int WrittenCount;
        public QueryResult(WorldResult result, WorldVersion version, int requiredCount, int writtenCount)
        {
            if (requiredCount < 0 || writtenCount < 0 || writtenCount > requiredCount || (!result.IsSuccess && writtenCount != 0))
                throw new ArgumentException("失败查询不能返回部分成功计数。");
            Result = result; Version = version; RequiredCount = requiredCount; WrittenCount = writtenCount;
        }
    }
}
