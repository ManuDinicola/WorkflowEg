using Flowd.Bpmn.Model;
using Flowd.Engine.Results;

namespace Flowd.Engine.Tests;

public sealed record Circle(double Radius);

public sealed record Square(double Side);

public union Shape(Circle, Square);

/// <summary>T-BOOT-01 (UNI-001): C# 15 unions and closed hierarchies compile and behave as the spec assumes.</summary>
public sealed class UnionSmokeTests
{
    private static string Describe(Shape shape) => shape switch
    {
        Circle c => $"circle:{c.Radius}",
        Square s => $"square:{s.Side}",
    };

    private static string Describe(FlowNode node) => node switch
    {
        StartEvent => "start",
        EndEvent => "end",
    };

    [Fact]
    public void T_BOOT_01_union_cases_construct_and_switch_exhaustively()
    {
        Describe(new Circle(2)).ShouldBe("circle:2");
        Describe(new Square(3)).ShouldBe("square:3");
    }

    [Fact]
    public void T_BOOT_01_generic_result_union_carries_value_or_failure()
    {
        Result<int> ok = 42;
        Result<int> failed = new Failure(new NotFound("missing"));

        (ok switch { int v => v, Failure => -1 }).ShouldBe(42);
        (failed switch { int v => v, Failure => -1 }).ShouldBe(-1);
    }

    [Fact]
    public void T_BOOT_01_closed_hierarchy_switch_is_exhaustive_without_discard()
    {
        Describe(new StartEvent { Id = "s" }).ShouldBe("start");
        Describe(new EndEvent { Id = "e" }).ShouldBe("end");
    }

    [Fact]
    public void T_BOOT_01_non_exhaustive_union_switch_is_a_compile_error_cs8509()
    {
        var diagnostics = SnippetCompiler.Compile("""
            public record A(); public record B();
            public union U(A, B);
            public static class C { public static int F(U u) => u switch { A => 1 }; }
            """);

        diagnostics.ShouldContain("CS8509");
    }

    [Fact]
    public void T_BOOT_01_exhaustive_union_snippet_compiles_cleanly()
    {
        var diagnostics = SnippetCompiler.Compile("""
            public record A(); public record B();
            public union U(A, B);
            public static class C { public static int F(U u) => u switch { A => 1, B => 2 }; }
            """);

        diagnostics.ShouldBeEmpty();
    }
}
