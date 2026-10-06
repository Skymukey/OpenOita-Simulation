using System;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Tests.Fixtures;
using UnityEditor.Compilation;

namespace OpenOita.Tests.EditMode
{
    public sealed class AssemblyBoundaryTests
    {
        [Test]
        public void M00_02_PlayerAssemblyGraphDoesNotReferenceEditorOrTestAssemblies()
        {
            var runtime = CompilationPipeline.GetAssemblies(AssembliesType.Player).Single(a => a.name == "OpenOita.Runtime");
            Assert.That(runtime.compiledAssemblyReferences.Any(path => System.IO.Path.GetFileName(path).StartsWith("UnityEditor", StringComparison.Ordinal)), Is.False);
            Assert.That(runtime.assemblyReferences.Any(a => a.name.StartsWith("OpenOita.Tests", StringComparison.Ordinal)), Is.False);
            Assert.That(runtime.sourceFiles.Any(path => path.Replace('\\', '/').Contains("Assets/Tests/")), Is.False);
            Assert.That(typeof(IWorld).Assembly.GetName().Name, Is.EqualTo("OpenOita.Runtime"));
        }
        [Test]
        public void M00_02_OnlyOneAuthoritativeCellStateExistsAndOldHostGuidIsPreserved()
        {
            var types = typeof(IWorld).Assembly.GetTypes().Where(t => t.Name == "CellState").ToArray();
            Assert.That(types.Length, Is.EqualTo(1));
            Assert.That(types[0].FullName, Is.EqualTo("CellState"));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/Scripts/OpenOitaWorld/Host/WorldHost.cs"), Is.EqualTo("f8e7d6c5b4a39281706f5e4d3c2b1a09"));
            Assert.That(ContractConsumer.Exercise(new RejectingWorldFactory(), new WorldSources("{}", "{}", "{}")).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
        }
    }
}
