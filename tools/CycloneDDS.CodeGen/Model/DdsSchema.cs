using Microsoft.CodeAnalysis;

namespace CycloneDDS.CodeGen.Model
{
    /// <summary>Result of discovery: the local DDS types of one project and any problems found.</summary>
    public sealed class DdsSchema
    {
        /// <summary>Local structs in source order.</summary>
        public List<StructType> Structs { get; } = [];

        /// <summary>Local enums in source order.</summary>
        public List<EnumType> Enums { get; } = [];

        public List<SchemaDiagnostic> Diagnostics { get; } = [];

        public bool HasErrors => Diagnostics.Count > 0;

        public IEnumerable<NamedType> LocalTypes => Structs.Cast<NamedType>().Concat(Enums);
    }

    /// <summary>A schema error, located at the offending declaration when one is known.</summary>
    public sealed record SchemaDiagnostic(string Message, Location? Location)
    {
        /// <summary>Canonical MSBuild error format, so the build reports it against the source line.</summary>
        public override string ToString()
        {
            if (Location is { IsInSource: true } location)
            {
                var span = location.GetLineSpan();
                var start = span.StartLinePosition;
                return $"{span.Path}({start.Line + 1},{start.Character + 1}): error CDDS0001: {Message}";
            }
            return $"CycloneDDS.CodeGen: error CDDS0001: {Message}";
        }
    }
}
