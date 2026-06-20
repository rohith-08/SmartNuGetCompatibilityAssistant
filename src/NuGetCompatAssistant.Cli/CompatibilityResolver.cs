using NuGet.Frameworks;
using NuGet.Versioning;

namespace NuGetCompatAssistant.Cli;

/// <summary>
/// The result of compatibility resolution for a package against a project TFM.
/// </summary>
public record CompatibilityResult(
    string PackageId,
    string RecommendedVersion,
    string LatestOverallVersion,
    string LatestStableVersion,
    bool IsLatestStableCompatible,
    string ProjectTfm,
    string Reason,
    List<string> IncompatibleLatestTfms
);

/// <summary>
/// Resolves the best compatible NuGet package version for a given project TFM,
/// using the official <see cref="NuGetFramework"/> and <see cref="DefaultCompatibilityProvider"/>
/// so the logic exactly matches what the dotnet CLI would do.
/// </summary>
public class CompatibilityResolver
{
    private static readonly IFrameworkCompatibilityProvider CompatibilityProvider =
        DefaultCompatibilityProvider.Instance;

    /// <summary>
    /// Given a list of package versions (with their dependency group TFMs), selects the
    /// best <em>stable</em> version for <paramref name="projectTfm"/> using NuGet's own
    /// compatibility rules. Pre-release versions are excluded from consideration unless
    /// no stable version is available.
    /// </summary>
    /// <param name="packageId">The NuGet package ID (for reporting).</param>
    /// <param name="projectTfm">The project's TFM, e.g. "net8.0".</param>
    /// <param name="allVersions">All available, listed versions from the NuGet API.</param>
    /// <returns>A <see cref="CompatibilityResult"/> describing the recommendation and reasoning.</returns>
    public CompatibilityResult Resolve(
        string packageId,
        string projectTfm,
        List<PackageVersionInfo> allVersions)
    {
        if (allVersions.Count == 0)
            throw new CompatibilityResolverException($"No published versions found for package '{packageId}'.");

        var projectFramework = NuGetFramework.Parse(projectTfm);
        if (projectFramework.IsUnsupported)
            throw new CompatibilityResolverException(
                $"Could not parse project TFM '{projectTfm}' as a valid NuGet framework.");

        // Parse and sort ALL versions descending using NuGet's own NuGetVersion comparison
        var sortedAll = allVersions
            .Select(v => (Info: v, Parsed: TryParseVersion(v.Version)))
            .Where(v => v.Parsed is not null)
            .OrderByDescending(v => v.Parsed)
            .ToList();

        if (sortedAll.Count == 0)
            throw new CompatibilityResolverException(
                $"No parseable versions found for package '{packageId}'.");

        // Latest overall (including pre-release) for display
        var latestOverallVersion = sortedAll[0].Info.Version;

        // Latest stable (no pre-release label) — what we recommend from
        var stableVersions = sortedAll
            .Where(v => !v.Parsed!.IsPrerelease)
            .ToList();

        // Fall back to pre-release only if there are literally no stable versions
        var candidateVersions = stableVersions.Count > 0 ? stableVersions : sortedAll;

        var latestStable = candidateVersions[0];
        var latestStableVersion = latestStable.Info.Version;

        // Find best compatible version (highest stable that is compatible with projectTfm)
        (PackageVersionInfo Info, NuGetVersion? Parsed)? bestCompatible = null;
        foreach (var entry in candidateVersions)
        {
            if (IsCompatible(entry.Info, projectFramework))
            {
                bestCompatible = entry;
                break; // candidateVersions is descending, so first match is highest compatible
            }
        }

        var isLatestStableCompatible = bestCompatible.HasValue &&
            string.Equals(bestCompatible.Value.Info.Version, latestStableVersion,
                StringComparison.OrdinalIgnoreCase);

        // Collect TFMs that the latest stable version declares (for the explanation)
        var latestTfms = latestStable.Info.DependencyGroups
            .Select(dg => dg.TargetFramework ?? "(any)")
            .ToList();

        if (bestCompatible is null)
        {
            return new CompatibilityResult(
                PackageId: packageId,
                RecommendedVersion: string.Empty,
                LatestOverallVersion: latestOverallVersion,
                LatestStableVersion: latestStableVersion,
                IsLatestStableCompatible: false,
                ProjectTfm: projectTfm,
                Reason: BuildNoCompatibleVersionReason(
                    packageId, projectTfm, latestStableVersion, latestTfms),
                IncompatibleLatestTfms: latestTfms
            );
        }

        return new CompatibilityResult(
            PackageId: packageId,
            RecommendedVersion: bestCompatible.Value.Info.Version,
            LatestOverallVersion: latestOverallVersion,
            LatestStableVersion: latestStableVersion,
            IsLatestStableCompatible: isLatestStableCompatible,
            ProjectTfm: projectTfm,
            Reason: string.Empty, // filled by ExplanationGenerator.BuildReason
            IncompatibleLatestTfms: isLatestStableCompatible ? new List<string>() : latestTfms
        );
    }

    // -------------------------------------------------------------------------
    // Compatibility check
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if the given package version has at least one dependency group
    /// that is compatible with <paramref name="projectFramework"/>, using NuGet's
    /// official <see cref="DefaultCompatibilityProvider"/>.
    /// </summary>
    /// <remarks>
    /// No manual string-based TFM matching is performed here. All compatibility
    /// decisions are delegated to <c>DefaultCompatibilityProvider.IsCompatible</c>,
    /// which is the exact same logic used internally by the dotnet CLI and NuGet.
    ///
    /// Examples of what this correctly handles:
    ///   • net8.0 IS compatible with packages targeting net6.0, net5.0, netstandard2.1, netstandard2.0
    ///   • net8.0 is NOT compatible with packages that only target net9.0 or net10.0
    ///   • net8.0 is NOT compatible with net472 (different runtime stack)
    /// </remarks>
    public bool IsCompatible(PackageVersionInfo versionInfo, NuGetFramework projectFramework)
    {
        var depGroups = versionInfo.DependencyGroups;

        // A package with NO dependency groups is universally compatible —
        // it ships no managed assembly requiring a specific TFM (e.g. content-only packages).
        if (depGroups.Count == 0)
            return true;

        foreach (var dg in depGroups)
        {
            var groupFramework = string.IsNullOrWhiteSpace(dg.TargetFramework)
                ? NuGetFramework.AnyFramework
                : NuGetFramework.Parse(dg.TargetFramework);

            // DefaultCompatibilityProvider.IsCompatible(projectFx, packageFx) answers:
            // "Can a project targeting 'projectFx' consume a package built for 'packageFx'?"
            if (CompatibilityProvider.IsCompatible(projectFramework, groupFramework))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Overload accepting a raw TFM string for the project.
    /// </summary>
    public bool IsCompatible(PackageVersionInfo versionInfo, string projectTfm)
        => IsCompatible(versionInfo, NuGetFramework.Parse(projectTfm));

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static NuGetVersion? TryParseVersion(string version)
    {
        NuGetVersion.TryParse(version, out var parsed);
        return parsed;
    }

    private static string BuildNoCompatibleVersionReason(
        string packageId, string projectTfm, string latestStableVersion, List<string> latestTfms)
    {
        var tfmList = latestTfms.Count > 0
            ? string.Join(", ", latestTfms.Select(t => $"'{t}'"))
            : "(no dependency groups — may be a meta-package or content-only package)";

        return
            $"No stable version of '{packageId}' found that is compatible with '{projectTfm}'. " +
            $"The latest stable version ({latestStableVersion}) declares dependency groups for: {tfmList}. " +
            "None of these are compatible with your project's target framework.";
    }
}

/// <summary>
/// Exception thrown by <see cref="CompatibilityResolver"/> for user-facing error conditions.
/// </summary>
public class CompatibilityResolverException : Exception
{
    public CompatibilityResolverException(string message) : base(message) { }
}
