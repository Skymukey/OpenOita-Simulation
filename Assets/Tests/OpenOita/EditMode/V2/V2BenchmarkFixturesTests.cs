using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;
using OpenOita.V2.Validation;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class V2BenchmarkFixturesTests
    {
        [Test]
        public void FractureFixtureUsesStrictCoordinateOrderingAndOneSupport()
        {
            Assert.That(V2BenchmarkScenarioCatalog.TryCreate("fracture8193-concentrated", out var scenario), Is.True);
            WorldSources sources = scenario.Build(BaselineSources.Read());
            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(sources);
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Assert.That(loaded.Scene.Cells.Count, Is.EqualTo(8193));
            Assert.That(loaded.Scene.FixedCells.Count, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MixedLayoutsContainAllMaterialsAndKeepTheRequestedActivity(bool dispersed)
        {
            var scenario = new V2BenchmarkScenario
            {
                Width = 256, Height = 256, CellCount = 10000, ActiveCount = 1000,
                Layout = dispersed ? V2BenchmarkLayout.Dispersed : V2BenchmarkLayout.Concentrated,
                Material = V2BenchmarkMaterial.Mixed,
                ActivityFixture = true, NaturalActivity = true, WithPhysics = true
            };
            WorldSources sources = scenario.Build(BaselineSources.Read());
            var scene = JObject.Parse(sources.SceneText);
            var cells = (JArray)scene["cells"];
            Assert.That(cells.Count, Is.EqualTo(10000));
            Assert.That(cells.Count(cell => (int)cell["materialId"] == 101), Is.EqualTo(490));
            Assert.That(cells.Count(cell => (int)cell["materialId"] == 103), Is.EqualTo(490));
            Assert.That(cells.Count(cell => (int)cell["materialId"] == 104), Is.EqualTo(20));
            Assert.That(cells.Count(cell => (int)cell["materialId"] == 102), Is.EqualTo(9000));
            Assert.That(((JArray)scene["initialBurning"]).Count, Is.EqualTo(20));
            Assert.That(scenario.ActiveFlowCount, Is.EqualTo(980));
        }
    }
}
