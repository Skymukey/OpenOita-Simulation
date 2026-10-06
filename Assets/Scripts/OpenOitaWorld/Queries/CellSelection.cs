using System;
using OpenOita.Contracts;
using OpenOita.Spatial;
using UnityEngine;

namespace OpenOita.Queries
{
    // 命令及查询共用M06真实格几何；编辑擦除预览可直接调用公开QueryRegion。
    internal static class CellSelection
    {
        internal static CellGeometry Geometry(in CellKey key, Vector2 origin, float size, ReadOnlySpan<BodySnapshot> bodies)
        {
            BodyPose pose = new BodyPose(origin, 0);
            if (key.Position.OwnerKind == OwnerKind.Body)
                foreach (BodySnapshot body in bodies)
                    if (body.BodyId == key.Position.BodyId) { pose = body.Pose; break; }
            return new CellGeometry(key, pose, new Vector2Int(key.Position.X, key.Position.Y), size);
        }
    }
}
