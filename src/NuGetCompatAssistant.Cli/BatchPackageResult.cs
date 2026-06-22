namespace NuGetCompatAssistant.Cli;

/// <summary>
/// Represents the resolution result for a single package in a batch install operation.
/// </summary>
/// <param name="PackageId">The NuGet package ID.</param>
/// <param name="LatestStableVersion">The latest stable version on nuget.org, or "—" if unknown.</param>
/// <param name="RecommendedVersion">The recommended version to install, or "—" if none.</param>
/// <param name="Status">One of: Compatible, Downgrade, Not Found, No Compatible Version.</param>
/// <param name="Reason">A short human-readable explanation for the status.</param>
public record BatchPackageResult(
    string PackageId,
    string LatestStableVersion,
    string RecommendedVersion,
    string Status,
    string Reason
)
{
    /// <summary>
    /// Returns true if this package can be installed (Compatible or Downgrade status).
    /// </summary>
    public bool IsInstallable => Status is "Compatible" or "Downgrade";
}
