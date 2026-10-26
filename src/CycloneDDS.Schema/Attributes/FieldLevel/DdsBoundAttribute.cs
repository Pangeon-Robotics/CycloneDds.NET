namespace CycloneDDS.Schema;

/// <summary>
/// Bounds a string member (<c>string&lt;=N</c>) or a sequence member (<c>T[&lt;=N]</c>).
/// String bounds count UTF-8 bytes, or UTF-16 code units for <see cref="DdsWideStringAttribute"/> members.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class DdsBoundAttribute : Attribute
{
    /// <summary>Maximum length.</summary>
    public int Max { get; }

    /// <param name="max">Maximum length. Must be greater than 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is not positive.</exception>
    public DdsBoundAttribute(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        Max = max;
    }
}

/// <summary>
/// Bounds the string elements of a sequence or array member (<c>string&lt;=N[]</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class DdsElementBoundAttribute : Attribute
{
    /// <summary>Maximum length of each element.</summary>
    public int Max { get; }

    /// <param name="max">Maximum length. Must be greater than 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is not positive.</exception>
    public DdsElementBoundAttribute(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        Max = max;
    }
}
