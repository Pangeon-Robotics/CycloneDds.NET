using System.Text.RegularExpressions;
using CycloneDDS.CodeGen.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CycloneDDS.CodeGen.Discovery
{
    /// <summary>
    /// Builds the <see cref="DdsSchema"/> of a project from its Roslyn symbols. Every member is
    /// resolved once into a <see cref="Shape"/>; anything not representable as a final XCDR1
    /// type is reported as a diagnostic instead.
    /// </summary>
    public sealed class SchemaDiscovery
    {
        private const string DdsStructAttribute = "CycloneDDS.Schema.DdsStructAttribute";
        private const string DdsIdlFileAttribute = "CycloneDDS.Schema.DdsIdlFileAttribute";
        private const string DdsIdlModuleAttribute = "CycloneDDS.Schema.DdsIdlModuleAttribute";
        private const string DdsIdlMappingAttribute = "CycloneDDS.Schema.DdsIdlMappingAttribute";
        private const string DdsBoundAttribute = "CycloneDDS.Schema.DdsBoundAttribute";
        private const string DdsElementBoundAttribute = "CycloneDDS.Schema.DdsElementBoundAttribute";
        private const string ArrayLengthAttribute = "CycloneDDS.Schema.ArrayLengthAttribute";
        private const string DdsWideStringAttribute = "CycloneDDS.Schema.DdsWideStringAttribute";
        private const string InlineArrayAttribute = "System.Runtime.CompilerServices.InlineArrayAttribute";

        private static readonly Regex IdlIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private readonly Compilation _compilation;
        private readonly DdsSchema _schema = new();

        // Every type a member can reference, local or external, keyed by symbol
        private readonly Dictionary<INamedTypeSymbol, StructType> _structs = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<INamedTypeSymbol, EnumType> _enums = new(SymbolEqualityComparer.Default);

        // Local enums that cannot be expressed in IDL, with the reason; an error only once referenced
        private readonly Dictionary<INamedTypeSymbol, string> _unrepresentableEnums = new(SymbolEqualityComparer.Default);

        private SchemaDiscovery(Compilation compilation)
        {
            _compilation = compilation;
        }

        /// <summary>Discovers the DDS types declared in <paramref name="compilation"/>'s source.</summary>
        public static DdsSchema Discover(Compilation compilation) => new SchemaDiscovery(compilation).Run();

        /// <summary>
        /// Compiles every <c>*.cs</c> under <paramref name="sourceDirectory"/> (excluding build output
        /// and generated folders) against <paramref name="referencePaths"/>, then discovers its DDS types.
        /// </summary>
        public static DdsSchema Discover(string sourceDirectory, IEnumerable<string>? referencePaths)
        {
            return Discover(CreateCompilation(sourceDirectory, referencePaths));
        }

        public static Compilation CreateCompilation(string sourceDirectory, IEnumerable<string>? referencePaths)
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");

            char sep = Path.DirectorySeparatorChar;
            string[] excludedDirs = [$"{sep}obj{sep}", $"{sep}bin{sep}", $"{sep}Generated{sep}"];

            var trees = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !excludedDirs.Any(f.Contains))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
                .ToList();

            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CycloneDDS.Schema.DdsStructAttribute).Assembly.Location),
            };

            foreach (var path in referencePaths ?? [])
            {
                if (File.Exists(path)) references.Add(MetadataReference.CreateFromFile(path));
            }

            // The generator's own framework, so BCL types resolve even when no reference pack is passed
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
            {
                foreach (var path in trusted.Split(Path.PathSeparator))
                    references.Add(MetadataReference.CreateFromFile(path));
            }

            return CSharpCompilation.Create("CycloneDdsDiscovery")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
                .AddReferences(references)
                .AddSyntaxTrees(trees);
        }


        private DdsSchema Run()
        {
            var sourceTypes = SourceTypes().ToList();

            // Declare first so members may reference types declared later
            foreach (var symbol in sourceTypes)
            {
                if (symbol.TypeKind == TypeKind.Enum && symbol.ContainingType == null)
                    DeclareEnum(symbol);
                else if (HasAttribute(symbol, DdsStructAttribute))
                    DeclareStruct(symbol);
            }

            foreach (var symbol in sourceTypes)
            {
                if (_structs.TryGetValue(symbol, out var structType) && !structType.IsExternal)
                    ResolveFields(symbol, structType);
            }

            CheckCycles();
            CheckIdlNameCollisions();
            return _schema;
        }

        /// <summary>All named types declared in source, nested ones included, in source order.</summary>
        private IEnumerable<INamedTypeSymbol> SourceTypes()
        {
            var found = new List<INamedTypeSymbol>();
            var pending = new Stack<INamespaceOrTypeSymbol>();
            pending.Push(_compilation.SourceModule.GlobalNamespace);

            while (pending.Count > 0)
            {
                foreach (var member in pending.Pop().GetMembers())
                {
                    if (member is INamespaceSymbol ns) pending.Push(ns);
                    else if (member is INamedTypeSymbol type && type.Locations.Any(l => l.IsInSource))
                    {
                        found.Add(type);
                        pending.Push(type);
                    }
                }
            }

            return found
                .OrderBy(t => t.Locations[0].SourceTree?.FilePath, StringComparer.Ordinal)
                .ThenBy(t => t.Locations[0].SourceSpan.Start);
        }


        // ---- Declarations ----------------------------------------------------------

        private void DeclareStruct(INamedTypeSymbol symbol)
        {
            int errors = _schema.Diagnostics.Count;
            Location location = symbol.Locations[0];

            if (symbol.ContainingType != null)
                Error($"[DdsStruct] type '{symbol.Name}' is nested in '{symbol.ContainingType.Name}'; DDS types must be declared directly in a namespace.", location);
            if (symbol.IsGenericType)
                Error($"[DdsStruct] type '{symbol.Name}' is generic; DDS types must be concrete.", location);
            if (symbol.IsRefLikeType || symbol.IsReadOnly)
                Error($"[DdsStruct] type '{symbol.Name}' is a ref or readonly struct; deserialization must assign its members.", location);
            if (!IsPartial(symbol))
                Error($"[DdsStruct] type '{symbol.Name}' must be declared partial so its serializer can be generated.", location);

            if (symbol.TypeKind == TypeKind.Class)
            {
                if (symbol.IsAbstract)
                    Error($"[DdsStruct] class '{symbol.Name}' is abstract; it must be instantiable.", location);
                if (symbol.BaseType?.SpecialType != SpecialType.System_Object)
                    Error($"[DdsStruct] class '{symbol.Name}' derives from '{symbol.BaseType}'; IDL struct inheritance is not supported.", location);
                if (!symbol.InstanceConstructors.Any(c => c.Parameters.Length == 0))
                    Error($"[DdsStruct] class '{symbol.Name}' needs a parameterless constructor for deserialization.", location);
            }

            if (_schema.Diagnostics.Count > errors) return;

            var structType = new StructType
            {
                Name = symbol.Name,
                Namespace = NamespaceOf(symbol),
                IdlModule = IdlModuleOf(symbol),
                IdlFile = IdlFileOf(symbol),
                DeclarationKind = (symbol.IsRecord, symbol.TypeKind) switch
                {
                    (true, TypeKind.Struct) => "record struct",
                    (true, _) => "record class",
                    (false, TypeKind.Struct) => "struct",
                    _ => "class",
                },
            };

            _structs[symbol] = structType;
            _schema.Structs.Add(structType);
        }

        private void DeclareEnum(INamedTypeSymbol symbol)
        {
            PrimitiveKind? underlying = symbol.EnumUnderlyingType?.SpecialType switch
            {
                SpecialType.System_Byte => PrimitiveKind.Byte,
                SpecialType.System_SByte => PrimitiveKind.SByte,
                SpecialType.System_Int16 => PrimitiveKind.Int16,
                SpecialType.System_UInt16 => PrimitiveKind.UInt16,
                SpecialType.System_Int32 => PrimitiveKind.Int32,
                SpecialType.System_UInt32 => PrimitiveKind.UInt32,
                _ => null,
            };

            if (underlying == null)
            {
                _unrepresentableEnums[symbol] = "its underlying type is 64-bit; IDL enums are at most 32-bit";
                return;
            }

            var members = symbol.GetMembers().OfType<IFieldSymbol>()
                .Where(f => f.HasConstantValue)
                .Select(f => new EnumMember(f.Name, Convert.ToInt64(f.ConstantValue)))
                .ToList();

            string? problem =
                members.Count == 0 ? "it has no members" :
                members.Any(m => m.Value < 0) ? "it has negative values" :
                members.Select(m => m.Value).Distinct().Count() != members.Count ? "two members share a value" :
                null;

            if (problem != null)
            {
                _unrepresentableEnums[symbol] = problem;
                return;
            }

            var enumType = new EnumType
            {
                Name = symbol.Name,
                Namespace = NamespaceOf(symbol),
                IdlModule = IdlModuleOf(symbol),
                IdlFile = IdlFileOf(symbol),
                Underlying = underlying.Value,
            };
            enumType.Members.AddRange(members);

            _enums[symbol] = enumType;
            _schema.Enums.Add(enumType);
        }

        private string IdlFileOf(INamedTypeSymbol symbol)
        {
            if (StringArgument(symbol.GetAttributes(), DdsIdlFileAttribute) is { } file)
            {
                if (file.Length == 0 || file.IndexOfAny(['.', '/', '\\']) >= 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    Error($"[DdsIdlFile(\"{file}\")] on '{symbol.Name}' must be a file name without extension or path.", symbol.Locations[0]);
                return file;
            }

            return Path.GetFileNameWithoutExtension(symbol.Locations[0].SourceTree?.FilePath ?? symbol.Name);
        }

        private string IdlModuleOf(INamedTypeSymbol symbol)
        {
            if (StringArgument(symbol.GetAttributes(), DdsIdlModuleAttribute) is { } module)
            {
                var segments = module.Split("::");
                if (segments.Any(s => !IdlIdentifier.IsMatch(s)))
                    Error($"[DdsIdlModule(\"{module}\")] on '{symbol.Name}' must be identifiers separated by '::'.", symbol.Locations[0]);
                return module;
            }

            return NamespaceOf(symbol).Replace(".", "::");
        }


        // ---- Members ---------------------------------------------------------------

        private void ResolveFields(INamedTypeSymbol symbol, StructType structType)
        {
            var members = symbol.GetMembers();
            var autoProperties = members.OfType<IFieldSymbol>()
                .Select(f => f.AssociatedSymbol)
                .OfType<IPropertySymbol>()
                .ToHashSet<IPropertySymbol>(SymbolEqualityComparer.Default);

            foreach (var member in members)
            {
                if (member.IsStatic || member.IsImplicitlyDeclared) continue;

                Location location = member.Locations[0];
                Shape? shape;

                switch (member)
                {
                    case IFieldSymbol { IsConst: true }:
                        continue;

                    case IFieldSymbol field:
                        if (field.IsReadOnly)
                        {
                            Error($"'{symbol.Name}.{field.Name}' is readonly; deserialization must assign it.", location);
                            continue;
                        }
                        if (field.IsFixedSizeBuffer)
                        {
                            Error($"'{symbol.Name}.{field.Name}' is a fixed buffer; use an [InlineArray] struct or [ArrayLength] T[] instead.", location);
                            continue;
                        }
                        shape = ResolveMember(field, field.Type, isProperty: false);
                        break;

                    // Only auto-properties hold data; computed properties are not members
                    case IPropertySymbol property when autoProperties.Contains(property):
                        if (property.SetMethod == null || property.SetMethod.IsInitOnly)
                        {
                            Error($"'{symbol.Name}.{property.Name}' needs a setter; deserialization must assign it.", location);
                            continue;
                        }
                        shape = ResolveMember(property, property.Type, isProperty: true);
                        break;

                    default:
                        continue;
                }

                if (shape != null)
                    structType.Fields.Add(new Field(member.Name, shape, member is IPropertySymbol));
            }

            if (structType.Fields.Count == 0)
                Error($"[DdsStruct] type '{symbol.Name}' has no members; IDL structs need at least one.", symbol.Locations[0]);

            // IDL identifiers collide case-insensitively, including with the enclosing type's name
            foreach (var group in structType.Fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                Error($"'{symbol.Name}' has members differing only in case ({string.Join(", ", group.Select(f => f.Name))}); IDL identifiers are case-insensitive.", symbol.Locations[0]);
            foreach (var field in structType.Fields.Where(f => string.Equals(f.Name, symbol.Name, StringComparison.OrdinalIgnoreCase)))
                Error($"'{symbol.Name}.{field.Name}' differs from its type's name only in case; IDL identifiers are case-insensitive.", symbol.Locations[0]);
        }

        private Shape? ResolveMember(ISymbol member, ITypeSymbol type, bool isProperty)
        {
            var attributes = member.GetAttributes();
            int? bound = IntArgument(attributes, DdsBoundAttribute);
            int? elementBound = IntArgument(attributes, DdsElementBoundAttribute);
            int? arrayLength = IntArgument(attributes, ArrayLengthAttribute);
            bool wide = HasAttribute(attributes, DdsWideStringAttribute);

            string name = $"{member.ContainingType.Name}.{member.Name}";
            Location location = member.Locations[0];

            bool boundUsed = false, elementBoundUsed = false, arrayLengthUsed = false, wideUsed = false;
            Shape? shape;

            if (type.SpecialType == SpecialType.System_String)
            {
                shape = new StringShape(bound ?? 0, wide);
                boundUsed = wideUsed = true;
            }
            else if (CollectionElementOf(type, out var kind, out var elementType, out int inlineLength))
            {
                Shape? element = ResolveElement(elementType, elementBound ?? 0, wide, name, location);
                elementBoundUsed = wideUsed = element is StringShape;

                if (kind == CollectionKind.InlineArray)
                {
                    if (isProperty)
                        Error($"'{name}' is an [InlineArray] property; declare it as a field so it can be accessed in place.", location);
                    shape = element == null ? null : new ArrayShape(element, inlineLength, kind);
                }
                else if (kind == CollectionKind.Array && arrayLength != null)
                {
                    shape = element == null ? null : new ArrayShape(element, arrayLength.Value, kind);
                    arrayLengthUsed = true;
                }
                else
                {
                    shape = element == null ? null : new SequenceShape(element, bound ?? 0, kind);
                    boundUsed = true;
                }
            }
            else
            {
                shape = ResolveScalar(type, name, location);
            }

            if (bound != null && !boundUsed)
                Error($"'{name}': [DdsBound] applies only to string and sequence members.", location);
            if (elementBound != null && !elementBoundUsed)
                Error($"'{name}': [DdsElementBound] applies only to collections of strings.", location);
            if (arrayLength != null && !arrayLengthUsed)
                Error($"'{name}': [ArrayLength] applies only to T[] members.", location);
            if (wide && !wideUsed)
                Error($"'{name}': [DdsWideString] applies only to string members and collections of strings.", location);

            return shape;
        }

        private Shape? ResolveElement(ITypeSymbol type, int bound, bool wide, string name, Location location)
        {
            if (type.SpecialType == SpecialType.System_String)
                return new StringShape(bound, wide);

            if (CollectionElementOf(type, out _, out _, out _))
            {
                Error($"'{name}' is a collection of collections; wrap the inner collection in a [DdsStruct].", location);
                return null;
            }

            return ResolveScalar(type, name, location);
        }

        private Shape? ResolveScalar(ITypeSymbol type, string name, Location location)
        {
            PrimitiveKind? primitive = type.SpecialType switch
            {
                SpecialType.System_Boolean => PrimitiveKind.Bool,
                SpecialType.System_Byte => PrimitiveKind.Byte,
                SpecialType.System_SByte => PrimitiveKind.SByte,
                SpecialType.System_Int16 => PrimitiveKind.Int16,
                SpecialType.System_UInt16 => PrimitiveKind.UInt16,
                SpecialType.System_Int32 => PrimitiveKind.Int32,
                SpecialType.System_UInt32 => PrimitiveKind.UInt32,
                SpecialType.System_Int64 => PrimitiveKind.Int64,
                SpecialType.System_UInt64 => PrimitiveKind.UInt64,
                SpecialType.System_Single => PrimitiveKind.Single,
                SpecialType.System_Double => PrimitiveKind.Double,
                _ => null,
            };
            if (primitive != null) return new PrimitiveShape(primitive.Value);

            if (type.SpecialType == SpecialType.System_Char)
            {
                Error($"'{name}' is a char, which is a UTF-16 code unit in C#; use byte for IDL char/uint8, or string.", location);
                return null;
            }

            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                Error($"'{name}' is nullable; optional members require XCDR2 and are not supported.", location);
                return null;
            }

            if (type is INamedTypeSymbol named)
            {
                if (named.TypeKind == TypeKind.Enum)
                {
                    var enumType = ResolveEnum(named, name, location);
                    return enumType == null ? null : new EnumShape(enumType);
                }

                if (HasAttribute(named, DdsStructAttribute))
                {
                    var structType = ResolveStruct(named, name, location);
                    return structType == null ? null : new StructShape(structType);
                }
            }

            Error($"'{name}' has type '{type.ToDisplayString()}', which is not a DDS type. Use a primitive, string, enum, " +
                  "[DdsStruct] type, List<T>, T[] or [InlineArray] struct.", location);
            return null;
        }

        /// <summary>True for <c>List&lt;T&gt;</c>, single-dimensional <c>T[]</c> and <c>[InlineArray]</c> structs.</summary>
        private static bool CollectionElementOf(ITypeSymbol type, out CollectionKind kind, out ITypeSymbol element, out int inlineLength)
        {
            inlineLength = 0;

            if (type is IArrayTypeSymbol { Rank: 1 } array)
            {
                kind = CollectionKind.Array;
                element = array.ElementType;
                return true;
            }

            if (type is INamedTypeSymbol { IsGenericType: true } generic &&
                generic.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
            {
                kind = CollectionKind.List;
                element = generic.TypeArguments[0];
                return true;
            }

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Struct } inline &&
                inline.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == InlineArrayAttribute) is { } attribute &&
                attribute.ConstructorArguments.FirstOrDefault().Value is int length &&
                inline.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic) is { } elementField)
            {
                kind = CollectionKind.InlineArray;
                element = elementField.Type;
                inlineLength = length;
                return true;
            }

            kind = default;
            element = null!;
            return false;
        }


        // ---- Type references -------------------------------------------------------

        private StructType? ResolveStruct(INamedTypeSymbol symbol, string name, Location location)
        {
            if (_structs.TryGetValue(symbol, out var known)) return known;

            // A local [DdsStruct] missing from the map failed its own declaration checks
            if (symbol.Locations.Any(l => l.IsInSource)) return null;

            if (ExternalMapping(symbol, name, location) is not { } mapping) return null;

            var external = new StructType
            {
                Name = symbol.Name,
                Namespace = NamespaceOf(symbol),
                IdlModule = mapping.Module,
                IdlFile = mapping.File,
                IsExternal = true,
            };
            _structs[symbol] = external;
            return external;
        }

        private EnumType? ResolveEnum(INamedTypeSymbol symbol, string name, Location location)
        {
            if (_enums.TryGetValue(symbol, out var known)) return known;

            if (_unrepresentableEnums.TryGetValue(symbol, out var reason))
            {
                Error($"'{name}' uses enum '{symbol.Name}', which cannot be expressed in IDL: {reason}.", location);
                return null;
            }

            if (symbol.Locations.Any(l => l.IsInSource))
            {
                Error($"'{name}' uses enum '{symbol.Name}', which is nested in a type; DDS enums must be declared directly in a namespace.", location);
                return null;
            }

            if (ExternalMapping(symbol, name, location) is not { } mapping) return null;

            PrimitiveKind underlying = symbol.EnumUnderlyingType?.SpecialType switch
            {
                SpecialType.System_Byte => PrimitiveKind.Byte,
                SpecialType.System_SByte => PrimitiveKind.SByte,
                SpecialType.System_Int16 => PrimitiveKind.Int16,
                SpecialType.System_UInt16 => PrimitiveKind.UInt16,
                SpecialType.System_UInt32 => PrimitiveKind.UInt32,
                _ => PrimitiveKind.Int32,
            };

            var external = new EnumType
            {
                Name = symbol.Name,
                Namespace = NamespaceOf(symbol),
                IdlModule = mapping.Module,
                IdlFile = mapping.File,
                IsExternal = true,
                Underlying = underlying,
            };
            _enums[symbol] = external;
            return external;
        }

        /// <summary>
        /// Reads the IDL location of a type from a referenced assembly. The defining project's
        /// generator records it as <c>[assembly: DdsIdlMapping]</c>; the C# source file name it
        /// defaults from is not otherwise visible in metadata.
        /// </summary>
        private (string File, string Module)? ExternalMapping(INamedTypeSymbol symbol, string name, Location location)
        {
            string fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

            foreach (var attribute in symbol.ContainingAssembly.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != DdsIdlMappingAttribute) continue;

                var args = attribute.ConstructorArguments;
                if (args.Length == 3 && args[0].Value as string == fullName &&
                    args[1].Value is string file && args[2].Value is string module)
                {
                    return (file, module);
                }
            }

            Error($"'{name}' uses '{fullName}' from assembly '{symbol.ContainingAssembly.Name}', which has no [assembly: DdsIdlMapping] " +
                  "for it. That assembly was built without CycloneDDS code generation (check for a stale generated folder), " +
                  "or the type is not a DDS type.", location);
            return null;
        }


        // ---- Schema-wide checks ----------------------------------------------------

        /// <summary>Final XCDR1 cannot encode recursive types; ROS 2 interfaces forbid them too.</summary>
        private void CheckCycles()
        {
            var state = new Dictionary<StructType, bool>(); // false = on stack, true = done
            var path = new Stack<StructType>();

            foreach (var structType in _schema.Structs)
                Visit(structType);

            void Visit(StructType current)
            {
                if (state.TryGetValue(current, out bool done))
                {
                    if (!done)
                    {
                        var cycle = path.Reverse().SkipWhile(s => s != current).Select(s => s.Name).Append(current.Name);
                        Error($"Recursive DDS types are not supported: {string.Join(" -> ", cycle)}.", null);
                    }
                    return;
                }

                state[current] = false;
                path.Push(current);

                foreach (var field in current.Fields)
                {
                    if (ReferencedStruct(field.Shape) is { IsExternal: false } next)
                        Visit(next);
                }

                path.Pop();
                state[current] = true;
            }
        }

        private void CheckIdlNameCollisions()
        {
            var groups = _schema.LocalTypes.GroupBy(t => t.IdlName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                Error($"Types {string.Join(", ", group.Select(t => $"'{t.CSharpFullName}'"))} all map to IDL name '{group.Key}' " +
                      "(IDL names are case-insensitive). Rename one or give it a different [DdsIdlModule].", null);
            }
        }

        /// <summary>The struct a member's shape refers to, directly or as its collection element.</summary>
        public static StructType? ReferencedStruct(Shape shape) => shape switch
        {
            StructShape s => s.Type,
            SequenceShape { Element: StructShape s } => s.Type,
            ArrayShape { Element: StructShape s } => s.Type,
            _ => null,
        };


        // ---- Helpers ---------------------------------------------------------------

        private void Error(string message, Location? location) => _schema.Diagnostics.Add(new SchemaDiagnostic(message, location));

        private static string NamespaceOf(INamedTypeSymbol symbol) =>
            symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

        private static bool IsPartial(INamedTypeSymbol symbol) =>
            symbol.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<TypeDeclarationSyntax>()
                .All(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));

        private static bool HasAttribute(ISymbol symbol, string fullName) => HasAttribute(symbol.GetAttributes(), fullName);

        private static bool HasAttribute(IEnumerable<AttributeData> attributes, string fullName) =>
            attributes.Any(a => a.AttributeClass?.ToDisplayString() == fullName);

        private static object? FirstArgument(IEnumerable<AttributeData> attributes, string fullName) =>
            attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName)?.ConstructorArguments.FirstOrDefault().Value;

        private static int? IntArgument(IEnumerable<AttributeData> attributes, string fullName) =>
            FirstArgument(attributes, fullName) is int value ? value : null;

        private static string? StringArgument(IEnumerable<AttributeData> attributes, string fullName) =>
            FirstArgument(attributes, fullName) as string;
    }
}
