namespace CycloneDDS.Schema;

/// <summary>
/// Maps a string member, or the string elements of a collection member, to <c>wstring</c>
/// (UTF-16) instead of <c>string</c> (UTF-8).
/// </summary>
/// <remarks>
/// Uses Cyclone's wstring encoding, which ROS 2 does not: rmw_cyclonedds encodes wstring
/// differently, so wide strings do not interoperate with ROS 2 nodes.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class DdsWideStringAttribute : Attribute
{
}
