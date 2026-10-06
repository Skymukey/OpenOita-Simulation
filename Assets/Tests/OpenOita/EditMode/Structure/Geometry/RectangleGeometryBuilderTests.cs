using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Structure;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Structure.Geometry
{
    public sealed class RectangleGeometryBuilderTests
    {
        internal static PlannedCell Cell(int x, int y, ushort material = 104, ulong id = 7)
        {
            var key = new CellKey(1, new CellPositionKey(OwnerKind.Body, id, x, y));
            return new PlannedCell(key, key, default, new CellSnapshot(material));
        }
        internal static void ExactCoverage(PlannedCell[] cells, CellRectangle[] rectangles)
        {
            var covered = new HashSet<Vector2Int>();
            foreach (CellRectangle rectangle in rectangles)
            {
                Assert.That(rectangle.MaxExclusive.x, Is.GreaterThan(rectangle.Min.x));
                Assert.That(rectangle.MaxExclusive.y, Is.GreaterThan(rectangle.Min.y));
                for (int y = rectangle.Min.y; y < rectangle.MaxExclusive.y; y++)
                    for (int x = rectangle.Min.x; x < rectangle.MaxExclusive.x; x++)
                        Assert.That(covered.Add(new Vector2Int(x, y)), Is.True, "矩形不得重叠");
            }
            CollectionAssert.AreEquivalent(cells.Select(c => new Vector2Int(c.Target.Position.X, c.Target.Position.Y)), covered);
        }

        [Test]
        public void M05_T5_03_RingHasEightCoveredCellsAndFourRectanglesWithoutFillingHole()
        {
            var cells = new List<PlannedCell>();
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++) if (x != 1 || y != 1) cells.Add(Cell(x, y));
            Assert.That(RectangleGeometryBuilder.Build(cells.ToArray(), out CellRectangle[] rectangles).IsSuccess, Is.True);
            ExactCoverage(cells.ToArray(), rectangles);
            Assert.That(rectangles.Length, Is.EqualTo(4));
            Assert.That(rectangles[0].Min, Is.EqualTo(new Vector2Int(0, 0)));
            Assert.That(rectangles[0].MaxExclusive, Is.EqualTo(new Vector2Int(3, 1)));
        }

        [Test]
        public void MixedMaterialsMergeAndGreedyOrderIsXThenY()
        {
            var cells = new[] { Cell(1, 1), Cell(1, 0, 102), Cell(0, 1), Cell(0, 0) };
            Assert.That(RectangleGeometryBuilder.Build(cells, out CellRectangle[] rectangles).IsSuccess, Is.True);
            Assert.That(rectangles.Length, Is.EqualTo(1));
            Assert.That(rectangles[0].Min, Is.EqualTo(Vector2Int.zero));
            Assert.That(rectangles[0].MaxExclusive, Is.EqualTo(new Vector2Int(2, 2)));
            ExactCoverage(cells, rectangles);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(7)] [TestCase(19)] [TestCase(41)]
        public void SparseRandomOccupancyMatchesIndependentExactSet(int seed)
        {
            var random = new System.Random(seed);
            var cells = new List<PlannedCell>();
            for (int y = -10; y < 10; y++)
                for (int x = -10; x < 10; x++) if (random.Next(3) != 0) cells.Add(Cell(x, y));
            var shuffled = cells.OrderBy(_ => random.Next()).ToArray();
            Assert.That(RectangleGeometryBuilder.Build(shuffled, out CellRectangle[] actual).IsSuccess, Is.True);
            ExactCoverage(cells.ToArray(), actual);
            Assert.That(RectangleGeometryBuilder.Build(cells.ToArray(), out CellRectangle[] ordered).IsSuccess, Is.True);
            Assert.That(actual.Select(r => r.Min.ToString() + r.MaxExclusive), Is.EqualTo(ordered.Select(r => r.Min.ToString() + r.MaxExclusive)));
        }

        [Test]
        public void HugeSparseLocalRangeDoesNotAllocateBoundingArea()
        {
            var cells = new[] { Cell(int.MinValue, int.MinValue), Cell(int.MaxValue - 1, int.MaxValue - 1) };
            Assert.That(RectangleGeometryBuilder.Build(cells, out CellRectangle[] rectangles).IsSuccess, Is.True);
            ExactCoverage(cells, rectangles);
            Assert.That(rectangles.Length, Is.EqualTo(2));
        }

        [Test]
        public void InvalidOrMixedOwnersReturnNoPartialRectangles()
        {
            foreach (PlannedCell[] cells in new[] { new[] { Cell(0, 0), Cell(0, 0) },
                new[] { Cell(0, 0), Cell(1, 0, 104, 8) }, new[] { Cell(0, 0, 0) } })
            {
                Assert.That(RectangleGeometryBuilder.Build(cells, out CellRectangle[] rectangles).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                Assert.That(rectangles, Is.Null);
            }
        }

        [TestCase(int.MaxValue, 0)] [TestCase(0, int.MaxValue)]
        public void ExclusiveBoundaryOverflowReturnsCapacityWithoutPartialGeometry(int x, int y)
        {
            Assert.That(RectangleGeometryBuilder.Build(new[] { Cell(0, 0), Cell(x, y) }, out CellRectangle[] rectangles).ErrorCode,
                Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(rectangles, Is.Null);
        }

        [Test]
        public void EmptyGeometryIsEmpty()
        {
            Assert.That(RectangleGeometryBuilder.Build(ReadOnlySpan<PlannedCell>.Empty, out CellRectangle[] rectangles).IsSuccess, Is.True);
            Assert.That(rectangles, Is.Empty);
        }
    }
}
