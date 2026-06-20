using NuGetCompatAssistant.Cli;
using Xunit;

namespace NuGetCompatAssistant.Tests;

/// <summary>
/// Unit tests for <see cref="CompatibilityResolver"/>.
/// All tests use hand-crafted <see cref="PackageVersionInfo"/> objects — no real HTTP calls.
/// </summary>
public class CompatibilityResolverTests
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

    // ─────────────────────────────────────────────────────────────────────────
    // Resolve — picks highest STABLE compatible version
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_PicksHighestCompatibleVersion()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("1.0.0", "net6.0"),
            MakeVersion("2.0.0", "net6.0"),
            MakeVersion("3.0.0", "net6.0"),
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        Assert.Equal("3.0.0", result.RecommendedVersion);
        Assert.True(result.IsLatestStableCompatible);
    }

    [Fact]
    public void Resolve_ExcludesPrereleaseVersionsByDefault()
    {
        // 4.0.0-beta1 is pre-release — should not be recommended
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("3.0.0", "net8.0"),
            MakeVersion("4.0.0-beta1", "net8.0"),
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        // Latest stable is 3.0.0, not the pre-release 4.0.0-beta1
        Assert.Equal("3.0.0", result.RecommendedVersion);
        Assert.Equal("3.0.0", result.LatestStableVersion);
        // But the overall latest (including pre-release) is 4.0.0-beta1
        Assert.Equal("4.0.0-beta1", result.LatestOverallVersion);
    }

    [Fact]
    public void Resolve_LatestStableIsIncompatible_PicksNewestCompatibleStable()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("1.0.0", "net6.0"),
            MakeVersion("2.0.0", "net8.0"),
            MakeVersion("3.0.0", "net10.0"), // stable but incompatible with net8.0
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        Assert.Equal("2.0.0", result.RecommendedVersion);
        Assert.Equal("3.0.0", result.LatestStableVersion);
        Assert.False(result.IsLatestStableCompatible);
        Assert.Contains("net10.0", result.IncompatibleLatestTfms);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // netstandard2.0 fallback — must be compatible with net8.0
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NetStandard20_IsCompatibleWithNet80()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("5.0.0", "netstandard2.0"),
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        Assert.Equal("5.0.0", result.RecommendedVersion);
        Assert.True(result.IsLatestStableCompatible);
    }

    [Fact]
    public void Resolve_NetStandard21_IsCompatibleWithNet80()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("5.0.0", "netstandard2.1"),
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        Assert.Equal("5.0.0", result.RecommendedVersion);
        Assert.True(result.IsLatestStableCompatible);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Full-framework (net4x) is NOT compatible with net8.0
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_Net472Only_IsNotCompatibleWithNet80()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("2.0.0", "net472"),
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        // NuGet does not treat net472 as compatible with net8.0
        Assert.True(string.IsNullOrEmpty(result.RecommendedVersion),
            "net472 should NOT be compatible with net8.0");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // No dependency groups = universal compatibility
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoDependencyGroups_IsCompatibleWithAll()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("1.0.0" /* no TFMs */),
        };

        var result = _resolver.Resolve("ContentPackage", "net8.0", versions);

        Assert.Equal("1.0.0", result.RecommendedVersion);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Multi-version with mixed compatibility
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_MultipleVersionsMixed_PicksHighestCompatible()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("1.0.0", "netstandard2.0"),
            MakeVersion("2.0.0", "net6.0"),
            MakeVersion("3.0.0", "net8.0"),
            MakeVersion("4.0.0", "net9.0"),  // incompatible with net8.0
            MakeVersion("5.0.0", "net10.0"), // incompatible with net8.0
        };

        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        Assert.Equal("3.0.0", result.RecommendedVersion);
        Assert.Equal("5.0.0", result.LatestStableVersion);
        Assert.False(result.IsLatestStableCompatible);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Edge: no versions at all
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoVersions_ThrowsCompatibilityResolverException()
    {
        Assert.Throws<CompatibilityResolverException>(() =>
            _resolver.Resolve("TestPackage", "net8.0", new List<PackageVersionInfo>()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsCompatible — direct unit tests (Theory)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("net8.0", "net8.0", true)]
    [InlineData("net8.0", "net6.0", true)]    // net8 satisfies net6 requirement (backward compat)
    [InlineData("net8.0", "net5.0", true)]
    [InlineData("net8.0", "netstandard2.0", true)]
    [InlineData("net8.0", "netstandard2.1", true)]
    [InlineData("net8.0", "net9.0", false)]   // net9 is newer — net8 can't satisfy it
    [InlineData("net8.0", "net10.0", false)]  // net10 — not compatible
    [InlineData("net6.0", "net8.0", false)]   // net6 cannot satisfy net8 requirement
    [InlineData("net8.0", "net472", false)]   // different stack entirely
    public void IsCompatible_VariousTfmPairs_ReturnsExpected(
        string projectTfm, string packageTfm, bool expected)
    {
        var versionInfo = MakeVersion("1.0.0", packageTfm);
        var result = _resolver.IsCompatible(versionInfo, projectTfm);
        Assert.Equal(expected, result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Explanation generator — reason string
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildReason_LatestIsCompatible_ContainsNoDowngradeMessage()
    {
        var result = new CompatibilityResult(
            PackageId: "Newtonsoft.Json",
            RecommendedVersion: "13.0.3",
            LatestOverallVersion: "13.0.3",
            LatestStableVersion: "13.0.3",
            IsLatestStableCompatible: true,
            ProjectTfm: "net8.0",
            Reason: string.Empty,
            IncompatibleLatestTfms: new List<string>());

        var reason = ExplanationGenerator.BuildReason(result);

        Assert.Contains("latest", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no downgrade", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReason_LatestIsIncompatible_ContainsDowngradeExplanation()
    {
        var result = new CompatibilityResult(
            PackageId: "Microsoft.EntityFrameworkCore",
            RecommendedVersion: "8.0.21",
            LatestOverallVersion: "10.0.2",
            LatestStableVersion: "10.0.2",
            IsLatestStableCompatible: false,
            ProjectTfm: "net8.0",
            Reason: string.Empty,
            IncompatibleLatestTfms: new List<string> { "net10.0", "net9.0" });

        var reason = ExplanationGenerator.BuildReason(result);

        Assert.Contains("10.0.2", reason);
        Assert.Contains("8.0.21", reason);
        Assert.Contains("net8.0", reason);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pre-release: only pre-release available → should still return something
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_OnlyPrereleaseVersionsExist_StillReturnsResult()
    {
        var versions = new List<PackageVersionInfo>
        {
            MakeVersion("1.0.0-alpha", "net8.0"),
            MakeVersion("1.0.0-beta", "net8.0"),
        };

        // When only pre-release versions exist, the resolver should fall back to them
        var result = _resolver.Resolve("TestPackage", "net8.0", versions);

        Assert.Equal("1.0.0-beta", result.RecommendedVersion); // highest pre-release
    }
}
