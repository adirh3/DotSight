using Microsoft.CodeAnalysis;

namespace DotSight.Tests;

/// <summary>
/// Tests for symbol resolution patterns used across tool implementations.
/// Validates type hierarchy, interface implementations, and member lookups.
/// </summary>
public class SymbolResolutionTests
{
    private readonly Compilation _compilation = CompilationFactory.CreateSampleCompilation();

    // --- Interface Implementation Discovery ---

    [Fact]
    public void FindInterfaceImplementors_ByAllInterfaces()
    {
        var animal = _compilation.GetTypeByMetadataName("SampleApp.Models.IAnimal")!;

        var implementors = new List<INamedTypeSymbol>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var symbol = model.GetDeclaredSymbol(node);
                if (symbol is INamedTypeSymbol type &&
                    type.AllInterfaces.Contains(animal, SymbolEqualityComparer.Default))
                {
                    implementors.Add(type);
                }
            }
        }

        var names = implementors.Select(t => t.Name).Distinct().ToList();
        Assert.Contains("Dog", names);
        Assert.Contains("Cat", names);
        Assert.Equal(2, names.Count);
    }

    // --- Abstract Class Derived Types ---

    [Fact]
    public void FindDerivedClasses_OfAbstractType()
    {
        var vehicle = _compilation.GetTypeByMetadataName("SampleApp.Models.Vehicle")!;

        var derived = new List<INamedTypeSymbol>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var symbol = model.GetDeclaredSymbol(node);
                if (symbol is INamedTypeSymbol type &&
                    !SymbolEqualityComparer.Default.Equals(type, vehicle) &&
                    InheritsFrom(type, vehicle))
                {
                    derived.Add(type);
                }
            }
        }

        var names = derived.Select(t => t.Name).Distinct().ToList();
        Assert.Contains("Car", names);
        Assert.Contains("Bicycle", names);
    }

    // --- Extension Method Discovery ---

    [Fact]
    public void FindExtensionMethods_ForInterface()
    {
        var animal = _compilation.GetTypeByMetadataName("SampleApp.Models.IAnimal")!;

        var extensions = new List<IMethodSymbol>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var symbol = model.GetDeclaredSymbol(node);
                if (symbol is IMethodSymbol method &&
                    method.IsExtensionMethod &&
                    method.Parameters.Length > 0 &&
                    SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, animal))
                {
                    extensions.Add(method);
                }
            }
        }

        var names = extensions.Select(m => m.Name).ToList();
        Assert.Contains("Greet", names);
        Assert.Contains("IsQuadruped", names);
    }

    // --- Member Resolution ---

    [Fact]
    public void GetMembers_EnumValues()
    {
        var color = _compilation.GetTypeByMetadataName("SampleApp.Models.Color")!;
        var members = color.GetMembers()
            .Where(m => m is IFieldSymbol f && f.IsConst && f.Type.Equals(color, SymbolEqualityComparer.Default))
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(["Red", "Green", "Blue"], members);
    }

    [Fact]
    public void GetMembers_GenericConstraints()
    {
        var repo = _compilation.GetTypeByMetadataName("SampleApp.Models.Repository`1")!;
        var typeParam = repo.TypeParameters.Single();

        Assert.Equal("T", typeParam.Name);
        Assert.True(typeParam.HasReferenceTypeConstraint);
    }

    [Fact]
    public void GetMembers_OverriddenMethod()
    {
        var bicycle = _compilation.GetTypeByMetadataName("SampleApp.Models.Bicycle")!;
        var describe = bicycle.GetMembers("Describe").OfType<IMethodSymbol>().Single();

        Assert.NotNull(describe.OverriddenMethod);
        Assert.Equal("Vehicle", describe.OverriddenMethod.ContainingType.Name);
    }

    [Fact]
    public void GetMembers_InterfaceMembers()
    {
        var animal = _compilation.GetTypeByMetadataName("SampleApp.Models.IAnimal")!;
        var memberNames = animal.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("Name", memberNames);
        Assert.Contains("LegCount", memberNames);
        Assert.Contains("Speak", memberNames);
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
            current = current.BaseType;
        }
        return false;
    }
}
