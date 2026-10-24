using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetArchTest.Rules;

namespace Flowd.Architecture.Tests;

/// <summary>ARC-001: enforces the dependency rule and code-shape rules of spec section 2.4.</summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Bpmn = typeof(Flowd.Bpmn.AssemblyMarker).Assembly;
    private static readonly Assembly Expressions = typeof(Flowd.Expressions.AssemblyMarker).Assembly;
    private static readonly Assembly Engine = typeof(Flowd.Engine.AssemblyMarker).Assembly;

    private static readonly Assembly[] AllSrc =
    [
        Bpmn,
        Expressions,
        Engine,
        typeof(Flowd.Application.AssemblyMarker).Assembly,
        typeof(Flowd.Contracts.AssemblyMarker).Assembly,
        typeof(Flowd.Infrastructure.AssemblyMarker).Assembly,
        typeof(Flowd.Api.AssemblyMarker).Assembly,
        typeof(Flowd.Worker.Client.AssemblyMarker).Assembly,
        typeof(Flowd.Host.FlowdHostBuilder).Assembly,
    ];

    /// <summary>Allowed Flowd.* references per project (the arrows of section 2.4, transitive closure included).</summary>
    public static TheoryData<string, string[]> AllowedReferences => new()
    {
        { "Flowd.Bpmn", [] },
        { "Flowd.Expressions", [] },
        { "Flowd.Engine", ["Flowd.Bpmn", "Flowd.Expressions"] },
        { "Flowd.Application", ["Flowd.Engine", "Flowd.Bpmn", "Flowd.Expressions"] },
        { "Flowd.Contracts", [] },
        { "Flowd.Infrastructure", ["Flowd.Application", "Flowd.Engine", "Flowd.Bpmn", "Flowd.Expressions"] },
        { "Flowd.Api", ["Flowd.Application", "Flowd.Contracts", "Flowd.Engine", "Flowd.Bpmn", "Flowd.Expressions"] },
        { "Flowd.Worker.Client", ["Flowd.Contracts"] },
        {
            "flowd",
            ["Flowd.Api", "Flowd.Infrastructure", "Flowd.Application", "Flowd.Contracts", "Flowd.Engine", "Flowd.Bpmn", "Flowd.Expressions"]
        },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void ARC_001_projects_reference_only_inward(string project, string[] allowed)
    {
        var assembly = AllSrc.Single(a => a.GetName().Name == project);
        var flowdReferences = assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => n.StartsWith("Flowd.", StringComparison.Ordinal));

        flowdReferences.ShouldBeSubsetOf(allowed);
    }

    [Fact]
    public void ARC_001_pure_core_has_no_io_framework_dependencies()
    {
        string[] forbidden = ["System.Net", "Npgsql", "Microsoft.AspNetCore", "System.IO.File", "System.Random"];

        foreach (var assembly in new[] { Bpmn, Expressions, Engine })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            result.IsSuccessful.ShouldBeTrue($"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void ARC_001_non_abstract_classes_are_sealed()
    {
        foreach (var assembly in AllSrc)
        {
            var result = Types.InAssembly(assembly).That().AreClasses().And().AreNotAbstract().Should().BeSealed().GetResult();
            result.IsSuccessful.ShouldBeTrue($"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void ARC_001_public_types_live_in_their_project_namespace()
    {
        foreach (var assembly in AllSrc)
        {
            var root = RootNamespace(assembly);
            var outsiders = assembly.GetExportedTypes()
                .Where(t => t.Namespace != root && t.Namespace?.StartsWith(root + ".", StringComparison.Ordinal) != true)
                .Select(t => t.FullName);

            outsiders.ShouldBeEmpty(root);
        }
    }

    [Fact]
    public void ARC_001_switch_expressions_have_no_discard_arm_unless_marked_non_union()
    {
        var offenders = new List<string>();
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(objSegment, StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            var tree = CSharpSyntaxTree.ParseText(string.Join('\n', lines));
            foreach (var arm in tree.GetRoot().DescendantNodes().OfType<SwitchExpressionArmSyntax>()
                         .Where(a => a.Pattern is DiscardPatternSyntax))
            {
                var line = tree.GetLineSpan(arm.Span).StartLinePosition.Line;
                if (!lines[line].Contains("// non-union", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{line + 1}");
                }
            }
        }

        offenders.ShouldBeEmpty("discard arms hide new union cases (UNI-003); mark non-union switches with '// non-union'");
    }

    private static string RootNamespace(Assembly assembly) =>
        assembly.GetName().Name == "flowd" ? "Flowd.Host" : assembly.GetName().Name!;

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Flowd.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Flowd.slnx not found"), "src");
    }
}
