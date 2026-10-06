using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Preview;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Preview
{
    public sealed class PixelPreviewViewportTests
    {
        [TestCase(1, 8)]
        [TestCase(2, 16)]
        [TestCase(3, 24)]
        public void EightByEightStatesCoverExactlyTheSelectedNumberOfOutputPixels(int scale, int outputSize)
        {
            var viewport = new PixelPreviewViewport(new Rect(16, 16, 768, 768), 256, 256, scale, Vector2Int.zero);
            Rect block = viewport.CellRect(0, 248, 8, 8);
            Assert.That(block, Is.EqualTo(new Rect(16, 16, outputSize, outputSize)));
            var counts = new int[8, 8];
            for (int y = 16; y < 16 + outputSize; y++)
                for (int x = 16; x < 16 + outputSize; x++)
                {
                    Assert.That(viewport.TryCell(new Vector2(x + 0.5f, y + 0.5f), out Vector2Int cell), Is.True);
                    counts[cell.x, cell.y - 248]++;
                }
            foreach (int count in counts) Assert.That(count, Is.EqualTo(scale * scale));
            Assert.That(viewport.TryCell(new Vector2(16, 16), out Vector2Int topLeft), Is.True);
            Assert.That(topLeft, Is.EqualTo(new Vector2Int(0, 255)), "上边界属于显示的第一行。");
            Assert.That(viewport.TryCell(new Vector2(16, 16 + scale), out Vector2Int nextRow), Is.True);
            Assert.That(nextRow, Is.EqualTo(new Vector2Int(0, 254)), "相邻屏幕行的上边界不可重复命中上一行。");
        }

        [Test]
        public void SmallViewportClipsAndPansWithoutChangingResolutionOrSilentlyScaling()
        {
            var viewport = new PixelPreviewViewport(new Rect(16, 16, 100, 60), 129, 65, 3, new Vector2Int(10000, 10000));
            Assert.That(viewport.Pan, Is.EqualTo(new Vector2Int(287, 135)));
            Assert.That(viewport.CellRect(0, 0, 129, 65).size, Is.EqualTo(new Vector2(387, 195)));
            Assert.That(viewport.TryCell(new Vector2(115.5f, 75.5f), out Vector2Int last), Is.True);
            Assert.That(last, Is.EqualTo(new Vector2Int(128, 0)));
            Assert.That(viewport.TryCell(new Vector2(116, 75), out _), Is.False, "视口右边界不接受裁剪区域点击。");
            Assert.That(viewport.TryCell(new Vector2(115, 76), out _), Is.False);
            Assert.That(viewport.TryCell(new Vector2(float.NaN, 16), out _), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PixelPreviewViewport(new Rect(0, 0, 10, 10), 8, 8, 0, Vector2Int.zero));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryCoveredOutputPixelSelectsTheSameSingleStateAndEditRemovesOnlyOne(int scale)
        {
            using var session = new ModulePreviewSession(BaselineSources.Read(), 2, (ulong)scale);
            var viewport = new PixelPreviewViewport(new Rect(16, 16, 768, 768), 256, 256, scale, Vector2Int.zero);
            Vector2Int anchor = session.AnchorCell + new Vector2Int(2, 2);
            Rect target = viewport.CellRect(anchor.x, anchor.y);
            CellKey expected = new CellKey((ulong)scale, new CellPositionKey(OwnerKind.Grid, 0, anchor.x, anchor.y));
            for (int y = 0; y < scale; y++)
                for (int x = 0; x < scale; x++)
                {
                    Assert.That(viewport.TryHit(session.View, target.position + new Vector2(x + 0.5f, y + 0.5f), out CellKey selected), Is.True);
                    Assert.That(selected, Is.EqualTo(expected));
                }
            int before = session.View.OccupiedCells.Length;
            Assert.That(session.Edit(expected, false), Is.True, session.Error);
            Assert.That(session.View.OccupiedCells.Length, Is.EqualTo(before - 1));
            Assert.That(viewport.TryHit(session.View, target.center, out _), Is.False, "删除单像素后不能命中相邻像素。");
        }

        [TestCase(1)]
        [TestCase(3)]
        public void SplitBodiesKeepTheirLocalPixelIdentityUnderPanning(int scale)
        {
            using var session = new ModulePreviewSession(BaselineSources.Read(), 2, 9);
            Assert.That(session.RemoveRegion(session.ActionCell, 8, 8), Is.True, session.Error);
            var viewport = new PixelPreviewViewport(new Rect(16, 16, 600, 600), 256, 256, scale, new Vector2Int(21, 27));
            CellKey? expected = null;
            foreach (CellKey key in session.View.OccupiedCells)
                if (key.Position.OwnerKind == OwnerKind.Body)
                {
                    Vector2 origin = ModulePreviewSession.CellOrigin(key, session.View);
                    if (viewport.Bounds.Contains(viewport.CellRect(origin.x, origin.y).center)) { expected = key; break; }
                }
            Assert.That(expected.HasValue, Is.True);
            Vector2 point = ModulePreviewSession.CellOrigin(expected.Value, session.View);
            Assert.That(viewport.TryHit(session.View, viewport.CellRect(point.x, point.y).center, out CellKey hit), Is.True);
            Assert.That(hit, Is.EqualTo(expected.Value));
        }
    }
}
