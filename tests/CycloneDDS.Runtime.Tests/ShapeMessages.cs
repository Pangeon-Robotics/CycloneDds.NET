using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests.Shapes
{
    // Invalid lies above IDL's signed 8-bit range, so it travels as the same bits declared signed
    [DdsEnum]
    public enum Color : byte { Red, Green, Blue = 7, Invalid = byte.MaxValue }

    [DdsEnum]
    public enum Wide { None, Small = 1, Large = 100_000 }

    [InlineArray(4)]
    public struct Four<T> { private T _element; }

    [DdsStruct]
    public partial struct Inner
    {
        public int Id;
        public string Label;
    }

    /// <summary>One member of every shape the generator supports.</summary>
    [DdsStruct]
    public partial class AllShapes
    {
        public bool Flag;
        public byte U8;
        public sbyte I8;
        public short I16;
        public ushort U16;
        public int I32;
        public uint U32;
        public long I64;
        public ulong U64;
        public float F32;
        public double F64;

        public Color Color;
        public Wide WideEnum;

        public string Text;
        [DdsBound(8)] public string BoundedText;
        [DdsWideString] public string WideText;

        public Inner Nested;

        public List<int> IntList;
        public double[] DoubleSequence;
        [DdsBound(3)] public List<short> BoundedShorts;
        public List<bool> Bools;
        public List<string> Strings;
        [DdsElementBound(4), DdsBound(2)] public string[] BoundedStrings;
        [DdsWideString] public List<string> WideStrings;
        public List<Inner> Structs;
        public List<Color> Colors;

        [ArrayLength(3)] public float[] FixedFloats;
        [ArrayLength(2)] public Inner[] FixedStructs;
        public Four<double> InlineDoubles;
        public Four<Color> InlineColors;

        public int Property { get; set; }
        public List<long> ListProperty { get; set; }
    }

    [DdsStruct]
    public partial record struct RecordMessage
    {
        public int A { get; set; }
        public string B { get; set; }
    }

    [DdsStruct]
    public partial class Leaf
    {
        public int Value;
    }

    [DdsStruct]
    public partial class FixedClassItem
    {
        public int Value;

        // Non-public on purpose: the default element must still be constructible from generated code
        private FixedClassItem() { Value = 7; }
    }

    [DdsStruct]
    public partial class FixedClassHolder
    {
        [ArrayLength(3)] public FixedClassItem[] Items;
    }

    [DdsStruct]
    public partial class Holder
    {
        public Leaf Child;
    }

}
