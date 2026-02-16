using Microsoft.CodeAnalysis;

namespace DotSight.Tests;

public class CompilationFactoryTests
{
    [Fact]
    public void CreateSampleCompilation_ProducesValidCompilation()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        Assert.NotNull(compilation);
        Assert.Equal("SampleAssembly", compilation.AssemblyName);
    }

    [Fact]
    public void CreateSampleCompilation_ContainsExpectedTypes()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();

        var expectedTypes = new[]
        {
            "SampleApp.Models.IAnimal",
            "SampleApp.Models.Dog",
            "SampleApp.Models.Cat",
            "SampleApp.Services.AnimalService",
            "SampleApp.Models.Vehicle",
            "SampleApp.Models.Car",
            "SampleApp.Models.Bicycle",
            "SampleApp.Models.Color",
            "SampleApp.Models.Constants",
            "SampleApp.Models.Repository`1",
            "SampleApp.Extensions.AnimalExtensions",
            "SampleApp.Broken.BrokenClass",
        };

        foreach (var typeName in expectedTypes)
        {
            var type = compilation.GetTypeByMetadataName(typeName);
            Assert.NotNull(type);
        }
    }

    [Fact]
    public void CreateSampleCompilation_HasDiagnostics_InBrokenClass()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var diagnostics = compilation.GetDiagnostics();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void CreateSampleCompilation_InterfaceImplementations_AreCorrect()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var animal = compilation.GetTypeByMetadataName("SampleApp.Models.IAnimal")!;
        var dog = compilation.GetTypeByMetadataName("SampleApp.Models.Dog")!;
        var cat = compilation.GetTypeByMetadataName("SampleApp.Models.Cat")!;

        Assert.Contains(animal, dog.AllInterfaces);
        Assert.Contains(animal, cat.AllInterfaces);
    }

    [Fact]
    public void CreateSampleCompilation_InheritanceChain_IsCorrect()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var vehicle = compilation.GetTypeByMetadataName("SampleApp.Models.Vehicle")!;
        var car = compilation.GetTypeByMetadataName("SampleApp.Models.Car")!;
        var bicycle = compilation.GetTypeByMetadataName("SampleApp.Models.Bicycle")!;

        Assert.Equal(vehicle, car.BaseType, SymbolEqualityComparer.Default);
        Assert.Equal(vehicle, bicycle.BaseType, SymbolEqualityComparer.Default);
        Assert.True(vehicle.IsAbstract);
        Assert.False(car.IsAbstract);
    }
}
