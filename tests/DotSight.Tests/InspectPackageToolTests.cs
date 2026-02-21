using System.Text.Json;
using DotSight.Services;
using DotSight.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotSight.Tests;

public class InspectPackageToolTests
{
    private const string PackageName = "Microsoft.CodeAnalysis.CSharp";
    private const string NamespaceFilter = "Microsoft.CodeAnalysis.CSharp";

    [Fact]
    public async Task InspectPackage_UsesInstalledVersion_WhenVersionNotProvided()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            includeMembers: false,
            maxMembersPerType: 50,
            maxTypes: 10);

        AssertNoError(root);
        Assert.Equal(PackageName, root.GetProperty("package").GetString());
        Assert.True(root.GetProperty("resolvedFromInstalledPackage").GetBoolean());

        var version = root.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("latest", version);

        Assert.True(root.GetProperty("typeCount").GetInt32() > 0);
    }

    [Fact]
    public async Task InspectPackage_MemberFilters_ReturnOnlyMatchingMembers()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            memberFilter: "ParseText",
            memberKindFilter: "method",
            includeMembers: true,
            maxMembersPerType: 25,
            maxTypes: 10);

        AssertNoError(root);

        var types = root.GetProperty("types").EnumerateArray().ToList();
        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            Assert.True(type.TryGetProperty("members", out var members));
            var memberList = members.EnumerateArray().ToList();
            Assert.NotEmpty(memberList);

            foreach (var member in memberList)
            {
                Assert.Equal("method", member.GetProperty("kind").GetString());
                Assert.Contains("ParseText", member.GetProperty("name").GetString()!, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task InspectPackage_QueryFilter_MatchesSpecificMemberText()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            query: "ParseText",
            includeMembers: true,
            maxMembersPerType: 50,
            maxTypes: 10);

        AssertNoError(root);

        var types = root.GetProperty("types").EnumerateArray().ToList();
        Assert.NotEmpty(types);

        var sawParseText = false;
        foreach (var type in types)
        {
            Assert.True(type.TryGetProperty("members", out var members));
            foreach (var member in members.EnumerateArray())
            {
                var name = member.GetProperty("name").GetString() ?? string.Empty;
                var signature = member.GetProperty("signature").GetString() ?? string.Empty;
                if (name.Contains("ParseText", StringComparison.OrdinalIgnoreCase) ||
                    signature.Contains("ParseText", StringComparison.OrdinalIgnoreCase))
                {
                    sawParseText = true;
                }
            }
        }

        Assert.True(sawParseText, "Expected at least one member match for query=ParseText.");
    }

    [Fact]
    public async Task InspectPackage_TypeFilter_ReturnsOnlyMatchingTypes()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            includeMembers: false,
            maxTypes: 10);

        AssertNoError(root);

        var types = root.GetProperty("types").EnumerateArray().ToList();
        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            var name = type.GetProperty("name").GetString() ?? string.Empty;
            var signature = type.GetProperty("signature").GetString() ?? string.Empty;
            Assert.True(
                name.Contains("CSharpSyntaxTree", StringComparison.OrdinalIgnoreCase) ||
                signature.Contains("CSharpSyntaxTree", StringComparison.OrdinalIgnoreCase),
                $"Type should match typeFilter. Name='{name}', Signature='{signature}'");
        }
    }

    [Fact]
    public async Task InspectPackage_IncludeMembersFalse_ExcludesMembersCollection()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            includeMembers: false,
            maxTypes: 3);

        AssertNoError(root);

        foreach (var type in root.GetProperty("types").EnumerateArray())
        {
            Assert.False(type.TryGetProperty("members", out _));
        }
    }

    [Fact]
    public async Task InspectPackage_MaxMembersPerType_LimitsAndMarksTruncation()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            includeMembers: true,
            maxMembersPerType: 1,
            maxTypes: 5);

        AssertNoError(root);

        var types = root.GetProperty("types").EnumerateArray().ToList();
        Assert.NotEmpty(types);

        var sawTruncated = false;
        foreach (var type in types)
        {
            if (!type.TryGetProperty("members", out var members))
                continue;

            var memberCount = members.EnumerateArray().Count();
            Assert.InRange(memberCount, 0, 1);

            if (type.TryGetProperty("membersTruncated", out var membersTruncated) && membersTruncated.GetBoolean())
                sawTruncated = true;
        }

        Assert.True(sawTruncated, "Expected at least one type with membersTruncated=true when maxMembersPerType=1.");
    }

    [Fact]
    public async Task InspectPackage_AppliedFilters_ContainsNewFilterFields()
    {
        var root = await InvokeInspectPackageAsync(
            typeFilter: "CSharpSyntaxTree",
            memberFilter: "ParseText",
            memberKindFilter: "method",
            query: "ParseText",
            includeMembers: true,
            maxMembersPerType: 7,
            maxTypes: 2);

        AssertNoError(root);
        Assert.True(root.TryGetProperty("appliedFilters", out var appliedFilters));

        Assert.Equal("CSharpSyntaxTree", appliedFilters.GetProperty("typeFilter").GetString());
        Assert.Equal("ParseText", appliedFilters.GetProperty("memberFilter").GetString());
        Assert.Equal("method", appliedFilters.GetProperty("memberKindFilter").GetString());
        Assert.Equal("ParseText", appliedFilters.GetProperty("query").GetString());
        Assert.Equal(7, appliedFilters.GetProperty("maxMembersPerType").GetInt32());
        Assert.Equal(2, appliedFilters.GetProperty("maxTypes").GetInt32());
    }

    private static async Task<JsonElement> InvokeInspectPackageAsync(
        string? kindFilter = "class",
        string? query = null,
        string? typeFilter = null,
        string? memberFilter = null,
        string? memberKindFilter = null,
        bool includeMembers = true,
        int maxMembersPerType = 200,
        int maxTypes = 200)
    {
        using var workspace = CreateWorkspaceService();

        var json = await InspectPackageTool.InspectPackage(
            workspace,
            server: null!,
            packageName: PackageName,
            version: null,
            namespaceFilter: NamespaceFilter,
            kindFilter: kindFilter,
            query: query,
            typeFilter: typeFilter,
            memberFilter: memberFilter,
            memberKindFilter: memberKindFilter,
            includeMembers: includeMembers,
            maxMembersPerType: maxMembersPerType,
            maxTypes: maxTypes,
            solution: FindSolutionPath(),
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void AssertNoError(JsonElement root)
    {
        Assert.False(root.TryGetProperty("error", out _));
    }

    private static WorkspaceService CreateWorkspaceService()
    {
        var options = new WorkspaceOptions(FindSolutionPath());
        return new WorkspaceService(options, NullLogger<WorkspaceService>.Instance);
    }

    private static string FindSolutionPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DotSight.sln");
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate DotSight.sln from test output directory.");
    }
}
