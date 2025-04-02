using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length == 0)
{
    Console.WriteLine("Usage: ListPublicTypes <solution-path>");
    return;
}

var solutionPath = args[0];

MSBuildLocator.RegisterDefaults();

using var workspace = MSBuildWorkspace.Create();

Console.WriteLine($"Loading solution: {solutionPath}");
var solution = await workspace.OpenSolutionAsync(solutionPath);

var publicTypes = new Dictionary<string, HashSet<INamedTypeSymbol>>();
var extensionMethodTypes = new Dictionary<string, HashSet<INamedTypeSymbol>>();

var solutionDir = Path.GetDirectoryName(solution.FilePath);

foreach (var project in solution.Projects.OrderBy(p => p.FilePath))
{
    var relativeProjectPath = project.FilePath![(solutionDir!.Length + 1)..];
    if (!relativeProjectPath.StartsWith("src")) continue;

    publicTypes[relativeProjectPath] = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    extensionMethodTypes[relativeProjectPath] = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    Console.WriteLine($"Processing project: {relativeProjectPath}");

    var compilation = await project.GetCompilationAsync();
    if (compilation == null)
    {
        Console.WriteLine($"Failed to compile project: {project.Name}");
        continue;
    }

    foreach (var syntaxTree in compilation.SyntaxTrees)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();

        foreach (var node in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var symbol = semanticModel.GetDeclaredSymbol(node);
            if (symbol is {DeclaredAccessibility: Accessibility.Public, ContainingType: null})
            {
                publicTypes[relativeProjectPath].Add(symbol);

                if (symbol.IsStatic)
                {
                    // Check methods for extension methods
                    var hasExtensionMethods = symbol.GetMembers()
                        .OfType<IMethodSymbol>()
                        .Any(m => m is
                        {
                            DeclaredAccessibility: Accessibility.Public,
                            IsExtensionMethod: true
                        });

                    if (hasExtensionMethods) extensionMethodTypes[relativeProjectPath].Add(symbol);
                }
            }
        }
    }
}

var output = "c:/temp/public-types.md";

var typesCount = publicTypes.Values.Sum(set => set.Count);
var projectCount = publicTypes.Count;

Console.WriteLine($"Writing {typesCount} top-level types from {projectCount} projects to file {output}");

await using var writer = new StreamWriter(output);

await writer.WriteLineAsync($"- Solution: {solution.FilePath} top-level types from {projectCount} projects");
await writer.WriteLineAsync($"- Projects with public types: {projectCount}");
await writer.WriteLineAsync($"- Public types: {typesCount}");
await writer.WriteLineAsync();

foreach (var kvp in publicTypes.Where(kvp => kvp.Value.Count > 1).OrderBy(kvp => kvp.Key))
{
    await writer.WriteLineAsync($"# {kvp.Key}");
    await writer.WriteLineAsync();
    foreach (var v in kvp.Value.OrderBy(v => v.ToDisplayString()))
        await writer.WriteLineAsync(
            $" - `{v.ToDisplayString()}`{(extensionMethodTypes[kvp.Key].Contains(v) ? " (extension methods)" : "")}");

    await writer.WriteLineAsync();
}