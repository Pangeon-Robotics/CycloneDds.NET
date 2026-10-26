namespace CycloneDDS.CodeGen.Model
{
    /// <summary>
    /// A DDS type known to the generator: a <c>[DdsStruct]</c> or an enum, declared in the
    /// compiled sources (local) or in a referenced assembly (external).
    /// </summary>
    public abstract class NamedType
    {
        /// <summary>Simple C# (and IDL) type name.</summary>
        public required string Name { get; init; }

        /// <summary>C# namespace, empty for the global namespace.</summary>
        public required string Namespace { get; init; }

        /// <summary>IDL module path using <c>::</c>, empty for the global scope.</summary>
        public required string IdlModule { get; init; }

        /// <summary>IDL file the type is declared in, without extension.</summary>
        public required string IdlFile { get; init; }

        /// <summary>Declared in a referenced assembly; its IDL and serializer already exist.</summary>
        public bool IsExternal { get; init; }

        /// <summary>Fully qualified C# name, usable in generated code.</summary>
        public string CSharpReference => Namespace.Length == 0 ? $"global::{Name}" : $"global::{Namespace}.{Name}";

        /// <summary>C# full name without the <c>global::</c> prefix, as recorded in <c>[DdsIdlMapping]</c>.</summary>
        public string CSharpFullName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";

        /// <summary>Scoped IDL name without leading <c>::</c>; also the DDS type name.</summary>
        public string IdlName => IdlModule.Length == 0 ? Name : $"{IdlModule}::{Name}";
    }

    /// <summary>A <c>[DdsStruct]</c> type, emitted as an IDL <c>@final</c> struct.</summary>
    public sealed class StructType : NamedType
    {
        /// <summary>C# declaration keywords for the generated partial, e.g. <c>struct</c> or <c>record class</c>.</summary>
        public string DeclarationKind { get; init; } = "struct";

        /// <summary>Members in declaration order; empty for external types.</summary>
        public List<Field> Fields { get; } = [];
    }

    /// <summary>An enum, emitted as an IDL enum with a bit bound matching its underlying type.</summary>
    public sealed class EnumType : NamedType
    {
        /// <summary>Underlying C# integer type, which fixes the wire size.</summary>
        public required PrimitiveKind Underlying { get; init; }

        public List<EnumMember> Members { get; } = [];

        /// <summary>IDL <c>@bit_bound</c>: the underlying type's width in bits.</summary>
        public int BitBound => Underlying.Size() * 8;
    }

    public sealed record EnumMember(string Name, long Value);

    /// <summary>A struct member. <see cref="IsProperty"/> distinguishes auto-properties from fields.</summary>
    public sealed record Field(string Name, Shape Shape, bool IsProperty);


    // ---- Shapes: the wire form of a member, resolved once from the C# type ---------

    public abstract record Shape;

    public sealed record PrimitiveShape(PrimitiveKind Kind) : Shape;

    public sealed record EnumShape(EnumType Type) : Shape;

    /// <param name="Bound">Maximum length, 0 for unbounded.</param>
    /// <param name="Wide">UTF-16 <c>wstring</c> rather than UTF-8 <c>string</c>.</param>
    public sealed record StringShape(int Bound, bool Wide) : Shape;

    public sealed record StructShape(StructType Type) : Shape;

    /// <summary>Variable-length collection (<c>sequence&lt;T&gt;</c>).</summary>
    /// <param name="Bound">Maximum element count, 0 for unbounded.</param>
    public sealed record SequenceShape(Shape Element, int Bound, CollectionKind Kind) : Shape;

    /// <summary>Fixed-length collection (<c>T name[N]</c>).</summary>
    public sealed record ArrayShape(Shape Element, int Length, CollectionKind Kind) : Shape;

    /// <summary>The C# container a collection member is declared as.</summary>
    public enum CollectionKind
    {
        List,
        Array,
        InlineArray,
    }

    /// <summary>C# primitive types with a direct IDL equivalent.</summary>
    public enum PrimitiveKind
    {
        Bool,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
    }

    public static class PrimitiveKindExtensions
    {
        /// <summary>Serialized size in bytes, which is also the XCDR1 alignment.</summary>
        public static int Size(this PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool or PrimitiveKind.Byte or PrimitiveKind.SByte => 1,
            PrimitiveKind.Int16 or PrimitiveKind.UInt16 => 2,
            PrimitiveKind.Int32 or PrimitiveKind.UInt32 or PrimitiveKind.Single => 4,
            _ => 8,
        };

        public static string CSharpKeyword(this PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool => "bool",
            PrimitiveKind.Byte => "byte",
            PrimitiveKind.SByte => "sbyte",
            PrimitiveKind.Int16 => "short",
            PrimitiveKind.UInt16 => "ushort",
            PrimitiveKind.Int32 => "int",
            PrimitiveKind.UInt32 => "uint",
            PrimitiveKind.Int64 => "long",
            PrimitiveKind.UInt64 => "ulong",
            PrimitiveKind.Single => "float",
            _ => "double",
        };

        public static string IdlName(this PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool => "boolean",
            PrimitiveKind.Byte => "octet",
            PrimitiveKind.SByte => "int8",
            PrimitiveKind.Int16 => "int16",
            PrimitiveKind.UInt16 => "uint16",
            PrimitiveKind.Int32 => "int32",
            PrimitiveKind.UInt32 => "uint32",
            PrimitiveKind.Int64 => "int64",
            PrimitiveKind.UInt64 => "uint64",
            PrimitiveKind.Single => "float",
            _ => "double",
        };

        /// <summary>Suffix of the matching <c>CdrWriter.Write*</c> / <c>CdrReader.Read*</c> methods.</summary>
        public static string CdrMethodSuffix(this PrimitiveKind kind) => kind.ToString();
    }
}
