using System.Collections;
using System.Runtime.InteropServices;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Tests.Fixtures;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode
{
    public sealed class ContractConsumerTests
    {
        [UnityTest]
        public IEnumerator M00_02_ConsumerAndReadonlyValuesWorkInPlayMode()
        {
            var result = ContractConsumer.Exercise(new RejectingWorldFactory(), new WorldSources("{}", "{}", "{}"));
            Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(Marshal.SizeOf<CellSnapshot>(), Is.LessThanOrEqualTo(32));
            var fixture = FixtureCatalog.Cross();
            Assert.That(fixture.Scene.Cells.Count, Is.EqualTo(9));
            yield return null;
            Assert.That(fixture.Config.ChunkSize, Is.EqualTo(128));
        }
    }
}
