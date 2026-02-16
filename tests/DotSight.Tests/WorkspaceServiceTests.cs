using DotSight.Services;
using Microsoft.CodeAnalysis;

namespace DotSight.Tests;

public class WorkspaceServiceTests
{
    private readonly Compilation _compilation = CompilationFactory.CreateSampleCompilation();

    // --- ResolveType ---

    [Theory]
    [InlineData("SampleApp.Models.Dog")]
    [InlineData("SampleApp.Models.IAnimal")]
    [InlineData("SampleApp.Models.Color")]
    [InlineData("SampleApp.Services.AnimalService")]
    [InlineData("SampleApp.Models.Vehicle")]
    public void ResolveType_FindsExistingTypes(string fullyQualifiedName)
    {
        var result = WorkspaceService.ResolveType(_compilation, fullyQualifiedName);
        Assert.NotNull(result);
        Assert.Equal(fullyQualifiedName.Split('.').Last(), result.Name);
    }

    [Fact]
    public void ResolveType_ReturnsNull_ForNonExistent()
    {
        var result = WorkspaceService.ResolveType(_compilation, "SampleApp.Models.NonExistent");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveType_FindsGenericType_ByMetadataName()
    {
        var result = WorkspaceService.ResolveType(_compilation, "SampleApp.Models.Repository`1");
        Assert.NotNull(result);
        Assert.Equal("Repository", result.Name);
        Assert.Single(result.TypeParameters);
    }

    // --- ResolveMember ---

    [Fact]
    public void ResolveMember_FindsMethod()
    {
        var result = WorkspaceService.ResolveMember(_compilation, "SampleApp.Models.Dog", "Fetch");
        Assert.NotNull(result);
        Assert.Equal("Fetch", result.Name);
        Assert.IsAssignableFrom<IMethodSymbol>(result);
    }

    [Fact]
    public void ResolveMember_FindsProperty()
    {
        var result = WorkspaceService.ResolveMember(_compilation, "SampleApp.Models.Dog", "Name");
        Assert.NotNull(result);
        Assert.Equal("Name", result.Name);
        Assert.IsAssignableFrom<IPropertySymbol>(result);
    }

    [Fact]
    public void ResolveMember_FindsConstant()
    {
        var result = WorkspaceService.ResolveMember(_compilation, "SampleApp.Models.Constants", "MaxItems");
        Assert.NotNull(result);
        var field = Assert.IsAssignableFrom<IFieldSymbol>(result);
        Assert.True(field.IsConst);
        Assert.Equal(100, field.ConstantValue);
    }

    [Fact]
    public void ResolveMember_ReturnsNull_ForInvalidType()
    {
        var result = WorkspaceService.ResolveMember(_compilation, "SampleApp.NonExistent", "Foo");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveMember_ReturnsNull_ForInvalidMember()
    {
        var result = WorkspaceService.ResolveMember(_compilation, "SampleApp.Models.Dog", "NonExistent");
        Assert.Null(result);
    }
}
