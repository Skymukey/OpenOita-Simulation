using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode.M02
{
    public sealed class StateLifetimeTests
    {
        [UnityTest]
        public IEnumerator M02_02_07_StateSizeAndTwentyNativeOwnersInEditorPlayMode()
        {
            string folder = Path.Combine(Application.streamingAssetsPath, "OpenOita");
            Assert.That(new ConfigurationFileStore().ReadSources(folder, out WorldSources sources).IsSuccess, Is.True);
            WorldLoadResult loaded = new WorldSourceLoader().Load(sources);
            Assert.That(loaded.Result.IsSuccess, Is.True);
            Assert.That(Marshal.SizeOf<CellState>(), Is.EqualTo(32));
            Assert.That(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CellState>(), Is.EqualTo(32));
            for (ulong generation = 1; generation <= 20; generation++)
            {
                Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, generation, out WorkingWorld state).IsSuccess, Is.True);
                try
                {
                    Assert.That(state.Published.Version.Generation, Is.EqualTo(generation));
                    Assert.That(state.MaterialCells, Is.EqualTo(175));
                    for (int tick = 1; tick <= 100; tick++)
                    {
                        Assert.That(state.BeginTick().IsSuccess, Is.True);
                        Assert.That(state.PublishState().IsSuccess, Is.True);
                    }
                    Assert.That(state.Published.Version.CommittedTick, Is.EqualTo(100));
                }
                finally { state.Dispose(); }
                Assert.That(state.ChunkCount, Is.Zero);
                state.Dispose();
                yield return null;
            }
            // 仅状态核心和 Editor PlayMode；不将此记录当作完整世界 Reset/物理/GPU 或 Player 验收。
            LogAssert.NoUnexpectedReceived();
        }
    }
}
