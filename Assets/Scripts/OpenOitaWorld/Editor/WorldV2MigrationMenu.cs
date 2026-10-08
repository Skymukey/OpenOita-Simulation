using System;
using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEditor;
using UnityEngine;

namespace OpenOita.Editor
{
    public static class WorldV2MigrationMenu
    {
        private const string MenuPath = "OpenOita/迁移选中关卡资产到 V2";

        [MenuItem(MenuPath, false, 85)]
        private static void MigrateSelected()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var source = Selection.activeObject as OpenOitaMapAsset;
            if (source == null) return;

            WorldResult result = WorldV2SourceConverter.Convert(source.Sources, out WorldSources converted);
            if (!result.IsSuccess)
            {
                EditorUtility.DisplayDialog("V2 迁移失败", Format(result), "确定");
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(source);
            string directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(directory)) directory = "Assets";
            directory = directory.Replace('\\', '/');
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(baseName)) baseName = source.name;
            string targetPath = AssetDatabase.GenerateUniqueAssetPath(directory + "/" + baseName + "-V2.asset");
            OpenOitaMapAsset target = ScriptableObject.CreateInstance<OpenOitaMapAsset>();
            try
            {
                WorldResult replaced = target.ReplaceSources(converted);
                if (!replaced.IsSuccess) throw new InvalidOperationException(Format(replaced));

                AssetDatabase.CreateAsset(target, targetPath);
                AssetDatabase.SaveAssets();
                Selection.activeObject = target;
                EditorGUIUtility.PingObject(target);
                EditorUtility.DisplayDialog("V2 迁移完成", "已另存为：" + targetPath + "\n原始关卡资产未覆盖。", "确定");
            }
            catch (Exception ex)
            {
                if (target != null && !EditorUtility.IsPersistent(target)) UnityEngine.Object.DestroyImmediate(target);
                EditorUtility.DisplayDialog("V2 迁移失败", ex.Message, "确定");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateMigrateSelected()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode && Selection.activeObject is OpenOitaMapAsset;
        }

        private static string Format(WorldResult result)
        {
            WorldDiagnostic diagnostic = result.Diagnostic;
            return result.ErrorCode + " / " + diagnostic.FileName + " / " + diagnostic.Target + "\n" + diagnostic.Message;
        }
    }
}
