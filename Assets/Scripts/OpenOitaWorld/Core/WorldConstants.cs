public static class WorldConstants
{
    // 仅供旧原型坐标辅助函数使用；正式存储按 WorldConfig 的实际宽高检查。
    public const int LogicalWorldSize = 256;
    public const int ChunkSize = 128;
    public const int CellsPerChunk = ChunkSize * ChunkSize;
    public const int ChunksPerAxis = (LogicalWorldSize + ChunkSize - 1) / ChunkSize;
}
