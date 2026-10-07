using System;
using System.Collections.Generic;
using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Host;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpenOita.Editor
{
    // Inspector与窗口共享资产身份、缓存和提交边界，绝不让旧窗口覆盖新修订。
    public sealed class MapEditingSession
    {
        private static readonly Dictionary<OpenOitaMapAsset, MapEditingSession> Sessions = new();
        public OpenOitaMapAsset Asset { get; }
        public SceneEditingDocument Document { get; } = new SceneEditingDocument();
        private string _knownScene, _knownConfig, _knownMaterials;
        private int _committedRevision;
        public bool Pending => Document.Data != null && Document.Data.Revision != _committedRevision;
        public WorldResult LastResult { get; private set; }

        private MapEditingSession(OpenOitaMapAsset asset)
        {
            Asset = asset; ReloadCache();
            if (asset.HasPendingRecovery && LastResult.IsSuccess)
            {
                LastResult = Document.Load(asset.PendingRecovery);
                if (LastResult.IsSuccess) _committedRevision = -1;
            }
        }
        public static MapEditingSession For(OpenOitaMapAsset asset)
        {
            if (asset == null) return null;
            if (!Sessions.TryGetValue(asset, out MapEditingSession session))
                Sessions.Add(asset, session = new MapEditingSession(asset));
            session.Refresh();
            return session;
        }

        public static bool FlushAll()
        {
            bool success = true;
            foreach (var pair in Sessions)
                if (pair.Key != null && !pair.Value.Commit().IsSuccess) success = false;
            return success;
        }

        public static void ClearCaches() { Sessions.Clear(); }
        public static void TrimUnusedCaches(HashSet<OpenOitaMapAsset> referenced)
        {
            var removed = new List<OpenOitaMapAsset>();
            foreach (var pair in Sessions)
                if (pair.Key == null || (!referenced.Contains(pair.Key) && !pair.Value.Pending)) removed.Add(pair.Key);
            foreach (var asset in removed) Sessions.Remove(asset);
        }
        public bool Refresh()
        {
            WorldSources sources = Asset.Sources;
            if (_knownScene == sources.SceneText && _knownConfig == sources.WorldConfigText && _knownMaterials == sources.MaterialsText) return false;
            if (Pending)
            {
                LastResult = Error("资产已有外部修订，待处理笔触仍保留。请保存或恢复待处理编辑后再切换。");
                return false;
            }
            ReloadCache();
            return true;
        }

        private void ReloadCache()
        {
            LastResult = Document.Load(Asset.Sources);
            if (!LastResult.IsSuccess) { Document.Clear(); return; }
            Remember();
        }

        private void Remember()
        {
            WorldSources source = Asset.Sources;
            _knownScene = source.SceneText; _knownConfig = source.WorldConfigText; _knownMaterials = source.MaterialsText;
            _committedRevision = Document.Data.Revision;
        }

        public static WorldResult CheckWritable(OpenOitaMapAsset asset)
        {
            if (Application.isPlaying) return Error("Play期间禁止修改关卡资产。");
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsOpenForEdit(asset)) return Error("关卡必须是可写的持久资产。");
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return Error("关卡文件为只读：" + path);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Error(path + "：" + ex.Message); }
            return WorldResult.Success();
        }

        public WorldResult Commit(string undoName = "绘制地图")
        {
            if (!Pending) return LastResult = Asset.BuildEditingData(out _);
            WorldResult exported = Document.Data.Export(out WorldSources sources);
            if (!exported.IsSuccess) return LastResult = exported;
            WorldResult writable = CheckWritable(Asset);
            if (!writable.IsSuccess)
            {
                Asset.RetainPendingRecovery(sources); EditorUtility.SetDirty(Asset);
                return LastResult = writable;
            }
            WorldSources current = Asset.Sources;
            if (current.SceneText != _knownScene || current.MaterialsText != _knownMaterials || current.WorldConfigText != _knownConfig)
                return LastResult = Error("资产发生外部修订，拒绝用旧笔触覆盖；待处理编辑已保留。");
            WorldResult result;
            if (!new WorldSourceLoader().Load(sources).Result.IsSuccess) return LastResult = Error("笔触校验失败，已保留待处理编辑。");
            Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName(undoName);
            Undo.RegisterCompleteObjectUndo(Asset, undoName);
            result = Asset.ReplaceSources(sources);
            if (result.IsSuccess) { Asset.ClearPendingRecovery(); EditorUtility.SetDirty(Asset); Remember(); UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); }
            return LastResult = result;
        }

        public WorldResult Import(WorldSources sources)
        {
            WorldResult writable = CheckWritable(Asset);
            if (!writable.IsSuccess) return LastResult = writable;
            WorldLoadResult loaded = new WorldSourceLoader().Load(sources);
            if (!loaded.Result.IsSuccess) return LastResult = loaded.Result;
            WorldResult committed = Pending ? Commit() : WorldResult.Success();
            if (!committed.IsSuccess) return committed;
            Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("导入地图JSON");
            Undo.RegisterCompleteObjectUndo(Asset, "导入地图JSON");
            WorldResult result = Asset.ReplaceSources(sources);
            if (result.IsSuccess) { EditorUtility.SetDirty(Asset); ReloadCache(); UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); }
            return LastResult = result;
        }

        public WorldResult Save(Action<OpenOitaMapAsset> save = null)
        {
            WorldResult result = Commit();
            if (result.IsSuccess) result = CheckWritable(Asset);
            if (result.IsSuccess) result = Asset.BuildEditingData(out _);
            if (!result.IsSuccess) return LastResult = result;
            string path = AssetDatabase.GetAssetPath(Asset);
            try
            {
                if (save == null) AssetDatabase.SaveAssetIfDirty(Asset); else save(Asset);
                if (EditorUtility.IsDirty(Asset)) return LastResult = Error("保存未完成，脏状态保留：" + path);
                result = ReadDiskSources(path, out WorldSources disk);
                WorldSources expected = Asset.Sources;
                if (!result.IsSuccess || disk.SceneText != expected.SceneText || disk.MaterialsText != expected.MaterialsText || disk.WorldConfigText != expected.WorldConfigText)
                {
                    EditorUtility.SetDirty(Asset);
                    return LastResult = Error("保存后磁盘内容核对失败，修改及脏状态保留：" + path);
                }
                return LastResult = WorldResult.Success();
            }
            catch (Exception ex) { EditorUtility.SetDirty(Asset); return LastResult = Error("保存失败：" + path + "，" + ex.Message); }
        }

        public bool ConfirmReplacement()
        {
            if (Pending && !Commit().IsSuccess) return false;
            if (!EditorUtility.IsDirty(Asset)) return true;
            int choice = EditorUtility.DisplayDialogComplex("替换当前关卡", "当前关卡有未保存修改。加载失败仍保留当前内容。", "保存后继续", "取消", "放弃并继续");
            return choice == 2 || (choice == 0 && Save().IsSuccess);
        }

        public WorldResult ReloadFromDisk()
        {
            string path = AssetDatabase.GetAssetPath(Asset);
            WorldResult result = ReadDiskSources(path, out WorldSources sources);
            return LastResult = result.IsSuccess ? Import(sources) : result;
        }

        private static WorldResult ReadDiskSources(string path, out WorldSources sources)
        {
            sources = null;
            try
            {
                UnityEngine.Object[] disk = UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(path);
                try
                {
                    foreach (var value in disk) if (value is OpenOitaMapAsset level) sources = level.Sources;
                    if (sources == null) return Error("磁盘关卡无法读取：" + path);
                    return new WorldSourceLoader().Load(sources).Result;
                }
                finally { foreach (var value in disk) UnityEngine.Object.DestroyImmediate(value); }
            }
            catch (Exception ex) { return Error("读取失败：" + path + "，" + ex.Message); }
        }

        private static WorldResult Error(string message) => WorldResult.Failure(WorldErrorCode.InvalidConfig,
            new WorldDiagnostic("地图编辑", "关卡资产", message));
    }

    public sealed class MapSaveProcessor : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            SceneMaterialEditorWindow.EndActiveStroke();
            MapEditingSession.FlushAll();
            EditorApplication.delayCall += SaveMaps;
            return paths;
        }

        private static void SaveMaps()
        {
            if (Application.isPlaying) return;
            foreach (OpenOitaMap map in UnityEngine.Object.FindObjectsByType<OpenOitaMap>(FindObjectsSortMode.None))
                if (map.Level != null && EditorUtility.IsDirty(map.Level))
                {
                    WorldResult result = MapEditingSession.For(map.Level).Save();
                    if (!result.IsSuccess) Debug.LogWarning(result.Diagnostic.Message, map);
                }
        }
    }
}
