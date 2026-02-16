using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using DotSight.Services;

namespace DotSight.Tools;

[McpServerToolType]
public sealed class InspectPackageTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "inspect_package", ReadOnly = true, Destructive = false, OpenWorld = true),
     Description("Explore a NuGet package's public API without modifying the current project. " +
                 "Creates a temporary project, installs the package, and extracts all public types, methods, properties, and XML doc summaries. " +
                 "Use this to understand an unfamiliar NuGet package before adding it to your project.")]
    public static async Task<string> InspectPackage(
        WorkspaceService workspace,
        [Description("NuGet package name (e.g., 'MediatR', 'Polly', 'FluentValidation').")] string packageName,
        [Description("Optional package version. If omitted, uses the latest stable version.")] string? version = null,
        [Description("Filter types to a specific namespace prefix (e.g., 'MediatR' to skip internal namespaces).")] string? namespaceFilter = null,
        [Description("Filter by type kind: 'interface', 'class', 'struct', 'enum', 'delegate'. If omitted, returns all kinds.")] string? kindFilter = null,
        [Description("Maximum number of types to return. Default: 200.")] int maxTypes = 200,
        CancellationToken ct = default)
    {
        // Determine TFM from the current solution's first project
        var tfm = "net8.0"; // safe default
        try
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var firstProject = solution.Projects.FirstOrDefault();
            if (firstProject is not null)
            {
                var parseOptions = firstProject.ParseOptions as CSharpParseOptions;
                var langVersion = parseOptions?.LanguageVersion ?? LanguageVersion.Default;
                // Infer TFM from language version
                tfm = langVersion switch
                {
                    >= LanguageVersion.CSharp13 => "net10.0",
                    >= LanguageVersion.CSharp12 => "net9.0",
                    >= LanguageVersion.CSharp11 => "net8.0",
                    _ => "net8.0"
                };
            }
        }
        catch { /* use default TFM */ }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dotsight_inspect_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create a minimal project that references the package
            var versionAttr = $" Version=\"{EscapeXml(version ?? "*")}\"";
            var csproj = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{tfm}</TargetFramework>
                    <OutputType>Library</OutputType>
                    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{EscapeXml(packageName)}"{versionAttr} />
                  </ItemGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Inspect.csproj"), csproj, ct);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Empty.cs"), "// placeholder", ct);

            // Restore and build to get the DLLs
            var (exitCode, output) = await RunDotnetAsync("build --nologo -v q", tempDir, ct);
            if (exitCode != 0)
                return JsonSerializer.Serialize(new { error = "Failed to build temp project", details = TruncateOutput(output, 2000) }, SerializerOptions);

            // Find the output DLLs
            var outputDir = Path.Combine(tempDir, "bin", "Debug", tfm);
            if (!Directory.Exists(outputDir))
                return JsonSerializer.Serialize(new { error = $"Output directory not found: {outputDir}" }, SerializerOptions);

            // Load all assemblies as metadata references and create a compilation
            var metadataRefs = new List<MetadataReference>();
            foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(dll)); }
                catch { /* skip unloadable assemblies */ }
            }

            // Also add runtime assemblies for framework types
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            foreach (var runtimeDll in new[] { "System.Runtime.dll", "netstandard.dll" })
            {
                var path = Path.Combine(runtimeDir, runtimeDll);
                if (File.Exists(path))
                    metadataRefs.Add(MetadataReference.CreateFromFile(path));
            }

            var compilation = CSharpCompilation.Create("InspectAssembly",
                references: metadataRefs,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Find the package's assemblies (not framework/runtime ones)
            var packageAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                // Skip the placeholder assembly and common runtime assemblies
                if (name is "Inspect" or "System.Runtime" or "netstandard") continue;
                packageAssemblies.Add(name);
            }

            // Extract public API from package assemblies
            var types = new List<object>();
            var installedVersion = DetectInstalledVersion(tempDir, packageName);

            foreach (var asm in compilation.References)
            {
                var asmSymbol = compilation.GetAssemblyOrModuleSymbol(asm) as IAssemblySymbol;
                if (asmSymbol is null) continue;

                var asmName = asmSymbol.Name;
                if (!packageAssemblies.Contains(asmName)) continue;

                CollectPublicTypes(asmSymbol.GlobalNamespace, namespaceFilter, kindFilter, types, maxTypes);
            }

            var result = new Dictionary<string, object?>
            {
                ["package"] = packageName,
                ["version"] = installedVersion ?? version ?? "latest",
                ["typeCount"] = types.Count,
                ["types"] = types,
            };

            if (types.Count >= maxTypes)
            {
                result["truncated"] = true;
                result["hint"] = "Results truncated. Use namespaceFilter to focus on a specific namespace, or increase maxTypes.";

                // Collect all namespaces so the agent knows what to filter on
                var allNamespaces = new HashSet<string>();
                foreach (var asm in compilation.References)
                {
                    var asmSymbol = compilation.GetAssemblyOrModuleSymbol(asm) as IAssemblySymbol;
                    if (asmSymbol is null) continue;
                    if (!packageAssemblies.Contains(asmSymbol.Name)) continue;
                    CollectNamespaces(asmSymbol.GlobalNamespace, namespaceFilter, allNamespaces);
                }
                result["availableNamespaces"] = allNamespaces.OrderBy(n => n).ToList();
            }

            return JsonSerializer.Serialize(result, SerializerOptions);
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void CollectPublicTypes(
        INamespaceSymbol ns,
        string? namespaceFilter,
        string? kindFilter,
        List<object> types,
        int maxTypes)
    {
        if (types.Count >= maxTypes) return;

        foreach (var member in ns.GetMembers())
        {
            if (types.Count >= maxTypes) return;

            if (member is INamespaceSymbol childNs)
            {
                // Skip compiler-generated and internal namespaces
                if (childNs.Name.StartsWith("<") || childNs.Name is "Internal" or "Internals")
                    continue;

                CollectPublicTypes(childNs, namespaceFilter, kindFilter, types, maxTypes);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.DeclaredAccessibility != Accessibility.Public)
                    continue;

                // Apply namespace filter
                var typeNs = type.ContainingNamespace?.ToDisplayString() ?? "";
                if (namespaceFilter is not null &&
                    !typeNs.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Apply kind filter
                if (kindFilter is not null)
                {
                    var kind = type.TypeKind.ToString().ToLowerInvariant();
                    if (!string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                types.Add(FormatType(type));
            }
        }
    }

    private static void CollectNamespaces(
        INamespaceSymbol ns,
        string? namespaceFilter,
        HashSet<string> namespaces)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                if (childNs.Name.StartsWith("<") || childNs.Name is "Internal" or "Internals")
                    continue;

                var nsName = childNs.ToDisplayString();
                if (namespaceFilter is not null &&
                    !nsName.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only add namespaces that contain public types
                if (childNs.GetTypeMembers().Any(t => t.DeclaredAccessibility == Accessibility.Public))
                    namespaces.Add(nsName);

                CollectNamespaces(childNs, namespaceFilter, namespaces);
            }
        }
    }

    private static object FormatType(INamedTypeSymbol type)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = type.Name,
            ["namespace"] = type.ContainingNamespace?.ToDisplayString(),
            ["kind"] = type.TypeKind.ToString().ToLowerInvariant(),
            ["signature"] = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        };

        // XML doc summary
        var summary = SymbolFormatter.GetXmlDocSummary(type);
        if (!string.IsNullOrEmpty(summary))
            result["summary"] = summary;

        // Base type (for classes)
        if (type.BaseType is not null &&
            type.BaseType.SpecialType == SpecialType.None &&
            type.BaseType.Name != "Object")
        {
            result["baseType"] = type.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        // Interfaces
        var interfaces = type.Interfaces
            .Where(i => i.DeclaredAccessibility == Accessibility.Public)
            .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToList();
        if (interfaces.Count > 0)
            result["interfaces"] = interfaces;

        // Public members (methods, properties)
        var members = new List<object>();
        foreach (var m in type.GetMembers())
        {
            if (m.DeclaredAccessibility != Accessibility.Public) continue;
            if (m.IsImplicitlyDeclared) continue;
            if (m is IMethodSymbol ms && ms.MethodKind is
                MethodKind.PropertyGet or MethodKind.PropertySet or
                MethodKind.EventAdd or MethodKind.EventRemove)
                continue;

            var memberEntry = new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["kind"] = SymbolFormatter.GetKind(m),
                ["signature"] = SymbolFormatter.GetSignature(m),
            };

            var memberSummary = SymbolFormatter.GetXmlDocSummary(m);
            if (!string.IsNullOrEmpty(memberSummary))
                memberEntry["summary"] = memberSummary;

            members.Add(memberEntry);
        }

        if (members.Count > 0)
            result["members"] = members;

        return result;
    }

    private static async Task<(int exitCode, string output)> RunDotnetAsync(string arguments, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var outTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                stdout.AppendLine(line);
        }, ct);

        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
                stderr.AppendLine(line);
        }, ct);

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outTask, errTask);

        var output = stdout.ToString();
        if (stderr.Length > 0)
            output += "\n--- stderr ---\n" + stderr;

        return (process.ExitCode, output);
    }

    private static string? DetectInstalledVersion(string tempDir, string packageName)
    {
        // Try to find the version from the obj/project.assets.json
        var assetsPath = Path.Combine(tempDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath)) return null;

        try
        {
            var json = File.ReadAllText(assetsPath);
            // Simple pattern match: "PackageName/1.2.3"
            var pattern = $"\"{packageName}/";
            var idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var start = idx + pattern.Length;
            var end = json.IndexOf('"', start);
            if (end < 0) return null;

            return json[start..end];
        }
        catch { return null; }
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string TruncateOutput(string output, int maxLength) =>
        output.Length <= maxLength ? output : output[..maxLength] + "... (truncated)";
}
