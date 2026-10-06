using System;
using System.IO;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEngine;

namespace OpenOita.Preview
{
    [DisallowMultipleComponent]
    public sealed class M05PreviewController : MonoBehaviour
    {
        private static readonly string[] Names = { "水与蒸汽", "燃烧断桥", "十字拆分" };
        private static readonly Color Background = new Color(0.035f, 0.055f, 0.085f);
        private static readonly Color Panel = new Color(0.065f, 0.09f, 0.13f);
        private static readonly Color Muted = new Color(0.57f, 0.65f, 0.74f);
        private static readonly Color Accent = new Color(0.29f, 0.89f, 0.72f);
        private static readonly Color[] BodyColors = { new Color(0.42f, 0.85f, 1), new Color(0.89f, 0.59f, 1),
            new Color(1, 0.8f, 0.36f), new Color(0.38f, 0.96f, 0.68f) };
        private ModulePreviewSession _session;
        private WorldSources _sources;
        private ulong _generation;
        private float _elapsed;
        private int _tool;
        private bool _showGeometry;
        private PixelPreviewTextures _textures;
        private Vector2Int _pan;
        private Vector2 _panelScroll;
        private Vector2 _dragPosition;
        private bool _panning;
        private string _error;
        private Font _font;
        private GUIStyle _text;
        private GUIStyle _small;
        private GUIStyle _button;
        public bool IsPaused { get; private set; } = true;
        public int ActiveScenario { get; private set; }
        public float TicksPerSecond { get; set; } = 10;
        public ulong CurrentTick => _session?.View.Version.CommittedTick ?? 0;
        public int BodyCount => _session == null ? 0 : _session.View.Bodies.Length;
        public int CellCount => _session == null ? 0 : _session.View.OccupiedCells.Length;
        public string LastError => _error ?? _session?.Error;
        public int DisplayScale { get; private set; } = 1;
        public bool ShowGrid { get; set; }
        public bool ShowEffects { get; set; } = true;
        public bool ShowHover { get; set; } = true;
        public bool ShowGeometry { get => _showGeometry; set => _showGeometry = value; }
        public Vector2Int LogicalSize => new Vector2Int(_session?.View.Config.Width ?? ModulePreviewSession.Width,
            _session?.View.Config.Height ?? ModulePreviewSession.Height);
        public Rect ViewportBounds => CreateViewport().Bounds;
        public Rect LogicalWorldRect => CreateViewport().CellRect(0, 0, LogicalSize.x, LogicalSize.y);
        public Vector2Int DisplayPan => CreateViewport().Pan;

        public void SetDisplayScale(int scale)
        {
            if (scale < 1 || scale > 8) throw new ArgumentOutOfRangeException(nameof(scale));
            DisplayScale = scale;
            _pan = CreateViewport().Pan;
        }

        public void SetDisplayPan(Vector2Int outputPixels)
        {
            _pan = outputPixels;
            _pan = CreateViewport().Pan;
        }

        private PixelPreviewViewport CreateViewport()
        {
            bool sidePanel = Screen.width >= 800;
            float width = Mathf.Max(1, Mathf.Min(768, Screen.width - (sidePanel ? 504 : 32)));
            float height = Mathf.Max(1, Mathf.Min(768, sidePanel ? Screen.height - 32 : (Screen.height - 48) / 2));
            return new PixelPreviewViewport(new Rect(16, 16, width, height), LogicalSize.x, LogicalSize.y, DisplayScale, _pan);
        }

        private void OnEnable()
        {
            if (Application.isPlaying) SelectScenario(0);
        }

        public void SelectScenario(int scenario)
        {
            if (scenario < 0 || scenario > 2) throw new ArgumentOutOfRangeException(nameof(scenario));
            IsPaused = true;
            _elapsed = 0;
            _tool = 0;
            _error = null;
            _textures?.Dispose();
            _textures = new PixelPreviewTextures();
            _pan = Vector2Int.zero;
            _panning = false;
            _session?.Dispose();
            _session = null;
            ActiveScenario = scenario;
            try
            {
                if (_sources == null)
                {
                    WorldResult read = new ConfigurationFileStore().ReadSources(
                        Path.Combine(Application.streamingAssetsPath, "OpenOita"), out _sources);
                    if (!read.IsSuccess) throw new InvalidOperationException(read.Diagnostic.Message);
                }
                _session = new ModulePreviewSession(_sources, scenario, checked(++_generation));
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                Debug.LogError("模块预览初始化失败：" + _error, this);
            }
        }

        public void SetPaused(bool paused) { IsPaused = paused; _elapsed = 0; }
        public void ResetPreview() => SelectScenario(ActiveScenario);
        public void StepOnce()
        {
            SetPaused(true);
            Advance();
        }
        public void BreakConnection()
        {
            SetPaused(true);
            if (_session != null && ActiveScenario != 0)
                _session.RemoveRegion(_session.ActionCell, ModulePreviewSession.LayoutScale, ModulePreviewSession.LayoutScale);
        }

        private void Update()
        {
            if (IsPaused || _session == null || _session.Faulted) return;
            float interval = 1 / Mathf.Clamp(TicksPerSecond, 1, 15);
            // 大场景的预览只按处理能力播放；最多一帧一步，不积压墙钟时间追帧。
            // 只舍弃多余等待时间，规则仍逐步完整执行，正式固定步驱动不受影响。
            _elapsed = Mathf.Min(_elapsed + Time.unscaledDeltaTime, interval);
            if (_elapsed >= interval)
            {
                _elapsed -= interval;
                Advance();
            }
        }

        private bool Advance()
        {
            if (_session == null || !_session.Step())
            {
                IsPaused = true;
                return false;
            }
            return true;
        }

        private void OnGUI()
        {
            if (!Application.isPlaying) return;
            InitializeStyles();
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            bool oldEnabled = GUI.enabled;
            // 运行时 IMGUI 使用游戏输出坐标；不以 UI 布局或系统 DPI 比率缩放材料。
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;
            try
            {
                Fill(new Rect(0, 0, Screen.width, Screen.height), Background);
                DrawPanel();
                DrawWorld();
            }
            finally { GUI.matrix = oldMatrix; GUI.color = oldColor; GUI.enabled = oldEnabled; }
        }

        private void DrawPanel()
        {
            PixelPreviewViewport viewport = CreateViewport();
            Rect panel = Screen.width >= 800
                ? new Rect(viewport.Bounds.xMax + 16, 16, Mathf.Max(1, Screen.width - viewport.Bounds.xMax - 32), Mathf.Max(1, Screen.height - 32))
                : new Rect(16, viewport.Bounds.yMax + 16, Mathf.Max(1, Screen.width - 32), Mathf.Max(1, Screen.height - viewport.Bounds.yMax - 32));
            Fill(panel, Panel);
            CellKey hovered = default;
            bool hasHover = ShowHover && _session != null && viewport.TryHit(_session.View, Event.current.mousePosition, out hovered);
            _panelScroll = GUI.BeginScrollView(panel, _panelScroll, new Rect(0, 0, 456, 930));
            try
            {
                for (int i = 0; i < Names.Length; i++)
                    if (Button(new Rect(24 + i * 138, 24, 132, 38), Names[i], ActiveScenario == i)) SelectScenario(i);
                Label(new Rect(24, 78, 408, 28), "独立模块探针 · 自有世界配置", _text, Accent);
                Label(new Rect(24, 110, 408, 94),
                    $"逻辑尺寸 {LogicalSize.x}×{LogicalSize.y} · cellSize 0.1\n模板标记展开8×8，生成64份独立状态\n展开是初态生成，与显示倍率无关\n仅轴对齐材料体；未接正式物理/编辑器", _small, Muted);
                Label(new Rect(24, 210, 408, 30),
                    $"显示 {DisplayScale}:1 · 输出 {Screen.width}×{Screen.height} · 状态 {CellCount}", _small, Color.white);
                for (int i = 1; i <= 3; i++)
                    if (Button(new Rect(24 + (i - 1) * 138, 244, 132, 34), i == 1 ? "1:1 原始像素" : i + "倍显示", DisplayScale == i))
                        SetDisplayScale(i);
                Label(new Rect(24, 284, 408, 28), "超出视口裁剪 · 中键拖动平移（输出像素）", _small, Muted);
                GUI.enabled = _session != null && !_session.Faulted;
                if (Button(new Rect(24, 322, 198, 42), IsPaused ? "播放" : "暂停", true)) SetPaused(!IsPaused);
                if (Button(new Rect(234, 322, 198, 42), "单步 +1")) StepOnce();
                GUI.enabled = true;
                if (Button(new Rect(24, 380, 198, 38), "重置初态")) ResetPreview();
                if (Button(new Rect(234, 380, 198, 38), "平移归零")) SetDisplayPan(Vector2Int.zero);
                Label(new Rect(24, 432, 408, 25), $"Tick {CurrentTick} · 材料体 {BodyCount} · 播放速度", _small, Muted);
                int[] speeds = { 5, 10, 15 };
                for (int i = 0; i < speeds.Length; i++)
                    if (Button(new Rect(24 + 138 * i, 462, 132, 32), speeds[i] + " 步/秒", TicksPerSecond == speeds[i]))
                        TicksPerSecond = speeds[i];
                GUI.enabled = _session != null && !_session.Faulted && ActiveScenario != 0;
                if (Button(new Rect(24, 512, 408, 42), ActiveScenario == 2 ? "移除中心8×8区域" : "切断桥中部8×8区域")) BreakConnection();
                if (Button(new Rect(24, 566, 132, 34), "观察", _tool == 0)) _tool = 0;
                if (Button(new Rect(162, 566, 132, 34), "删除1像素", _tool == 1)) _tool = 1;
                if (Button(new Rect(300, 566, 132, 34), "点燃1像素", _tool == 2)) _tool = 2;
                GUI.enabled = true;
                if (Button(new Rect(24, 620, 198, 34), ShowGeometry ? "几何框：开" : "几何框：关", ShowGeometry)) ShowGeometry = !ShowGeometry;
                if (Button(new Rect(234, 620, 198, 34), ShowGrid ? "网格叠加：开" : "网格叠加：关", ShowGrid)) ShowGrid = !ShowGrid;
                if (Button(new Rect(24, 670, 198, 34), ShowEffects ? "火焰/锚点：开" : "火焰/锚点：关", ShowEffects)) ShowEffects = !ShowEffects;
                if (Button(new Rect(234, 670, 198, 34), ShowHover ? "悬停提示：开" : "悬停提示：关", ShowHover)) ShowHover = !ShowHover;
                Label(new Rect(24, 716, 408, 36), "验证纯材料像素时关闭全部叠加及悬停提示", _small, Muted);
                if (hasHover && _session != null && hovered.Generation == _session.View.Version.Generation)
                {
                    _session.View.Read(hovered, out CellSnapshot cell);
                    _session.View.Materials.TryGet(cell.MaterialId, out MaterialRuntimeEntry material);
                    string owner = hovered.Position.OwnerKind == OwnerKind.Grid ? "主网格" : "材料体 #" + hovered.Position.BodyId;
                    Label(new Rect(24, 756, 408, 72),
                        $"{material.Name} · {owner} · ({hovered.Position.X}, {hovered.Position.Y})\n燃料 {cell.FuelTicksRemaining} · 寿命 {cell.LifetimeTicksRemaining}\n{(cell.IsBurning ? "燃烧中" : "未燃烧")}", _small, Color.white);
                }
                if (LastError != null)
                    Label(new Rect(24, 832, 408, 84), "请重置：" + LastError, _small, new Color(1, 0.45f, 0.35f));
            }
            finally { GUI.EndScrollView(); }
        }

        private void DrawWorld()
        {
            PixelPreviewViewport viewport = CreateViewport();
            HandlePan(viewport.Bounds);
            viewport = CreateViewport();
            _pan = viewport.Pan;
            Fill(viewport.Bounds, new Color(0.045f, 0.07f, 0.10f));
            if (_session == null) return;
            ICommittedRenderView view = _session.View;
            _textures.Refresh(view);
            bool hit = viewport.TryHit(view, Event.current.mousePosition, out CellKey hovered);
            bool edit = hit && Event.current.type == EventType.MouseDown && Event.current.button == 0 && _tool != 0 && !_panning;
            var local = new PixelPreviewViewport(new Rect(0, 0, viewport.Bounds.width, viewport.Bounds.height),
                viewport.Width, viewport.Height, viewport.Scale, viewport.Pan);
            GUI.BeginGroup(viewport.Bounds);
            try
            {
                Fill(local.CellRect(0, 0, local.Width, local.Height), new Color(0.045f, 0.07f, 0.10f));
                foreach (PixelPreviewTextures.Layer layer in _textures.Layers)
                    GUI.DrawTexture(local.CellRect(layer.Origin.x, layer.Origin.y, layer.Texture.width, layer.Texture.height),
                        layer.Texture, ScaleMode.StretchToFill, true);
                if (ShowEffects)
                    foreach (CellKey key in view.OccupiedCells)
                    {
                        view.Read(key, out CellSnapshot cell);
                        Vector2 point = ModulePreviewSession.CellOrigin(key, view);
                        Rect rect = local.CellRect(point.x, point.y);
                        if (cell.IsBurning)
                        {
                            float pulse = IsPaused ? 0.6f : 0.6f + 0.2f * Mathf.Sin(Time.unscaledTime * 8 + point.x);
                            Fill(rect, new Color(1, pulse, 0.13f));
                        }
                        Vector2Int anchor = _session.AnchorCell;
                        if (key.Position.OwnerKind == OwnerKind.Grid && key.Position.X == anchor.x && key.Position.Y == anchor.y)
                            Fill(rect, new Color(1, 0.85f, 0.3f));
                    }
                if (ShowGrid)
                {
                    Rect world = local.CellRect(0, 0, local.Width, local.Height);
                    for (int x = 0; x <= local.Width; x++)
                        Fill(new Rect(world.x + x * local.Scale, world.y, 1, world.height), Muted);
                    for (int y = 0; y <= local.Height; y++)
                        Fill(new Rect(world.x, world.y + y * local.Scale, world.width, 1), Muted);
                }
                if (ShowGeometry)
                    foreach (BodyGeometryPlan body in _session.Geometry)
                    {
                        Vector2 origin = (body.Pose.Position - view.Origin) / view.Config.CellSize;
                        foreach (CellRectangle rectangle in body.Rectangles)
                            Outline(local.CellRect(origin.x + rectangle.Min.x, origin.y + rectangle.Min.y,
                                rectangle.MaxExclusive.x - rectangle.Min.x, rectangle.MaxExclusive.y - rectangle.Min.y),
                                body.Owner.OwnerKind == OwnerKind.Grid ? Muted : BodyColor(body.Owner.BodyId), 1);
                    }
                if (ShowHover && hit)
                {
                    Vector2 origin = ModulePreviewSession.CellOrigin(hovered, view);
                    Outline(local.CellRect(origin.x, origin.y), Color.white, 1);
                }
            }
            finally { GUI.EndGroup(); }
            if (edit)
            {
                Event.current.Use();
                SetPaused(true);
                _session.Edit(hovered, _tool == 2);
            }
        }

        private void HandlePan(Rect bounds)
        {
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 2 && bounds.Contains(current.mousePosition))
            {
                _panning = true;
                _dragPosition = current.mousePosition;
                current.Use();
            }
            else if (_panning && current.type == EventType.MouseDrag && current.button == 2)
            {
                Vector2Int delta = Vector2Int.RoundToInt(current.mousePosition - _dragPosition);
                SetDisplayPan(_pan - delta);
                _dragPosition += (Vector2)delta;
                current.Use();
            }
            else if (_panning && current.type == EventType.MouseUp && current.button == 2)
            {
                _panning = false;
                current.Use();
            }
        }
        private static Color BodyColor(ulong id) => BodyColors[(int)((id - 1) % (ulong)BodyColors.Length)];
        private static void Fill(Rect rect, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
        private static void Outline(Rect rect, Color color, float width)
        {
            width = Mathf.Min(width, Mathf.Min(rect.width, rect.height) / 4);
            Fill(new Rect(rect.x, rect.y, rect.width, width), color);
            Fill(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            Fill(new Rect(rect.x, rect.y, width, rect.height), color);
            Fill(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
        }
        private void Label(Rect rect, string text, GUIStyle style, Color color)
        {
            GUI.color = color;
            GUI.Label(rect, text, style);
            GUI.color = Color.white;
        }
        private bool Button(Rect rect, string text, bool selected = false)
        {
            Fill(rect, selected ? new Color(0.11f, 0.34f, 0.31f) : new Color(0.11f, 0.15f, 0.21f));
            if (selected) Outline(rect, Accent, 1);
            return GUI.Button(rect, text, _button);
        }
        private void InitializeStyles()
        {
            if (_text != null) return;
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 18);
            _text = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 18, wordWrap = true };
            _small = new GUIStyle(_text) { fontSize = 15 };
            _button = new GUIStyle(_text) { alignment = TextAnchor.MiddleCenter, fontSize = 16, wordWrap = false };
        }
        private void OnDisable()
        {
            _textures?.Dispose();
            _textures = null;
            _session?.Dispose();
            _session = null;
            if (_font != null)
            {
                if (Application.isPlaying) Destroy(_font);
                else DestroyImmediate(_font);
            }
            _font = null;
            _text = _small = _button = null;
        }
    }
}

