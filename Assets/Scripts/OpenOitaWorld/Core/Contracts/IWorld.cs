using System;
using UnityEngine;

namespace OpenOita.Contracts
{
    public readonly struct WorldRect
    {
        public readonly Vector2 Min;
        public readonly Vector2 Max;
        public WorldRect(Vector2 min, Vector2 max) { Min = min; Max = max; }
        public bool ContainsCenter(Vector2 center) => center.x >= Min.x && center.x < Max.x && center.y >= Min.y && center.y < Max.y;
    }

    public enum MaterialOperation : byte { Spawn, Remove, Replace, Ignite }
    public readonly struct MaterialCommand
    {
        public readonly MaterialOperation Operation;
        public readonly WorldRect Region;
        public readonly ushort MaterialId;
        public readonly ulong Generation;
        public MaterialCommand(MaterialOperation operation, WorldRect region, ushort materialId, ulong generation)
        {
            Operation = operation; Region = region; MaterialId = materialId; Generation = generation;
        }
    }

    // M02 实现；主线程操作。未提供生产桩，不能把原型 WorldSimulation 当作 Ready 世界。
    public interface IWorldFactory
    {
        WorldCreateResult Create(WorldSources sources, Vector2 origin);
    }

    // 可选能力：与此世界加载的同一份不可变材料表。分类不决定调用方的碰撞规则。
    public interface IWorldMaterialCatalog
    {
        IMaterialRuntimeTable Materials { get; }
    }

    public interface IWorld
    {
        WorldLifecycle Lifecycle { get; }
        WorldVersion Version { get; }
        WorldConfig Config { get; }
        event Action<ChangeSet> Committed;
        EnqueueResult Enqueue(in MaterialCommand command);
        CommandResult Retry(in CommandToken token);
        StepResult Step();
        PointQueryResult QueryPoint(Vector2 point);
        QueryResult QueryRegion(in WorldRect region, Span<CellHit> destination);
        QueryResult QuerySegment(Vector2 start, Vector2 end, Span<CellHit> destination);
        MaterialCountsResult QueryMaterialCounts();
        WorldResult Reset();
        WorldResult Dispose();
    }
}
