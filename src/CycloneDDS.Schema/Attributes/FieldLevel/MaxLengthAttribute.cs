namespace CycloneDDS.Schema;

/// <summary>
/// Specifies the maximum length of a string or sequence.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MaxLengthAttribute"/> class.
/// </remarks>
/// <param name="length">The maximum length.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class MaxLengthAttribute(int length) : Attribute
{
    /// <summary>
    /// Gets the maximum length of the string or sequence.
    /// </summary>
    public int Length { get; } = length;
}
