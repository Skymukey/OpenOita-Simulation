using System;
using System.IO;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEditor;
using UnityEngine;

namespace OpenOita.Editor
{
    public static class OilMaterialMenu
    {
        private const string MenuPath = "OpenOita/V2/为选中关卡添加油";

        [MenuItem(MenuPath)]
        private static void UpgradeSelected()
        {
            try
            {
                Upgrade(Selection.activeObject as OpenOitaMapAsset);
                EditorUtility.DisplayDialog("材料已更新", "已加入可燃液体油。重新打开关卡后选择油绘制，并用初燃工具点燃。", "确定");
            }
            catch (Exception ex) { EditorUtility.DisplayDialog("材料更新失败", ex.Message, "确定"); }
        }

        [MenuItem(MenuPath, true)]
        private static bool CanUpgrade() => !EditorApplication.isPlayingOrWillChangePlaymode && Selection.activeObject is OpenOitaMapAsset;

        public static void Upgrade(OpenOitaMapAsset asset)
        {
            if (asset == null) throw new InvalidOperationException("请选择 V2 关卡资产。");
            WorldSources sources = asset.Sources;
            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(sources);
            if (!loaded.Result.IsSuccess) throw new InvalidOperationException(loaded.Result.Diagnostic.Message);
            JObject materials = JObject.Parse(sources.MaterialsText);
            JObject defaults = JObject.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "OpenOitaV2/materials.json")));
            var entries = (JArray)materials["materials"];
            // 油的排烟引用105，四材料旧V2关卡也可直接升级；已有自定义同名材料保留参数。
            foreach (JObject material in (JArray)defaults["materials"])
            {
                int id = (int)material["id"];
                if (id != 105 && id != 107) continue;
                JObject existing = null;
                foreach (JObject entry in entries) if ((int)entry["id"] == id) { existing = entry; break; }
                if (existing == null) entries.Add(material.DeepClone());
                else if ((string)existing["name"] != (string)material["name"] || (string)existing["kind"] != (string)material["kind"])
                    throw new InvalidOperationException("材料 ID " + id + " 已被其它材料占用，未修改关卡。");
            }
            var updated = new WorldSources(materials.ToString(), sources.WorldConfigText, sources.SceneText);
            WorldLoadResult validated = new WorldSourceLoaderV2().Load(updated);
            if (!validated.Result.IsSuccess) throw new InvalidOperationException(validated.Result.Diagnostic.Message);
            Undo.RecordObject(asset, "添加油材料");
            WorldResult result = asset.ReplaceSources(updated);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic.Message);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }
    }
}
