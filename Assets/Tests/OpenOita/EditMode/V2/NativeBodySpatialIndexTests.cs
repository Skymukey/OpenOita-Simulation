using System;
using NUnit.Framework;
using OpenOita.V2;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class NativeBodySpatialIndexTests
    {
        [Test]
        public void QueryRect_DeduplicatesAcrossBuckets_AndSortsBodyIds()
        {
            using (var index = NewIndex())
            using (var candidates = new NativeList<ulong>(8, Allocator.Temp))
            {
                index.UpdateAabb(20, Bounds(0, 0, 64, 64));
                index.UpdateAabb(3, Bounds(31, 31, 33, 33));
                index.UpdateAabb(7, Bounds(16, 16, 48, 48));

                Assert.That(index.QueryRect(Bounds(0, 0, 64, 64), candidates, out int required), Is.True);
                Assert.That(required, Is.EqualTo(3));
                CollectionAssert.AreEqual(new ulong[] { 3, 7, 20 }, Values(candidates));
            }
        }

        [Test]
        public void QueryRect_UsesClosedOpenThirtyTwoCellBuckets_ForNegativeAndBoundaryCoordinates()
        {
            using (var index = NewIndex())
            using (var candidates = new NativeList<ulong>(8, Allocator.Temp))
            {
                index.UpdateAabb(5, Bounds(-32, -32, 0, 0));
                index.UpdateAabb(9, Bounds(0, 0, 32, 32));

                Assert.That(index.QueryRect(Bounds(-32, -32, 0, 0), candidates, out _), Is.True);
                CollectionAssert.AreEqual(new ulong[] { 5 }, Values(candidates));
                Assert.That(index.QueryRect(Bounds(0, 0, 32, 32), candidates, out _), Is.True);
                CollectionAssert.AreEqual(new ulong[] { 9 }, Values(candidates));
                Assert.That(index.QueryRect(Bounds(-31, -31, 1, 1), candidates, out _), Is.True);
                CollectionAssert.AreEqual(new ulong[] { 5, 9 }, Values(candidates));
            }
        }

        [Test]
        public void UpdateAabb_RemovesOldBuckets_WhenCallerMovesOrRotatesBody()
        {
            using (var index = NewIndex())
            using (var candidates = new NativeList<ulong>(8, Allocator.Temp))
            {
                index.UpdateAabb(12, Bounds(-64, -32, -32, 0));
                Assert.That(index.QueryRect(Bounds(-64, -32, -32, 0), candidates, out _), Is.True);
                CollectionAssert.AreEqual(new ulong[] { 12 }, Values(candidates));

                // The caller has already converted a rotated body to this conservative AABB.
                index.UpdateAabb(12, Bounds(63, 31, 97, 66));
                Assert.That(index.QueryRect(Bounds(-64, -32, -32, 0), candidates, out _), Is.True);
                Assert.That(candidates.Length, Is.Zero);
                Assert.That(index.QueryRect(Bounds(64, 32, 96, 64), candidates, out _), Is.True);
                CollectionAssert.AreEqual(new ulong[] { 12 }, Values(candidates));
                Assert.That(index.BucketEntryCount, Is.EqualTo(index.BodyBucketEntryCount));
            }
        }

        [Test]
        public void QueryDestinationTooSmall_ReturnsRequiredCountWithoutPartialResults()
        {
            using (var index = new NativeBodySpatialIndex(64, 128, 8))
            using (var small = new NativeList<ulong>(1, Allocator.Temp))
            {
                int expected = small.Capacity + 1;
                index.Reserve(expected, 128, expected);
                for (int i = 0; i < expected; i++)
                    index.UpdateAabb((ulong)(i + 2), Bounds(i, 0, i + 1, 1));
                Assert.That(index.QueryRect(Bounds(0, 0, expected, 1), small, out int required), Is.False);
                Assert.That(required, Is.EqualTo(expected));
                Assert.That(small.Length, Is.Zero);
            }
        }

        [Test]
        public void CapacityMustBeReservedBeforeSteadyState_AndDoesNotGrowDuringQueries()
        {
            using (var index = new NativeBodySpatialIndex(2, 4, 2))
            using (var candidates = new NativeList<ulong>(2, Allocator.Temp))
            {
                Assert.Throws<InvalidOperationException>(() => index.UpdateAabb(1, Bounds(0, 0, 129, 1)));
                index.Reserve(2, 8, 2);
                index.UpdateAabb(1, Bounds(0, 0, 65, 1));
                index.UpdateAabb(2, Bounds(-32, -32, 0, 0));
                int bodyCapacity = index.BodyCapacity;
                int bucketCapacity = index.BucketCapacity;
                int queryCapacity = index.QueryCapacity;

                Assert.That(index.QueryRect(Bounds(-32, -32, 65, 1), candidates, out _), Is.True);
                index.UpdateAabb(1, Bounds(32, 0, 97, 1));
                Assert.That(index.BodyCapacity, Is.EqualTo(bodyCapacity));
                Assert.That(index.BucketCapacity, Is.EqualTo(bucketCapacity));
                Assert.That(index.QueryCapacity, Is.EqualTo(queryCapacity));
            }
        }

        [Test]
        public void RemoveAndDispose_ClearOwnershipAndRejectFurtherAccess()
        {
            var index = NewIndex();
            try
            {
                index.UpdateAabb(17, Bounds(0, 0, 1, 1));
                Assert.That(index.Remove(17), Is.True);
                Assert.That(index.Remove(17), Is.False);
                Assert.That(index.BodyCount, Is.Zero);
            }
            finally
            {
                index.Dispose();
            }

            Assert.Throws<ObjectDisposedException>(() => index.Remove(17));
            index.Dispose();
        }

        private static NativeBodySpatialIndex NewIndex()
        {
            var index = new NativeBodySpatialIndex(8, 64, 8);
            index.Reserve(8, 64, 8);
            return index;
        }

        private static RectInt Bounds(int minX, int minY, int maxX, int maxY)
        {
            return new RectInt(minX, minY, maxX - minX, maxY - minY);
        }

        private static ulong[] Values(NativeList<ulong> values)
        {
            var result = new ulong[values.Length];
            for (int i = 0; i < result.Length; i++) result[i] = values[i];
            return result;
        }
    }
}
