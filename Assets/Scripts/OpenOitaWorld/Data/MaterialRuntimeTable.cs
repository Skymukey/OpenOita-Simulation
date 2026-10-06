using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Data
{
    public sealed class MaterialRuntimeTable : IMaterialRuntimeTable
    {
        private readonly ushort[] _idToIndex = new ushort[65536];
        private readonly MaterialRuntimeEntry[] _entries;
        public string MaterialSetId { get; }
        public int Count => _entries.Length - 1;
        internal MaterialRuntimeTable(string materialSetId, List<MaterialRuntimeEntry> entries)
        {
            MaterialSetId = materialSetId;
            entries.Sort((a, b) => a.Id.CompareTo(b.Id));
            _entries = new MaterialRuntimeEntry[entries.Count + 1];
            for (int i = 0; i < entries.Count; i++)
            {
                MaterialRuntimeEntry entry = entries[i];
                ushort index = checked((ushort)(i + 1));
                _idToIndex[entry.Id] = index;
                _entries[index] = new MaterialRuntimeEntry(entry.Id, index, entry.Name, entry.Kind, entry.Color,
                    entry.MassPerCell, entry.Rules, entry.Parameters);
            }
        }
        public bool TryGet(ushort id, out MaterialRuntimeEntry entry)
        {
            ushort index = _idToIndex[id];
            entry = _entries[index];
            return index != 0;
        }
        public MaterialRuntimeEntry GetByCompactIndex(ushort compactIndex)
        {
            if (compactIndex >= _entries.Length) throw new ArgumentOutOfRangeException(nameof(compactIndex));
            return _entries[compactIndex];
        }
    }
}
