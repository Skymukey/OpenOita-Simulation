using System;
using UnityEngine;

namespace OpenOita.Contracts
{
    public readonly struct SpatialLease : IEquatable<SpatialLease>
    {
        public readonly ulong Generation;
        public readonly ulong WorkingTick;
        public readonly TickStage Stage;
        public readonly long Revision;
        public SpatialLease(ulong generation, ulong workingTick, TickStage stage, long revision)
        {
            Generation = generation; WorkingTick = workingTick; Stage = stage; Revision = revision;
        }
        public bool Equals(SpatialLease other) => Generation == other.Generation && WorkingTick == other.WorkingTick && Stage == other.Stage && Revision == other.Revision;
    }
    public readonly struct CellGeometry
    {
        public readonly CellKey Key;
        public readonly BodyPose Pose;
        public readonly Vector2Int LocalCell;
        public readonly float CellSize;
        public CellGeometry(CellKey key, BodyPose pose, Vector2Int localCell, float cellSize)
        {
            Key = key; Pose = pose; LocalCell = localCell; CellSize = cellSize;
        }
    }
    public enum ContactFeature : byte { Separated, EdgeEdge, VertexEdge, VertexVertex, PositiveAreaOverlap }
    public readonly struct CellContact
    {
        public readonly CellKey First;
        public readonly CellKey Second;
        public readonly ContactFeature Feature;
        public readonly float Distance;
        public CellContact(CellKey first, CellKey second, ContactFeature feature, float distance)
        {
            First = first; Second = second; Feature = feature; Distance = distance;
        }
    }
    public readonly struct SegmentCellHit
    {
        public readonly CellHit Hit;
        public readonly double FirstIntersectionT;
        public SegmentCellHit(CellHit hit, double firstIntersectionT) { Hit = hit; FirstIntersectionT = firstIntersectionT; }
    }
    // M06 实现；正方形 OBB 精确检测，AABB 仅作宽阶段。调用方缓冲不足返回完整 requiredCount。
    public interface IContactQuery
    {
        SpatialLease Lease { get; }
        WorldResult Classify(in CellGeometry first, in CellGeometry second, float epsilon, out CellContact contact);
        QueryResult QueryContacts(in CellKey cell, Span<CellContact> destination);
    }
    public interface IOccupancyView
    {
        SpatialLease Lease { get; }
        WorldVersion Version { get; }
        WorldResult HasSolidOverlap(in CellGeometry cell, out bool overlaps);
    }
    public interface ICellGeometryService
    {
        bool ContainsPoint(in CellGeometry cell, Vector2 point);
        bool IntersectSegment(in CellGeometry cell, Vector2 start, Vector2 end, out double firstIntersectionT);
        Vector2 Center(in CellGeometry cell);
    }
    // M06 拥有候选缓冲，仅在下一子步/释放前有效；M02 校验与排开后方可采用。
    public interface IPhysicsStepView
    {
        SpatialLease Lease { get; }
        ReadOnlySpan<BodySnapshot> CandidateBodies { get; }
        ReadOnlySpan<CellContact> Contacts { get; }
    }
    public readonly struct PhysicsStepResult
    {
        public readonly WorldResult Result;
        public readonly IPhysicsStepView Candidates;
        public PhysicsStepResult(WorldResult result, IPhysicsStepView candidates = null)
        {
            if (result.IsSuccess != (candidates != null)) throw new ArgumentException("物理候选仅在完整成功时提供。");
            Result = result; Candidates = candidates;
        }
    }
    public interface IPhysicsStepper : IDisposable
    {
        PhysicsStepResult StepSubstep(IWorkingWorldView world, float substepSeconds, int substepIndex, long availableCpuBytes, IFailureInjector failures);
    }
    public interface IQueryService
    {
        PointQueryResult QueryPoint(ICommittedWorldView view, Vector2 point);
        QueryResult QueryRegion(ICommittedWorldView view, in WorldRect region, Span<CellHit> destination);
        QueryResult QuerySegment(ICommittedWorldView view, Vector2 start, Vector2 end, Span<CellHit> destination);
    }
    public interface ICommandQueue
    {
        EnqueueResult Enqueue(in MaterialCommand command);
        CommandResult Retry(in CommandToken token);
        bool TryDequeue(out CommandToken token, out MaterialCommand command);
        void Complete(in CommandResult result);
        void FaultPending(in WorldDiagnostic diagnostic);
        void Reset(ulong generation);
    }
}
