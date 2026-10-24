using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Flowd.Engine.Tests;

/// <summary>
/// Compiles a C# snippet with the real SDK compiler (a throwaway project built with <c>dotnet build</c>).
/// The Roslyn NuGet packages lag the SDK compiler and cannot parse C# 15 <c>union</c> yet, so they cannot be used here.
/// </summary>
internal static partial class SnippetCompiler
{
    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <OutputType>Library</OutputType>
            <Nullable>enable</Nullable>
            <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
          </PropertyGroup>
        </Project>
        """;

    private const string GlobalJson = """{ "sdk": { "version": "11.0.100-rc.2.26504.105", "rollForward": "latestFeature", "allowPrerelease": true } }""";

    /// <summary>Returns the distinct compiler diagnostic ids (errors and warnings) reported for the snippet.</summary>
    public static IReadOnlyCollection<string> Compile(string source)
    {
        var dir = Directory.CreateTempSubdirectory("flowd-snippet-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "snippet.csproj"), ProjectXml);
            File.WriteAllText(Path.Combine(dir.FullName, "global.json"), GlobalJson);
            File.WriteAllText(Path.Combine(dir.FullName, "Snippet.cs"), source);

            var info = new ProcessStartInfo("dotnet", "build snippet.csproj -nologo -v:q -clp:NoSummary")
            {
                WorkingDirectory = dir.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet not started");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return [.. DiagnosticId().Matches(output).Select(m => m.Groups["id"].Value).Distinct()];
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [GeneratedRegex(@"(?:error|warning) (?<id>(?:CS|CA)\d+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DiagnosticId();
}
