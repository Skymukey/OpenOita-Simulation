using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    public readonly struct TimedCommand
    {
        public readonly ulong Tick;
        public readonly MaterialCommand Command;
        public TimedCommand(ulong tick, MaterialCommand command) { Tick = tick; Command = command; }
    }
    public readonly struct FixtureObservation
    {
        public readonly ulong Tick;
        public readonly TickStage Stage;
        public readonly string Expected;
        public FixtureObservation(ulong tick, TickStage stage, string expected) { Tick = tick; Stage = stage; Expected = expected; }
    }
    public readonly struct BodyProbeState
    {
        // 根据初始化前原主网格分量最小格定位，绝不猜测创建后的 BodyId。
        public readonly Vector2Int OriginalMinimum;
        public readonly BodyPose Pose;
        public readonly BodyMotion Motion;
        public BodyProbeState(Vector2Int originalMinimum, BodyPose pose, BodyMotion motion)
        {
            OriginalMinimum = originalMinimum; Pose = pose; Motion = motion;
        }
    }
    public sealed class ScenarioFixture
    {
        public string Id { get; }
        public WorldConfig Config { get; }
        public SceneInitialData Scene { get; }
        public IReadOnlyList<TimedCommand> Commands { get; }
        public IReadOnlyList<BodyProbeState> BodiesAfterCreate { get; }
        public IReadOnlyList<FixtureObservation> Observations { get; }
        public int ResetEveryTicks { get; }
        public IReadOnlyList<CellStateProbe> CellsAfterCreate { get; }
        public ScenarioFixture(string id, WorldConfig config, SceneInitialData scene, IEnumerable<TimedCommand> commands,
            IEnumerable<BodyProbeState> bodies, IEnumerable<FixtureObservation> observations, int resetEveryTicks = 0,
            IEnumerable<CellStateProbe> cellsAfterCreate = null)
        {
            Id = id; Config = config; Scene = scene;
            Commands = new List<TimedCommand>(commands).AsReadOnly();
            BodiesAfterCreate = new List<BodyProbeState>(bodies).AsReadOnly();
            Observations = new List<FixtureObservation>(observations).AsReadOnly();
            ResetEveryTicks = resetEveryTicks;
            CellsAfterCreate = new List<CellStateProbe>(cellsAfterCreate ?? Array.Empty<CellStateProbe>()).AsReadOnly();
        }
    }

    public readonly struct CellStateProbe
    {
        public readonly Vector2Int OriginalBodyMinimum;
        public readonly Vector2Int LocalCell;
        public readonly CellSnapshot State;
        public CellStateProbe(Vector2Int originalBodyMinimum, Vector2Int localCell, CellSnapshot state)
        {
            OriginalBodyMinimum = originalBodyMinimum; LocalCell = localCell; State = state;
        }
    }

    // 仅测试程序集可消费；M02/M06 提供内部适配器。不是公开速度/冲量 API。
    public interface ITestWorldProbe
    {
        WorldResult SetBodyAfterCreate(in BodyProbeState state);
        WorldResult SetCellAfterCreate(in CellStateProbe state);
        // 子步候选故障探针，不推进/替换宿主默认物理场景。
        void InjectNextPhysicsCandidates(IPhysicsStepView candidates);
        void InstallFailure(IFailureInjector injector);
        IWorkingWorldView InspectAtStage(TickStage stage);
        ITickInstanceMap Instances { get; }
        ITickChangeCounter Changes { get; }
        ResourceBudget Resources { get; }
    }
    public sealed class FailOnceInjector : IFailureInjector
    {
        private readonly FailurePoint _point;
        private bool _fired;
        public FailOnceInjector(FailurePoint point) { _point = point; }
        public WorldResult Check(in TransactionContext context, FailurePoint point)
        {
            if (_fired || point != _point) return WorldResult.Success();
            _fired = true;
            return WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic(context.Stage.ToString(), point.ToString(), "夹具注入一次失败。"));
        }
    }
}
