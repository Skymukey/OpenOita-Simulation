using OpenOita.Contracts;

namespace OpenOita.Structure
{
    // IsFixed 必须由 M02 的候选视图按材料实例提供，Remove/Replace 后不能沿用旧坐标标记。
    internal static class FixedCellPolicy
    {
        internal static WorldResult Read(IWorkingWorldView world, in CellKey key, bool isStructure, out bool isFixed)
        {
            isFixed = world.IsFixed(key);
            if (isFixed && (key.Position.OwnerKind != OwnerKind.Grid || !isStructure))
                return ConnectivityAnalyzer.Error(WorldErrorCode.InvalidArgument, key,
                    "有效固定点只能属于候选主网格中的结构材料实例。");
            return WorldResult.Success();
        }
    }
}
