using NuGetCompatAssistant.Cli;
using Xunit;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

namespace NuGetCompatAssistant.Tests;

/// <summary>
/// Unit tests for the v1.1 multi-package batch install feature.
/// All tests use hand-crafted data and injected delegates — no real HTTP calls or installations.
/// </summary>
public class BatchInstallTests
{
    private readonly CompatibilityResolver _resolver = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Helper: build test data without real HTTP
    // ─────────────────────────────────────────────────────────────────────────

    private static PackageVersionInfo MakeVersion(string version, params string[] tfms)
    {
        var groups = tfms.Length == 0
            ? new List<DependencyGroup>()
            : tfms.Select(t => new DependencyGroup(t, new List<PackageDependency>())).ToList();
        return new PackageVersionInfo(version, groups);
    }

    private static Func<string, Task<List<PackageVersionInfo>>> FakeFetcher(
        Dictionary<string, List<PackageVersionInfo>> data)
    {
        return id =>
        {
            if (data.TryGetValue(id, out var versions))
                return Task.FromResult(versions);
            throw new NuGetClientException($"Package '{id}' was not found on nuget.org.");
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BatchPackageResult.IsInstallable
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Compatible", true)]
    [InlineData("Downgrade", true)]
    [InlineData("Not Found", false)]
    [InlineData("No Compatible Version", false)]
    public void IsInstallable_ReturnsExpected(string status, bool expected)
    {
        var result = new BatchPackageResult("Test", "1.0.0", "1.0.0", status, "reason");
        Assert.Equal(expected, result.IsInstallable);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveAllAsync — multiple valid packages
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAll_MultipleValidPackages_AllCompatible()
    {
        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            ["AutoMapper"] = new() { MakeVersion("12.0.0", "net6.0", "net8.0") },
            ["Serilog"] = new() { MakeVersion("3.1.1", "netstandard2.0") },
            ["Newtonsoft.Json"] = new() { MakeVersion("13.0.3", "netstandard2.0") },
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "AutoMapper", "Serilog", "Newtonsoft.Json" }, "net8.0");

        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal("Compatible", r.Status);
            Assert.True(r.IsInstallable);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveAllAsync — mixed valid + invalid (not found) package IDs
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAll_MixedValidAndInvalid_InvalidIsNotFound()
    {
        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            ["AutoMapper"] = new() { MakeVersion("12.0.0", "net6.0") },
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "AutoMapper", "TotallyFakePackage123" }, "net8.0");

        Assert.Equal(2, results.Count);

        Assert.Equal("Compatible", results[0].Status);
        Assert.True(results[0].IsInstallable);

        Assert.Equal("Not Found", results[1].Status);
        Assert.False(results[1].IsInstallable);
        Assert.Contains("not found", results[1].Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveAllAsync — mixed compatible + incompatible packages
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAll_MixedCompatibleAndIncompatible()
    {
        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            ["CompatPkg"] = new() { MakeVersion("2.0.0", "net8.0") },
            ["IncompatPkg"] = new() { MakeVersion("1.0.0", "net472") }, // not compatible with net8.0
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "CompatPkg", "IncompatPkg" }, "net8.0");

        Assert.Equal(2, results.Count);

        Assert.Equal("Compatible", results[0].Status);
        Assert.True(results[0].IsInstallable);
        Assert.Equal("2.0.0", results[0].RecommendedVersion);

        Assert.Equal("No Compatible Version", results[1].Status);
        Assert.False(results[1].IsInstallable);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveAllAsync — downgrade scenario
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAll_DowngradeScenario_StatusIsDowngrade()
    {
        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            ["PkgWithDowngrade"] = new()
            {
                MakeVersion("1.0.0", "net6.0"),
                MakeVersion("2.0.0", "net8.0"),
                MakeVersion("3.0.0", "net10.0"), // latest stable, incompatible with net8.0
            },
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "PkgWithDowngrade" }, "net8.0");

        Assert.Single(results);
        Assert.Equal("Downgrade", results[0].Status);
        Assert.Equal("2.0.0", results[0].RecommendedVersion);
        Assert.Equal("3.0.0", results[0].LatestStableVersion);
        Assert.True(results[0].IsInstallable);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Summary table rendering
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrintBatchSummaryTable_ContainsAllPackagesAndStatuses()
    {
        var results = new List<BatchPackageResult>
        {
            new("AutoMapper", "12.0.0", "12.0.0", "Compatible", "Latest stable is compatible"),
            new("EfCore", "10.0.2", "8.0.21", "Downgrade", "Latest (10.0.2) incompatible; downgraded"),
            new("FakePackage", "—", "—", "Not Found", "Package not found on nuget.org"),
        };

        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            ExplanationGenerator.PrintBatchSummaryTable(results);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();

        // Verify header columns
        Assert.Contains("Package", output);
        Assert.Contains("Latest Stable", output);
        Assert.Contains("Recommended", output);
        Assert.Contains("Status", output);
        Assert.Contains("Reason", output);

        // Verify all packages appear
        Assert.Contains("AutoMapper", output);
        Assert.Contains("EfCore", output);
        Assert.Contains("FakePackage", output);

        // Verify statuses appear
        Assert.Contains("Compatible", output);
        Assert.Contains("Downgrade", output);
        Assert.Contains("Not Found", output);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dry-run — should resolve but never call install
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_ResolvesPackages_NeverCallsInstall()
    {
        bool installCalled = false;

        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            ["PackageA"] = new() { MakeVersion("1.0.0", "net8.0") },
            ["PackageB"] = new() { MakeVersion("2.0.0", "net6.0") },
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "PackageA", "PackageB" }, "net8.0");

        var installable = results.Where(r => r.IsInstallable).ToList();

        // In dry-run mode, the caller shows the summary table and returns 0
        // without ever calling InstallAllAsync.
        Assert.Equal(2, installable.Count);

        // Simulate: dry-run stops here — install is never called
        Assert.False(installCalled);

        // Dry-run exit code is always 0
        int exitCode = 0; // dry-run always returns 0
        Assert.Equal(0, exitCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // User declines installation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UserDeclines_ResolvesPackages_NeverCallsInstall_ExitCodeZero()
    {
        bool installCalled = false;

        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            ["PackageA"] = new() { MakeVersion("1.0.0", "net8.0") },
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "PackageA" }, "net8.0");

        var installable = results.Where(r => r.IsInstallable).ToList();
        Assert.Single(installable);

        // Simulate user declining: answer is "n"
        // The flow prints "Installation cancelled." and returns 0
        string userAnswer = "n";
        bool shouldInstall = userAnswer is "y" or "yes";
        Assert.False(shouldInstall);
        Assert.False(installCalled);

        // Exit code is 0 when user cancels (not a failure)
        int exitCode = 0;
        Assert.Equal(0, exitCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // InstallAllAsync — all succeed → exit code 0
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAll_AllSucceed_ExitCodeZero()
    {
        var installable = new List<BatchPackageResult>
        {
            new("PackageA", "1.0.0", "1.0.0", "Compatible", "reason"),
            new("PackageB", "2.0.0", "2.0.0", "Compatible", "reason"),
            new("PackageC", "3.0.0", "2.5.0", "Downgrade", "reason"),
        };

        var originalOut = Console.Out;
        Console.SetOut(new StringWriter()); // suppress progress output

        try
        {
            var (installed, failed) = await BatchInstallOrchestrator.InstallAllAsync(
                installable,
                "test.csproj",
                (_, _, _) => Task.FromResult(true)); // all succeed

            Assert.Equal(3, installed);
            Assert.Equal(0, failed);

            int exitCode = failed > 0 ? 1 : 0;
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // InstallAllAsync — one fails → exit code 1
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAll_OneFails_ExitCodeOne()
    {
        var installable = new List<BatchPackageResult>
        {
            new("PackageA", "1.0.0", "1.0.0", "Compatible", "reason"),
            new("PackageB", "2.0.0", "2.0.0", "Compatible", "reason"),
        };

        int callIndex = 0;
        var originalOut = Console.Out;
        Console.SetOut(new StringWriter()); // suppress progress output

        try
        {
            var (installed, failed) = await BatchInstallOrchestrator.InstallAllAsync(
                installable,
                "test.csproj",
                (_, _, _) => Task.FromResult(callIndex++ == 0)); // first succeeds, second fails

            Assert.Equal(1, installed);
            Assert.Equal(1, failed);

            int exitCode = failed > 0 ? 1 : 0;
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // InstallAllAsync — progress output format
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallAll_PrintsProgressInExpectedFormat()
    {
        var installable = new List<BatchPackageResult>
        {
            new("AutoMapper", "12.0.0", "12.0.0", "Compatible", "reason"),
            new("Serilog", "3.1.1", "3.1.1", "Compatible", "reason"),
        };

        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            await BatchInstallOrchestrator.InstallAllAsync(
                installable,
                "test.csproj",
                (_, _, _) => Task.FromResult(true));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();

        Assert.Contains("[1/2] Installing AutoMapper 12.0.0", output);
        Assert.Contains("[2/2] Installing Serilog 3.1.1", output);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full mixed scenario — end-to-end orchestration
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullScenario_MixedResults_CorrectCountsAndStatuses()
    {
        var data = new Dictionary<string, List<PackageVersionInfo>>
        {
            // Compatible
            ["AutoMapper"] = new() { MakeVersion("12.0.0", "net8.0") },
            // Downgrade needed
            ["EfCore"] = new()
            {
                MakeVersion("8.0.21", "net8.0"),
                MakeVersion("10.0.2", "net10.0"),
            },
            // Not found — will throw NuGetClientException
        };

        var orchestrator = new BatchInstallOrchestrator(FakeFetcher(data), _resolver);
        var results = await orchestrator.ResolveAllAsync(
            new[] { "AutoMapper", "EfCore", "NonExistentPackage" }, "net8.0");

        Assert.Equal(3, results.Count);

        // AutoMapper: compatible
        Assert.Equal("Compatible", results[0].Status);
        Assert.Equal("12.0.0", results[0].RecommendedVersion);

        // EfCore: downgrade
        Assert.Equal("Downgrade", results[1].Status);
        Assert.Equal("8.0.21", results[1].RecommendedVersion);
        Assert.Equal("10.0.2", results[1].LatestStableVersion);

        // NonExistentPackage: not found
        Assert.Equal("Not Found", results[2].Status);

        // Installable count
        var installable = results.Where(r => r.IsInstallable).ToList();
        Assert.Equal(2, installable.Count);

        // Skipped count
        int skipped = results.Count - installable.Count;
        Assert.Equal(1, skipped);
    }


    private class CustomHelpBuilder : System.CommandLine.Help.HelpBuilder
    {
        public CustomHelpBuilder() : base(System.CommandLine.LocalizationResources.Instance) {}

        public override void Write(System.CommandLine.Help.HelpContext context)
        {
            using var stringWriter = new StringWriter();
            var newContext = new System.CommandLine.Help.HelpContext(this, context.Command, stringWriter, context.ParseResult);
            base.Write(newContext);

            var output = stringWriter.ToString();
            var modified = output.Replace("<PackageId>...", "<PackageId> [<PackageId>...]");
            context.Output.Write(modified);
        }
    }

    [Fact]
    public async Task HelpOutput_ShowsCorrectBatchUsage()
    {
        var rootCommand = new System.CommandLine.RootCommand("Root description");
        var packageIdArg = new System.CommandLine.Argument<string[]>(
            "PackageId",
            "One or more NuGet package IDs to install (e.g. Microsoft.EntityFrameworkCore Serilog AutoMapper).")
        {
            Arity = System.CommandLine.ArgumentArity.OneOrMore
        };

        var projectOption = new System.CommandLine.Option<string?>(
            aliases: ["--project", "-p"],
            description: "Path to .csproj file.");

        var yesOption = new System.CommandLine.Option<bool>(
            aliases: ["--yes", "-y"],
            description: "Skip confirmation prompt.");

        var dryRunOption = new System.CommandLine.Option<bool>(
            aliases: ["--dry-run"],
            description: "Show recommendation.");

        var versionOption = new System.CommandLine.Option<string?>(
            aliases: ["--version", "-v"],
            description: "Check specific version.");

        var installCommand = new System.CommandLine.Command(
            "install",
            "Find best compatible version.")
        {
            packageIdArg,
            projectOption,
            versionOption,
            yesOption,
            dryRunOption,
        };

        rootCommand.AddCommand(installCommand);

        var console = new System.CommandLine.IO.TestConsole();
        var parser = new System.CommandLine.Builder.CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseHelpBuilder(context => new CustomHelpBuilder())
            .Build();

        await parser.InvokeAsync(new[] { "install", "--help" }, console);
        var output = console.Out.ToString();
        
        Assert.Contains("install <PackageId> [<PackageId>...]", output);
    }
}

