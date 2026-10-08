using System;
using System.Collections;
using System.IO;
using System.Text;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.V2.Diagnostics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OpenOita.V2.Validation
{
    /// <summary>
    /// Explicit Player/Editor benchmark entry point. It is inert unless the command line
    /// contains -openoita-v2-benchmark, so normal scenes never start a long background run.
    /// </summary>
    public sealed class V2BenchmarkRunner : MonoBehaviour
    {
        private const string Flag = "-openoita-v2-benchmark";
        private bool _started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasFlag()) return;
            if (FindFirstObjectByType<V2BenchmarkRunner>() != null) return;
            var host = new GameObject("OpenOitaV2Benchmark");
            host.AddComponent<V2BenchmarkRunner>();
            DontDestroyOnLoad(host);
        }

        private void Start()
        {
            if (!_started && HasFlag())
            {
                _started = true;
                StartCoroutine(RunBenchmark());
            }
        }

        private IEnumerator RunBenchmark()
        {
            string scenarioName = Argument("-openoita-v2-benchmark-scenario=", "current40k");
            if (!V2BenchmarkScenarioCatalog.TryCreate(scenarioName, out V2BenchmarkScenario scenario))
            {
                Debug.LogError("OpenOita V2 benchmark: unknown scenario " + scenarioName);
                QuitFailedBenchmark();
                yield break;
            }

            int warmup = PositiveArgument("-openoita-v2-benchmark-warmup=", 1000);
            int samples = PositiveArgument("-openoita-v2-benchmark-samples=", 10000);
            string outputDirectory = Argument("-openoita-v2-benchmark-output=",
                Path.Combine(Application.persistentDataPath, "OpenOitaV2Benchmarks"));
            outputDirectory = Path.GetFullPath(outputDirectory);

            if (!TryCreateWorld(scenario, out MaterialWorld world, out string setupError))
            {
                Debug.LogError("OpenOita V2 benchmark: " + setupError);
                QuitFailedBenchmark();
                yield break;
            }
            if (!TryCreateDirectory(outputDirectory, out string directoryError))
            {
                Debug.LogError("OpenOita V2 benchmark: " + directoryError);
                world.Dispose();
                QuitFailedBenchmark();
                yield break;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string path = Path.Combine(outputDirectory, scenario.Name + "-" + stamp + ".csv");
            if (!TryOpenWriter(path, out StreamWriter writer, out string writerError))
            {
                Debug.LogError("OpenOita V2 benchmark: " + writerError);
                world.Dispose();
                QuitFailedBenchmark();
                yield break;
            }

            // Validate the allocation backend before the first measured tick.  Any
            // recorder/thread-counter setup and its positive control stay outside the
            // benchmark scope; unsupported backends remain explicit NA in the CSV.
            bool allowInstrumentedAllocationProbe = Debug.isDebugBuild ||
                HasFlag("-openoita-v2-allocation-validation");
            AllocationProbeInfo allocationProbe = ScopedAllocationProbe.Initialize(
                new AllocationProbeConfiguration(4096, 4096, allowInstrumentedAllocationProbe));

            int invalidSamples = 0;
            int completedSamples = 0;
            double peakTotalMs = 0, peakWallMs = 0;
            var metadata = new Newtonsoft.Json.Linq.JObject
            {
                ["scenario"] = scenario.Name, ["warmup"] = warmup, ["requestedSamples"] = samples,
                ["unityVersion"] = Application.unityVersion, ["developmentBuild"] = Debug.isDebugBuild,
                ["operatingSystem"] = SystemInfo.operatingSystem, ["processor"] = SystemInfo.processorType,
                ["processorCount"] = SystemInfo.processorCount, ["physics"] = scenario.WithPhysics,
                ["nonemptyCells"] = world.QueryMaterialCounts().TotalCells,
                ["width"] = world.Config.Width, ["height"] = world.Config.Height,
                ["stepSeconds"] = world.Config.StepSeconds, ["naturalActivity"] = scenario.NaturalActivity,
                ["configuredActiveCells"] = scenario.ActiveCount,
                ["rotatingBodies"] = scenario.RotatingBodies,
                ["sleepingBodies"] = scenario.SleepBodies,
                ["fracture"] = scenario.IsFracture,
                ["repeatResetBeforeTick"] = scenario.RepeatResetBeforeTick,
                ["fractureSupport"] = scenario.IsFracture
                    ? scenario.FractureSupportX + "," + scenario.FractureSupportY : null,
                ["expectedFractureBodies"] = scenario.IsFracture ? 2 : 0,
                ["targetPeakTotalMs"] = scenario.IsFracture ? 50.0 : 0.0,
                ["targetPeakWallMs"] = scenario.IsFracture ? 50.0 : 0.0,
                ["waterInterval"] = world.Grid.Definitions[101].MoveInterval,
                ["steamInterval"] = world.Grid.Definitions[103].MoveInterval,
                ["steamLifetime"] = world.Grid.Definitions[103].Lifetime,
                ["woodFuel"] = world.Grid.Definitions[104].Fuel,
                ["woodSpreadInterval"] = world.Grid.Definitions[104].SpreadInterval,
                ["allocationSupported"] = allocationProbe.Supported,
                ["allocationInstrumented"] = allocationProbe.Instrumented,
                ["allocationStatus"] = allocationProbe.Status,
                ["allocationMetric"] = allocationProbe.Metric,
                ["allocationPositiveControlBytes"] = allocationProbe.PositiveControlBytes,
                ["allocationEmptyScopeBytes"] = allocationProbe.EmptyScopeBytes,
                ["allocationRecorderSampleCapacity"] = allocationProbe.RecorderSampleCapacity,
                ["graphicsDevice"] = SystemInfo.graphicsDeviceType.ToString()
            };
            File.WriteAllText(path + ".metadata.json", metadata.ToString());
            bool cameraEnabled = ComponentEnabled<Camera>("OpenOitaV2BenchmarkCamera");
            bool directionalLightEnabled = ComponentEnabled<Light>("OpenOitaV2BenchmarkDirectionalLight");
            try
            {
                writer.WriteLine("scenario,phase,tick,generation,camera_enabled,directional_light_enabled,total_ms,timer_ms,flow_ms,events_ms,structure_ms,physics_ms,fixture_ms,wall_tick_ms,reset_ms,peak_total_ms,peak_wall_ms,wake_cells,burning_cells,activity_valid,nonempty_cells,rule_attempts,moves,sleeping,tile_jobs,main_structure_cell_visits,main_structure_edge_visits,coverage_candidate_visits,wet_candidate_visits,body_count,rotating_body_count,sleeping_body_count,body_material_cells,body_events,changed_blocks,suspended_count,allocated_bytes_thread,allocation_status,gc0,gc1,gc2");
                for (int i = 0; i < warmup + samples; i++)
                {
                    bool sample = i >= warmup;
                    if (!TryRunTick(world, scenario, sample, out TickMeasurement measurement, out string tickError))
                    {
                        Debug.LogError("OpenOita V2 benchmark: Tick failed at " + world.Version.CommittedTick + ": " + tickError);
                        break;
                    }
                    if (sample)
                    {
                        peakTotalMs = Math.Max(peakTotalMs, world.Metrics.TotalMs);
                        peakWallMs = Math.Max(peakWallMs, measurement.WallMs);
                        WriteSample(writer, scenario.Name, world, measurement, peakTotalMs, peakWallMs,
                            cameraEnabled, directionalLightEnabled);
                        completedSamples++;
                        if (!measurement.ActivityValid) invalidSamples++;
                    }
                    if ((i & 63) == 63) yield return null;
                }
                Debug.Log("OpenOita V2 benchmark complete: " + path + " (warmup=" + warmup + ", samples=" + completedSamples +
                    ", physics=true, rotatingBodies=" + scenario.RotatingBodies + ", sleepFixture=" + scenario.SleepBodies +
                    ", minimumRuleAttempts=" + scenario.MinimumRuleAttempts + ")");
                if (invalidSamples != 0)
                    Debug.LogWarning("OpenOita V2 benchmark: " + invalidSamples + " sample ticks did not meet the configured active-flow rule/move threshold; inspect activity_valid and moves in the CSV.");
                metadata["completedSamples"] = completedSamples; metadata["invalidSamples"] = invalidSamples;
                metadata["peakTotalMs"] = peakTotalMs; metadata["peakWallMs"] = peakWallMs;
                metadata["complete"] = completedSamples == samples;
                File.WriteAllText(path + ".metadata.json", metadata.ToString());
            }
            finally
            {
                writer.Dispose();
                world.Dispose();
                ScopedAllocationProbe.Shutdown();
            }
            enabled = false;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-openoita-v2-benchmark-exit") >= 0) Application.Quit();
        }

        private static bool TryCreateWorld(V2BenchmarkScenario scenario, out MaterialWorld world, out string error)
        {
            world = null;
            error = null;
            try
            {
                WorldSources sources;
                if (scenario.UseBenchmarkCurrentAsset)
                {
                    OpenOitaMapAsset asset = Resources.Load<OpenOitaMapAsset>("OpenOita/BenchmarkCurrent");
                    if (asset == null)
                    {
                        error = "缺少 Resources/OpenOita/BenchmarkCurrent.asset；请先在Editor菜单创建独立验证场景。";
                        return false;
                    }
                    sources = asset.Sources;
                }
                else
                {
                    WorldResult read = new ConfigurationFileStore().ReadSources(
                        Path.Combine(Application.streamingAssetsPath, "OpenOitaV2"), out WorldSources baseline);
                    if (!read.IsSuccess)
                    {
                        error = "无法读取V2样例配置：" + read.Diagnostic.Message;
                        return false;
                    }
                    sources = scenario.Build(baseline);
                }

                scenario.PrepareWakeCells(sources);
                WorldLoadResult loaded = new WorldSourceLoaderV2().Load(sources);
                if (!loaded.Result.IsSuccess)
                {
                    error = "fixture被拒绝：" + loaded.Result.Diagnostic.Message;
                    return false;
                }
                if (scenario.UseBenchmarkCurrentAsset &&
                    (loaded.Config.Width != scenario.Width || loaded.Config.Height != scenario.Height || loaded.Scene.Cells.Count != scenario.CellCount))
                {
                    error = "BenchmarkCurrent资产尺寸/格数与当前BasicSandbox不一致：" +
                        loaded.Config.Width + "x" + loaded.Config.Height + ", cells=" + loaded.Scene.Cells.Count;
                    return false;
                }
                if (!scenario.UseBenchmarkCurrentAsset && scenario.Layout != V2BenchmarkLayout.Bodies &&
                    loaded.Scene.Cells.Count != scenario.CellCount)
                {
                    error = "field fixture非空格数不正确：期望 " + scenario.CellCount + "，实际 " + loaded.Scene.Cells.Count;
                    return false;
                }

                world = new MaterialWorld(loaded, Vector2.zero, scenario.WithPhysics);
                WorldResult initialized = world.Initialize();
                if (!initialized.IsSuccess)
                {
                    error = "初始化失败：" + initialized.Diagnostic.Message;
                    world.Dispose();
                    world = null;
                    return false;
                }
                if (scenario.HasBodyFixture && world.Physics != null)
                {
                    if (world.Physics.BodyCount != 64 || world.QueryMaterialCounts().TotalCells != scenario.CellCount)
                        throw new InvalidOperationException("物理夹具必须包含64个体并保持配置总格数。");
                    for (int i = 0; i < world.Physics.BodyCount; i++)
                    {
                        BodyV2 body = world.Physics.GetBody(i);
                        var motion = new BodyMotion(Vector2.zero, scenario.RotatingBodies ? 0.5f : 0f);
                        world.Physics.ConfigureBodyMotion(body.Id, motion, scenario.SleepBodies);
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                world?.Dispose();
                world = null;
                error = exception.Message;
                return false;
            }
        }

        private static bool TryCreateDirectory(string path, out string error)
        {
            error = null;
            try { Directory.CreateDirectory(path); return true; }
            catch (Exception exception) { error = "无法创建输出目录：" + exception.Message; return false; }
        }

        private static bool TryOpenWriter(string path, out StreamWriter writer, out string error)
        {
            writer = null;
            error = null;
            try
            {
                writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
                return true;
            }
            catch (Exception exception)
            {
                writer?.Dispose();
                error = "无法创建CSV：" + exception.Message;
                return false;
            }
        }

        private readonly struct TickMeasurement
        {
            internal readonly long Allocated;
            internal readonly bool AllocationUsable;
            internal readonly string AllocationStatus;
            internal readonly int Gc0, Gc1, Gc2, WakeCells, BurningCells, BodyCount, BodyEvents, ChangedBlocks, SuspendedCount;
            internal readonly int RotatingBodies, SleepingBodies, BodyMaterialCells;
            internal readonly int NonemptyCells;
            internal readonly long StructureCells, StructureEdges, CoverageCandidates, WetCandidates;
            internal readonly double FixtureMs, WallMs, ResetMs;
            internal readonly bool ActivityValid;

            internal TickMeasurement(long allocated, bool allocationUsable, string allocationStatus,
                int gc0, int gc1, int gc2, int wakeCells, int burningCells,
                int bodyCount, int bodyEvents, int changedBlocks, int suspendedCount,
                int rotatingBodies, int sleepingBodies, int bodyMaterialCells,
                int nonemptyCells, long structureCells, long structureEdges, long coverageCandidates, long wetCandidates,
                double fixtureMs, double wallMs, double resetMs, bool activityValid)
            {
                Allocated = allocated; AllocationUsable = allocationUsable; AllocationStatus = allocationStatus;
                Gc0 = gc0; Gc1 = gc1; Gc2 = gc2; WakeCells = wakeCells; BurningCells = burningCells;
                BodyCount = bodyCount; BodyEvents = bodyEvents; ChangedBlocks = changedBlocks; SuspendedCount = suspendedCount;
                RotatingBodies = rotatingBodies; SleepingBodies = sleepingBodies; BodyMaterialCells = bodyMaterialCells;
                NonemptyCells = nonemptyCells; StructureCells = structureCells; StructureEdges = structureEdges;
                CoverageCandidates = coverageCandidates; WetCandidates = wetCandidates;
                FixtureMs = fixtureMs; WallMs = wallMs; ResetMs = resetMs; ActivityValid = activityValid;
            }
        }

        private static bool TryRunTick(MaterialWorld world, V2BenchmarkScenario scenario, bool sample,
            out TickMeasurement measurement, out string error)
        {
            measurement = default;
            error = null;
            double resetMs = 0;
            bool allocationScope = false;
            AllocationProbeMeasurement allocationMeasurement = default;
            if (scenario.RepeatResetBeforeTick)
            {
                long resetStart = Stopwatch.GetTimestamp();
                WorldResult reset = world.Reset();
                resetMs = Milliseconds(resetStart);
                if (!reset.IsSuccess)
                {
                    error = "fracture样本Reset失败：" + reset.Diagnostic.Message;
                    return false;
                }
            }
            long wallStart = Stopwatch.GetTimestamp();
            int gc0Before = sample ? GC.CollectionCount(0) : 0;
            int gc1Before = sample ? GC.CollectionCount(1) : 0;
            int gc2Before = sample ? GC.CollectionCount(2) : 0;
            long fixtureStart = Stopwatch.GetTimestamp();
            long structureCellsBefore = world.Physics?.StructureCellVisits ?? 0;
            long structureEdgesBefore = world.Physics?.StructureEdgeVisits ?? 0;
            long coverageCandidatesBefore = world.Physics?.CoverageCandidateVisits ?? 0;
            long wetCandidatesBefore = world.Physics?.WetCandidateVisits ?? 0;
            try
            {
                // Warm the exact instrumented path during the normal 1000-Tick
                // warmup too; otherwise Mono's first probe return path is JITted
                // inside the first measured sample. Never discard a sample row.
                if (ScopedAllocationProbe.IsSupported)
                {
                    ScopedAllocationProbe.Begin();
                    allocationScope = true;
                }
                if (scenario.IsFracture)
                {
                    WorldRect support = new WorldRect(
                        new Vector2(scenario.FractureSupportX * world.Config.CellSize, scenario.FractureSupportY * world.Config.CellSize),
                        new Vector2((scenario.FractureSupportX + 1) * world.Config.CellSize,
                            (scenario.FractureSupportY + 1) * world.Config.CellSize));
                    EnqueueOrThrow(world, new MaterialCommand(MaterialOperation.Remove, support, 0, world.Version.Generation));
                }
                WakeFixture(world, scenario);
                double fixtureMs = Milliseconds(fixtureStart);
                StepResult step = world.Step();
                double wallMs = Milliseconds(wallStart);
                if (allocationScope)
                {
                    allocationMeasurement = ScopedAllocationProbe.End();
                    allocationScope = false;
                }
                long allocated = allocationMeasurement.Usable ? allocationMeasurement.Bytes : -1;
                int gc0 = sample ? GC.CollectionCount(0) - gc0Before : 0;
                int gc1 = sample ? GC.CollectionCount(1) - gc1Before : 0;
                int gc2 = sample ? GC.CollectionCount(2) - gc2Before : 0;
                int rotatingBodies = 0, sleepingBodies = 0, bodyMaterialCells = 0;
                if (world.Physics != null)
                    for (int bodyIndex = 0; bodyIndex < world.Physics.BodyCount; bodyIndex++)
                    {
                        BodyV2 body = world.Physics.GetBody(bodyIndex);
                        if (Math.Abs(body.Motion.AngularVelocityRadians) > 0.001f) rotatingBodies++;
                        if (scenario.HasBodyFixture && world.Physics.IsBodySleeping(body.Id)) sleepingBodies++;
                        bodyMaterialCells += body.Grid.CellCount;
                    }
                int nonemptyCells = world.Grid.CellCount + bodyMaterialCells + (world.Physics?.SuspendedCount ?? 0);
                bool valid = scenario.IsFracture
                    ? world.Physics != null && world.Physics.BodyCount == 2 && bodyMaterialCells == 8192 && world.Grid.CellCount == 0
                    : scenario.RequiresRuleActivity
                    ? world.Metrics.RuleAttempts >= scenario.MinimumRuleAttempts && world.Metrics.Moves > 0
                    : !scenario.RequiresBurningActivity || world.Grid.BurningCount >= scenario.ActiveCount;
                if (scenario.HasBodyFixture)
                    valid &= world.Physics?.BodyCount == 64 && bodyMaterialCells == 8192 &&
                        (scenario.RotatingBodies ? rotatingBodies == 64 : sleepingBodies == 64);
                if (!scenario.UseBenchmarkCurrentAsset)
                    valid &= nonemptyCells == scenario.CellCount - (scenario.IsFracture ? 1 : 0);
                if (scenario.ActivityFixture && scenario.Material == V2BenchmarkMaterial.Mixed)
                    valid &= world.Grid.BurningCount >= scenario.ActiveCount - scenario.ActiveFlowCount;
                string allocationStatus = sample
                    ? (allocationMeasurement.Status ?? ScopedAllocationProbe.Info.Status)
                    : ScopedAllocationProbe.Info.Status;
                measurement = new TickMeasurement(allocated, allocationMeasurement.Usable, allocationStatus,
                    gc0, gc1, gc2, scenario.WakeCells.Count,
                    world.Grid.BurningCount, world.Physics?.BodyCount ?? 0, world.BodyEventCount,
                    world.ChangedBlockCount, world.Physics?.SuspendedCount ?? 0,
                    rotatingBodies, sleepingBodies, bodyMaterialCells,
                    nonemptyCells, (world.Physics?.StructureCellVisits ?? 0) - structureCellsBefore,
                    (world.Physics?.StructureEdgeVisits ?? 0) - structureEdgesBefore,
                    (world.Physics?.CoverageCandidateVisits ?? 0) - coverageCandidatesBefore,
                    (world.Physics?.WetCandidateVisits ?? 0) - wetCandidatesBefore,
                    fixtureMs, wallMs, resetMs, valid);
                if (!step.Result.IsSuccess)
                {
                    error = step.Result.Diagnostic.Message;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                if (allocationScope && ScopedAllocationProbe.IsActive)
                {
                    try { ScopedAllocationProbe.End(); } catch (Exception) { }
                }
                error = exception.Message;
                return false;
            }
        }

        private static void WakeFixture(MaterialWorld world, V2BenchmarkScenario scenario)
        {
            if (scenario.RotatingBodies && world.Physics != null)
                for (int i = 0; i < world.Physics.BodyCount; i++)
                {
                    BodyV2 body = world.Physics.GetBody(i);
                    world.Physics.ConfigureBodyMotion(body.Id, new BodyMotion(body.Motion.LinearVelocity, 0.5f), false);
                }
            if (!scenario.ActivityFixture || scenario.WakeCells.Count == 0 || world.Grid == null) return;
            for (int i = 0; i < scenario.WakeCells.Count; i++)
            {
                Vector2Int position = scenario.WakeCells[i];
                world.Grid.Wake(position.x, position.y);
            }
        }

        private static void WriteSample(StreamWriter writer, string scenario, MaterialWorld world,
            TickMeasurement measurement, double peakTotalMs, double peakWallMs,
            bool cameraEnabled, bool directionalLightEnabled)
        {
            MaterialTickMetrics metrics = world.Metrics;
            WorldVersion version = world.Version;
            writer.Write(scenario); writer.Write(",sample,"); writer.Write(version.CommittedTick);
            writer.Write(','); writer.Write(version.Generation);
            writer.Write(','); writer.Write(cameraEnabled ? 1 : 0);
            writer.Write(','); writer.Write(directionalLightEnabled ? 1 : 0);
            writer.Write(','); writer.Write(metrics.TotalMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(metrics.TimerMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(metrics.FlowMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(metrics.EventsMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(metrics.StructureMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(metrics.PhysicsMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(measurement.FixtureMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(measurement.WallMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(measurement.ResetMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(peakTotalMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(peakWallMs.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(','); writer.Write(measurement.WakeCells);
            writer.Write(','); writer.Write(measurement.BurningCells);
            writer.Write(','); writer.Write(measurement.ActivityValid ? 1 : 0);
            writer.Write(','); writer.Write(measurement.NonemptyCells);
            writer.Write(','); writer.Write(metrics.RuleAttempts);
            writer.Write(','); writer.Write(metrics.Moves);
            writer.Write(','); writer.Write(metrics.Sleeping);
            writer.Write(','); writer.Write(metrics.TileJobs);
            writer.Write(','); writer.Write(measurement.StructureCells);
            writer.Write(','); writer.Write(measurement.StructureEdges);
            writer.Write(','); writer.Write(measurement.CoverageCandidates);
            writer.Write(','); writer.Write(measurement.WetCandidates);
            writer.Write(','); writer.Write(measurement.BodyCount);
            writer.Write(','); writer.Write(measurement.RotatingBodies);
            writer.Write(','); writer.Write(measurement.SleepingBodies);
            writer.Write(','); writer.Write(measurement.BodyMaterialCells);
            writer.Write(','); writer.Write(measurement.BodyEvents);
            writer.Write(','); writer.Write(measurement.ChangedBlocks);
            writer.Write(','); writer.Write(measurement.SuspendedCount);
            writer.Write(',');
            if (measurement.AllocationUsable) writer.Write(measurement.Allocated);
            else writer.Write("NA");
            writer.Write(','); writer.Write(measurement.AllocationStatus);
            writer.Write(','); writer.Write(measurement.Gc0); writer.Write(','); writer.Write(measurement.Gc1); writer.Write(','); writer.Write(measurement.Gc2);
            writer.WriteLine();
        }

        private static void EnqueueOrThrow(MaterialWorld world, in MaterialCommand command)
        {
            EnqueueResult result = world.Enqueue(command);
            if (!result.Result.IsSuccess && result.Result.Status != ResultStatus.Pending)
                throw new InvalidOperationException("fracture命令入队失败：" + result.Result.Diagnostic.Message);
        }

        private static bool ComponentEnabled<T>(string objectName) where T : Behaviour
        {
            GameObject gameObject = GameObject.Find(objectName);
            T component = gameObject == null ? null : gameObject.GetComponent<T>();
            return component != null && component.enabled;
        }

        private static double Milliseconds(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        private static void QuitFailedBenchmark()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-openoita-v2-benchmark-exit") >= 0)
                Application.Quit(1);
        }

        private static bool HasFlag()
        {
            return HasFlag(Flag);
        }

        private static bool HasFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++) if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Argument(string prefix, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return args[i].Substring(prefix.Length);
            return fallback;
        }

        private static int PositiveArgument(string prefix, int fallback)
        {
            string value = Argument(prefix, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return int.TryParse(value, out int result) && result > 0 ? result : fallback;
        }
    }
}
