using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class FindReferencesTool
{
    [McpServerTool(Name = "find_references", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Find all references (usages) of a symbol across the solution. Returns each reference location classified as read, write, or declaration.")]
    public static async Task<string> FindReferences(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified name of the symbol to find references for (e.g., 'MyNamespace.MyClass' or 'MyNamespace.MyClass.MyMethod').")] string fullyQualifiedName,
        [Description("Project name where the symbol is defined. If omitted, searches all projects.")] string? project = null,
        [Description("Maximum number of reference locations to return. Default: 100.")] int maxResults = 100,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        var sln = await workspace.GetSolutionAsync(solution, ct);
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";

        // First, resolve the symbol
        ISymbol? targetSymbol = null;
        Project? targetProject = null;

        var projects = string.IsNullOrEmpty(project)
            ? sln.Projects
            : sln.Projects.Where(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

        foreach (var proj in projects)
        {
            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Try as type
            targetSymbol = WorkspaceService.ResolveType(compilation, fullyQualifiedName);
            if (targetSymbol is not null)
            {
                targetProject = proj;
                break;
            }

            // Try as member
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

        // Find all references
        var references = await SymbolFinder.FindReferencesAsync(targetSymbol, sln, ct);
        var locations = new List<object>();

        foreach (var refGroup in references)
        {
            // Add the definition locations
            foreach (var defLocation in refGroup.Definition.Locations)
            {
                if (locations.Count >= maxResults) break;
                if (!defLocation.IsInSource) continue;
                locations.Add(FormatReferenceLocation(defLocation, "declaration", solutionDir, sln));
            }

            // Add reference locations
            foreach (var refLocation in refGroup.Locations)
            {
                if (locations.Count >= maxResults) break;
                var loc = refLocation.Location;
                if (!loc.IsInSource) continue;

                var classification = ClassifyReference(refLocation);
                locations.Add(FormatReferenceLocation(loc, classification, solutionDir, sln));
            }
        }

        if (locations.Count == 0)
            return $"No references found for '{fullyQualifiedName}'.";

        var result = new
        {
            symbol = new
            {
                name = targetSymbol.Name,
                kind = SymbolFormatter.GetKind(targetSymbol),
                fullyQualifiedName = SymbolFormatter.GetFullyQualifiedName(targetSymbol),
                project = targetProject?.Name
            },
            totalReferences = locations.Count,
            references = locations
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static object FormatReferenceLocation(Location location, string classification, string solutionDir, Solution sln)
    {
        var span = location.GetLineSpan();
        var filePath = Path.GetRelativePath(solutionDir, span.Path);

        // Try to get the containing project
        var documentId = sln.GetDocumentIdsWithFilePath(span.Path).FirstOrDefault();
        var projectName = documentId is not null ? sln.GetProject(documentId.ProjectId)?.Name : null;

        return new
        {
            file = filePath,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
            endLine = span.EndLinePosition.Line + 1,
            endColumn = span.EndLinePosition.Character + 1,
            classification,
            project = projectName
        };
    }

    private static string ClassifyReference(ReferenceLocation refLocation)
    {
        if (refLocation.IsImplicit)
            return "implicit";
        return "reference";
    }
}
