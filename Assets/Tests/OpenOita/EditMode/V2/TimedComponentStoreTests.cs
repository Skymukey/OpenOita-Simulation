using NUnit.Framework;
using OpenOita.V2;
using Unity.Collections;
using Unity.Jobs;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class TimedComponentStoreTests
    {
        [Test]
        public void DueAtExact256IsReturnedOnThatTick()
        {
            using (var store = new TimedComponentStore())
            using (var due = new NativeList<int>(4, Allocator.Persistent))
            {
                int handle = store.Create(new CellCold { MaterialId = 103, ExpiryTick = 256 });
                store.Schedule(handle, 256);
                for (ulong tick = 1; tick <= 256; tick++)
                {
                    store.Advance(tick, due);
                    if (tick < 256) Assert.That(due.Length, Is.Zero, "过早触发 tick=" + tick);
                }
                Assert.That(Contains(due, handle), Is.True);
            }
        }

        [Test]
        public void DueAtExact65536SurvivesTwoCascadeLevels()
        {
            using (var store = new TimedComponentStore())
            using (var due = new NativeList<int>(4, Allocator.Persistent))
            {
                int handle = store.Create(new CellCold { MaterialId = 103, ExpiryTick = 65536 });
                store.Schedule(handle, 65536);
                for (ulong tick = 1; tick <= 65536; tick++)
                {
                    store.Advance(tick, due);
                    if (tick < 65536 && due.Length != 0)
                        Assert.Fail("过早触发 tick=" + tick);
                }
                Assert.That(Contains(due, handle), Is.True);
            }
        }

        [Test]
        public void BlockedGasStillExpiresFromItsAbsoluteDueTick()
        {
            using (var store = new TimedComponentStore())
            using (var due = new NativeList<int>(4, Allocator.Persistent))
            {
                int handle = store.Create(new CellCold
                {
                    MaterialId = 103, X = 8, Y = 9, ExpiryTick = 5, FuelRemaining = 17
                });
                store.Schedule(handle, 5);
                for (ulong tick = 1; tick <= 5; tick++) store.Advance(tick, due);

                Assert.That(Contains(due, handle), Is.True);
                Assert.That(store.Read(handle).X, Is.EqualTo(8));
                Assert.That(store.Read(handle).Y, Is.EqualTo(9));
                Assert.That(store.Read(handle).FuelRemaining, Is.EqualTo(17));
            }
        }

        [Test]
        public void DeletedRecordCannotFireAndReusedHandleGetsNewGeneration()
        {
            using (var store = new TimedComponentStore())
            using (var due = new NativeList<int>(4, Allocator.Persistent))
            {
                int oldHandle = store.Create(new CellCold { MaterialId = 103, ExpiryTick = 5 });
                uint oldGeneration = store.Read(oldHandle).Generation;
                store.Schedule(oldHandle, 5);
                store.Delete(oldHandle);
                int newHandle = store.Create(new CellCold { MaterialId = 103, ExpiryTick = 20 });
                uint newGeneration = store.Read(newHandle).Generation;
                Assert.That(newHandle, Is.EqualTo(oldHandle));
                Assert.That(newGeneration, Is.GreaterThan(oldGeneration));
                store.Schedule(newHandle, 20);

                for (ulong tick = 1; tick <= 5; tick++) store.Advance(tick, due);
                Assert.That(Contains(due, oldHandle), Is.False);
                for (ulong tick = 6; tick <= 20; tick++) store.Advance(tick, due);
                Assert.That(Contains(due, newHandle), Is.True);
            }
        }

        [Test]
        public void NativeAdvanceClassifiesGasAndBurningWithoutGrowingOutputLists()
        {
            using (var store = new TimedComponentStore())
            using (var definitions = new NativeArray<OpenOita.V2.MaterialDefinition>(65536, Allocator.Persistent))
            using (var gasDue = new NativeList<int>(1, Allocator.Persistent))
            using (var burningDue = new NativeList<int>(1, Allocator.Persistent))
            using (var overflow = new NativeArray<int>(1, Allocator.Persistent))
            {
                var writableDefinitions = definitions;
                int gasCapacity = gasDue.Capacity, burningCapacity = burningDue.Capacity;
                writableDefinitions[103] = new OpenOita.V2.MaterialDefinition { Id = 103, Rules = OpenOita.Contracts.RuleMask.GasDrift };
                writableDefinitions[104] = new OpenOita.V2.MaterialDefinition { Id = 104, Rules = OpenOita.Contracts.RuleMask.Burnable };
                int gas = store.Create(new CellCold { MaterialId = 103, ExpiryTick = 1 });
                int burning = store.Create(new CellCold { MaterialId = 104, BurnEndTick = 1 });
                store.Schedule(gas, 1);
                store.Schedule(burning, 1);

                var job = new NativeTimeWheelAdvanceJob
                {
                    Wheel = store.GetNativeTimeWheel(),
                    Definitions = definitions,
                    GasDue = gasDue,
                    BurningDue = burningDue,
                    Overflow = overflow,
                    Tick = 1
                };
                job.Run();
                store.CommitNativeTick(1);

                Assert.That(gasDue.Length, Is.EqualTo(1));
                Assert.That(gasDue[0], Is.EqualTo(gas));
                Assert.That(burningDue.Length, Is.EqualTo(1));
                Assert.That(burningDue[0], Is.EqualTo(burning));
                Assert.That(gasDue.Capacity, Is.EqualTo(gasCapacity));
                Assert.That(burningDue.Capacity, Is.EqualTo(burningCapacity));
                Assert.That(overflow[0], Is.Zero);
            }
        }

        [Test]
        public void NativeAdvanceReportsOutputOverflowWithoutResizing()
        {
            using (var store = new TimedComponentStore())
            using (var definitions = new NativeArray<OpenOita.V2.MaterialDefinition>(65536, Allocator.Persistent))
            using (var gasDue = new NativeList<int>(1, Allocator.Persistent))
            using (var burningDue = new NativeList<int>(1, Allocator.Persistent))
            using (var overflow = new NativeArray<int>(1, Allocator.Persistent))
            {
                var writableDefinitions = definitions;
                int gasCapacity = gasDue.Capacity;
                writableDefinitions[103] = new OpenOita.V2.MaterialDefinition { Id = 103, Rules = OpenOita.Contracts.RuleMask.GasDrift };
                for (int i = 0; i <= gasCapacity; i++)
                    store.Schedule(store.Create(new CellCold { MaterialId = 103 }), 1);

                var job = new NativeTimeWheelAdvanceJob
                {
                    Wheel = store.GetNativeTimeWheel(), Definitions = definitions,
                    GasDue = gasDue, BurningDue = burningDue, Overflow = overflow, Tick = 1
                };
                job.Run();
                store.CommitNativeTick(1);

                Assert.That(overflow[0], Is.EqualTo(1));
                Assert.That(gasDue.Capacity, Is.EqualTo(gasCapacity));
                Assert.That(gasDue.Length, Is.EqualTo(gasCapacity));
            }
        }

        private static bool Contains(NativeList<int> values, int target)
        {
            for (int i = 0; i < values.Length; i++) if (values[i] == target) return true;
            return false;
        }
    }
}
