using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Spatial
{
    // 物理候选的完整接触集合，与规则查询共用同一精确几何判定。
    internal static class MaterialContactResolver
    {
        internal static WorldResult Collect(IWorkingWorldView world, ReadOnlySpan<BodySnapshot> poses, out CellContact[] contacts)
        {
            contacts = Array.Empty<CellContact>();
            var bodies = new Dictionary<ulong, BodyPose>(); foreach (BodySnapshot body in poses) bodies.Add(body.BodyId, body.Pose);
            CellKey[] keys = world.OccupiedCells.ToArray(); Array.Sort(keys, (a, b) => a.Position.CompareTo(b.Position));
            var cells = new CellGeometry[keys.Length];
            var bins = new Dictionary<Vector2Int, List<int>>();
            float size = world.Config.CellSize, epsilon = size * 1e-4f;
            for (int i = 0; i < keys.Length; i++)
            {
                CellKey key = keys[i];
                BodyPose pose = key.Position.OwnerKind == OwnerKind.Grid ? new BodyPose(world.Origin, 0) : bodies[key.Position.BodyId];
                cells[i] = new CellGeometry(key, pose, new Vector2Int(key.Position.X, key.Position.Y), size);
                Vector2 min = ExactCellGeometry.Corner(cells[i], 0), max = min;
                for (int v = 1; v < 4; v++) { Vector2 point = ExactCellGeometry.Corner(cells[i], v); min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
                int left = Mathf.FloorToInt((min.x - world.Origin.x - epsilon) / size), right = Mathf.FloorToInt((max.x - world.Origin.x + epsilon) / size);
                int bottom = Mathf.FloorToInt((min.y - world.Origin.y - epsilon) / size), top = Mathf.FloorToInt((max.y - world.Origin.y + epsilon) / size);
                for (int y = bottom; y <= top; y++) for (int x = left; x <= right; x++)
                {
                    var bin = new Vector2Int(x, y); if (!bins.TryGetValue(bin, out List<int> items)) bins.Add(bin, items = new List<int>());
                    items.Add(i);
                }
            }
            var pairs = new HashSet<ulong>(); var found = new List<CellContact>();
            foreach (List<int> bin in bins.Values)
                for (int a = 0; a < bin.Count; a++) for (int b = a + 1; b < bin.Count; b++)
                {
                    int first = bin[a], second = bin[b];
                    CellPositionKey x = keys[first].Position, y = keys[second].Position;
                    if (x.OwnerKind == y.OwnerKind && x.BodyId == y.BodyId) continue;
                    ulong pair = ((ulong)(uint)first << 32) | (uint)second;
                    if (!pairs.Add(pair)) continue;
                    if (pairs.Count > keys.Length * 64L)
                        return WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Physics", "contactCapacity", "宽阶段工作集超过有界容量。"));
                    CellContact contact = ExactCellGeometry.Contact(cells[first], cells[second], epsilon);
                    if (contact.Feature == ContactFeature.Separated || contact.Feature == ContactFeature.VertexVertex) continue;
                    if (found.Count >= keys.Length * 8L)
                        return WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Physics", "contactCapacity", "完整候选接触或宽阶段工作集超过有界容量。"));
                    found.Add(contact);
                }
            found.Sort((a, b) => { int first = a.First.Position.CompareTo(b.First.Position); return first == 0 ? a.Second.Position.CompareTo(b.Second.Position) : first; });
            contacts = found.ToArray();
            return WorldResult.Success();
        }
    }
}
