// Evidence runner: demonstrates the "no compatible version" code path
// by calling the resolver with a hand-crafted net472-only package (no netstandard fallback).
// This is the same code path that runs in production.

using NuGetCompatAssistant.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var resolver = new CompatibilityResolver();
var projectTfm = "net8.0";
var packageId = "Legacy.WindowsOnly.Package";

// Simulates a package that ONLY ships for .NET Framework 4.7.2 — no netstandard, no net5+
var onlyNet472Versions = new List<PackageVersionInfo>
{
    new PackageVersionInfo("1.0.0", new List<DependencyGroup>
    {
        new DependencyGroup("net45", new List<PackageDependency>()),
    }),
    new PackageVersionInfo("2.0.0", new List<DependencyGroup>
    {
        new DependencyGroup("net472", new List<PackageDependency>()),
    }),
    new PackageVersionInfo("3.0.0", new List<DependencyGroup>
    {
        new DependencyGroup("net472", new List<PackageDependency>()),
        new DependencyGroup("net462", new List<PackageDependency>()),
    }),
};

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("┌─────────────────────────────────────────────────────────┐");
Console.WriteLine("│        Smart NuGet Compatibility Assistant  v1.0         │");
Console.WriteLine("└─────────────────────────────────────────────────────────┘");
Console.ResetColor();
Console.WriteLine();
Console.WriteLine($"Project : EvidenceApp.csproj");
Console.WriteLine($"TFM(s)  : {projectTfm}");
Console.WriteLine();
Console.WriteLine($"Querying NuGet for '{packageId}' versions…");
Console.WriteLine($"Found {onlyNet472Versions.Count} listed versions.");

var result = resolver.Resolve(packageId, projectTfm, onlyNet472Versions);
ExplanationGenerator.PrintRecommendation(result);

Console.WriteLine($"Exit code: {(string.IsNullOrEmpty(result.RecommendedVersion) ? 1 : 0)}");
Console.WriteLine();
Console.WriteLine("--- Verification ---");
Console.WriteLine($"RecommendedVersion = \"{result.RecommendedVersion}\" (empty = no compatible version found)");
Console.WriteLine($"LatestStableVersion = \"{result.LatestStableVersion}\"");
Console.WriteLine($"IsLatestStableCompatible = {result.IsLatestStableCompatible}");
Console.WriteLine($"IncompatibleLatestTfms = [{string.Join(", ", result.IncompatibleLatestTfms)}]");
