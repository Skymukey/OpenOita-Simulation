using UnityEngine;

namespace OpenOita.V2
{
    public sealed unsafe partial class MaterialWorld
    {
        // 时间轮的非气体事件包含燃烧与腐蚀。仅检查到期酸格，不扫描世界或休眠液体。
        private void ProcessCorrosion()
        {
            for (int i = 0; i < _burnDue.Length; i++)
            {
                int handle = _burnDue[i];
                if (!_components.IsLive(handle)) continue;
                CellCold state = _components.Read(handle);
                MaterialDefinition definition = _definitions[state.MaterialId];
                if (!definition.IsCorrosive) continue;
                if (state.NextCorrosionTick > Grid.Tick) { Schedule(handle); continue; }

                bool active = state.GridHandle == Grid.GridHandle && Grid.Read(state.X, state.Y).ComponentHandle == handle;
                if (active && CorrodeNeighbour(state.X, state.Y))
                {
                    Grid.Write(state.X, state.Y, default);
                    continue;
                }

                // 暂存酸保留计时，不在隐藏状态腐蚀；恢复后继续从当前坐标尝试。
                state.NextCorrosionTick = Grid.Tick + definition.CorrosionInterval;
                _components.Write(handle, state);
                Schedule(handle);
            }
        }

        private bool CorrodeNeighbour(int x, int y)
        {
            // 主网格优先，下、左、右、上；每格酸只移除一个目标。
            for (int direction = 0; direction < 4; direction++)
            {
                int nx = x + (direction == 1 ? -1 : direction == 2 ? 1 : 0);
                int ny = y + (direction == 0 ? -1 : direction == 3 ? 1 : 0);
                if (!_definitions[Grid.Read(nx, ny).MaterialId].IsCorrodible) continue;
                Grid.Write(nx, ny, default);
                return true;
            }
            if (Physics != null && Physics.TryFindCorrosionTarget(x, y, out MaterialGrid target, out int tx, out int ty))
            {
                target.Write(tx, ty, default);
                return true;
            }
            return false;
        }

        private void EmitSmoke()
        {
            if (_smokeSources.Length == 0) return;
            int remaining = Config.Limits.MaxMaterialCells - TotalCells();
            for (int i = 0; i < _smokeSources.Length && remaining > 0; i++)
            {
                int handle = _smokeSources[i];
                if (!_components.IsLive(handle)) continue;
                CellCold state = _components.Read(handle);
                MaterialDefinition definition = _definitions[state.MaterialId];
                MaterialGrid source = FindGrid(state.GridHandle);
                if (source == null || !source.Read(state.X, state.Y).IsBurning || definition.SmokeMaterialId == 0) continue;
                int x = state.X, y = state.Y;
                if (source != Grid)
                {
                    if (Physics == null || !Physics.TryGetBodyByGridHandle(state.GridHandle, out BodyV2 body)) continue;
                    float c = Mathf.Cos(body.Pose.AngleRadians), s = Mathf.Sin(body.Pose.AngleRadians);
                    Vector2 local = new Vector2((x + 0.5f) * Config.CellSize, (y + 0.5f) * Config.CellSize);
                    Vector2 point = body.Pose.Position + new Vector2(c * local.x - s * local.y, s * local.x + c * local.y);
                    x = Mathf.FloorToInt((point.x - _origin.x) / Config.CellSize);
                    y = Mathf.FloorToInt((point.y - _origin.y) / Config.CellSize);
                }
                // 向世界上方排烟；上方堵塞时尝试左右，不穿过固体或覆盖液体。
                for (int direction = 0; direction < 3; direction++)
                {
                    int nx = x + (direction == 1 ? -1 : direction == 2 ? 1 : 0);
                    int ny = y + (direction == 0 ? 1 : 0);
                    if (!Grid.Passable(nx, ny)) continue;
                    Grid.Write(nx, ny, Grid.CreateCell(definition.SmokeMaterialId));
                    remaining--;
                    break;
                }
            }
            // 空间或材料容量不足时跳过本次排烟，不积压、不覆盖已有材料。
        }
    }
}
