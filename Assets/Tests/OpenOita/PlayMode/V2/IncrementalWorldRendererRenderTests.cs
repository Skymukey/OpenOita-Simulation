using System;
using OpenOita.V2;
using OpenOita.V2.Render;
using NUnit.Framework;
using OpenOita.Contracts;
using Unity.Collections;
using UnityEngine;

using CellMaterialDefinition = OpenOita.V2.MaterialDefinition;

namespace OpenOita.Tests.PlayMode.V2
{
    public sealed class IncrementalWorldRendererRenderTests
    {
        [Test]
        public void PackedPixelRoundTripsMaterialBurnAndFuel()
        {
            uint packed = IncrementalWorldRenderer.PackPixel(65535, true, 127);

            Assert.That(IncrementalWorldRenderer.UnpackMaterialId(packed), Is.EqualTo((ushort)65535));
            Assert.That(IncrementalWorldRenderer.UnpackBurning(packed), Is.True);
            Assert.That(IncrementalWorldRenderer.UnpackFuelBin(packed), Is.EqualTo((byte)127));
        }

        [Test]
        public void RendererKeepsStableLayerMappingAndFrameVersion()
        {
            V2RenderingResources resources = RequireRenderingResources();
            Shader tileShader = resources.TileShader;
            ComputeShader updateShader = resources.UpdateShader;

            var renderer = new IncrementalWorldRenderer(updateShader, tileShader, resources.FireShader);
            try
            {
                renderer.Initialize(new Color32[4]);
                renderer.BeginFrame(27);
                renderer.SetBodyPose(0, new BodyPose(new Vector2(2, 3), 0));
                renderer.SetTile(0, 0, Vector2.zero, 0.1f);
                renderer.SetTile(128, 0, new Vector2(12.8f, 0), 0.1f);
                renderer.WritePixel(0, 0, 0, IncrementalWorldRenderer.PackPixel(1, false, 255));
                renderer.WritePixel(128, 127, 127, IncrementalWorldRenderer.PackPixel(2, true, 200));
                renderer.FlushFrame();

                Assert.That(renderer.PageCount, Is.EqualTo(4));
                Assert.That(renderer.QueuedVersion, Is.EqualTo(27ul));
                renderer.BeginFrame(28);
                renderer.WritePixel(0, 1, 1, IncrementalWorldRenderer.PackPixel(1, false, 255));
                renderer.FlushFrame();
                Assert.That(renderer.QueuedVersion, Is.EqualTo(28ul));
            }
            finally
            {
                renderer.Dispose();
                renderer.Dispose();
            }
        }

        [Test]
        public void RendererRejectsPixelWritesOutsideTile()
        {
            V2RenderingResources resources = RequireRenderingResources();
            Shader tileShader = resources.TileShader;
            ComputeShader updateShader = resources.UpdateShader;

            var renderer = new IncrementalWorldRenderer(updateShader, tileShader);
            try
            {
                renderer.Initialize(new Color32[1]);
                renderer.BeginFrame(1);
                renderer.SetTile(0, 0, Vector2.zero, 1);
                Assert.Throws<ArgumentOutOfRangeException>(() => renderer.WritePixel(0, 128, 0, 1));
                Assert.Throws<InvalidOperationException>(() => renderer.WritePixel(1, 0, 0, 1));
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void RendererRejectsMissingComputeResource()
        {
            var renderer = new IncrementalWorldRenderer(null);
            try
            {
                Assert.Throws<InvalidOperationException>(() => renderer.Initialize(new Color32[1]));
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void RendererDeduplicatesRepeatedPixelWrites()
        {
            V2RenderingResources resources = RequireRenderingResources();
            Shader tileShader = resources.TileShader;
            ComputeShader updateShader = resources.UpdateShader;

            var renderer = new IncrementalWorldRenderer(updateShader, tileShader);
            try
            {
                renderer.Initialize(new Color32[2]);
                renderer.BeginFrame(1);
                renderer.SetTile(0, 0, Vector2.zero, 1);
                for (int tick = 1; tick <= 100; tick++)
                {
                    if (tick > 1) renderer.BeginFrame((ulong)tick);
                    renderer.WritePixel(0, 4, 5,
                        IncrementalWorldRenderer.PackPixel(1, tick % 2 == 0, (byte)(tick - 1)));
                    renderer.FlushFrame();
                }

                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(1));
                Assert.That(renderer.TryReadCachedPixel(0, 4, 5, out uint packed), Is.True);
                Assert.That(packed, Is.EqualTo(IncrementalWorldRenderer.PackPixel(1, true, 99)));
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public unsafe void RendererBatchMatchesPixelPackingAndDeduplicatesAcrossTicks()
        {
            V2RenderingResources resources = RequireRenderingResources();
            NativeArray<CellMaterialDefinition> definitions = new NativeArray<CellMaterialDefinition>(65536,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            MaterialGrid grid = new MaterialGrid(32, 32, definitions);
            var renderer = new IncrementalWorldRenderer(resources.UpdateShader, resources.TileShader);
            try
            {
                MaterialTile tile = grid.EnsureTile(0);
                const int x = 4, y = 5, position = x + y * 32;
                tile.Material[position] = 1;
                tile.Occupied[y] |= 1u << x;
                renderer.Initialize(new Color32[1]);
                renderer.BeginFrame(1);
                renderer.SetTile(0, 0, Vector2.zero, 1);
                renderer.AddPixelBatchOperation(0, 0, 0, true, tile, 0, definitions,
                    grid.ColdStore.Records.AsArray(), grid.VisualTiles, 1);
                renderer.FlushFrame();
                Assert.That(renderer.TryReadCachedPixel(0, x, y, out uint first), Is.True);
                Assert.That(first, Is.EqualTo(IncrementalWorldRenderer.PackPixel(1, false, 255)));
                renderer.CompleteFrameAfterRenderGraphRecording();

                for (ulong tick = 2; tick <= 100; tick++)
                {
                    renderer.BeginFrame(tick);
                    tile.Visual[y] |= 1u << x;
                    grid.VisualTiles[0] = 1;
                    renderer.AddPixelBatchOperation(0, 0, 0, false, tile, 0, definitions,
                        grid.ColdStore.Records.AsArray(), grid.VisualTiles, tick);
                    renderer.FlushFrame();
                    Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(1));
                    Assert.That(renderer.TryReadCachedPixel(0, x, y, out uint packed), Is.True);
                    Assert.That(packed, Is.EqualTo(first));
                    renderer.CompleteFrameAfterRenderGraphRecording();
                }
            }
            finally
            {
                renderer.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public unsafe void RendererBatchClearsEmptyCellAndBurningBookkeeping()
        {
            V2RenderingResources resources = RequireRenderingResources();
            NativeArray<CellMaterialDefinition> definitions = new NativeArray<CellMaterialDefinition>(65536,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            definitions[1] = new CellMaterialDefinition { Id = 1, Fuel = 10 };
            MaterialGrid grid = new MaterialGrid(32, 32, definitions);
            var renderer = new IncrementalWorldRenderer(resources.UpdateShader, resources.TileShader);
            try
            {
                MaterialTile tile = grid.EnsureTile(0);
                const int x = 4, y = 5, position = x + y * 32;
                tile.Material[position] = 1;
                tile.Flags[position] = GridCell.BurningFlag;
                tile.Component[position] = grid.ColdStore.Create(new CellCold
                {
                    MaterialId = 1,
                    BurnEndTick = 100
                });
                tile.Occupied[y] |= 1u << x;
                renderer.Initialize(new Color32[1]);
                renderer.BeginFrame(1);
                renderer.SetTile(0, 0, Vector2.zero, 1);
                renderer.AddPixelBatchOperation(0, 0, 0, true, tile, 0, definitions,
                    grid.ColdStore.Records.AsArray(), grid.VisualTiles, 1);
                renderer.FlushFrame();
                Assert.That(renderer.TryReadCachedPixel(0, x, y, out uint burning), Is.True);
                Assert.That(IncrementalWorldRenderer.UnpackBurning(burning), Is.True);
                Assert.That(IncrementalWorldRenderer.UnpackFuelBin(burning), Is.EqualTo((byte)255));
                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(1));
                renderer.CompleteFrameAfterRenderGraphRecording();

                tile.Material[position] = 0;
                tile.Flags[position] = 0;
                tile.Visual[y] |= 1u << x;
                grid.VisualTiles[0] = 1;
                renderer.BeginFrame(2);
                renderer.AddPixelBatchOperation(0, 0, 0, false, tile, 0, definitions,
                    grid.ColdStore.Records.AsArray(), grid.VisualTiles, 2);
                renderer.FlushFrame();
                Assert.That(renderer.TryReadCachedPixel(0, x, y, out uint empty), Is.True);
                Assert.That(empty, Is.EqualTo(0u));
                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(1));
            }
            finally
            {
                renderer.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public unsafe void RendererReservesQueuedCapacityAndKeepsOldAndBatchUpdates()
        {
            V2RenderingResources resources = RequireRenderingResources();
            NativeArray<CellMaterialDefinition> definitions = new NativeArray<CellMaterialDefinition>(65536,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            MaterialGrid grid = new MaterialGrid(32, 32, definitions);
            var renderer = new IncrementalWorldRenderer(resources.UpdateShader, resources.TileShader);
            try
            {
                renderer.Initialize(new Color32[1]);
                FillOneQueuedPage(renderer);
                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(65536));

                // The old queue has not entered RenderGraph yet, so the frame barrier may
                // replace its GraphicsBuffer while preserving the NativeList contents.
                renderer.ReserveFrameCapacity(131072, 512, 1);
                MaterialTile tile = grid.EnsureTile(0);
                for (int row = 0; row < 32; row++)
                {
                    tile.Occupied[row] = uint.MaxValue;
                    for (int column = 0; column < 32; column++) tile.Material[row * 32 + column] = 1;
                }
                renderer.BeginFrame(2);
                renderer.SetTile(4, 0, Vector2.zero, 1);
                renderer.AddPixelBatchOperation(4, 0, 0, true, tile, 0, definitions,
                    grid.ColdStore.Records.AsArray(), grid.VisualTiles, 2);
                renderer.FlushFrame();

                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(65536 + 1024));
                Assert.That(renderer.TryReadCachedPixel(0, 0, 0, out uint oldPixel), Is.True);
                Assert.That(oldPixel, Is.EqualTo(IncrementalWorldRenderer.PackPixel(1, true, 255)));
                Assert.That(renderer.TryReadCachedPixel(4, 31, 31, out uint newPixel), Is.True);
                Assert.That(newPixel, Is.EqualTo(IncrementalWorldRenderer.PackPixel(1, false, 255)));
            }
            finally
            {
                renderer.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public unsafe void RendererBatchCapacityRejectPreservesDirtyStateAndAllowsRetry()
        {
            V2RenderingResources resources = RequireRenderingResources();
            NativeArray<CellMaterialDefinition> definitions = new NativeArray<CellMaterialDefinition>(65536,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            MaterialGrid grid = new MaterialGrid(32, 32, definitions);
            var renderer = new IncrementalWorldRenderer(resources.UpdateShader, resources.TileShader);
            try
            {
                renderer.Initialize(new Color32[1]);
                FillOneQueuedPage(renderer);
                MaterialTile tile = grid.EnsureTile(0);
                for (int row = 0; row < 32; row++)
                {
                    tile.Occupied[row] = uint.MaxValue;
                    for (int column = 0; column < 32; column++) tile.Material[row * 32 + column] = 1;
                    tile.Visual[row] = uint.MaxValue;
                }
                grid.VisualTiles[0] = 1;

                renderer.BeginFrame(2);
                renderer.SetTile(4, 0, Vector2.zero, 1);
                renderer.AddPixelBatchOperation(4, 0, 0, true, tile, 0, definitions,
                    grid.ColdStore.Records.AsArray(), grid.VisualTiles, 2);
                Assert.Throws<InvalidOperationException>(() => renderer.FlushFrame());

                Assert.That(tile.Visual[0], Is.EqualTo(uint.MaxValue));
                Assert.That(grid.VisualTiles[0], Is.EqualTo(1));
                Assert.That(renderer.TryReadCachedPixel(0, 0, 0, out uint retained), Is.True);
                Assert.That(retained, Is.EqualTo(IncrementalWorldRenderer.PackPixel(1, true, 255)));
                Assert.That(renderer.TryReadCachedPixel(4, 0, 0, out uint rejected), Is.True);
                Assert.That(rejected, Is.EqualTo(0u));
                Assert.That(IncrementalWorldRenderer.UnpackBurning(retained), Is.True);

                renderer.ReserveFrameCapacity(131072, 512, 1);
                renderer.BeginFrame(3);
                renderer.AddPixelBatchOperation(4, 0, 0, false, tile, 0, definitions,
                    grid.ColdStore.Records.AsArray(), grid.VisualTiles, 3);
                renderer.FlushFrame();

                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo(65536 + 1024));
                Assert.That(renderer.TryReadCachedPixel(4, 31, 31, out uint retried), Is.True);
                Assert.That(retried, Is.EqualTo(IncrementalWorldRenderer.PackPixel(1, false, 255)));
                renderer.CompleteFrameAfterRenderGraphRecording();
            }
            finally
            {
                renderer.Dispose();
                grid.Dispose();
                definitions.Dispose();
            }
        }

        [Test]
        public void RendererDeduplicatesRepeatedBodyPoseWrites()
        {
            V2RenderingResources resources = RequireRenderingResources();
            Shader tileShader = resources.TileShader;
            ComputeShader updateShader = resources.UpdateShader;

            var renderer = new IncrementalWorldRenderer(updateShader, tileShader);
            try
            {
                renderer.Initialize(new Color32[1]);
                renderer.BeginFrame(1);
                var pose = new BodyPose(new Vector2(4, -2), 0.25f);
                renderer.SetBodyPose(0, pose);
                renderer.SetBodyPose(0, pose);
                renderer.FlushFrame();

                Assert.That(renderer.PendingBodyPoseUpdateCount, Is.EqualTo(1));
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void RendererClearsOneBusyLayerWithoutDroppingOtherPageLayers()
        {
            V2RenderingResources resources = RequireRenderingResources();
            var renderer = new IncrementalWorldRenderer(resources.UpdateShader, resources.TileShader);
            const int layerCount = IncrementalWorldRenderer.LayersPerPage;
            const int writesPerLayer = 32;
            const int clearedLayer = 7;
            try
            {
                renderer.Initialize(new Color32[2]);
                renderer.BeginFrame(1);
                for (int layer = 0; layer < layerCount; layer++)
                {
                    renderer.SetTile(layer, 0, Vector2.zero, 1);
                    for (int i = 0; i < writesPerLayer; i++)
                    {
                        int x = i & 31;
                        int y = i >> 5;
                        renderer.WritePixel(layer, x, y,
                            IncrementalWorldRenderer.PackPixel((ushort)(layer + 1), false, (byte)i));
                    }
                }

                long probesBeforeClear = renderer.ClearPixelLayerProbeCount;
                renderer.SetTile(clearedLayer, 0, Vector2.zero, 1);
                Assert.That(renderer.ClearPixelLayerProbeCount - probesBeforeClear,
                    Is.EqualTo(IncrementalWorldRenderer.TileSide * IncrementalWorldRenderer.TileSide));
                Assert.That(renderer.TryReadCachedPixel(clearedLayer, 0, 0, out uint cleared), Is.True);
                Assert.That(cleared, Is.EqualTo(0u));

                for (int layer = 0; layer < layerCount; layer++)
                {
                    if (layer == clearedLayer) continue;
                    Assert.That(renderer.TryReadCachedPixel(layer, 0, 0, out uint retained), Is.True);
                    Assert.That(retained,
                        Is.EqualTo(IncrementalWorldRenderer.PackPixel((ushort)(layer + 1), false, 0)));
                }

                renderer.FlushFrame();
                Assert.That(renderer.PendingPixelUpdateCount, Is.EqualTo((layerCount - 1) * writesPerLayer));
                renderer.CompleteFrameAfterRenderGraphRecording();
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void RendererRemovalDeactivatesAndClearsTileCache()
        {
            V2RenderingResources resources = RequireRenderingResources();
            Shader tileShader = resources.TileShader;
            ComputeShader updateShader = resources.UpdateShader;

            var renderer = new IncrementalWorldRenderer(updateShader, tileShader);
            try
            {
                renderer.Initialize(new Color32[1]);
                renderer.BeginFrame(1);
                renderer.SetTile(0, 0, Vector2.zero, 1);
                renderer.WritePixel(0, 3, 4, IncrementalWorldRenderer.PackPixel(1, false, 0));
                renderer.RemoveTile(0);

                Assert.That(renderer.TryReadCachedPixel(0, 3, 4, out _), Is.False);
            }
            finally
            {
                renderer.Dispose();
            }
        }

        private static V2RenderingResources RequireRenderingResources()
        {
            V2RenderingResources resources = Resources.Load<V2RenderingResources>("OpenOita/V2Rendering");
            if (resources == null) Assert.Fail("缺少 Resources/OpenOita/V2Rendering.asset。");
            if (!resources.IsComplete) Assert.Fail("V2Rendering.asset 未绑定完整的 Compute/WorldPage/WorldFire 资源。");
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("当前图形设备不支持 ComputeShader。");
            return resources;
        }

        private static void FillOneQueuedPage(IncrementalWorldRenderer renderer)
        {
            renderer.BeginFrame(1);
            for (int layer = 0; layer < 4; layer++)
            {
                renderer.SetTile(layer, 0, Vector2.zero, 1);
                for (int y = 0; y < IncrementalWorldRenderer.TileSide; y++)
                    for (int x = 0; x < IncrementalWorldRenderer.TileSide; x++)
                    {
                        bool burning = layer == 0 && x == 0 && y == 0;
                        renderer.WritePixel(layer, x, y,
                            IncrementalWorldRenderer.PackPixel((ushort)(layer + 1), burning, 255));
                    }
            }
            renderer.FlushFrame();
        }
    }
}
