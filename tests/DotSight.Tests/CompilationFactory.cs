using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotSight.Tests;

/// <summary>
/// Creates in-memory Roslyn compilations for testing without MSBuild or .sln files.
/// </summary>
internal static class CompilationFactory
{
    private static readonly MetadataReference[] SharedReferences =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    ];

    /// <summary>
    /// Creates a CSharpCompilation from one or more source code strings.
    /// </summary>
    public static CSharpCompilation Create(string assemblyName, params string[] sources)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        return CSharpCompilation.Create(
            assemblyName,
            trees,
            SharedReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Creates a compilation with a typical sample solution structure.
    /// </summary>
    public static CSharpCompilation CreateSampleCompilation()
    {
        return Create("SampleAssembly",
            // IAnimal interface
            """
            namespace SampleApp.Models
            {
                /// <summary>Represents an animal with basic properties.</summary>
                public interface IAnimal
                {
                    string Name { get; }
                    int LegCount { get; }
                    void Speak();
                }
            }
            """,
            // Dog class
            """
            namespace SampleApp.Models
            {
                /// <summary>A domestic dog.</summary>
                public class Dog : IAnimal
                {
                    public string Name { get; set; } = "Rex";
                    public int LegCount => 4;
                    public void Speak() { }
                    public void Fetch(string item) { }
                }
            }
            """,
            // Cat class
            """
            namespace SampleApp.Models
            {
                /// <summary>A domestic cat.</summary>
                public class Cat : IAnimal
                {
                    public string Name { get; set; } = "Whiskers";
                    public int LegCount => 4;
                    public void Speak() { }
                    private int _livesRemaining = 9;
                }
            }
            """,
            // AnimalService
            """
            using System.Collections.Generic;
            using SampleApp.Models;

            namespace SampleApp.Services
            {
                public class AnimalService
                {
                    private readonly List<IAnimal> _animals = new();

                    public void Register(IAnimal animal) => _animals.Add(animal);
                    public IReadOnlyList<IAnimal> GetAll() => _animals;
                    public IAnimal? FindByName(string name) =>
                        _animals.Find(a => a.Name == name);
                }
            }
            """,
            // Abstract base + derived
            """
            namespace SampleApp.Models
            {
                public abstract class Vehicle
                {
                    public abstract int WheelCount { get; }
                    public virtual string Describe() => $"Vehicle with {WheelCount} wheels";
                }

                public class Car : Vehicle
                {
                    public override int WheelCount => 4;
                }

                public class Bicycle : Vehicle
                {
                    public override int WheelCount => 2;
                    public override string Describe() => "A bicycle";
                }
            }
            """,
            // Enum + constant
            """
            namespace SampleApp.Models
            {
                public enum Color
                {
                    Red,
                    Green,
                    Blue
                }

                public static class Constants
                {
                    public const string AppName = "SampleApp";
                    public const int MaxItems = 100;
                    public static readonly string Version = "1.0.0";
                }
            }
            """,
            // Generic class
            """
            using System.Collections.Generic;

            namespace SampleApp.Models
            {
                public class Repository<T> where T : class
                {
                    private readonly List<T> _items = new();
                    public void Add(T item) => _items.Add(item);
                    public T? GetById(int id) => _items.Count > id ? _items[id] : null;
                    public IReadOnlyList<T> GetAll() => _items;
                }
            }
            """,
            // Extension methods
            """
            namespace SampleApp.Extensions
            {
                public static class AnimalExtensions
                {
                    public static string Greet(this SampleApp.Models.IAnimal animal) =>
                        $"Hello, I am {animal.Name}!";

                    public static bool IsQuadruped(this SampleApp.Models.IAnimal animal) =>
                        animal.LegCount == 4;
                }
            }
            """,
            // Code with errors for diagnostics testing
            """
            namespace SampleApp.Broken
            {
                public class BrokenClass
                {
                    public void Method()
                    {
                        int x = "not an int";
                        UndefinedType y;
                    }
                }
            }
            """);
    }
}
