namespace CycloneDDS.Schema
{
    /// <summary>
    /// Specifies the IDL module hierarchy for this type.
    /// Use "::" as separator (e.g., "std_msgs::msg::dds_").
    /// If omitted, defaults to C# namespace converted to modules.
    /// The module and type name together form the DDS type name used for matching on the wire.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DdsIdlModuleAttribute : Attribute
    {
        /// <summary>
        /// The module path using :: separator.
        /// </summary>
        public string ModulePath { get; }

        /// <param name="modulePath">IDL module path using :: separator (e.g., "Corp::Math::Geo")</param>
        public DdsIdlModuleAttribute(string modulePath)
        {
            if (string.IsNullOrWhiteSpace(modulePath))
                throw new ArgumentException("Module path cannot be empty", nameof(modulePath));

            ModulePath = modulePath;
        }
    }
}
