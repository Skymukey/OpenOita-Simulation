using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace OpenOita.Contracts
{
    public sealed class WorldSources
    {
        public string MaterialsText { get; }
        public string WorldConfigText { get; }
        public string SceneText { get; }
        public string MaterialsFileName { get; }
        public string WorldConfigFileName { get; }
        public string SceneFileName { get; }
        public WorldSources(string materialsText, string worldConfigText, string sceneText,
            string materialsFileName = "materials.json", string worldConfigFileName = "world_config.json", string sceneFileName = "scene.json")
        {
            MaterialsText = materialsText; WorldConfigText = worldConfigText; SceneText = sceneText;
            MaterialsFileName = materialsFileName; WorldConfigFileName = worldConfigFileName; SceneFileName = sceneFileName;
        }
    }

    public sealed class WorldLimits
    {
        public int MaxMaterialCells { get; }
        public int MaxDynamicBodies { get; }
        public int MaxShapesPerBody { get; }
        public int MaxTotalShapes { get; }
        public int MaxChangesPerTick { get; }
        public float MaxLinearSpeed { get; }
        public float MaxAngularSpeedDegrees { get; }
        public int MaxPhysicsSubsteps { get; }
        public int FluidDisplacementRadius { get; }
        public WorldLimits(int maxMaterialCells, int maxDynamicBodies, int maxShapesPerBody, int maxTotalShapes,
            int maxChangesPerTick, float maxLinearSpeed, float maxAngularSpeedDegrees, int maxPhysicsSubsteps, int fluidDisplacementRadius)
        {
            MaxMaterialCells = maxMaterialCells; MaxDynamicBodies = maxDynamicBodies;
            MaxShapesPerBody = maxShapesPerBody; MaxTotalShapes = maxTotalShapes; MaxChangesPerTick = maxChangesPerTick;
            MaxLinearSpeed = maxLinearSpeed; MaxAngularSpeedDegrees = maxAngularSpeedDegrees;
            MaxPhysicsSubsteps = maxPhysicsSubsteps; FluidDisplacementRadius = fluidDisplacementRadius;
        }
    }

    // 由 M01 在全部语义校验后构造；构造器不是加载器，不补缺失字段。
    public sealed class WorldConfig
    {
        public int SchemaVersion { get; }
        public int Width { get; }
        public int Height { get; }
        public int ChunkSize { get; }
        public float CellSize { get; }
        public float StepSeconds { get; }
        public float GravityY { get; }
        public uint Seed { get; }
        public WorldLimits Limits { get; }
        public WorldConfig(int schemaVersion, int width, int height, int chunkSize, float cellSize,
            float stepSeconds, float gravityY, uint seed, WorldLimits limits)
        {
            SchemaVersion = schemaVersion; Width = width; Height = height; ChunkSize = chunkSize;
            CellSize = cellSize; StepSeconds = stepSeconds; GravityY = gravityY; Seed = seed;
            Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }
    }

    public readonly struct InitialCell
    {
        public readonly Vector2Int Position;
        public readonly ushort MaterialId;
        public InitialCell(int x, int y, ushort materialId) { Position = new Vector2Int(x, y); MaterialId = materialId; }
    }

    // M00 定义，M01 校验/序列化，M02 持有创建时副本；不含运行体/速度/已消耗状态。
    public sealed class SceneInitialData
    {
        public int SchemaVersion { get; }
        public string MaterialSetId { get; }
        public string MaterialsFile { get; }
        public string WorldConfigFile { get; }
        public IReadOnlyList<InitialCell> Cells { get; }
        public IReadOnlyList<Vector2Int> FixedCells { get; }
        public IReadOnlyList<Vector2Int> InitialBurning { get; }
        public SceneInitialData(int schemaVersion, string materialSetId, string materialsFile, string worldConfigFile,
            IEnumerable<InitialCell> cells, IEnumerable<Vector2Int> fixedCells, IEnumerable<Vector2Int> initialBurning)
        {
            SchemaVersion = schemaVersion; MaterialSetId = materialSetId; MaterialsFile = materialsFile; WorldConfigFile = worldConfigFile;
            var orderedCells = new List<InitialCell>(cells ?? throw new ArgumentNullException(nameof(cells)));
            orderedCells.Sort((a, b) => ComparePosition(a.Position, b.Position));
            Cells = new ReadOnlyCollection<InitialCell>(orderedCells);
            FixedCells = FreezePositions(fixedCells);
            InitialBurning = FreezePositions(initialBurning);
        }

        private static IReadOnlyList<Vector2Int> FreezePositions(IEnumerable<Vector2Int> source)
        {
            var copy = new List<Vector2Int>(source ?? throw new ArgumentNullException(nameof(source)));
            copy.Sort(ComparePosition);
            return new ReadOnlyCollection<Vector2Int>(copy);
        }
        private static int ComparePosition(Vector2Int a, Vector2Int b) => a.y == b.y ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y);
    }

    public readonly struct WorldLoadResult
    {
        public readonly WorldResult Result;
        public readonly WorldConfig Config;
        public readonly SceneInitialData Scene;
        public readonly IMaterialRuntimeTable Materials;
        public readonly IRuleRegistry Rules;
        public WorldLoadResult(WorldResult result, WorldConfig config = null, SceneInitialData scene = null,
            IMaterialRuntimeTable materials = null, IRuleRegistry rules = null)
        {
            bool complete = config != null && scene != null && materials != null && rules != null;
            bool any = config != null || scene != null || materials != null || rules != null;
            if ((result.IsSuccess && !complete) || (!result.IsSuccess && any))
                throw new ArgumentException("加载结果不能包含部分配置。");
            Result = result; Config = config; Scene = scene; Materials = materials; Rules = rules;
        }
    }

    public interface IWorldSourceLoader
    {
        WorldLoadResult Load(WorldSources sources);
    }

    // M08 唯一定义 SceneMaterialData；此接口不创建第二套编辑模型。导出必须复制并经 M01 校验。
    public interface ISceneInitialDataConverter<TSceneMaterialData>
    {
        WorldResult Export(TSceneMaterialData editorData, WorldConfig config, IMaterialRuntimeTable materials, out SceneInitialData initialData);
        WorldResult Import(SceneInitialData initialData, out TSceneMaterialData editorData);
    }
}
