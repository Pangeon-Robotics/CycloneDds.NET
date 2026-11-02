namespace CycloneDDS.Schema;

/// <summary>
/// Makes a <c>T[]</c> member a fixed-length array (<c>T[N]</c>) instead of a sequence.
/// The array must hold exactly <see cref="Length"/> elements when written; null writes defaults.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ArrayLengthAttribute : Attribute
{
    /// <summary>Number of elements.</summary>
    public int Length { get; }

    /// <param name="length">Number of elements. Must be greater than 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.</exception>
    public ArrayLengthAttribute(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
    }
}
