namespace CycloneDDS.Schema
{
    /// <summary>
    /// The extensibility kind of the DDS type.
    /// </summary>
    public enum DdsExtensibilityKind
    {
        /// <summary>
        /// Final extensibility.
        /// </summary>
        Final,
        /// <summary>
        /// Appendable extensibility.
        /// </summary>
        Appendable,
        /// <summary>
        /// Mutable extensibility.
        /// </summary>
        Mutable
    }

    /// <summary>
    /// Controls the XTypes extensibility kind.
    /// Defaults to Final if not specified.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="DdsExtensibilityAttribute"/> class.
    /// </remarks>
    /// <param name="kind">The extensibility kind.</param>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DdsExtensibilityAttribute(DdsExtensibilityKind kind) : Attribute
    {
        /// <summary>
        /// The extensibility kind.
        /// </summary>
        public DdsExtensibilityKind Kind { get; } = kind;
    }
}
