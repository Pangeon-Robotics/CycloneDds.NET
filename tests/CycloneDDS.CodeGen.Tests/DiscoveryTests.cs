using System.Linq;
using CycloneDDS.CodeGen.Model;
using Xunit;

namespace CycloneDDS.CodeGen.Tests
{
    public class DiscoveryShapeTests : CodeGenTestBase
    {
        private static Shape ShapeOf(string member, string declarations)
        {
            var schema = DiscoverValid($@"
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Schema;
namespace T
{{
    public enum Small : byte {{ A, B }}
    [InlineArray(3)] public struct Three {{ private float _e; }}
    [DdsStruct] public partial struct Other {{ public int X; }}
    [DdsStruct] public partial struct S
    {{
        {declarations}
    }}
}}");
            return schema.Structs.Single(s => s.Name == "S").Fields.Single(f => f.Name == member).Shape;
        }

        [Theory]
        [InlineData("bool", PrimitiveKind.Bool)]
        [InlineData("byte", PrimitiveKind.Byte)]
        [InlineData("sbyte", PrimitiveKind.SByte)]
        [InlineData("short", PrimitiveKind.Int16)]
        [InlineData("ushort", PrimitiveKind.UInt16)]
        [InlineData("int", PrimitiveKind.Int32)]
        [InlineData("uint", PrimitiveKind.UInt32)]
        [InlineData("long", PrimitiveKind.Int64)]
        [InlineData("ulong", PrimitiveKind.UInt64)]
        [InlineData("float", PrimitiveKind.Single)]
        [InlineData("double", PrimitiveKind.Double)]
        public void Primitive(string csharp, PrimitiveKind expected)
        {
            Assert.Equal(new PrimitiveShape(expected), ShapeOf("M", $"public {csharp} M;"));
        }

        [Fact]
        public void Strings()
        {
            Assert.Equal(new StringShape(0, false), ShapeOf("M", "public string M;"));
            Assert.Equal(new StringShape(5, false), ShapeOf("M", "[DdsBound(5)] public string M;"));
            Assert.Equal(new StringShape(5, true), ShapeOf("M", "[DdsBound(5), DdsWideString] public string M;"));
        }

        [Fact]
        public void Sequences()
        {
            var i32 = new PrimitiveShape(PrimitiveKind.Int32);
            Assert.Equal(new SequenceShape(i32, 0, CollectionKind.List), ShapeOf("M", "public List<int> M;"));
            Assert.Equal(new SequenceShape(i32, 4, CollectionKind.List), ShapeOf("M", "[DdsBound(4)] public List<int> M;"));
            Assert.Equal(new SequenceShape(i32, 0, CollectionKind.Array), ShapeOf("M", "public int[] M;"));
            Assert.Equal(new SequenceShape(new StringShape(3, true), 2, CollectionKind.List),
                ShapeOf("M", "[DdsBound(2), DdsElementBound(3), DdsWideString] public List<string> M;"));
        }

        [Fact]
        public void FixedArrays()
        {
            Assert.Equal(new ArrayShape(new PrimitiveShape(PrimitiveKind.Double), 9, CollectionKind.Array),
                ShapeOf("M", "[ArrayLength(9)] public double[] M;"));
            Assert.Equal(new ArrayShape(new PrimitiveShape(PrimitiveKind.Single), 3, CollectionKind.InlineArray),
                ShapeOf("M", "public Three M;"));
        }

        [Fact]
        public void NamedTypes()
        {
            var enumShape = Assert.IsType<EnumShape>(ShapeOf("M", "public Small M;"));
            Assert.Equal(8, enumShape.Type.BitBound);
            Assert.Equal("T::Small", enumShape.Type.IdlName);

            var structShape = Assert.IsType<StructShape>(ShapeOf("M", "public Other M;"));
            Assert.Equal("T::Other", structShape.Type.IdlName);
            Assert.False(structShape.Type.IsExternal);
        }

        [Fact]
        public void Members_AreFieldsAndAutoProperties_InDeclarationOrder()
        {
            var schema = DiscoverValid(@"
using CycloneDDS.Schema;
namespace T
{
    [DdsStruct] public partial class C
    {
        public const int Constant = 1;
        public static int Static;
        public int A;
        public int B { get; set; }
        public int Computed => A + B;
        private int _c;
    }
}");
            var fields = schema.Structs.Single().Fields;
            Assert.Equal(["A", "B", "_c"], fields.Select(f => f.Name));
            Assert.Equal([false, true, false], fields.Select(f => f.IsProperty));
        }

        [Fact]
        public void IdlPlacement_DefaultsToFileAndNamespace_UnlessOverridden()
        {
            var schema = DiscoverValid(@"
using CycloneDDS.Schema;
namespace A.B
{
    [DdsStruct] public partial struct Default { public int X; }
    [DdsStruct, DdsIdlFile(""Custom""), DdsIdlModule(""pkg::msg::dds_"")] public partial struct Overridden { public int X; }
}");
            var byName = schema.Structs.ToDictionary(s => s.Name);
            Assert.Equal(("File0", "A::B"), (byName["Default"].IdlFile, byName["Default"].IdlModule));
            Assert.Equal(("Custom", "pkg::msg::dds_::Overridden"), (byName["Overridden"].IdlFile, byName["Overridden"].IdlName));
        }

        [Fact]
        public void Enums_TopLevelRepresentableOnly_AreDiscovered()
        {
            var schema = DiscoverValid(@"
namespace T
{
    public enum Plain { A, B }
    public enum Negative { A = -1 }
    public enum Big : long { A }
    public enum Aliased { A = 1, B = 1 }
    public static class Holder { public enum Nested { A } }
}");
            Assert.Equal(["Plain"], schema.Enums.Select(e => e.Name));
        }
    }

    public class DiscoveryDiagnosticTests : CodeGenTestBase
    {
        private static string ErrorsOf(string body, string preamble = "")
        {
            var schema = Discover($@"
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Schema;
namespace T
{{
    {preamble}
    {body}
}}");
            Assert.True(schema.HasErrors, "expected a diagnostic");
            return string.Join("\n", schema.Diagnostics);
        }

        [Theory]
        [InlineData("[DdsStruct] public struct S { public int X; }", "partial")]
        [InlineData("public static partial class Outer { [DdsStruct] public partial struct S { public int X; } }", "nested")]
        [InlineData("[DdsStruct] public partial struct S<TArg> { public int X; }", "generic")]
        [InlineData("[DdsStruct] public readonly partial struct S { public readonly int X; }", "readonly")]
        [InlineData("[DdsStruct] public abstract partial class S { public int X; }", "abstract")]
        [InlineData("public class Base { } [DdsStruct] public partial class S : Base { public int X; }", "inheritance")]
        [InlineData("[DdsStruct] public partial class S { public S(int x) { X = x; } public int X; }", "parameterless constructor")]
        [InlineData("[DdsStruct] public partial struct S { }", "no members")]
        public void InvalidDeclaration(string body, string expected)
        {
            Assert.Contains(expected, ErrorsOf(body));
        }

        [Theory]
        [InlineData("public readonly int X;", "readonly")]
        [InlineData("public int X { get; init; }", "setter")]
        [InlineData("public int X { get; }", "setter")]
        [InlineData("public unsafe fixed byte X[4];", "fixed buffer")]
        [InlineData("public int? X;", "optional")]
        [InlineData("public char X;", "char")]
        [InlineData("public DateTime X;", "not a DDS type")]
        [InlineData("public Dictionary<int, int> X;", "not a DDS type")]
        [InlineData("public List<List<int>> X;", "collection of collections")]
        [InlineData("public int[,] X;", "not a DDS type")]
        [InlineData("[DdsBound(3)] public int X;", "[DdsBound]")]
        [InlineData("[DdsBound(3), ArrayLength(2)] public int[] X;", "[DdsBound]")]
        [InlineData("[ArrayLength(3)] public List<int> X;", "[ArrayLength]")]
        [InlineData("[DdsElementBound(3)] public List<int> X;", "[DdsElementBound]")]
        [InlineData("[DdsWideString] public int X;", "[DdsWideString]")]
        [InlineData("public int X; public int x;", "only in case")]
        [InlineData("public int s;", "only in case")]
        public void InvalidMember(string member, string expected)
        {
            Assert.Contains(expected, ErrorsOf($"[DdsStruct] public unsafe partial struct S {{ {member} }}"));
        }

        [Fact]
        public void InlineArrayProperty_IsRejected()
        {
            Assert.Contains("[InlineArray] property",
                ErrorsOf("[DdsStruct] public partial struct S { public Three X { get; set; } }", "[InlineArray(3)] public struct Three { private int _e; }"));
        }

        [Fact]
        public void ReferencedUnrepresentableEnum_IsRejected_WithReason()
        {
            var errors = ErrorsOf("[DdsStruct] public partial struct S { public Negative X; }", "public enum Negative { A = -1 }");
            Assert.Contains("Negative", errors);
            Assert.Contains("negative values", errors);
        }

        [Fact]
        public void NestedEnum_IsRejected()
        {
            Assert.Contains("nested in a type",
                ErrorsOf("[DdsStruct] public partial struct S { public Holder.E X; }", "public static class Holder { public enum E { A } }"));
        }

        [Fact]
        public void RecursiveTypes_AreRejected()
        {
            var errors = ErrorsOf(@"
[DdsStruct] public partial struct A { public List<B> Bs; }
[DdsStruct] public partial struct B { public List<A> As; }");
            Assert.Contains("Recursive", errors);
            Assert.Contains("A -> B -> A", errors);
        }

        [Theory]
        [InlineData("[DdsIdlModule(\"a.b\")]", "[DdsIdlModule")]
        [InlineData("[DdsIdlModule(\"a::\")]", "[DdsIdlModule")]
        [InlineData("[DdsIdlFile(\"x.idl\")]", "[DdsIdlFile")]
        public void InvalidIdlPlacement(string attribute, string expected)
        {
            Assert.Contains(expected, ErrorsOf($"[DdsStruct] {attribute} public partial struct S {{ public int X; }}"));
        }

        [Fact]
        public void Diagnostics_UseCanonicalMsBuildFormat_AtTheMember()
        {
            var errors = ErrorsOf("[DdsStruct] public partial struct S {\n public char X; }");
            Assert.Matches(@"File0\.cs\(\d+,\d+\): error CDDS0001: ", errors);
        }
    }
}
