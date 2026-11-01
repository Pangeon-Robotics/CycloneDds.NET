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

        [Fact]
        public void LocalIdlFileNamedLikeReferencedOne_ClearError()
        {
            var folderA = CreateProject("SameNameA", ("Messages.cs", @"
using CycloneDDS.Schema;
namespace SameNameA { [DdsStruct] public partial struct Foo { public int X; } }"));
            string dllA = CompileProject("SameNameA", folderA);

            var folderB = CreateProject("SameNameB", ("Messages.cs", @"
using CycloneDDS.Schema;
namespace SameNameB { [DdsStruct] public partial struct Bar { public SameNameA.Foo F; } }"));

            var ex = Assert.Throws<CodeGenerator.SchemaException>(() => CompileProject("SameNameB", folderB, [dllA]));
            Assert.Contains("Messages.idl", ex.Message);
            Assert.Contains("[DdsIdlFile]", ex.Message);
        }

        [Fact]
        public void LocalIdlFileNamedLikeOneIncludedTransitively_ClearError()
        {
            var folderA = CreateProject("IncludedA", ("Base.cs", @"
namespace IncludedA { [CycloneDDS.Schema.DdsStruct] public partial struct Inner { public int X; } }"));
            string dllA = CompileProject("IncludedA", folderA);

            var folderB = CreateProject("IncludedB", ("Types.cs", @"
namespace IncludedB { [CycloneDDS.Schema.DdsStruct] public partial struct Outer { public IncludedA.Inner I; } }"));
            string dllB = CompileProject("IncludedB", folderB, [dllA]);

            var folderC = CreateProject("IncludedC", ("Base.cs", @"
namespace IncludedC { [CycloneDDS.Schema.DdsStruct] public partial struct User { public IncludedB.Outer O; } }"));

            var ex = Assert.Throws<CodeGenerator.SchemaException>(() => CompileProject("IncludedC", folderC, [dllB, dllA]));
            Assert.Contains("Types.idl, which includes Base.idl", ex.Message);
        }

        [Fact]
        public void LocalIdlFileDifferingOnlyInCaseFromReferencedOne_ClearError()
        {
            var folderA = CreateProject("CaseA", ("Messages.cs", @"
namespace CaseA { [CycloneDDS.Schema.DdsStruct] public partial struct Foo { public int X; } }"));
            string dllA = CompileProject("CaseA", folderA);

            var folderB = CreateProject("CaseB", ("messages.cs", @"
namespace CaseB { [CycloneDDS.Schema.DdsStruct] public partial struct Bar { public CaseA.Foo F; } }"));

            var ex = Assert.Throws<CodeGenerator.SchemaException>(() => CompileProject("CaseB", folderB, [dllA]));
            Assert.Contains("Messages.idl", ex.Message);
            Assert.Contains("[DdsIdlFile]", ex.Message);
        }

        [Fact]
        public void UnusedEnumeratorNamedLikeModule_PassesIdlc()
        {
            var folder = CreateProject("EnumModule", ("Types.cs", @"
using CycloneDDS.Schema;
namespace App { public enum Mode { Sim } }
namespace App.Sim { [DdsStruct] public partial struct S { public int X; } }"));

            CompileProject("EnumModule", folder);
        }

        [Fact]
        public void ModulesDifferingOnlyInCase_PassIdlc()
        {
            var folder = CreateProject("ModuleCase", ("Types.cs", @"
using CycloneDDS.Schema;
namespace App.Sim { [DdsStruct] public partial struct A { public int X; } }
namespace App.sim { [DdsStruct] public partial struct B { public int X; } }"));

            CompileProject("ModuleCase", folder);
        }

        [Fact]
        public void CSharpKeywordNames_Compile_AcrossAssemblies()
        {
            // Compiling fails if generated code loses an @; idlc must accept the same names
            var folderA = CreateProject("KeywordA", ("Types.cs", @"
using CycloneDDS.Schema;
namespace KeywordA.@class {
    public enum @enum { @default, @checked }
    [DdsStruct] public partial struct @struct { public int @event; public @enum @params { get; set; } public int[] @lock; }
}"));
            string dllA = CompileProject("KeywordA", folderA);

            var folderB = CreateProject("KeywordB", ("User.cs", @"
using CycloneDDS.Schema;
namespace KeywordB {
    [DdsStruct] public partial struct User { public KeywordA.@class.@struct S; public KeywordA.@class.@enum E; }
}"));
            CompileProject("KeywordB", folderB, [dllA]);

            var map = File.ReadAllText(Path.Combine(GeneratedDir(folderA), "CycloneDDS.IdlMap.g.cs"));
            Assert.Contains("DdsIdlMapping(\"KeywordA.class.struct\", \"Types\", \"KeywordA::class\")", map);
        }

        [Fact]
        public void KeywordAndUnderscoreNames_PassIdlc()
        {
            // Keywords in every position, referenced across modules and against idlc's real parser
            var folder = CreateProject("Keywords", ("Types.cs", @"
using CycloneDDS.Schema;
namespace Company.Public.Module {
    public enum Enum { Case, _Default }
    [DdsStruct] public partial struct Map { public int _value; public Enum Struct; }
    [DdsStruct] public partial struct _Under { public Map Sequence; private int __hidden; }
}"));

            CompileProject("Keywords", folder);

            var idl = File.ReadAllText(Path.Combine(GeneratedDir(folder), "Types.idl"));
            Assert.Contains("module _Public {", idl);
            Assert.Contains("module _Module {", idl);
            Assert.Contains("struct _Map {", idl);
            Assert.Contains("enum _Enum {", idl);
            Assert.Contains("struct Under {", idl);
            Assert.Contains("::Company::_Public::_Module::_Map _Sequence;", idl);
            Assert.Contains("int32 value;", idl);
            Assert.Contains("int32 hidden;", idl);

            // The DDS type name carries no escapes
            var generated = File.ReadAllText(Path.Combine(GeneratedDir(folder), "Company.Public.Module.Map.g.cs"));
            Assert.Contains("\"Company::Public::Module::Map\"", generated);
        }

        [Fact]
        public void ProjectBelowFolderNamedLikeAnExclusion_KeepsItsSources()
        {
            var folder = Path.Combine(TempDir, "Generated", "App");
            Directory.CreateDirectory(Path.Combine(folder, "obj"));
            File.WriteAllText(Path.Combine(folder, "Kept.cs"), "namespace App { [CycloneDDS.Schema.DdsStruct] public partial struct Kept { public int X; } }");
            File.WriteAllText(Path.Combine(folder, "obj", "Skipped.cs"), "namespace App { [CycloneDDS.Schema.DdsStruct] public partial struct Skipped { public int X; } }");

            var schema = CycloneDDS.CodeGen.Discovery.SchemaDiscovery.Discover(folder, null);

            Assert.Equal(["Kept"], System.Linq.Enumerable.Select(schema.Structs, s => s.Name));
        }
    }
}
