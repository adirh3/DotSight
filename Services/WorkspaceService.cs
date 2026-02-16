using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace DotSight.Services;

public sealed class WorkspaceService : IDisposable
{
    private readonly WorkspaceOptions _options;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private DateTime _loadedAt;

    public WorkspaceService(WorkspaceOptions options, ILogger<WorkspaceService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<Solution> GetSolutionAsync(CancellationToken ct = default)
    {
        if (_solution is not null && !ProjectFilesChanged())
            return _solution;

        await _gate.WaitAsync(ct);
        try
        {
            if (_solution is not null && !ProjectFilesChanged())
                return _solution;

            if (_solution is not null)
                _logger.LogInformation("Project files changed on disk, reloading solution");

            _workspace?.Dispose();
            _logger.LogInformation("Opening solution: {Path}", _options.SolutionPath);
            _workspace = MSBuildWorkspace.Create();
            _workspace.WorkspaceFailed += (_, e) =>
                _logger.LogWarning("Workspace warning: {Message}", e.Diagnostic.Message);

            _solution = await _workspace.OpenSolutionAsync(_options.SolutionPath, cancellationToken: ct);
            _loadedAt = DateTime.UtcNow;
            _logger.LogInformation("Solution loaded: {Count} projects", _solution.ProjectIds.Count);
            return _solution;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks whether any solution/project files have been modified since we last loaded.
    /// </summary>
    private bool ProjectFilesChanged()
    {
        if (_loadedAt == default)
            return true;

        // Check the solution file itself
        var solutionPath = _options.SolutionPath;
        if (File.Exists(solutionPath) && File.GetLastWriteTimeUtc(solutionPath) > _loadedAt)
            return true;

        // Check all project files in the loaded solution
        if (_solution is not null)
        {
            foreach (var project in _solution.Projects)
            {
                if (project.FilePath is not null &&
                    File.Exists(project.FilePath) &&
                    File.GetLastWriteTimeUtc(project.FilePath) > _loadedAt)
                    return true;
            }
        }

        return false;
    }

    public async Task<Compilation?> GetCompilationAsync(ProjectId projectId, CancellationToken ct = default)
    {
        var solution = await GetSolutionAsync(ct);
        var project = solution.GetProject(projectId);
        return project is null ? null : await project.GetCompilationAsync(ct);
    }

    public async Task<IReadOnlyList<(Project Project, Compilation Compilation)>> GetAllCompilationsAsync(CancellationToken ct = default)
    {
        var solution = await GetSolutionAsync(ct);
        var results = new List<(Project, Compilation)>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null)
                results.Add((project, compilation));
        }
        return results;
    }

    /// <summary>
    /// Finds a project by name (case-insensitive).
    /// </summary>
    public async Task<Project?> FindProjectAsync(string projectName, CancellationToken ct = default)
    {
        var solution = await GetSolutionAsync(ct);
        return solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a symbol from a fully qualified name within a project's compilation.
    /// </summary>
    public static INamedTypeSymbol? ResolveType(Compilation compilation, string fullyQualifiedName)
    {
        return compilation.GetTypeByMetadataName(fullyQualifiedName);
    }

    /// <summary>
    /// Resolves a symbol by fully qualified name, searching all accessible types in a compilation.
    /// Supports member lookup with "TypeName.MemberName" syntax.
    /// </summary>
    public static ISymbol? ResolveMember(Compilation compilation, string fullyQualifiedTypeName, string memberName)
    {
        var type = ResolveType(compilation, fullyQualifiedTypeName);
        if (type is null) return null;
        return type.GetMembers(memberName).FirstOrDefault();
    }

    public void Dispose()
    {
        _workspace?.Dispose();
    }
}
