using DotSight.Services;
using Microsoft.CodeAnalysis;

namespace DotSight.Tests;

/// <summary>
/// Tests for diagnostic reporting on in-memory compilations.
/// Validates the pattern used by GetDiagnosticsTool.
/// </summary>
public class DiagnosticsTests
{
    [Fact]
    public void GetDiagnostics_DetectsTypeConversionError()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        // BrokenClass has: int x = "not an int" → CS0029
        Assert.Contains(errors, e => e.Id == "CS0029");
    }

    [Fact]
    public void GetDiagnostics_DetectsUndefinedType()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        // BrokenClass has: UndefinedType y → CS0246
        Assert.Contains(errors, e => e.Id == "CS0246");
    }

    [Fact]
    public void GetDiagnostics_ErrorsHaveLocations()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        foreach (var error in errors)
        {
            Assert.True(error.Location.IsInSource, $"Diagnostic {error.Id} should be in source");
            var lineSpan = error.Location.GetLineSpan();
            Assert.True(lineSpan.StartLinePosition.Line >= 0);
        }
    }

    [Fact]
    public void GetDiagnostics_CleanCompilation_HasNoErrors()
    {
        var compilation = CompilationFactory.Create("CleanAssembly",
            """
            namespace Clean
            {
                public class Foo
                {
                    public int Bar => 42;
                }
            }
            """);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.Empty(errors);
    }

    [Fact]
    public void GetDiagnostics_SeverityFiltering_Works()
    {
        var compilation = CompilationFactory.CreateSampleCompilation();
        var allDiags = compilation.GetDiagnostics();

        var errorsOnly = allDiags.Where(d => d.Severity >= DiagnosticSeverity.Error).ToList();
        var warningsUp = allDiags.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        var allSeverities = allDiags.Where(d => d.Severity >= DiagnosticSeverity.Hidden).ToList();

        Assert.True(errorsOnly.Count <= warningsUp.Count);
        Assert.True(warningsUp.Count <= allSeverities.Count);
    }
}
