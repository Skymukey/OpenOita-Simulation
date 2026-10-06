using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Preview;
using OpenOita.Rules;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Preview
{
    public sealed class PixelPreviewTextureTests
    {
        [Test]
        public void AdjacentWoodWithDifferentFuelKeepsDifferentTexelsAndExtinguishingRetainsBurnDamage()
        {
            using var session = new ModulePreviewSession(BaselineSources.Read(), 1, 1);
            using var textures = new PixelPreviewTextures();
            for (int i = 0; i < 20; i++) Assert.That(session.Step(), Is.True, session.Error);
            textures.Refresh(session.View);
            Texture2D grid = textures.Layers[0].Texture;
            var burningKey = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 135, 128));
            var freshKey = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 136, 128));
            session.View.Read(burningKey, out CellSnapshot burned);
            session.View.Read(freshKey, out CellSnapshot fresh);
            Assert.That(burned.MaterialId, Is.EqualTo(fresh.MaterialId));
            Assert.That(burned.FuelTicksRemaining, Is.LessThan(fresh.FuelTicksRemaining));
            Assert.That((Color32)grid.GetPixel(135, 128), Is.Not.EqualTo((Color32)grid.GetPixel(136, 128)));
            session.View.Materials.TryGet(burned.MaterialId, out MaterialRuntimeEntry wood);
            CellSnapshot extinguished = WetContactPolicy.Extinguish(burned, wood.Parameters.SpreadIntervalTicks);
            Assert.That(extinguished.IsBurning, Is.False);
            Assert.That(PixelPreviewTextures.MaterialPixel(extinguished, wood), Is.EqualTo((Color32)grid.GetPixel(135, 128)));
        }

        [Test]
        public void GridRetainsIndependentOriginalTexelsAcrossChunkBoundaryAndKeepsEmptyPixelsTransparent()
        {
            using var session = new ModulePreviewSession(BaselineSources.Read(), 0, 1);
            using var textures = new PixelPreviewTextures();
            textures.Refresh(session.View);
            Assert.That(textures.Layers.Count, Is.EqualTo(1));
            Texture2D grid = textures.Layers[0].Texture;
            Assert.That(grid.width, Is.EqualTo(256));
            Assert.That(grid.height, Is.EqualTo(256));
            Assert.That(grid.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(grid.mipmapCount, Is.EqualTo(1));
            Assert.That(grid.GetPixel(0, 0).a, Is.Zero);
            session.View.Materials.TryGet(102, out MaterialRuntimeEntry concrete);
            Assert.That((Color32)grid.GetPixel(127, 48), Is.EqualTo((Color32)concrete.Color));
            Assert.That((Color32)grid.GetPixel(128, 48), Is.EqualTo((Color32)concrete.Color));
            foreach (CellKey key in session.View.OccupiedCells)
            {
                session.View.Read(key, out CellSnapshot state);
                session.View.Materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material);
                Assert.That((Color32)grid.GetPixel(key.Position.X, key.Position.Y), Is.EqualTo((Color32)material.Color));
            }
            textures.Refresh(session.View);
            Assert.That(textures.Layers[0].Texture, Is.SameAs(grid), "同版本重绘必须复用纹理。");
        }

        [Test]
        public void SinglePixelHoleClearsItsTexelAndPreservesNeighborsWhileReusingTexture()
        {
            using var session = new ModulePreviewSession(BaselineSources.Read(), 2, 1);
            using var textures = new PixelPreviewTextures();
            textures.Refresh(session.View);
            Texture2D grid = textures.Layers[0].Texture;
            Vector2Int pixel = session.AnchorCell + new Vector2Int(2, 2);
            Color32 neighbor = grid.GetPixel(pixel.x + 1, pixel.y);
            Assert.That(session.Edit(new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, pixel.x, pixel.y)), false), Is.True);
            textures.Refresh(session.View);
            Assert.That(textures.Layers[0].Texture, Is.SameAs(grid));
            Assert.That(grid.GetPixel(pixel.x, pixel.y).a, Is.Zero);
            Assert.That((Color32)grid.GetPixel(pixel.x + 1, pixel.y), Is.EqualTo(neighbor));
        }

        [Test]
        public void ExtractedBodiesHaveOneOriginalTexelPerOccupiedLocalStateAndRetiredTexturesAreReleased()
        {
            using var session = new ModulePreviewSession(BaselineSources.Read(), 2, 1);
            using var textures = new PixelPreviewTextures();
            Assert.That(session.RemoveRegion(session.ActionCell, 8, 8), Is.True);
            textures.Refresh(session.View);
            Assert.That(textures.Layers.Count, Is.EqualTo(5));
            int colored = 0;
            foreach (PixelPreviewTextures.Layer layer in textures.Layers)
                foreach (Color32 pixel in layer.Texture.GetPixels32())
                    if (pixel.a != 0) colored++;
            Assert.That(colored, Is.EqualTo(session.View.OccupiedCells.Length));
            PixelPreviewTextures.Layer body = textures.Layers[1];
            Texture2D retired = body.Texture;
            int beforeHole = session.View.OccupiedCells.Length;
            var hole = new CellKey(1, new CellPositionKey(OwnerKind.Body, body.BodyId, body.Min.x + 2, body.Min.y + 2));
            Assert.That(session.Edit(hole, false), Is.True, session.Error);
            textures.Refresh(session.View);
            Assert.That(session.View.OccupiedCells.Length, Is.EqualTo(beforeHole - 1));
            Assert.That(textures.Layers[1].Texture, Is.SameAs(retired), "体内单像素孔洞不应重建未变尺寸的纹理。");
            Assert.That(retired.GetPixel(2, 2).a, Is.Zero, "体局部孔洞保持一个透明原始纹素。");
            Assert.That(retired.GetPixel(3, 2).a, Is.GreaterThan(0));
            Vector2Int min = Vector2Int.RoundToInt(body.Origin);
            Assert.That(session.RemoveRegion(min, body.Texture.width, body.Texture.height), Is.True);
            textures.Refresh(session.View);
            Assert.That(retired == null, Is.True, "撤销体应释放旧局部纹理。");
        }

        [Test]
        public void RepeatedPreviewLifetimesReleaseAllTexturesAndRejectUseAfterDispose()
        {
            for (int i = 0; i < 20; i++)
            {
                using var session = new ModulePreviewSession(BaselineSources.Read(), 2, (ulong)i + 1);
                var textures = new PixelPreviewTextures();
                textures.Refresh(session.View);
                Texture2D grid = textures.Layers[0].Texture;
                textures.Dispose();
                textures.Dispose();
                Assert.That(textures.Layers.Count, Is.Zero);
                Assert.That(grid == null, Is.True);
                Assert.Throws<ObjectDisposedException>(() => textures.Refresh(session.View));
            }
        }
    }
}
