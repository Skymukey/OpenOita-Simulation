using OpenOita.Contracts;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace OpenOita.V2
{
    public struct MaterialDefinition
    {
        public ushort Id;
        public MaterialKind Kind;
        public RuleMask Rules;
        public uint MoveInterval, Lifetime, Fuel, SpreadInterval, ConnectionGroup, Color;
        public float Mass;
        public bool IsStructure => (Rules & RuleMask.Structure) != 0;
        public bool IsGas => (Rules & RuleMask.GasDrift) != 0;
        public bool IsWater => (Rules & RuleMask.LiquidFlow) != 0;
        public bool IsBurnable => (Rules & RuleMask.Burnable) != 0;

        public static NativeArray<MaterialDefinition> Build(IMaterialRuntimeTable source)
        {
            var result = new NativeArray<MaterialDefinition>(65536, Allocator.Persistent);
            var groups = new Dictionary<string, uint>(System.StringComparer.Ordinal);
            for (int i = 1; i <= source.Count; i++)
            {
                MaterialRuntimeEntry value = source.GetByCompactIndex((ushort)i);
                Color32 color = value.Color;
                uint group = 0;
                string groupName = value.Parameters.ConnectionGroup;
                if (!string.IsNullOrEmpty(groupName) && !groups.TryGetValue(groupName, out group))
                { group = (uint)groups.Count + 1; groups.Add(groupName, group); }
                result[value.Id] = new MaterialDefinition
                {
                    Id = value.Id, Kind = value.Kind, Rules = value.Rules, Mass = value.MassPerCell,
                    MoveInterval = value.Parameters.MoveIntervalTicks, Lifetime = value.Parameters.LifetimeTicks,
                    Fuel = value.Parameters.FuelTicks, SpreadInterval = value.Parameters.SpreadIntervalTicks,
                    ConnectionGroup = group,
                    Color = (uint)(color.r | color.g << 8 | color.b << 16 | color.a << 24)
                };
            }
            return result;
        }

    }

    public struct CellCold
    {
        public ulong ExpiryTick, BurnEndTick, NextSpreadTick, NextVisualTick, IgnitedTick, WetTick;
        public uint FuelRemaining, Generation;
        public int GridHandle, X, Y, Next, Previous, Bucket;
        public ulong Due;
        public ushort MaterialId;
    }

    public struct GridCell
    {
        public const byte BurningFlag = 1, LeftFlag = 2, FixedFlag = 4;
        public ushort MaterialId;
        public byte Flags;
        public uint NextMoveTick, ProcessedTick;
        public int ComponentHandle;
        public CellCold Cold;
        public bool IsEmpty => MaterialId == 0;
        public bool IsBurning => (Flags & BurningFlag) != 0;
        public bool IsFixed => (Flags & FixedFlag) != 0;
        public bool PreferLeft => (Flags & LeftFlag) != 0;
    }
}
