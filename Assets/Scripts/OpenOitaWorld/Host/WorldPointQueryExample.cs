using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Host
{
    // 只读接入示例；坐标由调用方提供，不创建实体，也不推进世界。
    [AddComponentMenu("OpenOita/世界坐标元素查询示例")]
    public sealed class WorldPointQueryExample : MonoBehaviour
    {
        [SerializeField, InspectorName("世界宿主")] private WorldHost worldHost;
        [SerializeField, InspectorName("Unity世界XY坐标")] private Vector2 worldPosition;
        [SerializeField, InspectorName("每帧末读取")] private bool sampleEveryFrame = true;

        public PointQueryResult LastQuery { get; private set; }
        public bool HasMaterialInfo { get; private set; }
        public MaterialRuntimeEntry LastMaterial { get; private set; }

        public void Bind(WorldHost host) => worldHost = host;

        public PointQueryResult Sample(Vector2 position)
        {
            IWorld world = worldHost != null ? worldHost.World : null;
            LastQuery = world == null
                ? new PointQueryResult(WorldResult.Failure(WorldErrorCode.NotReady,
                    new WorldDiagnostic("坐标查询示例", "WorldHost.World", "世界尚未创建，请等待宿主创建成功。")), default)
                : world.QueryPoint(position);
            HasMaterialInfo = false;
            LastMaterial = default;
            if (LastQuery.Result.IsSuccess && LastQuery.HasHit && world is IWorldMaterialCatalog catalog &&
                catalog.Materials.TryGet(LastQuery.Hit.MaterialId, out MaterialRuntimeEntry entry))
            {
                LastMaterial = entry;
                HasMaterialInfo = true;
            }
            return LastQuery;
        }

        private void LateUpdate()
        {
            if (sampleEveryFrame) Sample(worldPosition);
        }
    }
}
