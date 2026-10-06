using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Preview
{
    // 仅供独立轴对齐模块探针使用，不是正式 M07B 显示参与者。
    internal sealed class PixelPreviewTextures : IDisposable
    {
        internal sealed class Layer
        {
            internal readonly ulong BodyId;
            internal readonly Vector2Int Min;
            internal readonly Texture2D Texture;
            internal readonly Color32[] Pixels;
            internal Vector2 Origin;

            internal Layer(ulong bodyId, Vector2Int min, int width, int height)
            {
                BodyId = bodyId;
                Min = min;
                Pixels = new Color32[checked(width * height)];
                Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = bodyId == 0 ? "模块探针主网格像素" : "模块探针材料体像素 " + bodyId,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
        }

        private readonly Dictionary<ulong, Layer> _layers = new Dictionary<ulong, Layer>();
        private readonly Dictionary<ulong, RectInt> _bounds = new Dictionary<ulong, RectInt>();
        private readonly List<ulong> _retired = new List<ulong>();
        private readonly List<Layer> _ordered = new List<Layer>();
        private WorldVersion _version;
        private bool _hasVersion;
        private bool _disposed;
        internal IReadOnlyList<Layer> Layers => _ordered;

        internal void Refresh(ICommittedRenderView view)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PixelPreviewTextures));
            if (_hasVersion && _version.Equals(view.Version)) return;
            _bounds.Clear();
            _bounds.Add(0, new RectInt(0, 0, view.Config.Width, view.Config.Height));
            foreach (CellKey key in view.OccupiedCells)
            {
                if (key.Position.OwnerKind == OwnerKind.Grid) continue;
                ulong id = key.Position.BodyId;
                int x = key.Position.X, y = key.Position.Y;
                if (_bounds.TryGetValue(id, out RectInt bounds))
                {
                    int minX = Mathf.Min(bounds.xMin, x), minY = Mathf.Min(bounds.yMin, y);
                    _bounds[id] = new RectInt(minX, minY, Mathf.Max(bounds.xMax, x + 1) - minX,
                        Mathf.Max(bounds.yMax, y + 1) - minY);
                }
                else _bounds.Add(id, new RectInt(x, y, 1, 1));
            }
            _retired.Clear();
            foreach (var pair in _layers)
            {
                Layer layer = pair.Value;
                if (!_bounds.TryGetValue(pair.Key, out RectInt bounds) || bounds.min != layer.Min ||
                    bounds.width != layer.Texture.width || bounds.height != layer.Texture.height)
                    _retired.Add(pair.Key);
            }
            foreach (ulong id in _retired)
            {
                Release(_layers[id].Texture);
                _layers.Remove(id);
            }
            _ordered.Clear();
            foreach (var pair in _bounds)
            {
                RectInt bounds = pair.Value;
                if (!_layers.TryGetValue(pair.Key, out Layer layer))
                {
                    layer = new Layer(pair.Key, bounds.min, bounds.width, bounds.height);
                    _layers.Add(pair.Key, layer);
                }
                Array.Clear(layer.Pixels, 0, layer.Pixels.Length);
                layer.Origin = Vector2.zero;
                if (pair.Key != 0)
                    layer.Origin = ModulePreviewSession.CellOrigin(new CellKey(view.Version.Generation,
                        new CellPositionKey(OwnerKind.Body, pair.Key, bounds.x, bounds.y)), view);
                _ordered.Add(layer);
            }
            _ordered.Sort((first, second) => first.BodyId.CompareTo(second.BodyId));
            foreach (CellKey key in view.OccupiedCells)
            {
                if (!view.Read(key, out CellSnapshot cell).IsSuccess ||
                    !view.Materials.TryGet(cell.MaterialId, out MaterialRuntimeEntry material))
                    throw new InvalidOperationException("模块预览提交材料不可读取。");
                Layer layer = _layers[key.Position.BodyId];
                int x = key.Position.X - layer.Min.x, y = key.Position.Y - layer.Min.y;
                layer.Pixels[y * layer.Texture.width + x] = MaterialPixel(cell, material);
            }
            foreach (Layer layer in _ordered)
            {
                layer.Texture.SetPixels32(layer.Pixels);
                layer.Texture.Apply(false, false);
            }
            _version = view.Version;
            _hasVersion = true;
        }

        // 探针的烧损可视化：满燃料为原色，零燃料为35%亮度。
        // 亮度仅由剩余燃料派生，灭火不补燃料，也不恢复已烧损的颜色。
        internal static Color32 MaterialPixel(in CellSnapshot cell, in MaterialRuntimeEntry material)
        {
            if ((material.Rules & RuleMask.Burnable) == 0 || material.Parameters.FuelTicks == 0) return material.Color;
            float remaining = Mathf.Clamp01((float)cell.FuelTicksRemaining / material.Parameters.FuelTicks);
            float brightness = 0.35f + 0.65f * remaining;
            Color32 color = material.Color;
            return new Color32((byte)Mathf.RoundToInt(color.r * brightness), (byte)Mathf.RoundToInt(color.g * brightness),
                (byte)Mathf.RoundToInt(color.b * brightness), color.a);
        }

        private static void Release(Texture2D texture)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Layer layer in _layers.Values) Release(layer.Texture);
            _layers.Clear();
            _ordered.Clear();
            _bounds.Clear();
            _retired.Clear();
        }
    }
}
