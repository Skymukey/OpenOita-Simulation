using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Host;
using UnityEngine;

public sealed class WorldHost : MonoBehaviour
{
    [SerializeField] TextAsset materialsSource;
    [SerializeField] TextAsset worldConfigSource;
    [SerializeField] TextAsset sceneSource;
    [SerializeField] string configurationDirectory = "";
    [SerializeField] Vector2 worldOrigin;
    [SerializeField] bool automatic = true;
    [SerializeField, InspectorName("禁止刚体旋转（重新启动生效）")]
    [Tooltip("临时观察开关：保留下落、平移和碰撞。停止后重新进入Play Mode生效，Reset沿用创建时设置。")]
    bool freezeBodyRotation = true;
    [SerializeField] Shader committedMaterialShader;
    [SerializeField] Shader committedFlameShader;
    [SerializeField] bool pixelView = true;
    [SerializeField, Min(1)] int pixelScale = 1;
    [SerializeField] Vector2Int pixelPan;
    [SerializeField] Camera displayCamera;

    // 保留旧场景序列化字段及脚本 GUID；旧演示不作为正式模拟启动路径。
#pragma warning disable CS0414
    [SerializeField, HideInInspector] string materialsFolder = "Assets/Data/Materials";
    [SerializeField, HideInInspector] string coreChunkFillMaterial = "stone";
    [SerializeField, HideInInspector] ComputeShader chunkColorCompute;
    [SerializeField, HideInInspector] Shader chunkDisplayShader;
#pragma warning restore CS0414

    private IWorld _world;
    private Vector2 _displayOrigin;
    private FixedStepDriver _driver;
    private bool _validateFluids;
    private ulong _lastFluidValidationTick;
    public IWorld World => _world;
    public WorldResult LastResult { get; private set; }

    private void OnEnable()
    {
        if (_world != null) return;
        LastResult = ReadSources(out WorldSources sources);
        if (LastResult.IsSuccess) LastResult = CreateWorld(new WorldSimulation(rendererFactory: () => new OpenOita.Render.CommittedWorldRenderer(committedMaterialShader, committedFlameShader), freezeBodyRotation: freezeBodyRotation), sources, worldOrigin, automatic);
        if (!LastResult.IsSuccess) Report(LastResult);
    }

    // M08 试玩传入独立三文件副本；M02 完整装配完成后使用正式 IWorldFactory。
    public WorldResult CreateWorld(IWorldFactory factory, WorldSources sources, Vector2 origin, bool autoDrive)
    {
        if (_world != null) return Failure(WorldErrorCode.Busy, "world", "请先关闭当前世界。");
        if (factory == null || !ContractDefaults.IsFinite(origin))
            return Failure(WorldErrorCode.InvalidArgument, "factory/origin", "工厂及原点必须有效。");
        if (transform.rotation != Quaternion.identity || transform.lossyScale != Vector3.one)
            return Failure(WorldErrorCode.InvalidArgument, "transform", "Host 必须旋转为 0、世界缩放为 1。");
        WorldCreateResult created = factory.Create(sources, origin);
        LastResult = created.Result;
        if (!created.Result.IsSuccess) return created.Result;
        if (created.World.Lifecycle != WorldLifecycle.Ready)
        {
            created.World.Dispose();
            return LastResult = Failure(WorldErrorCode.NotReady, "factory", "工厂不能返回部分准备的世界。");
        }
        try
        {
            _driver = new FixedStepDriver(created.World, autoDrive);
            _world = created.World;
            _displayOrigin = origin;
            _validateFluids = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-openOitaValidateF01") >= 0;
            _lastFluidValidationTick = 0;
            // M09/M08 Player证据开关：只读取正式查询，不添加推进逻辑。
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-openOitaValidateM08") >= 0)
            {
                QueryResult initial = _world.QueryRegion(OpenOita.Contracts.GridSelection.Bounds(_world.Config, origin), System.Span<CellHit>.Empty);
                Debug.Log($"OpenOita M08 Player Ready：{_world.Config.Width}×{_world.Config.Height}，cellSize={_world.Config.CellSize}，初态材料格={initial.RequiredCount}，generation={_world.Version.Generation}，Tick={_world.Version.CommittedTick}");
            }
            if (_validateFluids) LogFluidCounts();
            return LastResult;
        }
        catch
        {
            created.World.Dispose();
            throw;
        }
    }

    public StepResult Step()
    {
        if (_driver == null) return new StepResult(Failure(WorldErrorCode.NotReady, "world", "世界尚未创建。"), default, null);
        StepResult result = _driver.StepManual();
        LastResult = result.Result;
        return result;
    }

    public WorldResult ResetWorld()
    {
        if (_world == null) return Failure(WorldErrorCode.NotReady, "world", "世界尚未创建。");
        WorldResult result = _world.Reset();
        if (result.IsSuccess) _driver = new FixedStepDriver(_world, _driver.Automatic);
        return LastResult = result;
    }

    public WorldResult CloseWorld()
    {
        if (_world == null) return WorldResult.Success();
        WorldResult result = _world.Dispose();
        if (result.IsSuccess)
        {
            _world = null;
            _driver = null;
        }
        return LastResult = result;
    }

    // M02：M08只请求现有驱动和精确像素显示，不另建Update/物理推进器。
    public void ConfigurePixelView(Camera camera, int scale, Vector2Int pan)
    {
        displayCamera = camera;
        pixelView = true;
        pixelScale = Mathf.Max(1, scale);
        pixelPan = pan;
    }

    private void Update()
    {
        if (_driver == null || !_driver.Automatic || _world.Lifecycle != WorldLifecycle.Ready) return;
        LastResult = _driver.Advance(Time.deltaTime);
        if (!LastResult.IsSuccess) Report(LastResult);
        if (_validateFluids)
        {
            ulong tick = _world.Version.CommittedTick;
            if (tick < _lastFluidValidationTick) _lastFluidValidationTick = 0;
            if (tick - _lastFluidValidationTick >= 250) { LogFluidCounts(); _lastFluidValidationTick = tick; }
        }
    }

    private void LateUpdate()
    {
        if (pixelView && _world != null)
        {
            Camera camera = displayCamera != null ? displayCamera : Camera.main;
            if (camera != null) OpenOita.Render.PixelWorldViewport.Configure(camera, _world.Config, _displayOrigin, Mathf.Max(1, pixelScale), pixelPan);
        }
        (_world as OpenOita.Simulation.SimulationWorld)?.FlushFrame();
    }
    private void OnGUI()
    {
        if (!pixelView || _world == null) return;
        GUI.Label(new Rect(12, 10, 440, 24), $"正式世界 {_world.Config.Width}×{_world.Config.Height} | {pixelScale}倍 | Tick {_world.Version.CommittedTick}");
        if (GUI.Button(new Rect(12, 36, 60, 24), "1:1")) pixelScale = 1;
        if (GUI.Button(new Rect(80, 36, 60, 24), "3倍")) pixelScale = 3;
        MaterialCountsResult counts = _world.QueryMaterialCounts();
        if (counts.Result.IsSuccess)
            GUI.Label(new Rect(12, 66, 600, 24), $"活动 {counts.ActiveCells} | 暂存水 {counts.SuspendedWaterCells} / 蒸汽 {counts.SuspendedSteamCells} | 总量 {counts.TotalCells}");
    }

    // 可选Player验证日志，仅消费已提交数量诊断，不改变驱动、规则或运行配置。
    private void LogFluidCounts()
    {
        MaterialCountsResult counts = _world.QueryMaterialCounts();
        if (!counts.Result.IsSuccess) return;
        Debug.Log($"OpenOita F01 Player：{_world.Lifecycle}，generation={counts.Version.Generation}，Tick={counts.Version.CommittedTick}，活动={counts.ActiveCells}，暂存水={counts.SuspendedWaterCells}，暂存蒸汽={counts.SuspendedSteamCells}，总量={counts.TotalCells}");
        foreach (MaterialCount material in counts.Counts)
            Debug.Log($"OpenOita F01 材料：Tick={counts.Version.CommittedTick}，ID={material.MaterialId}，网格={material.GridCells}，体内={material.BodyCells}，暂存={material.SuspendedCells}，合计={material.TotalCells}");
    }

    private void OnDisable() { CloseWorld(); }
    private void OnDestroy() { CloseWorld(); }

    private WorldResult ReadSources(out WorldSources sources)
    {
        sources = null;
        bool any = materialsSource != null || worldConfigSource != null || sceneSource != null;
        if (any)
        {
            if (materialsSource == null || worldConfigSource == null || sceneSource == null)
                return Failure(WorldErrorCode.InvalidConfig, "sources", "请同时指定材料、世界配置、场景三份文本。");
            sources = new WorldSources(materialsSource.text, worldConfigSource.text, sceneSource.text);
            return WorldResult.Success();
        }
        string directory = string.IsNullOrWhiteSpace(configurationDirectory)
            ? Path.Combine(Application.streamingAssetsPath, "OpenOita") : configurationDirectory;
        return new ConfigurationFileStore().ReadSources(directory, out sources);
    }

    private static WorldResult Failure(WorldErrorCode code, string target, string message) =>
        WorldResult.Failure(code, new WorldDiagnostic("Host", target, message));
    private static void Report(WorldResult result) => Debug.LogWarning(
        $"OpenOita: {result.ErrorCode} / {result.Diagnostic.Stage} / {result.Diagnostic.Target} / {result.Diagnostic.Message}");
}
