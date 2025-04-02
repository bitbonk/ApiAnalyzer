using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length == 0)
{
    Console.WriteLine("Usage: ListPublicSubscriptionApis <solution-path>");
    return;
}

var solutionPath = args[0];

MSBuildLocator.RegisterDefaults();

using var workspace = MSBuildWorkspace.Create();

Console.WriteLine($"Loading solution: {solutionPath}");
var solution = await workspace.OpenSolutionAsync(solutionPath);

var publicTypes = new Dictionary<string, HashSet<INamedTypeSymbol>>();
var publicMembers = new Dictionary<INamedTypeSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);

var solutionDir = Path.GetDirectoryName(solution.FilePath);

foreach (var project in solution.Projects.OrderBy(p => p.FilePath))
{
    var relativeProjectPath = project.FilePath![(solutionDir!.Length + 1)..];
    if (!relativeProjectPath.StartsWith("src")) continue;

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

        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(typeSyntax);
            if (typeSymbol is {DeclaredAccessibility: Accessibility.Public, ContainingType: null})
            {
                foreach (var publicMember in typeSymbol.GetMembers()
                             .Where(member => member.DeclaredAccessibility == Accessibility.Public
                                              && member.Name.Contains("subscribe", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!publicTypes.ContainsKey(relativeProjectPath))
                    {
                        publicTypes[relativeProjectPath] = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    }
                    
                    publicTypes[relativeProjectPath].Add(typeSymbol);

                    if (!publicMembers.ContainsKey(typeSymbol))
                    {
                        publicMembers[typeSymbol] = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    }
                    
                    publicMembers[typeSymbol].Add(publicMember);
                }
            }
        }
    }
}

var output = "c:/temp/public-subscription-members.md";

var typeCount = publicTypes.Values.Sum(set => set.Count);
Console.WriteLine($"Writing members of {typeCount} types from {publicTypes.Count} projects to file {output}");

await using var writer = new StreamWriter(output);

await writer.WriteLineAsync($"- Solution: {solution.FilePath}");
await writer.WriteLineAsync($"- Projects: {publicTypes.Count}");
await writer.WriteLineAsync($"- Types: {typeCount}");
await writer.WriteLineAsync($"- Members: {publicMembers.Values.Sum(set => set.Count)}");
await writer.WriteLineAsync();

foreach (var kvp in publicTypes.Where(kvp => kvp.Value.Count > 1).OrderBy(kvp => kvp.Key))
{
    await writer.WriteLineAsync($"# {kvp.Key}");
    await writer.WriteLineAsync();
    foreach (var v in kvp.Value.OrderBy(v => v.ToDisplayString()))
    {
        await writer.WriteLineAsync($"## `{v.ToDisplayString()}`");
        await writer.WriteLineAsync();

        foreach (var member in publicMembers[v].OrderBy(m => m.ToDisplayString()))
            await writer.WriteLineAsync($"- `{member.ToDisplayString()}`");
        
        await writer.WriteLineAsync();
    }
}