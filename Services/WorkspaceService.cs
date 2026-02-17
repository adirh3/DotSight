using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotSight.Services;

public sealed class WorkspaceService : IDisposable
{
    private readonly WorkspaceOptions _options;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _resolvedSolutionPath;
    private DateTime _loadedAt;
    private McpServer? _server;
    private string? _shadowCopyDir;

    public WorkspaceService(WorkspaceOptions options, ILogger<WorkspaceService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Sets the MCP server reference for roots-based workspace discovery.
    /// Called once from tool invocations that have the server injected.
    /// </summary>
    public void SetServer(McpServer server) => _server ??= server;

    /// <summary>
    /// Gets (or loads) the solution. If <paramref name="solution"/> is specified,
    /// it overrides any previously loaded solution. Accepts a .sln, .slnx, or .csproj
    /// filename (resolved relative to workspace root) or an absolute path.
    /// </summary>
    public async Task<Solution> GetSolutionAsync(string? solution = null, CancellationToken ct = default)
    {
        var requestedPath = await ResolveSolutionArgAsync(solution, ct);

        // If a specific solution was requested and it differs from current, force reload
        if (requestedPath is not null && _resolvedSolutionPath is not null &&
            !string.Equals(requestedPath, _resolvedSolutionPath, StringComparison.OrdinalIgnoreCase))
        {
            await ReloadSolutionAsync(requestedPath, ct);
            return _solution!;
        }

        if (_solution is not null && !ProjectFilesChanged())
            return _solution;

        await _gate.WaitAsync(ct);
        try
        {
            if (_solution is not null && !ProjectFilesChanged())
                return _solution;

            if (_solution is not null)
                _logger.LogInformation("Project files changed on disk, reloading solution");

            var pathToLoad = requestedPath ?? _resolvedSolutionPath ?? await DiscoverSolutionPathAsync(ct);
            await LoadSolutionCoreAsync(pathToLoad, ct);
            return _solution!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReloadSolutionAsync(string solutionPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Switching solution to: {Path}", solutionPath);
            await LoadSolutionCoreAsync(solutionPath, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadSolutionCoreAsync(string path, CancellationToken ct)
    {
        _workspace?.Dispose();
        _resolvedSolutionPath = path;
        _logger.LogInformation("Opening: {Path}", _resolvedSolutionPath);
        _workspace = MSBuildWorkspace.Create();
        _workspace.RegisterWorkspaceFailedHandler(e =>
            _logger.LogWarning("Workspace warning: {Message}", e.Diagnostic.Message));

        var ext = Path.GetExtension(path);
        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await _workspace.OpenProjectAsync(path, cancellationToken: ct);
            _solution = project.Solution;
        }
        else
        {
            _solution = await _workspace.OpenSolutionAsync(path, cancellationToken: ct);
        }

        _loadedAt = DateTime.UtcNow;
        _solution = ShadowCopyAnalyzerReferences(_solution);
        _logger.LogInformation("Loaded: {Count} projects", _solution.ProjectIds.Count);
    }

    /// <summary>
    /// Resolves a user-provided solution/project argument to an absolute path.
    /// Accepts .sln, .slnx, .csproj filenames, relative paths, or absolute paths.
    /// Returns null if no value was specified.
    /// </summary>
    private async Task<string?> ResolveSolutionArgAsync(string? solution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(solution))
            return null;

        // Already absolute
        if (Path.IsPathRooted(solution) && File.Exists(solution))
            return Path.GetFullPath(solution);

        // Try resolving relative to workspace roots
        var rootDirs = await GetWorkspaceRootDirsAsync(ct);
        foreach (var rootDir in rootDirs)
        {
            var candidate = Path.GetFullPath(Path.Combine(rootDir, solution));
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            $"'{solution}' not found. Provide a valid .sln, .slnx, or .csproj filename or path.");
    }

    /// <summary>
    /// Checks whether any solution/project files have been modified since we last loaded.
    /// </summary>
    private bool ProjectFilesChanged()
    {
        if (_loadedAt == default)
            return true;

        // Check the solution file itself
        var solutionPath = _resolvedSolutionPath;
        if (solutionPath is not null && File.Exists(solutionPath) && File.GetLastWriteTimeUtc(solutionPath) > _loadedAt)
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
        var sln = await GetSolutionAsync(ct: ct);
        var project = sln.GetProject(projectId);
        return project is null ? null : await project.GetCompilationAsync(ct);
    }

    public async Task<IReadOnlyList<(Project Project, Compilation Compilation)>> GetAllCompilationsAsync(CancellationToken ct = default)
    {
        var sln = await GetSolutionAsync(ct: ct);
        var results = new List<(Project, Compilation)>();
        foreach (var project in sln.Projects)
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
        var sln = await GetSolutionAsync(ct: ct);
        return sln.Projects.FirstOrDefault(p =>
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
        CleanupShadowDir();
    }

    /// <summary>
    /// Replaces analyzer/generator references with non-locking versions so that
    /// DotSight never holds file locks on DLLs in project bin/obj directories.
    /// On Windows: shadow-copies DLLs that are in writable locations (bin/obj).
    /// On Linux/macOS: uses LoadFromStream which doesn't lock files at all.
    /// DLLs in read-only locations (NuGet cache, dotnet runtime) are loaded directly
    /// since they're never overwritten by builds.
    /// </summary>
    private Solution ShadowCopyAnalyzerReferences(Solution solution)
    {
        CleanupShadowDir();

        IAnalyzerAssemblyLoader loader;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _shadowCopyDir = Path.Combine(Path.GetTempPath(), "dotsight", "shadow", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_shadowCopyDir);
            loader = new ShadowCopyAnalyzerLoader(_shadowCopyDir);
        }
        else
        {
            loader = new StreamAnalyzerLoader();
        }

        foreach (var project in solution.Projects)
        {
            if (!project.AnalyzerReferences.Any(r => r is AnalyzerFileReference))
                continue;

            var newRefs = project.AnalyzerReferences
                .Select(r => r is AnalyzerFileReference afr
                    ? (AnalyzerReference)new AnalyzerFileReference(afr.FullPath, loader)
                    : r)
                .ToList();
            solution = solution.WithProjectAnalyzerReferences(project.Id, newRefs);
        }

        return solution;
    }

    private void CleanupShadowDir()
    {
        if (_shadowCopyDir is not null)
        {
            try { Directory.Delete(_shadowCopyDir, true); } catch { }
            _shadowCopyDir = null;
        }
    }

    /// <summary>
    /// Determines whether a DLL path is in a read-only location (NuGet cache, dotnet runtime,
    /// Program Files) where files are never overwritten by builds — no shadow copy needed.
    /// </summary>
    private static bool IsReadOnlyLocation(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');

        // NuGet package cache — packages are immutable once restored
        if (normalized.Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase))
            return true;

        // .NET SDK/runtime directories — never modified by user builds
        var dotnetRoot = Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory())?.Replace('\\', '/');
        if (dotnetRoot is not null && normalized.StartsWith(dotnetRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        // Program Files on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).Replace('\\', '/');
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).Replace('\\', '/');
            if ((!string.IsNullOrEmpty(pf) && normalized.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(pf86) && normalized.StartsWith(pf86, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Windows: shadow-copies writable DLLs to a temp directory, loads read-only ones directly.
    /// </summary>
    private sealed class ShadowCopyAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        private readonly string _shadowDir;

        public ShadowCopyAnalyzerLoader(string shadowDir) => _shadowDir = shadowDir;

        public void AddDependencyLocation(string fullPath)
        {
            if (!File.Exists(fullPath) || IsReadOnlyLocation(fullPath))
                return;

            var dest = Path.Combine(_shadowDir, Path.GetFileName(fullPath));
            if (!File.Exists(dest))
                try { File.Copy(fullPath, dest); } catch { }
        }

        public Assembly LoadFromPath(string fullPath)
        {
            // Read-only locations: load directly — no locking concern
            if (IsReadOnlyLocation(fullPath))
                return Assembly.LoadFrom(fullPath);

            // Writable locations (bin/obj): shadow copy first
            var dest = Path.Combine(_shadowDir, Path.GetFileName(fullPath));
            if (!File.Exists(dest))
                File.Copy(fullPath, dest);
            return Assembly.LoadFrom(dest);
        }
    }

    /// <summary>
    /// Non-Windows: loads assemblies from a stream so the file is never locked.
    /// </summary>
    private sealed class StreamAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath)
        {
            var bytes = File.ReadAllBytes(fullPath);
            return Assembly.Load(bytes);
        }
    }

    private async Task<string> DiscoverSolutionPathAsync(CancellationToken ct)
    {
        // If --solution was passed at startup, use it directly
        if (_options.SolutionPath is not null)
            return _options.SolutionPath;

        // Try MCP roots first — the client (VS Code) knows the workspace folders
        var rootDirs = await GetWorkspaceRootDirsAsync(ct);
        var allSlnFiles = new List<string>();

        foreach (var rootDir in rootDirs)
        {
            var dir = new DirectoryInfo(rootDir);
            if (!dir.Exists) continue;

            var slnFiles = dir.GetFiles("*.sln")
                .Concat(dir.GetFiles("*.slnx"))
                .ToArray();
            if (slnFiles.Length == 1)
            {
                _logger.LogInformation("Found solution via MCP roots: {Path}", slnFiles[0].FullName);
                return slnFiles[0].FullName;
            }
            allSlnFiles.AddRange(slnFiles.Select(f => f.FullName));
        }

        if (allSlnFiles.Count > 1)
        {
            var names = string.Join(", ", allSlnFiles.Select(Path.GetFileName));
            throw new InvalidOperationException(
                $"Multiple solutions found: {names}. Specify which one using the 'solution' parameter (e.g. solution=\"{Path.GetFileName(allSlnFiles[0])}\").");
        }

        if (allSlnFiles.Count == 1)
            return allSlnFiles[0];

        // Fall back to searching from current directory upward
        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (cwd is not null)
        {
            var slnFiles = cwd.GetFiles("*.sln")
                .Concat(cwd.GetFiles("*.slnx"))
                .ToArray();
            if (slnFiles.Length == 1)
                return slnFiles[0].FullName;
            if (slnFiles.Length > 1)
            {
                var names = string.Join(", ", slnFiles.Select(f => f.Name));
                throw new InvalidOperationException(
                    $"Multiple solutions found: {names}. Specify which one using the 'solution' parameter.");
            }
            cwd = cwd.Parent;
        }

        throw new InvalidOperationException(
            "No .sln/.slnx file found. Open a workspace containing a solution, or pass --solution <path> in the MCP server args.");
    }

    /// <summary>
    /// Returns local directory paths from MCP roots (workspace folders).
    /// </summary>
    private async Task<List<string>> GetWorkspaceRootDirsAsync(CancellationToken ct)
    {
        var dirs = new List<string>();
        try
        {
            if (_server is not null)
            {
                var roots = await _server.RequestRootsAsync(new ListRootsRequestParams(), ct);
                foreach (var root in roots.Roots)
                {
                    var localPath = FileUriToLocalPath(root.Uri);
                    if (localPath is not null)
                        dirs.Add(localPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP roots discovery failed");
        }
        return dirs;
    }

    /// <summary>
    /// Converts a file:// URI to a local filesystem path.
    /// Handles VS Code's encoding of Windows drive letters (e.g. file:///e%3A/Git/Foo → E:\Git\Foo).
    /// </summary>
    private static string? FileUriToLocalPath(string uriString)
    {
        if (!uriString.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return null;

        // Decode percent-encoding first so %3A becomes :
        var decoded = Uri.UnescapeDataString(uriString);

        // Now parse the decoded URI — drive colons are plain text
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri) || !uri.IsFile)
            return null;

        return uri.LocalPath;
    }
}
