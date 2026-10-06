using System;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Spatial
{
    // 所有最终判定使用实际正方形；计算用 double，容差只吸收浮点换算误差。
    public sealed class ExactCellGeometry : ICellGeometryService
    {
        public Vector2 Center(in CellGeometry cell) => Transform(cell, (cell.LocalCell.x + 0.5) * cell.CellSize, (cell.LocalCell.y + 0.5) * cell.CellSize);

        public static Vector2 Transform(in CellGeometry cell, double x, double y)
        {
            double c = Math.Cos(cell.Pose.AngleRadians), s = Math.Sin(cell.Pose.AngleRadians);
            return new Vector2((float)(cell.Pose.Position.x + c * x - s * y), (float)(cell.Pose.Position.y + s * x + c * y));
        }

        public static Vector2 Corner(in CellGeometry cell, int index) => Transform(cell,
            (cell.LocalCell.x + (index == 1 || index == 2 ? 1d : 0d)) * cell.CellSize,
            (cell.LocalCell.y + (index >= 2 ? 1d : 0d)) * cell.CellSize);

        private static void Local(in CellGeometry cell, Vector2 point, out double x, out double y)
        {
            double c = Math.Cos(cell.Pose.AngleRadians), s = Math.Sin(cell.Pose.AngleRadians);
            double dx = (double)point.x - cell.Pose.Position.x, dy = (double)point.y - cell.Pose.Position.y;
            x = (c * dx + s * dy) / cell.CellSize - cell.LocalCell.x;
            y = (-s * dx + c * dy) / cell.CellSize - cell.LocalCell.y;
        }

        public bool ContainsPoint(in CellGeometry cell, Vector2 point)
        {
            Local(cell, point, out double x, out double y);
            return x >= 0 && x < 1 && y >= 0 && y < 1;
        }

        public bool IntersectSegment(in CellGeometry cell, Vector2 start, Vector2 end, out double firstIntersectionT)
        {
            Local(cell, start, out double x, out double y);
            Local(cell, end, out double ex, out double ey);
            double lo = 0, hi = 1;
            bool hit = Clip(x, ex - x, ref lo, ref hi) && Clip(y, ey - y, ref lo, ref hi);
            firstIntersectionT = hit ? lo : 0;
            return hit;
        }

        private static bool Clip(double start, double delta, ref double lo, ref double hi)
        {
            if (Math.Abs(delta) < 1e-14) return start >= 0 && start <= 1;
            double a = -start / delta, b = (1 - start) / delta;
            if (a > b) (a, b) = (b, a);
            lo = Math.Max(lo, a); hi = Math.Min(hi, b);
            return lo <= hi;
        }

        // SAT 的最小完整分离平移深度；包括一个方形完全包含另一个的情况。
        public static double Penetration(in CellGeometry a, in CellGeometry b)
        {
            double minimum = double.PositiveInfinity;
            for (int i = 0; i < 4; i++)
            {
                double angle = i < 2 ? a.Pose.AngleRadians : b.Pose.AngleRadians;
                double c = Math.Cos(angle), s = Math.Sin(angle);
                // 直接取正交轴，避免cos(pi/2)残差使轴对齐的共边格产生伪正面积。
                // 正面积阈值仍为严格大于0，不增加Spawn或排开容差。
                double ax = i % 2 == 0 ? c : -s, ay = i % 2 == 0 ? s : c;
                Project(a, ax, ay, out double amin, out double amax);
                Project(b, ax, ay, out double bmin, out double bmax);
                double depth = Math.Min(amax - bmin, bmax - amin);
                if (depth <= 0) return 0;
                minimum = Math.Min(minimum, depth);
            }
            return minimum;
        }

        private static void Project(in CellGeometry cell, double ax, double ay, out double min, out double max)
        {
            // 使用与点/线、边界检查相同的可表示顶点，避免中心公式与Unity float位姿
            // 产生两套边界，使原本共边的初态被判成正面积重叠。
            min = double.PositiveInfinity; max = double.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector2 corner = Corner(cell, i);
                double value = corner.x * ax + corner.y * ay;
                min = Math.Min(min, value); max = Math.Max(max, value);
            }
        }

        public static bool PositiveOverlap(in CellGeometry a, in CellGeometry b) => Penetration(a, b) > 0;

        public static CellContact Contact(in CellGeometry a, in CellGeometry b, float epsilon)
        {
            if (a.Key.Position.OwnerKind == b.Key.Position.OwnerKind && a.Key.Position.BodyId == b.Key.Position.BodyId)
            {
                int dx = Math.Abs(a.Key.Position.X - b.Key.Position.X), dy = Math.Abs(a.Key.Position.Y - b.Key.Position.Y);
                return new CellContact(a.Key, b.Key, dx + dy == 1 ? ContactFeature.EdgeEdge : ContactFeature.Separated, dx + dy == 1 ? 0 : float.PositiveInfinity);
            }
            if (PositiveOverlap(a, b)) return new CellContact(a.Key, b.Key, ContactFeature.PositiveAreaOverlap, 0);
            double best = double.PositiveInfinity;
            ContactFeature feature = ContactFeature.VertexVertex;
            double tolerance = Math.Min(a.CellSize, b.CellSize) * 1e-7;
            for (int direction = 0; direction < 2; direction++)
            {
                CellGeometry from = direction == 0 ? a : b, to = direction == 0 ? b : a;
                for (int v = 0; v < 4; v++)
                {
                    Vector2 point = Corner(from, v);
                    for (int e = 0; e < 4; e++)
                    {
                        Vector2 p = Corner(to, e), q = Corner(to, (e + 1) % 4);
                        double dx = (double)q.x - p.x, dy = (double)q.y - p.y;
                        double t = Math.Clamp(((point.x - p.x) * dx + (point.y - p.y) * dy) / (dx * dx + dy * dy), 0, 1);
                        double ex = point.x - p.x - t * dx, ey = point.y - p.y - t * dy;
                        double distance = Math.Sqrt(ex * ex + ey * ey);
                        ContactFeature next = t > 1e-6 && t < 1 - 1e-6 ? ContactFeature.VertexEdge : ContactFeature.VertexVertex;
                        if (distance < best - tolerance) { best = distance; feature = next; }
                        else if (distance <= best + tolerance && next == ContactFeature.VertexEdge) feature = next;
                        // 平行边有正长度投影重叠，即使最近点均为端点也属于边接触。
                        Vector2 u = Corner(from, (v + 1) % 4) - point;
                        if (Math.Abs(u.x * dy - u.y * dx) < tolerance * from.CellSize)
                        {
                            double t0 = ((point.x - p.x) * dx + (point.y - p.y) * dy) / (dx * dx + dy * dy);
                            double t1 = t0 + (u.x * dx + u.y * dy) / (dx * dx + dy * dy);
                            if (Math.Min(1, Math.Max(t0, t1)) - Math.Max(0, Math.Min(t0, t1)) > 1e-6 && distance <= best + tolerance)
                                feature = ContactFeature.EdgeEdge;
                        }
                    }
                }
            }
            if (best > epsilon + tolerance) feature = ContactFeature.Separated;
            return new CellContact(a.Key, b.Key, feature, (float)best);
        }
    }
}
