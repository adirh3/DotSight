using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class GetSourceTextTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "get_source_text", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Read the source code of a symbol or a file region from the solution. Given a fully qualified symbol name, returns its complete source text including the body. Given a file path, returns the file contents (optionally scoped to a line range). Essential for reading implementation details beyond signatures.")]
    public static async Task<string> GetSourceText(
        WorkspaceService workspace,
        McpServer server,
        [Description("Fully qualified symbol name (e.g., 'MyNamespace.MyClass' or 'MyNamespace.MyClass.MyMethod'). Mutually exclusive with 'file'.")] string? fullyQualifiedName = null,
        [Description("File path relative to the solution directory. Mutually exclusive with 'fullyQualifiedName'.")] string? file = null,
        [Description("Start line (1-based). Only used with 'file'. Default: 1.")] int startLine = 1,
        [Description("End line (1-based, inclusive). Only used with 'file'. Default: end of file. Max 200 lines per request.")] int? endLine = null,
        [Description("Project name to search in when using fullyQualifiedName. If omitted, searches all projects.")] string? project = null,
        [Description("Solution or project file to load (e.g. 'MyApp.sln', 'MyApp.csproj'). If omitted, auto-detected.")] string? solution = null,
        CancellationToken ct = default)
    {
        workspace.SetServer(server);
        if (string.IsNullOrEmpty(fullyQualifiedName) && string.IsNullOrEmpty(file))
            return "Provide either 'fullyQualifiedName' or 'file'.";

        if (!string.IsNullOrEmpty(fullyQualifiedName) && !string.IsNullOrEmpty(file))
            return "Provide either 'fullyQualifiedName' or 'file', not both.";

        var sln = await workspace.GetSolutionAsync(solution, ct);
        var solutionDir = Path.GetDirectoryName(sln.FilePath) ?? "";

        if (!string.IsNullOrEmpty(file))
            return await GetSourceByFile(sln, solutionDir, file, startLine, endLine, ct);

        return await GetSourceBySymbol(sln, solutionDir, fullyQualifiedName!, project, ct);
    }

    private static async Task<string> GetSourceByFile(
        Solution sln, string solutionDir, string file, int startLine, int? endLine, CancellationToken ct)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(solutionDir, file));

        // Find the document in the solution
        Document? document = null;
        foreach (var proj in sln.Projects)
        {
            document = proj.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, absolutePath, StringComparison.OrdinalIgnoreCase));
            if (document is not null) break;
        }

        if (document is null)
        {
            // Fall back to reading from disk if file exists but isn't in the solution
            if (!File.Exists(absolutePath))
                return $"File '{file}' not found in the solution or on disk.";

            var diskText = await File.ReadAllLinesAsync(absolutePath, ct);
            return FormatLines(diskText, file, startLine, endLine);
        }

        var sourceText = await document.GetTextAsync(ct);
        var lines = sourceText.Lines.Select(l => l.ToString()).ToArray();
        return FormatLines(lines, file, startLine, endLine);
    }

    private static string FormatLines(string[] lines, string file, int startLine, int? endLine)
    {
        startLine = Math.Max(1, startLine);
        var effectiveEnd = endLine ?? lines.Length;
        effectiveEnd = Math.Min(effectiveEnd, lines.Length);

        // Cap at 200 lines per request
        if (effectiveEnd - startLine + 1 > 200)
            effectiveEnd = startLine + 199;

        var sb = new StringBuilder();
        for (int i = startLine - 1; i < effectiveEnd; i++)
        {
            sb.AppendLine($"{i + 1,5}: {lines[i]}");
        }

        var result = new
        {
            file,
            startLine,
            endLine = effectiveEnd,
            totalLines = lines.Length,
            source = sb.ToString()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static async Task<string> GetSourceBySymbol(
        Solution sln, string solutionDir, string fullyQualifiedName, string? project, CancellationToken ct)
    {
        var projects = string.IsNullOrEmpty(project)
            ? sln.Projects
            : sln.Projects.Where(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

        foreach (var proj in projects)
        {
            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;

            // Try as type
            ISymbol? symbol = WorkspaceService.ResolveType(compilation, fullyQualifiedName);

            // Try as member
            if (symbol is null)
            {
                var lastDot = fullyQualifiedName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var typePart = fullyQualifiedName[..lastDot];
                    var memberPart = fullyQualifiedName[(lastDot + 1)..];
                    symbol = WorkspaceService.ResolveMember(compilation, typePart, memberPart);
                }
            }

            if (symbol is null) continue;

            // Get source locations
            var sourceLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (sourceLocation is null)
                return $"Symbol '{fullyQualifiedName}' is from metadata and has no source code.";

            var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef is null)
                return $"Symbol '{fullyQualifiedName}' has no syntax reference available.";

            var syntaxNode = await syntaxRef.GetSyntaxAsync(ct);
            var sourceText = syntaxNode.SyntaxTree.GetText(ct);
            var span = syntaxNode.FullSpan;

            var startLinePos = sourceText.Lines.GetLinePosition(span.Start);
            var endLinePos = sourceText.Lines.GetLinePosition(span.End);
            var filePath = Path.GetRelativePath(solutionDir, syntaxNode.SyntaxTree.FilePath);

            // Build numbered source
            var sb = new StringBuilder();
            for (int i = startLinePos.Line; i <= endLinePos.Line; i++)
            {
                var line = sourceText.Lines[i];
                sb.AppendLine($"{i + 1,5}: {line}");
            }

            var result = new
            {
                symbol = fullyQualifiedName,
                kind = SymbolFormatter.GetKind(symbol),
                file = filePath,
                startLine = startLinePos.Line + 1,
                endLine = endLinePos.Line + 1,
                source = sb.ToString()
            };

            return JsonSerializer.Serialize(result, SerializerOptions);
        }

        return $"Symbol '{fullyQualifiedName}' not found. Check the fully qualified name and project scope.";
    }
}
