using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Simulation
{
    // 生命周期与 Tick 发布仍由 M02 调度器负责；本类只拥有一次事务的候选资源。
    internal sealed class TransactionCoordinator : IDisposable
    {
        private readonly List<IPreparedMutation> _participants = new();
        private readonly List<WorldDiagnostic> _cleanupDiagnostics = new();
        private bool _applied;
        private bool _closed;
        private TickStage _stage;
        internal bool RequiresFault { get; private set; }
        internal IReadOnlyList<WorldDiagnostic> CleanupDiagnostics => _cleanupDiagnostics;

        internal void Own(IPreparedMutation participant)
        {
            if (_closed || _applied || participant == null || participant.State != PreparationState.Prepared)
                throw new InvalidOperationException("事务仅能取得未应用候选资源。");
            if (_participants.Contains(participant)) throw new InvalidOperationException("候选资源只能有一个拥有者。");
            _participants.Add(participant);
        }

        internal WorldResult ValidateAndApply(in TransactionContext context, WorldLimits limits,
            long memoryLimit, IFailureInjector failures = null)
        {
            if (_closed || _applied) return Error(context, WorldErrorCode.Busy, "事务已应用或关闭。");
            _stage = context.Stage;
            try
            {
                long cpuBytes = 0;
                foreach (IPreparedMutation participant in _participants)
                {
                    ResourceBudget budget = participant.Budget;
                    if (budget.MaterialCells < 0 || budget.DynamicBodies < 0 || budget.StaticShapes < 0 ||
                        budget.DynamicShapes < 0 || budget.ChangedPositions < 0 || budget.CpuBytesIncludingPreparation < 0)
                        return Reject(Error(context, WorldErrorCode.InvalidArgument, "候选预算不能为负。"));
                    cpuBytes = checked(cpuBytes + budget.CpuBytesIncludingPreparation);
                    if (budget.MaterialCells > limits.MaxMaterialCells || budget.DynamicBodies > limits.MaxDynamicBodies ||
                        (long)budget.StaticShapes + budget.DynamicShapes > limits.MaxTotalShapes ||
                        budget.ChangedPositions > limits.MaxChangesPerTick || cpuBytes > memoryLimit)
                        return Reject(Error(context, WorldErrorCode.CapacityExceeded, "候选资源的全世界预算超限。"));
                    if (participant is IPreparedMaterialMutation material)
                    {
                        foreach (BodyGeometryPlan body in material.Geometry)
                        {
                            if (body.Owner.OwnerKind == OwnerKind.Body && body.Rectangles.Count > limits.MaxShapesPerBody)
                                return Reject(Error(context, WorldErrorCode.CapacityExceeded, "单体形状预算超限。"));
                        }
                    }
                    WorldResult check = participant.Preflight(context);
                    if (!check.IsSuccess) return Reject(check);
                }
                WorldResult injected = failures?.Check(context, FailurePoint.BeforeApply) ?? WorldResult.Success();
                if (!injected.IsSuccess) return Reject(injected);
                // 一旦进入应用，失败都交给世界 Faulted；不能假定物理对象可回滚。
                _applied = true;
                foreach (IPreparedMutation participant in _participants)
                {
                    injected = failures?.Check(context, FailurePoint.DuringApply) ?? WorldResult.Success();
                    if (!injected.IsSuccess) return Fault(injected);
                    WorldResult applied = participant.Apply(context);
                    if (!applied.IsSuccess) return Fault(applied);
                }
                return WorldResult.Success();
            }
            catch (Exception exception)
            {
                WorldResult result = Error(context, _applied ? WorldErrorCode.Faulted : WorldErrorCode.CapacityExceeded,
                    exception.GetType().Name + ": " + exception.Message);
                return _applied ? Fault(result) : Reject(result);
            }
        }

        internal void MarkCommitted(WorldVersion version)
        {
            if (_closed || !_applied) throw new InvalidOperationException("只能在完整 Tick 发布后移交事务。");
            try
            {
                foreach (IPreparedMutation participant in _participants) participant.MarkCommitted(version);
            }
            catch
            {
                RequiresFault = true;
                throw;
            }
            finally
            {
                Dispose();
            }
        }

        private WorldResult Reject(WorldResult result)
        {
            // 可预知拒绝只对外部命令可恢复；自动规则/物理失败必须冻结整个世界。
            RequiresFault |= _stage != TickStage.Commands;
            Dispose();
            return result;
        }
        private WorldResult Fault(WorldResult result) { RequiresFault = true; Dispose(); return result; }
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            for (int i = _participants.Count - 1; i >= 0; i--)
            {
                IPreparedMutation participant = _participants[i];
                try { if (participant.State != PreparationState.Committed) participant.Abort(); }
                catch (Exception exception) { RecordCleanup(exception); }
                try { participant.Dispose(); }
                catch (Exception exception) { RecordCleanup(exception); }
            }
            _participants.Clear();
        }

        private void RecordCleanup(Exception exception)
        {
            RequiresFault = true;
            _cleanupDiagnostics.Add(new WorldDiagnostic("Abort", "preparedResource", exception.GetType().Name + ": " + exception.Message));
        }

        private static WorldResult Error(in TransactionContext context, WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic(context.Stage.ToString(), "transaction", message));
    }
}
