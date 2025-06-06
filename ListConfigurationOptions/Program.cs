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

var optiopns = new Dictionary<string, HashSet<INamedTypeSymbol>>();

var solutionDir = Path.GetDirectoryName(solution.FilePath);

foreach (var project in solution.Projects.OrderBy(p => p.FilePath))
{
    var relativeProjectPath = project.FilePath![(solutionDir!.Length + 1)..];
    if (!relativeProjectPath.StartsWith("src")) continue;

    optiopns[relativeProjectPath] = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

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

        foreach (var node in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                     .Where(s => s is RecordDeclarationSyntax or ClassDeclarationSyntax)
                     .Where(s => s.Identifier.Text.EndsWith("Options")))
        {
            var symbol = semanticModel.GetDeclaredSymbol(node);
            if (symbol is not null)
                optiopns[relativeProjectPath].Add(symbol);
        }
    }
}

var output = "c:/temp/public-options.md";

var typesCount = optiopns.Values.Sum(set => set.Count);
var projectCount = optiopns.Count;

Console.WriteLine($"Writing {typesCount} top-level options from {projectCount} projects to file {output}");

await using var writer = new StreamWriter(output);

await writer.WriteLineAsync($"- Solution: {solution.FilePath} top-level types from {projectCount} projects");
await writer.WriteLineAsync($"- Projects with public types: {projectCount}");
await writer.WriteLineAsync($"- Option types: {typesCount}");
await writer.WriteLineAsync();

foreach (var kvp in optiopns.Where(kvp => kvp.Value.Count > 1).OrderBy(kvp => kvp.Key))
{
    await writer.WriteLineAsync($"# {kvp.Key}");
    await writer.WriteLineAsync();
    foreach (var v in kvp.Value.OrderBy(v => v.ToDisplayString()))
    {
        var access = v.DeclaredAccessibility is Accessibility.Public ? "" : $" ({v.DeclaredAccessibility})";
        await writer.WriteLineAsync(
            $" - `{v.ToDisplayString()}` {access}");
    }

    await writer.WriteLineAsync();
}