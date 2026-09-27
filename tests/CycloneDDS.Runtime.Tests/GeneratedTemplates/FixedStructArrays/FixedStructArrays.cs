// Schema for the fixed-size-array-of-structs round trip tests.
// Arrays of structs are declared with C# 12 [InlineArray]; the generator backs them with an
// [InlineArray] buffer in the native struct (C# 'fixed' buffers only accept primitives).

using System.Runtime.CompilerServices;
using CycloneDDS.Schema;

namespace FixedStructArrays
{
    [DdsStruct]
    public partial struct Vec2
    {
        public double X;
        public double Y;
    }

    [DdsStruct]
    public partial struct Marker
    {
        public int Id;

        [DdsManaged]
        public string Label;

        public bool Active;
    }

    /// <summary>Fixed array of three <see cref="Vec2"/> (no dynamic payload).</summary>
    [InlineArray(3)]
    public struct Vec2Array3
    {
        private Vec2 _element0;
    }

    /// <summary>Fixed array of two <see cref="Marker"/> (each carries a string).</summary>
    [InlineArray(2)]
    public struct MarkerArray2
    {
        private Marker _element0;
    }

    [DdsStruct]
    [DdsManaged]
    public partial struct FixedArrayTopic
    {
        [DdsKey]
        public int Id;

        public Vec2Array3 Corners;

        public MarkerArray2 Markers;

        /// <summary>Guards against layout drift after the inline arrays.</summary>
        public int Trailer;
    }
}
