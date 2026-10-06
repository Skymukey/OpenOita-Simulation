using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using OpenOita.Simulation.Bodies;

namespace OpenOita.Simulation
{
    internal sealed partial class WorkingWorld
    {
        internal long Revision => _revision;
        internal bool IsLeaseValid(long revision, ulong tick) => !_disposed && !_faulted && _instances != null && !_instances.IsClosed && (_tickActive || tick == 0) && Thread.CurrentThread.ManagedThreadId == _threadId && _revision == revision && WorkingTick == tick;
        internal TickChangeCounter Changes => _changes;

        // 候选位姿与整批暂存沿用同一材料候选。只更新状态/位姿时几何版本保持不变。
        internal PreparationResult<IPreparedMutation> PreparePhysics(IPhysicsStepView physics, ReadOnlySpan<CellKey> covered, in TransactionContext context)
        {
            WorldResult access = CheckAccess(); if (!access.IsSuccess) return Rejected(access);
            if (physics == null || !physics.Lease.Equals(new SpatialLease(Generation, WorkingTick, TickStage.Physics, _revision)) || context.Stage != TickStage.Physics)
                return Rejected(Error(WorldErrorCode.NotReady, "Physics", "lease", "位姿候选不属于当前工作修订。"));
            BodySnapshot[] snapshots;
            try { snapshots = physics.CandidateBodies.ToArray(); }
            catch (Exception exception) { return Rejected(Error(WorldErrorCode.NotReady, "Physics", "lease", exception.Message)); }
            if (snapshots.Length != _bodySnapshots.Length) return Rejected(Error(WorldErrorCode.InvalidArgument, "Physics", "bodies", "候选必须包含全部体。"));
            for (int i = 0; i < snapshots.Length; i++)
            {
                BodySnapshot next = snapshots[i], old = _bodySnapshots[i];
                if (next.BodyId != old.BodyId || next.GeometryVersion != old.GeometryVersion || !next.LocalCenterOfMass.Equals(old.LocalCenterOfMass) ||
                    !ContractDefaults.IsFinite(next.Pose.Position) || !ContractDefaults.IsFinite(next.Pose.AngleRadians) ||
                    !ContractDefaults.IsFinite(next.Motion.LinearVelocity) || !ContractDefaults.IsFinite(next.Motion.AngularVelocityRadians))
                    return Rejected(Error(WorldErrorCode.InvalidArgument, "Physics", "bodies", "位姿不能改变几何版本、目录或质心。"));
            }
            var prepared = PrepareCapture(covered, context);
            return prepared.Result.IsSuccess ? PrepareStateBodies(prepared.Prepared, snapshots) : prepared;
        }

        internal PreparationResult<IPreparedMutation> PrepareStateBodies(IPreparedMutation prepared, ReadOnlySpan<BodySnapshot> poses)
        {
            if (!ReferenceEquals(prepared, _pending) || _pending == null || _pending._ownershipPrepared)
                return Rejected(Error(WorldErrorCode.Busy, "State", "candidate", "状态采用必须使用当前未合并的候选。"));
            PreparedGridMutation edit = _pending;
            var bodies = new SortedDictionary<ulong, IMaterialBodyStorage>();
            try
            {
                foreach (BodySnapshot pose in poses)
                {
                    BodyGeometryPlan old = _bodies[pose.BodyId].Geometry;
                    var cells = new List<PlannedCell>();
                    foreach (PlannedCell cell in old.Cells)
                    {
                        CellKey key = cell.Target;
                        if (!edit.Read(key, out CellSnapshot state).IsSuccess || state.MaterialId == 0 || !edit._instances.TryGetInstance(key, out CellInstanceHandle instance))
                            throw new InvalidOperationException("状态采用不能改变结构占据。");
                        cells.Add(new PlannedCell(key, key, instance, state));
                    }
                    var geometry = new BodyGeometryPlan(old.Owner, pose.Pose, pose.Motion, old.LocalCenterOfMass, old.Mass, old.Inertia,
                        old.BoundingRadius, pose.GeometryVersion, cells, old.Rectangles);
                    bodies.Add(pose.BodyId, new MaterialBody(geometry, pose.GeometryVersion));
                }
                if (bodies.Count != _bodies.Count) throw new InvalidOperationException("状态采用必须保留全部体。");
                edit._ownedBodies = bodies;
                edit._bodySnapshots = poses.ToArray();
                edit._ownershipPrepared = true;
                edit.Budget = new ResourceBudget(edit.Budget.MaterialCells, bodies.Count, edit.Budget.StaticShapes, edit.Budget.DynamicShapes,
                    edit.Budget.ChangedPositions, edit.Budget.CpuBytesIncludingPreparation + MaterialCells * 384L);
                return new PreparationResult<IPreparedMutation>(WorldResult.Success(), edit);
            }
            catch (Exception exception)
            {
                foreach (IMaterialBodyStorage body in bodies.Values) body.Dispose();
                edit.Abort();
                return Rejected(Error(WorldErrorCode.CapacityExceeded, "State", "bodyCopy", exception.Message));
            }
        }
    }
}
