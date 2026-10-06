using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Editor;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M08
{
    public sealed class ScenePainterTests
    {
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);

        [TestCase(false)] [TestCase(true)]
        public void SizeOneIsOneLogicalCell(bool round)
        {
            Assert.That(SceneBrushSelection.Stamp(new Vector2Int(7, 9), 1, round, 256, 256),
                Is.EqualTo(new[] { new Vector2Int(7, 9) }));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(8)] [TestCase(128)]
        public void SquareHasExactSizeAndStableOrder(int size)
        {
            var cells = SceneBrushSelection.Stamp(new Vector2Int(128, 128), size, false, 256, 256);
            Assert.That(cells.Length, Is.EqualTo(size * size));
            Assert.That(cells.Select(p => p.x).Distinct().Count(), Is.EqualTo(size));
            Assert.That(cells.Select(p => p.y).Distinct().Count(), Is.EqualTo(size));
            Assert.That(cells, Is.Ordered.By("y").Then.By("x"));
        }

        [Test] public void RoundBrushOmitsCornersAndMatchesErase()
        {
            var cells = SceneBrushSelection.Stamp(new Vector2Int(10, 10), 5, true, 256, 256);
            Assert.That(cells, Has.No.Member(new Vector2Int(8, 8)));
            Assert.That(cells, Has.Member(new Vector2Int(10, 8)));
            Success(SceneMaterialData.Load(M06Sources.Create(Array.Empty<InitialCell>()), out var data));
            Success(data.Apply(cells, SceneEditOperation.Paint, 104));
            Assert.That(data.Count, Is.EqualTo(cells.Length));
            Success(data.Apply(cells, SceneEditOperation.Erase));
            Assert.That(data.Count, Is.Zero);
        }

        [Test] public void BrushAtWorldEdgeClipsBeforeStrictModelApply()
        {
            var cells = SceneBrushSelection.Stamp(Vector2Int.zero, 5, false, 129, 65);
            Assert.That(cells.Length, Is.EqualTo(9));
            var corner = SceneBrushSelection.Stamp(new Vector2Int(128, 64), 5, false, 129, 65);
            Assert.That(corner.Length, Is.EqualTo(9));
            Assert.That(corner.All(p => p.x < 129 && p.y < 65), Is.True);
        }

        [TestCase(220, 10)] [TestCase(10, 220)] [TestCase(220, 220)] [TestCase(10, 10)]
        public void SparseMouseEventsProduceContinuousUniqueStroke(int x, int y)
        {
            var start = new Vector2Int(100, 100); var end = new Vector2Int(x, y);
            var cells = SceneBrushSelection.Stroke(start, end, 1, false, 256, 256);
            Assert.That(cells, Has.Member(start));
            Assert.That(cells, Has.Member(end));
            Assert.That(cells.Length, Is.EqualTo(Mathf.Max(Mathf.Abs(x - 100), Mathf.Abs(y - 100)) + 1));
            Assert.That(cells.Distinct().Count(), Is.EqualTo(cells.Length));
            foreach (var p in cells.Where(p => p != end))
                Assert.That(cells.Any(q => q != p && Mathf.Abs(q.x - p.x) <= 1 && Mathf.Abs(q.y - p.y) <= 1), Is.True);
        }

        [Test] public void ReverseRectangleIncludesBothEndsAcrossChunkBoundary()
        {
            var cells = SceneBrushSelection.Rectangle(new Vector2Int(128, 3), new Vector2Int(126, 1), 129, 65);
            Assert.That(cells.Length, Is.EqualTo(9));
            Assert.That(cells.First(), Is.EqualTo(new Vector2Int(126, 1)));
            Assert.That(cells.Last(), Is.EqualTo(new Vector2Int(128, 3)));
        }

        [Test] public void CreateEmptyKeepsTemplateRulesAndDoesNotRetainCellsOrMarkers()
        {
            var doc = new SceneEditingDocument(); var template = BaselineSources.Read();
            Success(doc.CreateEmpty(template, 129, 65, 0.2f));
            Assert.That(doc.Data.Count, Is.Zero);
            Assert.That(doc.Data.Config.Width, Is.EqualTo(129));
            Assert.That(doc.Data.Config.Height, Is.EqualTo(65));
            Assert.That(doc.Data.Config.CellSize, Is.EqualTo(0.2f));
            Assert.That(doc.Data.Snapshot().FixedCells, Is.Empty);
            Assert.That(doc.Data.Snapshot().InitialBurning, Is.Empty);
            Assert.That(doc.Data.Materials.TryGet(104, out _), Is.True);
            var before = doc.Data;
            Assert.That(doc.CreateEmpty(template, 0, 65, 0.2f).IsSuccess, Is.False);
            Assert.That(doc.Data, Is.SameAs(before));
        }

        [Test] public void NewSceneSaveReloadPreservesDrawingAndRefusesExistingDirectory()
        {
            var doc = new SceneEditingDocument();
            Success(doc.CreateEmpty(BaselineSources.Read(), 129, 65, 0.2f));
            var cells = SceneBrushSelection.Rectangle(new Vector2Int(125, 0), new Vector2Int(128, 2), 129, 65);
            Success(doc.Data.Apply(cells, SceneEditOperation.Paint, 104));
            Success(doc.Data.Apply(cells, SceneEditOperation.Fixed));
            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "M08Painter", Guid.NewGuid().ToString("N"));
            Success(doc.SaveNewDirectory(folder));
            string original = File.ReadAllText(Path.Combine(folder, "scene.json"));
            Success(doc.Data.Apply(cells, SceneEditOperation.Erase));
            Assert.That(doc.SaveNewDirectory(folder).IsSuccess, Is.False);
            Assert.That(File.ReadAllText(Path.Combine(folder, "scene.json")), Is.EqualTo(original));
            Success(doc.LoadDirectory(folder));
            Assert.That(doc.Data.Count, Is.EqualTo(12));
            Assert.That(doc.Data.Snapshot().FixedCells.Count, Is.EqualTo(12));
            Assert.That(doc.Data.Config.Width, Is.EqualTo(129));
            Success(doc.Save(folder));
        }
    }
}
