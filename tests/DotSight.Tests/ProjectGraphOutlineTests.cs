using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DotSight.Services;

namespace DotSight.Tests;

/// <summary>
/// Tests for the file outline extraction logic used by GetProjectGraphTool(includeOutlines=true).
/// Validates that type outlines are correctly extracted from syntax/semantic analysis.
/// </summary>
public class ProjectGraphOutlineTests
{
    private readonly CSharpCompilation _compilation = CompilationFactory.CreateSampleCompilation();

    [Fact]
    public void ExtractOutline_FindsClassesAndInterfaces()
    {
        var types = GetAllDeclaredTypes();
        var names = types.Select(t => t.Name).ToList();

        Assert.Contains("IAnimal", names);
        Assert.Contains("Dog", names);
        Assert.Contains("Cat", names);
        Assert.Contains("Vehicle", names);
        Assert.Contains("AnimalService", names);
    }

    [Fact]
    public void ExtractOutline_GetKind_CorrectForAllTypes()
    {
        var types = GetAllDeclaredTypes();

        var animal = types.First(t => t.Name == "IAnimal");
        Assert.Equal("interface", SymbolFormatter.GetKind(animal));

        var dog = types.First(t => t.Name == "Dog");
        Assert.Equal("class", SymbolFormatter.GetKind(dog));

        var color = types.First(t => t.Name == "Color");
        Assert.Equal("enum", SymbolFormatter.GetKind(color));
    }

    [Fact]
    public void ExtractOutline_Members_ExcludeImplicit()
    {
        var dog = GetAllDeclaredTypes().First(t => t.Name == "Dog");
        var members = dog.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m is not IMethodSymbol ms || ms.MethodKind is not
                (MethodKind.PropertyGet or MethodKind.PropertySet))
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("Name", members);
        Assert.Contains("Fetch", members);
        Assert.Contains("Speak", members);
        Assert.Contains("LegCount", members);
    }

    [Fact]
    public void ExtractOutline_BaseType_Detected()
    {
        var car = GetAllDeclaredTypes().First(t => t.Name == "Car");
        Assert.NotNull(car.BaseType);
        Assert.Equal("Vehicle", car.BaseType!.Name);
    }

    [Fact]
    public void ExtractOutline_Interfaces_Detected()
    {
        var dog = GetAllDeclaredTypes().First(t => t.Name == "Dog");
        var ifaces = dog.Interfaces.Select(i => i.Name).ToList();
        Assert.Contains("IAnimal", ifaces);
    }

    [Fact]
    public void ExtractOutline_XmlDocSummary_Extracted()
    {
        var animal = GetAllDeclaredTypes().First(t => t.Name == "IAnimal");
        var summary = SymbolFormatter.GetXmlDocSummary(animal);
        Assert.Contains("animal", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractOutline_NestedTypes_NotDuplicated()
    {
        // Car and Bicycle are nested inside the same file as Vehicle but are
        // separate top-level types — they should appear independently
        var types = GetAllDeclaredTypes();
        var vehicleTypes = types.Where(t => t.Name is "Vehicle" or "Car" or "Bicycle").ToList();
        Assert.Equal(3, vehicleTypes.Count);
    }

    [Fact]
    public void ExtractOutline_GenericType_HasTypeParameter()
    {
        var repo = GetAllDeclaredTypes().First(t => t.Name == "Repository");
        Assert.Single(repo.TypeParameters);
        Assert.Equal("T", repo.TypeParameters[0].Name);
    }

    [Fact]
    public void ExtractOutline_StaticClass_MembersIncluded()
    {
        var constants = GetAllDeclaredTypes().First(t => t.Name == "Constants");
        var members = constants.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("AppName", members);
        Assert.Contains("MaxItems", members);
        Assert.Contains("Version", members);
    }

    private List<INamedTypeSymbol> GetAllDeclaredTypes()
    {
        var types = new List<INamedTypeSymbol>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var typeNode in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeNode) is INamedTypeSymbol typeSymbol
                    && !typeSymbol.IsImplicitlyDeclared
                    && typeSymbol.ContainingType is null) // skip nested
                {
                    types.Add(typeSymbol);
                }
            }
        }
        return types;
    }
}
