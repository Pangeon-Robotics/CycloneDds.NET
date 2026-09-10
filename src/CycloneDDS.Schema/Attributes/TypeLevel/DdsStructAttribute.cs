using System;

namespace CycloneDDS.Schema
{
    /// <summary>
    /// Marks a struct or class as a DDS data type and triggers code generation for its
    /// serialization. A marked type can be nested in another or published on a topic; the
    /// topic name and QoS are chosen where the reader or writer is created, not on the type.
    /// </summary>
    /// <example>
    /// <code>
    /// [DdsStruct]
    /// public partial struct Point3D
    /// {
    ///     public double X;
    ///     public double Y;
    ///     public double Z;
    /// }
    ///
    /// [DdsStruct]
    /// public partial struct RobotState
    /// {
    ///     [DdsKey] public int Id;
    ///     public Point3D Position;  // Uses the [DdsStruct] type
    /// }
    ///
    /// using var writer = new DdsWriter&lt;RobotState&gt;(participant, "Robot");
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DdsStructAttribute : Attribute
    {
    }
}
