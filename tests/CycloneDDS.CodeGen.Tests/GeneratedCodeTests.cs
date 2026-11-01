using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace CycloneDDS.CodeGen.Tests
{
    /// <summary>
    /// Generates, compiles and runs the type support of a project covering every shape. The
    /// project carries its own probe class, since IDdsType members are static abstract and
    /// cannot be invoked through reflection on the interface.
    /// </summary>
    public class GeneratedCodeTests : CodeGenTestBase
    {
        private const string Types = @"
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Schema;
namespace Gen
{
    public enum Color : byte { Red, Blue = 3 }
    [InlineArray(2)] public struct Two { private double _e; }

    [DdsStruct] public partial struct Point { public int X; public string Name; }

    [DdsStruct] public partial class Msg
    {
        public bool Flag; public byte U8; public long I64; public double F64;
        public Color Color;
        [DdsBound(4)] public string Short;
        [DdsWideString] public string Wide;
        public Point Nested;
        public List<int> Ints;
        public string[] Names;
        public List<Point> Points;
        public List<bool> Bools;
        [ArrayLength(3)] public float[] Fixed;
        public Two Inline;
        public List<Color> Colors { get; set; }
    }

    [DdsStruct] public partial record struct Rec { public int A { get; set; } }
}";

        private const string Probe = @"
using System;
using System.Linq;
using CycloneDDS.Core;
namespace Gen
{
    public static class Probe
    {
        public static byte[] Bytes<T>(T value) where T : IDdsType<T>
        {
            var writer = new CdrWriter(16);
            try { T.Serialize(ref writer, in value); return writer.WrittenSpan.ToArray(); }
            finally { writer.Dispose(); }
        }

        public static T Read<T>(byte[] bytes) where T : IDdsType<T>
        {
            var reader = new CdrReader(bytes);
            return T.Deserialize(ref reader);
        }

        public static string TypeName<T>() where T : IDdsType<T> => T.Descriptor.TypeName;

        public static byte[] PointBytes() => Bytes(new Point { X = 1, Name = ""ab"" });

        public static string MsgTypeName() => TypeName<Msg>();

        public static Rec RecordRoundTrip() => Read<Rec>(Bytes(new Rec { A = 9 }));

        /// <summary>Round-trips a populated Msg; returns the names of members that differ.</summary>
        public static string MsgRoundTripMismatches()
        {
            var sent = new Msg
            {
                Flag = true, U8 = 200, I64 = -7, F64 = 0.25, Color = Color.Blue, Short = ""abcd"", Wide = ""w\U0001F600"",
                Nested = new Point { X = 5, Name = ""n"" }, Ints = [1, 2], Names = [""a"", """"],
                Points = [new Point { X = 1, Name = ""p"" }], Bools = [true, false], Fixed = [1, 2, 3],
                Colors = [Color.Red, Color.Blue],
            };
            sent.Inline[0] = 1.5; sent.Inline[1] = -1.5;

            var got = Read<Msg>(Bytes(sent));
            var bad = new System.Collections.Generic.List<string>();
            if (got.Flag != sent.Flag || got.U8 != sent.U8 || got.I64 != sent.I64 || got.F64 != sent.F64) bad.Add(""primitives"");
            if (got.Color != sent.Color) bad.Add(""Color"");
            if (got.Short != sent.Short || got.Wide != sent.Wide) bad.Add(""strings"");
            if (!got.Nested.Equals(sent.Nested)) bad.Add(""Nested"");
            if (!got.Ints.SequenceEqual(sent.Ints)) bad.Add(""Ints"");
            if (!got.Names.SequenceEqual(sent.Names)) bad.Add(""Names"");
            if (!got.Points.SequenceEqual(sent.Points)) bad.Add(""Points"");
            if (!got.Bools.SequenceEqual(sent.Bools)) bad.Add(""Bools"");
            if (!got.Fixed.SequenceEqual(sent.Fixed)) bad.Add(""Fixed"");
            if (got.Inline[0] != 1.5 || got.Inline[1] != -1.5) bad.Add(""Inline"");
            if (!got.Colors.SequenceEqual(sent.Colors)) bad.Add(""Colors"");
            return string.Join("","", bad);
        }
    }
}";

        private Type CompileProbe()
        {
            var folder = CreateProject("Gen", ("Types.cs", Types), ("Probe.cs", Probe));
            // Each test compiles its own "Gen"; a private context keeps them apart while still
            // sharing CycloneDDS.Core with this test assembly through the default context
            var assembly = new AssemblyLoadContext(null, isCollectible: true).LoadFromAssemblyPath(CompileProject("Gen", folder));
            return assembly.GetType("Gen.Probe")!;
        }

        private static object? Invoke(Type probe, string method) => probe.GetMethod(method)!.Invoke(null, null);

        [Fact]
        public void GeneratedCode_RoundTripsEveryShape()
        {
            Assert.Equal("", Invoke(CompileProbe(), "MsgRoundTripMismatches"));
        }

        [Fact]
        public void GeneratedCode_WritesExactXcdr1()
        {
            byte[] expected =
            [
                0x00, 0x01, 0x00, 0x00,                 // CDR_LE header
                0x01, 0x00, 0x00, 0x00,                 // X = 1
                0x03, 0x00, 0x00, 0x00, (byte)'a', (byte)'b', 0x00, // Name = "ab"
            ];
            Assert.Equal(expected, (byte[])Invoke(CompileProbe(), "PointBytes")!);
        }

        [Fact]
        public void GeneratedCode_RecordStructRoundTrips()
        {
            var probe = CompileProbe();
            object record = Invoke(probe, "RecordRoundTrip")!;
            Assert.Equal(9, record.GetType().GetProperty("A")!.GetValue(record));
        }

        [Fact]
        public void Descriptor_TypeNameIsIdlName()
        {
            Assert.Equal("Gen::Msg", Invoke(CompileProbe(), "MsgTypeName"));
        }

        [Fact]
        public void Regeneration_IsDeterministic_AndLeavesUnchangedFilesAlone()
        {
            var folder = CreateProject("Stable", ("Types.cs", Types));
            string output = GeneratedDir(folder);

            new CodeGenerator().Generate(folder, output, [typeof(CycloneDDS.Schema.DdsStructAttribute).Assembly.Location]);
            var first = Directory.GetFiles(output).ToDictionary(f => f, f => (File.ReadAllText(f), File.GetLastWriteTimeUtc(f)));

            new CodeGenerator().Generate(folder, output, [typeof(CycloneDDS.Schema.DdsStructAttribute).Assembly.Location]);
            foreach (var (file, (text, written)) in first)
            {
                Assert.Equal(text, File.ReadAllText(file));
                Assert.Equal(written, File.GetLastWriteTimeUtc(file));
            }
        }

        [Fact]
        public void Regeneration_RemovesOutputOfDeletedTypes()
        {
            var folder = CreateProject("Shrinking", ("Types.cs", @"
using CycloneDDS.Schema;
namespace S { [DdsStruct] public partial struct Kept { public int X; } [DdsStruct] public partial struct Dropped { public int X; } }"));
            string output = GeneratedDir(folder);
            string[] references = [typeof(CycloneDDS.Schema.DdsStructAttribute).Assembly.Location];

            new CodeGenerator().Generate(folder, output, references);
            Assert.True(File.Exists(Path.Combine(output, "S.Dropped.g.cs")));

            File.WriteAllText(Path.Combine(folder, "Types.cs"), @"
using CycloneDDS.Schema;
namespace S { [DdsStruct] public partial struct Kept { public int X; } }");
            new CodeGenerator().Generate(folder, output, references);

            Assert.False(File.Exists(Path.Combine(output, "S.Dropped.g.cs")));
            Assert.True(File.Exists(Path.Combine(output, "S.Kept.g.cs")));
        }
    }
}
