namespace NuGetCompatAssistant.Cli;

/// <summary>
/// Orchestrates batch package resolution and installation.
/// Dependencies are injected as <see cref="Func{T,TResult}"/> delegates so the class
/// is easily testable without mocking frameworks.
/// </summary>
public class BatchInstallOrchestrator
{
    private readonly Func<string, Task<List<PackageVersionInfo>>> _fetchVersions;
    private readonly CompatibilityResolver _resolver;

    /// <summary>
    /// Creates a new orchestrator.
    /// </summary>
    /// <param name="fetchVersions">
    ///   A function that returns all published versions for a package ID.
    ///   In production this is <c>nugetClient.GetAllVersionsAsync</c>.
    /// </param>
    /// <param name="resolver">The existing compatibility resolver instance.</param>
    public BatchInstallOrchestrator(
        Func<string, Task<List<PackageVersionInfo>>> fetchVersions,
        CompatibilityResolver resolver)
    {
        _fetchVersions = fetchVersions;
        _resolver = resolver;
    }

    /// <summary>
    /// Resolves each package independently against the project TFM.
    /// Continues processing even if a package is invalid or has no compatible version.
    /// </summary>
    public async Task<List<BatchPackageResult>> ResolveAllAsync(
        string[] packageIds,
        string projectTfm)
    {
        var results = new List<BatchPackageResult>();

        foreach (var packageId in packageIds)
        {
            results.Add(await ResolveSingleAsync(packageId, projectTfm));
        }

        return results;
    }

    /// <summary>
    /// Sequentially installs every package in <paramref name="installable"/>,
    /// printing progress like <c>[1/3] Installing AutoMapper 16.1.1...</c>.
    /// </summary>
    /// <returns>A tuple of (installed, failed) counts.</returns>
    public static async Task<(int Installed, int Failed)> InstallAllAsync(
        List<BatchPackageResult> installable,
        string csprojPath,
        Func<string, string, string, Task<bool>> installAsync)
    {
        int installed = 0, failed = 0;

        for (int i = 0; i < installable.Count; i++)
        {
            var pkg = installable[i];
            Console.WriteLine($"[{i + 1}/{installable.Count}] Installing {pkg.PackageId} {pkg.RecommendedVersion}...");

            bool success = await installAsync(pkg.PackageId, pkg.RecommendedVersion, csprojPath);
            if (success)
                installed++;
            else
                failed++;
        }

        return (installed, failed);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task<BatchPackageResult> ResolveSingleAsync(string packageId, string projectTfm)
    {
        List<PackageVersionInfo> allVersions;
        try
        {
            allVersions = await _fetchVersions(packageId);
        }
        catch (NuGetClientException)
        {
            return new BatchPackageResult(
                packageId, "—", "—", "Not Found", "Package not found on nuget.org");
        }

        CompatibilityResult result;
        try
        {
            result = _resolver.Resolve(packageId, projectTfm, allVersions);
        }
        catch (CompatibilityResolverException ex)
        {
            return new BatchPackageResult(
                packageId, "—", "—", "No Compatible Version", ex.Message);
        }

        if (string.IsNullOrEmpty(result.RecommendedVersion))
        {
            return new BatchPackageResult(
                packageId,
                result.LatestStableVersion,
                "—",
                "No Compatible Version",
                $"No version compatible with {projectTfm}");
        }

        if (result.IsLatestStableCompatible)
        {
            return new BatchPackageResult(
                packageId,
                result.LatestStableVersion,
                result.RecommendedVersion,
                "Compatible",
                "Latest stable is compatible");
        }

        return new BatchPackageResult(
            packageId,
            result.LatestStableVersion,
            result.RecommendedVersion,
            "Downgrade",
            $"Latest ({result.LatestStableVersion}) incompatible; downgraded");
    }
}
