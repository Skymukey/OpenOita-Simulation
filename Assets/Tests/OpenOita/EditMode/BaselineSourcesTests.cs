using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Tests.Fixtures;

namespace OpenOita.Tests.EditMode
{
    public sealed class BaselineSourcesTests
    {
        [Test]
        public void M00_01_BaselineHasFourMaterialsAnd175CellsWithValidReferences()
        {
            var sources = BaselineSources.Read();
            JObject materials = BaselineSources.Parse(sources.MaterialsText);
            JObject world = BaselineSources.Parse(sources.WorldConfigText);
            JObject scene = BaselineSources.Parse(sources.SceneText);
            Assert.That(((JArray)materials["materials"]).Select(m => (int)m["id"]), Is.EqualTo(new[] { 101, 102, 103, 104 }));
            Assert.That((string)scene["materialSetId"], Is.EqualTo((string)materials["materialSetId"]));
            Assert.That((string)scene["materialsFile"], Is.EqualTo(sources.MaterialsFileName));
            Assert.That((string)scene["worldConfig"], Is.EqualTo(sources.WorldConfigFileName));
            Assert.That(((JArray)scene["cells"]).Count, Is.EqualTo(175));
            Assert.That(((JArray)scene["fixedCells"]).Count, Is.EqualTo(2));
            Assert.That(((JArray)scene["initialBurning"]).Count, Is.EqualTo(1));
            var cells = new Dictionary<string, ushort>(StringComparer.Ordinal);
            int previous = -1;
            foreach (JObject cell in (JArray)scene["cells"])
            {
                int x = (int)cell["x"], y = (int)cell["y"];
                Assert.That(x, Is.InRange(0, (int)world["width"] - 1));
                Assert.That(y, Is.InRange(0, (int)world["height"] - 1));
                int position = y * (int)world["width"] + x;
                Assert.That(position, Is.GreaterThan(previous));
                previous = position;
                ushort id = (ushort)cell["materialId"];
                Assert.That(id, Is.InRange(101, 104));
                cells.Add(x + "," + y, id);
            }
            foreach (JObject point in (JArray)scene["fixedCells"])
                Assert.That(cells[(int)point["x"] + "," + (int)point["y"]], Is.EqualTo((ushort)102).Or.EqualTo((ushort)104));
            foreach (JObject point in (JArray)scene["initialBurning"])
                Assert.That(cells[(int)point["x"] + "," + (int)point["y"]], Is.EqualTo(104));
        }

        [TestCaseSource(typeof(BaselineSources), nameof(BaselineSources.NegativeCaseIds))]
        public void M00_01_NegativeCasesAreIndependentCopies(string caseId)
        {
            var sources = BaselineSources.Read();
            var negative = BaselineSources.NegativeCopy(sources, caseId);
            Assert.That(negative.MaterialsText != sources.MaterialsText || negative.WorldConfigText != sources.WorldConfigText || negative.SceneText != sources.SceneText, Is.True);
            var unchanged = BaselineSources.Read();
            Assert.That(unchanged.MaterialsText, Is.EqualTo(sources.MaterialsText));
            Assert.That(unchanged.WorldConfigText, Is.EqualTo(sources.WorldConfigText));
            Assert.That(unchanged.SceneText, Is.EqualTo(sources.SceneText));
            if (caseId == "DuplicateJsonKey") Assert.Throws<JsonReaderException>(() => BaselineSources.Parse(negative.MaterialsText));
        }

        [Test]
        public void M00_04_ShortBurningConfigurationOnlyChangesTwoWoodParameters()
        {
            var sources = BaselineSources.Read();
            JObject before = BaselineSources.Parse(sources.MaterialsText);
            JObject after = BaselineSources.Parse(BaselineSources.ShortBurningMaterials(sources.MaterialsText));
            Assert.That((int)after["materials"][3]["ruleParameters"]["burnable"]["fuelTicks"], Is.EqualTo(5));
            Assert.That((int)after["materials"][3]["ruleParameters"]["burnable"]["spreadIntervalTicks"], Is.EqualTo(2));
            after["materials"][3]["ruleParameters"]["burnable"] = before["materials"][3]["ruleParameters"]["burnable"].DeepClone();
            Assert.That(JToken.DeepEquals(before, after), Is.True);
        }
    }
}
