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
var publicMembers =
    new Dictionary<INamedTypeSymbol, Dictionary<ISymbol, INamedTypeSymbol>>(SymbolEqualityComparer.Default);

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
                foreach (var publicMember in GetMembersIncludingDerived(typeSymbol)
                             .Where(member => member.Member.DeclaredAccessibility == Accessibility.Public
                                              && member.Member.Name.Contains("subscribe",
                                                  StringComparison.OrdinalIgnoreCase)))
                {
                    if (!publicTypes.ContainsKey(relativeProjectPath))
                        publicTypes[relativeProjectPath] =
                            new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

                    publicTypes[relativeProjectPath].Add(typeSymbol);

                    if (!publicMembers.ContainsKey(typeSymbol))
                        publicMembers[typeSymbol] =
                            new Dictionary<ISymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);

                    publicMembers[typeSymbol].Add(publicMember.Member, publicMember.Type);
                }
        }
    }
}

static IEnumerable<(ISymbol Member, INamedTypeSymbol Type)> GetMembersIncludingDerived(INamedTypeSymbol typeSymbol)
{
    var processedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    var typesToProcess = new Stack<INamedTypeSymbol>();
    typesToProcess.Push(typeSymbol);

    while (typesToProcess.Count > 0)
    {
        var currentType = typesToProcess.Pop();

        // Skip if already processed
        if (!processedTypes.Add(currentType))
        {
            continue;
        }

        // Yield all members of the current type
        foreach (var member in currentType.GetMembers())
        {
            yield return (member, currentType);
        }

        // Enqueue base type and implemented interfaces
        if (currentType.BaseType != null)
        {
            typesToProcess.Push(currentType.BaseType);
        }

        foreach (var interfaceType in currentType.Interfaces)
        {
            typesToProcess.Push(interfaceType);
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

foreach (var kvp in publicTypes.Where(kvp => kvp.Value.Count > 0).OrderBy(kvp => kvp.Key))
{
    await writer.WriteLineAsync($"# {kvp.Key}");
    await writer.WriteLineAsync();
    foreach (var typeWithMembers in kvp.Value.OrderBy(v => v.ToDisplayString()))
    {
        await writer.WriteLineAsync($"## `{typeWithMembers.ToDisplayString()}`");
        await writer.WriteLineAsync();

        foreach (var member in publicMembers[typeWithMembers]
                     .OrderBy(m => m.Key.ToDisplayString()))
        {
            var baseType = SymbolEqualityComparer.Default.Equals(typeWithMembers, member.Value) ? "" : $" (inherited from `{member.Value.Name}`)";
            await writer.WriteLineAsync($"- `{member.Key.ToDisplayString()}`{baseType}");
        }

        await writer.WriteLineAsync();
    }
}