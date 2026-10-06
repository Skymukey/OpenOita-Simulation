using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Render;
using OpenOita.Tests.Fixtures;

namespace OpenOita.Tests.EditMode.M07B
{
    public sealed class M07DisplayPixelTests
    {
        [Test] public void M07B_01_08_SparseMaterialColorAndPersistentHalfFuelDamage()
        {
            var source = M06Sources.Create(System.Array.Empty<InitialCell>());
            source = new WorldSources(source.MaterialsText.Replace("\"id\": 104", "\"id\": 65535"), source.WorldConfigText, source.SceneText);
            var loaded = new WorldSourceLoader().Load(source); Assert.That(loaded.Result.IsSuccess, Is.True);
            loaded.Materials.TryGet(65535, out MaterialRuntimeEntry wood);
            var fresh = CommittedWorldRenderer.MaterialPixel(new CellSnapshot(65535, fuelTicksRemaining: 250), wood);
            var burning = CommittedWorldRenderer.MaterialPixel(new CellSnapshot(65535, flags: 1, fuelTicksRemaining: 125), wood);
            var extinguished = CommittedWorldRenderer.MaterialPixel(new CellSnapshot(65535, fuelTicksRemaining: 125), wood);
            Assert.That(fresh, Is.EqualTo(wood.Color)); Assert.That(extinguished, Is.EqualTo(burning)); Assert.That(burning.r, Is.LessThan(fresh.r)); Assert.That(burning.a, Is.EqualTo(255));
            Assert.That(loaded.Materials.Count, Is.EqualTo(4)); Assert.That(wood.CompactIndex, Is.InRange(1, 4));
        }
    }
}
