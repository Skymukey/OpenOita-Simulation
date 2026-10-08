using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Data
{
    // 三份规范来源是唯一持久初态；编辑缓存及运行世界均为独立副本。
    public sealed class OpenOitaMapAsset : ScriptableObject
    {
        [SerializeField, HideInInspector] private int _formatVersion = 1;
        [SerializeField, HideInInspector] private string _materials;
        [SerializeField, HideInInspector] private string _config;
        [SerializeField, HideInInspector] private string _scene;
        [SerializeField, HideInInspector] private int _revision;
        [SerializeField, HideInInspector] private string _pendingMaterials, _pendingConfig, _pendingScene;
        public int Revision => _revision;
        public WorldSources Sources => new WorldSources(_materials, _config, _scene);
        public bool HasPendingRecovery => !string.IsNullOrEmpty(_pendingScene);
        public WorldSources PendingRecovery => new WorldSources(_pendingMaterials, _pendingConfig, _pendingScene);

        public void RetainPendingRecovery(WorldSources sources)
        {
            _pendingMaterials = sources.MaterialsText; _pendingConfig = sources.WorldConfigText; _pendingScene = sources.SceneText;
        }
        public void ClearPendingRecovery() { _pendingMaterials = _pendingConfig = _pendingScene = null; }

        public WorldResult BuildEditingData(out SceneMaterialData data)
        {
            data = null;
            if (_formatVersion != 1) return WorldResult.Failure(WorldErrorCode.InvalidConfig,
                new WorldDiagnostic("关卡资产", "version", "不支持的关卡资产版本。"));
            return SceneMaterialData.Load(Sources, out data);
        }

        public WorldResult ReplaceSources(WorldSources sources)
        {
            // 编辑资产按输入世界配置的 schemaVersion 选择严格 Loader；V2 不回落到 v1。
            WorldResult loaded = SceneMaterialData.Load(sources, out SceneMaterialData data);
            if (!loaded.IsSuccess) return loaded;
            WorldResult result = data.Export(out WorldSources canonical);
            if (!result.IsSuccess) return result;
            _materials = canonical.MaterialsText;
            _config = canonical.WorldConfigText;
            _scene = canonical.SceneText;
            _formatVersion = 1;
            _revision++;
            return result;
        }
    }
}
