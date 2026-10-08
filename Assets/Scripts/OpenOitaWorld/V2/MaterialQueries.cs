using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Spatial;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.V2
{
    /// <summary>Read-only V2 queries using allocated tiles and occupied row bitmaps.</summary>
    public sealed unsafe class MaterialQueries : IDisposable
    {
        private readonly MaterialGrid _grid;
        private readonly MaterialPhysics _physics;
        private readonly Vector2 _origin;
        private readonly float _cellSize;
        private readonly int _capacity;
        private readonly ExactCellGeometry _geometry = new ExactCellGeometry();
        private NativeList<QueryHit> _scratch;
        private NativeList<ulong> _bodyCandidates;
        private bool _disposed;

        private struct QueryHit
        {
            public CellHit Hit;
            public double T;
            public QueryHit(CellHit hit, double t) { Hit = hit; T = t; }
        }

        public MaterialQueries(MaterialGrid grid, MaterialPhysics physics, Vector2 origin, float cellSize, int capacity)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _physics = physics;
            if (!ContractDefaults.IsFinite(origin) || !ContractDefaults.IsFinite(cellSize) || cellSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _origin = origin;
            _cellSize = cellSize;
            _capacity = capacity;
            _scratch = new NativeList<QueryHit>(Math.Min(capacity, 1024), Allocator.Persistent);
            _bodyCandidates = new NativeList<ulong>(Math.Max(64, physics?.BodyCount ?? 0), Allocator.Persistent);
        }

        public PointQueryResult QueryPoint(WorldVersion version, Vector2 point)
        {
            if (_disposed) return PointFailure(version, WorldErrorCode.Disposed, "查询对象已释放。");
            if (!ContractDefaults.IsFinite(point)) return PointFailure(version, WorldErrorCode.InvalidArgument, "点坐标必须有限。");
            // 世界半开范围适用于全部材料归属，不能让动态体绕过右/上端点排除。
            Vector2 maximum = _origin + new Vector2(_grid.Width, _grid.Height) * _cellSize;
            if (point.x < _origin.x || point.y < _origin.y || point.x >= maximum.x || point.y >= maximum.y)
                return new PointQueryResult(WorldResult.Success(), version);
            if (TryGridPoint(version, point, out CellHit gridHit))
                return new PointQueryResult(WorldResult.Success(), version, true, gridHit);
            BodyV2 selected = null;
            CellHit selectedHit = default;
            if (_physics != null)
            {
                _physics.QueryBodyCandidates(point, point, _bodyCandidates);
                for (int i = 0; i < _bodyCandidates.Length; i++)
                {
                    if (!_physics.TryGetBody(_bodyCandidates[i], out BodyV2 body)) continue;
                    if (TryBodyPoint(version, body, point, out CellHit bodyHit) && (selected == null || body.Id < selected.Id))
                    { selected = body; selectedHit = bodyHit; }
                }
            }
            return selected == null ? new PointQueryResult(WorldResult.Success(), version) :
                new PointQueryResult(WorldResult.Success(), version, true, selectedHit);
        }

        public QueryResult QueryRegion(WorldVersion version, WorldRect region, Span<CellHit> destination)
        {
            if (_disposed) return Failure(version, WorldErrorCode.Disposed, "查询对象已释放。", 0);
            if (!ValidRegion(region)) return Failure(version, WorldErrorCode.InvalidArgument, "区域必须有限且为正面积。", 0);
            _scratch.Clear();
            CollectGridRegion(version, region);
            if (_physics != null)
            {
                _physics.QueryBodyCandidates(region.Min, region.Max, _bodyCandidates);
                for (int i = 0; i < _bodyCandidates.Length; i++)
                    if (_physics.TryGetBody(_bodyCandidates[i], out BodyV2 body)) CollectBodyRegion(version, body, region);
            }
            SortScratch();
            return Complete(version, destination);
        }

        public QueryResult QuerySegment(WorldVersion version, Vector2 start, Vector2 end, Span<CellHit> destination)
        {
            if (_disposed) return Failure(version, WorldErrorCode.Disposed, "查询对象已释放。", 0);
            if (!ContractDefaults.IsFinite(start) || !ContractDefaults.IsFinite(end))
                return Failure(version, WorldErrorCode.InvalidArgument, "线段端点必须有限。", 0);
            _scratch.Clear();
            TraceLocalSegment(version, _grid, OwnerKind.Grid, 0, new BodyPose(_origin, 0), start - _origin, end - _origin, start, end);
            if (_physics != null)
            {
                _physics.QueryBodyCandidates(Vector2.Min(start, end), Vector2.Max(start, end), _bodyCandidates);
                for (int i = 0; i < _bodyCandidates.Length; i++)
                {
                    if (!_physics.TryGetBody(_bodyCandidates[i], out BodyV2 body)) continue;
                    TraceLocalSegment(version, body.Grid, OwnerKind.Body, body.Id, body.Pose,
                        Inverse(body.Pose, start), Inverse(body.Pose, end), start, end);
                }
            }
            SortScratch();
            return Complete(version, destination);
        }

        /// <summary>Converts absolute V2 deadlines into the public countdown snapshot.</summary>
        public static CellSnapshot Snapshot(GridCell cell, MaterialDefinition definition, ulong tick)
        {
            if (cell.MaterialId == 0) return default;
            CellCold cold = cell.Cold;
            uint fuel = cell.IsBurning ? Remaining(cold.BurnEndTick, tick) :
                cell.ComponentHandle == 0 ? definition.Fuel : cold.FuelRemaining;
            return new CellSnapshot(cell.MaterialId, cell.Flags, fuel,
                Remaining(cold.NextSpreadTick, tick), Remaining(cold.ExpiryTick, tick),
                Remaining(cell.NextMoveTick, tick), cold.IgnitedTick);
        }

        private static uint Remaining(ulong absolute, ulong tick)
        {
            if (absolute == 0 || absolute <= tick) return 0;
            ulong value = absolute - tick;
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static PointQueryResult PointFailure(WorldVersion version, WorldErrorCode code, string message) =>
            new PointQueryResult(Error(code, message), version);
        private static QueryResult Failure(WorldVersion version, WorldErrorCode code, string message, int required) =>
            new QueryResult(Error(code, message), version, required, 0);
        private static WorldResult Error(WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("V2Query", "query", message));

        private QueryResult Complete(WorldVersion version, Span<CellHit> destination)
        {
            int required = _scratch.Length;
            if (destination.Length < required)
                return Failure(version, WorldErrorCode.BufferTooSmall, "缓冲不足；未写入部分结果。", required);
            for (int i = 0; i < required; i++) destination[i] = _scratch[i].Hit;
            return new QueryResult(WorldResult.Success(), version, required, required);
        }

        private bool TryGridPoint(WorldVersion version, Vector2 point, out CellHit hit)
        {
            hit = default;
            if (!TryPointCoordinates(_grid, new BodyPose(_origin, 0), point, out int x, out int y)) return false;
            if (TryCell(_grid, OwnerKind.Grid, 0, x, y, version, out hit))
            {
                CellGeometry geometry = new CellGeometry(hit.Key, new BodyPose(_origin, 0), new Vector2Int(x, y), _cellSize);
                if (_geometry.ContainsPoint(geometry, point)) return true;
            }
            hit = default;
            return false;
        }

        private bool TryBodyPoint(WorldVersion version, BodyV2 body, Vector2 point, out CellHit hit)
        {
            hit = default;
            if (!TryPointCoordinates(body.Grid, body.Pose, point, out int x, out int y)) return false;
            if (!TryCell(body.Grid, OwnerKind.Body, body.Id, x, y, version, out hit)) return false;
            CellGeometry geometry = new CellGeometry(hit.Key, body.Pose, new Vector2Int(x, y), _cellSize);
            return _geometry.ContainsPoint(geometry, point);
        }

        private bool TryPointCoordinates(MaterialGrid grid, BodyPose pose, Vector2 point, out int x, out int y)
        {
            double c = Math.Cos(pose.AngleRadians), s = Math.Sin(pose.AngleRadians);
            double dx = (double)point.x - pose.Position.x, dy = (double)point.y - pose.Position.y;
            double localX = (c * dx + s * dy) / _cellSize;
            double localY = (-s * dx + c * dy) / _cellSize;
            x = y = 0;
            if (localX < 0 || localY < 0 || localX >= grid.Width || localY >= grid.Height) return false;
            x = (int)Math.Floor(localX);
            y = (int)Math.Floor(localY);
            return true;
        }

        private void CollectGridRegion(WorldVersion version, WorldRect region)
        {
            CellBounds(region, _origin.x, _origin.y, _cellSize, _grid.Width, _grid.Height, out int minX, out int maxX, out int minY, out int maxY);
            if (minX > maxX || minY > maxY) return;
            int minTileX = minX >> 5, maxTileX = maxX >> 5, minTileY = minY >> 5, maxTileY = maxY >> 5;
            for (int ty = minTileY; ty <= maxTileY; ty++) for (int tx = minTileX; tx <= maxTileX; tx++)
            {
                MaterialTile tile = _grid.Tiles[tx + ty * _grid.TileColumns];
                if (!tile.IsCreated) continue;
                int x0 = Math.Max(minX, tx * 32), x1 = Math.Min(maxX, tx * 32 + 31);
                int y0 = Math.Max(minY, ty * 32), y1 = Math.Min(maxY, ty * 32 + 31);
                for (int y = y0; y <= y1; y++)
                {
                    uint bits = tile.Occupied[y & 31] & Mask(x0 & 31, x1 & 31);
                    while (bits != 0)
                    {
                        int bit = LowestBit(bits), x = tx * 32 + bit;
                        if (x >= x0 && x <= x1 && region.ContainsCenter(_origin + new Vector2((x + .5f) * _cellSize, (y + .5f) * _cellSize)))
                            TryAddCell(version, _grid, OwnerKind.Grid, 0, x, y, 0);
                        bits &= bits - 1;
                    }
                }
            }
        }

        private void CollectBodyRegion(WorldVersion version, BodyV2 body, WorldRect region)
        {
            if (!BodyAabbIntersects(body, region)) return;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            AddBounds(Inverse(body.Pose, region.Min), ref minX, ref minY, ref maxX, ref maxY);
            AddBounds(Inverse(body.Pose, new Vector2(region.Max.x, region.Min.y)), ref minX, ref minY, ref maxX, ref maxY);
            AddBounds(Inverse(body.Pose, region.Max), ref minX, ref minY, ref maxX, ref maxY);
            AddBounds(Inverse(body.Pose, new Vector2(region.Min.x, region.Max.y)), ref minX, ref minY, ref maxX, ref maxY);
            int minCellX = Math.Max(0, (int)Math.Ceiling(minX / _cellSize - .5));
            int maxCellX = Math.Min(body.Grid.Width - 1, (int)Math.Ceiling(maxX / _cellSize - .5) - 1);
            int minCellY = Math.Max(0, (int)Math.Ceiling(minY / _cellSize - .5));
            int maxCellY = Math.Min(body.Grid.Height - 1, (int)Math.Ceiling(maxY / _cellSize - .5) - 1);
            if (minCellX > maxCellX || minCellY > maxCellY) return;
            int minTileX = minCellX >> 5, maxTileX = maxCellX >> 5, minTileY = minCellY >> 5, maxTileY = maxCellY >> 5;
            for (int ty = minTileY; ty <= maxTileY; ty++) for (int tx = minTileX; tx <= maxTileX; tx++)
            {
                MaterialTile tile = body.Grid.Tiles[tx + ty * body.Grid.TileColumns];
                if (!tile.IsCreated) continue;
                int x0 = Math.Max(minCellX, tx * 32), x1 = Math.Min(maxCellX, tx * 32 + 31);
                int y0 = Math.Max(minCellY, ty * 32), y1 = Math.Min(maxCellY, ty * 32 + 31);
                for (int y = y0; y <= y1; y++)
                {
                    uint bits = tile.Occupied[y & 31] & Mask(x0 & 31, x1 & 31);
                    while (bits != 0)
                    {
                        int bit = LowestBit(bits), x = tx * 32 + bit;
                        Vector2 center = _geometry.Center(new CellGeometry(default, body.Pose, new Vector2Int(x, y), _cellSize));
                        if (x >= x0 && x <= x1 && region.ContainsCenter(center))
                            TryAddCell(version, body.Grid, OwnerKind.Body, body.Id, x, y, 0);
                        bits &= bits - 1;
                    }
                }
            }
        }

        private void TraceLocalSegment(WorldVersion version, MaterialGrid grid, OwnerKind owner, ulong bodyId,
            BodyPose pose, Vector2 localStart, Vector2 localEnd, Vector2 worldStart, Vector2 worldEnd)
        {
            double enter = 0, exit = 1;
            if (!ClipAxis(localStart.x, localEnd.x - localStart.x, 0, grid.Width * _cellSize, ref enter, ref exit) ||
                !ClipAxis(localStart.y, localEnd.y - localStart.y, 0, grid.Height * _cellSize, ref enter, ref exit)) return;
            Vector2 at = localStart + (localEnd - localStart) * (float)enter;
            int x = Mathf.Clamp(Mathf.FloorToInt(at.x / _cellSize), 0, grid.Width - 1), y = Mathf.Clamp(Mathf.FloorToInt(at.y / _cellSize), 0, grid.Height - 1);
            double dx = localEnd.x - localStart.x, dy = localEnd.y - localStart.y;
            int stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0, stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
            double boundaryX = stepX > 0 ? (x + 1) * _cellSize : x * _cellSize, boundaryY = stepY > 0 ? (y + 1) * _cellSize : y * _cellSize;
            double nextX = stepX == 0 ? double.PositiveInfinity : (boundaryX - localStart.x) / dx;
            double nextY = stepY == 0 ? double.PositiveInfinity : (boundaryY - localStart.y) / dy;
            double deltaX = stepX == 0 ? double.PositiveInfinity : _cellSize / Math.Abs(dx), deltaY = stepY == 0 ? double.PositiveInfinity : _cellSize / Math.Abs(dy);
            int guard = 0, maxGuard = grid.Width + grid.Height + 4;
            while (guard++ < maxGuard && x >= 0 && y >= 0 && x < grid.Width && y < grid.Height)
            {
                TryAddSegmentCell(version, grid, owner, bodyId, pose, x, y, worldStart, worldEnd);
                if (nextX > exit + 1e-12 && nextY > exit + 1e-12) break;
                if (Math.Abs(nextX - nextY) <= 1e-12)
                {
                    if (stepX != 0) TryAddSegmentCell(version, grid, owner, bodyId, pose, x + stepX, y, worldStart, worldEnd);
                    if (stepY != 0) TryAddSegmentCell(version, grid, owner, bodyId, pose, x, y + stepY, worldStart, worldEnd);
                    x += stepX; y += stepY; nextX += deltaX; nextY += deltaY;
                }
                else if (nextX < nextY) { x += stepX; nextX += deltaX; }
                else { y += stepY; nextY += deltaY; }
            }
        }

        private void TryAddSegmentCell(WorldVersion version, MaterialGrid grid, OwnerKind owner, ulong bodyId,
            BodyPose pose, int x, int y, Vector2 start, Vector2 end)
        {
            if (!TryCell(grid, owner, bodyId, x, y, version, out CellHit hit)) return;
            CellGeometry geometry = new CellGeometry(hit.Key, pose, new Vector2Int(x, y), _cellSize);
            if (_geometry.IntersectSegment(geometry, start, end, out double t)) Add(hit, t);
        }

        private void TryAddCell(WorldVersion version, MaterialGrid grid, OwnerKind owner, ulong bodyId, int x, int y, double t)
        {
            if (TryCell(grid, owner, bodyId, x, y, version, out CellHit hit)) Add(hit, t);
        }

        private void Add(CellHit hit, double t)
        {
            // capacity is only the configured world-cell ceiling/reservation hint. Query
            // scratch may grow past its 1024 initial capacity so a sizing call can return
            // the complete RequiredCount with BufferTooSmall and no partial writes.
            _scratch.Add(new QueryHit(hit, t));
        }

        private static bool TryCell(MaterialGrid grid, OwnerKind owner, ulong bodyId, int x, int y,
            WorldVersion version, out CellHit hit)
        {
            hit = default;
            if (!grid.Inside(x, y)) return false;
            MaterialTile tile = grid.Tiles[grid.TileId(x, y)];
            if (!tile.IsCreated || (tile.Occupied[y & 31] & (1u << (x & 31))) == 0) return false;
            GridCell cell = grid.Read(x, y);
            if (cell.MaterialId == 0) return false;
            hit = new CellHit(version, new CellPositionKey(owner, bodyId, x, y), Snapshot(cell, grid.Definitions[cell.MaterialId], version.CommittedTick));
            return true;
        }

        private static void CellBounds(WorldRect region, float originX, float originY, float cellSize, int width, int height,
            out int minX, out int maxX, out int minY, out int maxY)
        {
            minX = Math.Max(0, (int)Math.Ceiling((region.Min.x - originX) / cellSize - .5));
            maxX = Math.Min(width - 1, (int)Math.Ceiling((region.Max.x - originX) / cellSize - .5) - 1);
            minY = Math.Max(0, (int)Math.Ceiling((region.Min.y - originY) / cellSize - .5));
            maxY = Math.Min(height - 1, (int)Math.Ceiling((region.Max.y - originY) / cellSize - .5) - 1);
        }

        private static uint Mask(int first, int last)
        {
            uint result = 0;
            for (int bit = first; bit <= last; bit++) result |= 1u << bit;
            return result;
        }

        private static int LowestBit(uint bits)
        {
            int bit = 0;
            while ((bits & 1u) == 0) { bits >>= 1; bit++; }
            return bit;
        }

        private static bool ValidRegion(WorldRect region) => ContractDefaults.IsFinite(region.Min) && ContractDefaults.IsFinite(region.Max) &&
            region.Max.x > region.Min.x && region.Max.y > region.Min.y;

        private bool BodyAabb(BodyV2 body, Vector2 point) => BodyAabbIntersects(body, new WorldRect(point, point + new Vector2(1e-6f, 1e-6f)));

        private bool BodyAabbIntersects(BodyV2 body, WorldRect region)
        {
            GetBodyBounds(body, out float minX, out float minY, out float maxX, out float maxY);
            return maxX >= region.Min.x && minX <= region.Max.x && maxY >= region.Min.y && minY <= region.Max.y;
        }

        private void GetBodyBounds(BodyV2 body, out float minX, out float minY, out float maxX, out float maxY)
        {
            Vector2 first = Transform(body.Pose, Vector2.zero);
            minX = maxX = first.x; minY = maxY = first.y;
            AddBounds(Transform(body.Pose, new Vector2(body.Grid.Width * _cellSize, 0)), ref minX, ref minY, ref maxX, ref maxY);
            AddBounds(Transform(body.Pose, new Vector2(body.Grid.Width * _cellSize, body.Grid.Height * _cellSize)), ref minX, ref minY, ref maxX, ref maxY);
            AddBounds(Transform(body.Pose, new Vector2(0, body.Grid.Height * _cellSize)), ref minX, ref minY, ref maxX, ref maxY);
        }

        private static void AddBounds(Vector2 value, ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            minX = Math.Min(minX, value.x); minY = Math.Min(minY, value.y);
            maxX = Math.Max(maxX, value.x); maxY = Math.Max(maxY, value.y);
        }

        private static void AddBounds(Vector2 value, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            minX = Math.Min(minX, value.x); minY = Math.Min(minY, value.y);
            maxX = Math.Max(maxX, value.x); maxY = Math.Max(maxY, value.y);
        }

        private static Vector2 Transform(BodyPose pose, Vector2 local)
        {
            float c = Mathf.Cos(pose.AngleRadians), s = Mathf.Sin(pose.AngleRadians);
            return pose.Position + new Vector2(c * local.x - s * local.y, s * local.x + c * local.y);
        }

        private static Vector2 Inverse(BodyPose pose, Vector2 world)
        {
            Vector2 delta = world - pose.Position;
            float c = Mathf.Cos(pose.AngleRadians), s = Mathf.Sin(pose.AngleRadians);
            return new Vector2(c * delta.x + s * delta.y, -s * delta.x + c * delta.y);
        }

        private void SortScratch() => _scratch.AsArray().Sort(QueryHitComparer.Instance);

        private struct QueryHitComparer : IComparer<QueryHit>
        {
            internal static readonly QueryHitComparer Instance = new QueryHitComparer();
            public int Compare(QueryHit first, QueryHit second)
            {
                int result = first.T.CompareTo(second.T);
                return result == 0 ? first.Hit.Position.CompareTo(second.Hit.Position) : result;
            }
        }

        private static bool ClipAxis(double start, double delta, double min, double max, ref double lo, ref double hi)
        {
            if (Math.Abs(delta) < 1e-14) return start >= min && start <= max;
            double a = (min - start) / delta, b = (max - start) / delta;
            if (a > b) (a, b) = (b, a);
            lo = Math.Max(lo, a); hi = Math.Min(hi, b);
            return lo <= hi;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_bodyCandidates.IsCreated) _bodyCandidates.Dispose();
        }
    }
}
