using System;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.V2
{
    /// <summary>
    /// Persistent coarse index for dynamic-body world AABBs. Coordinates are cell
    /// coordinates and rectangles use a closed-open maximum. The caller computes
    /// the conservative AABB (including any rotation); this class only bins it.
    ///
    /// Call Reserve at a simulation barrier before adding the bodies or bucket pairs
    /// needed by the next steady-state window. Updates and queries then use AddNoResize
    /// and fail loudly when the reserved capacity is insufficient.
    /// </summary>
    public sealed class NativeBodySpatialIndex : IDisposable
    {
        public const int BucketSize = 32;

        private NativeParallelHashMap<ulong, BodyBounds> _bounds;
        private NativeParallelMultiHashMap<long, ulong> _buckets;
        private NativeParallelMultiHashMap<ulong, long> _bodyBuckets;
        private NativeParallelHashSet<ulong> _querySeen;
        private NativeList<ulong> _queryCandidates;
        private NativeList<long> _oldBuckets;
        private NativeList<ulong> _bucketValues;
        private bool _disposed;

        public int BodyCount
        {
            get { EnsureUsable(); return _bounds.Count(); }
        }

        public int BucketEntryCount
        {
            get { EnsureUsable(); return _buckets.Count(); }
        }

        public int BodyBucketEntryCount
        {
            get { EnsureUsable(); return _bodyBuckets.Count(); }
        }

        public int BodyCapacity
        {
            get { EnsureUsable(); return _bounds.Capacity; }
        }

        public int BucketCapacity
        {
            get { EnsureUsable(); return _buckets.Capacity; }
        }

        public int QueryCapacity
        {
            get { EnsureUsable(); return _queryCandidates.Capacity; }
        }

        public NativeBodySpatialIndex(int bodyCapacity = 64, int bucketEntryCapacity = 256,
            int queryCapacity = 64)
        {
            ValidateCapacity(bodyCapacity, nameof(bodyCapacity));
            ValidateCapacity(bucketEntryCapacity, nameof(bucketEntryCapacity));
            ValidateCapacity(queryCapacity, nameof(queryCapacity));

            int safeBodyCapacity = Math.Max(1, bodyCapacity);
            int safeBucketCapacity = Math.Max(1, bucketEntryCapacity);
            int safeQueryCapacity = Math.Max(1, queryCapacity);
            _bounds = new NativeParallelHashMap<ulong, BodyBounds>(safeBodyCapacity, Allocator.Persistent);
            _buckets = new NativeParallelMultiHashMap<long, ulong>(safeBucketCapacity, Allocator.Persistent);
            _bodyBuckets = new NativeParallelMultiHashMap<ulong, long>(safeBucketCapacity, Allocator.Persistent);
            _querySeen = new NativeParallelHashSet<ulong>(safeQueryCapacity, Allocator.Persistent);
            _queryCandidates = new NativeList<ulong>(safeQueryCapacity, Allocator.Persistent);
            _oldBuckets = new NativeList<long>(safeBucketCapacity, Allocator.Persistent);
            _bucketValues = new NativeList<ulong>(safeBodyCapacity, Allocator.Persistent);
        }

        /// <summary>
        /// Grows all persistent containers before a simulation barrier. Existing data
        /// is preserved. bucketEntryCapacity counts body-to-bucket pairs, not unique
        /// buckets, because a large AABB can occupy multiple buckets.
        /// </summary>
        public void Reserve(int bodyCapacity, int bucketEntryCapacity, int queryCapacity)
        {
            EnsureUsable();
            ValidateCapacity(bodyCapacity, nameof(bodyCapacity));
            ValidateCapacity(bucketEntryCapacity, nameof(bucketEntryCapacity));
            ValidateCapacity(queryCapacity, nameof(queryCapacity));

            int requestedBodies = Math.Max(1, bodyCapacity);
            int requestedBuckets = Math.Max(1, bucketEntryCapacity);
            int requestedQueries = Math.Max(1, queryCapacity);
            if (_bounds.Capacity < requestedBodies) _bounds.Capacity = requestedBodies;
            if (_buckets.Capacity < requestedBuckets) _buckets.Capacity = requestedBuckets;
            if (_bodyBuckets.Capacity < requestedBuckets) _bodyBuckets.Capacity = requestedBuckets;
            if (_querySeen.Capacity < requestedQueries) _querySeen.Capacity = requestedQueries;
            if (_queryCandidates.Capacity < requestedQueries) _queryCandidates.Capacity = requestedQueries;
            if (_oldBuckets.Capacity < requestedBuckets) _oldBuckets.Capacity = requestedBuckets;
            if (_bucketValues.Capacity < requestedBodies) _bucketValues.Capacity = requestedBodies;
        }

        /// <summary>
        /// Inserts or moves a body AABB. maxX/maxY are exclusive. The caller must
        /// provide a conservative world-cell AABB for rotated bodies.
        /// </summary>
        public void UpdateAabb(ulong bodyId, int minX, int minY, int maxX, int maxY)
        {
            EnsureUsable();
            ValidateBodyId(bodyId);
            ValidateRect(minX, minY, maxX, maxY);
            GetBucketRange(minX, minY, maxX, maxY,
                out int minBucketX, out int minBucketY, out int maxBucketX, out int maxBucketY);
            int newBucketCount = CheckedBucketCount(minBucketX, minBucketY, maxBucketX, maxBucketY);

            bool existing = _bounds.TryGetValue(bodyId, out BodyBounds oldBounds);
            int oldBucketCount = existing ? _bodyBuckets.CountValuesForKey(bodyId) : 0;
            EnsureUpdateCapacity(existing, oldBucketCount, newBucketCount);
            if (existing) RemoveBodyFromBuckets(bodyId);

            BodyBounds next = new BodyBounds(minX, minY, maxX, maxY);
            if (existing) _bounds[bodyId] = next;
            else if (!_bounds.TryAdd(bodyId, next))
                throw new InvalidOperationException("刚体空间索引主体容量不足，请在屏障调用 Reserve。");

            AddBodyBuckets(bodyId, minBucketX, minBucketY, maxBucketX, maxBucketY);
        }

        public void UpdateAabb(ulong bodyId, RectInt aabb)
        {
            UpdateAabb(bodyId, aabb.xMin, aabb.yMin, aabb.xMax, aabb.yMax);
        }

        public bool Remove(ulong bodyId)
        {
            EnsureUsable();
            ValidateBodyId(bodyId);
            if (!_bounds.ContainsKey(bodyId)) return false;
            RemoveBodyFromBuckets(bodyId);
            return _bounds.Remove(bodyId);
        }

        /// <summary>
        /// Queries candidate body IDs intersecting a closed-open cell rectangle.
        /// IDs are deduplicated and sorted by BodyId, so hash bucket iteration order
        /// never leaks into deterministic physics/query decisions. Returns false when
        /// destination.Capacity is too small and leaves destination empty; requiredCount
        /// reports the needed number of candidates.
        /// </summary>
        public bool QueryRect(int minX, int minY, int maxX, int maxY,
            NativeList<ulong> destination, out int requiredCount)
        {
            EnsureUsable();
            if (!destination.IsCreated) throw new ArgumentException("查询目标列表未创建。", nameof(destination));
            ValidateRect(minX, minY, maxX, maxY);
            destination.Clear();
            _querySeen.Clear();
            _queryCandidates.Clear();
            GetBucketRange(minX, minY, maxX, maxY,
                out int minBucketX, out int minBucketY, out int maxBucketX, out int maxBucketY);

            for (long bucketY = minBucketY; bucketY <= maxBucketY; bucketY++)
                for (long bucketX = minBucketX; bucketX <= maxBucketX; bucketX++)
                {
                    long bucket = MakeBucketKey((int)bucketX, (int)bucketY);
                    if (!_buckets.TryGetFirstValue(bucket, out ulong bodyId,
                        out NativeParallelMultiHashMapIterator<long> iterator)) continue;
                    do
                    {
                        if (!_bounds.TryGetValue(bodyId, out BodyBounds bounds) ||
                            !bounds.Intersects(minX, minY, maxX, maxY) || _querySeen.Contains(bodyId)) continue;
                        EnsureQueryCapacity();
                        _querySeen.Add(bodyId);
                        _queryCandidates.AddNoResize(bodyId);
                    }
                    while (_buckets.TryGetNextValue(out bodyId, ref iterator));
                }

            _queryCandidates.AsArray().Sort();
            requiredCount = _queryCandidates.Length;
            if (destination.Capacity < requiredCount) return false;
            for (int i = 0; i < requiredCount; i++) destination.AddNoResize(_queryCandidates[i]);
            return true;
        }

        public bool QueryRect(RectInt rect, NativeList<ulong> destination, out int requiredCount)
        {
            return QueryRect(rect.xMin, rect.yMin, rect.xMax, rect.yMax, destination, out requiredCount);
        }

        public bool QueryRect(int minX, int minY, int maxX, int maxY, NativeList<ulong> destination)
        {
            return QueryRect(minX, minY, maxX, maxY, destination, out _);
        }

        public bool QueryRect(RectInt rect, NativeList<ulong> destination)
        {
            return QueryRect(rect.xMin, rect.yMin, rect.xMax, rect.yMax, destination, out _);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_bounds.IsCreated) _bounds.Dispose();
            if (_buckets.IsCreated) _buckets.Dispose();
            if (_bodyBuckets.IsCreated) _bodyBuckets.Dispose();
            if (_querySeen.IsCreated) _querySeen.Dispose();
            if (_queryCandidates.IsCreated) _queryCandidates.Dispose();
            if (_oldBuckets.IsCreated) _oldBuckets.Dispose();
            if (_bucketValues.IsCreated) _bucketValues.Dispose();
            _disposed = true;
        }

        private void AddBodyBuckets(ulong bodyId, int minBucketX, int minBucketY,
            int maxBucketX, int maxBucketY)
        {
            for (long bucketY = minBucketY; bucketY <= maxBucketY; bucketY++)
                for (long bucketX = minBucketX; bucketX <= maxBucketX; bucketX++)
                {
                    long bucket = MakeBucketKey((int)bucketX, (int)bucketY);
                    _buckets.Add(bucket, bodyId);
                    _bodyBuckets.Add(bodyId, bucket);
                }
        }

        private void RemoveBodyFromBuckets(ulong bodyId)
        {
            _oldBuckets.Clear();
            if (_bodyBuckets.TryGetFirstValue(bodyId, out long bucket,
                out NativeParallelMultiHashMapIterator<ulong> iterator))
            {
                do { _oldBuckets.AddNoResize(bucket); }
                while (_bodyBuckets.TryGetNextValue(out bucket, ref iterator));
            }
            _bodyBuckets.Remove(bodyId);
            for (int i = 0; i < _oldBuckets.Length; i++) RemoveBodyFromBucket(_oldBuckets[i], bodyId);
        }

        private void RemoveBodyFromBucket(long bucket, ulong bodyId)
        {
            _bucketValues.Clear();
            if (!_buckets.TryGetFirstValue(bucket, out ulong value,
                out NativeParallelMultiHashMapIterator<long> iterator)) return;
            do
            {
                if (value != bodyId) _bucketValues.AddNoResize(value);
            }
            while (_buckets.TryGetNextValue(out value, ref iterator));
            _buckets.Remove(bucket);
            for (int i = 0; i < _bucketValues.Length; i++) _buckets.Add(bucket, _bucketValues[i]);
        }

        private void EnsureUpdateCapacity(bool existing, int oldBucketCount, int newBucketCount)
        {
            long bodyCount = _bounds.Count() + (existing ? 0L : 1L);
            long bucketCount = (long)_buckets.Count() - oldBucketCount + newBucketCount;
            long bodyBucketCount = (long)_bodyBuckets.Count() - oldBucketCount + newBucketCount;
            if (bodyCount > _bounds.Capacity || bucketCount > _buckets.Capacity ||
                bodyBucketCount > _bodyBuckets.Capacity || oldBucketCount > _oldBuckets.Capacity ||
                _bounds.Count() > _bucketValues.Capacity)
                throw new InvalidOperationException("刚体空间索引容量不足，请在屏障调用 Reserve。");
        }

        private void EnsureQueryCapacity()
        {
            if (_querySeen.Count() >= _querySeen.Capacity ||
                _queryCandidates.Length >= _queryCandidates.Capacity)
                throw new InvalidOperationException("刚体空间索引查询容量不足，请在屏障调用 Reserve。");
        }

        private static int CheckedBucketCount(int minBucketX, int minBucketY, int maxBucketX, int maxBucketY)
        {
            long width = (long)maxBucketX - minBucketX + 1;
            long height = (long)maxBucketY - minBucketY + 1;
            long count = checked(width * height);
            if (count > int.MaxValue) throw new ArgumentOutOfRangeException("aabb", "AABB覆盖的空间桶过多。");
            return (int)count;
        }

        private static void GetBucketRange(int minX, int minY, int maxX, int maxY,
            out int minBucketX, out int minBucketY, out int maxBucketX, out int maxBucketY)
        {
            minBucketX = FloorDiv(minX, BucketSize);
            minBucketY = FloorDiv(minY, BucketSize);
            maxBucketX = FloorDiv(maxX - 1, BucketSize);
            maxBucketY = FloorDiv(maxY - 1, BucketSize);
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            if (value < 0 && value % divisor != 0) quotient--;
            return quotient;
        }

        private static long MakeBucketKey(int bucketX, int bucketY)
        {
            return ((long)bucketX << 32) | (uint)bucketY;
        }

        private static void ValidateBodyId(ulong bodyId)
        {
            if (bodyId == 0) throw new ArgumentOutOfRangeException(nameof(bodyId), "BodyId 必须非零。");
        }

        private static void ValidateRect(int minX, int minY, int maxX, int maxY)
        {
            if (minX >= maxX || minY >= maxY)
                throw new ArgumentException("空间索引矩形必须为正面积的闭左开右区域。", nameof(maxX));
        }

        private static void ValidateCapacity(int value, string name)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(name);
        }

        private void EnsureUsable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeBodySpatialIndex));
        }

        private readonly struct BodyBounds
        {
            internal readonly int MinX, MinY, MaxX, MaxY;

            internal BodyBounds(int minX, int minY, int maxX, int maxY)
            { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }

            internal bool Intersects(int minX, int minY, int maxX, int maxY) =>
                MinX < maxX && MaxX > minX && MinY < maxY && MaxY > minY;
        }
    }
}
