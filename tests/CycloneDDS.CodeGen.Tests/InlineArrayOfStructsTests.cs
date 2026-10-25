using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using CycloneDDS.CodeGen;
using CycloneDDS.CodeGen.Emitters;

namespace CycloneDDS.CodeGen.Tests
{
    /// <summary>
    /// Tests for fixed-size arrays of structs. C# <c>fixed</c> buffers only accept primitive element
    /// types, so the native struct backs these fields with a generated <c>[InlineArray(N)]</c> buffer
    /// type and marshals them element by element.
    /// </summary>
    public class InlineArrayOfStructsTests : CodeGenTestBase, IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDir;

        public InlineArrayOfStructsTests(ITestOutputHelper output)
        {
            _output = output;
            _tempDir = Path.Combine(Path.GetTempPath(), "CG_IAS_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>A static (fully inline) element struct.</summary>
        private static TypeInfo MakePointType() => new()
        {
            Name = "Point",
            Namespace = "TestIA",
            IsStruct = true,
            Fields = new List<FieldInfo>
            {
                new FieldInfo { Name = "X", TypeName = "double" },
                new FieldInfo { Name = "Y", TypeName = "double" }
            }
        };

        /// <summary>An element struct with a dynamic (arena-allocated) payload.</summary>
        private static TypeInfo MakeTagType() => new()
        {
            Name = "Tag",
            Namespace = "TestIA",
            IsStruct = true,
            Fields = new List<FieldInfo>
            {
                new FieldInfo { Name = "Name", TypeName = "string" },
                new FieldInfo { Name = "Enabled", TypeName = "bool" }
            }
        };

        private static FieldInfo MakeStructBufferField(string name, TypeInfo elementType, int length) => new()
        {
            Name = name,
            TypeName = elementType.FullName,
            Type = elementType,
            IsFixedSizeBuffer = true,
            IsInlineArray = true,
            FixedSize = length
        };

        private static (string Code, GlobalTypeRegistry Registry) EmitOwner(TypeInfo owner, params TypeInfo[] elementTypes)
        {
            var registry = new GlobalTypeRegistry();
            foreach (var t in elementTypes) registry.RegisterLocal(t, t.Name + ".cs", t.Name, t.Namespace);
            registry.RegisterLocal(owner, owner.Name + ".cs", owner.Name, owner.Namespace);

            var emitter = new SerializerEmitter();
            return (emitter.EmitSerializer(owner, registry), registry);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 1. Native struct uses [InlineArray], not a fixed buffer
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GhostStruct_StructElement_UsesInlineArrayBuffer()
        {
            var point = MakePointType();
            var owner = new TypeInfo
            {
                Name = "Cloud",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo> { MakeStructBufferField("Corners", point, 4) }
            };

            var (code, _) = EmitOwner(owner, point);
            _output.WriteLine(code);

            Assert.Contains("public Corners_Array Corners;", code);
            Assert.Contains("[InlineArray(4)]", code);
            Assert.Contains("public struct Corners_Array", code);
            Assert.Contains("private TestIA.Point_Native _element0;", code);
            // 'fixed' is illegal for struct element types.
            Assert.DoesNotContain("public fixed TestIA.Point", code);
        }

        [Fact]
        public void GhostStruct_PrimitiveElement_StillUsesFixedBuffer()
        {
            var owner = new TypeInfo
            {
                Name = "Samples",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo>
                {
                    new FieldInfo { Name = "Values", TypeName = "float", IsFixedSizeBuffer = true, FixedSize = 8 }
                }
            };

            var (code, _) = EmitOwner(owner);

            Assert.Contains("public fixed float Values[8];", code);
            Assert.DoesNotContain("[InlineArray(", code);
        }

        [Fact]
        public void GhostStruct_UnionStructElement_UsesInlineArrayBuffer()
        {
            var point = MakePointType();
            var owner = new TypeInfo
            {
                Name = "Shape",
                Namespace = "TestIA",
                IsStruct = true,
                IsUnion = true,
                Attributes = new List<AttributeInfo> { new AttributeInfo { Name = "DdsUnion" } },
                Fields = new List<FieldInfo>
                {
                    new FieldInfo
                    {
                        Name = "Kind", TypeName = "int",
                        Attributes = new List<AttributeInfo> { new AttributeInfo { Name = "DdsDiscriminator" } }
                    },
                    new FieldInfo
                    {
                        Name = "Quad", TypeName = point.FullName, Type = point,
                        IsFixedSizeBuffer = true, IsInlineArray = true, FixedSize = 4,
                        Attributes = new List<AttributeInfo>
                        {
                            new AttributeInfo { Name = "DdsCase", Arguments = new List<object> { 1 } }
                        }
                    }
                }
            };

            var (code, _) = EmitOwner(owner, point);
            _output.WriteLine(code);

            Assert.Contains("public Quad_Array Quad;", code);
            Assert.Contains("[InlineArray(4)]", code);
            Assert.DoesNotContain("public fixed TestIA.Point", code);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 2. Marshalling is per element, not a memcpy
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Marshal_StructElement_EmitsPerElementLoop()
        {
            var tag = MakeTagType();
            var owner = new TypeInfo
            {
                Name = "Holder",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo> { MakeStructBufferField("Tags", tag, 2) }
            };

            var (code, _) = EmitOwner(owner, tag);
            _output.WriteLine(code);

            Assert.Contains("TestIA.Tag.MarshalToNative(in __item, ref target.Tags[__i], ref arena);", code);
            Assert.Contains("TestIA.Tag.MarshalFromNative(ref target.Tags[__i], in source.Tags[__i]);", code);
            Assert.DoesNotContain("MemoryCopy", code);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 3. Sizer accounts for dynamic element payloads
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Sizer_DynamicStructElement_AddsPerElementDynamicSize()
        {
            var tag = MakeTagType();
            var owner = new TypeInfo
            {
                Name = "Holder",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo> { MakeStructBufferField("Tags", tag, 2) }
            };

            var (code, _) = EmitOwner(owner, tag);

            Assert.Contains("size += GetDynamicSize(source);", code);
            Assert.Contains("TestIA.Tag.GetNativeSize(source.Tags[__i]) - Unsafe.SizeOf<TestIA.Tag_Native>();", code);
        }

        [Fact]
        public void Sizer_StaticStructElement_HasNoDynamicSize()
        {
            var point = MakePointType();
            var owner = new TypeInfo
            {
                Name = "Cloud",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo> { MakeStructBufferField("Corners", point, 4) }
            };

            var (code, _) = EmitOwner(owner, point);

            Assert.DoesNotContain("GetDynamicSize", code);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 4. View exposes count + per-element view
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void View_StructElement_EmitsCountAndElementView()
        {
            var point = MakePointType();
            var owner = new TypeInfo
            {
                Name = "Cloud",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo> { MakeStructBufferField("Corners", point, 4) }
            };

            var registry = new GlobalTypeRegistry();
            registry.RegisterLocal(point, "Point.cs", "Point", "TestIA");
            registry.RegisterLocal(owner, "Cloud.cs", "Cloud", "TestIA");

            var code = new ViewEmitter().EmitViewStruct(owner, registry);
            _output.WriteLine(code);

            Assert.Contains("public int CornersCount => 4;", code);
            Assert.Contains("public unsafe TestIA.PointView GetCorners(int index)", code);
            Assert.Contains("target.Corners[__i] = this.GetCorners(__i).ToManaged();", code);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 5. SchemaDiscovery detects an [InlineArray] of structs
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void SchemaDiscovery_InlineArrayOfStructs_IsDetected()
        {
            File.WriteAllText(Path.Combine(_tempDir, "Src.cs"), @"
using CycloneDDS.Schema;
using System.Runtime.CompilerServices;
namespace Scan
{
    [DdsStruct]
    public partial struct Point
    {
        public double X;
        public double Y;
    }

    [InlineArray(4)]
    public partial struct Point4 { private Point _e0; }

    [DdsStruct]
    public partial struct Cloud
    {
        [DdsKey] public int Id;
        public Point4 Corners;
    }
}");
            var types = new SchemaDiscovery().DiscoverTopics(_tempDir);
            var cloud = types.First(t => t.Name == "Cloud");
            var corners = cloud.Fields.First(f => f.Name == "Corners");

            Assert.True(corners.IsFixedSizeBuffer);
            Assert.True(corners.IsInlineArray);
            Assert.Equal(4, corners.FixedSize);
            Assert.Equal("Scan.Point", corners.TypeName);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 6. End-to-end: generated code compiles and round-trips
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void RoundTrip_StructArrayWithDynamicElements_Succeeds()
        {
            var tag = MakeTagType();
            var point = MakePointType();
            var owner = new TypeInfo
            {
                Name = "Holder",
                Namespace = "TestIA",
                IsStruct = true,
                Fields = new List<FieldInfo>
                {
                    new FieldInfo { Name = "Id", TypeName = "int" },
                    MakeStructBufferField("Tags", tag, 2),
                    MakeStructBufferField("Corners", point, 2)
                }
            };

            var registry = new GlobalTypeRegistry();
            registry.RegisterLocal(tag, "Tag.cs", "Tag", "TestIA");
            registry.RegisterLocal(point, "Point.cs", "Point", "TestIA");
            registry.RegisterLocal(owner, "Holder.cs", "Holder", "TestIA");

            var serializer = new SerializerEmitter();
            var views = new ViewEmitter();

            var declarations = @"
using System.Runtime.CompilerServices;
namespace TestIA
{
    public partial struct Tag { public string Name; public bool Enabled; }
    public partial struct Point { public double X; public double Y; }

    [InlineArray(2)] public struct Tag2 { private Tag _e0; }
    [InlineArray(2)] public struct Point2 { private Point _e0; }

    public partial struct Holder
    {
        public int Id;
        public Tag2 Tags;
        public Point2 Corners;
    }
}
";

            var runner = @"
using System;
using CycloneDDS.Core;
using CycloneDDS.Runtime;

namespace TestIA
{
    public static class TestRunner
    {
        public static unsafe string Run()
        {
            try
            {
                var input = new Holder { Id = 7 };
                input.Tags[0] = new Tag { Name = ""alpha"", Enabled = true };
                input.Tags[1] = new Tag { Name = ""beta"", Enabled = false };
                input.Corners[0] = new Point { X = 1.5, Y = 2.5 };
                input.Corners[1] = new Point { X = 3.5, Y = 4.5 };

                int total = Holder.GetNativeSize(in input);
                byte[] buffer = new byte[total + 64];
                fixed (byte* ptr = buffer)
                {
                    var arena = new NativeArena(new Span<byte>(buffer), (IntPtr)ptr, Holder.GetNativeHeadSize());
                    Holder.MarshalToNative(in input, (IntPtr)ptr, ref arena);

                    var back = Holder.FromNative((IntPtr)ptr);
                    if (back.Id != 7) return ""Id mismatch: "" + back.Id;
                    if (back.Tags[0].Name != ""alpha"" || !back.Tags[0].Enabled) return ""Tags[0] mismatch"";
                    if (back.Tags[1].Name != ""beta"" || back.Tags[1].Enabled) return ""Tags[1] mismatch"";
                    if (back.Corners[1].X != 3.5 || back.Corners[1].Y != 4.5) return ""Corners[1] mismatch"";

                    var view = new HolderView((Holder_Native*)ptr);
                    if (view.TagsCount != 2) return ""TagsCount mismatch"";
                    if (view.GetTags(0).Name != ""alpha"") return ""View Tags[0] mismatch"";
                    if (view.GetCorners(1).X != 3.5) return ""View Corners[1] mismatch"";

                    var managed = view.ToManaged();
                    if (managed.Tags[1].Name != ""beta"") return ""ToManaged Tags[1] mismatch"";
                    if (managed.Corners[0].Y != 2.5) return ""ToManaged Corners[0] mismatch"";

                    // The buffer must have been large enough for the two strings.
                    if (total <= Holder.GetNativeHeadSize()) return ""Dynamic size not accounted for"";
                }
                return ""SUCCESS"";
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }
    }
}
";

            var assembly = CompileToAssembly(
                "InlineArrayStructRoundTrip",
                declarations,
                serializer.EmitSerializer(tag, registry),
                serializer.EmitSerializer(point, registry),
                serializer.EmitSerializer(owner, registry),
                views.EmitViewStruct(tag, registry),
                views.EmitViewStruct(point, registry),
                views.EmitViewStruct(owner, registry),
                runner);

            var result = assembly.GetType("TestIA.TestRunner")!
                                 .GetMethod("Run")!
                                 .Invoke(null, null) as string;

            _output.WriteLine(result);
            Assert.Equal("SUCCESS", result);
        }
    }
}
