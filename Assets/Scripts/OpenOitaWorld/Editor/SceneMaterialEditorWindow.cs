using System;
using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Host;
using UnityEditor;
using UnityEngine;

namespace OpenOita.Editor
{
    public sealed class SceneMaterialEditorWindow : EditorWindow
    {
        [SerializeField] private string _directory = "";
        [SerializeField] private string _materials, _config, _scene;
        [SerializeField] private Vector2 _origin;
        [SerializeField] private int _scale = 1;
        [SerializeField] private Vector2Int _pan;
        [SerializeField] private bool _startOnPlay;
        [SerializeField] private bool _automatic = true;
        [SerializeField] private bool _freezeBodyRotation = true;
        [SerializeField] private bool _grid;
        [SerializeField] private bool _markers;
        [SerializeField] private int _materialIndex;
        [SerializeField] private SceneEditOperation _operation;
        [SerializeField] private bool _rectangle;
        [SerializeField] private bool _mark = true;
        [SerializeField] private bool _sceneTool;
        [SerializeField] private int _brushSize = 8;
        [SerializeField] private bool _roundBrush = true;
        [SerializeField] private bool _creationExpanded = true;
        [SerializeField] private int _newWidth = 256, _newHeight = 256;
        [SerializeField] private float _newCellSize = 0.1f;
        [SerializeField] private string _sceneName = "新场景";
        private SceneEditingDocument _document;
        private SceneEditorSession _session;
        private WorldHost _referenceHost;
        [SerializeField] private OpenOitaMap _map;
        [SerializeField] private bool _legacy;
        private MapEditingSession _mapSession;
        private WorldResult _last;
        private string _feedback = "创建空白场景即可绘制，也可加载已有场景目录。保存仅保存编辑初态。";
        private Vector2Int _dragStart, _hover;
        private bool _dragging, _hasHover;
        private Vector2Int _lastStrokeCell;
        private bool _strokeHasPrevious, _strokeInScene;
        private int _strokeControl;
        private Rect _previewRect;
        private string[] _materialNames;
        private ushort[] _materialIds;
        private SceneMaterialData _paletteData;
        private Vector2 _scroll;

        [MenuItem("OpenOita/编辑初态与正式试玩")]
        public static void Open() => GetWindow<SceneMaterialEditorWindow>("OpenOita初态编辑", typeof(SceneView));

        [MenuItem("OpenOita/场景绘制工具")]
        public static void OpenPainter() => Open();

        public static void EditMap(OpenOitaMap map)
        {
            Selection.activeGameObject = map.gameObject;
            var window = GetWindow<SceneMaterialEditorWindow>("OpenOita地图编辑", typeof(SceneView));
            window._legacy = false; window.SelectionChanged(); window._sceneTool = true;
            window.Focus();
        }

        public static void EndActiveStroke()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SceneMaterialEditorWindow>()) window.CancelStroke();
        }
        public static void RefreshMapBinding()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SceneMaterialEditorWindow>())
            {
                window._session?.Dispose(); window._session = new SceneEditorSession();
                window.SelectionChanged(); window.Repaint();
            }
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            minSize = new Vector2(560, 650);
            _document = new SceneEditingDocument(); _session = new SceneEditorSession();
            if (_legacy && !string.IsNullOrEmpty(_scene))
            {
                Show(_document.Load(new WorldSources(_materials, _config, _scene)), "已恢复编辑初态");
                BuildPalette();
            }
            SceneView.duringSceneGui += DuringSceneGui;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            EditorApplication.update += EditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseResources;
            Selection.selectionChanged += SelectionChanged;
            SelectionChanged();
            if (_startOnPlay && EditorApplication.isPlaying) EditorApplication.delayCall += StartPendingTrial;
        }

        private void OnDisable()
        {
            Persist();
            SceneView.duringSceneGui -= DuringSceneGui;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            EditorApplication.update -= EditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseResources;
            Selection.selectionChanged -= SelectionChanged;
            EditorApplication.delayCall -= StartPendingTrial;
            ReleaseResources();
        }

        private void ReleaseResources() { CancelStroke(); _session?.Dispose(); _session = null; }
        private void OnLostFocus() { CancelStroke(); _hasHover = false; Repaint(); }
        private void EditorUpdate()
        {
            if (_map != null)
            {
                if (_mapSession?.Asset != _map.Level) { SelectionChanged(); return; }
                if (_mapSession != null && !_dragging && _mapSession.Refresh()) BuildPalette();
                if (_document?.Data != _paletteData) BuildPalette();
                _origin = Application.isPlaying && _map.Host.World != null ? _map.Host.CreatedOrigin : _map.Origin;
                if (Application.isPlaying && _map.Host.World != null) _session?.ObserveHost(_map.Host, _map.Host.CreatedOrigin);
            }
            if (_session?.IsTrial == true) Repaint();
        }
        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) { Persist(); ReleaseResources(); }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _session ??= new SceneEditorSession(); StartPendingTrial();
            }
            if (state == PlayModeStateChange.ExitingPlayMode) { _startOnPlay = false; ReleaseResources(); }
            if (state == PlayModeStateChange.EnteredEditMode) { _session ??= new SceneEditorSession(); Repaint(); }
        }
        private void SelectionChanged()
        {
            OpenOitaMap selectedMap = Selection.gameObjects.Length == 1 ? Selection.activeGameObject.GetComponent<OpenOitaMap>() : null;
            if (selectedMap != null || !_legacy)
            {
                CancelStroke();
                if (_mapSession?.Pending == true) { Show(_mapSession.LastResult, ""); return; }
                bool changed = _map != selectedMap || _mapSession?.Asset != selectedMap?.Level;
                _map = selectedMap;
                _referenceHost = _map != null ? _map.Host : null;
                _mapSession = _map != null ? MapEditingSession.For(_map.Level) : null;
                _document = _mapSession?.Document ?? new SceneEditingDocument();
                _legacy = false;
                _materials = _config = _scene = null;
                if (changed) { _session?.Dispose(); _session = new SceneEditorSession(); _pan = Vector2Int.zero; }
                if (_map != null)
                {
                    _origin = _map.Origin; BuildPalette();
                    if (changed && _mapSession != null) Show(_mapSession.LastResult, "已绑定地图关卡；完成笔触后请保存关卡及场景。");
                }
                _hasHover = false; Repaint(); return;
            }
            WorldHost selected = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<WorldHost>() : null;
            if (_referenceHost != selected)
            {
                _referenceHost = selected; CancelStroke();
                // 选择改变释放自己的预览和试玩；不关闭用户的Host世界。
                _session?.Dispose(); _session = new SceneEditorSession(); Repaint();
            }
        }

        private WorldResult ValidateTransform()
        {
            if (_map != null)
            {
                if (Application.isPlaying || !_map.isActiveAndEnabled) return WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("地图编辑", "状态", "Play期间或地图禁用时不能绘制。"));
                if (_map.Level != _mapSession?.Asset) return WorldResult.Failure(WorldErrorCode.Busy, new WorldDiagnostic("地图编辑", "引用", "目标资产已更换，请重新绑定。"));
                WorldResult valid = _map.ValidateTransform();
                return valid.IsSuccess ? MapEditingSession.CheckWritable(_map.Level) : valid;
            }
            if (_referenceHost != null && (_referenceHost.transform.rotation != Quaternion.identity || _referenceHost.transform.lossyScale != Vector3.one))
                return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("编辑器", "Host.transform", "选中Host必须零旋转、单位世界缩放。请修正变换后再编辑或试玩。"));
            return WorldResult.Success();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            try { DrawInterface(); }
            finally { EditorGUILayout.EndScrollView(); }
        }

        private void DrawInterface()
        {
            _session ??= new SceneEditorSession();
            bool trial = _session.IsTrial;
            if (!_legacy)
            {
                EditorGUILayout.LabelField(_map != null ? "目标：" + _map.name : "请选择一个地图对象", EditorStyles.boldLabel);
                if (_map != null && _map.Level != null)
                {
                    EditorGUILayout.ObjectField("关卡资产", _map.Level, typeof(OpenOitaMapAsset), false);
                    EditorGUILayout.HelpBox("修改共享关卡影响所有引用者。笔触写回资产内存；请保存关卡及Unity场景。", MessageType.Info);
                    EditorGUILayout.LabelField("保存状态", EditorUtility.IsDirty(_map.Level) || _mapSession.Pending ? "未保存到磁盘" : "已保存");
                }
                if (GUILayout.Button("旧三文件独立工作流（显式迁移入口）"))
                {
                    CancelStroke(); _map = null; _mapSession = null; _legacy = true;
                    _document = new SceneEditingDocument(); _session.Dispose(); _session = new SceneEditorSession();
                }
                if (_document.Data == null) { EditorGUILayout.HelpBox("Hierarchy右键 → OpenOita/地图。选中地图并点击“编辑地图”；缺失或无效资产请在Inspector修复。", MessageType.Info); return; }
            }
            EditorGUILayout.LabelField(trial ? "正式试玩（编辑工具暂停，保存仍保存初态）" : "编辑初态（不推进模拟）", EditorStyles.boldLabel);
            if (_legacy)
            {
            using (new EditorGUI.DisabledScope(trial || _startOnPlay))
            {
                _creationExpanded = EditorGUILayout.Foldout(_creationExpanded, "新建空白场景", true);
                if (_creationExpanded)
                {
                    _sceneName = EditorGUILayout.TextField("场景名称", _sceneName);
                    EditorGUILayout.BeginHorizontal();
                    _newWidth = EditorGUILayout.IntField("宽（格）", _newWidth);
                    _newHeight = EditorGUILayout.IntField("高（格）", _newHeight);
                    EditorGUILayout.EndHorizontal();
                    _newCellSize = EditorGUILayout.FloatField("每格世界尺寸", _newCellSize);
                    if (GUILayout.Button("创建空白场景并开始绘制")) CreateEmptyScene();
                }
                _directory = EditorGUILayout.TextField("配置目录", _directory);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("选择目录并加载"))
                {
                    string chosen = EditorUtility.OpenFolderPanel("选择三文件配置目录", string.IsNullOrEmpty(_directory) ? Application.streamingAssetsPath : _directory, "");
                    if (!string.IsNullOrEmpty(chosen)) LoadDirectory(chosen);
                }
                if (GUILayout.Button("选择scene.json"))
                {
                    string chosen = EditorUtility.OpenFilePanel("选择scene.json", _directory, "json");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        if (Path.GetFileName(chosen) == "scene.json") LoadDirectory(Path.GetDirectoryName(chosen));
                        else _feedback = "只允许选择固定文件名scene.json；材料和世界配置从同目录读取。";
                    }
                }
                if (GUILayout.Button("重新加载")) LoadDirectory(_directory);
                EditorGUILayout.EndHorizontal();
            }
            }
            if (_document.Data == null) { EditorGUILayout.HelpBox(_feedback, MessageType.Info); return; }
            var data = _document.Data;
            EditorGUILayout.LabelField($"逻辑尺寸 {data.Config.Width}×{data.Config.Height} ｜ cellSize={data.Config.CellSize} ｜ {data.Count}材料格");
            using (new EditorGUI.DisabledScope(trial || _startOnPlay || Application.isPlaying))
            {
                if (_map == null) _origin = EditorGUILayout.Vector2Field("世界原点", _origin);
                else EditorGUILayout.LabelField("世界原点（对象XY）", _map.Origin.ToString());
                _materialIndex = EditorGUILayout.Popup("材料", Mathf.Clamp(_materialIndex, 0, _materialNames.Length - 1), _materialNames);
                EditorGUI.BeginChangeCheck();
                _operation = (SceneEditOperation)GUILayout.Toolbar((int)_operation, new[] { "绘制", "橡皮擦", "固定", "初燃" });
                _rectangle = GUILayout.Toolbar(_rectangle ? 1 : 0, new[] { "连续画笔", "矩形工具" }) == 1;
                if (!_rectangle)
                {
                    _brushSize = EditorGUILayout.IntSlider("画笔大小（格）", _brushSize, 1, SceneBrushSelection.MaxSize);
                    _roundBrush = GUILayout.Toolbar(_roundBrush ? 0 : 1, new[] { "圆形", "方形" }) == 0;
                }
                if (EditorGUI.EndChangeCheck()) CancelStroke();
                EditorGUILayout.BeginHorizontal();
                if (_operation == SceneEditOperation.Fixed || _operation == SceneEditOperation.InitialBurning)
                    _mark = GUILayout.Toggle(_mark, "设置标记（关闭=取消）");
                _sceneTool = GUILayout.Toggle(_sceneTool, "启用Scene画笔");
                if (GUILayout.Button("定位Scene画布"))
                {
                    CancelStroke(); _sceneTool = true;
                    SceneView view = SceneView.lastActiveSceneView ?? GetWindow<SceneView>();
                    Vector2 size = new Vector2(data.Config.Width, data.Config.Height) * data.Config.CellSize;
                    view.in2DMode = true;
                    view.Frame(new Bounds(_origin + size * 0.5f, new Vector3(size.x, size.y, 0.1f)), false);
                    view.Focus(); view.Repaint();
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(_rectangle ? "拖出矩形，松开填充；选橡皮擦可矩形擦除；Esc取消。" : "按住左键连续绘制；B画笔 / E橡皮擦 / R矩形；[ ]调整大小。", EditorStyles.miniLabel);
            }
            EditorGUILayout.BeginHorizontal();
            _scale = Mathf.Clamp(EditorGUILayout.IntField("整数倍率", _scale), 1, 16);
            if (GUILayout.Button("1:1", GUILayout.Width(48))) _scale = 1;
            if (GUILayout.Button("3倍", GUILayout.Width(48))) _scale = 3;
            _grid = GUILayout.Toggle(_grid, "网格"); _markers = GUILayout.Toggle(_markers, "标记层");
            EditorGUILayout.EndHorizontal();
            _pan = EditorGUILayout.Vector2IntField("平移（逻辑格）", _pan);
            bool exitTrialRequested = false;
            using (new EditorGUI.DisabledScope(trial || _startOnPlay))
                _freezeBodyRotation = EditorGUILayout.Toggle("禁止刚体旋转", _freezeBodyRotation);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("保存初态"))
            {
                CancelStroke();
                if (_mapSession != null) Show(_mapSession.Save(), "关卡已保存；对象引用与位置请保存Unity场景。JSON尚未导出。");
                else if (string.IsNullOrEmpty(_directory)) SaveAsNewScene();
                else Show(_document.Save(_directory), "初态已原子保存；材料与世界参数未改写");
            }
            if (_legacy && GUILayout.Button("另存新场景")) SaveAsNewScene();
            if (!trial)
            {
                if (_legacy) _automatic = GUILayout.Toggle(_automatic, "自动推进");
                if (GUILayout.Button("正式试玩")) RequestTrial();
            }
            else
            {
                if (!_automatic && GUILayout.Button("推进1 Tick")) Show(_session.Host.Step().Result, "正式世界已推进");
                if (GUILayout.Button("Reset")) Show(_session.Host.ResetWorld(), "已从创建初态Reset");
                exitTrialRequested = GUILayout.Button("退出试玩");
            }
            EditorGUILayout.EndHorizontal();
            if (exitTrialRequested)
            {
                if (_map != null) { EditorApplication.isPlaying = false; GUIUtility.ExitGUI(); }
                Show(_session.EndTrial(), "已退出试玩，恢复编辑初态");
                // 关闭布局组后结束本次绘制，避免读取已释放的Host或沿用试玩布局。
                GUIUtility.ExitGUI();
            }
            if (trial)
            {
                EditorGUILayout.LabelField($"{_session.Host.World.Lifecycle} ｜ generation={_session.Host.World.Version.Generation} ｜ Tick={_session.Host.World.Version.CommittedTick}");
                MaterialCountsResult counts = _session.Host.World.QueryMaterialCounts();
                if (counts.Result.IsSuccess)
                    EditorGUILayout.LabelField($"活动 {counts.ActiveCells}（网格 {counts.GridCells} / 体内 {counts.BodyCells}）｜ 暂存水 {counts.SuspendedWaterCells} / 蒸汽 {counts.SuspendedSteamCells} ｜ 总量 {counts.TotalCells}");
            }
            EditorGUILayout.HelpBox(_feedback, _last.Status == ResultStatus.Failed ? MessageType.Warning : MessageType.Info);
            DrawPixelPreview();
        }

        private void LoadDirectory(string directory)
        {
            CancelStroke();
            WorldResult result = _document.LoadDirectory(directory);
            Show(result, "已加载严格校验的编辑初态");
            if (!result.IsSuccess) return;
            _directory = directory; _session.Dispose(); _session = new SceneEditorSession();
            _creationExpanded = false;
            BuildPalette(); Persist(); SceneView.RepaintAll();
        }

        private void CreateEmptyScene()
        {
            if (_document.Data != null && _document.Data.Count > 0 &&
                !EditorUtility.DisplayDialog("创建空白场景", "当前窗口将切换为空白场景。尚未保存的绘制不会写入磁盘，请先保存需要保留的内容。", "创建", "返回编辑")) return;
            WorldSources template;
            WorldResult result;
            if (_document.Data != null) result = _document.Data.Export(out template);
            else result = new ConfigurationFileStore().ReadSources(Path.Combine(Application.streamingAssetsPath, "OpenOitaV2"), out template);
            if (result.IsSuccess && _document.Data != null && _document.Data.Config.SchemaVersion == 1)
                result = WorldV2SourceConverter.Convert(template, out template);
            if (result.IsSuccess) result = _document.CreateEmpty(template, _newWidth, _newHeight, _newCellSize);
            Show(result, "空白场景已创建：选择材料和画笔直接绘制；首次保存会创建独立场景目录。");
            if (!result.IsSuccess) return;
            CancelStroke(); _directory = ""; _pan = Vector2Int.zero; _origin = Vector2.zero;
            _creationExpanded = false;
            _session.Dispose(); _session = new SceneEditorSession();
            BuildPalette(); Persist(); SceneView.RepaintAll();
        }

        private void SaveAsNewScene()
        {
            if (string.IsNullOrWhiteSpace(_sceneName) || _sceneName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || _sceneName == "." || _sceneName == "..")
            { _feedback = "请在“新建空白场景”中填写有效的场景名称。"; _creationExpanded = true; return; }
            string parent = EditorUtility.OpenFolderPanel("选择父目录，将在其中创建“" + _sceneName + "”场景目录", Application.dataPath, "");
            if (string.IsNullOrEmpty(parent)) return;
            string target = Path.Combine(parent, _sceneName.Trim());
            WorldResult result = _document.SaveNewDirectory(target);
            Show(result, "场景三文件已保存到：" + target);
            if (result.IsSuccess) { _directory = target; Persist(); }
        }
        private void BuildPalette()
        {
            if (_document.Data == null) return;
            var table = _document.Data.Materials;
            _paletteData = _document.Data;
            _materialNames = new string[table.Count]; _materialIds = new ushort[table.Count];
            for (int i = 0; i < table.Count; i++)
            {
                MaterialRuntimeEntry entry = table.GetByCompactIndex((ushort)(i + 1));
                _materialNames[i] = $"{entry.Id} · {entry.Name}"; _materialIds[i] = entry.Id;
            }
            _materialIndex = Mathf.Clamp(_materialIndex, 0, _materialIds.Length - 1);
        }
        private void Persist()
        {
            if (_mapSession != null) { CancelStroke(); return; }
            if (_document?.Data == null) return;
            if (_document.Data.Export(out WorldSources sources).IsSuccess)
            { _materials = sources.MaterialsText; _config = sources.WorldConfigText; _scene = sources.SceneText; }
        }
        private void Show(WorldResult result, string success)
        {
            _last = result;
            _feedback = result.IsSuccess ? success : $"{result.ErrorCode} ｜ {result.Diagnostic.FileName} ｜ {result.Diagnostic.Stage} ｜ {result.Diagnostic.Target}\n{result.Diagnostic.Message}";
            Repaint();
        }
        private void RequestTrial()
        {
            if (_map != null) { OpenOitaMapEditor.StartTrial(_map); return; }
            WorldResult valid = ValidateTransform();
            if (!valid.IsSuccess) { Show(valid, ""); return; }
            Persist(); _startOnPlay = true;
            if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
            else StartPendingTrial();
        }
        private void StartPendingTrial()
        {
            if (!_startOnPlay || !EditorApplication.isPlaying || _document?.Data == null) return;
            _startOnPlay = false; _session ??= new SceneEditorSession();
            Show(_session.BeginTrial(_document.Data, _origin, _automatic, _freezeBodyRotation),
                _freezeBodyRotation ? "已进入正式试玩：禁止刚体旋转，保留平移和碰撞" : "已进入正式试玩：允许刚体旋转");
        }

        private void DrawPixelPreview()
        {
            if (Application.isPlaying && _map != null && _map.Host.World == null)
            {
                EditorGUILayout.HelpBox(_map.Host.LastResult.Diagnostic.Message ?? "当前地图不参与运行。Play期间不显示编辑初态。", MessageType.Info);
                return;
            }
            float dpi = EditorGUIUtility.pixelsPerPoint;
            Rect rect = GUILayoutUtility.GetRect(32, 4096, 180, 4096, GUILayout.ExpandHeight(true));
            int control = GUIUtility.GetControlID(FocusType.Passive);
            int width = Mathf.Max(1, Mathf.FloorToInt(rect.width * dpi));
            int height = Mathf.Max(1, Mathf.FloorToInt(rect.height * dpi));
            rect.width = width / dpi; rect.height = height / dpi;
            if (Event.current.type == EventType.Repaint)
            {
                _previewRect = rect;
                WorldResult result = _session.Render(_document.Data, _origin, width, height, _scale, _pan);
                if (!result.IsSuccess) { Show(result, ""); return; }
                GUI.DrawTexture(rect, _session.Output, ScaleMode.StretchToFill, false);
                if (!_session.IsTrial) DrawPreviewOverlays(rect, dpi);
            }
            // 上左GUI坐标转换到X右Y上的真实像素；倍率只作用显示。
            if (rect.Contains(Event.current.mousePosition))
            {
                PixelPreviewCoordinates.TryPoint(rect, Event.current.mousePosition, dpi, height, _scale, _pan, out _hover);
                _hasHover = IsInside(_hover);
                if (_session.IsTrial)
                {
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && _hasHover)
                    {
                        var query = _session.Host.World.QueryPoint(GridSelection.Center(_document.Data.Config, _origin, _hover));
                        Show(query.Result, query.HasHit ? $"同版本查询 {query.Version.Generation}/{query.Version.CommittedTick} ｜ {query.Hit.MaterialId} ｜ {query.Hit.Position.OwnerKind} ｜ BodyId={query.Hit.Position.BodyId}" : "查询成功：空格");
                        Event.current.Use();
                    }
                }
                else HandleStroke(Event.current, control, false);
            }
            else
            {
                _hasHover = false;
                if (_dragging && !_strokeInScene) HandleStroke(Event.current, control, false);
            }
            if (Event.current.type == EventType.MouseMove || Event.current.type == EventType.MouseDrag) Repaint();
        }
        private bool IsInside(Vector2Int cell) => cell.x >= 0 && cell.y >= 0 && cell.x < _document.Data.Config.Width && cell.y < _document.Data.Config.Height;
        private Vector2Int[] CurrentSelection()
        {
            var config = _document.Data.Config;
            if (_rectangle) return SceneBrushSelection.Rectangle(_dragging ? _dragStart : _hover, _hover, config.Width, config.Height);
            return SceneBrushSelection.Stamp(_hover, _brushSize, _roundBrush, config.Width, config.Height);
        }
        private void CancelStroke()
        {
            if (_mapSession?.Pending == true) Show(_mapSession.Commit(), "笔触已写回关卡内存；请保存磁盘。");
            if (_strokeControl != 0 && GUIUtility.hotControl == _strokeControl) GUIUtility.hotControl = 0;
            _strokeControl = 0; _dragging = false; _strokeHasPrevious = false;
        }
        private void HandleStroke(Event evt, int control, bool inScene)
        {
            if (Application.isPlaying || _startOnPlay || _session?.IsTrial == true || (_dragging && inScene != _strokeInScene)) return;
            if (evt.type == EventType.KeyDown && !EditorGUIUtility.editingTextField)
            {
                if ((evt.control || evt.command) && (evt.keyCode == KeyCode.Z || evt.keyCode == KeyCode.Y)) { CancelStroke(); return; }
                if (evt.keyCode == KeyCode.Escape) { CancelStroke(); evt.Use(); Repaint(); return; }
                if (_hasHover && !_dragging && !evt.control && !evt.command && !evt.alt)
                {
                    switch (evt.keyCode)
                    {
                        case KeyCode.B: _operation = SceneEditOperation.Paint; _rectangle = false; break;
                        case KeyCode.E: _operation = SceneEditOperation.Erase; _rectangle = false; break;
                        case KeyCode.R: _rectangle = true; break;
                        case KeyCode.LeftBracket: _brushSize = Mathf.Max(1, _brushSize - 1); break;
                        case KeyCode.RightBracket: _brushSize = Mathf.Min(SceneBrushSelection.MaxSize, _brushSize + 1); break;
                        default: return;
                    }
                    evt.Use(); Repaint(); return;
                }
            }
            if (_dragging && evt.rawType == EventType.MouseUp && evt.button == 0)
            {
                if (_hasHover && !evt.alt && (_rectangle || _hover != _lastStrokeCell)) ApplyStroke();
                CancelStroke(); evt.Use(); Repaint(); return;
            }
            if (!_hasHover || evt.alt)
            {
                // Layout/Repaint可能没有鼠标坐标，不能因此截断正在绘制的连续路径。
                if (evt.type == EventType.MouseMove || evt.type == EventType.MouseDrag) _strokeHasPrevious = false;
                return;
            }
            if (evt.button != 0) return;
            if (evt.type == EventType.MouseDown)
            {
                _dragStart = _hover; _lastStrokeCell = _hover; _dragging = true;
                _strokeInScene = inScene; _strokeControl = control; GUIUtility.hotControl = control;
                if (!_rectangle) ApplyStroke(); evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && _dragging)
            { if (!_rectangle && (!_strokeHasPrevious || _lastStrokeCell != _hover)) ApplyStroke(); evt.Use(); }
            if (evt.type == EventType.Used) Repaint();
        }
        private void ApplyStroke()
        {
            WorldResult valid = ValidateTransform();
            if (!valid.IsSuccess) { Show(valid, ""); return; }
            Vector2Int[] selection = CurrentSelection();
            if (!_rectangle && _strokeHasPrevious)
                selection = SceneBrushSelection.Stroke(_lastStrokeCell, _hover, _brushSize, _roundBrush, _document.Data.Config.Width, _document.Data.Config.Height);
            Show(_document.Data.Apply(selection, _operation, _materialIds[_materialIndex], _mark), $"已编辑 {selection.Length} 个逻辑像素");
            _lastStrokeCell = _hover; _strokeHasPrevious = true;
            SceneView.RepaintAll();
        }
        private void DrawPreviewOverlays(Rect rect, float dpi)
        {
            GUI.BeginClip(rect);
            float step = _scale / dpi;
            Rect canvas = new Rect(-_pan.x * step, rect.height - (_document.Data.Config.Height - _pan.y) * step,
                _document.Data.Config.Width * step, _document.Data.Config.Height * step);
            EditorGUI.DrawRect(new Rect(canvas.x, canvas.y, canvas.width, 1 / dpi), Color.gray);
            EditorGUI.DrawRect(new Rect(canvas.x, canvas.yMax, canvas.width, 1 / dpi), Color.gray);
            EditorGUI.DrawRect(new Rect(canvas.x, canvas.y, 1 / dpi, canvas.height), Color.gray);
            EditorGUI.DrawRect(new Rect(canvas.xMax, canvas.y, 1 / dpi, canvas.height), Color.gray);
            if (_grid && _scale >= 3)
            {
                for (float x = 0; x < rect.width; x += step) EditorGUI.DrawRect(new Rect(x, 0, 1 / dpi, rect.height), new Color(1, 1, 1, 0.15f));
                for (float y = 0; y < rect.height; y += step) EditorGUI.DrawRect(new Rect(0, y, rect.width, 1 / dpi), new Color(1, 1, 1, 0.15f));
            }
            if (_markers)
            {
                var snapshot = _document.Data.Snapshot();
                foreach (Vector2Int cell in snapshot.FixedCells) DrawMarker(cell, Color.cyan);
                foreach (Vector2Int cell in snapshot.InitialBurning) DrawMarker(cell, Color.yellow);
            }
            if (_hasHover && !_session.IsTrial)
            {
                foreach (Vector2Int cell in CurrentSelection()) DrawMarker(cell, _operation == SceneEditOperation.Erase ? new Color(1, 0.25f, 0.25f, 0.5f) : new Color(1, 1, 1, 0.4f));
                if (_rectangle && _dragging)
                    GUI.Label(new Rect(8, 8, 250, 22), $"矩形 {Mathf.Abs(_hover.x - _dragStart.x) + 1} × {Mathf.Abs(_hover.y - _dragStart.y) + 1} 格");
            }
            GUI.EndClip();
            void DrawMarker(Vector2Int cell, Color color) => EditorGUI.DrawRect(new Rect((cell.x - _pan.x) * step,
                rect.height - (cell.y - _pan.y + 1) * step, step, step), color);
        }

        private void DuringSceneGui(SceneView view)
        {
            if (!_sceneTool || _document?.Data == null || Application.isPlaying || _session?.IsTrial == true || _startOnPlay || (!_legacy && _map == null)) return;
            if (_map != null) _origin = _map.Origin;
            Event evt = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive);
            if (evt.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            var plane = new Plane(Vector3.forward, Vector3.zero);
            _hasHover = plane.Raycast(ray, out float distance) &&
                GridSelection.Point(_document.Data.Config, _origin, ray.GetPoint(distance), out _hover).IsSuccess;
            if (evt.type == EventType.Repaint)
            {
                Vector3 min = _origin;
                float width = _document.Data.Config.Width * _document.Data.Config.CellSize;
                float height = _document.Data.Config.Height * _document.Data.Config.CellSize;
                Handles.DrawSolidRectangleWithOutline(new[] { min, min + Vector3.right * width,
                    min + new Vector3(width, height), min + Vector3.up * height }, Color.clear, Color.gray);
                var snapshot = _document.Data.Snapshot();
                if (_legacy) foreach (InitialCell cell in snapshot.Cells)
                {
                    _document.Data.Materials.TryGet(cell.MaterialId, out var material);
                    DrawCell(cell.Position, material.Color);
                }
                foreach (Vector2Int cell in snapshot.FixedCells) DrawSymbol(cell, "固", Color.cyan);
                foreach (Vector2Int cell in snapshot.InitialBurning) DrawSymbol(cell, "燃", Color.yellow);
                if (_hasHover)
                {
                    var selection = CurrentSelection();
                    foreach (Vector2Int cell in selection) DrawCell(cell, _operation == SceneEditOperation.Erase ? new Color(1, 0.25f, 0.25f, 0.5f) : new Color(1, 1, 1, 0.4f));
                    Handles.Label(GridSelection.Center(_document.Data.Config, _origin, _hover), $"{selection.Length}逻辑像素；Scene自由视角");
                }
            }
            HandleStroke(evt, control, true);
            if (evt.type == EventType.MouseMove || evt.type == EventType.MouseDrag || evt.type == EventType.Used) view.Repaint();
        }
        private void DrawCell(Vector2Int cell, Color color)
        {
            float size = _document.Data.Config.CellSize;
            Vector3 min = _origin + (Vector2)cell * size;
            Handles.DrawSolidRectangleWithOutline(new[] { min, min + Vector3.right * size, min + new Vector3(size, size), min + Vector3.up * size }, color, _grid ? Color.gray : Color.clear);
        }
        private void DrawSymbol(Vector2Int cell, string label, Color color)
        {
            Handles.color = color; Handles.Label(GridSelection.Center(_document.Data.Config, _origin, cell), label); Handles.color = Color.white;
        }
    }
}
