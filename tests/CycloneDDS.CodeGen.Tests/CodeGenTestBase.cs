using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CycloneDDS.CodeGen.Discovery;
using CycloneDDS.CodeGen.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CycloneDDS.CodeGen.Tests
{
    /// <summary>
    /// Helpers to run discovery on in-memory sources, and to build throwaway projects on disk
    /// through the full generator (idlc included) and compile them.
    /// </summary>
    public abstract class CodeGenTestBase : IDisposable
    {
        protected readonly string TempDir = Path.Combine(Path.GetTempPath(), "cdds-codegen-" + Guid.NewGuid().ToString("N"));

        protected CodeGenTestBase()
        {
            Directory.CreateDirectory(TempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(TempDir, true); } catch { }
        }


        // ---- In-memory discovery ---------------------------------------------------

        /// <summary>Framework, Schema, Core and Runtime: everything generated code compiles against.</summary>
        protected static List<MetadataReference> StandardReferences()
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            references.Add(MetadataReference.CreateFromFile(typeof(CycloneDDS.Schema.DdsStructAttribute).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(CycloneDDS.Core.CdrWriter).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(CycloneDDS.Runtime.DdsParticipant).Assembly.Location));
            return references;
        }

        /// <summary>Discovers the schema of <paramref name="sources"/>, each its own file named <c>File{index}.cs</c>.</summary>
        protected static DdsSchema Discover(params string[] sources)
        {
            var trees = sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, path: $"File{i}.cs"));
            var compilation = CSharpCompilation.Create("DiscoveryTest")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
                .AddReferences(StandardReferences())
                .AddSyntaxTrees(trees);
            return SchemaDiscovery.Discover(compilation);
        }

        /// <summary>Discovers a valid schema, failing with its diagnostics otherwise.</summary>
        protected static DdsSchema DiscoverValid(params string[] sources)
        {
            var schema = Discover(sources);
            if (schema.HasErrors) throw new Xunit.Sdk.XunitException(string.Join(Environment.NewLine, schema.Diagnostics));
            return schema;
        }


        // ---- Projects on disk ------------------------------------------------------

        protected string CreateProject(string name, params (string File, string Content)[] files)
        {
            string folder = Path.Combine(TempDir, name);
            Directory.CreateDirectory(folder);
            foreach (var (file, content) in files)
                File.WriteAllText(Path.Combine(folder, file), content);
            return folder;
        }

        protected static string GeneratedDir(string projectFolder) => Path.Combine(projectFolder, "Generated");

        /// <summary>
        /// Runs the generator on <paramref name="sourceDir"/> as the MSBuild targets would (IDL of
        /// referenced projects copied in first), then compiles sources plus generated code.
        /// </summary>
        /// <returns>Path of the compiled assembly.</returns>
        protected string CompileProject(string projectName, string sourceDir, IEnumerable<string>? references = null)
        {
            var referenceList = references?.ToList() ?? [];
            string outputDir = GeneratedDir(sourceDir);
            Directory.CreateDirectory(outputDir);

            // CopyReferencedIdlFiles: each referenced assembly sits next to its project folder
            foreach (var dll in referenceList)
            {
                string referencedGenerated = GeneratedDir(Path.Combine(Path.GetDirectoryName(dll)!, Path.GetFileNameWithoutExtension(dll)));
                foreach (var idl in Directory.GetFiles(referencedGenerated, "*.idl"))
                    File.Copy(idl, Path.Combine(outputDir, Path.GetFileName(idl)), overwrite: true);
            }

            new CodeGenerator().Generate(sourceDir, outputDir, referenceList.Prepend(typeof(CycloneDDS.Schema.DdsStructAttribute).Assembly.Location));

            var trees = Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f));

            var compilation = CSharpCompilation.Create(projectName)
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
                .AddReferences(StandardReferences())
                .AddReferences(referenceList.Select(r => MetadataReference.CreateFromFile(r)))
                .AddSyntaxTrees(trees);

            string dllPath = Path.Combine(TempDir, projectName + ".dll");
            var result = compilation.Emit(dllPath);
            if (!result.Success)
            {
                var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
                throw new Xunit.Sdk.XunitException($"Compilation of {projectName} failed:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
            }

            return dllPath;
        }
    }
}
