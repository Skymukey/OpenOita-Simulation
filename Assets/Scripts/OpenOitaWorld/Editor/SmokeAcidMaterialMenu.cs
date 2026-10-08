using System;
using System.IO;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEditor;
using UnityEngine;

namespace OpenOita.Editor
{
    public static class SmokeAcidMaterialMenu
    {
        private const string MenuPath = "OpenOita/V2/为选中关卡添加烟雾和酸";

        [MenuItem(MenuPath)]
        private static void UpgradeSelected()
        {
            try
            {
                Upgrade(Selection.activeObject as OpenOitaMapAsset);
                EditorUtility.DisplayDialog("材料已更新", "已加入烟雾和酸，木头可被酸腐蚀并在燃烧时产烟。重新打开关卡即可绘制。", "确定");
            }
            catch (Exception ex) { EditorUtility.DisplayDialog("材料更新失败", ex.Message, "确定"); }
        }

        [MenuItem(MenuPath, true)]
        private static bool CanUpgrade() => !EditorApplication.isPlayingOrWillChangePlaymode && Selection.activeObject is OpenOitaMapAsset;

        private static void Upgrade(OpenOitaMapAsset asset)
        {
            if (asset == null) throw new InvalidOperationException("请选择 V2 关卡资产。");
            WorldSources sources = asset.Sources;
            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(sources);
            if (!loaded.Result.IsSuccess) throw new InvalidOperationException(loaded.Result.Diagnostic.Message);
            JObject materials = JObject.Parse(sources.MaterialsText);
            JObject defaults = JObject.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "OpenOitaV2/materials.json")));
            var entries = (JArray)materials["materials"];
            foreach (JObject material in (JArray)defaults["materials"])
            {
                int id = (int)material["id"];
                if (id != 105 && id != 106) continue;
                JObject existing = Find(entries, id);
                if (existing == null) entries.Add(material.DeepClone());
                else if ((string)existing["name"] != (string)material["name"] || (string)existing["kind"] != (string)material["kind"])
                    throw new InvalidOperationException("材料 ID " + id + " 已被其它材料占用，未修改关卡。");
            }
            JObject wood = Find(entries, 104);
            if (wood != null && (string)wood["name"] == "木头" && wood["ruleParameters"]["burnable"] is JObject burn)
            {
                var tags = (JArray)wood["tags"];
                bool corrodible = false;
                foreach (JToken tag in tags) if ((string)tag == "corrodible") corrodible = true;
                if (!corrodible) tags.Add("corrodible");
                if (burn["smokeMaterialId"] == null) { burn["smokeMaterialId"] = 105; burn["smokeIntervalTicks"] = 15; }
            }
            Undo.RecordObject(asset, "添加烟雾和酸");
            WorldResult result = asset.ReplaceSources(new WorldSources(materials.ToString(), sources.WorldConfigText, sources.SceneText));
            if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic.Message);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log("烟雾和酸已接入：" + AssetDatabase.GetAssetPath(asset));
        }

        private static JObject Find(JArray entries, int id)
        {
            foreach (JObject entry in entries) if ((int)entry["id"] == id) return entry;
            return null;
        }
    }
}
