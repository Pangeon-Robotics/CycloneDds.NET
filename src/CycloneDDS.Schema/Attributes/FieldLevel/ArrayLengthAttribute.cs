namespace CycloneDDS.Schema;

/// <summary>
/// Specifies the fixed length of an array.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ArrayLengthAttribute"/> class.
/// </remarks>
/// <param name="length">The fixed length of the array.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ArrayLengthAttribute(int length) : Attribute
{
    /// <summary>
    /// Gets the fixed length of the array.
    /// </summary>
    public int Length { get; } = length;
}
