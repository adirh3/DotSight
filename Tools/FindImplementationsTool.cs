using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class FindImplementationsTool
{
    [McpServerTool(Name = "find_implementations", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Find concrete implementations of an interface, abstract class, or virtual/abstract member. Returns each implementing type or member with its location.")]
    public static async Task<string> FindImplementations(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified name of the interface, abstract class, or member (e.g., 'MyNamespace.IMyInterface' or 'MyNamespace.IMyInterface.MyMethod').")] string fullyQualifiedName,
        [Description("Project name where the symbol is defined. If omitted, searches all projects.")] string? project = null,
        [Description("Maximum number of implementations to return. Default: 50.")] int maxResults = 50,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        var sln = await workspace.GetSolutionAsync(solution, ct);
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";

        // Resolve the symbol
        ISymbol? targetSymbol = null;
        Project? targetProject = null;

        var projects = string.IsNullOrEmpty(project)
            ? sln.Projects
            : sln.Projects.Where(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

        foreach (var proj in projects)
        {
            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;

            targetSymbol = WorkspaceService.ResolveType(compilation, fullyQualifiedName);
            if (targetSymbol is not null)
            {
                targetProject = proj;
                break;
            }

            var lastDot = fullyQualifiedName.LastIndexOf('.');
            if (lastDot > 0)
            {
                var typePart = fullyQualifiedName[..lastDot];
                var memberPart = fullyQualifiedName[(lastDot + 1)..];
                targetSymbol = WorkspaceService.ResolveMember(compilation, typePart, memberPart);
                if (targetSymbol is not null)
                {
                    targetProject = proj;
                    break;
                }
            }
        }

        if (targetSymbol is null)
            return $"Symbol '{fullyQualifiedName}' not found. Check the fully qualified name and project scope.";

        var implementations = new List<object>();

        if (targetSymbol is INamedTypeSymbol typeSymbol)
        {
            // Find implementations of a type
            IEnumerable<INamedTypeSymbol> impls;

            if (typeSymbol.TypeKind == TypeKind.Interface)
                impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, sln, cancellationToken: ct);
            else
                impls = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, sln, cancellationToken: ct);

            foreach (var impl in impls.Take(maxResults))
            {
                implementations.Add(FormatImplementation(impl, solutionDir, sln));
            }
        }
        else if (targetSymbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            // Find overrides of a member
            var overrides = await SymbolFinder.FindOverridesAsync(targetSymbol, sln, cancellationToken: ct);
            foreach (var ov in overrides.Take(maxResults))
            {
                implementations.Add(FormatImplementation(ov, solutionDir, sln));
            }

            // Also find interface implementations
            if (targetSymbol.ContainingType?.TypeKind == TypeKind.Interface)
            {
                var memberImpls = await SymbolFinder.FindImplementationsAsync(targetSymbol, sln, cancellationToken: ct);
                foreach (var impl in memberImpls.Take(maxResults - implementations.Count))
                {
                    implementations.Add(FormatImplementation(impl, solutionDir, sln));
                }
            }
        }

        if (implementations.Count == 0)
            return $"No implementations found for '{fullyQualifiedName}'.";

        var result = new
        {
            symbol = new
            {
                name = targetSymbol.Name,
                kind = SymbolFormatter.GetKind(targetSymbol),
                fullyQualifiedName = SymbolFormatter.GetFullyQualifiedName(targetSymbol),
                project = targetProject?.Name
            },
            totalImplementations = implementations.Count,
            implementations
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static object FormatImplementation(ISymbol symbol, string solutionDir, Solution sln)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? symbol.Locations.FirstOrDefault();

        var info = new Dictionary<string, object?>
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolFormatter.GetKind(symbol),
            ["fullyQualifiedName"] = SymbolFormatter.GetFullyQualifiedName(symbol),
            ["signature"] = SymbolFormatter.GetSignature(symbol),
            ["isAbstract"] = symbol is INamedTypeSymbol { IsAbstract: true },
        };

        if (location is not null)
        {
            info["location"] = SymbolFormatter.FormatLocation(location, solutionDir);

            if (location.IsInSource)
            {
                var documentId = sln.GetDocumentIdsWithFilePath(location.GetLineSpan().Path).FirstOrDefault();
                if (documentId is not null)
                    info["project"] = sln.GetProject(documentId.ProjectId)?.Name;
            }
        }

        return info;
    }
}
