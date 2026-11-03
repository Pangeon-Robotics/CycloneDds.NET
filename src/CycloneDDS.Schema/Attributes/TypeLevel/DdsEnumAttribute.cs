namespace CycloneDDS.Schema
{
    /// <summary>
    /// Marks an enum as a DDS enum (an IDL enum whose bit bound matches its underlying type), so
    /// <c>[DdsStruct]</c> types in this or any referencing project can use it. Unmarked enums are
    /// ignored by code generation.
    /// </summary>
    /// <example>
    /// <code>
    /// [DdsEnum]
    /// public enum SensorStatus : byte { Offline, Online, Error }
    ///
    /// [DdsStruct]
    /// public partial struct SensorData
    /// {
    ///     public SensorStatus Status;
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Enum, AllowMultiple = false)]
    public sealed class DdsEnumAttribute : Attribute
    {
    }
}
