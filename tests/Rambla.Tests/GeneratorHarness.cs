using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Rambla.Tests;

/// <summary>
/// Compiles a source snippet with the Rambla generators attached. The
/// compilation references the real <c>Rambla</c> assembly, so
/// <see cref="GeneratorRun.Errors"/> proves the emitted code actually compiles —
/// not just that it contains the expected text.
/// </summary>
internal static class GeneratorHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static GeneratorRun Generate(string source, params IIncrementalGenerator[] generators)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators.Select(g => g.AsSourceGenerator()).ToArray());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        GeneratorDriverRunResult result = driver.GetRunResult();
        string generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));

        return new GeneratorRun(generated, result.Diagnostics, output.GetDiagnostics());
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in tpa.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                byName[Path.GetFileName(path)] = MetadataReference.CreateFromFile(path);
            }
        }

        string ramblaPath = typeof(RamblaState).Assembly.Location;
        byName[Path.GetFileName(ramblaPath)] = MetadataReference.CreateFromFile(ramblaPath);

        return byName.Values.ToImmutableArray();
    }
}

/// <summary>The result of one generator run: what it emitted and what it reported.</summary>
internal sealed record GeneratorRun(
    string Generated,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> AllDiagnostics)
{
    public bool HasError(string id) => GeneratorDiagnostics.Any(d => d.Id == id);

    /// <summary>Compilation errors, including any in the generated code itself.</summary>
    public IEnumerable<Diagnostic> Errors => AllDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
}
