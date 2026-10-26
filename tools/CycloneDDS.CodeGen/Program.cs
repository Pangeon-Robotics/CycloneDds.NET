namespace CycloneDDS.CodeGen
{
    public static class Program
    {
        /// <summary>
        /// Usage: <c>CycloneDDS.CodeGen &lt;source-directory&gt; &lt;output-directory&gt; [references]</c>,
        /// where references are assembly paths separated by ';' (MSBuild's flattened @(ReferencePath)).
        /// </summary>
        public static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: CycloneDDS.CodeGen <source-directory> <output-directory> [references]");
                return 1;
            }

            var references = string.Join(";", args.Skip(2)).Split(';', StringSplitOptions.RemoveEmptyEntries);

            try
            {
                new CodeGenerator().Generate(args[0], args[1], references);
                return 0;
            }
            catch (CodeGenerator.SchemaException ex)
            {
                // One line per diagnostic, in the canonical format MSBuild turns into build errors
                foreach (var diagnostic in ex.Diagnostics)
                    Console.Error.WriteLine(diagnostic);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CycloneDDS.CodeGen: error CDDS0000: {ex.Message}");
                return 1;
            }
        }
    }
}
