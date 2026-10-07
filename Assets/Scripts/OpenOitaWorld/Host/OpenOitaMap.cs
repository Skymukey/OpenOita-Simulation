using OpenOita.Contracts;
using OpenOita.Data;
using UnityEngine;

namespace OpenOita.Host
{
    [DisallowMultipleComponent]
    public sealed class OpenOitaMap : MonoBehaviour
    {
        [SerializeField, InspectorName("关卡资产")] private OpenOitaMapAsset _level;
        [SerializeField, InspectorName("参与运行")] private bool _participate = true;
        public OpenOitaMapAsset Level => _level;
        public bool Participate => _participate;
        public Vector2 Origin => transform.position;
        public WorldHost Host => GetComponent<WorldHost>();
        public void EnsureHost() { if (Host == null) gameObject.AddComponent<WorldHost>(); }
        private void Reset() { EnsureHost(); }
        private void Awake() { EnsureHost(); }
        public void SetLevel(OpenOitaMapAsset level) { _level = level; }
        public void SetParticipation(bool participate) { _participate = participate; }

        public WorldResult ValidateInput(out WorldSources sources)
        {
            sources = null;
            WorldResult transformResult = ValidateTransform();
            if (!transformResult.IsSuccess) return transformResult;
            if (_level == null) return Error(WorldErrorCode.InvalidConfig, "关卡资产缺失，请绑定合法关卡。不会退回默认配置。");
            if (_level.HasPendingRecovery) return Error(WorldErrorCode.NotReady, "有未写回的待处理笔触，请恢复权限并保存关卡后再试玩。");
            WorldResult result = _level.BuildEditingData(out SceneMaterialData data);
            return result.IsSuccess ? data.Export(out sources) : result;
        }

        public WorldResult ValidateTransform()
        {
            if (!ContractDefaults.IsFinite(Origin) || Mathf.Abs(transform.position.z) > 0.00001f ||
                Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.0001f ||
                (transform.lossyScale - Vector3.one).sqrMagnitude > 0.00000001f)
                return Error(WorldErrorCode.InvalidArgument, "地图及父对象须世界Z=0、零旋转、单位世界缩放。原点取对象世界XY位置。");
            // 拒绝父级非均匀缩放/旋转抵消形成的剪切。
            Matrix4x4 matrix = transform.localToWorldMatrix;
            if (((Vector3)matrix.GetColumn(0) - Vector3.right).sqrMagnitude > 0.00000001f ||
                ((Vector3)matrix.GetColumn(1) - Vector3.up).sqrMagnitude > 0.00000001f)
                return Error(WorldErrorCode.InvalidArgument, "父对象变换产生了不支持的剪切。");
            return WorldResult.Success();
        }

        public static WorldResult ValidateSingleRunner()
        {
            int count = 0;
            foreach (OpenOitaMap map in FindObjectsByType<OpenOitaMap>(FindObjectsSortMode.None))
                if (map.isActiveAndEnabled && map.Participate && map.Host != null && map.Host.isActiveAndEnabled) count++;
            return count <= 1 ? WorldResult.Success() : Error(WorldErrorCode.InvalidConfig,
                "多个地图参与运行。请停止Play，在Inspector只启用一个地图的“参与运行”。所有地图均未创建世界。");
        }

        private static WorldResult Error(WorldErrorCode code, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("地图启动", "地图", message));

        private void OnEnable() { if (Application.isPlaying) { EnsureHost(); Host.RequestConfiguredRestart(); } }
        private void OnDisable() { if (Application.isPlaying) Host?.CloseWorld(); }
    }
}
