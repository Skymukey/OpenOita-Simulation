using OpenOita.Contracts;
using OpenOita.Data;
using UnityEngine;
using System;
using OpenOita.Simulation;

// 正式入口：M02–M06调度与M07B默认生产显示均准备成功后返回Ready。
public sealed class WorldSimulation : IWorldFactory
{
    private readonly IWorldSourceLoader _loader;
    private readonly Func<IWorldRenderer> _rendererFactory;
    private readonly bool _freezeBodyRotation;
    public WorldSimulation(IWorldSourceLoader loader = null, Func<IWorldRenderer> rendererFactory = null, bool freezeBodyRotation = false)
    {
        _loader = loader ?? new WorldSourceLoader();
        _rendererFactory = rendererFactory ?? (() => new OpenOita.Render.CommittedWorldRenderer());
        _freezeBodyRotation = freezeBodyRotation;
    }

    public WorldCreateResult Create(WorldSources sources, Vector2 origin)
    {
        if (!ContractDefaults.IsFinite(origin))
            return new WorldCreateResult(WorldResult.Failure(WorldErrorCode.InvalidArgument,
                new WorldDiagnostic("Create", "origin", "世界原点必须有限。")));
        WorldLoadResult loaded = _loader.Load(sources);
        if (!loaded.Result.IsSuccess) return new WorldCreateResult(loaded.Result);
        if (!Application.isPlaying)
            return new WorldCreateResult(WorldResult.Failure(WorldErrorCode.NotReady,
                new WorldDiagnostic("Assembly", "PlayMode", "正式独立物理世界必须在PlayMode或Player中创建。")));
        var world = new SimulationWorld(loaded, origin, _rendererFactory, _freezeBodyRotation);
        WorldResult initialized = world.Initialize();
        if (initialized.IsSuccess) return new WorldCreateResult(initialized, world);
        world.Dispose();
        return new WorldCreateResult(initialized);
    }
}
