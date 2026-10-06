using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Preview;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Preview
{
    [Category("LiquidLevelingIntegration")]
    public sealed class LiquidPreviewRegressionTests
    {
        [Test]
        public void L01_08_Real256PreviewConservesWaterExtinguishesAndRemainsStillAfterTerrainSettles()
        {
            using var preview = new ModulePreviewSession(BaselineSources.Read(), 0, 1);
            Assert.That(preview.View.Config.Width, Is.EqualTo(256));
            Assert.That(preview.View.Config.Height, Is.EqualTo(256));
            Assert.That(ModulePreviewSession.LayoutScale, Is.EqualTo(8));
            Assert.That(preview.View.Version.CommittedTick, Is.Zero);
            // 此预设没有可用流体编辑按钮；完整记录为Tick0初态，随后无外部注水/删改命令。
            var concrete = new HashSet<int>();
            var water = new List<int>(1024);
            var wood = new List<int>(384);
            var previousWater = new List<int>(1024);
            var previousWood = new List<int>(384);
            var support = new HashSet<int>();
            var observations = new StringBuilder("tick,water_count,water_moves,water_hash,wood_count,burning_count,terrain_changed,high_water_count,holes,stable_ticks\n");
            foreach (CellKey key in preview.View.OccupiedCells)
            {
                Assert.That(key.Position.OwnerKind, Is.EqualTo(OwnerKind.Grid));
                preview.View.Read(key, out CellSnapshot state);
                int position = key.Position.Y * 256 + key.Position.X;
                if (state.MaterialId == 102) concrete.Add(position);
                if (state.MaterialId == 101) previousWater.Add(position);
                if (state.MaterialId == 104) previousWood.Add(position);
            }
            Assert.That(previousWater.Count, Is.EqualTo(1024));
            Assert.That(previousWood.Count, Is.EqualTo(384));
            Assert.That(concrete.Count, Is.EqualTo(2944));
            Assert.That(preview.EditAt(64, 56, true), Is.False, "流体预设的调试编辑入口保持禁用。");
            Assert.That(preview.View.Version.CommittedTick, Is.Zero);

            int stableTicks = 0, firstStableTick = 0, lastTerrainChange = 0, lastWaterMove = 0;
            bool sawBurning = false, sawExtinguishedWood = false, sawHorizontalSpread = false;
            try
            {
                for (int tick = 1; tick <= 1152 && stableTicks < 128; tick++)
                {
                    Assert.That(preview.Step(), Is.True, $"Tick {tick}：{preview.Error}");
                    Assert.That(preview.View.Version.CommittedTick, Is.EqualTo((ulong)tick));
                    Assert.That(preview.View.Bodies.Length, Is.Zero, "流体预设不应靠未接入物理的材料体改变地形。");
                    water.Clear();
                    wood.Clear();
                    support.Clear();
                    int concreteCount = 0, burningCount = 0, highWater = 0;
                    ulong waterHash = 14695981039346656037UL;
                    // 只遍历已占据格；复用列表和集合，不逐Tick扫描65536格或复制全部快照。
                    foreach (CellKey key in preview.View.OccupiedCells)
                    {
                        Assert.That(preview.View.Read(key, out CellSnapshot state).IsSuccess, Is.True);
                        int x = key.Position.X, y = key.Position.Y;
                        int position = y * 256 + x;
                        if (state.MaterialId == 101)
                        {
                            water.Add(position);
                            support.Add(position);
                            waterHash = unchecked((waterHash ^ (uint)position) * 1099511628211UL);
                            Assert.That(x, Is.InRange(40, 215), $"Tick {tick}水不可穿过槽壁。");
                            Assert.That(y, Is.InRange(56, 199), $"Tick {tick}水不可穿槽底或向上搬运。");
                            if (y >= 64) highWater++;
                            if (x >= 104) sawHorizontalSpread = true;
                        }
                        else if (state.MaterialId == 102)
                        {
                            concreteCount++;
                            support.Add(position);
                            Assert.That(concrete.Contains(position), Is.True, $"Tick {tick}固定混凝土不可移位。");
                        }
                        else if (state.MaterialId == 104)
                        {
                            wood.Add(position);
                            support.Add(position);
                            if (state.IsBurning) burningCount++;
                            else if (state.FuelTicksRemaining > 0 && state.FuelTicksRemaining < 250) sawExtinguishedWood = true;
                        }
                    }
                    Assert.That(water.Count, Is.EqualTo(1024), $"Tick {tick}水量必须守恒。");
                    Assert.That(concreteCount, Is.EqualTo(concrete.Count), $"Tick {tick}固定槽不可缺格。");
                    sawBurning |= burningCount > 0;
                    bool terrainChanged = !SamePositions(previousWood, wood);
                    if (terrainChanged) lastTerrainChange = tick;
                    if (preview.LastWaterMoves > 0) lastWaterMove = tick;
                    int holes = 0;
                    foreach (int position in water)
                        if (!support.Contains(position - 256)) holes++;
                    bool settled = !terrainChanged && burningCount == 0 && highWater == 0 && holes == 0 &&
                        preview.LastWaterMoves == 0 && SamePositions(previousWater, water);
                    if (settled)
                    {
                        if (stableTicks == 0) firstStableTick = tick;
                        stableTicks++;
                    }
                    else { stableTicks = 0; firstStableTick = 0; }
                    observations.Append(tick).Append(',').Append(water.Count).Append(',').Append(preview.LastWaterMoves)
                        .Append(',').Append(waterHash.ToString("X16")).Append(',').Append(wood.Count).Append(',')
                        .Append(burningCount).Append(',').Append(terrainChanged ? 1 : 0).Append(',').Append(highWater)
                        .Append(',').Append(holes).Append(',').Append(stableTicks).Append('\n');
                    previousWater.Clear(); previousWater.AddRange(water);
                    previousWood.Clear(); previousWood.AddRange(wood);
                }
                Assert.That(sawBurning, Is.True, "真实预览回归应保留燃烧阶段。");
                Assert.That(sawExtinguishedWood, Is.True, "至少一个已消耗燃料的木格被水熄灭。");
                Assert.That(sawHorizontalSpread, Is.True, "水应越过初始木平台右缘到可达低处。");
                Assert.That(firstStableTick, Is.InRange(1, 1024), "真实预览应在1024Tick内达到低处并开始稳定观察。");
                Assert.That(stableTicks, Is.EqualTo(128), "地形和火焰稳定后须连续128Tick水占据不变且成功搬运为0。");
                TestContext.WriteLine($"L01-08：初态Tick0，无外部命令；首次稳定Tick={firstStableTick}；最后地形变化={lastTerrainChange}；最后水搬运={lastWaterMove}；稳定观察={stableTicks}；剩余木头={wood.Count}。");
            }
            finally
            {
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "liquid-l01-preview-observations.csv");
                File.WriteAllText(path, observations.ToString(), new UTF8Encoding(false));
                TestContext.WriteLine("逐Tick预览证据：" + path);
            }
        }

        private static bool SamePositions(List<int> first, List<int> second)
        {
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++) if (first[i] != second[i]) return false;
            return true;
        }
    }
}
