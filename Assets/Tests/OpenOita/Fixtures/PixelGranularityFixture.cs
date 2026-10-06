using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    public readonly struct PixelState
    {
        public readonly Vector2Int Position;
        public readonly CellSnapshot State;

        public PixelState(int x, int y, CellSnapshot state)
        {
            Position = new Vector2Int(x, y);
            State = state;
        }
    }

    // 同一份逻辑状态可供状态验收和后续1:1/3倍显示验收使用，不含屏幕倍率。
    // AuthoredStates在Tick1通过真实候选写入；不声称这些探针字段可由场景JSON保存。
    public sealed class PixelGranularityFixture
    {
        public WorldConfig Config { get; }
        public SceneInitialData Scene { get; }
        public Vector2Int LogicalMinimum { get; }
        public Vector2Int LogicalSize { get; }
        public IReadOnlyList<PixelState> AuthoredStates { get; }
        public Vector2Int SinglePixelHole => LogicalMinimum + new Vector2Int(3, 3);

        private PixelGranularityFixture(Vector2Int minimum, Vector2Int size, List<PixelState> states,
            IEnumerable<Vector2Int> fixedCells = null)
        {
            LogicalMinimum = minimum;
            LogicalSize = size;
            Config = FixtureCatalog.Config();
            AuthoredStates = new List<PixelState>(states).AsReadOnly();
            var cells = new List<InitialCell>();
            foreach (PixelState pixel in states)
                cells.Add(new InitialCell(pixel.Position.x, pixel.Position.y, pixel.State.MaterialId));
            Scene = FixtureCatalog.Scene(cells, fixedCells);
        }

        public static PixelGranularityFixture IndependentMaterials8x8(int minimum = 10)
        {
            var states = new List<PixelState>();
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int index = y * 8 + x;
                    ushort material = (ushort)(101 + x % 4);
                    CellSnapshot state = material == 101 ? new CellSnapshot(101, moveCountdown: (uint)(y % 3)) :
                        material == 102 ? new CellSnapshot(102) :
                        material == 103 ? new CellSnapshot(103, lifetimeTicksRemaining: (uint)(80 + index), moveCountdown: (uint)(y % 4)) :
                        new CellSnapshot(104, (ushort)(y % 2), (uint)(64 + index), (uint)(2 + y % 8), ignitedTick: (ulong)(y % 2));
                    states.Add(new PixelState(minimum + x, minimum + y, state));
                }
            return new PixelGranularityFixture(new Vector2Int(minimum, minimum), new Vector2Int(8, 8), states);
        }

        public static PixelGranularityFixture MixedSolid8x8(int minimum = 10, bool withHole = false)
        {
            var states = new List<PixelState>();
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    if (withHole && x == 3 && y == 3) continue;
                    int index = y * 8 + x;
                    CellSnapshot state = (x + y) % 2 == 0 ? new CellSnapshot(102) :
                        new CellSnapshot(104, (ushort)(y % 2), (uint)(64 + index), (uint)(2 + y % 8), ignitedTick: (ulong)(y % 2));
                    states.Add(new PixelState(minimum + x, minimum + y, state));
                }
            return new PixelGranularityFixture(new Vector2Int(minimum, minimum), new Vector2Int(8, 8), states);
        }

        public static PixelGranularityFixture AdjacentFuel(int x = 10)
        {
            var states = new List<PixelState>
            {
                new PixelState(x, 10, new CellSnapshot(104, 1, 1, 4)),
                new PixelState(x + 1, 10, new CellSnapshot(104, 1, 9, 7))
            };
            return new PixelGranularityFixture(new Vector2Int(x, 10), new Vector2Int(2, 1), states,
                new[] { new Vector2Int(x, 10) });
        }

        public static PixelGranularityFixture AdjacentWater(int x = 10)
        {
            var states = new List<PixelState>
            {
                new PixelState(x, 5, new CellSnapshot(101)),
                new PixelState(x + 1, 5, new CellSnapshot(101)),
                new PixelState(x + 1, 4, new CellSnapshot(102)),
                new PixelState(x + 2, 5, new CellSnapshot(102))
            };
            return new PixelGranularityFixture(new Vector2Int(x, 4), new Vector2Int(3, 2), states,
                new[] { new Vector2Int(x + 1, 4), new Vector2Int(x + 2, 5) });
        }

        // 只导出供M06后续采用的初始位姿；不模拟旋转或伪造物理通过。
        public BodyProbeState BodyProbe(bool rotated)
        {
            return new BodyProbeState(LogicalMinimum,
                new BodyPose(new Vector2(LogicalMinimum.x * Config.CellSize, LogicalMinimum.y * Config.CellSize),
                    rotated ? (float)(Math.PI / 6) : 0),
                new BodyMotion(rotated ? new Vector2(0.25f, 0.1f) : Vector2.zero, rotated ? 0.5f : 0));
        }
    }
}
