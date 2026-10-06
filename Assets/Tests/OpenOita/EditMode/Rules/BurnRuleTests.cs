using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Rules;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Rules
{
    public sealed class BurnRuleTests
    {
        [Test]
        public void M03_T9_01_ShortFuelPropagationAndBurnoutFollowExactTicks()
        {
            var f = new RuleFixture(); f.Put(10, 10, 104, true); f.Put(11, 10, 104);
            for (int tick = 1; tick <= 5; tick++)
            {
                f.Step();
                CellSnapshot a = f.State(10, 10), b = f.State(11, 10);
                Assert.That(a.FuelTicksRemaining, Is.EqualTo(tick == 5 ? 0 : 5 - tick));
                Assert.That(b.IsBurning, Is.EqualTo(tick >= 2));
                Assert.That(b.FuelTicksRemaining, Is.EqualTo(tick < 3 ? 5 : 7 - tick));
                if (tick == 2) Assert.That(b.IgnitedTick, Is.EqualTo(2));
                if (tick == 5) Assert.That(a.MaterialId, Is.Zero);
                TestContext.WriteLine($"Tick{tick}: A余量={a.FuelTicksRemaining}, B余量={b.FuelTicksRemaining}, B燃烧={b.IsBurning}");
            }
            Assert.That(f.BurnedOut, Is.EqualTo(1));
        }

        [Test]
        public void M03_T9_02_PreFlowWetHandleSurvivesWaterLeavingContact()
        {
            var f = new RuleFixture(); f.Put(10, 10, 104, true); f.Put(10, 9, 101);
            f.Begin(); f.Collect(TickStage.PreFlowContacts);
            f.Instances.TryGetInstance(RuleFixture.Grid(10, 10), out CellInstanceHandle wood);
            Assert.That(f.Instances.IsWet(wood), Is.True);
            f.Water(); f.Steam(); f.Collect(TickStage.Extinguish); f.Extinguish(); f.Burn();
            Assert.That(f.State(10, 8).MaterialId, Is.EqualTo(101));
            Assert.That(f.State(10, 10).IsBurning, Is.False);
            Assert.That(f.State(10, 10).FuelTicksRemaining, Is.EqualTo(5));
            Assert.That(f.State(10, 10).SpreadCountdown, Is.EqualTo(2));
            Assert.That(f.State(10, 10).IgnitedTick, Is.Zero);
            Assert.That(f.Instances.IsWet(wood), Is.True);
            TestContext.WriteLine("Tick1: 水=(10,8)，A熄灭，余量=5，湿句柄保留");
        }

        [Test]
        public void M03_T9_03_ExtinguishKeepsPartialFuelAndReigniteStartsNextTick()
        {
            var f = new RuleFixture(); f.Put(10, 10, 104, true); f.Step(); f.Step();
            Assert.That(f.State(10, 10).FuelTicksRemaining, Is.EqualTo(3));
            f.Put(10, 9, 101); f.Step();
            Assert.That(f.State(10, 10).FuelTicksRemaining, Is.EqualTo(3));
            Assert.That(f.State(10, 10).IsBurning, Is.False);
            f.Cells.Remove(RuleFixture.Grid(10, 8));
            f.Begin(); CellKey key = RuleFixture.Grid(10, 10);
            f.Cells[key] = WetContactPolicy.Ignite(f.State(key), f.WorkingTick); f.Refresh();
            f.Collect(TickStage.PreFlowContacts); f.Burn();
            Assert.That(f.State(key).FuelTicksRemaining, Is.EqualTo(3));
            Assert.That(f.Materials.TryGet(104, out MaterialRuntimeEntry wood), Is.True);
            CellSnapshot before = f.State(key);
            Assert.That(WetContactPolicy.CanIgnite(wood, before, false), Is.False);
            Assert.That(f.State(key), Is.EqualTo(before));
            f.Step(); Assert.That(f.State(key).FuelTicksRemaining, Is.EqualTo(2));
            TestContext.WriteLine("Tick1/2: A余量4/3；Tick3熄灭仍3；Tick4重新点燃仍3；Tick5余量2");
        }

        [Test]
        public void M03_T9_04_WetTargetRejectsExternalIgniteAndMultiplePropagation()
        {
            var f = new RuleFixture(); f.Put(9, 10, 104, true); f.Put(11, 10, 104, true);
            f.Put(10, 10, 104); f.Put(10, 9, 101);
            foreach (CellKey key in new[] { RuleFixture.Grid(9, 10), RuleFixture.Grid(11, 10) })
                f.Cells[key] = new CellSnapshot(104, 1, 5, 1);
            f.Begin(); CellKey target = RuleFixture.Grid(10, 10);
            f.Cells[target] = WetContactPolicy.Ignite(f.State(target), f.WorkingTick);
            f.Collect(TickStage.PreFlowContacts); f.Water(); f.Collect(TickStage.Extinguish); f.Extinguish(); f.Burn();
            Assert.That(f.State(target).IsBurning, Is.False);
            Assert.That(f.State(target).FuelTicksRemaining, Is.EqualTo(5));
            Assert.That(f.State(target).IgnitedTick, Is.Zero);
            Assert.That(f.State(9, 10).FuelTicksRemaining, Is.EqualTo(4));
            Assert.That(f.State(11, 10).FuelTicksRemaining, Is.EqualTo(4));
        }

        [Test]
        public void M03_T9_01_OnlyFourNeighboursIgniteOnceWithoutRecursiveScan()
        {
            var f = new RuleFixture();
            f.Put(9, 10, 104, true); f.Put(11, 10, 104, true); f.Put(10, 10, 104);
            f.Put(10, 11, 104); f.Put(10, 12, 104); f.Put(9, 11, 104);
            f.Cells[RuleFixture.Grid(9, 10)] = new CellSnapshot(104, 1, 5, 1);
            f.Cells[RuleFixture.Grid(11, 10)] = new CellSnapshot(104, 1, 5, 1);
            f.Begin(); IRuleBatch batch = f.Run(f.Rules.Burning, TickStage.Burning);
            Assert.That(batch.Intents.ToArray().Count(i => i.Kind == MutationKind.Ignite && i.Target.Equals(RuleFixture.Grid(10, 10))), Is.EqualTo(1));
            f.Apply(batch);
            Assert.That(f.State(10, 10).IsBurning, Is.True);
            Assert.That(f.State(10, 11).IsBurning, Is.False); // 相对两源只是角点。
            Assert.That(f.State(10, 12).IsBurning, Is.False);
            Assert.That(f.State(9, 11).IsBurning, Is.True);
        }

        [Test]
        public void M03_T9_01_BurnedOutSourceCannotPropagateOrBeRevived()
        {
            var f = new RuleFixture(); f.Put(10, 10, 104, true); f.Put(11, 10, 104);
            f.Cells[RuleFixture.Grid(10, 10)] = new CellSnapshot(104, 1, 1, 1);
            f.Begin(); f.Burn();
            Assert.That(f.State(10, 10).MaterialId, Is.Zero);
            Assert.That(f.State(11, 10).IsBurning, Is.False);
            Assert.That(f.BurnedOut, Is.EqualTo(1));
            var other = new RuleFixture(); other.Put(10, 10, 104, true); other.Put(11, 10, 104, true);
            other.Cells[RuleFixture.Grid(10, 10)] = new CellSnapshot(104, 1, 4, 1);
            other.Cells[RuleFixture.Grid(11, 10)] = new CellSnapshot(104, 1, 1, 1);
            other.Begin(); other.Burn();
            Assert.That(other.State(11, 10).MaterialId, Is.Zero);
        }

        [Test]
        public void M03_T9_04_ExtinguishingUsesCapabilityInsteadOfIdOrKind()
        {
            var f = new RuleFixture(editMaterials: root =>
            {
                JObject water = (JObject)((JArray)root["materials"])[0];
                water["tags"] = new JArray("liquid_flow");
            });
            f.Put(10, 10, 104, true); f.Put(10, 9, 101); f.Step();
            Assert.That(f.State(10, 10).IsBurning, Is.True);
            Assert.That(f.State(10, 10).FuelTicksRemaining, Is.EqualTo(4));
            var custom = new RuleFixture(editMaterials: root =>
            {
                ((JArray)root["materials"])[0]["id"] = 501;
                ((JArray)root["materials"])[3]["id"] = 504;
            });
            custom.Put(10, 10, 504, true); custom.Put(10, 9, 501); custom.Step();
            Assert.That(custom.State(10, 10).IsBurning, Is.False);
            Assert.That(custom.State(10, 8).MaterialId, Is.EqualTo(501));
        }

        [TestCase(ContactFeature.EdgeEdge, 0f, true)]
        [TestCase(ContactFeature.VertexEdge, 0f, true)]
        [TestCase(ContactFeature.VertexVertex, 0f, false)]
        [TestCase(ContactFeature.EdgeEdge, 0.001f, false)]
        [TestCase(ContactFeature.PositiveAreaOverlap, 0f, true)]
        public void M03_T9_05_PreclassifiedCrossOwnerContactFixture(ContactFeature feature, float distance, bool wet)
        {
            var f = new RuleFixture(); CellKey body = RuleFixture.Body(7, 0, 0), water = RuleFixture.Grid(10, 9);
            f.Put(body, 104, true); f.Put(water, 101);
            f.BodyItems = new[] { new BodySnapshot(7, new BodyPose(new Vector2(1, 1), (float)Math.PI / 4), default, default, 1) };
            var contacts = new ContactFixture(); contacts.Items.Add(new CellContact(body, water, feature, distance)); f.Contacts = contacts;
            f.Begin(); f.Collect(TickStage.PreFlowContacts); f.Extinguish(); f.Burn();
            Assert.That(f.State(body).IsBurning, Is.EqualTo(!wet));
            Assert.That(f.State(body).FuelTicksRemaining, Is.EqualTo(wet ? 5 : 4));
            TestContext.WriteLine($"预分类接触夹具: {feature}, d={distance}, 熄灭={wet}；未执行 M06 实际 OBB/旋转求解");
        }

        [Test]
        public void M03_T9_05_PhysicsWetExtinguishesWithoutRefundingConsumedFuel()
        {
            var f = new RuleFixture(); CellKey body = RuleFixture.Body(7, 0, 0), water = RuleFixture.Grid(10, 9);
            f.Put(body, 104, true); f.Put(water, 101);
            f.BodyItems = new[] { new BodySnapshot(7, default, default, default, 1) };
            var contacts = new ContactFixture(); f.Contacts = contacts;
            f.Begin(); f.Burn(); Assert.That(f.State(body).FuelTicksRemaining, Is.EqualTo(4));
            contacts.Items.Add(new CellContact(water, body, ContactFeature.VertexEdge, 0));
            f.Collect(TickStage.Physics); f.Extinguish(TickStage.Physics);
            Assert.That(f.State(body).IsBurning, Is.False);
            Assert.That(f.State(body).FuelTicksRemaining, Is.EqualTo(4));
        }

        [Test]
        public void M03_T9_06_IdentityTransferPreservesWetAndPartialFuelInMemoryFixture()
        {
            var f = new RuleFixture(); CellKey grid = RuleFixture.Grid(10, 10), body = RuleFixture.Body(8, 0, 0);
            f.Put(grid, 104, true); f.Cells[grid] = new CellSnapshot(104, 1, 3, 1);
            f.Begin(); f.Instances.TryGetInstance(grid, out CellInstanceHandle instance); f.Instances.MarkWet(instance);
            f.Cells[body] = f.State(grid); f.Cells.Remove(grid); f.Instances.Move(instance, body); f.Refresh();
            f.Extinguish();
            Assert.That(f.State(body).FuelTicksRemaining, Is.EqualTo(3));
            Assert.That(f.State(body).IsBurning, Is.False);
            Assert.That(f.Instances.TryGetInstance(body, out CellInstanceHandle moved), Is.True);
            Assert.That(moved, Is.EqualTo(instance));
            Assert.That(f.Instances.IsWet(moved), Is.True);
            TestContext.WriteLine("仅手动身份迁移夹具；真实 M05 提取/再次拆分未运行");
        }

        [Test]
        public void M03_T9_06_RuleOnlyMaterialAccountingConservesMovesAndCountsRemovalReasons()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(15, 5, 103); f.Put(20, 20, 104, true);
            f.Cells[RuleFixture.Grid(15, 5)] = new CellSnapshot(103, lifetimeTicksRemaining: 2);
            for (int tick = 1; tick <= 5; tick++) f.Step();
            Assert.That(f.Cells.Values.Count(c => c.MaterialId == 101), Is.EqualTo(1));
            Assert.That(f.Cells.Values.Count(c => c.MaterialId == 103) + f.Expired, Is.EqualTo(1));
            Assert.That(f.Cells.Values.Count(c => c.MaterialId == 104) + f.BurnedOut, Is.EqualTo(1));
            Assert.That(f.Expired, Is.EqualTo(1)); Assert.That(f.BurnedOut, Is.EqualTo(1));
            TestContext.WriteLine("规则夹具账目: 水1→1；蒸汽1=现存0+自然消散1；木1=现存0+燃尽1");
        }
    }
}
