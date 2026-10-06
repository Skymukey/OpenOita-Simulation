using System;
using OpenOita.Contracts;

namespace OpenOita.Host
{
    internal sealed class FixedStepDriver
    {
        private readonly IWorld _world;
        private readonly double _stepSeconds;
        private bool _executing;
        internal bool Automatic { get; }
        internal double AccumulatedSeconds { get; private set; }

        internal FixedStepDriver(IWorld world, bool automatic)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _stepSeconds = world.Config.StepSeconds;
            if (double.IsNaN(_stepSeconds) || double.IsInfinity(_stepSeconds) || _stepSeconds <= 0)
                throw new ArgumentException("世界固定步长无效。", nameof(world));
            Automatic = automatic;
        }

        internal WorldResult Advance(double frameSeconds, int maxStepsPerFrame = 8)
        {
            if (!Automatic || _executing) return Busy();
            if (double.IsNaN(frameSeconds) || double.IsInfinity(frameSeconds) || frameSeconds < 0 || maxStepsPerFrame < 1 ||
                double.IsInfinity(AccumulatedSeconds + frameSeconds))
                return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("Host", "deltaTime", "帧时长或步数预算无效。"));
            AccumulatedSeconds += frameSeconds;
            _executing = true;
            try
            {
                for (int step = 0; step < maxStepsPerFrame && AccumulatedSeconds >= _stepSeconds; step++)
                {
                    StepResult result = _world.Step();
                    if (!result.Result.IsSuccess) return result.Result;
                    AccumulatedSeconds -= _stepSeconds;
                }
                return WorldResult.Success();
            }
            finally { _executing = false; }
        }

        internal StepResult StepManual()
        {
            if (Automatic || _executing) return new StepResult(Busy(), _world.Version, null);
            _executing = true;
            try { return _world.Step(); }
            finally { _executing = false; }
        }

        private static WorldResult Busy() => WorldResult.Failure(WorldErrorCode.Busy,
            new WorldDiagnostic("Host", "driver", "自动和手动驱动互斥，推进中不允许重入。"));
    }
}
