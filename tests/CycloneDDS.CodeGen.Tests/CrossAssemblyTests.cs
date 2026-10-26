using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CycloneDDS.CodeGen.Tests
{
    /// <summary>
    /// IDL file and module placement, and type references across assemblies via
    /// <c>[assembly: DdsIdlMapping]</c> and <c>#include</c>.
    /// </summary>
    public class CrossAssemblyTests : CodeGenTestBase
    {
        [Fact]
        public void CustomFile_MultipleTypes_SingleIdl()
        {
            var folder = CreateProject("ProjectA", ("Types.cs", @"
using CycloneDDS.Schema;
namespace ProjectA {
    [DdsIdlFile(""Common"")]
    [DdsStruct]
    public partial struct Point { public int X; }

    [DdsIdlFile(""Common"")]
    [DdsStruct]
    public partial struct Vector { public int Y; }
}"));

            CompileProject("ProjectA", folder);

            var content = File.ReadAllText(Path.Combine(GeneratedDir(folder), "Common.idl"));
            Assert.Contains("struct Point", content);
            Assert.Contains("struct Vector", content);
            Assert.False(File.Exists(Path.Combine(GeneratedDir(folder), "Types.idl")));

            var map = File.ReadAllText(Path.Combine(GeneratedDir(folder), "CycloneDDS.IdlMap.g.cs"));
            Assert.Contains("DdsIdlMapping(\"ProjectA.Point\", \"Common\", \"ProjectA\")", map);
            Assert.Contains("DdsIdlMapping(\"ProjectA.Vector\", \"Common\", \"ProjectA\")", map);
        }

        [Fact]
        public void CustomModule_NestsModulesAndNamesTheType()
        {
            var folder = CreateProject("ProjectB", ("Legacy.cs", @"
using CycloneDDS.Schema;
namespace MyApp.Internal {
    [DdsIdlModule(""Legacy::Sys"")]
    [DdsStruct]
    public partial struct State { public int Id; }
}"));

            CompileProject("ProjectB", folder);

            var lines = File.ReadAllLines(Path.Combine(GeneratedDir(folder), "Legacy.idl"));
            int legacy = System.Array.FindIndex(lines, l => l.Trim() == "module Legacy {");
            int sys = System.Array.FindIndex(lines, l => l.Trim() == "module Sys {");
            int state = System.Array.FindIndex(lines, l => l.Trim() == "struct State {");
            Assert.True(legacy >= 0 && legacy < sys && sys < state);

            // The module, not the C# namespace, names the type on the wire
            var generated = File.ReadAllText(Path.Combine(GeneratedDir(folder), "MyApp.Internal.State.g.cs"));
            Assert.Contains("\"Legacy::Sys::State\"", generated);
        }

        [Fact]
        public void TwoAssemblies_BReferencesA_IncludeGenerated()
        {
            var folderA = CreateProject("LibA", ("Point.cs", @"
using CycloneDDS.Schema;
namespace LibA {
    [DdsIdlFile(""MathDefs"")]
    [DdsStruct]
    public partial struct Point { public int X; }
}"));
            string dllA = CompileProject("LibA", folderA);

            var folderB = CreateProject("LibB", ("Path.cs", @"
using System.Collections.Generic;
using CycloneDDS.Schema;
using LibA;
namespace LibB {
    [DdsStruct]
    [DdsIdlFile(""RobotPath"")]
    public partial struct RobotPath {
        public Point Start;
        public List<Point> Waypoints;
    }
}"));
            CompileProject("LibB", folderB, [dllA]);

            var content = File.ReadAllText(Path.Combine(GeneratedDir(folderB), "RobotPath.idl"));
            Assert.Contains("#include \"MathDefs.idl\"", content);
            Assert.Contains("::LibA::Point Start;", content);
            Assert.Contains("sequence<::LibA::Point> Waypoints;", content);
        }

        [Fact]
        public void IncludeCycleBetweenFiles_ClearError()
        {
            // Types are acyclic (A1 -> B1 -> A2) but their files include each other
            var folder = CreateProject("Circular",
                ("FileA.cs", @"
using CycloneDDS.Schema;
namespace Circular {
    [DdsStruct] public partial struct A1 { public B1 b; }
    [DdsStruct] public partial struct A2 { public int x; }
}"),
                ("FileB.cs", @"
using CycloneDDS.Schema;
namespace Circular {
    [DdsStruct] public partial struct B1 { public A2 a; }
}"));

            var ex = Assert.Throws<CodeGenerator.SchemaException>(() => CompileProject("Circular", folder));
            Assert.Contains("include each other", ex.Message);
            Assert.Contains("FileA.idl", ex.Message);
            Assert.Contains("FileB.idl", ex.Message);
        }

        [Fact]
        public void IdlNameCollision_ClearError()
        {
            var folder = CreateProject("Collision", ("Collision.cs", @"
using CycloneDDS.Schema;
namespace Collision {
    [DdsStruct]
    public partial struct Type1 { public int x; }
}
namespace Other {
    [DdsStruct]
    [DdsIdlModule(""Collision"")]
    public partial struct Type1 { public int y; }
}"));

            var ex = Assert.Throws<CodeGenerator.SchemaException>(() => CompileProject("Collision", folder));
            Assert.Contains("Collision::Type1", ex.Message);
        }

        [Fact]
        public void CrossAssembly_Transitive_AllIncluded()
        {
            var folderA = CreateProject("TransitiveA", ("Point.cs", @"
using CycloneDDS.Schema;
namespace LibA {
    [DdsIdlFile(""PointFile"")]
    [DdsStruct]
    public partial struct Point { public int X; }
    public enum Kind { One, Two }
}"));
            string dllA = CompileProject("TransitiveA", folderA);

            var folderB = CreateProject("TransitiveB", ("Path.cs", @"
using CycloneDDS.Schema;
using LibA;
namespace LibB {
    [DdsIdlFile(""PathFile"")]
    [DdsStruct]
    public partial struct Path { public Point P; public Kind K; }
}"));
            string dllB = CompileProject("TransitiveB", folderB, [dllA]);

            var folderC = CreateProject("TransitiveC", ("Robot.cs", @"
using CycloneDDS.Schema;
using LibB;
namespace LibC {
    [DdsIdlFile(""RobotFile"")]
    [DdsStruct]
    public partial struct Robot { public Path MyPath; }
}"));
            CompileProject("TransitiveC", folderC, [dllB, dllA]);

            Assert.Contains("#include \"PathFile.idl\"", File.ReadAllText(Path.Combine(GeneratedDir(folderC), "RobotFile.idl")));

            var pathIdl = File.ReadAllText(Path.Combine(GeneratedDir(folderB), "PathFile.idl"));
            Assert.Contains("#include \"Point.idl\"", pathIdl);   // the enum, by source file name
            Assert.Contains("#include \"PointFile.idl\"", pathIdl);
        }

        [Fact]
        public void ExternalTypeWithoutMapping_ClearError()
        {
            // A [DdsStruct] compiled without running the generator has no DdsIdlMapping
            var folderA = CreateProject("Unmapped", ("Point.cs", @"
using CycloneDDS.Schema;
namespace Unmapped { [DdsStruct] public partial struct Point { public int X; } }"));
            var compilation = CSharpCompilation.Create("Unmapped")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(StandardReferences())
                .AddSyntaxTrees(CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(folderA, "Point.cs"))));
            string dllA = Path.Combine(TempDir, "Unmapped.dll");
            Assert.True(compilation.Emit(dllA).Success);

            var folderB = CreateProject("UsesUnmapped", ("User.cs", @"
using CycloneDDS.Schema;
namespace UsesUnmapped { [DdsStruct] public partial struct User { public Unmapped.Point P; } }"));
            Directory.CreateDirectory(GeneratedDir(folderA));

            var ex = Assert.Throws<CodeGenerator.SchemaException>(() => CompileProject("UsesUnmapped", folderB, [dllA]));
            Assert.Contains("DdsIdlMapping", ex.Message);
            Assert.Contains("User.cs", ex.Message);
        }
    }
}
