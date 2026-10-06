using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;

namespace OpenOita.Structure
{
    // 全量只读计划；不写材料、不分配 BodyId、不触碰几何或物理。
    // 搜索工作区在构造时预留；公共计划依 M00 合同复制成员，由调用者持有。
    public sealed class ConnectivityAnalyzer : IStructurePlanner
    {
        private sealed class KeyComparer : IComparer<CellKey>
        {
            internal static readonly KeyComparer Instance = new KeyComparer();
            public int Compare(CellKey first, CellKey second) => first.Position.CompareTo(second.Position);
        }

        // 私有工作区索引，不是另一份公共分量 DTO。
        private readonly struct ComponentRange
        {
            internal readonly int SeedIndex;
            internal readonly int MemberStart;
            internal readonly int MemberCount;
            internal readonly int BodyIndex;
            internal readonly bool IsFixed;

            internal ComponentRange(int seedIndex, int memberStart, int memberCount, int bodyIndex, bool isFixed)
            {
                SeedIndex = seedIndex;
                MemberStart = memberStart;
                MemberCount = memberCount;
                BodyIndex = bodyIndex;
                IsFixed = isFixed;
            }
        }

        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly CellKey[] _keys;
        private readonly CellKey[] _members;
        private readonly string[] _groups;
        private readonly bool[] _fixed;
        private readonly bool[] _visited;
        private readonly int[] _queue;
        private readonly ulong[] _bodyIds;
        private readonly int[] _bodyComponentCounts;
        private readonly ulong[] _retiredBodyIds;
        private readonly ComponentRange[] _ranges;
        private readonly StructureComponent[] _components;
        private bool _planning;

        public int CellCapacity => _keys.Length;
        public int BodyCapacity => _bodyIds.Length;

        public ConnectivityAnalyzer(int cellCapacity, int bodyCapacity)
        {
            if (cellCapacity < 1) throw new ArgumentOutOfRangeException(nameof(cellCapacity));
            if (bodyCapacity < 0) throw new ArgumentOutOfRangeException(nameof(bodyCapacity));
            _keys = new CellKey[cellCapacity];
            _members = new CellKey[cellCapacity];
            _groups = new string[cellCapacity];
            _fixed = new bool[cellCapacity];
            _visited = new bool[cellCapacity];
            _queue = new int[cellCapacity];
            _components = new StructureComponent[cellCapacity];
            _ranges = new ComponentRange[cellCapacity];
            _bodyIds = new ulong[bodyCapacity];
            _bodyComponentCounts = new int[bodyCapacity];
            _retiredBodyIds = new ulong[bodyCapacity];
        }

        public StructurePlanResult Plan(IWorkingWorldView world, IMaterialRuntimeTable materials,
            ReadOnlySpan<CellKey> changedCells)
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
                return Failed(Error(WorldErrorCode.InvalidArgument, "thread", "分析必须在所属线程执行。"));
            if (_planning)
                return Failed(Error(WorldErrorCode.Busy, "planner", "不能重入结构分析。"));
            if (world == null || materials == null)
                return Failed(Error(WorldErrorCode.InvalidArgument, "candidate", "缺少有效候选视图、材料表或代次。"));

            _planning = true;
            int cellCount = 0;
            int componentCount = 0;
            try
            {
                if (world.Config == null || world.Generation == 0)
                    return Failed(Error(WorldErrorCode.InvalidArgument, "candidate", "候选视图缺少配置或有效代次。"));
                ReadOnlySpan<CellKey> occupied = world.OccupiedCells;
                cellCount = occupied.Length;
                if (cellCount > CellCapacity)
                    return Failed(Error(WorldErrorCode.CapacityExceeded, "cellCapacity", "结构扫描工作缓冲不足。"));
                if (cellCount > world.Config.Limits.MaxMaterialCells)
                    return Failed(Error(WorldErrorCode.CapacityExceeded, "maxMaterialCells", "候选材料格总量超限。"));

                ReadOnlySpan<BodySnapshot> bodies = world.Bodies;
                if (bodies.Length > BodyCapacity)
                    return Failed(Error(WorldErrorCode.CapacityExceeded, "bodyCapacity", "原体目录工作缓冲不足。"));
                Array.Clear(_bodyComponentCounts, 0, bodies.Length);
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (bodies[i].BodyId == 0)
                        return Failed(Error(WorldErrorCode.InvalidArgument, "Bodies", "原动态体 ID 不能为0。"));
                    _bodyIds[i] = bodies[i].BodyId;
                }
                Array.Sort(_bodyIds, 0, bodies.Length);
                for (int i = 1; i < bodies.Length; i++)
                    if (_bodyIds[i] == _bodyIds[i - 1])
                        return Failed(Error(WorldErrorCode.InvalidArgument, "Bodies", "原动态体目录包含重复 ID。"));

                // changedCells 仅作输入引用校验。选择全量分析，不按局部修改集合截断输出。
                foreach (CellKey key in changedCells)
                {
                    WorldResult valid = ValidateKey(world, key, bodies.Length);
                    if (!valid.IsSuccess) return Failed(valid);
                }

                occupied.CopyTo(_keys);
                Array.Sort(_keys, 0, cellCount, KeyComparer.Instance);
                Array.Clear(_visited, 0, cellCount);
                for (int i = 0; i < cellCount; i++)
                {
                    CellKey key = _keys[i];
                    WorldResult valid = ValidateKey(world, key, bodies.Length);
                    if (!valid.IsSuccess) return Failed(valid);
                    if (i > 0 && key.Equals(_keys[i - 1]))
                        return Failed(Error(WorldErrorCode.InvalidArgument, key, "候选占据集合包含重复格身份。"));
                    WorldResult read = world.Read(key, out CellSnapshot state);
                    if (!read.IsSuccess)
                        return Failed(Error(read.ErrorCode, key, read.Diagnostic.Message));
                    if (state.MaterialId == 0)
                        return Failed(Error(WorldErrorCode.InvalidArgument, key, "占据集合不能引用空格。"));
                    if (!materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material))
                        return Failed(Error(WorldErrorCode.UnknownMaterial, key, "候选材料未注册。"));
                    bool structure = (material.Rules & RuleMask.Structure) != 0;
                    if (structure && string.IsNullOrEmpty(material.Parameters.ConnectionGroup))
                        return Failed(Error(WorldErrorCode.IncompatibleRule, key, "结构材料缺少有效 connectionGroup。"));
                    if (key.Position.OwnerKind == OwnerKind.Body && !structure)
                        return Failed(Error(WorldErrorCode.UnsupportedOperation, key, "动态材料体只能包含结构格。"));
                    WorldResult fixedResult = FixedCellPolicy.Read(world, key, structure, out _fixed[i]);
                    if (!fixedResult.IsSuccess) return Failed(fixedResult);
                    _groups[i] = structure ? material.Parameters.ConnectionGroup : null;
                    _visited[i] = !structure;
                }

                int totalMembers = 0;
                for (int start = 0; start < cellCount; start++)
                {
                    if (_visited[start]) continue;
                    int head = 0;
                    int tail = 1;
                    int memberStart = totalMembers;
                    bool isFixed = false;
                    _queue[0] = start;
                    _visited[start] = true;
                    while (head < tail)
                    {
                        int index = _queue[head++];
                        CellKey key = _keys[index];
                        _members[totalMembers++] = key;
                        isFixed |= _fixed[index];
                        // long 中间值防止极限体局部坐标跨 int 溢出而误连。
                        Visit(key, (long)key.Position.X - 1, key.Position.Y, _groups[start], cellCount, ref tail);
                        Visit(key, (long)key.Position.X + 1, key.Position.Y, _groups[start], cellCount, ref tail);
                        Visit(key, key.Position.X, (long)key.Position.Y - 1, _groups[start], cellCount, ref tail);
                        Visit(key, key.Position.X, (long)key.Position.Y + 1, _groups[start], cellCount, ref tail);
                    }
                    int memberCount = totalMembers - memberStart;
                    Array.Sort(_members, memberStart, memberCount, KeyComparer.Instance);
                    int bodyIndex = _keys[start].Position.OwnerKind == OwnerKind.Body
                        ? Array.BinarySearch(_bodyIds, 0, bodies.Length, _keys[start].Position.BodyId) : -1;
                    if (bodyIndex >= 0) _bodyComponentCounts[bodyIndex]++;
                    _ranges[componentCount++] = new ComponentRange(start, memberStart, memberCount, bodyIndex, isFixed);
                }
                int retiredCount = 0;
                for (int i = 0; i < bodies.Length; i++)
                    if (_bodyComponentCounts[i] != 1) _retiredBodyIds[retiredCount++] = _bodyIds[i];
                for (int i = 0; i < componentCount; i++)
                {
                    // 先知道每个旧体的全部分量数，再一次构造最终分类，避免拆分成员被复制两遍。
                    ComponentRange range = _ranges[i];
                    StructureDisposition disposition = range.BodyIndex < 0
                        ? range.IsFixed ? StructureDisposition.RetainFixedGrid : StructureDisposition.ExtractFreeGrid
                        : _bodyComponentCounts[range.BodyIndex] == 1
                            ? StructureDisposition.RetainBody : StructureDisposition.CreateChildBody;
                    _components[i] = new StructureComponent(_keys[range.SeedIndex].Position, _groups[range.SeedIndex],
                        range.IsFixed, disposition, new ArraySegment<CellKey>(_members, range.MemberStart, range.MemberCount));
                }
                var plan = new StructurePlan(new ArraySegment<StructureComponent>(_components, 0, componentCount),
                    new ArraySegment<ulong>(_retiredBodyIds, 0, retiredCount));
                return new StructurePlanResult(WorldResult.Success(), plan);
            }
            catch (OutOfMemoryException)
            {
                return Failed(Error(WorldErrorCode.CapacityExceeded, "planAllocation", "公共计划复制分配失败，未返回部分计划。"));
            }
            catch (InvalidOperationException exception)
            {
                return Failed(Error(WorldErrorCode.NotReady, "candidate", "候选视图租约不可读：" + exception.Message));
            }
            finally
            {
                Array.Clear(_groups, 0, Math.Min(cellCount, CellCapacity));
                Array.Clear(_components, 0, componentCount);
                _planning = false;
            }
        }

        private WorldResult ValidateKey(IWorkingWorldView world, in CellKey key, int bodyCount)
        {
            if (key.Generation != world.Generation)
                return Error(WorldErrorCode.StaleGeneration, key, "候选格代次与视图不一致。");
            CellPositionKey position = key.Position;
            if (position.OwnerKind == OwnerKind.Grid)
            {
                if (position.BodyId != 0 || position.X < 0 || position.Y < 0 ||
                    position.X >= world.Config.Width || position.Y >= world.Config.Height)
                    return Error(WorldErrorCode.OutOfBounds, key, "主网格格引用超出逻辑世界，尾块区域不能参与分析。");
            }
            else if (position.OwnerKind != OwnerKind.Body || position.BodyId == 0 ||
                Array.BinarySearch(_bodyIds, 0, bodyCount, position.BodyId) < 0)
                return Error(WorldErrorCode.InvalidArgument, key, "动态格引用没有对应原体目录项。");
            return WorldResult.Success();
        }

        private void Visit(in CellKey source, long x, long y, string group, int cellCount, ref int tail)
        {
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue) return;
            var neighbour = new CellKey(source.Generation,
                new CellPositionKey(source.Position.OwnerKind, source.Position.BodyId, (int)x, (int)y));
            int index = Array.BinarySearch(_keys, 0, cellCount, neighbour, KeyComparer.Instance);
            if (index < 0 || _visited[index] || !string.Equals(group, _groups[index], StringComparison.Ordinal)) return;
            _visited[index] = true;
            _queue[tail++] = index;
        }

        internal static WorldResult Error(WorldErrorCode code, in CellKey key, string message) =>
            Error(code, key.Position.OwnerKind + "/" + key.Position.BodyId + "/" + key.Position.X + "/" + key.Position.Y, message);

        private static WorldResult Error(WorldErrorCode code, string target, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("Structure", target, message));

        private static StructurePlanResult Failed(WorldResult result) => new StructurePlanResult(result);
    }
}
