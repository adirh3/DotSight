using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DotSight.Services;

// MSBuild locator must be registered before any Roslyn/MSBuild types are loaded
MSBuildLocator.RegisterDefaults();

var builder = Host.CreateApplicationBuilder(args);

// MCP servers use stdio — redirect all logging to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Parse --solution argument
var solutionPath = GetSolutionPath(args);
builder.Services.AddSingleton(new WorkspaceOptions(solutionPath));
builder.Services.AddSingleton<WorkspaceService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "DotSight",
            Version = "0.1.0"
        };
        options.ServerInstructions = """
            C# solution intelligence server. IMPORTANT: Always use these tools instead of reading files directly — they provide richer, semantic information than raw source code.

            FIRST CALL: get_project_graph(includeOutlines=true) — returns the complete codebase architecture in ONE call: all projects, dependencies, packages, every source file, and a full outline of all types with their members. Always call this before reading individual files.

            Then use targeted tools:
            - find_symbols(pattern, includeMetadata=true) — regex search across source and NuGet metadata
            - get_symbol_detail — full symbol info: members, base types, interfaces, docs
            - get_source_text — read implementation code by symbol name or file+line range
            - get_document_symbols — IDE-style file outline
            - find_references / find_implementations — navigate the code graph
            - get_diagnostics — compiler errors, warnings, and analyzer issues
            - inspect_package — explore any NuGet package's public API without modifying the project
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static string GetSolutionPath(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--solution" or "-s")
            return Path.GetFullPath(args[i + 1]);
    }

    // Try to find a .sln in the current directory
    var slnFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.sln");
    if (slnFiles.Length == 1)
        return slnFiles[0];

    throw new InvalidOperationException(
        "No solution specified. Use --solution <path> or run from a directory with a single .sln file.");
}
