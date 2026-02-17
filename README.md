# DotSight

An MCP (Model Context Protocol) server that provides C# solution intelligence to AI coding agents. Built with .NET 10, Roslyn, and the official [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) SDK.

## Tools

| Tool | Description |
|---|---|
| `get_project_graph` | Full codebase overview: project dependency graph, TFMs, packages, source files, and code outlines with types and members |
| `find_symbols` | Search for types, methods, properties by name pattern or regex across source and metadata |
| `get_symbol_detail` | Full detail for a symbol: signature, docs, members, base types, extension methods |
| `get_source_text` | Read the actual source code of a symbol or file region with line numbers |
| `get_document_symbols` | File outline — all symbols defined in a source file with kind, signature, and line numbers |
| `find_references` | All usages of a symbol across the solution with location info |
| `find_implementations` | Concrete implementations of interfaces, abstract classes, virtual members |
| `get_diagnostics` | Compiler errors, warnings, and analyzer diagnostics by scope |
| `inspect_package` | Explore any NuGet package's public API — types, members, docs — without modifying the project |

All tools are **read-only** — no modifications to source code or projects.

## Installation

```bash
dotnet tool install -g DotSight --prerelease
```

## Setup

Add DotSight to your VS Code **user MCP config** (one-time setup, works for all C# workspaces):

`Ctrl+Shift+P` → "MCP: Open User Configuration":

```json
{
  "servers": {
    "dotsight": {
      "type": "stdio",
      "command": "dotsight"
    }
  }
}
}
```

DotSight auto-detects the `.sln` file from the workspace directory. If you have multiple solutions or need to specify one explicitly, use `--solution`:

```json
{
  "mcp": {
    "servers": {
      "dotsight": {
        "type": "stdio",
        "command": "dotsight",
        "args": ["--solution", "${workspaceFolder}/MyApp.sln"]
      }
    }
  }
}
```

You can also use a per-workspace `.vscode/mcp.json` if you prefer.

## Running from source

```bash
dotnet run -- --solution /path/to/YourSolution.sln
```

## Building

```bash
dotnet build
dotnet pack   # produces a .nupkg for tool installation
```
