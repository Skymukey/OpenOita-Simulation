using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Structure;
using OpenOita.Tests.EditMode.Structure.Geometry;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Bodies
{
    public sealed class MassPropertiesCalculatorTests
    {
        [Test]
        public void SingleCellAndMixedMassesMatchIndependentFormula()
        {
            var world = new BodyFixture();
            var single = new[] { RectangleGeometryBuilderTests.Cell(0, 0) };
            Assert.That(MassPropertiesCalculator.Calculate(single, world.Materials, 0.1f,
                out float mass, out Vector2 center, out float inertia, out float radius).IsSuccess, Is.True);
            Assert.That(mass, Is.EqualTo(0.6).Within(1e-6));
            Assert.That(center.x, Is.EqualTo(0.05).Within(1e-6));
            Assert.That(inertia, Is.EqualTo(0.6 * 0.01 / 6).Within(1e-8));
            Assert.That(radius, Is.EqualTo(Math.Sqrt(0.005)).Within(1e-6));
            var mixed = new[] { RectangleGeometryBuilderTests.Cell(0, 0), RectangleGeometryBuilderTests.Cell(1, 0, 102) };
            Assert.That(MassPropertiesCalculator.Calculate(mixed, world.Materials, 0.1f,
                out mass, out center, out inertia, out radius).IsSuccess, Is.True);
            Assert.That(mass, Is.EqualTo(3).Within(1e-6));
            Assert.That(center.x, Is.EqualTo(0.13).Within(1e-6));
            Assert.That(center.y, Is.EqualTo(0.05).Within(1e-6));
            Assert.That(inertia, Is.EqualTo(3 * 0.01 / 6 + 0.6 * 0.08 * 0.08 + 2.4 * 0.02 * 0.02).Within(1e-8));
            Assert.That(radius, Is.EqualTo(Math.Sqrt(0.13 * 0.13 + 0.05 * 0.05)).Within(1e-6));
        }

        [Test]
        public void M05_T9_01_PartialFuelDoesNotReduceMassOrOccupiedShape()
        {
            var world = new BodyFixture();
            var key = BodyFixture.Body(7, 0, 0);
            var cells = new[] { new PlannedCell(key, key, default, new CellSnapshot(104, 1, 1, 2, 3, 4, 5)) };
            Assert.That(MassPropertiesCalculator.Calculate(cells, world.Materials, 0.1f,
                out float mass, out _, out float inertia, out _).IsSuccess, Is.True);
            Assert.That(mass, Is.EqualTo(0.6).Within(1e-6));
            Assert.That(inertia, Is.EqualTo(0.001).Within(1e-8));
        }

        [TestCase(101)] [TestCase(103)] [TestCase(999)]
        public void UnsupportedOrUnknownMaterialReturnsZeroOutputs(int id)
        {
            var world = new BodyFixture();
            var result = MassPropertiesCalculator.Calculate(new[] { RectangleGeometryBuilderTests.Cell(0, 0, (ushort)id) },
                world.Materials, 0.1f, out float mass, out Vector2 center, out float inertia, out float radius);
            Assert.That(result.ErrorCode, Is.EqualTo(id == 999 ? WorldErrorCode.UnknownMaterial : WorldErrorCode.UnsupportedOperation));
            Assert.That(mass, Is.Zero); Assert.That(center, Is.EqualTo(Vector2.zero));
            Assert.That(inertia, Is.Zero); Assert.That(radius, Is.Zero);
        }

        [TestCase(0f)] [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidCellSizeIsRejected(float size)
        {
            Assert.That(MassPropertiesCalculator.Calculate(new[] { RectangleGeometryBuilderTests.Cell(0, 0) }, new BodyFixture().Materials,
                size, out _, out _, out _, out _).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
        }

        [Test]
        public void NumericOverflowIsRejectedWithoutNonFiniteOutputs()
        {
            Assert.That(MassPropertiesCalculator.Calculate(new[] { RectangleGeometryBuilderTests.Cell(0, 0) }, new BodyFixture().Materials,
                float.MaxValue, out float mass, out Vector2 center, out float inertia, out float radius).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(mass, Is.Zero); Assert.That(center, Is.EqualTo(Vector2.zero));
            Assert.That(inertia, Is.Zero); Assert.That(radius, Is.Zero);
        }
    }
}
