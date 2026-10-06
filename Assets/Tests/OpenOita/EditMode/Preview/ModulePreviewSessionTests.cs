using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Preview;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Preview
{
    public sealed class ModulePreviewSessionTests
    {
        private const int AreaScale = 64;

        [TestCase(0, 76)]
        [TestCase(1, 44)]
        [TestCase(2, 25)]
        public void PresetsScaleBothAxesAndKeepEveryCellInsideDefaultWorld(int scenario, int originalCells)
        {
            using var preview = new ModulePreviewSession(BaselineSources.Read(), scenario, 1);
            Assert.That(preview.View.Config.Width, Is.EqualTo(256));
            Assert.That(preview.View.Config.Height, Is.EqualTo(256));
            Assert.That(preview.View.OccupiedCells.Length, Is.EqualTo(originalCells * AreaScale));
            Assert.That(ModulePreviewSession.LayoutCell(0, 0), Is.EqualTo(new Vector2Int(0, 32)));
            foreach (CellKey key in preview.View.OccupiedCells)
            {
                Vector2 position = ModulePreviewSession.CellOrigin(key, preview.View);
                Assert.That(position.x, Is.InRange(0, 255));
                Assert.That(position.y, Is.InRange(32, 223));
            }
        }

        [Test]
        public void FlowMovesConservesWaterExtinguishesWoodAndExpiresSteam()
        {
            using var preview = new ModulePreviewSession(BaselineSources.Read(), 0, 1);
            Assert.That(Count(preview, 101), Is.EqualTo(16 * AreaScale));
            Assert.That(Count(preview, 103), Is.EqualTo(8 * AreaScale));
            Assert.That(preview.Step(), Is.True, preview.Error);
            Vector2Int top = ModulePreviewSession.LayoutCell(7, 20) + new Vector2Int(0, 7);
            var oldPosition = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, top.x, top.y));
            preview.View.Read(oldPosition, out CellSnapshot empty);
            Assert.That(empty.MaterialId, Is.Zero, "第一步应搬走左上角水格。");
            for (int i = 1; i < 200; i++) Assert.That(preview.Step(), Is.True, preview.Error);
            Assert.That(Count(preview, 101), Is.EqualTo(16 * AreaScale), "水必须守恒。");
            Assert.That(Count(preview, 103), Is.Zero, "蒸汽200步消散。");
            Assert.That(Count(preview, 104), Is.EqualTo(6 * AreaScale), "200步时木头尚未燃尽。");
            // 等比例放大了落差，水仍按原规则每两步最多移动一格。
            for (int i = 200; i < 240; i++) Assert.That(preview.Step(), Is.True, preview.Error);
            Assert.That(Count(preview, 101), Is.EqualTo(16 * AreaScale));
            Vector2Int surface = ModulePreviewSession.LayoutCell(8, 3) + new Vector2Int(4, 7);
            preview.View.Read(new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, surface.x, surface.y)), out CellSnapshot wetWood);
            Assert.That(wetWood.IsBurning, Is.False, "与水接触的木头表面应熄灭。");
            Assert.That(wetWood.FuelTicksRemaining, Is.InRange(1u, 249u), "灭火保留已消耗后的燃料。");
        }

        [Test]
        public void BurningBridgeAutomaticallyExtractsBodiesAndKeepsCommittedState()
        {
            using var preview = new ModulePreviewSession(BaselineSources.Read(), 1, 7);
            int before = preview.View.OccupiedCells.Length;
            Assert.That(preview.View.Bodies.Length, Is.Zero);
            for (int i = 0; i < 250; i++) Assert.That(preview.Step(), Is.True, preview.Error);
            Assert.That(preview.View.Bodies.Length, Is.GreaterThan(0));
            Assert.That(preview.View.OccupiedCells.Length, Is.EqualTo(before - AreaScale));
            Assert.That(preview.View.Version.CommittedTick, Is.EqualTo(250));
            Assert.That(preview.Geometry.Any(g => g.Owner.OwnerKind == OwnerKind.Body && g.Mass > 0 && g.Rectangles.Count > 0), Is.True);
            for (int i = 0; i < 20; i++) Assert.That(preview.Step(), Is.True, preview.Error);
        }

        [Test]
        public void CrossCanBeSplitAgainAndOldGenerationCannotEditNewPreview()
        {
            using var first = new ModulePreviewSession(BaselineSources.Read(), 2, 1);
            Assert.That(first.RemoveRegion(first.ActionCell, 8, 8), Is.True, first.Error);
            Assert.That(first.View.OccupiedCells.Length, Is.EqualTo(24 * AreaScale));
            Assert.That(first.View.Bodies.Length, Is.EqualTo(4));
            CellKey oldKey = first.View.OccupiedCells[0];
            Assert.That(first.RemoveRegion(ModulePreviewSession.LayoutCell(16, 15), 8, 8), Is.True, first.Error);
            Assert.That(first.View.Bodies.Length, Is.EqualTo(5));
            Assert.That(first.View.OccupiedCells.Length, Is.EqualTo(23 * AreaScale));
            Assert.That(first.Step(), Is.True, first.Error);
            using var reset = new ModulePreviewSession(BaselineSources.Read(), 2, 2);
            Assert.That(reset.Edit(oldKey, false), Is.False);
            Assert.That(reset.View.OccupiedCells.Length, Is.EqualTo(25 * AreaScale));
            Assert.That(reset.View.Bodies.Length, Is.Zero);
            Assert.That(reset.View.Version.CommittedTick, Is.Zero);
        }

        [Test]
        public void EveryPresetCanBeRecreatedAndDisposedWithPublishedViewsInvalidated()
        {
            for (int i = 0; i < 9; i++)
            {
                var preview = new ModulePreviewSession(BaselineSources.Read(), i % 3, (ulong)i + 1);
                Assert.That(preview.Step(), Is.True, preview.Error);
                ICommittedRenderView view = preview.View;
                CellKey key = view.OccupiedCells[0];
                preview.Dispose();
                preview.Dispose();
                Assert.That(preview.Step(), Is.False);
                Assert.That(view.Read(key, out _).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
            }
        }

        private static int Count(ModulePreviewSession preview, ushort material)
        {
            int count = 0;
            foreach (CellKey key in preview.View.OccupiedCells)
            {
                preview.View.Read(key, out CellSnapshot state);
                if (state.MaterialId == material) count++;
            }
            return count;
        }
    }
}
