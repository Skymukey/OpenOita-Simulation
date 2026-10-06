using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Spatial;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Spatial
{
    public sealed class ExactCellGeometryTests
    {
        private static CellGeometry Square(OwnerKind owner, ulong id, Vector2 position, float angle = 0, int x = 0, int y = 0) =>
            new CellGeometry(new CellKey(1, new CellPositionKey(owner, id, x, y)), new BodyPose(position, angle), new Vector2Int(x, y), 0.1f);

        [TestCase(12, 1)] [TestCase(28, 8)] [TestCase(127, 0)] [TestCase(128, 64)]
        public void M07B_06_StaticSharedEdgesNeverHavePositiveArea(int x, int y)
        {
            var a = Square(OwnerKind.Grid, 0, Vector2.zero, 0, x, y);
            Assert.That(ExactCellGeometry.Penetration(a, Square(OwnerKind.Grid, 0, Vector2.zero, 0, x + 1, y)), Is.Zero);
            Assert.That(ExactCellGeometry.Penetration(a, Square(OwnerKind.Grid, 0, Vector2.zero, 0, x, y + 1)), Is.Zero);
        }

        [TestCase(0.5f, true)] [TestCase(1f, true)] [TestCase(1.5f, false)]
        public void M06_T9_01_EdgeContactUsesExactEpsilon(float multiple, bool expected)
        {
            CellGeometry a = Square(OwnerKind.Grid, 0, Vector2.zero);
            CellGeometry b = Square(OwnerKind.Body, 1, new Vector2(0.1f + 1e-5f * multiple, 0));
            CellContact contact = ExactCellGeometry.Contact(a, b, 1e-5f);
            Assert.That(contact.Feature == ContactFeature.EdgeEdge, Is.EqualTo(expected));
        }
        [Test]
        public void M06_T9_01_VertexVertexExcludedAndRotatedVertexEdgeIncluded()
        {
            CellGeometry a = Square(OwnerKind.Grid, 0, Vector2.zero);
            Assert.That(ExactCellGeometry.Contact(a, Square(OwnerKind.Body, 1, new Vector2(0.1f, 0.1f)), 1e-5f).Feature, Is.EqualTo(ContactFeature.VertexVertex));
            float d = 0.1f / Mathf.Sqrt(2);
            var rotated = Square(OwnerKind.Body, 1, new Vector2(0.1f + d, 0.05f - d), Mathf.PI / 4);
            Assert.That(ExactCellGeometry.Contact(a, rotated, 1e-5f).Feature, Is.EqualTo(ContactFeature.VertexEdge));
            Assert.That(ExactCellGeometry.PositiveOverlap(a, rotated), Is.False);
        }
        [Test]
        public void M06_T9_01_SameOwnerUsesFourNeighboursOnly()
        {
            var a = Square(OwnerKind.Body, 1, Vector2.zero);
            Assert.That(ExactCellGeometry.Contact(a, Square(OwnerKind.Body, 1, Vector2.zero, 0, 1, 1), 0.01f).Feature, Is.EqualTo(ContactFeature.Separated));
            Assert.That(ExactCellGeometry.Contact(a, Square(OwnerKind.Body, 1, Vector2.zero, 0, 1, 0), 0).Feature, Is.EqualTo(ContactFeature.EdgeEdge));
        }
        [Test]
        public void M06_T7_03_AnyPositiveOverlapIsStricterThanPhysicsTolerance()
        {
            var a = Square(OwnerKind.Grid, 0, Vector2.zero);
            var b = Square(OwnerKind.Body, 1, new Vector2(0.0999f, 0));
            Assert.That(ExactCellGeometry.PositiveOverlap(a, b), Is.True);
            Assert.That(ExactCellGeometry.Penetration(a, b), Is.LessThan(0.01));
            Assert.That(ExactCellGeometry.PositiveOverlap(a, Square(OwnerKind.Body, 1, new Vector2(0.1f, 0))), Is.False);
        }
        [Test]
        public void M06_T5_02_RotatedSquareRejectsAabbCornerAndReturnsFirstSegmentIntersection()
        {
            var geometry = new ExactCellGeometry();
            var square = Square(OwnerKind.Body, 1, new Vector2(1, 1), Mathf.PI / 4);
            Assert.That(geometry.ContainsPoint(square, new Vector2(0.932f, 1.001f)), Is.False);
            Assert.That(geometry.ContainsPoint(square, geometry.Center(square)), Is.True);
            Assert.That(geometry.IntersectSegment(square, new Vector2(0.8f, 1.07f), new Vector2(1.2f, 1.07f), out double t), Is.True);
            Assert.That(t, Is.InRange(0.32, 0.34));
        }
    }
}
