using System;
using UnityEngine;

namespace OpenOita.Contracts
{
    public readonly struct BodyPose
    {
        public readonly Vector2 Position;
        public readonly float AngleRadians;
        public BodyPose(Vector2 position, float angleRadians) { Position = position; AngleRadians = angleRadians; }
    }
    public readonly struct BodyMotion
    {
        public readonly Vector2 LinearVelocity;
        public readonly float AngularVelocityRadians;
        public BodyMotion(Vector2 linearVelocity, float angularVelocityRadians)
        {
            LinearVelocity = linearVelocity; AngularVelocityRadians = angularVelocityRadians;
        }
    }
    public readonly struct BodySnapshot
    {
        public readonly ulong BodyId;
        public readonly BodyPose Pose;
        public readonly BodyMotion Motion;
        public readonly Vector2 LocalCenterOfMass;
        public readonly ulong GeometryVersion;
        public BodySnapshot(ulong bodyId, BodyPose pose, BodyMotion motion, Vector2 localCenterOfMass, ulong geometryVersion)
        {
            BodyId = bodyId; Pose = pose; Motion = motion; LocalCenterOfMass = localCenterOfMass; GeometryVersion = geometryVersion;
        }
    }

    // M05 创建的只读局部权威存储，受控准备成功后归 M02；移交方不得再 Dispose。
    public interface IMaterialBodyStorage : IDisposable
    {
        bool IsDisposed { get; }
        BodySnapshot Snapshot { get; }
        BodyGeometryPlan Geometry { get; }
        ReadOnlySpan<CellPositionKey> OccupiedPositions { get; }
        bool TryRead(in CellPositionKey position, out CellSnapshot state);
    }

    // M02 拥有，阶段内只读租约。TryRead 缺块返回空、不分配；越界及代次错误保留错误码。
    public interface IWorkingWorldView
    {
        WorldConfig Config { get; }
        Vector2 Origin { get; }
        ulong Generation { get; }
        ulong WorkingTick { get; }
        WorldResult Read(in CellKey key, out CellSnapshot cell);
        bool IsFixed(in CellKey key);
        ReadOnlySpan<CellKey> OccupiedCells { get; }
        ReadOnlySpan<BodySnapshot> Bodies { get; }
    }
    public interface ICommittedWorldView
    {
        WorldVersion Version { get; }
        WorldConfig Config { get; }
        Vector2 Origin { get; }
        WorldResult Read(in CellKey key, out CellSnapshot cell);
        ReadOnlySpan<CellKey> OccupiedCells { get; }
        ReadOnlySpan<BodySnapshot> Bodies { get; }
    }

    public enum CellChangeKind : byte { Spawned, Removed, StateChanged, OwnershipTransferred, BurnedOut, LifetimeExpired, Replaced, Suspended, Restored }
    public readonly struct CellChange
    {
        public readonly CellChangeKind Kind;
        public readonly CellKey BeforeKey;
        public readonly CellKey AfterKey;
        public readonly CellSnapshot Before;
        public readonly CellSnapshot After;
        public CellChange(CellChangeKind kind, CellKey beforeKey, CellKey afterKey, CellSnapshot before, CellSnapshot after)
        {
            Kind = kind; BeforeKey = beforeKey; AfterKey = afterKey; Before = before; After = after;
        }
    }
    // 拆分一对多用多条映射；创建旧ID=0，撤销新ID=0；仅存在一分量时允许旧新同ID。
    public readonly struct BodyIdMapping
    {
        public readonly ulong OldBodyId;
        public readonly ulong NewBodyId;
        public BodyIdMapping(ulong oldBodyId, ulong newBodyId) { OldBodyId = oldBodyId; NewBodyId = newBodyId; }
    }
    public readonly struct DirtyCellRange
    {
        public readonly OwnerKind OwnerKind;
        public readonly ulong BodyId;
        public readonly Vector2Int Min;
        public readonly Vector2Int MaxExclusive;
        public DirtyCellRange(OwnerKind ownerKind, ulong bodyId, Vector2Int min, Vector2Int maxExclusive)
        {
            OwnerKind = ownerKind; BodyId = bodyId; Min = min; MaxExclusive = maxExclusive;
        }
    }
    // M02 拥有；ChangeSet 仅回调期间有效，长期保存应逐项复制。
    public interface IChangeSetView
    {
        ReadOnlySpan<CellChange> Cells { get; }
        ReadOnlySpan<BodyIdMapping> BodyMappings { get; }
        ReadOnlySpan<ulong> CreatedBodies { get; }
        ReadOnlySpan<ulong> RetiredBodies { get; }
        ReadOnlySpan<DirtyCellRange> DirtyRanges { get; }
    }
    public readonly struct ChangeSet
    {
        public readonly WorldVersion Version;
        public readonly IChangeSetView Changes;
        public ChangeSet(WorldVersion version, IChangeSetView changes) { Version = version; Changes = changes; }
    }
    // M02 发布，M07B 消费；Faulted 可继续持有最后成功版本，释放/Reset 后旧租约失效。
    public interface ICommittedRenderView : ICommittedWorldView
    {
        IMaterialRuntimeTable Materials { get; }
    }
    public interface IWorldRenderer : IDisposable
    {
        WorldResult Prepare(ICommittedRenderView initialView);
        void OnCommitted(ICommittedRenderView view, in ChangeSet changes);
        void FlushFrame();
    }

    // M00/M02：候选显示在发布前完整准备；Adopt只移交已准备资源，不上传或分配。
    // 候选视图不可对外保存。成功后由M02 Adopt；失败/释放由候选所有者Dispose。
    public interface IPreparedWorldDisplay : IDisposable
    {
        WorldResult Result { get; }
        long CpuBytes { get; }
        void Adopt();
    }
    public interface ITransactionalWorldRenderer : IWorldRenderer
    {
        IPreparedWorldDisplay PrepareCommit(ICommittedRenderView candidate, in ChangeSet changes);
    }
}
