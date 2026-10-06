using UnityEngine;

public static class WorldGenerator
{
    public static void FillCoreChunk(ChunkStore store, MaterialRegistry registry, string fillMaterialName)
    {
        if (!registry.TryGetByName(fillMaterialName, out MaterialDefinition material))
        {
            Debug.LogError($"Core chunk fill material '{fillMaterialName}' was not found in MaterialRegistry.");
            return;
        }

        ushort materialId = (ushort)material.id;
        var cell = new CellState
        {
            MaterialId = materialId
        };

        for (int y = 0; y < System.Math.Min(store.Height, WorldConstants.ChunkSize); y++)
        {
            for (int x = 0; x < System.Math.Min(store.Width, WorldConstants.ChunkSize); x++)
            {
                var result = store.Write(x, y, cell);
                if (!result.IsSuccess) throw new System.InvalidOperationException(result.Diagnostic.Message);
            }
        }
    }
}
