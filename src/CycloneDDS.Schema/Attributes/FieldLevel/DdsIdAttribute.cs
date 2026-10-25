namespace CycloneDDS.Schema;

/// <summary>
/// Specifies an explicit member ID for a field or property.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DdsIdAttribute"/> class.
/// </remarks>
/// <param name="id">The explicit member ID.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class DdsIdAttribute(int id) : Attribute
{
    /// <summary>
    /// Gets the member ID.
    /// </summary>
    public int Id { get; } = id;
}
